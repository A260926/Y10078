namespace CertRotationDrill;

public abstract record RotationEvent;

public sealed record RotationProposed(
    string RotationId,
    IReadOnlyList<string> Instances,
    string PolicyMode,
    int Threshold,
    TimeSpan OverlapWindow,
    string IdempotencyKey) : RotationEvent;

public sealed record CertificateIssued(
    string RotationId,
    string SerialNumber,
    string KeyId,
    string Subject,
    IReadOnlyList<string> Sans,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Thumbprint) : RotationEvent;

public sealed record IssueRejectedByCa(
    string RotationId,
    string Reason,
    string Code) : RotationEvent;

public sealed record DeliveryAttempted(
    string RotationId,
    string InstanceId,
    bool Delivered,
    string? Reason) : RotationEvent;

public sealed record AllInstancesDistributed(string RotationId) : RotationEvent;

public sealed record InstanceReceived(
    string RotationId,
    string InstanceId,
    DateTimeOffset At) : RotationEvent;

public sealed record InstanceLoaded(
    string RotationId,
    string InstanceId,
    DateTimeOffset At) : RotationEvent;

public sealed record RotationActivated(
    string RotationId,
    string SerialNumber,
    DateTimeOffset At,
    DateTimeOffset RetireNotBefore,
    string? PreviousSerial = null) : RotationEvent;

public sealed record PreviousCertificateRetired(
    string RotationId,
    string PreviousSerial,
    DateTimeOffset At) : RotationEvent;

public sealed record RotationCompleted(string RotationId, DateTimeOffset At) : RotationEvent;

public sealed record RotationFailed(
    string RotationId,
    string FailedAtPhase,
    string Reason) : RotationEvent;

public sealed record PreviousCertificateRestored(
    string RotationId,
    string RestoredSerial,
    DateTimeOffset At) : RotationEvent;

public sealed record RolledBack(
    string RotationId,
    string FromPhase,
    DateTimeOffset At) : RotationEvent;

public sealed record ManualActionRequired(
    string RotationId,
    string FromPhase,
    string Reason) : RotationEvent;

public sealed record StoredEvent(
    long Seq,
    string RotationId,
    int Version,
    string Type,
    DateTimeOffset OccurredAt,
    string IdempotencyKey,
    RotationEvent Payload);
