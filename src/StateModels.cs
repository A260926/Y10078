namespace CertRotationDrill;

public sealed record InstanceView(
    string InstanceId,
    int DeliveryAttempts,
    bool Delivered,
    string? LastDeliveryError,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? LoadedAt,
    bool Received,
    bool Loaded);

public sealed record RotationView(
    string RotationId,
    string Phase,
    int Version,
    IReadOnlyList<string> Instances,
    string PolicyMode,
    int Threshold,
    int OverlapMinutes,
    int LoadedCount,
    bool AllDelivered,
    bool ThresholdMet,
    bool CanActivate,
    bool CanRetire,
    bool CanRollback,
    CertificateDescription? Candidate,
    string? ActiveSerial,
    string? PreviousSerial,
    string? RestoredSerial,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? RetireNotBefore,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? FailedAt,
    string? FailedAtPhase,
    string? FailureReason,
    bool RolledBack,
    bool NeedsManualAction,
    string? ManualReason,
    IReadOnlyList<InstanceView> InstancesProgress,
    CertificateDescription? PreviousCertificate,
    bool PreviousCurrentlyValid);

public sealed record EventView(
    long Seq,
    string RotationId,
    int Version,
    string Type,
    DateTimeOffset OccurredAt,
    string IdempotencyKey,
    object Payload);

public sealed record FullState(
    DateTimeOffset Now,
    string ClockType,
    CertificateDescription? ActiveCertificate,
    string? ActiveSerial,
    RotationView? CurrentRotation,
    IReadOnlyList<RotationView> Rotations,
    IReadOnlyList<EventView> Events);
