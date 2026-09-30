using System.Text.Json;
using CertRotationDrill;
using CertRotationDrill.Tests;

namespace CertRotationDrill.Tests;

public sealed class FaultInjectionTests
{
    [Fact]
    public async Task Half_Finished_Distribution_Does_Not_Pollute_Next_Rotation()
    {
        using var f = new DrillFactory();

        // 第一次轮换：节点 b 分发失败、整个轮换标记失败
        await f.PostJsonAsync("/api/rotations", new
        {
            rotationId = "dirty-1",
            instances = new[] { "a", "b" },
            policyMode = "unanimous"
        });
        await f.PostJsonAsync("/api/rotations/issue", new { });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "a" });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "a", loaded = true });
        await f.PostJsonAsync("/api/drill/inject/instance-failure", new { instanceId = "b" });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "b" });
        await f.PostJsonAsync("/api/rotations/fail", new { phase = "distributed", reason = "演练：分发无法恢复" });

        // 第二次轮换：必须从干净的 proposed 开始，不能继承失败实例的接收/装载确认
        await f.PostJsonAsync("/api/rotations", new
        {
            rotationId = "clean-1",
            instances = new[] { "a", "b" },
            policyMode = "unanimous"
        });
        await f.PostJsonAsync("/api/rotations/issue", new { });

        var state = await f.GetStateAsync();
        var current = state.GetProperty("currentRotation");
        Assert.Equal("clean-1", current.GetProperty("rotationId").GetString());
        Assert.Equal("issued", current.GetProperty("phase").GetString());
        Assert.Equal(0, current.GetProperty("loadedCount").GetInt32());
        foreach (var p in current.GetProperty("instancesProgress").EnumerateArray())
        {
            Assert.Equal(0, p.GetProperty("deliveryAttempts").GetInt32());
            Assert.False(p.GetProperty("delivered").GetBoolean());
            Assert.False(p.GetProperty("loaded").GetBoolean());
        }
    }

    [Fact]
    public async Task Injected_Duplicate_Serial_Is_Rejected_And_Next_Issue_Succeeds()
    {
        using var f = new DrillFactory();
        await f.PostJsonAsync("/api/rotations", new { rotationId = "dup-1", instances = new[] { "a" } });
        await f.PostJsonAsync("/api/rotations/issue", new { });
        var first = await f.GetStateAsync();
        var firstSerial = first.GetProperty("currentRotation").GetProperty("candidate")
            .GetProperty("serialNumber").GetString();
        await f.PostJsonAsync("/api/rotations/fail", new { phase = "issued", reason = "腾出轮换位置" });

        await f.PostJsonAsync("/api/rotations", new { rotationId = "dup-2", instances = new[] { "a" } });
        await f.PostJsonAsync("/api/drill/inject/duplicate-serial", new { });
        var rejected = await f.PostJsonAsync("/api/rotations/issue", new { });
        var rejectedRotation = rejected.GetProperty("rotations").EnumerateArray()
            .Single(r => "dup-2" == r.GetProperty("rotationId").GetString());
        Assert.Equal("failed", rejectedRotation.GetProperty("phase").GetString());
        Assert.Contains("重复序列号", rejectedRotation.GetProperty("failureReason").GetString());

        await f.PostJsonAsync("/api/rotations", new { rotationId = "dup-3", instances = new[] { "a" } });
        await f.PostJsonAsync("/api/rotations/issue", new { });
        var next = await f.GetStateAsync();
        var nextSerial = next.GetProperty("currentRotation").GetProperty("candidate")
            .GetProperty("serialNumber").GetString();
        Assert.NotEqual(firstSerial, nextSerial);
    }

    [Fact]
    public async Task Failed_Delivery_Can_Recover_Then_Reach_Threshold_And_Finish()
    {
        using var f = new DrillFactory();
        await f.PostJsonAsync("/api/rotations", new
        {
            rotationId = "recover-1",
            instances = new[] { "a", "b" },
            policyMode = "unanimous"
        });
        await f.PostJsonAsync("/api/rotations/issue", new { });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "a" });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "a", loaded = true });

        await f.PostJsonAsync("/api/drill/inject/instance-failure", new { instanceId = "b" });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "b" });
        var mid = await f.GetStateAsync();
        Assert.False(mid.GetProperty("currentRotation").GetProperty("allDelivered").GetBoolean());
        Assert.Equal("issued", mid.GetProperty("currentRotation").GetProperty("phase").GetString());

        await f.PostJsonAsync("/api/drill/inject/clear-failures", new { });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "b" });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "b", loaded = true });
        await f.PostJsonAsync("/api/rotations/activate", new { });
        await f.AdvanceAsync(60);
        await f.PostJsonAsync("/api/rotations/retire", new { });
        await f.PostJsonAsync("/api/rotations/complete", new { });

        var final = await f.GetStateAsync();
        var finished = final.GetProperty("rotations").EnumerateArray()
            .Single(r => "recover-1" == r.GetProperty("rotationId").GetString());
        Assert.Equal("completed", finished.GetProperty("phase").GetString());
    }
}
