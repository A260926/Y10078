using System.Security.Cryptography;
using System.Text;

namespace CertRotationDrill;

public sealed class FakeIssuerException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class FakeCa
{
    private long _issuedCount;
    private int _duplicateSerialNext;
    private readonly HashSet<string> _failNextDelivery = new();

    public FakeCa(long startingSerialCount)
    {
        _issuedCount = startingSerialCount;
    }

    public void InjectInstanceFailure(string instanceId)
    {
        lock (_failNextDelivery)
        {
            _failNextDelivery.Add(instanceId);
        }
    }

    public void ClearInjectedFailures()
    {
        lock (_failNextDelivery)
        {
            _failNextDelivery.Clear();
        }
    }

    public void InjectDuplicateSerial() => Interlocked.Exchange(ref _duplicateSerialNext, 1);

    public bool IsDeliveryFailing(string instanceId)
    {
        lock (_failNextDelivery)
        {
            return _failNextDelivery.Contains(instanceId);
        }
    }

    public void RecordDeliveryAttempt(string instanceId)
    {
        lock (_failNextDelivery)
        {
            _failNextDelivery.Remove(instanceId);
        }
    }

    public IssuedCertificate Issue(string cn, IReadOnlyList<string> sans, DateTimeOffset notBefore, TimeSpan lifetime)
    {
        long serialCounter = ++_issuedCount;
        bool duplicateRequested = Interlocked.CompareExchange(ref _duplicateSerialNext, 0, 1) == 1;
        if (duplicateRequested && serialCounter > 1)
        {
            serialCounter -= 1; // deliberately repeat the previous serial for this attempt only
        }

        var serial = serialCounter.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
        var notAfter = notBefore.Add(lifetime);
        var keyId = StableHash("kid:" + serial).ToLowerInvariant();
        var thumbprint = StableHash("thumb:" + serial + ":" + cn);

        // The private key is a deterministic, synthetic stand-in for the drill. It is handed
        // straight to the encrypted key slot and never retained on the certificate description.
        var privateKey = "FAKE-PRIVATE-KEY-" + serial + "-" + StableHash("key:" + serial);

        var description = new CertificateDescription(
            serial,
            keyId,
            "CN=" + cn,
            sans.ToArray(),
            notBefore,
            notAfter,
            thumbprint);

        return new IssuedCertificate(description, privateKey);
    }

    public static string StableHash(string input)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(digest);
    }
}
