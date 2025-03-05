using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GMHelper : MonoBehaviour
{
    // 主光源和天花板物体（在Inspector中赋值）
    public Light mainLight;
    public GameObject ceilingObject;

    // GUI控制参数
    private float rotationX = 50f;
    private float lightIntensity = 1f;
    private bool isCeilingOn = true;

    // 窗口尺寸和位置
    private Rect windowRect = new Rect(10, 10, 300, 200);
    private const float MIN_INTENSITY = 0f;
    private const float MAX_INTENSITY = 5f;

    void Start()
    {
        // 自动查找主光源
        // 优先使用手动指定的光源
        if (!mainLight)
        {
            // 智能查找主光源逻辑
            FindMainLight();

            // 仍然找不到时显示警告
            if (!mainLight)
            {
                Debug.LogWarning("未找到方向光！请手动拖拽指定");
                return;
            }
        }

        // 初始化参数
        if (mainLight != null)
        {
            rotationX = mainLight.transform.eulerAngles.x;
            lightIntensity = mainLight.intensity;
        }

        // 初始化天花板状态
        if (ceilingObject != null)
        {
            isCeilingOn = ceilingObject.activeSelf;
        }
    }

    void OnGUI()
    {
        // 将窗口定位到右上角
        windowRect.x = Screen.width - windowRect.width - 10;
        windowRect = GUI.Window(0, windowRect, WindowFunction, "Light Control Panel");
    }

    // 智能查找主光源方法
    void FindMainLight()
    {
        // 方案1：优先查找名称为 "Directional Light" 的物体
        GameObject dirLightGO = GameObject.Find("Directional Light");
        if (dirLightGO) mainLight = dirLightGO.GetComponent<Light>();

        // 方案2：查找所有灯光，选择第一个方向光
        if (!mainLight)
        {
            Light[] allLights = FindObjectsOfType<Light>();
            foreach (Light light in allLights)
            {
                if (light.type == LightType.Directional)
                {
                    mainLight = light;
                    break;
                }
            }
        }

        // 方案3：作为保底，选择第一个启用的灯光
        if (!mainLight)
        {
            Light firstLight = FindObjectOfType<Light>();
            if (firstLight && firstLight.enabled)
            {
                mainLight = firstLight;
                Debug.LogWarning("使用第一个找到的可用灯光: " + mainLight.name);
            }
        }
    }

    void WindowFunction(int windowID)
    {
        GUILayout.BeginVertical();

        // 光源旋转控制
        GUILayout.Label("Light Rotation X:");
        rotationX = GUILayout.HorizontalSlider(rotationX, 0f, 360f);
        rotationX = float.TryParse(GUILayout.TextField(rotationX.ToString("F1")), out float newRot) ? newRot : rotationX;

        // 光源亮度控制
        GUILayout.Label("Light Intensity:");
        lightIntensity = GUILayout.HorizontalSlider(lightIntensity, MIN_INTENSITY, MAX_INTENSITY);
        lightIntensity = float.TryParse(GUILayout.TextField(lightIntensity.ToString("F2")), out float newIntensity) ?
                        Mathf.Clamp(newIntensity, MIN_INTENSITY, MAX_INTENSITY) : lightIntensity;

        // 天花板开关
        isCeilingOn = GUILayout.Toggle(isCeilingOn, "Show Ceiling");

        GUILayout.EndVertical();

        // 实时应用设置
        UpdateSettings();
    }

    void UpdateSettings()
    {
        // 更新光源设置
        if (mainLight != null)
        {
            mainLight.transform.rotation = Quaternion.Euler(rotationX, mainLight.transform.eulerAngles.y, mainLight.transform.eulerAngles.z);
            mainLight.intensity = lightIntensity;
        }

        // 更新天花板状态
        if (ceilingObject != null)
        {
            ceilingObject.SetActive(isCeilingOn);
        }
    }
}
