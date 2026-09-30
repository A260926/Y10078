namespace CertRotationDrill;

public enum RotationPhase
{
    Proposed,
    Issued,
    Distributed,
    Active,
    Retiring,
    Completed,
    Failed,
    RollingBack,
    ManualActionRequired
}

public static class Phases
{
    public const string Proposed = "proposed";
    public const string Issued = "issued";
    public const string Distributed = "distributed";
    public const string Active = "active";
    public const string Retiring = "retiring";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string RollingBack = "rolling_back";
    public const string ManualActionRequired = "manual_action_required";

    public static string Name(RotationPhase phase) => phase switch
    {
        RotationPhase.Proposed => Proposed,
        RotationPhase.Issued => Issued,
        RotationPhase.Distributed => Distributed,
        RotationPhase.Active => Active,
        RotationPhase.Retiring => Retiring,
        RotationPhase.Completed => Completed,
        RotationPhase.Failed => Failed,
        RotationPhase.RollingBack => RollingBack,
        RotationPhase.ManualActionRequired => ManualActionRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };

    public static bool IsTerminal(string phase) =>
        phase is Completed or Failed or ManualActionRequired;
}

public sealed class InstanceProgress
{
    public required string InstanceId { get; init; }
    public int DeliveryAttempts { get; set; }
    public bool Delivered { get; set; }
    public string? LastDeliveryError { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public DateTimeOffset? LoadedAt { get; set; }
    public bool Loaded => LoadedAt is not null;
}

public sealed class RotationState
{
    public required string RotationId { get; init; }
    public RotationPhase Phase { get; set; } = RotationPhase.Proposed;
    public int Version { get; set; }
    public List<string> Instances { get; set; } = new();
    public string PolicyMode { get; set; } = "quorum";
    public int Threshold { get; set; }
    public TimeSpan OverlapWindow { get; set; }
    public Dictionary<string, InstanceProgress> Progress { get; } = new();
    public CertificateDescription? Candidate { get; set; }
    public string? ActiveSerial { get; set; }
    public string? RestoredSerial { get; set; }
    public string? PreviousSerial { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? RetireNotBefore { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string? FailedAtPhase { get; set; }
    public string? FailureReason { get; set; }
    public bool RolledBack { get; set; }
    public bool NeedsManualAction { get; set; }
    public string? ManualReason { get; set; }
    public List<StoredEvent> Events { get; } = new();

    public string PhaseName => Phases.Name(Phase);

    public bool IsTerminal => Phases.IsTerminal(PhaseName);

    public int LoadedCount => Progress.Values.Count(p => p.Loaded);

    public bool ThresholdMet => LoadedCount >= Threshold;

    public bool AllDelivered => Progress.Values.All(p => p.Delivered);
}

public sealed class CertRecord
{
    public required string SerialNumber { get; init; }
    public required string KeyId { get; init; }
    public required string Subject { get; init; }
    public required IReadOnlyList<string> Sans { get; init; }
    public required DateTimeOffset NotBefore { get; init; }
    public required DateTimeOffset NotAfter { get; init; }
    public required string Thumbprint { get; init; }
}

public static class RotationFold
{
    public static RotationState Apply(RotationState state, StoredEvent stored)
    {
        state.Events.Add(stored);
        switch (stored.Payload)
        {
            case RotationProposed proposed:
                state.Instances = proposed.Instances.ToList();
                state.PolicyMode = proposed.PolicyMode;
                state.Threshold = proposed.Threshold;
                state.OverlapWindow = proposed.OverlapWindow;
                foreach (var instance in proposed.Instances)
                {
                    state.Progress[instance] = new InstanceProgress { InstanceId = instance };
                }
                break;

            case CertificateIssued issued:
                state.Candidate = new CertificateDescription(
                    issued.SerialNumber,
                    issued.KeyId,
                    issued.Subject,
                    issued.Sans,
                    issued.NotBefore,
                    issued.NotAfter,
                    issued.Thumbprint);
                if (state.Phase == RotationPhase.Proposed)
                {
                    state.Phase = RotationPhase.Issued;
                }
                break;

            case IssueRejectedByCa rejected:
                state.Phase = RotationPhase.Failed;
                state.FailedAt = stored.OccurredAt;
                state.FailedAtPhase = Phases.Issued;
                state.FailureReason = rejected.Reason;
                break;

            case DeliveryAttempted attempted:
                var progress = state.Progress[attempted.InstanceId];
                progress.DeliveryAttempts++;
                if (attempted.Delivered)
                {
                    progress.Delivered = true;
                    progress.LastDeliveryError = null;
                }
                else
                {
                    progress.LastDeliveryError = attempted.Reason;
                }
                break;

            case AllInstancesDistributed:
                if (state.Phase == RotationPhase.Issued)
                {
                    state.Phase = RotationPhase.Distributed;
                }
                break;

            case InstanceReceived received:
                state.Progress[received.InstanceId].ReceivedAt = received.At;
                break;

            case InstanceLoaded loaded:
                state.Progress[loaded.InstanceId].LoadedAt = loaded.At;
                break;

            case RotationActivated activated:
                state.PreviousSerial = activated.PreviousSerial ?? state.ActiveSerial;
                state.ActiveSerial = activated.SerialNumber;
                state.ActivatedAt = activated.At;
                state.RetireNotBefore = activated.RetireNotBefore;
                state.Phase = RotationPhase.Active;
                break;

            case PreviousCertificateRetired retired:
                state.Phase = RotationPhase.Retiring;
                break;

            case RotationCompleted:
                state.CompletedAt = stored.OccurredAt;
                state.Phase = RotationPhase.Completed;
                break;

            case RotationFailed failed:
                state.Phase = RotationPhase.Failed;
                state.FailedAt = stored.OccurredAt;
                state.FailedAtPhase = failed.FailedAtPhase;
                state.FailureReason = failed.Reason;
                break;

            case PreviousCertificateRestored restored:
                state.RestoredSerial = restored.RestoredSerial;
                state.ActiveSerial = restored.RestoredSerial;
                state.Phase = RotationPhase.RollingBack;
                break;

            case RolledBack:
                state.RolledBack = true;
                state.CompletedAt = stored.OccurredAt;
                state.Phase = RotationPhase.Completed;
                break;

            case ManualActionRequired manual:
                state.NeedsManualAction = true;
                state.ManualReason = manual.Reason;
                state.Phase = RotationPhase.ManualActionRequired;
                break;
        }

        return state;
    }
}
