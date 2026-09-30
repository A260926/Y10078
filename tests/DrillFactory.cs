using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CertRotationDrill.Tests;

public sealed class DrillFactory : WebApplicationFactory<Program>
{
    public HttpClient ApiClient => CreateClient();

    public string? DataDirOverride { get; init; }

    public string DataDir => DataDirOverride ?? Path.Combine(Path.GetTempPath(), "drill-tests-" + Guid.NewGuid().ToString("N"));

    public bool SeedDemo { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..")));
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DRILL_DATA_DIR"] = DataDir,
                ["DRILL_DB"] = Path.Combine(DataDir, "drill.db"),
                ["DRILL_KEY_FILE"] = Path.Combine(DataDir, "keyslot.key"),
                ["DRILL_MUTABLE_CLOCK"] = "true",
                ["DRILL_CLOCK_START"] = "2026-09-01T00:00:00Z",
                ["DRILL_SEED_DEMO"] = SeedDemo ? "true" : "false"
            });
        });
    }

    public async Task<JsonElement> PostJsonAsync(string path, object body, string? idempotencyKey = null)
    {
        Directory.CreateDirectory(DataDir);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
        using var response = await ApiClient.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new DrillHttpException((int)response.StatusCode, text);
        }
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<(int Status, string Body)> PostRawAsync(string path, object body, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
        using var response = await ApiClient.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public async Task<JsonElement> GetStateAsync()
    {
        var text = await ApiClient.GetStringAsync("/api/state");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task AdvanceAsync(int minutes)
    {
        await PostJsonAsync("/api/drill/clock/advance", new { minutes });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (Directory.Exists(DataDir))
            {
                Directory.Delete(DataDir, recursive: true);
            }
        }
        catch
        {
            // best effort temp cleanup
        }
    }
}

public sealed class DrillHttpException(int status, string body) : Exception($"HTTP {status}: {body}")
{
    public int Status { get; } = status;
    public string Body { get; } = body;
}
