#ifndef DDGI_FUNCS
#define DDGI_FUNCS

#include "Common/Packing.hlsl"
#include "Common/MathUtility.hlsl"
#include "Common/QuaternionHelper.hlsl"

//------------------------------------------------------------------------
// Ray tracing payload helper. 光线追踪helper
//------------------------------------------------------------------------
DDGIPayload GetPrimaryPayload()
{
	const DDGIPayload payload = (DDGIPayload) 0;

	return payload;
}

DDGIPayload GetShadowPayload()
{
	DDGIPayload payload = (DDGIPayload) 0;

	payload.isShadowPayload = true;
	payload.isInShadow		= false;

	return payload;
}

//------------------------------------------------------------------------
// Randomize Functions 
//------------------------------------------------------------------------

// Ray Tracing Gems 2: Essential Ray Generation Shaders
//斐波那契螺旋算法，通过球坐标转换生成3D方向向量，比随机分布更均匀，没有聚类现象
float3 SphericalFibonacci(float i, float n)
	{
		const float PHI = sqrt(5) * 0.5f + 0.5f;
		float fraction	= (i * (PHI - 1)) - floor(i * (PHI - 1));
		float phi		= 2.0f * PI * fraction;
		float cosTheta	= 1.0f - (2.0f * i + 1.0f) * (1.0f / n);
		float sinTheta	= sqrt(saturate(1.0 - cosTheta * cosTheta));
	
		return float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
	}

float3 DDGIGetProbeRayDirection(int rayIndex)
	{
		// 处理固定光线和普通光线
		bool isFixedRay = false;
		int sampleIndex = rayIndex;
		int numRays		= _RaysPerProbe;

		//如果启用探针重定位或分类，那么前RTXGI_DDGI_NUM_FIXED_RAYS个光线是固定的，其他光线是随机的
	
		if ((DDGI_PROBE_RELOCATION == DDGI_PROBE_RELOCATION_ON) || (DDGI_PROBE_CLASSIFICATION == DDGI_PROBE_CLASSIFICATION_ON))
		{
			isFixedRay  = (rayIndex < RTXGI_DDGI_NUM_FIXED_RAYS);
			sampleIndex = isFixedRay ? rayIndex : (rayIndex - RTXGI_DDGI_NUM_FIXED_RAYS);
			numRays		= isFixedRay ? RTXGI_DDGI_NUM_FIXED_RAYS : (numRays - RTXGI_DDGI_NUM_FIXED_RAYS);
		}

		// Get a ray direction on the sphere
		// 使用斐波那契螺旋生成基础方向
		float3 direction = SphericalFibonacci(sampleIndex, numRays);

		// Don't rotate fixed rays so relocation/classification are temporally stable
		// 对于固定光线，直接返回
		if (isFixedRay) return normalize(direction);

		// Apply Rotation
		// 对于普通光线，应用随机旋转
		float3 randomDirection = RotateAboutAxisInRadians(direction, _RandomVector, _RandomAngle);
		return normalize(randomDirection);
	}


//------------------------------------------------------------------------
// Light Fetcher 光线获取器
//------------------------------------------------------------------------

Light GetDDGIDirectionalLight(int index)
{
	DirectionalLight directionalLight = DirectionalLightBuffer[index];

	Light light;
	light.direction				= directionalLight.direction.xyz;
	light.color					= directionalLight.color.rgb;
	light.distanceAttenuation	= 1.0f;
	light.shadowAttenuation		= 1.0f;
	light.layerMask				= 0;

	return light;
}

Light GetDDGIPunctualLight(int index, float3 positionWS)
{
	PunctualLight punctualLight = PunctualLightBuffer[index];
	float4 lightPositionWS = punctualLight.position;
	float3 color = punctualLight.color.rgb;
	float4 distanceAndSpotAttenuation = punctualLight.distanceAndSpotAttenuation;
	float4 spotDirection = punctualLight.spotDirection;
	
	float3 lightVector	= lightPositionWS.xyz - positionWS * lightPositionWS.w;
	float distanceSqr	= max(dot(lightVector, lightVector), HALF_MIN);

	half3 lightDirection = half3(lightVector * rsqrt(distanceSqr));
	float attenuation	 = DistanceAttenuation(distanceSqr, distanceAndSpotAttenuation.xy) * AngleAttenuation(spotDirection.xyz, lightDirection, distanceAndSpotAttenuation.zw);

	// 我们使用光线跟踪确定阴影，这里shadowAttenuation赋1
	Light light;
	light.direction				= lightDirection;
	light.distanceAttenuation	= attenuation;
	light.shadowAttenuation		= 1.0;
	light.color					= color.rgb;
	light.layerMask				= 0;

	return light;
}
//------------------------------------------------------------------------
// Probe Data Fetcher 探针数据获取器
//------------------------------------------------------------------------

#if defined(DDGI_VISUALIZATION) || defined(DDGI_RAYTRACING) || defined(FORWARD_USE_DDGI)
float3 DDGILoadProbeDataOffset(uint3 coords)
	{
		return LOAD_TEXTURE2D_ARRAY_LOD(_ProbeData, coords.xy, coords.z, 0).xyz * _ProbeSize;	
	}

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
// We use Texture2DArray in visualization, Texture2DArray dont support these function.
float3 DDGILoadProbeDataOffset(uint3 coords)
	{
		return _ProbeData[coords].xyz * _ProbeSize;
	}

void DDGIStoreProbeDataOffset(uint3 coords, float3 wsOffset)
	{
		// A-Component is useless now.
		_ProbeData[coords] = float4(wsOffset / _ProbeSize, 1.0f);
	}

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

float3 DDGIGetProbeWorldPosition(uint3 gridCoord)
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

// 重载：根据probe的一维网格索引获取其世界空间位置
float3 DDGIGetProbeWorldPosition(uint probeIndex)
{
	uint3 gridCoord = DDGIGetProbeCoords(probeIndex);
	return DDGIGetProbeWorldPosition(gridCoord);
}



// 接受一个世界空间位置P，返回与该位置相关的基准probe网格坐标（用于确定P所在的Probe网格块）
uint3 DDGIGetBaseGridCoords(float3 worldPos)
{
	const float3 probeVolumeExtents  = (_ProbeSize * (_ProbeCount - 1)) * 0.5f;
	const float3 probeVolumeCenter   = _StartPosition + probeVolumeExtents;

	//这里好像有点问题？
	float3 position = worldPos - probeVolumeCenter;
	position = DDGIQuaternionRotate(position, DDGIQuaternionConjugate(_ProbeRotation));
	position += probeVolumeExtents; // Transform to [0,n]

	uint3 probeCoords = uint3(position / _ProbeSize);
	probeCoords = clamp(probeCoords, uint3(0,0,0), uint3(_ProbeCount) - uint3(1, 1, 1));
	
	return probeCoords;
	// return clamp(uint3((worldPos - _StartPosition) / _ProbeSize), uint3(0, 0, 0), uint3(_ProbeCount) - uint3(1, 1, 1)); // No Rotation Implementation.
}

//------------------------------------------------------------------------
// Runtime Probe Sampling
//------------------------------------------------------------------------

//https://github.com/simco50/D3D12_Research/blob/master/Resources/Shaders/RayTracing/DDGICommon.hlsli
float3 ComputeBias(float3 normal, float3 viewDirection,float b = 0.2f)
{

	#if 0
		//Arida 实现
		const float normalBiasMultiplier = 0.2f;//法线偏置
		const float viewBiasMultiplier = 0.8f;//视图偏置
		const float axialDistanceMultiplier = 0.75f;//轴向距离函数
		return (normal * normalBiasMultiplier + viewDirection * viewBiasMultiplier) * axialDistanceMultiplier * Min(_ProbeSize) * b;
	#else
		//Nvidia实现
		return (normal * _NormalBiasMultiplier + viewDirection * _ViewBiasMultiplier);
	#endif
		
}

//计算体积混合权重
	float DDGIGetVolumeBlendWeight(float3 worldPosition)
	{
		const float3 probeVolumeExtents = (_ProbeSize * (_ProbeCount-1)) * 0.5f;//体积的一半
		const float3 probeVolumeCenter  = _StartPosition + probeVolumeExtents;//找到中心位置

		//绕中心点旋转
		float3 position = worldPosition - probeVolumeCenter;
		position = abs(DDGIQuaternionRotate(position,DDGIQuaternionConjugate(_ProbeRotation)));

		float3 delta = position - probeVolumeExtents;

		if(all(delta < 0.0f)) return 1.0f;//体积内则权重为1

		//根据相对位置计算权重,越远权重越小
		float volumeBlendWeight = 1.0f;
		volumeBlendWeight *= (1.0f - saturate(delta.x/ _ProbeSize.x));//
		volumeBlendWeight *= (1.0f - saturate(delta.y/ _ProbeSize.x));
		volumeBlendWeight *= (1.0f - saturate(delta.z/ _ProbeSize.x));

		return volumeBlendWeight;
	}

float3 SampleDDGIIrradiance(float3 P, float3 N,float3 Wo)
	{
		float3 direction	= N;
		float3 biasedPosition = P;
		float3 unbiasedPosition = P;
		float volumeWeight = 1.0f;

		//--------计算偏移-----------
		biasedPosition += ComputeBias(direction,-Wo);

		//--------体积权重-----------
		// 当着色点位于Volume区域外，我们将提前返回
		// 当着色点逼近Volume边界（但没有超出volume区域），我们对其辐照度进行平滑过渡
		volumeWeight = DDGIGetVolumeBlendWeight(biasedPosition);
		if(volumeWeight <= 0.0f) return 0.0f;

		// 计算relativeCoordinates时就需要偏移position（参考NVIDIA）
		// 如果在这里才偏移position（Adria的实现）会导致trilinear插值出现网格瑕疵
		//position += ComputeBias(direction, -Wo);

		//确定probe对应网格块
		const uint3 baseProbeCoords = DDGIGetBaseGridCoords(biasedPosition);
		//确认probe对应世界坐标
		const float3 baseProbePosition = DDGIGetProbeWorldPosition(baseProbeCoords);

		//--------计算网格空间距离----------
		float3 gridSpaceDistance = biasedPosition - baseProbePosition;
		gridSpaceDistance        = DDGIQuaternionRotate(gridSpaceDistance, DDGIQuaternionConjugate(_ProbeRotation));
		const float3 alpha       = saturate(gridSpaceDistance / _ProbeSize);	//根据到探针距离求出alpha

		float3 sumIrradiance = 0; // 累加辐照度
		float  sumWeight     = 0; // 累加权重

		for(uint j = 0;j < 8; ++j)
		{
			const uint3 indexOffset = uint3(j, j>>1u, j >> 2u) &1u; //计算索引偏移

			//获取探针坐标位置索引信息
			const uint3 probeCoords		= clamp(baseProbeCoords + indexOffset,0,_ProbeCount - 1);//获取探针坐标
			const float3 probePosition	= DDGIGetProbeWorldPosition(probeCoords); //获取探针位置
			const uint probeIndex		= DDGIGetProbeIndex(probeCoords);//获取探针索引

			//取探针数据坐标和探针状态
			const uint3 probeDataCoords	= DDGIGetProbeTexelCoordsOneByOne(probeIndex);
			const int probeState		= DDGILoadProbeState(probeDataCoords);
			
			//如果探针未启用，则跳过。
			if(probeState == DDGI_PROBE_STATE_INACTIVE) continue;

			//计算相对探针位置和方向
			float3 relativeProbePosition = biasedPosition - probePosition;
			float3 probeDirection		 = -normalize(relativeProbePosition);

			//计算三线性插值权重
			float3 trilinear	= max(0.001f,lerp(1.0f-alpha,alpha,indexOffset));
			float trilinearWeight = (trilinear.x * trilinear.y * trilinear.z);

			float weight = 1.0f;

			// --------------------------------
			// 背面权重计算(看不懂，先放一下)
			// --------------------------------

			#if 0
			// Adria Implementation.
				weight *= saturate(dot(probeDirection, direction));
			#else
			// NVIDIA Implementation.
				const float wrapShading = dot(normalize(probePosition - unbiasedPosition), direction) * 0.5f + 0.5f;
				weight *= (wrapShading * wrapShading) + 0.2f;
			#endif

			// --------------------------------
			// Chebyshev Visibility Test切比雪夫可见性测试，避免自遮挡、VSM实现
			// --------------------------------

			// 获取uv
			float3 probeDistanceUV	= DDGIGetProbeUV(probeIndex, -probeDirection, PROBE_DISTANCE_TEXELS);
			//获取探针距离
			float  probeDistance	= length(relativeProbePosition);
			//方差阴影映射VSM，一时半会儿真没看懂
			// https://developer.download.nvidia.com/SDK/10/direct3d/Source/VarianceShadowMapping/Doc/VarianceShadowMapping.pdf
			float2 moments = SAMPLE_TEXTURE2D_ARRAY_LOD(_ProbeDistanceHistory, sampler_LinearClamp, probeDistanceUV.xy, probeDistanceUV.z, 0).xy;
			float variance = abs(Pow2(moments.x) - moments.y);
			float chebyshev = 1.0f;
			if(probeDistance > moments.x)
			{
				float mD = moments.x - probeDistance;
				chebyshev = variance / (variance + Pow2(mD));
				chebyshev = max(Pow3(chebyshev), 0.0);
			}
			weight *= max(chebyshev, 0.05f);
		
			weight = max(0.000001f, weight);

			// --------------------------------
			// Threshold and Trilinear Weight.
			// 阈值和三线性插值权重
			// --------------------------------
			const float crushThreshold = 0.2f;
			if (weight < crushThreshold)
			{
				weight *= weight * weight * (1.0f / Pow2(crushThreshold));
			}
			weight *= trilinearWeight;

			// 采样探针辐照度
			float3 probeIrradianceUV = DDGIGetProbeUV(probeIndex, direction, PROBE_IRRADIANCE_TEXELS);
			float3 irradiance		 = SAMPLE_TEXTURE2D_ARRAY_LOD(_ProbeIrradianceHistory, sampler_LinearClamp, probeIrradianceUV.xy, probeIrradianceUV.z, 0).rgb;
			irradiance				 = pow(irradiance, 2.5f); // Gamma Correct.

			sumIrradiance += irradiance * weight;
			sumWeight	  += weight;

			
		}

		// 计算最终辐照度
		if(sumWeight == 0) return 0.0f;

		sumIrradiance *= (1.0f / sumWeight);
		sumIrradiance *= sumIrradiance;
		sumIrradiance *= DDGI_2PI;
		sumIrradiance *= _IndirectIntensity;
	
		return sumIrradiance * volumeWeight;
		
	}


#endif 
