using CertRotationDrill;
using CertRotationDrill.Tests;

namespace CertRotationDrill.Tests;

public sealed class DemoScenarioTests
{
    [Fact]
    public async Task Seeded_Demo_Shows_Blocked_Rotation_Then_Recover_Activate_Retire()
    {
        using var f = new DrillFactory { SeedDemo = true };

        var seeded = await f.GetStateAsync();
        var current = seeded.GetProperty("currentRotation");
        Assert.Equal(DemoSeeder.RotationTwo, current.GetProperty("rotationId").GetString());
        Assert.Equal("issued", current.GetProperty("phase").GetString());
        Assert.Equal(1, current.GetProperty("loadedCount").GetInt32());
        Assert.False(current.GetProperty("thresholdMet").GetBoolean());
        Assert.False(current.GetProperty("canActivate").GetBoolean());

        var nodeB = current.GetProperty("instancesProgress").EnumerateArray()
            .Single(p => p.GetProperty("instanceId").GetString() == DemoSeeder.NodeB);
        Assert.Equal(1, nodeB.GetProperty("deliveryAttempts").GetInt32());
        Assert.False(nodeB.GetProperty("delivered").GetBoolean());
        Assert.NotNull(nodeB.GetProperty("lastDeliveryError").GetString());

        var completedRotations = seeded.GetProperty("rotations").EnumerateArray()
            .Where(r => r.GetProperty("phase").GetString() == "completed").ToList();
        Assert.Single(completedRotations);
        Assert.Equal(DemoSeeder.RotationOne, completedRotations[0].GetProperty("rotationId").GetString());
        Assert.NotNull(seeded.GetProperty("activeCertificate").GetProperty("serialNumber").GetString());

        // 恢复后达到阈值，再安全完成旧证书退出
        await f.PostJsonAsync("/api/drill/inject/clear-failures", new { });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = DemoSeeder.NodeB });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = DemoSeeder.NodeB, loaded = true });
        await f.PostJsonAsync("/api/rotations/activate", new { });
        var active = await f.GetStateAsync();
        Assert.Equal("active", active.GetProperty("currentRotation").GetProperty("phase").GetString());

        await f.AdvanceAsync(60);
        await f.PostJsonAsync("/api/rotations/retire", new { });
        await f.PostJsonAsync("/api/rotations/complete", new { });
        var done = await f.GetStateAsync();
        var finished = done.GetProperty("rotations").EnumerateArray()
            .Single(r => DemoSeeder.RotationTwo == r.GetProperty("rotationId").GetString());
        Assert.Equal("completed", finished.GetProperty("phase").GetString());
        Assert.False(finished.GetProperty("needsManualAction").GetBoolean());
    }

    [Fact]
    public async Task Seeded_Events_Restore_Identically_After_Restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "drill-seed-restart-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var f1 = new DrillFactory { DataDirOverride = dir, SeedDemo = true })
            {
                var s = await f1.GetStateAsync();
                Assert.Equal(1, s.GetProperty("currentRotation").GetProperty("loadedCount").GetInt32());
            }
            using (var f2 = new DrillFactory { DataDirOverride = dir, SeedDemo = true })
            {
                var s = await f2.GetStateAsync();
                Assert.Equal(DemoSeeder.RotationTwo, s.GetProperty("currentRotation").GetProperty("rotationId").GetString());
                Assert.Equal(1, s.GetProperty("currentRotation").GetProperty("loadedCount").GetInt32());
                Assert.False(s.GetProperty("currentRotation").GetProperty("canActivate").GetBoolean());
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
