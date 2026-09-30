using System.Text.Json;
using CertRotationDrill;
using CertRotationDrill.Tests;

namespace CertRotationDrill.Tests;

public sealed class ConflictAndSecretTests
{
    [Fact]
    public async Task Stale_Version_Is_Rejected_With_Server_State()
    {
        using var f = new DrillFactory();
        await f.PostJsonAsync("/api/rotations", new
        {
            rotationId = "ver-1",
            instances = new[] { "a", "b" },
            policyMode = "unanimous"
        });
        await f.PostJsonAsync("/api/rotations/issue", new { });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "a" });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "b" });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "a", loaded = true });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "b", loaded = true });

        var before = await f.GetStateAsync();
        var staleVersion = before.GetProperty("currentRotation").GetProperty("version").GetInt32();

        // 操作者 A 先推进成功；操作者 B 拿着旧版本号推进必须被拒绝
        await f.PostJsonAsync("/api/rotations/activate", new { expectedVersion = staleVersion });
        var ex = await Assert.ThrowsAsync<DrillHttpException>(() =>
            f.PostJsonAsync("/api/rotations/retire", new { expectedVersion = staleVersion }));
        Assert.Equal(409, ex.Status);
        using var doc = JsonDocument.Parse(ex.Body);
        Assert.Equal("version_conflict", doc.RootElement.GetProperty("code").GetString());
        var serverPhase = doc.RootElement.GetProperty("serverState")
            .GetProperty("currentRotation").GetProperty("phase").GetString();
        Assert.Equal("active", serverPhase);
    }

    [Fact]
    public async Task Private_Key_Never_Appears_In_Api_Pages_Or_Database_Plaintext()
    {
        using var f = new DrillFactory();
        await f.PostJsonAsync("/api/rotations", new { rotationId = "sec-1", instances = new[] { "a", "b" } });
        var issueBody = await f.PostJsonAsync("/api/rotations/issue", new { });
        await f.PostJsonAsync("/api/rotations/distribute", new { instanceId = "a" });
        await f.PostJsonAsync("/api/rotations/acknowledge", new { instanceId = "a", loaded = true });

        var stateText = await f.ApiClient.GetStringAsync("/api/state");
        var page = await f.ApiClient.GetStringAsync("/");

        Assert.DoesNotContain("FAKE-PRIVATE-KEY", stateText);
        Assert.DoesNotContain("FAKE-PRIVATE-KEY", page);
        Assert.DoesNotContain("FAKE-PRIVATE-KEY", issueBody.ToString());

        Directory.CreateDirectory(f.DataDir);
        var dbFiles = Directory.Exists(f.DataDir)
            ? Directory.GetFiles(f.DataDir, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        foreach (var file in dbFiles.Where(p => p.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.DoesNotContain("FAKE-PRIVATE-KEY"u8.ToArray(), bytes);
        }
    }

    [Fact]
    public async Task Page_Contains_Drill_Title()
    {
        using var f = new DrillFactory();
        var page = await f.ApiClient.GetStringAsync("/");
        Assert.Contains("证书轮换演练器", page);
    }

    [Fact]
    public async Task Missing_Idempotency_Key_Is_Bad_Request()
    {
        using var f = new DrillFactory();
        var (status, body) = await f.PostRawAsync("/api/rotations/issue", new { }, idempotencyKey: "  ");
        Assert.Equal(400, status);
        Assert.Contains("missing_idempotency_key", body);
    }
}
