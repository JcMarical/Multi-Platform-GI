using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine;
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

        struct DDGIVolumeCpu
        {
            public Vector3 Origin;
            public Vector3 Extents;
            public Vector3Int NumProbes;
            public int MaxNumRays;
            public int NumRays;
        }
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


        private ConstantBuffer<DDGIVolumeGpu> mDDGIVolumeGpuCB;

        // 探针重定位
        private RenderTexture mProbeData;                           // For Probe Relocation
        private RenderTargetIdentifier mProbeDataId;

        /// <summary>
        /// GPU参数静态类，用于集中管理所有与GPU相关的参数和标识符
        /// </summary>
        private static class GpuParams
        {
            // 探针追踪和更新相关
            public static readonly string RayGenShaderName = "DDGI_RayGen";  // 光线追踪着色器名称
            public static readonly int RayBuffer = Shader.PropertyToID("RayBuffer");  // 光线数据缓冲区
            public static readonly int DirectionalLightBuffer = Shader.PropertyToID("DirectionalLightBuffer");  // 方向光源缓冲区
            public static readonly int PunctualLightBuffer = Shader.PropertyToID("PunctualLightBuffer");  // 点光源/聚光灯缓冲区
            public static readonly int DDGIVolumeGpu = Shader.PropertyToID("DDGIVolumeGpu");  // DDGI体积参数常量缓冲区

            // 探针数据存储
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


        #region 核心函数与渲染循环
        public DDGIPass()
        {
            //设置常量缓冲
            mDDGIVolumeGpuCB = new ConstantBuffer<DDGIVolumeGpu>();
        }
        private void Initialize()
        {
            // ---------------------------------------
            // Initialize cpu-side volume parameters
            // ---------------------------------------
            var sceneBoundingBox = GenerateSceneMeshBounds();
            if (sceneBoundingBox.extents == Vector3.zero) return;   // 包围盒零值表示场景没有任何几何体，没有GI意义
            mDDGIVolumeCpu.Origin = sceneBoundingBox.center;
            mDDGIVolumeCpu.Extents = 1.1f * sceneBoundingBox.extents;
            mDDGIVolumeCpu.NumProbes = new Vector3Int(mddgiOverride.probeCountX.value, mddgiOverride.probeCountY.value, mddgiOverride.probeCountZ.value);
            mDDGIVolumeCpu.NumRays = mddgiOverride.raysPerProbe.value;
            mDDGIVolumeCpu.MaxNumRays = 512;
        }

        //参数重新初始化
        public void Reinitialize()
        {

        }


        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            base.OnCameraSetup(cmd, ref renderingData);

            mddgiOverride = VolumeManager.instance.stack.GetComponent<DDGI>();

            Initialize();
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


        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            /*
            var cmd = CommandBufferPool.Get("DDGI Pass");
            var camera = renderingData.cameraData.camera;

            // 注：该函数每调用一次，随机数都会更新，进而导致_RandomVector和_RandomAngle发生改变
            // 如果不将更新随机数的逻辑抽离出来，那么该函数每帧只允许调用一次！
            PushGpuConstants(cmd);


            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            */
            }



        public void Release()
        {

        }

        #endregion
        #region 工具函数
        public Vector3Int GetNumProbes() => mDDGIVolumeCpu.NumProbes; // For Visualization Pass
        public RenderTargetIdentifier GetProbeData() => mProbeDataId; // For Visualization Pass

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
            { rotation = mCustomGIVolume.transform.rotation; }
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
            /*
            mDDGIVolumeGpu._IndirectIntensity = mddgiOverride.indirectIntensity.value;
            mDDGIVolumeGpu._NormalBiasMultiplier = mddgiOverride.normalBiasMultiplier.value;
            mDDGIVolumeGpu._ViewBiasMultiplier = mddgiOverride.viewBiasMultiplier.value;
            mDDGIVolumeGpu.DDGI_PROBE_CLASSIFICATION = mddgiOverride.enableProbeClassification.value ? 1 : 0;
            mDDGIVolumeGpu.DDGI_PROBE_RELOCATION = mddgiOverride.enableProbeRelocation.value ? 1 : 0;
            mDDGIVolumeGpu._ProbeFixedRayBackfaceThreshold = mddgiOverride.probeFixedRayBackfaceThreshold.value;
            mDDGIVolumeGpu._ProbeMinFrontfaceDistance = mddgiOverride.probeMinFrontfaceDistance.value;
            mDDGIVolumeGpu.DDGI_PROBE_REDUCTION = mddgiOverride.enableProbeVariability.value ? 1 : 0;
            
            */
            mDDGIVolumeGpu._Pad0 = 0.0f;
            mDDGIVolumeGpuCB.PushGlobal(cmd, mDDGIVolumeGpu, GpuParams.DDGIVolumeGpu);

            // -------------------------------------------------
            // Shader Keywords.
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

        //临时测试用
        public void Setup(Mesh debugMesh)
        {
            mVisualizeMesh = debugMesh; // 设置调试网格
        }

        //配置
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            base.Configure(cmd, cameraTextureDescriptor);

            mddgiOverride = VolumeManager.instance.stack.GetComponent<DDGI>();
        }

        /// <summary>
        /// 主要执行逻辑（）
        /// </summary>
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            //前置检查
            /*
           if (mVisualizeMesh == null || mDDGIPass == null) return;
            if (mddgiOverride == null || !mddgiOverride.IsActive()) return;
            */
            var ddgiOverride = VolumeManager.instance.stack.GetComponent<DDGI>();
            //if (ddgiOverride == null) return;
            

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
                Debug.Log("渲染测试");
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
                if (mArgsBuffer != null) { mArgsBuffer.Release(); mArgsBuffer = null; }
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
        return;
        if(!isActive)
        {
           //关闭所有pass
           // mDDGIPass?.Release();
           mDDGIVisualizePass?.Release();
            return;
        }
        
        //检测系统是否支持光线追踪
        mIsRayTracingSupported = SystemInfo.supportsRayTracing;

        //if (!mIsRayTracingSupported) return;

        //如果尚未初始化就创建初始化过程
       // mDDGIPass ??= new DDGIPass();
        mDDGIVisualizePass ??= new DDGIVisualizePass();


        //mDDGIVisualizeSphere = Resources.Load<Meshed/mDDGIVisualizeSphere>
        mDDGIVisualizeSphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");


    #if UNITY_EDITOR
        EditorSceneManager.sceneOpened += OnSceneOpened;
    #endif 
         

    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        //跳过预览摄像机
        //if (renderingData.cameraData.isPreviewCamera) return;

        //if (!mIsRayTracingSupported) return;

        //将这两个pass加入渲染队列
        //  renderer.EnqueuePass(mDDGIPass);
        //  mDDGIVisualizePass.Setup(mDDGIVisualizeSphere, mDDGIPass);
        //mDDGIVisualizePass.Setup(mDDGIVisualizeSphere);
        //renderer.EnqueuePass(mDDGIVisualizePass);

    }

    protected override void Dispose(bool disposing)
    {
        return;
        base.Dispose(disposing);

       // mDDGIPass?.Release();
       // mDDGIVisualizePass?.Release();

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
