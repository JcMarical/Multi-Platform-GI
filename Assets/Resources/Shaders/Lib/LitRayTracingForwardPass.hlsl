#ifndef LIT_RAY_TRACING_FORWARD_PASS
#define LIT_RAY_TRACING_FORWARD_PASS

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

struct Attributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float4 tangentOS    : TANGENT;
    float2 texcoord     : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};


struct Varyings
{
    float2 uv                       : TEXCOORD0;
    float3 positionWS               : TEXCOORD1;
    float3 normalWS                 : TEXCOORD2;
    float4 tangentWS                : TEXCOORD3;
    float4 shadowCoord              : TEXCOORD4;
    float4 positionCS               : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

void InitializeInputData(Varyings input, half3 normalTS, out InputData inputData)
{
    inputData = (InputData)0;

    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    
    float sgn       = input.tangentWS.w;
    float3 bitangent= sgn * cross(input.normalWS.xyz,input.tangentWS.xyz);  //计算副切线
    half3x3 tangentToWorld = half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz);//计算切线矩阵

    inputData.positionWS    = input.positionWS;
    inputData.positionCS    = input.positionCS;
    //将切线空间中的法线转换到世界空间并归一化
    inputData.normalWS      = TransformTangentToWorld(normalTS, tangentToWorld);
    inputData.normalWS      = NormalizeNormalPerVertex(inputData.normalWS);
    inputData.viewDirectionWS = viewDirWS;
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);//转换阴影坐标到阴影贴图空间
    inputData.fogCoord = 0.0f;
    inputData.vertexLighting = 0.0f;
    inputData.bakedGI = 0.0f;
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);//屏幕空间uv
    inputData.shadowMask = 1.0f;
    inputData.tangentToWorld = tangentToWorld;
    
    
}
///////////////////////////////////////////////////////////////////////////////
//                  Vertex and Fragment functions                            //
///////////////////////////////////////////////////////////////////////////////

Varyings LitPassVertex(Attributes input)
{
    Varyings output = (Varyings)0;

    //单通道立体实例化渲染(暂且不知道有啥用，VR适配？)
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
    // 计算切线空间到世界空间的转换
    VertexNormalInputs normalInput   = GetVertexNormalInputs(input.normalOS, input.tangentOS);
    real sign        = input.tangentOS.w * GetOddNegativeScale();//处理切线的正负号
    half4 tangentWS  = half4(normalInput.tangentWS.xyz, sign);

    //常规输出
    output.uv          = TRANSFORM_TEX(input.texcoord, _BaseMap);
    output.positionWS  = vertexInput.positionWS;
    output.normalWS    = normalInput.normalWS;
    output.tangentWS   = tangentWS;
    output.shadowCoord = GetShadowCoord(vertexInput);
    output.positionCS  = vertexInput.positionCS;
    
    return output;
}

float4 LitPassFragment(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);


    //表面数据
    SurfaceData surfaceData = (SurfaceData)0;
    InitializeStandardLitSurfaceData(input.uv,surfaceData);
    //初始化输入数据
    InputData   inputData   = (InputData)0;
    InitializeInputData(input, surfaceData.normalTS, inputData);
    //BRDFData
    BRDFData    brdfData    = (BRDFData)0;
    InitializeBRDFData(surfaceData, brdfData);

    //环境光遮蔽因子
    AmbientOcclusionFactor aoFactor = CreateAmbientOcclusionFactor(inputData, surfaceData);

    
    float4 color = float4(0.0f, 0.0f, 0.0f, surfaceData.alpha);

    //自发光
    color.rgb += surfaceData.emission;

    //直接光照计算
    Light mainLight = GetMainLight(inputData.shadowCoord);
    color.rgb       += LightingPhysicallyBased(brdfData,mainLight,inputData.normalWS,inputData.viewDirectionWS)
        *aoFactor.directAmbientOcclusion;

    //多光源计算
    for(int i = 0; i < GetAdditionalLightsCount(); ++i)
    {
        Light addLight = GetAdditionalLight(i,inputData.positionWS);
        color.rgb      += LightingPhysicallyBased(brdfData, addLight, inputData.normalWS, inputData.viewDirectionWS)
            *aoFactor.directAmbientOcclusion;
    }

    //--------间接光计算--------
    //间接光辐照度
    float3 indirectRadiance = SampleDDGIIrradiance(inputData.positionWS, inputData.normalWS, -inputData.viewDirectionWS);
    //间接光照
    float3 indirectLighting = surfaceData.albedo * Lambert() * indirectRadiance * aoFactor.indirectAmbientOcclusion;
    #ifdef DDGI_SHOW_INDIRECT_ONLY
        color.rgb = indirectLighting;
    #elif DDGI_SHOW_PURE_INDIRECT_RADIANCE
        color.rgb = indirectRadiance;
    #else
        color.rgb += indirectLighting;
    #endif

    return color;
    
}


#endif