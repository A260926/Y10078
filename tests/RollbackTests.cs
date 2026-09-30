using CertRotationDrill;
using CertRotationDrill.Tests;

namespace CertRotationDrill.Tests;

public sealed class RollbackTests
{
    private async Task<string> CompleteOneRotation(DrillFactory f, string id, int lifetimeDays)
    {
        await f.PostJsonAsync("/api/rotations", new
        {
            rotationId = id,
            instances = new[] { "a", "b" },
            policyMode = "unanimous",
            overlapMinutes = 60,
            lifetimeDays
        });
        await f.PostJsonAsync("/api/rotations/issue", new { lifetimeDays });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "a" });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "b" });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "a", loaded = true });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "b", loaded = true });
        await f.PostJsonAsync("/api/rotations/activate", new { });
        await f.AdvanceAsync(60);
        await f.PostJsonAsync("/api/rotations/retire", new { });
        await f.PostJsonAsync("/api/rotations/complete", new { });
        var state = await f.GetStateAsync();
        return state.GetProperty("activeCertificate").GetProperty("serialNumber").GetString()!;
    }

    private async Task StartAndActivateSecond(DrillFactory f, string id, int lifetimeDays = 90)
    {
        await f.PostJsonAsync("/api/rotations", new
        {
            rotationId = id,
            instances = new[] { "a", "b" },
            policyMode = "unanimous",
            overlapMinutes = 120,
            lifetimeDays
        });
        await f.PostJsonAsync("/api/rotations/issue", new { lifetimeDays });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "a" });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "b" });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "a", loaded = true });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "b", loaded = true });
        await f.PostJsonAsync("/api/rotations/activate", new { });
    }

    [Fact]
    public async Task Rollback_Restores_Previous_Still_Valid_Certificate()
    {
        using var f = new DrillFactory();
        var previousSerial = await CompleteOneRotation(f, "rb-old", 90);
        await f.AdvanceAsync(30);
        await StartAndActivateSecond(f, "rb-new");

        await f.PostJsonAsync("/api/rotations/rollback", new { });
        var state = await f.GetStateAsync();
        var rotation = state.GetProperty("rotations").EnumerateArray()
            .Single(r => "rb-new" == r.GetProperty("rotationId").GetString());
        Assert.Equal("completed", rotation.GetProperty("phase").GetString());
        Assert.True(rotation.GetProperty("rolledBack").GetBoolean());
        Assert.Equal(previousSerial, rotation.GetProperty("restoredSerial").GetString());
        Assert.Equal(previousSerial, state.GetProperty("activeSerial").GetString());
    }

    [Fact]
    public async Task Rollback_With_Expired_Previous_Becomes_Manual_Action_Only()
    {
        using var f = new DrillFactory();
        await CompleteOneRotation(f, "exp-old", lifetimeDays: 2);
        await f.AdvanceAsync(60 * 48); // 旧证书有效期 [notBefore, notBefore+2d) 已过
        await StartAndActivateSecond(f, "exp-new");

        await f.PostJsonAsync("/api/rotations/rollback", new { });
        var state = await f.GetStateAsync();
        var rotation = state.GetProperty("rotations").EnumerateArray()
            .Single(r => "exp-new" == r.GetProperty("rotationId").GetString());
        Assert.Equal("manual_action_required", rotation.GetProperty("phase").GetString());
        Assert.True(rotation.GetProperty("needsManualAction").GetBoolean());
        Assert.Contains("人工", rotation.GetProperty("manualReason").GetString());
        Assert.False(rotation.GetProperty("rolledBack").GetBoolean());
    }

    [Fact]
    public async Task First_Ever_Rotation_Rollback_Is_Manual_Action_Not_Fake_Restore()
    {
        using var f = new DrillFactory();
        await f.PostJsonAsync("/api/rotations", new { rotationId = "first-ever", instances = new[] { "a" } });
        await f.PostJsonAsync("/api/rotations/issue", new { });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "a" });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "a", loaded = true });
        await f.PostJsonAsync("/api/rotations/activate", new { });
        await f.PostJsonAsync("/api/rotations/rollback", new { });
        var state = await f.GetStateAsync();
        var rotation = state.GetProperty("rotations").EnumerateArray().Single();
        Assert.Equal("manual_action_required", rotation.GetProperty("phase").GetString());
    }
}
