#ifndef DDGI_INPUTS
#define DDGI_INPUTS

#define DDGI_2PI 6.2831853071795864f;  // 2 * PI


//----------------------------DDGI探针参数----------------------------
#define PROBE_IRRADIANCE_TEXELS     6    // 每个方向的纹素数量（不含边界）
#define PROBE_DISTANCE_TEXELS      14    // 每个方向的纹素数量（不含边界）
#define BACKFACE_DEPTH_MULTIPLIER   -0.2f
#define MIN_WEIGHT                  0.0001f

//环境天空光设置
#define DDGI_SKYLIGHT_MODE_SKYBOX_CUBEMAP	0
#define DDGI_SKYLIGHT_MODE_GRADIENT			1
#define DDGI_SKYLIGHT_MODE_COLOR			2
#define DDGI_SKYLIGHT_MODE_UNSUPPORTED		3

//探针分类
#define DDGI_PROBE_CLASSIFICATION_ON  1
#define DDGI_PROBE_CLASSIFICATION_OFF 0

//探针状态
#define DDGI_PROBE_STATE_ACTIVE		  0
#define DDGI_PROBE_STATE_INACTIVE	  1

//探针重定位
#define DDGI_PROBE_RELOCATION_ON	1
#define DDGI_PROBE_RELOCATION_OFF	0

//探针缩减
#define DDGI_PROBE_REDUCTION_ON		1
#define DDGI_PROBE_REDUCTION_OFF	0


// The number of fixed rays that are used by probe relocation and classification.
// These rays directions are always the same to produce temporally stable results.


//探针固定光线数量,用于探针重定位和分类
//这些光线方向始终保持一致,以产生稳定的结果
#define RTXGI_DDGI_NUM_FIXED_RAYS 32

RWStructuredBuffer<float4> RayBuffer;

//探针辐照度和距离
RWTexture2DArray<float4> _ProbeIrradiance;
RWTexture2DArray<float2> _ProbeDistance;

//探针辐照度和距离的历史
Texture2DArray<float4>   _ProbeIrradianceHistory;
Texture2DArray<float2>   _ProbeDistanceHistory;

//可视化或光线追踪或前向渲染使用DDGI时，使用只读Texture2DArray，否则使用RWTexture2DArray用来读写
#if defined(DDGI_VISUALIZATION) || defined(DDGI_RAYTRACING) || defined(FORWARD_USE_DDGI)
    Texture2DArray<float4>   _ProbeData;
    #else
    RWTexture2DArray<float4> _ProbeData;
#endif
//----------------------------DDGI探针参数----------------------------


//----------------------------光源参数----------------------------
struct DirectionalLight
{
    float4 direction;
    float4 color;
};

StructuredBuffer<DirectionalLight> DirectionalLightBuffer;


//点光源
struct PunctualLight
{
    float4 position;
    float4 color;
    float4 distanceAndSpotAttenuation;
    float4 spotDirection;
};

StructuredBuffer<PunctualLight> PunctualLightBuffer;

struct DDGIPayload
{
    // For recursive shadow ray tracing.
    //// 用于递归阴影光线追踪的标志
    bool isShadowPayload;   //是否为阴影负载
    bool isInShadow;        //是否在阴影中

    // Ray tracing api data.
    // 光线追踪API数据
    float	distance;           // 光线与交点之间的距离
    uint	hitKind;            // 交点的类型（例如，是否为物体、光源等）
    float3	worldRayDirection;  // 光线在世界空间中的方向

    // 光线未命中（天空采样）
    bool	isMissed;   //表示光线未命中任何物体
    float3	skySample;

    // 交点几何体和BRDF数据
    float3 worldPos;
    float3 worldNormal;
    float3 albedo;
    float3 emission;
};




CBUFFER_START(DDGIVolumeGpu)
    float4   _ProbeRotation;           // 探针旋转四元数，用于旋转探针的采样方向
    float3   _StartPosition;           // 探针体积的起始位置（通常是左下角）
    int      _RaysPerProbe;            // 每个探针当前发射的光线数量
    float3   _ProbeSize;               // 探针体积的尺寸（宽、高、深）
    int      _MaxRaysPerProbe;         // 每个探针最大可发射的光线数量
    uint3    _ProbeCount;              // 探针在XYZ三个方向的数量
    float    _NormalBias;              // 沿表面法线的位移偏移量，用于避免自遮挡
    float3   _RandomVector;            // 随机向量，用于光线方向的随机化
    float    _EnergyPreservation;      // 能量保存系数，控制间接光照的强度保持
    float    _RandomAngle;             // 光线随机化的角度范围
    float    _HistoryBlendWeight;      // 历史数据混合权重，控制时间过滤平滑度
    float    _IndirectIntensity;       // 间接光照强度乘数
    float    _NormalBiasMultiplier;    // 法线偏移的额外乘数
    float    _ViewBiasMultiplier;      // 视图偏移的额外乘数
    int      DDGI_PROBE_CLASSIFICATION; // 探针分类开关（0=关闭，1=开启）
    int      DDGI_PROBE_RELOCATION;    // 探针重定位开关（0=关闭，1=开启）
    float    _ProbeFixedRayBackfaceThreshold; // 探针背面检测阈值，用于分类和重定位
    float    _ProbeMinFrontfaceDistance; // 探针前面最小距离，用于分类和重定位
    int      _DirectionalLightCount;   // 存储场景内所有Directional光源数量（不考虑剔除）
    int      _PunctualLightCount;      // 存储场景内所有Spot和Point光源数量（不考虑剔除）
    int      DDGI_SKYLIGHT_MODE;       // 天空光照模式（0=天空盒，1=渐变，2=纯色，3=不支持）
    float4   _SkyboxTintColor;         // 天空盒颜色色调
    float4   _SkyColor;                // 天空颜色（用于渐变模式）
    float4   _EquatorColor;            // 地平线颜色（用于渐变模式）
    float4   _GroundColor;             // 地面颜色（用于渐变模式）
    float4   _AmbientColor;            // 环境光颜色（用于纯色模式）
    int      DDGI_PROBE_REDUCTION;     // 探针精简开关（0=关闭，1=开启）
    float    _SkyboxIntensityMultiplier; // 天空盒强度乘数
    float    _SkyboxExposure;          // 天空盒曝光值
    float    _Pad0;                    // 填充变量，用于对齐内存（无实际作用）
CBUFFER_END

//天空盒纹理
TEXTURECUBE(_SkyboxCubemap); SAMPLER(sampler_SkyboxCubemap);

//
uint3 _ReductionInputSize;

#endif