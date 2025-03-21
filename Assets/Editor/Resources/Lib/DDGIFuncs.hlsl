#ifndef DDGI_FUNCS
#define DDGI_FUNCS

#include "Common/QuaternionHelper.hlsl"

//------------------------------------------------------------------------
// Probe Data Fetcher 探针数据获取
//------------------------------------------------------------------------

#if 0

    int DDGILoadProbeState(uint3 coords)
    {
        return 0;
    }
#else if  defined(DDGI_VISUALIZATION) || defined(DDGI_RAYTRACING) || defined(FORWARD_USE_DDGI)

	int DDGILoadProbeState(uint3 coords)
	{
	    int state = DDGI_PROBE_STATE_ACTIVE;
		if(DDGI_PROBE_CLASSIFICATION == DDGI_PROBE_CLASSIFICATION_ON)
		{
			state = (int)LOAD_TEXTURE2D_ARRAY_LOD(_ProbeData, coords.xy, coords.z, 0).a;
		} 

		return state;
	}
#else

	int DDGILoadProbeState(uint3 coords)
	{
		int state = DDGI_PROBE_STATE_ACTIVE;
		if(DDGI_PROBE_CLASSIFICATION == DDGI_PROBE_CLASSIFICATION_ON)
		{
			state = (int)_ProbeData[coords].a;
		}

		return state;
	}
#endif


//------------------------------------------------------------------------
// Probe World Position
//------------------------------------------------------------------------

float3 DDGIGetProbeWorldPosition(uint gridCoord)
{
    //探针世界坐标 =  起始坐标 + 探针大小 * gridCoord坐标
    const float3 probeSpaceWorldPosition = gridCoord * _ProbeSize;


        //旋转探针（大小*间隔）
    const float3 probeVolumeExtents = (_ProbeSize * (_ProbeCount - 1)) * 0.5f;//总长度的一半
    float3 probeWorldPosition = probeSpaceWorldPosition - probeVolumeExtents;//实际位置减去一半，【-n/2,n/2】，方便旋转

    probeWorldPosition = DDGIQuaternionRotate(probeWorldPosition,_ProbeRotation) + probeVolumeExtents;//旋转后再把这一半加回来

    probeWorldPosition += _StartPosition;


    //探针重定位：暂时不考虑
    
    	// 光追Shader中会用到该函数，而根据下面的链接，光线跟踪Shader分支仍在计划中，这意味着我们不能用变体，所以用变量判断开启与否
	// https://portal.productboard.com/unity/1-unity-platform-rendering-visual-effects/tabs/125-shader-system
	//if(DDGI_PROBE_RELOCATION == DDGI_PROBE_RELOCATION_ON)
	//{
	//	// 因为我们采样tex2DArray时，采样坐标的z分量实际上对应于gridCoord的y分量，这里需要额外做一步反转
	//	int probeIndex				= DDGIGetProbeIndex(gridCoord);
	//	uint3 probeDataTexelCoord	= DDGIGetProbeTexelCoordsOneByOne(probeIndex);
	//	probeWorldPosition			+= DDGILoadProbeDataOffset(probeDataTexelCoord);
	//}
    
    return probeWorldPosition; 
}


#endif 
