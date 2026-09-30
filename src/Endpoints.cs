using System.Text.Json;

namespace CertRotationDrill;

public sealed record CommandEnvelope(
    string? IdempotencyKey,
    string? RotationId,
    IReadOnlyList<string>? Instances,
    string? PolicyMode,
    int? Threshold,
    int? OverlapMinutes,
    int? LifetimeDays,
    int? Minutes,
    string? InstanceId,
    bool? Loaded,
    int? ExpectedVersion,
    string? Phase,
    string? Reason);

public static class Endpoints
{
    private static string Idem(HttpRequest request, CommandEnvelope? body)
    {
        if (request.Headers.TryGetValue("Idempotency-Key", out var header) && !string.IsNullOrWhiteSpace(header))
        {
            return header!;
        }
        return body?.IdempotencyKey ?? "";
    }

    private static IResult Handle(Exception ex) => ex switch
    {
        ConflictException conflict => Results.Json(new
        {
            error = conflict.Message,
            code = "version_conflict",
            serverState = conflict.ServerState
        }, statusCode: StatusCodes.Status409Conflict),
        RuleViolationException rule => Results.Json(new
        {
            error = rule.Message,
            code = rule.Code
        }, statusCode: StatusCodes.Status400BadRequest),
        IdempotentReplayException replay => Results.Json(new
        {
            error = replay.Message,
            code = "idempotency_key_mismatch"
        }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError)
    };

    public static void MapDrillEndpoints(this WebApplication app)
    {
        app.MapGet("/api/state", (DrillEngine engine) => engine.BuildState());

        app.MapPost("/api/rotations", async (DrillEngine engine, HttpRequest request, CommandEnvelope? body) =>
        {
            try
            {
                var state = await engine.StartRotationAsync(new StartRotationRequest(
                    Idem(request, body),
                    body?.RotationId,
                    body?.Instances,
                    body?.PolicyMode,
                    body?.Threshold,
                    body?.OverlapMinutes,
                    body?.LifetimeDays));
                return Results.Json(state, statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex)
            {
                return Handle(ex);
            }
        });

        app.MapPost("/api/rotations/issue", async (DrillEngine engine, HttpRequest request, CommandEnvelope? body) =>
        {
            try
            {
                return Results.Ok(await engine.IssueAsync(new IssueRequest(Idem(request, body), body?.LifetimeDays)));
            }
            catch (Exception ex)
            {
                return Handle(ex);
            }
        });

        app.MapPost("/api/rotations/distribute", async (DrillEngine engine, HttpRequest request, CommandEnvelope? body) =>
        {
            try
            {
                return Results.Ok(await engine.DistributeAsync(
                    new DistributeRequest(Idem(request, body), body?.InstanceId ?? "")));
            }
            catch (Exception ex)
            {
                return Handle(ex);
            }
        });

        app.MapPost("/api/rotations/acknowledge", async (DrillEngine engine, HttpRequest request, CommandEnvelope? body) =>
        {
            try
            {
                return Results.Ok(await engine.AcknowledgeAsync(new AcknowledgeRequest(
                    Idem(request, body), body?.InstanceId ?? "", body?.Loaded ?? false)));
            }
            catch (Exception ex)
            {
                return Handle(ex);
            }
        });

        app.MapPost("/api/rotations/activate", async (DrillEngine engine, HttpRequest request, CommandEnvelope? body) =>
        {
            try
            {
                return Results.Ok(await engine.ActivateAsync(
                    new ActivateRequest(Idem(request, body), body?.ExpectedVersion)));
            }
            catch (Exception ex)
            {
                return Handle(ex);
            }
        });

        app.MapPost("/api/rotations/retire", async (DrillEngine engine, HttpRequest request, CommandEnvelope? body) =>
        {
            try
            {
                return Results.Ok(await engine.RetireAsync(
                    new RetireRequest(Idem(request, body), body?.ExpectedVersion)));
            }
            catch (Exception ex)
            {
                return Handle(ex);
            }
        });

        app.MapPost("/api/rotations/complete", async (DrillEngine engine, HttpRequest request, CommandEnvelope? body) =>
        {
            try
            {
                return Results.Ok(await engine.CompleteAsync(
                    new CompleteRequest(Idem(request, body), body?.ExpectedVersion)));
            }
            catch (Exception ex)
            {
                return Handle(ex);
            }
        });

        app.MapPost("/api/rotations/rollback", async (DrillEngine engine, HttpRequest request, CommandEnvelope? body) =>
        {
            try
            {
                return Results.Ok(await engine.RollbackAsync(
                    new RollbackRequest(Idem(request, body), body?.ExpectedVersion)));
            }
            catch (Exception ex)
            {
                return Handle(ex);
            }
        });

        app.MapPost("/api/rotations/fail", async (DrillEngine engine, HttpRequest request, CommandEnvelope? body) =>
        {
            try
            {
                return Results.Ok(await engine.FailAsync(new FailRequest(
                    Idem(request, body), body?.Phase, body?.Reason, body?.ExpectedVersion)));
            }
            catch (Exception ex)
            {
                return Handle(ex);
            }
        });

        app.MapPost("/api/drill/clock/advance", (Clock clock, DrillEngine engine, CommandEnvelope? body) =>
        {
            var minutes = body?.Minutes ?? body?.OverlapMinutes ?? 1;
            if (clock is MutableClock mutable)
            {
                mutable.Advance(TimeSpan.FromMinutes(minutes));
            }
            else
            {
                return Results.Json(new
                {
                    error = "当前使用系统时钟，不能推进；测试/演示使用 DRILL_MUTABLE_CLOCK=true"
                }, statusCode: StatusCodes.Status400BadRequest);
            }
            return Results.Ok(engine.BuildState());
        });

        app.MapPost("/api/drill/inject/instance-failure", (DrillEngine engine, CommandEnvelope? body) =>
        {
            if (string.IsNullOrWhiteSpace(body?.InstanceId))
            {
                return Results.Json(new { error = "需要 instanceId" }, statusCode: StatusCodes.Status400BadRequest);
            }
            engine.Ca.InjectInstanceFailure(body.InstanceId!);
            return Results.Ok(new { injected = body.InstanceId });
        });

        app.MapPost("/api/drill/inject/clear-failures", (DrillEngine engine) =>
        {
            engine.Ca.ClearInjectedFailures();
            return Results.Ok(new { cleared = true });
        });

        app.MapPost("/api/drill/inject/duplicate-serial", (DrillEngine engine) =>
        {
            engine.Ca.InjectDuplicateSerial();
            return Results.Ok(new { duplicateSerialInjected = true });
        });
    }
}
