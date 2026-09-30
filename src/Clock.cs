using System.Globalization;

namespace CertRotationDrill;

public abstract class Clock
{
    public abstract DateTimeOffset UtcNow { get; }

    public abstract void Advance(TimeSpan by);

    public abstract void SetTo(DateTimeOffset value);

    public string NowIso() => UtcNow.ToString("O", CultureInfo.InvariantCulture);
}

public sealed class MutableClock : Clock
{
    private DateTimeOffset _now;
    private readonly Action<DateTimeOffset>? _persist;

    public MutableClock(DateTimeOffset initial, Action<DateTimeOffset>? persist = null)
    {
        _now = initial;
        _persist = persist;
    }

    public override DateTimeOffset UtcNow => _now;

    public override void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
        {
            throw new ArgumentException("clock can only advance forward", nameof(by));
        }
        _now = _now.Add(by);
        _persist?.Invoke(_now);
    }

    public override void SetTo(DateTimeOffset value)
    {
        if (value < _now)
        {
            throw new ArgumentException("clock can only move forward", nameof(value));
        }
        _now = value;
        _persist?.Invoke(_now);
    }
}
