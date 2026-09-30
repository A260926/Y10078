using System.Globalization;
using CertRotationDrill;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var dataDir = config["DRILL_DATA_DIR"] ?? "data";
    var dbPath = config["DRILL_DB"] ?? Path.Combine(dataDir, "drill.db");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
    var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    return new EventStore(connectionString);
});

builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var dataDir = config["DRILL_DATA_DIR"] ?? "data";
    var keyPath = config["DRILL_KEY_FILE"] ?? Path.Combine(dataDir, "keyslot.key");
    return new KeySlot(keyPath);
});

builder.Services.AddSingleton<Clock>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var mutableClock = string.Equals(
        config["DRILL_MUTABLE_CLOCK"], "true", StringComparison.OrdinalIgnoreCase);
    if (!mutableClock)
    {
        return new SystemClock();
    }
    var initialClockText = config["DRILL_CLOCK_START"];
    var configuredStart = initialClockText is not null
        ? DateTimeOffset.Parse(initialClockText, CultureInfo.InvariantCulture)
        : DemoSeeder.BaseTime;
    var store = sp.GetRequiredService<EventStore>();
    var persisted = store.GetKv("clock.now") is { Length: > 0 } text
        ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture)
        : (DateTimeOffset?)null;
    var start = persisted is { } restored && restored > configuredStart ? restored : configuredStart;
    return new MutableClock(start, value => store.PutKv("clock.now", value.ToString("O", CultureInfo.InvariantCulture)));
});
builder.Services.AddSingleton<DrillEngine>();
builder.Services.AddHostedService<DemoSeedHostedService>();

var app = builder.Build();

var webRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var staticFileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = staticFileProvider });
app.UseStaticFiles(new StaticFileOptions { FileProvider = staticFileProvider });
app.MapDrillEndpoints();

await app.RunAsync();

public sealed class DemoSeedHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DemoSeedHostedService> _logger;

    public DemoSeedHostedService(
        IServiceProvider services, IConfiguration configuration, ILogger<DemoSeedHostedService> logger)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seedDemo = string.Equals(
            _configuration["DRILL_SEED_DEMO"], "true", StringComparison.OrdinalIgnoreCase);
        if (!seedDemo)
        {
            return;
        }

        var store = _services.GetRequiredService<EventStore>();
        if (DemoSeeder.HasData(store))
        {
            return;
        }

        var clock = _services.GetRequiredService<Clock>();
        if (clock is not MutableClock mutable)
        {
            throw new InvalidOperationException("DRILL_SEED_DEMO=true 必须配合 DRILL_MUTABLE_CLOCK=true");
        }

        _logger.LogInformation("未发现历史事件，播种预置轮换演练场景");
        var engine = _services.GetRequiredService<DrillEngine>();
        await DemoSeeder.SeedAsync(engine, mutable);
        _logger.LogInformation("预置场景播种完成，当前演练时钟 {Now}", mutable.UtcNow);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class SystemClock : Clock
{
    public override DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public override void Advance(TimeSpan by) =>
        throw new NotSupportedException("system clock cannot be advanced");

    public override void SetTo(DateTimeOffset value) =>
        throw new NotSupportedException("system clock cannot be set");
}

public partial class Program { }
