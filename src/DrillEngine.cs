using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CertRotationDrill;

public sealed class ConflictException(string message, FullState ServerState)
    : Exception(message)
{
    public FullState ServerState { get; } = ServerState;
}

public sealed class RuleViolationException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record StartRotationRequest(
    string IdempotencyKey,
    string? RotationId,
    IReadOnlyList<string>? Instances,
    string? PolicyMode,
    int? Threshold,
    int? OverlapMinutes,
    int? LifetimeDays);

public sealed record IssueRequest(string IdempotencyKey, int? LifetimeDays);

public sealed record DistributeRequest(string IdempotencyKey, string InstanceId);

public sealed record AcknowledgeRequest(string IdempotencyKey, string InstanceId, bool Loaded);

public sealed record ActivateRequest(string IdempotencyKey, int? ExpectedVersion);

public sealed record RetireRequest(string IdempotencyKey, int? ExpectedVersion);

public sealed record CompleteRequest(string IdempotencyKey, int? ExpectedVersion);

public sealed record RollbackRequest(string IdempotencyKey, int? ExpectedVersion);

public sealed record FailRequest(string IdempotencyKey, string? Phase, string? Reason, int? ExpectedVersion);

public sealed record CommandResult(int StatusCode, object Response, string RotationId);

public partial class DrillEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly EventStore _store;
    private readonly Clock _clock;
    private readonly FakeCa _ca;
    private readonly KeySlot _keySlot;
    private readonly ILogger<DrillEngine> _logger;

    private readonly List<RotationState> _rotations = new();
    private readonly Dictionary<string, RotationState> _byId = new();
    private readonly Dictionary<string, CertRecord> _certBySerial = new();
    private readonly List<string> _activationOrder = new();
    private string? _activeSerial;

    public DrillEngine(EventStore store, Clock clock, KeySlot keySlot, ILogger<DrillEngine> logger)
    {
        _store = store;
        _clock = clock;
        _keySlot = keySlot;
        _logger = logger;
        Replay();
        _ca = new FakeCa(_store.CountIssuedSerials());
    }

    public FakeCa Ca => _ca;

    public DateTimeOffset Now => _clock.UtcNow;

    private void Replay()
    {
        foreach (var cert in _store.LoadIssuedCertificates())
        {
            _certBySerial[cert.SerialNumber] = cert;
        }

        foreach (var stored in _store.LoadEvents())
        {
            if (!_byId.TryGetValue(stored.RotationId, out var state))
            {
                state = new RotationState { RotationId = stored.RotationId };
                _byId[stored.RotationId] = state;
                _rotations.Add(state);
            }
            RotationFold.Apply(state, stored);
            ApplyGlobal(stored);
            state.Version = stored.Version;
        }
    }

    private void ApplyGlobal(StoredEvent stored)
    {
        switch (stored.Payload)
        {
            case CertificateIssued issued:
                _certBySerial[issued.SerialNumber] = new CertRecord
                {
                    SerialNumber = issued.SerialNumber,
                    KeyId = issued.KeyId,
                    Subject = issued.Subject,
                    Sans = issued.Sans,
                    NotBefore = issued.NotBefore,
                    NotAfter = issued.NotAfter,
                    Thumbprint = issued.Thumbprint
                };
                break;

            case RotationActivated activated:
                _activeSerial = activated.SerialNumber;
                if (!_activationOrder.Contains(activated.SerialNumber))
                {
                    _activationOrder.Add(activated.SerialNumber);
                }
                break;

            case PreviousCertificateRestored restored:
                _activeSerial = restored.RestoredSerial;
                break;

            case ManualActionRequired:
                // no active pointer change: the stale active cert needs human triage
                break;
        }
    }

    // Half-open [NotBefore, NotAfter): every time boundary in the drill uses the same
    // convention, including the overlap window [ActivatedAt, ActivatedAt + Overlap).
    public static bool IsValidAt(CertRecord cert, DateTimeOffset at) =>
        at >= cert.NotBefore && at < cert.NotAfter;

    public bool IsWithinOverlap(RotationState state, DateTimeOffset at) =>
        state.RetireNotBefore is { } boundary && at >= state.ActivatedAt! && at < boundary;
}

public partial class DrillEngine
{
    private sealed class CommandContext(SqliteTransaction transaction, string idempotencyKey)
    {
        public SqliteTransaction Transaction { get; } = transaction;
        public string IdempotencyKey { get; } = idempotencyKey;
    }

    private void Rebuild()
    {
        _rotations.Clear();
        _byId.Clear();
        _certBySerial.Clear();
        _activationOrder.Clear();
        _activeSerial = null;
        Replay();
    }

    private StoredEvent Emit(CommandContext ctx, string rotationId, RotationEvent payload)
    {
        var (seq, version) = _store.AppendEventWithVersion(ctx.Transaction, rotationId, payload, Now, ctx.IdempotencyKey);
        if (!_byId.TryGetValue(rotationId, out var state))
        {
            state = new RotationState { RotationId = rotationId };
            _byId[rotationId] = state;
            _rotations.Add(state);
        }
        var stored = new StoredEvent(seq, rotationId, version, payload.GetType().Name, Now, ctx.IdempotencyKey, payload);
        RotationFold.Apply(state, stored);
        state.Version = version;
        ApplyGlobal(stored);
        return stored;
    }

    private RotationState RequireOpenRotation()
    {
        var open = _rotations.LastOrDefault(r => !r.IsTerminal);
        if (open is null)
        {
            throw new RuleViolationException("当前没有进行中的轮换", "no_open_rotation");
        }
        return open;
    }

    private static void CheckVersion(RotationState state, int? expectedVersion, Func<FullState> buildState)
    {
        if (expectedVersion is int expected && expected != state.Version)
        {
            throw new ConflictException(
                $"版本冲突：期望 {expected}，服务器当前 {state.Version}", buildState());
        }
    }

    private void StartRotation(CommandContext ctx, StartRotationRequest request, IReadOnlyList<string> defaults,
        int defaultOverlapMinutes, int defaultLifetimeDays)
    {
        var open = _rotations.LastOrDefault(r => !r.IsTerminal);
        if (open is not null)
        {
            throw new ConflictException(
                $"轮换 {open.RotationId} 仍在进行中（{open.PhaseName}），完成或回滚后才能发起下一次", BuildState());
        }

        var instances = (request.Instances is { Count: > 0 } ? request.Instances : defaults)
            .Select(i => i.Trim())
            .Where(i => i.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (instances.Count == 0)
        {
            throw new RuleViolationException("至少需要一个目标实例", "no_instances");
        }

        var policyMode = string.IsNullOrWhiteSpace(request.PolicyMode) ? "quorum" : request.PolicyMode.ToLowerInvariant();
        int threshold;
        if (request.Threshold is int explicitThreshold)
        {
            threshold = explicitThreshold;
        }
        else
        {
            threshold = policyMode switch
            {
                "unanimous" or "all" => instances.Count,
                "quorum" => instances.Count / 2 + 1,
                _ => throw new RuleViolationException("未知策略模式，支持 quorum 或 unanimous", "bad_policy")
            };
        }
        if (threshold < 1 || threshold > instances.Count)
        {
            throw new RuleViolationException("阈值必须在 1 到实例数之间", "bad_threshold");
        }

        var overlap = TimeSpan.FromMinutes(request.OverlapMinutes is int m && m > 0 ? m : defaultOverlapMinutes);
        var rotationId = string.IsNullOrWhiteSpace(request.RotationId)
            ? "rot-" + (_rotations.Count + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture)
            : request.RotationId.Trim();
        if (_byId.ContainsKey(rotationId))
        {
            throw new RuleViolationException("轮换标识已存在", "rotation_exists");
        }

        Emit(ctx, rotationId, new RotationProposed(
            rotationId, instances, policyMode, threshold, overlap, ctx.IdempotencyKey));
    }

    private void IssueCertificate(CommandContext ctx, IssueRequest request, int defaultLifetimeDays)
    {
        var state = RequireOpenRotation();
        if (state.Phase != RotationPhase.Proposed)
        {
            throw new RuleViolationException($"当前阶段 {state.PhaseName} 不能再签发", "not_proposed");
        }

        var lifetimeDays = request.LifetimeDays is int days && days > 0 ? days : defaultLifetimeDays;
        var cn = state.RotationId + ".edge.local";
        var sans = state.Instances.Select(i => i + ".edge.local").ToList();

        var issued = _ca.Issue(cn, sans, Now, TimeSpan.FromDays(lifetimeDays));
        var cert = issued.Description;

        if (_store.SerialExists(ctx.Transaction, cert.SerialNumber))
        {
            _logger.LogWarning("假签发端拒绝重复序列号 {Serial}", cert.SerialNumber);
            Emit(ctx, state.RotationId, new IssueRejectedByCa(
                state.RotationId,
                $"假签发端检测到重复序列号 {cert.SerialNumber}",
                "duplicate_serial"));
            return;
        }

        _keySlot.Put(_store.Connection, ctx.Transaction, cert.KeyId, issued.PrivateKeyPlaceholder);
        _store.RecordSerial(ctx.Transaction, cert, state.RotationId, Now);
        _certBySerial[cert.SerialNumber] = new CertRecord
        {
            SerialNumber = cert.SerialNumber,
            KeyId = cert.KeyId,
            Subject = cert.Subject,
            Sans = cert.SubjectAlternativeNames,
            NotBefore = cert.NotBefore,
            NotAfter = cert.NotAfter,
            Thumbprint = cert.Thumbprint
        };

        Emit(ctx, state.RotationId, new CertificateIssued(
            state.RotationId,
            cert.SerialNumber,
            cert.KeyId,
            cert.Subject,
            cert.SubjectAlternativeNames,
            cert.NotBefore,
            cert.NotAfter,
            cert.Thumbprint));

        _logger.LogInformation(
            "已签发描述证书 序列号={Serial} keyId={KeyId} 轮换={Rotation}",
            cert.SerialNumber, cert.KeyId, state.RotationId);
    }

    private void Distribute(CommandContext ctx, DistributeRequest request)
    {
        var state = RequireOpenRotation();
        if (state.Phase is not (RotationPhase.Issued or RotationPhase.Distributed))
        {
            throw new RuleViolationException($"当前阶段 {state.PhaseName} 不能分发", "not_issuable_phase");
        }
        if (state.Candidate is null)
        {
            throw new RuleViolationException("证书尚未签发", "not_issued");
        }
        if (!state.Progress.TryGetValue(request.InstanceId, out var progress))
        {
            throw new RuleViolationException($"未知实例 {request.InstanceId}", "unknown_instance");
        }

        if (progress.Delivered)
        {
            return; // idempotent retry: do not append another delivery event
        }

        if (_ca.IsDeliveryFailing(request.InstanceId))
        {
            _ca.RecordDeliveryAttempt(request.InstanceId);
            _logger.LogWarning("分发失败 实例={Instance} 轮换={Rotation}（注入故障）",
                request.InstanceId, state.RotationId);
            Emit(ctx, state.RotationId, new DeliveryAttempted(
                state.RotationId, request.InstanceId, false, "注入的实例分发故障"));
            return;
        }

        Emit(ctx, state.RotationId, new DeliveryAttempted(state.RotationId, request.InstanceId, true, null));
        if (state.Phase == RotationPhase.Issued && state.AllDelivered)
        {
            Emit(ctx, state.RotationId, new AllInstancesDistributed(state.RotationId));
        }
    }

    private void Acknowledge(CommandContext ctx, AcknowledgeRequest request)
    {
        var state = RequireOpenRotation();
        if (state.Phase is not (RotationPhase.Issued or RotationPhase.Distributed))
        {
            throw new RuleViolationException($"当前阶段 {state.PhaseName} 不接受实例确认", "bad_phase");
        }
        if (!state.Progress.TryGetValue(request.InstanceId, out var progress))
        {
            throw new RuleViolationException($"未知实例 {request.InstanceId}", "unknown_instance");
        }
        if (!progress.Delivered)
        {
            throw new RuleViolationException(
                $"实例 {request.InstanceId} 尚未成功接收证书包，不能确认", "not_delivered");
        }

        if (progress.ReceivedAt is null)
        {
            Emit(ctx, state.RotationId, new InstanceReceived(state.RotationId, request.InstanceId, Now));
        }
        if (request.Loaded && progress.LoadedAt is null)
        {
            Emit(ctx, state.RotationId, new InstanceLoaded(state.RotationId, request.InstanceId, Now));
        }
    }

    private void Activate(CommandContext ctx, ActivateRequest request)
    {
        var state = RequireOpenRotation();
        CheckVersion(state, request.ExpectedVersion, BuildState);
        if (state.Phase != RotationPhase.Distributed)
        {
            throw new RuleViolationException($"当前阶段 {state.PhaseName} 不能激活", "not_distributed");
        }
        if (!state.AllDelivered)
        {
            throw new RuleViolationException("仍有实例未收到证书，不能激活", "not_all_delivered");
        }
        if (!state.ThresholdMet)
        {
            throw new RuleViolationException(
                $"已装载 {state.LoadedCount}/{state.Instances.Count}，策略阈值 {state.Threshold} 未满足，不能激活",
                "threshold_not_met");
        }
        if (state.Candidate is null)
        {
            throw new RuleViolationException("候选证书缺失", "no_candidate");
        }
        var candidateRecord = _certBySerial[state.Candidate.SerialNumber];
        if (!IsValidAt(candidateRecord, Now))
        {
            throw new RuleViolationException("候选证书当前不在有效期内，不能激活", "candidate_invalid");
        }

        var retireBoundary = Now.Add(state.OverlapWindow);
        var previousSerialAtActivation = _activeSerial is not null && _activeSerial != state.Candidate.SerialNumber
            ? _activeSerial
            : null;
        Emit(ctx, state.RotationId, new RotationActivated(
            state.RotationId, state.Candidate.SerialNumber, Now, retireBoundary, previousSerialAtActivation));
        _logger.LogInformation(
            "激活 序列号={Serial} 轮换={Rotation} 重叠窗口=[{Start},{End})",
            state.Candidate.SerialNumber, state.RotationId,
            Now.ToString("O"), retireBoundary.ToString("O"));
    }

    private void RetirePrevious(CommandContext ctx, RetireRequest request)
    {
        var state = RequireOpenRotation();
        CheckVersion(state, request.ExpectedVersion, BuildState);
        if (state.Phase != RotationPhase.Active)
        {
            throw new RuleViolationException($"当前阶段 {state.PhaseName} 不能退出旧证书", "not_active");
        }
        if (state.RetireNotBefore is { } boundary && Now < boundary)
        {
            throw new RuleViolationException(
                $"仍处于双证书重叠窗口内（[{state.ActivatedAt:O},{boundary:O})），未到旧证书退出时刻",
                "overlap_open");
        }
        if (state.PreviousSerial is not null)
        {
            Emit(ctx, state.RotationId, new PreviousCertificateRetired(state.RotationId, state.PreviousSerial, Now));
            _logger.LogInformation("旧证书已退出 序列号={Serial} 轮换={Rotation}",
                state.PreviousSerial, state.RotationId);
        }
        else
        {
            // 首个活动证书没有可退出的旧证书，重叠窗口结束后直接收尾
            Emit(ctx, state.RotationId, new PreviousCertificateRetired(state.RotationId, "", Now));
            _logger.LogInformation("首个活动证书无旧证书可退出，轮换={Rotation}", state.RotationId);
        }
    }

    private void Complete(CommandContext ctx, CompleteRequest request)
    {
        var state = RequireOpenRotation();
        CheckVersion(state, request.ExpectedVersion, BuildState);
        if (state.Phase != RotationPhase.Retiring)
        {
            throw new RuleViolationException($"当前阶段 {state.PhaseName} 不能完成", "not_retiring");
        }
        Emit(ctx, state.RotationId, new RotationCompleted(state.RotationId, Now));
    }

    private void MarkFailed(CommandContext ctx, FailRequest request)
    {
        var state = RequireOpenRotation();
        CheckVersion(state, request.ExpectedVersion, BuildState);
        var failedPhase = string.IsNullOrWhiteSpace(request.Phase) ? state.PhaseName : request.Phase;
        Emit(ctx, state.RotationId, new RotationFailed(
            state.RotationId,
            failedPhase,
            string.IsNullOrWhiteSpace(request.Reason) ? "人工标记失败" : request.Reason));
    }

    private void Rollback(CommandContext ctx, RollbackRequest request)
    {
        var state = RequireOpenRotation();
        CheckVersion(state, request.ExpectedVersion, BuildState);
        if (state.Phase is RotationPhase.Completed or RotationPhase.Failed or RotationPhase.ManualActionRequired)
        {
            throw new RuleViolationException($"当前阶段 {state.PhaseName} 不能回滚", "terminal");
        }

        var previousSerial = state.Phase is RotationPhase.Active or RotationPhase.Retiring
            ? state.PreviousSerial
            : _activeSerial;
        var fromPhase = state.PhaseName;

        if (previousSerial is null || !_certBySerial.TryGetValue(previousSerial, out var previous))
        {
            Emit(ctx, state.RotationId, new ManualActionRequired(
                state.RotationId, fromPhase, "不存在可恢复的上一个有效证书，需要人工处理"));
            return;
        }
        if (!IsValidAt(previous, Now))
        {
            _logger.LogWarning("旧证书 {Serial} 已过期，回滚转为人工处理", previousSerial);
            Emit(ctx, state.RotationId, new ManualActionRequired(
                state.RotationId,
                fromPhase,
                $"上一个证书 {previousSerial} 已过期（有效期 [{previous.NotBefore:O},{previous.NotAfter:O})），不能假装恢复，需要人工处理"));
            return;
        }

        Emit(ctx, state.RotationId, new PreviousCertificateRestored(state.RotationId, previousSerial, Now));
        Emit(ctx, state.RotationId, new RolledBack(state.RotationId, fromPhase, Now));
        _logger.LogInformation("回滚完成，已恢复 序列号={Serial} 轮换={Rotation}", previousSerial, state.RotationId);
    }

}

public partial class DrillEngine
{
    public IReadOnlyList<string> DefaultInstances { get; set; } =
        new[] { "edge-node-a", "edge-node-b" };

    public int DefaultOverlapMinutes { get; set; } = 60;

    public int DefaultLifetimeDays { get; set; } = 90;

    public async Task<FullState> StartRotationAsync(StartRotationRequest request)
    {
        var result = await RunCommandAsync(request.IdempotencyKey, request,
            ctx => StartRotation(ctx, request, DefaultInstances, DefaultOverlapMinutes, DefaultLifetimeDays));
        return result;
    }

    public Task<FullState> IssueAsync(IssueRequest request) =>
        RunCommandAsync(request.IdempotencyKey, request, ctx => IssueCertificate(ctx, request, DefaultLifetimeDays));

    public Task<FullState> DistributeAsync(DistributeRequest request) =>
        RunCommandAsync(request.IdempotencyKey, request, ctx => Distribute(ctx, request));

    public Task<FullState> AcknowledgeAsync(AcknowledgeRequest request) =>
        RunCommandAsync(request.IdempotencyKey, request, ctx => Acknowledge(ctx, request));

    public Task<FullState> ActivateAsync(ActivateRequest request) =>
        RunCommandAsync(request.IdempotencyKey, request, ctx => Activate(ctx, request));

    public Task<FullState> RetireAsync(RetireRequest request) =>
        RunCommandAsync(request.IdempotencyKey, request, ctx => RetirePrevious(ctx, request));

    public Task<FullState> CompleteAsync(CompleteRequest request) =>
        RunCommandAsync(request.IdempotencyKey, request, ctx => Complete(ctx, request));

    public Task<FullState> RollbackAsync(RollbackRequest request) =>
        RunCommandAsync(request.IdempotencyKey, request, ctx => Rollback(ctx, request));

    public Task<FullState> FailAsync(FailRequest request) =>
        RunCommandAsync(request.IdempotencyKey, request, ctx => MarkFailed(ctx, request));

    private static string RequestHash(object request)
    {
        var json = JsonSerializer.Serialize(request, request.GetType(), JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private async Task<FullState> RunCommandAsync<T>(string idempotencyKey, T request, Action<CommandContext> action)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new RuleViolationException("命令必须携带幂等键 idempotencyKey", "missing_idempotency_key");
        }

        var requestJson = JsonSerializer.Serialize(request, typeof(T), JsonOptions);
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson)));

        using (await _store.LockAsync())
        {
            if (_store.TryGetIdempotent(idempotencyKey, requestHash, out var replay))
            {
                _logger.LogInformation("幂等重放 key={Key} rotation={Rotation}", idempotencyKey, replay.RotationId);
                return BuildState();
            }

            using var transaction = _store.Connection.BeginTransaction();
            var context = new CommandContext(transaction, idempotencyKey);
            try
            {
                action(context);
                var state = BuildState();
                _store.SaveIdempotent(transaction, idempotencyKey, requestHash,
                    state.CurrentRotation?.RotationId ?? "-", 200,
                    JsonSerializer.Serialize(state, JsonOptions), Now);
                transaction.Commit();
                return state;
            }
            catch (ConflictException)
            {
                transaction.Rollback();
                throw;
            }
            catch (RuleViolationException)
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    public FullState BuildState()
    {
        var rotationViews = _rotations.Select(BuildRotationView).ToList();
        var current = rotationViews.LastOrDefault(r => !Phases.IsTerminal(r.Phase));
        var events = _rotations
            .SelectMany(r => r.Events)
            .OrderBy(e => e.Seq)
            .Select(e => new EventView(
                e.Seq, e.RotationId, e.Version, e.Type, e.OccurredAt,
                e.IdempotencyKey, e.Payload))
            .ToList();

        CertificateDescription? activeCert = null;
        if (_activeSerial is not null && _certBySerial.TryGetValue(_activeSerial, out var active))
        {
            activeCert = ToDescription(active);
        }

        return new FullState(
            Now,
            _clock is MutableClock ? "mutable" : "system",
            activeCert,
            _activeSerial,
            current,
            rotationViews,
            events);
    }

    private RotationView BuildRotationView(RotationState state)
    {
        CertRecord? previous = null;
        if (state.PreviousSerial is not null)
        {
            _certBySerial.TryGetValue(state.PreviousSerial, out previous);
        }
        var inOverlap = state.Phase == RotationPhase.Active && state.RetireNotBefore is { } boundary && Now < boundary;
        var canRetire = state.Phase == RotationPhase.Active && !inOverlap && state.PreviousSerial is not null;

        var instanceViews = state.Progress.Values
            .OrderBy(p => p.InstanceId, StringComparer.Ordinal)
            .Select(p => new InstanceView(
                p.InstanceId,
                p.DeliveryAttempts,
                p.Delivered,
                p.LastDeliveryError,
                p.ReceivedAt,
                p.LoadedAt,
                p.ReceivedAt is not null,
                p.Loaded))
            .ToList();

        return new RotationView(
            state.RotationId,
            state.PhaseName,
            state.Version,
            state.Instances,
            state.PolicyMode,
            state.Threshold,
            (int)state.OverlapWindow.TotalMinutes,
            state.LoadedCount,
            state.AllDelivered,
            state.ThresholdMet,
            state.Phase == RotationPhase.Distributed && state.AllDelivered && state.ThresholdMet,
            canRetire,
            state.Phase is not (RotationPhase.Completed or RotationPhase.Failed or RotationPhase.ManualActionRequired),
            state.Candidate,
            state.ActiveSerial,
            state.PreviousSerial,
            state.RestoredSerial,
            state.ActivatedAt,
            state.RetireNotBefore,
            state.CompletedAt,
            state.FailedAt,
            state.FailedAtPhase,
            state.FailureReason,
            state.RolledBack,
            state.NeedsManualAction,
            state.ManualReason,
            instanceViews,
            previous is null ? null : ToDescription(previous),
            previous is not null && IsValidAt(previous, Now));
    }

    private static CertificateDescription ToDescription(CertRecord record) => new(
        record.SerialNumber,
        record.KeyId,
        record.Subject,
        record.Sans,
        record.NotBefore,
        record.NotAfter,
        record.Thumbprint);
}
