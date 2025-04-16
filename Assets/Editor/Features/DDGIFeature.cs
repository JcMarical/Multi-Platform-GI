using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Random = System.Random;

public class DDGIFeature : ScriptableRendererFeature
{
    public sealed class DDGIPass : ScriptableRenderPass
    {

        private DDGI mddgiOverride;
        private DDGICustomBounds mCustomGIVolume;   
        
        private bool mIsInitialized = false;

        //是否重置历史、重定位、分类
        private bool mNeedToResetProbeHistory = true;
        private bool mNeedToResetProbeRelocation = true;
        private bool mNeedToResetProbeClassification = true;
        
        //探针纹理尺寸常量（包含上下边界）
        private static readonly int PROBE_NUM_IRRADIANCE_INTERIOR_TEXELS = 6;
        private static readonly int PROBE_NUM_IRRADIANCE_TEXELS = PROBE_NUM_IRRADIANCE_INTERIOR_TEXELS + 2; // Including 1 texel for up border and 1 texel for down border;
        private static readonly int PROBE_NUM_DISTANCE_INTERIOR_TEXELS = 14;
        private static readonly int PROBE_NUM_DISTANCE_TEXELS = PROBE_NUM_DISTANCE_INTERIOR_TEXELS + 2;
        
        
        
        #region CPU和GPU常量区体积数据
        
        //CPU端体积参数结构：原点，范围，数量，最大光线数量和光线数量
        struct DDGIVolumeCpu
        {
            public Vector3 Origin;
            public Vector3 Extents;
            public Vector3Int NumProbes;
            public int MaxNumRays;
            public int NumRays;
        }
        
        //
        private DDGIVolumeCpu mDDGIVolumeCpu;

        /// <summary>
        /// DDGI体积的GPU参数结构体，用于将配置数据从CPU传递到GPU着色器
        /// </summary>
        struct DDGIVolumeGpu
        {
            public Vector4 _ProbeRotation;           // 探针旋转四元数，用于旋转探针的采样方向
            public Vector3 _StartPosition;           // 探针体积的起始位置（左下角）
            public int _RaysPerProbe;                // 每个探针当前使用的光线数量
            public Vector3 _ProbeSize;               // 探针体积的三维尺寸
            public int _MaxRaysPerProbe;             // 每个探针最大允许的光线数量上限
            public Vector3Int _ProbeCount;           // 三个方向上的探针数量(X,Y,Z)
            public float _NormalBias;                // 基础法线偏移值，用于避免自遮挡
            public Vector3 _RandomVector;            // 随机向量，用于光线方向随机化
            public float _EnergyPreservation;        // 能量保存系数，影响间接光照强度

            public float _RandomAngle;               // 光线随机化角度范围
            public float _HistoryBlendWeight;        // 历史帧混合权重，控制时间滤波平滑度
            public float _IndirectIntensity;         // 间接光照强度乘数
            public float _NormalBiasMultiplier;      // 法线偏移乘数，进一步调整法线偏移

            public float _ViewBiasMultiplier;        // 视图偏移乘数，调整视图相关偏移
            public int DDGI_PROBE_CLASSIFICATION;    // 探针分类开关(0=关闭,1=开启)，用于确定探针活跃状态
            public int DDGI_PROBE_RELOCATION;        // 探针重定位开关(0=关闭,1=开启)，用于动态调整探针位置
            public float _ProbeFixedRayBackfaceThreshold; // 探针背面检测阈值，用于分类和重定位算法

            public float _ProbeMinFrontfaceDistance; // 探针前面最小距离阈值，用于分类和重定位算法
            public int _DirectionalLightCount;       // 场景中方向光源数量（不考虑剔除）
            public int _PunctualLightCount;          // 场景中点光源和聚光灯数量（不考虑剔除）
            public int DDGI_SKYLIGHT_MODE;           // 天空光照模式(0=天空盒,1=渐变,2=纯色,3=不支持)

            public Vector4 _SkyboxTintColor;         // 天空盒颜色色调
            public Vector4 _SkyColor;                // 天空颜色（用于渐变模式）
            public Vector4 _EquatorColor;            // 地平线颜色（用于渐变模式）
            public Vector4 _GroundColor;             // 地面颜色（用于渐变模式）
            public Vector4 _AmbientColor;            // 环境光颜色（用于纯色模式）

            public int DDGI_PROBE_REDUCTION;         // 探针精简开关(0=关闭,1=开启)，用于减少不必要探针提高性能
            public float _SkyboxIntensityMultiplier; // 天空盒强度乘数
            public float _SkyboxExposure;            // 天空盒曝光值
            public float _Pad0;                      // 内存对齐填充变量，确保GPU内存布局正确
        }

        /// <summary>
        /// DDGI体积GPU参数实例，用于在运行时存储和更新参数
        /// </summary>
        private DDGIVolumeGpu mDDGIVolumeGpu;
        //ConstantBuffer存储常量数据，每帧提交一次
        private ConstantBuffer<DDGIVolumeGpu> mDDGIVolumeGpuCB;


        #endregion
        #region [Shader Resources] Shader资源
        

        private RayTracingShader mDDGIRayTraceShader;                               //DDGI光线追踪着色器
        private RayTracingAccelerationStructure mAccelerationStructure;             //加速结构
        
        private ComputeBuffer mRayBuffer;           //在GPU上创建缓冲区，配合CS使用

        private readonly ComputeShader mUpdateIrradianceCS; //更新辐照度
        private readonly int mUpdateIrradianceKernel;
        private readonly ComputeShader mUpdateDistanceCS;//更新深度
        private readonly int mUpdateDistanceKernel;
        private readonly ComputeShader mProbeClassificationCS;//探针分类
        private readonly int mResetClassificationKernel;
        private readonly int mProbeClassificationKernel;
        private readonly ComputeShader mRelocateProbeCS;//探针重定位
        private readonly int mResetRelocationKernel;
        private readonly int mRelocateProbeKernel;
        private readonly ComputeShader mProbeReductionCS;//探针缩减
        private readonly int mReductionKernel;
        private readonly int mExtraReductionKernel;

        private readonly Shader mCubemapSkyPS;  //天空盒着色器
        
        #endregion

        
        #region [Probe Volume Textures] 探针体积纹理
        //定义七种体积纹理种类
        private enum DDGIVolumeTextureType
        {
            RayData = 0,                //光线数据
            Irradiance = 1,             //辐照度
            Distance = 2,               //深度
            ProbeData = 3,              //探针数据
            Variability = 4,            //变化性（对比前后）
            VariabilityAverage = 5,     //平均变化性
            Count                       //计数？？
        }
        //注：渲染纹理都要附带一个渲染目标id.
        
        //辐射度量以及距离（及其历史帧的纹理）
        private RenderTexture mProbeIrradiance;
        private RenderTargetIdentifier mProbeIrradianceId;
        private RenderTexture mProbeDistance;
        private RenderTargetIdentifier mProbeDistanceId;
        private RenderTexture mProbeIrradianceHistory;
        private RenderTargetIdentifier mProbeIrradianceHistoryId;
        private RenderTexture mProbeDistanceHistory;
        private RenderTargetIdentifier mProbeDistanceHistoryId;
        
        
        // 探针重定位，存储探针数据
        private RenderTexture mProbeData;                           // For Probe Relocation
        private RenderTargetIdentifier mProbeDataId;
        
        //探针变化性（用来混合历史）
        private RenderTexture mProbeVariability;                    // For Probe Variability
        private RenderTargetIdentifier mProbeVariabilityId;         
        private RenderTexture mProbeVariabilityAverage;             // For Probe Variability
        private RenderTargetIdentifier mProbeVariabilityAverageId;
        
        #endregion

        #region [Probe Variability] 探针变化率
        
        private bool mIsConverged;  //当前全局光照是否已经收敛？
        private readonly uint mMinimumVariabilitySamples = 16u; //最小变化率样本--16个
        private bool mClearProbeVariability;                    //是否清楚变化率数据
        private uint mNumVolumeVariabilitySamples = 0u;         //已经收集的变化率样本数量
        
        #endregion
        
         #region [Light Update and Change Dectect]    光源更新和变化检测
        
        private ComputeBuffer mDirectionalLightBuffer;  //直射光缓冲
        private ComputeBuffer mPunctualLightBuffer;     //点光源缓冲
        
        // 用于在Build Light Structured Buffer过程中收集定向光数据
        private struct DirectionalLight
        {
            public Vector4 direction;
            public Vector4 color;
        }

        // 用于在Build Light Structured Buffer过程中收集精确光数据
        // Reference: RealtimeLights.hlsl 153 | 注：我们认定点光源、聚光灯和面光源是精确光，定向光不在此列
        private struct PunctualLight
        {
            public Vector4 position;
            public Vector4 color;
            public Vector4 distanceAndSpotAttenuation;
            public Vector4 spotDirection;
        }
        
        // 用于在Build Light Structured Buffer过程中确定天光模式
        // Raytrace shader不支持multi_compile，我们使用int define的方式确定天光模式
        private enum SkyLightMode
        {
            DDGI_SKYLIGHT_MODE_SKYBOX_CUBEMAP = 0,  //立方体贴图天光
            DDGI_SKYLIGHT_MODE_GRADIENT = 1,        //渐变色天光
            DDGI_SKYLIGHT_MODE_COLOR = 2,           //纯色天光
            DDGI_SKYLIGHT_MODE_UNSUPPORTED = 3      //不支持的天光
        }

        // 针对URP默认Skybox的参数Id，用于Build Light Structured Buffer过程以及Probe Variability灯光比对过程
        private static class SkyboxParam
        {
            public static readonly int _Tint = Shader.PropertyToID("_Tint");
            public static readonly int _Exposure = Shader.PropertyToID("_Exposure");
            public static readonly int _Rotation = Shader.PropertyToID("_Rotation");
            public static readonly int _Tex = Shader.PropertyToID("_Tex");
        }

        // 只用于确定最新一帧中Sky Light设置是否发生改变，与Build Light Structured Buffer过程无关，仅用于Probe Variability
        private class SkyLight
        {
            public SkyLight(Material skybox, AmbientMode ambientMode, float ambientIntensity,
                Color skyColor, Color equatorColor, Color groundColor)
            {
                if (skybox != null)
                {
                    skyboxTint = skybox.GetColor(SkyboxParam._Tint);
                    skyboxExposure = skybox.GetFloat(SkyboxParam._Exposure);
                    skyboxRotation = skybox.GetFloat(SkyboxParam._Rotation);
                    skyboxTex = skybox.GetTexture(SkyboxParam._Tex);
                }
                this.ambientMode = ambientMode;
                this.ambientIntensity = ambientIntensity;
                this.skyColor = skyColor;
                this.equatorColor = equatorColor;
                this.groundColor = groundColor;
            }

            //判断是否变化，主要就还是和原数据对比
            public bool Equals(SkyLight skyLight)
            {
                
                bool result = true;
                if (ambientMode == skyLight.ambientMode && ambientMode == AmbientMode.Skybox)
                {
                    result &= skyboxTint == skyLight.skyboxTint;
                    result &= FloatEqual(skyboxExposure, skyLight.skyboxExposure);
                    result &= FloatEqual(skyboxRotation, skyLight.skyboxRotation);
                    result &= skyboxTex == skyLight.skyboxTex;
                }
                result &= ambientMode == skyLight.ambientMode;
                result &= FloatEqual(ambientIntensity, skyLight.ambientIntensity);
                result &= skyColor == skyLight.skyColor;
                result &= equatorColor == skyLight.equatorColor;
                result &= groundColor == skyLight.groundColor;
                return result;
            }
        
            //浮点数判断相等。
            private static bool FloatEqual(float a, float b) => Mathf.Abs(a - b) < 0.0001f;
            
            //天光缓存数据
            private Color skyboxTint = Color.black;
            private float skyboxExposure = 0.0f;
            private float skyboxRotation = 0.0f;
            private Texture skyboxTex = null;
            private AmbientMode ambientMode;
            private float ambientIntensity;
            private Color skyColor;
            private Color equatorColor;
            private Color groundColor;
        }

        // 光照数据缓存，用于Probe Variability阶段
        private List<DirectionalLight> mCachedDirectionalLights = new List<DirectionalLight>();
        private List<PunctualLight> mCachedPunctualLights = new List<PunctualLight>();
        private SkyLight mCachedSkyLight = new SkyLight(null, AmbientMode.Flat, 0.0f, Color.black, Color.black, Color.black);
        private bool mAnyLightChanged;
        private bool mSkyChanged;
        
        #endregion


        /// <summary>
        /// GPU参数静态只读类，用于集中管理所有与GPU相关的参数和标识符
        /// </summary>
        private static class GpuParams
        {
            // 探针追踪和更新相关
            public static readonly string RayGenShaderName = "DDGI_RayGen";  // DDGI光线追踪着色器名称
            
            
            //-----------------将一些shader属性统一的转换为整数标识符---------------------
            //光线缓冲
            public static readonly int RayBuffer = Shader.PropertyToID("RayBuffer");  // 光线数据缓冲区
            
            //光源缓冲
            public static readonly int DirectionalLightBuffer = Shader.PropertyToID("DirectionalLightBuffer");  // 方向光源缓冲区
            public static readonly int PunctualLightBuffer = Shader.PropertyToID("PunctualLightBuffer");  // 点光源/聚光灯缓冲区
            
            //DDGI的GPU体积参数
            public static readonly int DDGIVolumeGpu = Shader.PropertyToID("DDGIVolumeGpu");  // DDGI体积参数常量缓冲区

            // 探针数据及其历史帧存储
            public static readonly int _ProbeIrradiance = Shader.PropertyToID("_ProbeIrradiance");  // 探针辐照度纹理
            public static readonly int _ProbeIrradianceHistory = Shader.PropertyToID("_ProbeIrradianceHistory");  // 探针辐照度历史纹理
            public static readonly int _ProbeDistance = Shader.PropertyToID("_ProbeDistance");  // 探针距离纹理
            public static readonly int _ProbeDistanceHistory = Shader.PropertyToID("_ProbeDistanceHistory");  // 探针距离历史纹理

            // 加速结构
            public static readonly int _AccelerationStructure = Shader.PropertyToID("_AccelerationStructure");  // 光线追踪加速结构

            // 天光采样
            public static readonly string DDGI_SKYLIGHT_MODE = "DDGI_SKYLIGHT_MODE";  // 天光模式开关
            public static readonly int _SkyboxCubemap = Shader.PropertyToID("_SkyboxCubemap");  // 天空盒立方体贴图

            // 探针重定位
            public static readonly string DDGI_PROBE_RELOCATION = "DDGI_PROBE_RELOCATION";  // 探针重定位开关
            public static readonly int _ProbeData = Shader.PropertyToID("_ProbeData");  // 探针数据纹理

            // 调试相关
            public static readonly string DDGI_SHOW_INDIRECT_ONLY = "DDGI_SHOW_INDIRECT_ONLY";  // 仅显示间接光照开关
            public static readonly string DDGI_SHOW_PURE_INDIRECT_RADIANCE = "DDGI_SHOW_PURE_INDIRECT_RADIANCE";  // 显示纯间接辐射开关

            // 探针精简（变异性）
            public static readonly int _ReductionInputSize = Shader.PropertyToID("_ReductionInputSize");  // 精简输入尺寸
            public static readonly int _ProbeVariability = Shader.PropertyToID("_ProbeVariability");  // 探针变异性
            public static readonly int _ProbeVariabilityAverage = Shader.PropertyToID("_ProbeVariabilityAverage");  // 探针平均变异性
        }


        #region 核心函数与渲染设置
        
        #region RenderFeature核心渲染流程
        
        public DDGIPass()
        {
            //将DDGI实现放置在常规前向渲染前
            renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
            
            //找到DDGI光线追踪着色器
            mDDGIRayTraceShader = Resources.Load<RayTracingShader>("Shaders/DDGIRayTracing");  
            
            //找到各个CS及其Kernel
            mUpdateIrradianceCS = Resources.Load<ComputeShader>("Shaders/DDGIUpdateIrradiance");
            mUpdateIrradianceKernel = mUpdateIrradianceCS.FindKernel("DDGIUpdateIrradiance");
            
            mUpdateDistanceCS = Resources.Load<ComputeShader>("Shaders/DDGIUpdateDistance");
            mUpdateDistanceKernel = mUpdateDistanceCS.FindKernel("DDGIUpdateDistance");
            
            mProbeClassificationCS = Resources.Load<ComputeShader>("Shaders/DDGIProbeClassification");
            mResetClassificationKernel = mProbeClassificationCS.FindKernel("DDGIProbeClassificationResetCS");
            mProbeClassificationKernel = mProbeClassificationCS.FindKernel("DDGIProbeClassificationCS");
            
            mRelocateProbeCS = Resources.Load<ComputeShader>("Shaders/DDGIRelocateProbe");
            mResetRelocationKernel = mRelocateProbeCS.FindKernel("DDGIResetRelocation");
            mRelocateProbeKernel = mRelocateProbeCS.FindKernel("DDGIRelocateProbe");
            
            mProbeReductionCS = Resources.Load<ComputeShader>("Shaders/DDGIReduction");
            mReductionKernel = mProbeReductionCS.FindKernel("DDGIReductionCS");
            mExtraReductionKernel = mProbeReductionCS.FindKernel("DDGIExtraReductionCS");
         
            //加速结构：
            // 自动管理模式（不需要手动干预）
            // 支持所有类型的光线追踪操作，
            // 最大递归深度255（啊？255？）
            RayTracingAccelerationStructure.RASSettings setting = new RayTracingAccelerationStructure.RASSettings
            (RayTracingAccelerationStructure.ManagementMode.Automatic, 
                RayTracingAccelerationStructure.RayTracingModeMask.Everything,  
                255);
            mAccelerationStructure = new RayTracingAccelerationStructure(setting);
            
            //初始化常量缓冲
            mDDGIVolumeGpuCB = new ConstantBuffer<DDGIVolumeGpu>();
            
            //找天光着色器
            // Shader.Find不稳健，Shader在打包后可能出现丢失的情况，此时用Find是无效的
            // 出于演示目的在此摆烂
            mCubemapSkyPS = Shader.Find("Skybox/Cubemap");
        }
        
        //初始化，只会先执行一次
        private void Initialize()
        {
            //已经初始化或者Volume为空，返回
            if (mIsInitialized || mddgiOverride == null) return;
            
            // ---------------------------------------
            // Initialize cpu-side volume parameters 初始化CPU端的参数
            // ---------------------------------------
            var sceneBoundingBox = GenerateSceneMeshBounds();   //生成：DDGI的包围盒
            
            if (sceneBoundingBox.extents == Vector3.zero) return;   // 包围盒范围为零值表示场景没有任何几何体，没有GI意义
            
            mDDGIVolumeCpu.Origin = sceneBoundingBox.center;//起始取包围盒的中间？？？
            mDDGIVolumeCpu.Extents = 1.1f * sceneBoundingBox.extents;//体积范围澳币包围盒稍微大一点
            //拿到体积设置里的探针数量以及设置的光线数量
            mDDGIVolumeCpu.NumProbes = new Vector3Int(mddgiOverride.probeCountX.value, mddgiOverride.probeCountY.value, mddgiOverride.probeCountZ.value);
            mDDGIVolumeCpu.NumRays = mddgiOverride.raysPerProbe.value;
            //最大光线数量：512
            mDDGIVolumeCpu.MaxNumRays = 512;

            // ---------------------------------------
            // Initialize Ray Data Buffer 初始化光线数据缓冲
            // ---------------------------------------
            //如果当前已经有光线缓冲了，释放掉设置为空
            if(mRayBuffer != null) { mRayBuffer.Release(); mRayBuffer = null; }
            //计算出探针的总数
            int numProbesFlat = mDDGIVolumeCpu.NumProbes.x * mDDGIVolumeCpu.NumProbes.y * mDDGIVolumeCpu.NumProbes.z;
            //光线缓冲数据==探针总数 * 每个探针最大光线数量 ,数据类型或者说步长为16(float4)，并使用默认的计算着色器数据类型 RW
            mRayBuffer = new ComputeBuffer(numProbesFlat * mDDGIVolumeCpu.MaxNumRays, 16 /* float4 */, ComputeBufferType.Default);
            
            // 注：尽量使用GraphicsFormat来提供明确的浮点数 / 定点数指认
            // 比如Distance Texture，先前使用RenderTextureFormat.RG32，该格式使用的是16-bit无符号定点数，但距离需要是浮点数
            // 申请RG32作为Distance Texture会忽略距离信息的小数位，会导致切比雪夫可见性测试发生Edge Clamp Artifacts.
            // ---------------------------------------
            // Radiance and Distance Texture2DArray 辐照度和距离 2D纹理数组
            // ---------------------------------------
            if(mProbeIrradiance != null) { mProbeIrradiance.Release(); mProbeIrradiance = null; }
            var probeIrradianceDimensions = GetDDGIVolumeTextureDimensions(mDDGIVolumeCpu, DDGIVolumeTextureType.Irradiance);
            mProbeIrradiance = new RenderTexture(probeIrradianceDimensions.x, probeIrradianceDimensions.y, 0, GraphicsFormat.R16G16B16A16_SFloat);
            mProbeIrradiance.filterMode = FilterMode.Bilinear;
            mProbeIrradiance.useMipMap = false;
            mProbeIrradiance.autoGenerateMips = false;
            mProbeIrradiance.enableRandomWrite = true;
            mProbeIrradiance.name = "DDGI Probe Irradiance";
            mProbeIrradiance.dimension = TextureDimension.Tex2DArray;
            mProbeIrradiance.volumeDepth = probeIrradianceDimensions.z;
            mProbeIrradiance.Create();
            mProbeIrradianceId = new RenderTargetIdentifier(mProbeIrradiance);
            
            if(mProbeDistance != null) { mProbeDistance.Release(); mProbeDistance = null; }
            var probeDistanceDimensions = GetDDGIVolumeTextureDimensions(mDDGIVolumeCpu, DDGIVolumeTextureType.Distance);
            mProbeDistance = new RenderTexture(probeDistanceDimensions.x, probeDistanceDimensions.y, 0, GraphicsFormat.R16G16_SFloat);
            mProbeDistance.filterMode = FilterMode.Bilinear;
            mProbeDistance.useMipMap = false;
            mProbeDistance.autoGenerateMips = false;
            mProbeDistance.enableRandomWrite = true;
            mProbeDistance.name = "DDGI Probe Distance";
            mProbeDistance.dimension = TextureDimension.Tex2DArray;
            mProbeDistance.volumeDepth = probeDistanceDimensions.z;
            mProbeDistance.Create();
            mProbeDistanceId = new RenderTargetIdentifier(mProbeDistance);
            
            if(mProbeIrradianceHistory != null) { mProbeIrradianceHistory.Release(); mProbeIrradianceHistory = null; }
            mProbeIrradianceHistory = new RenderTexture(mProbeIrradiance.descriptor);
            mProbeIrradianceHistory.name = "DDGI Probe Irradiance History";
            mProbeIrradianceHistory.Create();
            mProbeIrradianceHistoryId = new RenderTargetIdentifier(mProbeIrradianceHistory);
            
            if(mProbeDistanceHistory != null) { mProbeDistanceHistory.Release(); mProbeDistanceHistory = null; }
            mProbeDistanceHistory = new RenderTexture(mProbeDistance.descriptor);
            mProbeDistanceHistory.name = "DDGI Probe Distance History";
            mProbeDistanceHistory.Create();
            mProbeDistanceHistoryId = new RenderTargetIdentifier(mProbeDistanceHistory);

            // ---------------------------------------
            // Create Probe Data
            // ---------------------------------------
            if(mProbeData != null) { mProbeData.Release(); mProbeData = null; }
            var probeDataDimensions = GetDDGIVolumeTextureDimensions(mDDGIVolumeCpu, DDGIVolumeTextureType.ProbeData);
            mProbeData = new RenderTexture(probeDataDimensions.x, probeDataDimensions.y, 0, GraphicsFormat.R16G16B16A16_SFloat);
            mProbeData.filterMode = FilterMode.Bilinear;            //过滤方式，线性过滤
            mProbeData.useMipMap = false;
            mProbeData.autoGenerateMips = false;
            mProbeData.enableRandomWrite = true;
            mProbeData.name = "DDGI Probe Data";
            mProbeData.dimension = TextureDimension.Tex2DArray;     //二维数组
            mProbeData.volumeDepth = probeDataDimensions.z;         //设置数组大小
            mProbeData.Create();
            mProbeDataId = new RenderTargetIdentifier(mProbeData);  //将渲染纹理赋值给渲染目标
            
            // ---------------------------------------
            // Create Probe Variability
            // ---------------------------------------
            if (mProbeVariability != null) { mProbeVariability.Release(); mProbeVariability = null; }
            var probeVariabilityDimensions = GetDDGIVolumeTextureDimensions(mDDGIVolumeCpu, DDGIVolumeTextureType.Variability);
            mProbeVariability = new RenderTexture(probeVariabilityDimensions.x, probeVariabilityDimensions.y, 0, GraphicsFormat.R32_SFloat);
            mProbeVariability.filterMode = FilterMode.Bilinear;
            mProbeVariability.useMipMap = false;
            mProbeVariability.autoGenerateMips = false;
            mProbeVariability.enableRandomWrite = true;
            mProbeVariability.name = "DDGI Probe Variability";
            mProbeVariability.dimension = TextureDimension.Tex2DArray;
            mProbeVariability.volumeDepth = probeVariabilityDimensions.z;
            mProbeVariability.Create();
            mProbeVariabilityId = new RenderTargetIdentifier(mProbeVariability);

            if (mProbeVariabilityAverage != null) { mProbeVariabilityAverage.Release(); mProbeVariabilityAverage = null; }
            var probeVariabilityAverageDimensions = GetDDGIVolumeTextureDimensions(mDDGIVolumeCpu, DDGIVolumeTextureType.VariabilityAverage);
            mProbeVariabilityAverage = new RenderTexture(mProbeVariability.descriptor);
            mProbeVariabilityAverage.graphicsFormat = GraphicsFormat.R32G32_SFloat;
            mProbeVariabilityAverage.width = probeVariabilityAverageDimensions.x;
            mProbeVariabilityAverage.height = probeVariabilityAverageDimensions.y;
            mProbeVariabilityAverage.volumeDepth = probeVariabilityAverageDimensions.z;
            mProbeVariabilityAverage.name = "DDGI Probe Variability Average";
            mProbeVariabilityAverage.Create();
            mProbeVariabilityAverageId = new RenderTargetIdentifier(mProbeVariabilityAverage);

            mIsInitialized = true;
        }
 
        
        
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            base.OnCameraSetup(cmd, ref renderingData);

            mddgiOverride = VolumeManager.instance.stack.GetComponent<DDGI>();

            Initialize();
        }
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            
            if (mddgiOverride == null || !mIsInitialized) return;
            if (!mddgiOverride.IsActive()) return;
            
            var cmd = CommandBufferPool.Get("DDGI Pass");
            var camera = renderingData.cameraData.camera;
            
            //如果需要更新历史帧，重新设置渲染目标并清空
            ResetHistoryInfoIfNeeded(cmd);
            
            // 判断场景灯光是否发生变化，如果发生了变化再进行修改
            // 确保该函数在PushGpuConstants之前运行，否则灯光相关常量不会被推送
            UpdateSceneLights(cmd);
            
            
            // 注：该函数每调用一次，随机数都会更新，进而导致_RandomVector和_RandomAngle发生改变
            // 如果不将更新随机数的逻辑抽离出来，那么该函数每帧只允许调用一次！
            PushGpuConstants(cmd);
            
            //计算探针总数
            int numProbesFlat = mDDGIVolumeCpu.NumProbes.x * mDDGIVolumeCpu.NumProbes.y * mDDGIVolumeCpu.NumProbes.z;

            //DDGI Ratrace Pass 执行
            //主要使用DDGI的Raytrace Shader
            //设置了四种纹理，两种缓冲。
            using (new ProfilingScope(cmd, new ProfilingSampler("DDGI Ray Trace Pass")))
            {
                //如果全局光照未收敛
                if (!mIsConverged)
                {
                    //  创建光线追踪加速结构
                    cmd.BuildRayTracingAccelerationStructure(mAccelerationStructure);
                    
                    //  设置光线追踪加速结构
                    cmd.SetRayTracingAccelerationStructure(mDDGIRayTraceShader, GpuParams._AccelerationStructure, mAccelerationStructure);
                    
                    // 启用光线追踪着色器
                    cmd.SetRayTracingShaderPass(mDDGIRayTraceShader, "DDGIRayTracing");
                    
                    //通过渲染目标，设置全局纹理
                    cmd.SetGlobalTexture(GpuParams._ProbeIrradianceHistory, mProbeIrradianceHistoryId);
                    cmd.SetGlobalTexture(GpuParams._ProbeDistanceHistory, mProbeDistanceHistoryId);
                    cmd.SetGlobalTexture(GpuParams._ProbeData, mProbeDataId);

                    //设置光线缓冲（也是纹理）
                    cmd.SetRayTracingBufferParam(mDDGIRayTraceShader, GpuParams.RayBuffer, mRayBuffer);
                    
                    //设置全局缓冲（点光源和平行光源）
                    cmd.SetGlobalBuffer(GpuParams.DirectionalLightBuffer, mDirectionalLightBuffer);     // We will use it in closest hit shader, not in actual .raytrace shader
                    cmd.SetGlobalBuffer(GpuParams.PunctualLightBuffer, mPunctualLightBuffer);           // We will use it in closest hit shader, not in actual .raytrace shader
                    
                    //发送光源
                    cmd.DispatchRays(mDDGIRayTraceShader, GpuParams.RayGenShaderName, (uint)mDDGIVolumeCpu.NumRays, (uint)numProbesFlat, 1, camera);
                }
            }
            
            // 更新 Irradiance
            // 主要计算Irradiance更新相关的 Compute Shader
            using (new ProfilingScope(cmd, new ProfilingSampler("DDGI Update Irradiance Pass")))
            {
                if (!mIsConverged)
                {
                    cmd.SetComputeBufferParam(mUpdateIrradianceCS, mUpdateIrradianceKernel, GpuParams.RayBuffer, mRayBuffer);
                    cmd.SetComputeTextureParam(mUpdateIrradianceCS, mUpdateIrradianceKernel, GpuParams._ProbeIrradiance, mProbeIrradianceId);
                    cmd.SetComputeTextureParam(mUpdateIrradianceCS, mUpdateIrradianceKernel, GpuParams._ProbeIrradianceHistory, mProbeIrradianceHistoryId);
                    cmd.SetComputeTextureParam(mUpdateIrradianceCS, mUpdateIrradianceKernel, GpuParams._ProbeVariability, mProbeVariabilityId);

                    // 注意我们是Y-UP，这里Dispatch需要反转
                    cmd.DispatchCompute(mUpdateIrradianceCS, mUpdateIrradianceKernel, mDDGIVolumeCpu.NumProbes.x, mDDGIVolumeCpu.NumProbes.z, mDDGIVolumeCpu.NumProbes.y);
                }
            }
            
            // 更新 探针距离纹理
            // 主要计算Distance更新相关的 Compute Shader
            using (new ProfilingScope(cmd, new ProfilingSampler("DDGI Update Distance Pass")))
            {
                if (!mIsConverged)
                {
                    cmd.SetComputeBufferParam(mUpdateDistanceCS, mUpdateDistanceKernel, GpuParams.RayBuffer, mRayBuffer);
                    cmd.SetComputeTextureParam(mUpdateDistanceCS, mUpdateDistanceKernel, GpuParams._ProbeDistance, mProbeDistanceId);
                    cmd.SetComputeTextureParam(mUpdateDistanceCS, mUpdateDistanceKernel, GpuParams._ProbeDistanceHistory, mProbeDistanceHistoryId);

                    // 注意我们是Y-UP，这里Dispatch需要反转
                    cmd.DispatchCompute(mUpdateDistanceCS, mUpdateDistanceKernel, mDDGIVolumeCpu.NumProbes.x, mDDGIVolumeCpu.NumProbes.z, mDDGIVolumeCpu.NumProbes.y);
                }
            }
            
            //---------- 探针重定位功能 -----------
            //执行的也是RelocationComputeShder的功能
            //区别在于会根据开关 判断重置Kernel还是重定位的 Kernel。
            if (mddgiOverride.enableProbeRelocation.value)
            {
                using (new ProfilingScope(cmd, new ProfilingSampler("DDGI Relocate Probe Pass")))
                {
                    var numGroupsX = Mathf.CeilToInt(numProbesFlat / 32.0f /*relocationGroupSizeX*/);
                    
                    if (mNeedToResetProbeRelocation)
                    {
                        cmd.SetComputeTextureParam(mRelocateProbeCS, mResetRelocationKernel, GpuParams._ProbeData, mProbeDataId);
                        cmd.DispatchCompute(mRelocateProbeCS, mResetRelocationKernel, numGroupsX, 1, 1);
                        mNeedToResetProbeRelocation = false;
                    }
                    
                    cmd.SetComputeTextureParam(mRelocateProbeCS, mRelocateProbeKernel, GpuParams._ProbeData, mProbeDataId);
                    cmd.SetComputeBufferParam(mRelocateProbeCS, mRelocateProbeKernel, GpuParams.RayBuffer, mRayBuffer);
                    cmd.DispatchCompute(mRelocateProbeCS, mRelocateProbeKernel, numGroupsX, 1, 1);
                }
            }
            else
            {
                if (!mNeedToResetProbeRelocation)
                {
                    var numGroupsX = Mathf.CeilToInt(numProbesFlat / 32.0f /*relocationGroupSizeX*/);
                    
                    cmd.SetComputeTextureParam(mRelocateProbeCS, mResetRelocationKernel, GpuParams._ProbeData, mProbeDataId);
                    cmd.DispatchCompute(mRelocateProbeCS, mResetRelocationKernel, numGroupsX, 1, 1);
                    mNeedToResetProbeRelocation = true;
                }
            }
            
            
            //----------- 探针分类功能 ------------
            //依然是CS
            //根据开关 分类或重置
            if (mddgiOverride.enableProbeClassification.value)
            {
                using (new ProfilingScope(cmd, new ProfilingSampler("DDGI Classify Probe Pass")))
                {
                    var numGroupsX = Mathf.CeilToInt(numProbesFlat / 32.0f /*relocationGroupSizeX*/);

                    if (mNeedToResetProbeClassification)
                    {
                        cmd.SetComputeTextureParam(mProbeClassificationCS, mResetClassificationKernel, GpuParams._ProbeData, mProbeDataId);
                        cmd.DispatchCompute(mProbeClassificationCS, mResetClassificationKernel, numGroupsX, 1, 1);
                        mNeedToResetProbeClassification = false;
                    }
                    
                    cmd.SetComputeTextureParam(mProbeClassificationCS, mProbeClassificationKernel, GpuParams._ProbeData, mProbeDataId);
                    cmd.SetComputeBufferParam(mProbeClassificationCS, mProbeClassificationKernel, GpuParams.RayBuffer, mRayBuffer);
                    cmd.DispatchCompute(mProbeClassificationCS, mProbeClassificationKernel, numGroupsX, 1, 1);
                }
            }
            else
            {
                if (!mNeedToResetProbeClassification)
                {
                    var numGroupsX = Mathf.CeilToInt(numProbesFlat / 32.0f /*relocationGroupSizeX*/);
                    
                    cmd.SetComputeTextureParam(mProbeClassificationCS, mResetClassificationKernel, GpuParams._ProbeData, mProbeDataId);
                    cmd.DispatchCompute(mProbeClassificationCS, mResetClassificationKernel, numGroupsX, 1, 1);
                    mNeedToResetProbeClassification = true;
                }
            }
            
            //----------- 探针变异性（光照收敛计算） -------------
            //emmm: 主要做规约方面的工作，但是没怎么看懂
            // 大概步骤是 开启设置->设置线程组和纹理->第一步规约-> extra 规约，并且最后还有个异步读值。
            // 关闭设置 积分永远不会收敛，所以会一直计算。
            if (mddgiOverride.enableProbeVariability.value)
            {
                using (new ProfilingScope(cmd, new ProfilingSampler("DDGI Variability Pass")))
                {
                    // TODO: Y-UP Probe Volume硬编码，如果要修改Volume轴向需要做分支
                    var inputTexels = new Vector3Int(mDDGIVolumeCpu.NumProbes.x * PROBE_NUM_IRRADIANCE_INTERIOR_TEXELS,
                        mDDGIVolumeCpu.NumProbes.z * PROBE_NUM_IRRADIANCE_INTERIOR_TEXELS,
                        mDDGIVolumeCpu.NumProbes.y);
                    var NumThreadsInGroup = new Vector3Int(4, 8, 4);
                    var ThreadSampleFootprint = new Vector2Int(4, 2);
                    
                    // -------------------------
                    // First Reduction Pass
                    // -------------------------
                    {
                        cmd.SetComputeTextureParam(mProbeReductionCS, mReductionKernel, GpuParams._ProbeVariability, mProbeVariabilityId);
                        cmd.SetComputeTextureParam(mProbeReductionCS, mReductionKernel, GpuParams._ProbeVariabilityAverage, mProbeVariabilityAverageId);
                        cmd.SetComputeVectorParam(mProbeReductionCS, GpuParams._ReductionInputSize, new Vector4(inputTexels.x, inputTexels.y, inputTexels.z, 0.0f));

                        var outputTexelsX = Mathf.CeilToInt((float)inputTexels.x / (float)(NumThreadsInGroup.x * ThreadSampleFootprint.x));
                        var outputTexelsY = Mathf.CeilToInt((float)inputTexels.y / (float)(NumThreadsInGroup.y * ThreadSampleFootprint.y));
                        var outputTexelsZ = Mathf.CeilToInt((float)inputTexels.z / (float)(NumThreadsInGroup.z));
                    
                        cmd.DispatchCompute(mProbeReductionCS, mReductionKernel, outputTexelsX, outputTexelsY, outputTexelsZ);

                        inputTexels = new Vector3Int(outputTexelsX, outputTexelsY, outputTexelsZ);
                    }
                    
                    // -------------------------
                    // Extra Reduction Pass
                    // -------------------------
                    {
                        while (inputTexels.x > 1 || inputTexels.y > 1 || inputTexels.z > 1)
                        {
                            var outputTexelsX = Mathf.CeilToInt((float)inputTexels.x / (float)(NumThreadsInGroup.x * ThreadSampleFootprint.x));
                            var outputTexelsY = Mathf.CeilToInt((float)inputTexels.y / (float)(NumThreadsInGroup.y * ThreadSampleFootprint.y));
                            var outputTexelsZ = Mathf.CeilToInt((float)inputTexels.z / (float)(NumThreadsInGroup.z));
                            
                            cmd.SetComputeTextureParam(mProbeReductionCS, mExtraReductionKernel, GpuParams._ProbeVariabilityAverage, mProbeVariabilityAverageId);
                            cmd.SetComputeVectorParam(mProbeReductionCS, GpuParams._ReductionInputSize, new Vector4(inputTexels.x, inputTexels.y, inputTexels.z, 0.0f));
                            
                            cmd.DispatchCompute(mProbeReductionCS, mExtraReductionKernel, outputTexelsX, outputTexelsY, outputTexelsZ);
                            
                            inputTexels = new Vector3Int(outputTexelsX, outputTexelsY, outputTexelsZ);
                        }
                    }
                    
                    // ---------------------------------
                    // Readback From Variability Average
                    // ---------------------------------
                    // Grab First Pixel of Variability Average
                    AsyncGPUReadback.Request(mProbeVariabilityAverage, 0, 0, 1, 0, 1, 0, 1, VariabilityEstimate);
                }
            }
            else
            {
                // 如果不开启variability特性，那么我们假定积分过程永远不会收敛
                mIsConverged = false;
                mClearProbeVariability = true;
                mNumVolumeVariabilitySamples = 0u;
            }


            
            //历史数据赋值，虽然暂时不知道有啥用。
            cmd.CopyTexture(mProbeIrradianceId, mProbeIrradianceHistoryId);
            cmd.CopyTexture(mProbeDistanceId, mProbeDistanceHistoryId);
            
            
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            
        }



        public void Release()
        {
            if (mRayBuffer != null) { mRayBuffer.Release(); mRayBuffer = null; }
            if (mDirectionalLightBuffer != null) { mDirectionalLightBuffer.Release(); mDirectionalLightBuffer = null; }
            if (mPunctualLightBuffer != null) { mPunctualLightBuffer.Release(); mPunctualLightBuffer = null; }
            
            if (mAccelerationStructure != null) { mAccelerationStructure.Release(); mAccelerationStructure = null; }
            
            if (mProbeIrradiance != null) { mProbeIrradiance.Release(); mProbeIrradiance = null; }
            if (mProbeDistance != null) { mProbeDistance.Release(); mProbeDistance = null; }
            if (mProbeIrradianceHistory != null) { mProbeIrradianceHistory.Release(); mProbeIrradianceHistory = null; }
            if (mProbeDistanceHistory != null) { mProbeDistanceHistory.Release(); mProbeDistanceHistory = null; }
            if (mProbeData != null) { mProbeData.Release(); mProbeData = null; }
            if (mProbeVariability != null) { mProbeVariability.Release(); mProbeVariability = null; }
            if (mProbeVariabilityAverage != null) { mProbeVariabilityAverage.Release(); mProbeVariabilityAverage = null; }
            
            
            if (mDDGIVolumeGpuCB != null) { mDDGIVolumeGpuCB.Release(); mDDGIVolumeGpuCB = null; }
        }

        #endregion


        //参数重新初始化，即修改一些设置，让函数开始运行
        public void Reinitialize()
        {
            mIsInitialized = false;
            mNeedToResetProbeHistory = true;
            mNeedToResetProbeRelocation = true;
            mNeedToResetProbeClassification = true;
            mClearProbeVariability = true;
            mIsConverged = false;
        }



        /// <summary>
        /// 生成DDGI的包围盒
        /// </summary>
        /// <returns></returns>
        private Bounds GenerateSceneMeshBounds()
        {
            Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);

            if (mddgiOverride != null && mddgiOverride.useCustomBounds.value)
            {
                // 目前只支持单个自定义包围盒
                mCustomGIVolume = FindFirstObjectByType<DDGICustomBounds>();
                var boxCollider = mCustomGIVolume.GetComponent<BoxCollider>();
                if (boxCollider != null) bounds = boxCollider.bounds;
            }
            else
            {
                // 根据场景Mesh自动生成包围盒
                foreach (var meshRenderer in FindObjectsOfType<MeshRenderer>())
                {
                    bounds.Encapsulate(meshRenderer.bounds);
                }

                // 理论上来说我们不会逐帧更新包围盒，因此不必强行包含骨骼网格体，下面这段去掉也是可以的
                foreach (var skinnedMeshRenderer in FindObjectsOfType<SkinnedMeshRenderer>())
                {
                    bounds.Encapsulate(skinnedMeshRenderer.bounds);
                }
            }

            return bounds;
        }



        #endregion


        #region 灯光更新

        
            // 更新所有灯光，并监测灯光数据变化
            private void UpdateSceneLights(CommandBuffer cmd)
            {
                BuildLightStructuredBuffer(cmd);
                UpdateSkyLight(cmd);
                mClearProbeVariability = mAnyLightChanged || mSkyChanged;
            }
            
            // Unity默认会对场景中的额外光做剔除，这会影响我们获取场景全局的光照信息，故只能自己在CPU端手动收集一次
            private void BuildLightStructuredBuffer(CommandBuffer cmd)
            {
                var cpuLights = FindObjectsOfType<Light>();

                var gpuDirectionalLights = new List<DirectionalLight>();
                var gpuPunctualLights = new List<PunctualLight>();
                foreach (var cpuLight in cpuLights)
                {
                    if (cpuLight.lightmapBakeType == LightmapBakeType.Baked) continue;

                    // 暂不支持面光源的动态全局光照...
                    if (cpuLight.type == LightType.Point || cpuLight.type == LightType.Spot)
                    {
                        var position = cpuLight.transform.position;
                        var color = cpuLight.color * cpuLight.intensity;
                        var lightAttenuation = new Vector4(0.0f, 1.0f, 0.0f, 1.0f);
                        var lightSpotDir = new Vector4(0.0f, 0.0f, 1.0f, 0.0f);

                        GetPunctualLightDistanceAttenuation(cpuLight.range, ref lightAttenuation);

                        if (cpuLight.type == LightType.Spot)
                        {
                            GetSpotDirection(cpuLight.transform.forward, out lightSpotDir);
                            GetSpotAngleAttenuation(cpuLight.spotAngle, cpuLight.innerSpotAngle,
                                ref lightAttenuation);
                        }

                        PunctualLight punctualLight;
                        punctualLight.position = new Vector4(position.x, position.y, position.z, 1.0f);
                        punctualLight.color = color;
                        punctualLight.distanceAndSpotAttenuation = lightAttenuation;
                        punctualLight.spotDirection = lightSpotDir;

                        gpuPunctualLights.Add(punctualLight);
                    }
                    else if (cpuLight.type == LightType.Directional)
                    {
                        var lightForward = cpuLight.transform.forward;

                        DirectionalLight directionalLight;
                        directionalLight.direction =
                            new Vector4(-lightForward.x, -lightForward.y, -lightForward.z, 0.0f);
                        directionalLight.color = cpuLight.color;

                        gpuDirectionalLights.Add(directionalLight);
                    }
                }

                     
                // 如果灯光数组大小为0，就只申请带1个元素的空buffer，创建大小为0的ComputeBuffer会引发错误
                if(mDirectionalLightBuffer != null) { mDirectionalLightBuffer.Release(); mDirectionalLightBuffer = null; }
                mDirectionalLightBuffer = new ComputeBuffer(Mathf.Max(gpuDirectionalLights.Count, 1), 2 * 16, ComputeBufferType.Default);
            
                if(mPunctualLightBuffer != null) { mPunctualLightBuffer.Release(); mPunctualLightBuffer = null; }
                mPunctualLightBuffer = new ComputeBuffer(Mathf.Max(gpuPunctualLights.Count, 1), 4 * 16, ComputeBufferType.Default);

                mDirectionalLightBuffer.SetData(gpuDirectionalLights.ToArray());
                mPunctualLightBuffer.SetData(gpuPunctualLights.ToArray());

                mDDGIVolumeGpu._DirectionalLightCount = gpuDirectionalLights.Count;
                mDDGIVolumeGpu._PunctualLightCount = gpuPunctualLights.Count;
                /*cmd.SetGlobalInt(GpuParams._DirectionalLightCount, gpuDirectionalLights.Count);
                cmd.SetGlobalInt(GpuParams._PunctualLightCount, gpuPunctualLights.Count);*/
                
                
                // -----------------------------
                // Any Light Changed Determine 任何光线改变
                // -----------------------------
                mAnyLightChanged = (!mCachedDirectionalLights.SequenceEqual(gpuDirectionalLights)) || (!mCachedPunctualLights.SequenceEqual(gpuPunctualLights));
                if (mAnyLightChanged)
                {
                    mCachedDirectionalLights = new List<DirectionalLight>(gpuDirectionalLights);
                    mCachedPunctualLights = new List<PunctualLight>(gpuPunctualLights);
                }
            }
            
            
            //抄一些URP的源码过来
                       // Reference: UniversalRenderPipelineCore.cs 1634
            private static void GetPunctualLightDistanceAttenuation(float lightRange, ref Vector4 lightAttenuation)
            {
                // Light attenuation in universal matches the unity vanilla one (HINT_NICE_QUALITY).
                // attenuation = 1.0 / distanceToLightSqr
                // The smoothing factor makes sure that the light intensity is zero at the light range limit.
                // (We used to offer two different smoothing factors.)

                // The current smoothing factor matches the one used in the Unity lightmapper.
                // smoothFactor = (1.0 - saturate((distanceSqr * 1.0 / lightRangeSqr)^2))^2
                float lightRangeSqr = lightRange * lightRange;
                float fadeStartDistanceSqr = 0.8f * 0.8f * lightRangeSqr;
                float fadeRangeSqr = (fadeStartDistanceSqr - lightRangeSqr);
                float lightRangeSqrOverFadeRangeSqr = -lightRangeSqr / fadeRangeSqr;
                float oneOverLightRangeSqr = 1.0f / Mathf.Max(0.0001f, lightRangeSqr);

                // On all devices: Use the smoothing factor that matches the GI.
                lightAttenuation.x = oneOverLightRangeSqr;
                lightAttenuation.y = lightRangeSqrOverFadeRangeSqr;
            }

            // Reference: UniversalRenderPipelineCore.cs 1654
            private static void GetSpotAngleAttenuation(float spotAngle, float? innerSpotAngle, ref Vector4 lightAttenuation)
            {
                // Spot Attenuation with a linear falloff can be defined as
                // (SdotL - cosOuterAngle) / (cosInnerAngle - cosOuterAngle)
                // This can be rewritten as
                // invAngleRange = 1.0 / (cosInnerAngle - cosOuterAngle)
                // SdotL * invAngleRange + (-cosOuterAngle * invAngleRange)
                // If we precompute the terms in a MAD instruction
                float cosOuterAngle = Mathf.Cos(Mathf.Deg2Rad * spotAngle * 0.5f);
                // We need to do a null check for particle lights
                // This should be changed in the future
                // Particle lights will use an inline function
                float cosInnerAngle;
                if (innerSpotAngle.HasValue)
                    cosInnerAngle = Mathf.Cos(innerSpotAngle.Value * Mathf.Deg2Rad * 0.5f);
                else
                    cosInnerAngle = Mathf.Cos((2.0f * Mathf.Atan(Mathf.Tan(spotAngle * 0.5f * Mathf.Deg2Rad) * (64.0f - 18.0f) / 64.0f)) * 0.5f);
                float smoothAngleRange = Mathf.Max(0.001f, cosInnerAngle - cosOuterAngle);
                float invAngleRange = 1.0f / smoothAngleRange;
                float add = -cosOuterAngle * invAngleRange;

                lightAttenuation.z = invAngleRange;
                lightAttenuation.w = add;
            }
            
            // Reference: UniversalRenderPipelineCore.cs 1681
            private static void GetSpotDirection(Vector3 forward, out Vector4 lightSpotDir)
            {
                lightSpotDir = new Vector4(-forward.x, -forward.y, -forward.z, 0.0f);
            }
            
            // 更新天空光照，以便给Miss Shader采样使用（Window->Rendering->Lighting）
            private void UpdateSkyLight(CommandBuffer cmd)
            {
                // -----------------------------
                // Sky Light Changed Determine
                // -----------------------------
                var currSkyLight = new SkyLight(RenderSettings.skybox, RenderSettings.ambientMode, RenderSettings.ambientIntensity,
                    RenderSettings.ambientSkyColor, RenderSettings.ambientEquatorColor, RenderSettings.ambientGroundColor);

                mSkyChanged = !mCachedSkyLight.Equals(currSkyLight);
                if (mSkyChanged) { mCachedSkyLight = currSkyLight; }
                
                switch (RenderSettings.ambientMode)
                {
                    case AmbientMode.Skybox:
                        UpdateSkyLightAsSkybox(cmd);
                        break;
                    case AmbientMode.Trilight:
                        UpdateSkyLightAsGradient(cmd);
                        break;
                    case AmbientMode.Flat:
                        UpdateSkyLightAsColor(cmd);
                        break;
                }
            }

            private void UpdateSkyLightAsSkybox(CommandBuffer cmd)
            {
                var skybox = RenderSettings.skybox;
                if (skybox == null)
                {
                    // 如果没有正确设置天空盒材质，则Fallback到纯色 (Ambient Color)，与Unity内行为一致
                    UpdateSkyLightAsColor(cmd);
                    return;
                }

                if (mCubemapSkyPS == null)
                {
                    Debug.LogWarning("DDGIFeature没有成功找到URP内置的天空盒Shader，请排查");
                    UpdateSkyLightAsBlack(cmd);
                    return;
                }

                if (skybox.shader == mCubemapSkyPS)
                {
                    mDDGIVolumeGpu.DDGI_SKYLIGHT_MODE = (int)SkyLightMode.DDGI_SKYLIGHT_MODE_SKYBOX_CUBEMAP;
                    mDDGIVolumeGpu._SkyboxIntensityMultiplier = RenderSettings.ambientIntensity;
                    mDDGIVolumeGpu._SkyboxTintColor = skybox.GetColor(SkyboxParam._Tint);
                    mDDGIVolumeGpu._SkyboxExposure = skybox.GetFloat(SkyboxParam._Exposure);
                    /*cmd.SetRayTracingIntParam(mDDGIRayTraceShader, GpuParams.DDGI_SKYLIGHT_MODE, (int)SkyLightMode.DDGI_SKYLIGHT_MODE_SKYBOX_CUBEMAP);
                    cmd.SetRayTracingFloatParam(mDDGIRayTraceShader, GpuParams._SkyboxIntensityMultiplier, RenderSettings.ambientIntensity);
                    cmd.SetRayTracingVectorParam(mDDGIRayTraceShader, GpuParams._SkyboxTintColor, skybox.GetColor(SkyboxParam._Tint));
                    cmd.SetRayTracingFloatParam(mDDGIRayTraceShader, GpuParams._SkyboxExposure, skybox.GetFloat(SkyboxParam._Exposure));*/
                    cmd.SetRayTracingTextureParam(mDDGIRayTraceShader, GpuParams._SkyboxCubemap, skybox.GetTexture(SkyboxParam._Tex));
                }
                else
                {
                    // 我们目前只支持应用最多的Cubemap式天空盒，其它类型的天空盒不受支持，将Fallback到纯黑
                    UpdateSkyLightAsBlack(cmd);
                }
            }

            private void UpdateSkyLightAsGradient(CommandBuffer cmd)
            {
                mDDGIVolumeGpu.DDGI_SKYLIGHT_MODE = (int)SkyLightMode.DDGI_SKYLIGHT_MODE_GRADIENT;
                mDDGIVolumeGpu._SkyColor = RenderSettings.ambientSkyColor;
                mDDGIVolumeGpu._EquatorColor = RenderSettings.ambientEquatorColor;
                mDDGIVolumeGpu._GroundColor = RenderSettings.ambientGroundColor;
                /*cmd.SetRayTracingIntParam(mDDGIRayTraceShader, GpuParams.DDGI_SKYLIGHT_MODE, (int)SkyLightMode.DDGI_SKYLIGHT_MODE_GRADIENT);
                cmd.SetRayTracingVectorParam(mDDGIRayTraceShader, GpuParams._SkyColor, RenderSettings.ambientSkyColor);
                cmd.SetRayTracingVectorParam(mDDGIRayTraceShader, GpuParams._EquatorColor, RenderSettings.ambientEquatorColor);
                cmd.SetRayTracingVectorParam(mDDGIRayTraceShader, GpuParams._GroundColor, RenderSettings.ambientGroundColor);*/
            }

            private void UpdateSkyLightAsColor(CommandBuffer cmd)
            {
                mDDGIVolumeGpu.DDGI_SKYLIGHT_MODE = (int)SkyLightMode.DDGI_SKYLIGHT_MODE_COLOR;
                mDDGIVolumeGpu._AmbientColor = RenderSettings.ambientSkyColor;
                /*cmd.SetRayTracingIntParam(mDDGIRayTraceShader, GpuParams.DDGI_SKYLIGHT_MODE, (int)SkyLightMode.DDGI_SKYLIGHT_MODE_COLOR);
                cmd.SetRayTracingVectorParam(mDDGIRayTraceShader, GpuParams._AmbientColor, RenderSettings.ambientSkyColor);*/
            }

            private void UpdateSkyLightAsBlack(CommandBuffer cmd)
            {
                mDDGIVolumeGpu.DDGI_SKYLIGHT_MODE = (int)SkyLightMode.DDGI_SKYLIGHT_MODE_UNSUPPORTED;
                //cmd.SetRayTracingIntParam(mDDGIRayTraceShader, GpuParams.DDGI_SKYLIGHT_MODE, (int)SkyLightMode.DDGI_SKYLIGHT_MODE_UNSUPPORTED);
            }

        #endregion
        
        
        
        #region 工具函数
        
        //变化率估计，异步回读。
        private void VariabilityEstimate(AsyncGPUReadbackRequest request)
        {
            if (request.hasError)
            {
                Debug.LogError("DDGI: 回读Variability Average时发生错误！");
            }
            else if (request.done)
            {
                // 我们的Variability Average使用R32G32_SFLOAT格式，因此CPU端读取float刚好能对应32位
                // 此时readbackPixels的大小应为2，分别对应R和G通道，我们只取R通道即可
                var readbackPixels = request.GetData<float>().ToArray();
                if (readbackPixels.Length > 0)
                {
                    var volumeAverageVariability = readbackPixels[0];

                    if (mClearProbeVariability) mNumVolumeVariabilitySamples = 0;
                    
                    mIsConverged = (mNumVolumeVariabilitySamples++ > mMinimumVariabilitySamples) &&
                                   (volumeAverageVariability < mddgiOverride.probeVariabilityThreshold.value);
                }
                else
                {
                    Debug.LogError("DDGI: 回读Variability Average完成，但意外地返回了空数据，请排查");
                }
            }
        }
        
        public Vector3Int GetNumProbes() => mDDGIVolumeCpu.NumProbes; // For Visualization Pass
        public RenderTargetIdentifier GetProbeData() => mProbeDataId; // For Visualization Pass
        
        /// <summary>
        /// 重置历史渲染目标
        /// </summary>
        /// <param name="cmd"></param>
        private void ResetHistoryInfoIfNeeded(CommandBuffer cmd)
        {
            if (mNeedToResetProbeHistory)
            {
                CoreUtils.SetRenderTarget(cmd, mProbeIrradianceHistoryId, ClearFlag.Color, new Color(0,0,0,0));
                CoreUtils.SetRenderTarget(cmd, mProbeDistanceHistoryId, ClearFlag.Color, new Color(0,0,0,0));
                mNeedToResetProbeHistory = false;
            }
        }
        
        
        
        
        /// <summary>
        /// 从下到上，用于计算DDGI系统中各种纹理尺寸
        /// </summary>
        private static Vector3Int GetDDGIVolumeTextureDimensions(DDGIVolumeCpu volumeDescCpu, DDGIVolumeTextureType type)
        {
            // TODO: Y-UP Probe Volume硬编码，如果要修改Volume轴向需要做分支
            // 在unity中我们使用Y-UP的DDGI Volume
            // 我们的Texture编码原则是：哪一轴朝上，则哪一轴代表arraySize
            var width = volumeDescCpu.NumProbes.x;
            var height = volumeDescCpu.NumProbes.z;
            //但实际上只有平均纹素改了arraySize，其他的都是用的高度作为长度
            var arraySize = volumeDescCpu.NumProbes.y;

            // 由于ProbeData是One By One的，所以无需额外的分支处理，直接返回上面的代码就可以了
            switch (type)
            {
                case DDGIVolumeTextureType.RayData:
                {
                    // 每一行代表一个probe的所有ray info，行数是一个plane上所有probe的个数
                    height = width * height;            //一个平面上的所有probe
                    width = volumeDescCpu.NumRays;      //每一行都是probe上所有的光线数
                    break;
                }
                case DDGIVolumeTextureType.Irradiance:
                {
                    //也是按照平面划分的，长和宽 都 * 上了Irradiance纹素（含边界）
                    width *= PROBE_NUM_IRRADIANCE_TEXELS;   
                    height *= PROBE_NUM_IRRADIANCE_TEXELS;
                    break;    
                }
                case DDGIVolumeTextureType.Distance:
                {
                    //同样按照平面划分的，长和宽 都 * 上了距离纹素（含边界）
                    width *= PROBE_NUM_DISTANCE_TEXELS;
                    height *= PROBE_NUM_DISTANCE_TEXELS;
                    break;
                }
                case DDGIVolumeTextureType.Variability:
                {
                    //变化率：用的是irradiance内部纹素（不含边界）
                    width *= PROBE_NUM_IRRADIANCE_INTERIOR_TEXELS;
                    height *= PROBE_NUM_IRRADIANCE_INTERIOR_TEXELS;
                    break;
                }
                case DDGIVolumeTextureType.VariabilityAverage:
                {
                    //平均变化率：用的是irradiance内部纹素（不含边界）
                    width *= PROBE_NUM_IRRADIANCE_INTERIOR_TEXELS;
                    height *= PROBE_NUM_IRRADIANCE_INTERIOR_TEXELS;
                    
                    //线程组线程排布
                    var NumThreadsInGroup = new Vector3Int(4, 8, 4);
                    //最终尺寸计算
                    var DimensionScale = new Vector3Int(NumThreadsInGroup.x * 4, NumThreadsInGroup.y * 2, NumThreadsInGroup.z);
                    
                    //使用了确保向上取整的技巧？？？
                    width = (width + DimensionScale.x - 1) / DimensionScale.x;
                    height = (height + DimensionScale.y - 1) / DimensionScale.y;
                    arraySize = (arraySize + DimensionScale.z - 1) / DimensionScale.z;
                    
                    break;
                }
            }
            
            //返回最终计算出的2d纹理数组尺寸。
            return new Vector3Int(width, height, arraySize);
        }

        
        
        /// <summary>
        /// 设置GPU常量区
        /// </summary>
        /// <param name="cmd"></param>
        private void PushGpuConstants(CommandBuffer cmd)
        {
            var random = (float)NextDouble(new Random(), 0.0f, 1.0f, 5); // 生成0-1中的随机数，小数保留5位
            var randomVec = Vector3.Normalize(new Vector3(2.0f * random - 1.0f, 2.0f * random - 1.0f, 2.0f * random - 1.0f));
            var randomAngle = random * Mathf.PI * 2.0f;

            // -------------------------------------------------
            // 填充gpu端常量（灯光相关常量在UpdateSceneLights中更新）
            // -------------------------------------------------
            Quaternion rotation;
            if (mddgiOverride.useCustomBounds.value && mCustomGIVolume != null)
            {
                rotation = mCustomGIVolume.transform.rotation;
            }
            else 
            {
                rotation = Quaternion.identity;
               // rotation = Quaternion.Euler(mddgiOverride.probeRotationDegrees.value);
            }
            mDDGIVolumeGpu._ProbeRotation = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w);
            mDDGIVolumeGpu._StartPosition = mDDGIVolumeCpu.Origin - mDDGIVolumeCpu.Extents;
            mDDGIVolumeGpu._RaysPerProbe = mDDGIVolumeCpu.NumRays;
            var a = 2.0f * mDDGIVolumeCpu.Extents;
            var b = new Vector3(mDDGIVolumeCpu.NumProbes.x, mDDGIVolumeCpu.NumProbes.y, mDDGIVolumeCpu.NumProbes.z) - Vector3.one;
            mDDGIVolumeGpu._ProbeSize = new Vector3(a.x / b.x, a.y / b.y, a.z / b.z);
            mDDGIVolumeGpu._MaxRaysPerProbe = mDDGIVolumeCpu.MaxNumRays;
            mDDGIVolumeGpu._ProbeCount = new Vector3Int(mDDGIVolumeCpu.NumProbes.x, mDDGIVolumeCpu.NumProbes.y, mDDGIVolumeCpu.NumProbes.z);
            mDDGIVolumeGpu._NormalBias = 0.25f;
            mDDGIVolumeGpu._RandomVector = randomVec;
            mDDGIVolumeGpu._EnergyPreservation = 0.85f;
            mDDGIVolumeGpu._RandomAngle = randomAngle;
            mDDGIVolumeGpu._HistoryBlendWeight = 0.98f;
            
            mDDGIVolumeGpu._IndirectIntensity = mddgiOverride.indirectIntensity.value;
            mDDGIVolumeGpu._NormalBiasMultiplier = mddgiOverride.normalBiasMultiplier.value;
            mDDGIVolumeGpu._ViewBiasMultiplier = mddgiOverride.viewBiasMultiplier.value;
            mDDGIVolumeGpu.DDGI_PROBE_CLASSIFICATION = mddgiOverride.enableProbeClassification.value ? 1 : 0;
            mDDGIVolumeGpu.DDGI_PROBE_RELOCATION = mddgiOverride.enableProbeRelocation.value ? 1 : 0;
            mDDGIVolumeGpu._ProbeFixedRayBackfaceThreshold = mddgiOverride.probeFixedRayBackfaceThreshold.value;
            mDDGIVolumeGpu._ProbeMinFrontfaceDistance = mddgiOverride.probeMinFrontfaceDistance.value;
            mDDGIVolumeGpu.DDGI_PROBE_REDUCTION = mddgiOverride.enableProbeVariability.value ? 1 : 0;
            
            
            mDDGIVolumeGpu._Pad0 = 0.0f;
            //-------------------------
            
            //-----------------------发送GPU常量-------------------------
            mDDGIVolumeGpuCB.PushGlobal
                (cmd, 
                    mDDGIVolumeGpu, GpuParams.DDGIVolumeGpu);

            // -------------------------------------------------
            // Shader Keywords.设置Shader关键则
            // -------------------------------------------------
            cmd.DisableShaderKeyword(GpuParams.DDGI_SHOW_INDIRECT_ONLY);
            
            cmd.DisableShaderKeyword(GpuParams.DDGI_SHOW_PURE_INDIRECT_RADIANCE);
            if (mddgiOverride.debugIndirect.value)
            {
                switch (mddgiOverride.indirectDebugMode.value)
                {
                    case IndirectDebugMode.FullIndirectRadiance:
                        cmd.EnableShaderKeyword(GpuParams.DDGI_SHOW_INDIRECT_ONLY);
                        break;
                    case IndirectDebugMode.PureIndirectRadiance:
                        cmd.EnableShaderKeyword(GpuParams.DDGI_SHOW_PURE_INDIRECT_RADIANCE);
                        break;
                    default:
                        break;
                }
            }
        }



        // 随机数生成器
        private static double NextDouble(Random ran, double minValue, double maxValue, int decimalPlace)
        {
            double randNum = ran.NextDouble() * (maxValue - minValue) + minValue;
            return Convert.ToDouble(randNum.ToString("f" + decimalPlace));
        }
        
        #endregion
    }


    /// <summary>
    /// 可视化Pass
    /// </summary>
    public sealed class DDGIVisualizePass : ScriptableRenderPass
    {
        private DDGI mddgiOverride; // DDGI 体积设置
        private Shader mVisualizeShader; // 可视化着色器
        private Material mVisualizeMaterial; // 可视化材质
        private Mesh mVisualizeMesh; // 用于可视化的网格
        private DDGIPass mDDGIPass; // 主 DDGI 过程引用
        private ComputeBuffer mArgsBuffer; // 间接绘制参数缓冲区

        /// <summary>
        /// GPU参数
        /// </summary>
        private static class GpuParams
        {
            //关键字
            public static readonly string DDGI_DEBUG_IRRADIANCE = "DDGI_DEBUG_IRRADIANCE";
            public static readonly string DDGI_DEBUG_DISTANCE = "DDGI_DEBUG_DISTANCE";
            public static readonly string DDGI_DEBUG_OFFSET = "DDGI_DEBUG_OFFSET";

            //参数设置
            public static readonly int _ProbeData = Shader.PropertyToID("_ProbeData");
            public static readonly int _ddgiSphere_ObjectToWorld = Shader.PropertyToID("_ddgiSphere_ObjectToWorld");
        }


        /// <summary>
        /// 构造函数（Shader相关）
        /// </summary>
        public DDGIVisualizePass()
        {
            
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques; // 在渲染不透明物体之后执行
            
            mVisualizeShader = Resources.Load<Shader>("Shaders/DDGIVisualize"); // 加载可视化着色器
            
            mVisualizeMaterial = CoreUtils.CreateEngineMaterial(mVisualizeShader); // 创建材质
            
            //造成巨量卡顿的原因
            if(mVisualizeMaterial)
                mVisualizeMaterial.enableInstancing = true; // 启用GPU实例化
            
        }

        /// <summary>
        /// 设置
        /// </summary>
        public void Setup(Mesh debugMesh, DDGIPass ddgiPass)
        {
            mVisualizeMesh = debugMesh; // 设置调试网格
            mDDGIPass = ddgiPass; // 设置主 DDGI 过程引用
        }



        //配置
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {

            base.Configure(cmd, cameraTextureDescriptor);

            mddgiOverride = VolumeManager.instance.stack.GetComponent<DDGI>();
            return;
        }

        /// <summary>
        /// 主要执行逻辑（）
        /// </summary>
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {

            //前置检查
          
           if (mVisualizeMesh == null || mDDGIPass == null) return;
            if (mddgiOverride == null || !mddgiOverride.IsActive()) return;

            var ddgiOverride = VolumeManager.instance.stack.GetComponent<DDGI>();
            if (ddgiOverride == null) return;
            
            
            if (!ddgiOverride.debugProbe.value) return;

            //获取命令缓冲区
            var cmd = CommandBufferPool.Get("DDGI Visualize");

            //获取渲染数据
            var camera = renderingData.cameraData.camera;
            var renderer = renderingData.cameraData.renderer;

            //配置调试模式
            {
                //禁用所有关键字
                cmd.DisableShaderKeyword(GpuParams.DDGI_DEBUG_IRRADIANCE);
                cmd.DisableShaderKeyword(GpuParams.DDGI_DEBUG_DISTANCE);
                cmd.DisableShaderKeyword(GpuParams.DDGI_DEBUG_OFFSET);

                //根据Volume设置来启用关键字
                if (ddgiOverride.debugProbe.value)
                {
                    switch (ddgiOverride.probeDebugMode.value)
                    {
                        case ProbeDebugMode.Irradiance:
                            cmd.EnableShaderKeyword(GpuParams.DDGI_DEBUG_IRRADIANCE);
                            break;
                        case ProbeDebugMode.Distance:
                            cmd.EnableShaderKeyword(GpuParams.DDGI_DEBUG_DISTANCE);
                            break;
                        case ProbeDebugMode.RelocationOffset:
                            cmd.EnableShaderKeyword(GpuParams.DDGI_DEBUG_OFFSET);
                            break;
                    }
                }
            }

            //准备渲染参数
            {
                var matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * mddgiOverride.probeRadius.value);
                //设值变换矩阵
                cmd.SetGlobalMatrix(GpuParams._ddgiSphere_ObjectToWorld, matrix);
                //设置渲染纹理
                cmd.SetGlobalTexture(GpuParams._ProbeData, mDDGIPass.GetProbeData());
            }


            //构建间接绘制参数
            {
                //获取绘制结构，并计算总数量
                var numProbes = mDDGIPass.GetNumProbes();
                var numProbesFlat = numProbes.x * numProbes.y * numProbes.z;


                uint[] args = new uint[5] { 0, 0, 0, 0, 0 };
                args[0] = (uint)mVisualizeMesh.GetIndexCount(0);  // 每个实例的索引数量
                args[1] = (uint)numProbesFlat;                   // 实例总数（探针数量）
                args[2] = (uint)mVisualizeMesh.GetIndexStart(0); // 起始索引位置
                args[3] = (uint)mVisualizeMesh.GetBaseVertex(0); // 基础顶点索引


                //将其传入ComputeBuffer，保存为间接绘制参数
                if (mArgsBuffer != null) { mArgsBuffer.Release(); mArgsBuffer = null; }
                mArgsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
                mArgsBuffer.SetData(args);
            }


            //绘制球
            {
                //设置渲染目标，GPU渲染。
                cmd.SetRenderTarget(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
                // cmd.DrawMeshInstanced限制每Pass最多1024个，所以只能用间接绘
                cmd.DrawMeshInstancedIndirect(mVisualizeMesh, 0, mVisualizeMaterial, 0, mArgsBuffer);

                
            }

            //执行绘制命令然后释放
            
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            
        }

            //释放材质，释放间接绘制缓冲区
            public void Release()
            {
                CoreUtils.Destroy(mVisualizeMaterial);
                if (mArgsBuffer != null) { 
                mArgsBuffer.Release(); 
                mArgsBuffer = null; 
                  }
            }
    
    }






    //创建Pass
    private DDGIPass mDDGIPass;
    private DDGIVisualizePass mDDGIVisualizePass;

    private Mesh mDDGIVisualizeSphere;
    private bool mIsRayTracingSupported;

    public override void Create()
    {
        //非激活状态处理

        if(!isActive)
        {
           //关闭所有pass
            mDDGIPass?.Release();
            mDDGIVisualizePass?.Release();
            return;
        }
        
        //检测系统是否支持光线追踪
        mIsRayTracingSupported = SystemInfo.supportsRayTracing;

        //if (!mIsRayTracingSupported) return;

        //如果尚未初始化就创建初始化过程
        mDDGIPass ??= new DDGIPass();
        mDDGIVisualizePass ??= new DDGIVisualizePass();


        mDDGIVisualizeSphere = Resources.Load<Mesh>("Meshes/DDGIVisualizationSphere");
        //mDDGIVisualizeSphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");


    #if UNITY_EDITOR
        EditorSceneManager.sceneOpened += OnSceneOpened;
    #endif 
         

    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        //跳过预览摄像机
        if (renderingData.cameraData.isPreviewCamera) return;

        //if (!mIsRayTracingSupported) return;

        //将这两个pass加入渲染队列
          renderer.EnqueuePass(mDDGIPass);
          mDDGIVisualizePass.Setup(mDDGIVisualizeSphere, mDDGIPass);
        //mDDGIVisualizePass.Setup(mDDGIVisualizeSphere);
        renderer.EnqueuePass(mDDGIVisualizePass);

    }

    protected override void Dispose(bool disposing)
    {
        
        base.Dispose(disposing);
        //mDDGIPass?.Release();
        //mDDGIVisualizePass?.Release();

        #if UNITY_EDITOR
                EditorSceneManager.sceneOpened -= OnSceneOpened;
        #endif
    }

    public void Reinitialize()
    {
        mDDGIPass.Reinitialize();
    }

    //场景打开时重初始化参数
    private void OnSceneOpened(Scene scene, OpenSceneMode mode)
    {
        Reinitialize();
    }
}
