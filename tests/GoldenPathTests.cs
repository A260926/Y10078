using CertRotationDrill;
using CertRotationDrill.Tests;

namespace CertRotationDrill.Tests;

public sealed class GoldenPathTests
{
    private static async Task ProposeIssueDistribute(DrillFactory f)
    {
        await f.PostJsonAsync("/api/rotations", new
        {
            rotationId = "gold-1",
            instances = new[] { "n1", "n2" },
            policyMode = "quorum",
            overlapMinutes = 60,
            lifetimeDays = 90
        });
        await f.AdvanceAsync(10);
        await f.PostJsonAsync("/api/rotations/issue", new { });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "n1" });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "n2" });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "n1", loaded = true });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "n2", loaded = true });
    }

    [Fact]
    public async Task Full_Lifecycle_Proposed_To_Completed_With_Overlap_Boundary()
    {
        using var f = new DrillFactory();
        await ProposeIssueDistribute(f);

        var distributed = await f.GetStateAsync();
        Assert.Equal("distributed", distributed.GetProperty("currentRotation").GetProperty("phase").GetString());

        await f.PostJsonAsync("/api/rotations/activate", new { expectedVersion = (int?)null });

        // 半开重叠区间 [激活时刻, 退出时刻)：差 1 分钟仍不能退出旧证书
        await f.AdvanceAsync(59);
        await Assert.ThrowsAsync<DrillHttpException>(() =>
            f.PostJsonAsync("/api/rotations/retire", new { }));
        await f.AdvanceAsync(1);
        await f.PostJsonAsync("/api/rotations/retire", new { });
        await f.PostJsonAsync("/api/rotations/complete", new { });

        var state = await f.GetStateAsync();
        var finished = state.GetProperty("rotations").EnumerateArray().Single();
        Assert.Equal("completed", finished.GetProperty("phase").GetString());
        var phases = state.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("type").GetString()).ToList();
        Assert.Equal("RotationProposed", phases[0]);
        Assert.Contains("RotationActivated", phases);
        Assert.Contains("PreviousCertificateRetired", phases);
        Assert.Equal("RotationCompleted", phases[^1]);
    }

    [Fact]
    public async Task Candidate_Has_Serial_San_Validity_And_KeyId()
    {
        using var f = new DrillFactory();
        await f.PostJsonAsync("/api/rotations", new
        {
            rotationId = "san-1",
            instances = new[] { "alpha", "beta" },
            policyMode = "unanimous"
        });
        await f.PostJsonAsync("/api/rotations/issue", new { lifetimeDays = 30 });
        var state = await f.GetStateAsync();
        var cert = state.GetProperty("currentRotation").GetProperty("candidate");
        Assert.False(string.IsNullOrWhiteSpace(cert.GetProperty("serialNumber").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(cert.GetProperty("keyId").GetString()));
        var sans = cert.GetProperty("subjectAlternativeNames").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Equal(new[] { "alpha.edge.local", "beta.edge.local" }, sans);
        Assert.True(cert.GetProperty("notAfter").GetDateTimeOffset() > cert.GetProperty("notBefore").GetDateTimeOffset());
    }
}
