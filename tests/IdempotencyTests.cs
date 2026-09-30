using CertRotationDrill;
using CertRotationDrill.Tests;

namespace CertRotationDrill.Tests;

public sealed class IdempotencyTests
{
    [Fact]
    public async Task Same_Idempotency_Key_Does_Not_Duplicate_Issue_Or_Acknowledgement()
    {
        using var f = new DrillFactory();
        await f.PostJsonAsync("/api/rotations", new { rotationId = "idem-1", instances = new[] { "a", "b" } });

        await f.PostJsonAsync("/api/rotations/issue", new { }, "same-issue-key");
        await f.PostJsonAsync("/api/rotations/issue", new { }, "same-issue-key");

        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "a" }, "dist-a");
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "a" }, "dist-a");
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "a", loaded = true }, "ack-a");
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "a", loaded = true }, "ack-a");

        var state = await f.GetStateAsync();
        var rotation = state.GetProperty("currentRotation");
        var progress = rotation.GetProperty("instancesProgress").EnumerateArray()
            .Single(p => p.GetProperty("instanceId").GetString() == "a");
        Assert.Equal(1, progress.GetProperty("deliveryAttempts").GetInt32());
        Assert.True(progress.GetProperty("loaded").GetBoolean());

        var issuedCount = state.GetProperty("events").EnumerateArray()
            .Count(e => e.GetProperty("type").GetString() == "CertificateIssued");
        Assert.Equal(1, issuedCount);
    }

    [Fact]
    public async Task Same_Key_With_Different_Payload_Is_Rejected()
    {
        using var f = new DrillFactory();
        await f.PostJsonAsync("/api/rotations", new { rotationId = "idem-2", instances = new[] { "a" } }, "dup-key");
        var ex = await Assert.ThrowsAsync<DrillHttpException>(() =>
            f.PostJsonAsync("/api/rotations", new { rotationId = "different", instances = new[] { "z" } }, "dup-key"));
        Assert.Equal(409, ex.Status);
        Assert.Contains("idempotency_key_mismatch", ex.Body);
    }
}
