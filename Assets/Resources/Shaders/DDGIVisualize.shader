Shader "Custom/DDGIVisualize"
{
    Properties
    {

    }
    SubShader
    {
        Tags { 
            "RenderType"="Opaque" 
            "RenderPipeline" = "UniversalPipeline"
        }
        
        Pass
        {
            HLSLPROGRAM
            
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #pragma multi_compile _ DDGI_DEBUG_IRRADIANCE DDGI_DEBUG_DISTANCE  DDGI_DEBUG_OFFSET

            //#pragma enable_d3d12_debug_symbols
            #define DDGI_VISUALIZATION 1

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            
            //引用DDGI库函数
            #include "Lib/DDGIInputs.hlsl"
            #include "Lib/DDGIProbeIndexing.hlsl"
            #include "Lib/DDGIFuncs.hlsl"


            float4x4 _ddgiSphere_ObjectToWorld;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : NORMAL;
                uint probeIndex : SV_InstanceID;
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                float3 worldPos = mul((float3x3)_ddgiSphere_ObjectToWorld,input.positionOS);

                //根据id获取探针的相对世界坐标，并加在世界坐标上
                uint probeIndex = input.instanceID;
                float3 probePosition = DDGIGetProbeWorldPosition(probeIndex);
                
                
                worldPos += probePosition;

                output.positionCS = TransformWorldToHClip(worldPos);
                output.normalWS = mul(input.normalOS,(float3x3)_ddgiSphere_ObjectToWorld);

                output.probeIndex = probeIndex;

                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                //计算获得探针坐标
                const uint3 probeDataCoords = DDGIGetProbeTexelCoordsOneByOne(input.probeIndex);
                //探针状态
                const int probeState =  DDGILoadProbeState(probeDataCoords);

                //探针不活跃，直接裁剪
                if(probeState == DDGI_PROBE_STATE_INACTIVE) clip(-1);

                
                #ifdef DDGI_DEBUG_IRRADIANCE
                    float3 uv       = DDGIGetProbeUV(input.probeIndex, SafeNormalize(input.normalWS), PROBE_IRRADIANCE_TEXELS);
		            float3 radiance = SAMPLE_TEXTURE2D_ARRAY_LOD(_ProbeIrradianceHistory, sampler_LinearClamp, uv.xy, uv.z, 0).rgb;
		            radiance        = pow(radiance, 2.5f);
		            float4 result   = float4(radiance, 1.0f);
                #elif DDGI_DEBUG_DISTANCE
                    float3 uv       = DDGIGetProbeUV(input.probeIndex, SafeNormalize(input.normalWS), PROBE_DISTANCE_TEXELS);
		            float distance  = SAMPLE_TEXTURE2D_ARRAY_LOD(_ProbeDistanceHistory, sampler_LinearClamp, uv.xy, uv.z, 0).r;
		            float3 color    = distance.xxx / (Max(_ProbeSize) * 3);
		            float4 result   = float4(color, 1.0f);
                #elif DDGI_DEBUG_OFFSET
                    float3 offset   = LOAD_TEXTURE2D_ARRAY_LOD(_ProbeData, probeDataCoords.xy, probeDataCoords.z, 0).xyz;
                    float4 result   = float4(abs(offset), 1);
                #else
                    //或许需要更多测试模式？
                    float4 result = float4(0,0,1,0);
                #endif


                //return result;
                return  result;
            }
            
            ENDHLSL
        }
    }
    FallBack "Diffuse"
}
