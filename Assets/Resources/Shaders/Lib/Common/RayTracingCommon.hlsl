#ifndef RAY_TRACING_COMMON
#define RAY_TRACING_COMMON

// 插值宏，用于根据重心坐标插值法线、位置等属性
#define INTERPOLATE_RAYTRACING_ATTRIBUTE(A0, A1, A2, BARYCENTRIC_COORDINATES) (A0 * BARYCENTRIC_COORDINATES.x + A1 * BARYCENTRIC_COORDINATES.y + A2 * BARYCENTRIC_COORDINATES.z)

// 包含必要的头文件
#include "UnityRaytracingMeshUtils.cginc" // Unity光线追踪网格实用工具
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl" // 通用着色器库
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl" // URP核心库
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl" // URP光照库

RaytracingAccelerationStructure _AccelerationStructure; // 定义光线追踪加速结构

// 相交顶点属性结构体
struct IntersectionVertex
{
    float3 positionOS; // 物体空间中的位置
    float3 normalOS;   // 物体空间中的法线
    float4 tangentOS;  // 物体空间中的切线
    float2 uv;         // 纹理坐标
};

// 重心坐标插值结构体
struct AttributeData
{
    float2 barycentrics; // 重心坐标
};

// 生成相机光线
inline void GenerateCameraRay(out float3 origin, out float3 direction)
{
    float2 xy = DispatchRaysIndex().xy + 0.5f; // 将光线发射点设置在像素中心
    float2 screenPos = xy / DispatchRaysDimensions().xy * 2.0f - 1.0f; // 将坐标范围归一化到(-1, 1)
    screenPos.y *= -1; // 反转y轴以适应坐标系统
    
    // 将像素坐标反投影为光线
    float4 world  = mul(_InvCameraViewProj, float4(screenPos, 0, 1)); // 反投影到世界空间
    world.xyz     /= world.w; // 进行齐次坐标归一化
    origin        = _WorldSpaceCameraPos.xyz; // 设置光线起点为相机位置
    direction     = normalize(world.xyz - origin); // 计算光线方向并归一化
}

// 获取相交顶点属性
void FetchIntersectionVertex(uint vertexIndex, out IntersectionVertex outVertex)
{
    // 从顶点索引中获取几何属性
    outVertex.positionOS    = UnityRayTracingFetchVertexAttribute3(vertexIndex, kVertexAttributePosition); // 获取位置
    outVertex.normalOS      = UnityRayTracingFetchVertexAttribute3(vertexIndex, kVertexAttributeNormal); // 获取法线
    outVertex.tangentOS     = UnityRayTracingFetchVertexAttribute4(vertexIndex, kVertexAttributeTangent); // 获取切线
    outVertex.uv            = UnityRayTracingFetchVertexAttribute2(vertexIndex, kVertexAttributeTexCoord0); // 获取纹理坐标
}

// 获取当前交点的几何属性
void GetCurrentIntersectionVertex(AttributeData attributeData, out IntersectionVertex outVertex)
{
    // 获取当前三角形的索引
    uint3 triangleIndices = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());

    // 获取三个顶点的属性
    IntersectionVertex v0, v1, v2;
    FetchIntersectionVertex(triangleIndices.x, v0); // 获取第一个顶点
    FetchIntersectionVertex(triangleIndices.y, v1); // 获取第二个顶点
    FetchIntersectionVertex(triangleIndices.z, v2); // 获取第三个顶点

    // 计算重心坐标
    float3 barycentricCoordinates = float3(1.0 - attributeData.barycentrics.x - attributeData.barycentrics.y, attributeData.barycentrics.x, attributeData.barycentrics.y);
    // 通过重心坐标插值计算顶点属性
    float3 positionOS   = INTERPOLATE_RAYTRACING_ATTRIBUTE(v0.positionOS, v1.positionOS, v2.positionOS, barycentricCoordinates);
    float3 normalOS     = INTERPOLATE_RAYTRACING_ATTRIBUTE(v0.normalOS, v1.normalOS, v2.normalOS, barycentricCoordinates);
    float4 tangentOS    = INTERPOLATE_RAYTRACING_ATTRIBUTE(v0.tangentOS, v1.tangentOS, v2.tangentOS, barycentricCoordinates);
    float2 uv           = INTERPOLATE_RAYTRACING_ATTRIBUTE(v0.uv, v1.uv, v2.uv, barycentricCoordinates);

    // 将计算结果赋值给输出顶点属性
    outVertex.positionOS    = positionOS;
    outVertex.normalOS      = normalOS;
    outVertex.tangentOS     = tangentOS;
    outVertex.uv            = uv;
}

// 追踪阴影光线的方法（只是留档，目前不支持在Unity中使用，因为Closest Hit Shader中不允许使用RayTraceInline）
// 相关讨论：https://forum.unity.com/threads/raytracing-rayquery-and-traceinline.961075/
bool TraceShadowRay(RayDesc rayDesc)
{
    RayQuery<RAY_FLAG_CULL_NON_OPAQUE | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> q;

    q.TraceRayInline(_AccelerationStructure, RAY_FLAG_NONE, 0xFF, rayDesc);
    while (q.Proceed())
    {
        switch (q.CandidateType())
        {
        case CANDIDATE_NON_OPAQUE_TRIANGLE:
            {
                q.CommitNonOpaqueTriangleHit();
                break;
            }
        }
    }
    return q.CommittedStatus() != COMMITTED_TRIANGLE_HIT;
}

// 追踪方向光源的阴影光线
bool TraceDirectionalShadowRay(Light light, float3 worldPos)
{
    RayDesc rayDesc; // 定义光线描述
    rayDesc.Origin      = worldPos; // 设置光线起点
    rayDesc.Direction   = light.direction; // 设置光线方向
    rayDesc.TMin        = 1e-1f; // 设置光线的最小距离
    rayDesc.TMax        = FLT_MAX; // 设置光线的最大距离

    return TraceShadowRay(rayDesc); // 调用追踪阴影光线的函数
}

// 追踪点光源的阴影光线
bool TracePunctualShadowRay(uint i, float3 worldPos)
{
    // 参考: RealtimeLights.hlsl
    #if USE_FORWARD_PLUS
    int lightIndex = i; // 如果使用前向加法，直接使用索引
    #else
    int lightIndex = GetPerObjectLightIndex(i); // 否则获取每个对象的光源索引
    #endif

    Light light = GetAdditionalPerObjectLight(i, worldPos); // 获取光源信息
    
    RayDesc rayDesc; // 定义光线描述
    rayDesc.Origin      = worldPos; // 设置光线起点
    rayDesc.Direction   = light.direction; // 设置光线方向
    rayDesc.TMin        = 1e-1f; // 设置光线的最小距离

    float tMax = 0.0f; // 初始化最大距离
    #if USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA
    tMax = length(_AdditionalLightsBuffer[lightIndex].position - worldPos); // 从结构化缓冲区获取光源位置
    #else
    tMax = length(_AdditionalLightsPosition[lightIndex] - worldPos); // 从普通数组获取光源位置
    #endif
    rayDesc.TMax = tMax; // 设置光线的最大距离

    return TraceShadowRay(rayDesc); // 调用追踪阴影光线的函数
}



#endif
