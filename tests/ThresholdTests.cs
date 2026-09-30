using CertRotationDrill;
using CertRotationDrill.Tests;

namespace CertRotationDrill.Tests;

public sealed class ThresholdTests
{
    [Fact]
    public async Task Below_Threshold_Never_Activates_Even_After_Restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "drill-crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var f1 = new DrillFactory { DataDirOverride = dir })
            {
                await f1.PostJsonAsync("/api/rotations", new
                {
                    rotationId = "crash-1",
                    instances = new[] { "a", "b" },
                    policyMode = "unanimous"
                });
                await f1.PostJsonAsync("/api/rotations/issue", new { });
                await f1.PostJsonAsync("/api/rotations/distribute", new { instanceId = "a" });
                await f1.PostJsonAsync("/api/rotations/distribute", new { instanceId = "b" });
                await f1.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "a", loaded = true });
                // 进程在“写实例确认”与“汇总激活”之间退出：这里直接销毁宿主
            }

            using var f2 = new DrillFactory { DataDirOverride = dir };
            var restored = await f2.GetStateAsync();
            var rotation = restored.GetProperty("currentRotation");
            Assert.Equal("distributed", rotation.GetProperty("phase").GetString());
            Assert.Equal(1, rotation.GetProperty("loadedCount").GetInt32());
            Assert.False(rotation.GetProperty("thresholdMet").GetBoolean());
            Assert.False(rotation.GetProperty("canActivate").GetBoolean());

            var ex = await Assert.ThrowsAsync<DrillHttpException>(() =>
                f2.PostJsonAsync("/api/rotations/activate", new { }));
            Assert.Equal(400, ex.Status);
            Assert.Contains("threshold_not_met", ex.Body);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Threshold_Met_After_Recovery_Allows_Activation()
    {
        using var f = new DrillFactory();
        await f.PostJsonAsync("/api/rotations", new
        {
            rotationId = "thr-1",
            instances = new[] { "a", "b" },
            policyMode = "unanimous"
        });
        await f.PostJsonAsync("/api/rotations/issue", new { });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "a" });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "b" });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "a", loaded = true });

        var blocked = await f.GetStateAsync();
        Assert.False(blocked.GetProperty("currentRotation").GetProperty("canActivate").GetBoolean());

        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "b", loaded = true });
        await f.PostJsonAsync("/api/rotations/activate", new { });
        var active = await f.GetStateAsync();
        Assert.Equal("active", active.GetProperty("currentRotation").GetProperty("phase").GetString());
    }
}
