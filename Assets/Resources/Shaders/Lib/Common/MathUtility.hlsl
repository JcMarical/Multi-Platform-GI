#ifndef MATH_UTILITY
#define MATH_UTILITY


//------------------------------------------------------------------------
// Math Utility
//------------------------------------------------------------------------

float Min(float2 v) { return min(v.x, v.y); }
float Min(float3 v) { return min(Min(v.xy), v.z); }
float Min(float4 v) { return min(Min(v.xyz), v.w);}
float Max(float2 v) { return max(v.x, v.y); }
float Max(float3 v) { return max(Max(v.xy), v.z); }
float Max(float4 v) { return max(Max(v.xyz), v.w);}

float Pow2(float x) { return x * x; }
float Pow3(float x) { return x * x * x; }

// 输出旋转后的方向（弧度制）
// Reference: Unity Shader Graph
float3 RotateAboutAxisInRadians(float3 In, float3 Axis, float Rotation)
{
    float s = sin(Rotation);
    float c = cos(Rotation);
    float one_minus_c = 1.0 - c;

    Axis = normalize(Axis);
    float3x3 rot_mat =
    {   one_minus_c * Axis.x * Axis.x + c, one_minus_c * Axis.x * Axis.y - Axis.z * s, one_minus_c * Axis.z * Axis.x + Axis.y * s,
        one_minus_c * Axis.x * Axis.y + Axis.z * s, one_minus_c * Axis.y * Axis.y + c, one_minus_c * Axis.y * Axis.z - Axis.x * s,
        one_minus_c * Axis.z * Axis.x - Axis.y * s, one_minus_c * Axis.y * Axis.z + Axis.x * s, one_minus_c * Axis.z * Axis.z + c
    };
    return mul(rot_mat,  In);
}

// 输出旋转的矩阵（弧度制）
float3x3 AngleAxis3x3(float angle, float3 axis)
{
    // Rotation with angle (in radians) and axis
    float c, s;
    sincos(angle, s, c);

    float t = 1 - c;
    float x = axis.x;
    float y = axis.y;
    float z = axis.z;

    return float3x3(
        t * x * x + c, t * x * y - s * z, t * x * z + s * y,
        t * x * y + s * z, t * y * y + c, t * y * z - s * x,
        t * x * z - s * y, t * y * z + s * x, t * z * z + c
        );
}





#endif
