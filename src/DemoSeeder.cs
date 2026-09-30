namespace CertRotationDrill;

public static class DemoSeeder
{
    public static readonly DateTimeOffset BaseTime =
        new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    public const string RotationOne = "rot-001";
    public const string RotationTwo = "rot-002";
    public const string NodeA = "edge-node-a";
    public const string NodeB = "edge-node-b";

    public static bool HasData(EventStore store) => store.LoadEvents().Count > 0;

    public static async Task SeedAsync(DrillEngine engine, MutableClock clock)
    {
        // rot-001：一次完整的黄金路径，结束后旧证书已安全退出，其证书在预置时间点仍有效，
        // 方便作为回滚时“上一个仍有效证书”的参照。
        clock.SetTo(BaseTime);
        await engine.StartRotationAsync(new StartRotationRequest(
            "seed-r1-propose", RotationOne, new[] { NodeA, NodeB }, "unanimous", 2, 60, 90));

        clock.Advance(TimeSpan.FromMinutes(10));
        await engine.IssueAsync(new IssueRequest("seed-r1-issue", 90));

        clock.Advance(TimeSpan.FromMinutes(5));
        await engine.DistributeAsync(new DistributeRequest("seed-r1-dist-a", NodeA));
        await engine.DistributeAsync(new DistributeRequest("seed-r1-dist-b", NodeB));
        await engine.AcknowledgeAsync(new AcknowledgeRequest("seed-r1-ack-a", NodeA, true));
        await engine.AcknowledgeAsync(new AcknowledgeRequest("seed-r1-ack-b", NodeB, true));

        clock.Advance(TimeSpan.FromMinutes(5));
        await engine.ActivateAsync(new ActivateRequest("seed-r1-activate", null));
        clock.Advance(TimeSpan.FromMinutes(60));
        await engine.RetireAsync(new RetireRequest("seed-r1-retire", null));
        await engine.CompleteAsync(new CompleteRequest("seed-r1-complete", null));

        // rot-002：预置一次“半完成分发”。节点 A 已接收并装载，节点 B 被注入失败。
        // 已装载计数 1/2，unanimous 阈值 2 未满足，因此不允许激活。
        clock.Advance(TimeSpan.FromHours(24));
        await engine.StartRotationAsync(new StartRotationRequest(
            "seed-r2-propose", RotationTwo, new[] { NodeA, NodeB }, "unanimous", 2, 60, 90));

        clock.Advance(TimeSpan.FromMinutes(10));
        await engine.IssueAsync(new IssueRequest("seed-r2-issue", 90));

        clock.Advance(TimeSpan.FromMinutes(5));
        await engine.DistributeAsync(new DistributeRequest("seed-r2-dist-a", NodeA));
        await engine.AcknowledgeAsync(new AcknowledgeRequest("seed-r2-ack-a", NodeA, true));

        engine.Ca.InjectInstanceFailure(NodeB);
        await engine.DistributeAsync(new DistributeRequest("seed-r2-dist-b-fail", NodeB));
    }
}
