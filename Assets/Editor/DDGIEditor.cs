using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;





[CustomEditor(typeof(DDGI))]
public sealed class DDGIEditor : VolumeComponentEditor
{
    //启用ddgi
    private SerializedDataParameter mEnableDDGI;
    //间接光强度
    private SerializedDataParameter mIndirectIntensity;
    
    //偏移、旋转
    private SerializedDataParameter mNormalBiasMultiplier;
    private SerializedDataParameter mViewBiasMultiplier;
    private SerializedDataParameter mProbeRotationDegrees;
    //Probe调试
    private SerializedDataParameter mDebugProbe;
    private SerializedDataParameter mProbeDebugMode;
    private SerializedDataParameter mProbeRadius;

    //Debug
    private SerializedDataParameter mDebugIndirect;             //测试间接光
    private SerializedDataParameter mIndirectDebugMode;         //间接光测试模式
    

    private SerializedDataParameter mEnableProbeRelocation;             //探针重定位
    private SerializedDataParameter mProbeMinFrontfaceDistance;         //最近距离
    
    private SerializedDataParameter mEnableProbeClassification;         //开启分类
    private SerializedDataParameter mProbeFixedRayBackfaceThreshold;    //固定背面光线数量
    
    private SerializedDataParameter mEnableProbeVariability;            //变异性测试功能
    private SerializedDataParameter mProbeVariabilityThreshold;         //变异性阈值
    
    //DDGI 边界框设置
    private SerializedDataParameter mUseCustomBounds;
    private SerializedDataParameter mProbeCountX;
    private SerializedDataParameter mProbeCountY;
    private SerializedDataParameter mProbeCountZ;
    private SerializedDataParameter mRaysPerProbe;                      //每个探针的光线数量

    // 重写 OnInspectorGUI 方法


    public override void OnEnable()
    {


        var o = new PropertyFetcher<DDGI>(serializedObject);

        mEnableDDGI = Unpack(o.Find(x => x.enableDDGI));
        mIndirectIntensity = Unpack(o.Find(x => x.indirectIntensity));
        mNormalBiasMultiplier = Unpack(o.Find(x => x.normalBiasMultiplier));
        mViewBiasMultiplier = Unpack(o.Find(x => x.viewBiasMultiplier));
        mProbeRotationDegrees = Unpack(o.Find(x => x.probeRotationDegrees));
        
        
        mDebugProbe = Unpack(o.Find(x => x.debugProbe));
        mProbeDebugMode = Unpack(o.Find(x => x.probeDebugMode));
        mProbeRadius = Unpack(o.Find(x => x.probeRadius));


        mDebugIndirect = Unpack(o.Find(x => x.debugIndirect));
        mIndirectDebugMode = Unpack(o.Find(x => x.indirectDebugMode));
        mEnableProbeRelocation = Unpack(o.Find(x => x.enableProbeRelocation));
        mProbeMinFrontfaceDistance = Unpack(o.Find(x => x.probeMinFrontfaceDistance));
        mEnableProbeClassification = Unpack(o.Find(x => x.enableProbeClassification));
        mProbeFixedRayBackfaceThreshold = Unpack(o.Find(x => x.probeFixedRayBackfaceThreshold));
        mEnableProbeVariability = Unpack(o.Find(x => x.enableProbeVariability));
        mProbeVariabilityThreshold = Unpack(o.Find(x => x.probeVariabilityThreshold));
        
        
        //边界框和Probe设置
        mUseCustomBounds = Unpack(o.Find(x => x.useCustomBounds));
        mProbeCountX = Unpack(o.Find(x => x.probeCountX));
        mProbeCountY = Unpack(o.Find(x => x.probeCountY));
        mProbeCountZ = Unpack(o.Find(x => x.probeCountZ));
        mRaysPerProbe = Unpack(o.Find(x => x.raysPerProbe));

    }
    public override void OnInspectorGUI()
    {
        //DX12 硬件光线追踪提示
        if (!SystemInfo.supportsRayTracing)
        {
            EditorGUILayout.HelpBox("DDGI依赖硬件光线跟踪，只在DX12、Playstation 5以及Xbox Series X上受支持", MessageType.Warning);
            return;
        }
        
        PropertyField(mEnableDDGI);
        
        #region Dynamic Lighting Settings 动态光照设置

        EditorGUILayout.LabelField("Dynamic Lighting Settings 动态光照设置");
        
        PropertyField(mIndirectIntensity);
        PropertyField(mNormalBiasMultiplier);
        PropertyField(mViewBiasMultiplier);
        PropertyField(mProbeRotationDegrees);
        EditorGUILayout.Space(5.0f);

        #endregion
        
        #region Probe Feature Settings 探针Feature设置

        EditorGUILayout.LabelField("Probe Feature Settings 探针Feature设置");
        
        PropertyField(mEnableProbeRelocation);
        if (mEnableProbeRelocation.value.boolValue)
        {
            PropertyField(mProbeMinFrontfaceDistance);
        }
        EditorGUILayout.Space(3.0f);
        
        PropertyField(mEnableProbeClassification);
        EditorGUILayout.Space(3.0f);
        
        if (mEnableProbeRelocation.value.boolValue || mEnableProbeClassification.value.boolValue)
        {
            PropertyField(mProbeFixedRayBackfaceThreshold);
            EditorGUILayout.Space(3.0f);
        }
        
        PropertyField(mEnableProbeVariability);
        if (mEnableProbeVariability.value.boolValue)
        {
            PropertyField(mProbeVariabilityThreshold);
            EditorGUILayout.HelpBox("Probe Variability当前属于实验性功能，且不支持自发光物体，请酌情考虑使用", MessageType.Info);
        }
        EditorGUILayout.Space(5.0f);

        #endregion
        
        
        
        
        #region Debug Options
        
        EditorGUILayout.LabelField("Debug Options 调试选项");
        
        PropertyField(mDebugProbe);
        if (mDebugProbe.value.boolValue)
        {
            EditorGUI.indentLevel++;
            PropertyField(mProbeDebugMode);
            PropertyField(mProbeRadius);
            EditorGUI.indentLevel--;
        }
        PropertyField(mDebugIndirect);
        if (mDebugIndirect.value.boolValue)
        {
            EditorGUI.indentLevel++;
            PropertyField(mIndirectDebugMode);
            EditorGUI.indentLevel--;
        }
        EditorGUILayout.Space(5.0f);


        #endregion


        #region Reinitialize Settings--DDGI边界框设置

        EditorGUILayout.LabelField("重新初始化（DDGI边界和内部探针数量与光线）");
        
        PropertyField(mUseCustomBounds);
        if (mUseCustomBounds.value.boolValue)
        {
            var customBounds = FindFirstObjectByType<DDGICustomBounds>();
            if (customBounds == null)
            {
                EditorGUILayout.HelpBox("在当前场景中未检测到有效的DDGI Custom Bounds，您可能从未创建过它，或者将其设置为了禁用状态；" +
                                        "要创建它，你可以在Hierarchy中右击->Light->DDGI Custom Bounds",
                    MessageType.Warning);
            }
        }

        PropertyField(mProbeCountX);
        PropertyField(mProbeCountY);
        PropertyField(mProbeCountZ);
        PropertyField(mRaysPerProbe);

        #endregion

        if (GUILayout.Button("Refresh DDGI Settings 刷新DDGI重初始化设置"))
        {
            var urpAsset = GraphicsSettings.renderPipelineAsset;
            
            if (urpAsset != null && urpAsset is UniversalRenderPipelineAsset)
            {
                Type urpAssetType = urpAsset.GetType();
                FieldInfo scriptableRendererDataListField = urpAssetType.GetField("m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic);
                    
                if (scriptableRendererDataListField != null)
                {
                    ScriptableRendererData[] rendererDataList = scriptableRendererDataListField.GetValue(urpAsset) as ScriptableRendererData[];

                    if (rendererDataList == null) return;
                        
                    foreach (var rendererData in rendererDataList)
                    {
                        var ddgiFeature = (DDGIFeature)rendererData.rendererFeatures.Find(x => x.GetType() == typeof(DDGIFeature));
                        if(ddgiFeature != null) ddgiFeature.Reinitialize();
                    }
                }
            }
        }

    }


}
