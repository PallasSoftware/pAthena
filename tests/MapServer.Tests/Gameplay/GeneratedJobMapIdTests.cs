using Athena.Net.MapServer.Generated.Jobs;

namespace Athena.Net.MapServer.Tests.Gameplay;

// GeneratedJobMapId is parsed from pinned pc_jobid2mapid/pc_mapid2jobid; GeneratedJobEquipClass was derived separately from the
// same functions. Each must agree with the other for every job both know.
public sealed class GeneratedJobMapIdTests
{
    [Fact]
    public void MapIds_AgreeWithTheEquipClassTable_AndRoundTripThroughPcMapid2jobid()
    {
        var checkedJobs = 0;
        foreach (var job in Enum.GetValues<JobClass>())
        {
            if (!GeneratedJobEquipClass.TryGet(job, out var equipClass)) continue;
            var mapId = GeneratedJobMapId.JobIdToMapId((ushort)job);
            Assert.True(mapId >= 0, $"{job} has an equip class but no MAPID");
            Assert.Equal(equipClass.FirstMask, (byte)((ulong)mapId & GeneratedJobMapId.FirstMask));
            Assert.Equal((equipClass.Tier & 1) != 0, ((ulong)mapId & GeneratedJobMapId.Upper) != 0);
            Assert.Equal((equipClass.Tier & 2) != 0, ((ulong)mapId & GeneratedJobMapId.Baby) != 0);
            // pc_mapid2jobid inverts pc_jobid2mapid for one of the two sexes (Bard/Dancer-style pairs share a MAPID).
            Assert.Contains((int)job, new[] { GeneratedJobMapId.MapIdToJobId((ulong)mapId, 0), GeneratedJobMapId.MapIdToJobId((ulong)mapId, 1) });
            checkedJobs++;
        }
        Assert.True(checkedJobs > 100, $"only {checkedJobs} jobs checked");
    }
}
