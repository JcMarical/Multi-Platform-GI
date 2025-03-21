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


    //Probe调试
    private SerializedDataParameter mDebugProbe;
    private SerializedDataParameter mProbeDebugMode;
    private SerializedDataParameter mProbeRadius;

    //DDGI 边界框设置
    private SerializedDataParameter mUseCustomBounds;
    private SerializedDataParameter mProbeCountX;
    private SerializedDataParameter mProbeCountY;
    private SerializedDataParameter mProbeCountZ;
    private SerializedDataParameter mRaysPerProbe;

    // 重写 OnInspectorGUI 方法


    public override void OnEnable()
    {


        var o = new PropertyFetcher<DDGI>(serializedObject);

        mDebugProbe = Unpack(o.Find(x => x.debugProbe));
        mProbeDebugMode = Unpack(o.Find(x => x.probeDebugMode));
        mProbeRadius = Unpack(o.Find(x => x.probeRadius));


        //边界框和Probe设置
        mUseCustomBounds = Unpack(o.Find(x => x.useCustomBounds));
        mProbeCountX = Unpack(o.Find(x => x.probeCountX));
        mProbeCountY = Unpack(o.Find(x => x.probeCountY));
        mProbeCountZ = Unpack(o.Find(x => x.probeCountZ));
        mRaysPerProbe = Unpack(o.Find(x => x.raysPerProbe));

    }
    public override void OnInspectorGUI()
    {

        #region Debug Options

        PropertyField(mDebugProbe);
        if (mDebugProbe.value.boolValue)
        {
            EditorGUI.indentLevel++;
            PropertyField(mProbeDebugMode);
            PropertyField(mProbeRadius);
            EditorGUI.indentLevel--;
        }


        #endregion


        #region Reinitialize Settings--DDGI边界框设置

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


    }


}
