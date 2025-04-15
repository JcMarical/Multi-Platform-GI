#ifndef DDGI_PROBE_INDEXING_INCLUDED
#define DDGI_PROBE_INDEXING_INCLUDED

#include "Common/Packing.hlsl"
//------------------------------------------------------------------------
// Probe Indexing Helpers
// 独立于Volume存在，只受坐标轴类型影响
//------------------------------------------------------------------------

// 获得纵轴上每一个Slice内的Probe数量
int DDGIGetProbesPerPlane(int3 probeCounts)
{
    #if 1
        //
        return probeCounts.x * probeCounts.z;
    #elif 0
        //
        return probeCounts.x * probeCounts.y;
    #endif
}

int DDGIGetPlaneIndex(int3 probeCoords)
{
    #if 0
    // Left or Right Z-UP
    return probeCoords.z;
    #else
    // Left or Right Y-UP
    return probeCoords.y;
    #endif
}

int DDGIGetProbeIndexInPlane(int3 probeCoords, int3 probeCounts)
{
    #if 1
    // Left or Right Y-UP
    return probeCoords.x + (probeCounts.x * probeCoords.z);
    #elif 0
    // Left Z-UP
    return probeCoords.y + (probeCounts.y * probeCoords.x);
    #elif 0
    // Right Z-UP
    return probeCoords.x + (probeCounts.x * probeCoords.y);
    #endif
}


//------------------------------------------------------------------------
// Probe Index
// 受当前Volume影响，借助于Helper获得正确的一维索引
//------------------------------------------------------------------------

int DDGIGetProbeIndex(int3 probeCoords)
{
    int probesPerPlane      = DDGIGetProbesPerPlane(_ProbeCount);
    int planeIndex          = DDGIGetPlaneIndex(probeCoords);
    int probeIndexInPlane   = DDGIGetProbeIndexInPlane(probeCoords, _ProbeCount);

    return (planeIndex * probesPerPlane) + probeIndexInPlane;
}

//------------------------------------------------------------------------
// Probe Grid Coordinates
// 受当前Volume影响，借助于Helper获得正确的三维索引
//------------------------------------------------------------------------

int3 DDGIGetProbeCoords(int probeIndex)
{
    int3 probeCoords;

    #if 1
    // Left or Right Y-UP
    probeCoords.x = probeIndex % _ProbeCount.x;
    probeCoords.y = probeIndex / (_ProbeCount.x * _ProbeCount.z);
    probeCoords.z = (probeIndex / _ProbeCount.x) % _ProbeCount.z;
    #elif 0
    // Left Z-UP
    probeCoords.x = (probeIndex / _ProbeCount.y) % _ProbeCount.x;
    probeCoords.y = probeIndex % _ProbeCount.y;
    probeCoords.z = probeIndex / (_ProbeCount.x * _ProbeCount.y);
    #elif 0
    // Right Z-UP
    probeCoords.x = probeIndex % _ProbeCount.x;
    probeCoords.y = (probeIndex / _ProbeCount.x) % _ProbeCount.y;
    probeCoords.z = probeIndex / (_ProbeCount.y * _ProbeCount.x);
    #endif

    return probeCoords;
}




//------------------------------------------------------------------------
// Texture Coordinates纹理坐标
//------------------------------------------------------------------------

//计算单个探针的纹理坐标,根据探针索引计算其在平面中的位置（x, y）和所在平面（z）。
uint3 DDGIGetProbeTexelCoordsOneByOne(int probeIndex)
{
    //获取一个平面的探针数量
    int probesPerPlane  = DDGIGetProbesPerPlane(_ProbeCount);
    
    //获取平面索引
    int planeIndex      = int(probeIndex / probesPerPlane);

#if 1
    // Left or Right Y-UP
    int x = (probeIndex % _ProbeCount.x);
    int y = (probeIndex / _ProbeCount.x) % _ProbeCount.z;
#elif 0
    // Left Z-UP
    int x = (probeIndex % _ProbeCount.y);
    int y = (probeIndex / _ProbeCount.y) % _ProbeCount.x;
#elif 0
    // Right Z-UP
    int x = (probeIndex % _ProbeCount.x);
    int y = (probeIndex / _ProbeCount.x) % _ProbeCount.y;
#endif

    return uint3(x, y, planeIndex);

}

//计算探针的UV坐标。
float3 DDGIGetProbeUV(int probeIndex, float2 octantCoordinates, int numProbeInteriorTexels)
{
    // Get the probe's texel coordinates, assuming one texel per probe
    //获取探针的纹理坐标，假设每个探针都对应一个纹素
    uint3 coords = DDGIGetProbeTexelCoordsOneByOne(probeIndex);

    // Add the border texels to get the total texels per probe
    //添加边界纹理以获得探针的每个纹素。
    float numProbeTexels = (numProbeInteriorTexels + 2.f);

    //计算纹理的总宽和高
    #if 1
    // Left or Right Y-UP
    float textureWidth = numProbeTexels * _ProbeCount.x;
    float textureHeight = numProbeTexels * _ProbeCount.z;
    #elif 0
    // Left Z-UP
    float textureWidth = numProbeTexels * _ProbeCount.y;
    float textureHeight = numProbeTexels * _ProbeCount.x;
    #elif 0
    // Right Z-UP
    float textureWidth = numProbeTexels * _ProbeCount.x;
    float textureHeight = numProbeTexels * _ProbeCount.y;
    #endif

    // Move to the center of the probe and move to the octant texel before normalizing
    //uv计算，x和y乘以单个探针纹素数量，并加上纹素数量的一半（取中间）
    float2 uv   = float2(coords.x * numProbeTexels, coords.y * numProbeTexels) + (numProbeTexels * 0.5f);
    //再+= 八叉坐标*内部纹素的一半
    //再除以纹理总宽，纹理总高。
    uv          += octantCoordinates.xy * ((float)numProbeInteriorTexels * 0.5f);
    uv          /= float2(textureWidth, textureHeight);
    
    return float3(uv, coords.z);
}

float3 DDGIGetProbeUV(int probeIndex, float3 direction, int numProbeInteriorTexels)
{
    //把法线方向解压为？？？八面体坐标
    float2 octantCoordinates = EncodeNormalOctahedron(normalize(direction));
    return DDGIGetProbeUV(probeIndex, octantCoordinates, numProbeInteriorTexels);
}

#endif
