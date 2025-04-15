#ifndef QUATERNION_HELPER
#define QUATERNION_HELPER


//将向量v用一个四元数q旋转
float3 DDGIQuaternionRotate(float3 v,float4 q)
{
    float3 b = q.xyz;
    float b2 = dot(b,b);
    return (v * (q.w * q.w - b2) + b * (dot(v,b) * 2.0f) +cross(b,v) * (q.w * 2.f));

}

//共轭四元数
float4 DDGIQuaternionConjugate(float4 q)
{
    return float4(-q.x,-q.y,-q.z,q.w);
}





#endif
