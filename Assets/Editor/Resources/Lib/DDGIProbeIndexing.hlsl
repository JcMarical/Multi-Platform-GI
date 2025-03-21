#ifndef DDGI_PROBE_INDEXING_INCLUDED
#define DDGI_PROBE_INDEXING_INCLUDED


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





//------------------------------------------------------------------------
// Texture Coordinates纹理坐标
//------------------------------------------------------------------------

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


#endif
