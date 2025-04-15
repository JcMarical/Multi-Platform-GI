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


            float4x4 _ddgiSphere_Object2World;

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

                float3 worldPos = mul((float3x3)_ddgiSphere_Object2World,input.positionOS);

                //根据id获取探针的相对世界坐标，并加在世界坐标上
                uint probeIndex = input.instanceID;
                float3 probePosition = DDGIGetProbeWorldPosition(probeIndex);
                
                
                worldPos += probePosition;

                output.positionCS = TransformWorldToHClip(worldPos);
                output.normalWS = mul(input.normalOS,(float3x3)_ddgiSphere_Object2World);

                output.probeIndex = probeIndex;

                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                //计算获得探针坐标
                const uint3 probeDataCoords = DDGIGetProbeTexelCoordsOneByOne(input.probeIndex);
                //探针状态
                const int probeState =  DDGILoadProbeState(probeDataCoords);

                //探针未激活，直接裁剪
                //if(probeState == DDGI_PROBE_STATE_INACTIVE) clip(-1);

                #ifdef DDGI_DEBUG_IRRADIANCE
		            float4 result   = float4(1.0f,0,0,0);
                #elif DDGI_DEBUG_DISTANCE
		            float4 result   = float4(0,1.0f,0,0);
                #elif DDGI_DEBUG_OFFSET
		            float4 result   = float4(1.0f,1.0f,0,0);
                #else
                    //或许需要更多测试模式？
                    float4 result = float4(0,0,1,0);
                #endif


                //return result;
                return  float4(input.positionCS);
            }
            
            ENDHLSL
        }
    }
    FallBack "Diffuse"
}
