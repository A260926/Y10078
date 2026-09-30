# 证书轮换演练器（Cert Rotation Drill）

一个**不连接真实证书中心（CA）**的边缘服务证书轮换演练台。内置确定性的假签发端、
加密的本地密钥槽、SQLite 事件库、可替换时钟、网页控制台和 JSON API，覆盖一次轮换的
申请 → 分发 → 双证书重叠 → 切换 → 撤回/回滚全流程，并支持进程崩溃后的事件恢复。

## 安装与运行

需要 .NET 10 SDK。

```bash
dotnet restore && dotnet build --no-restore
```

演示（测试 + 启动，页面地址 http://127.0.0.1:5208 ，页面标题含“证书轮换演练器”）：

```bash
dotnet test --no-build
DRILL_MUTABLE_CLOCK=true DRILL_SEED_DEMO=true \
  dotnet run --no-build --urls http://127.0.0.1:5208
```

> 演示使用可推进时钟并自动播种预置场景。所有时间都由“可替换时钟”给出，测试用脚本化
> `MutableClock`，**全程不使用任何等待/睡眠**。

### 配置项（环境变量或配置键）

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `DRILL_DATA_DIR` | `data` | SQLite 库与密钥文件目录 |
| `DRILL_DB` | `$DATA_DIR/drill.db` | SQLite 文件路径 |
| `DRILL_KEY_FILE` | `$DATA_DIR/keyslot.key` | 32 字节本地主密钥文件（自动生成，Unix 下 0600） |
| `DRILL_MUTABLE_CLOCK` | `false` | `true` 使用可推进/可恢复的演练时钟 |
| `DRILL_CLOCK_START` | `2026-09-01T00:00:00Z` | 可变时钟起点 |
| `DRILL_SEED_DEMO` | `false` | 首次启动且事件库为空时播种预置场景 |

## 预置场景（页面打开即见）

- `rot-001`：一次完整黄金路径，已安全完成旧证书退出；其证书在预置时间仍有效，
  作为“上一个仍有效证书”的回滚参照。
- `rot-002`：处于**半完成分发**——`edge-node-a` 已接收并装载，`edge-node-b` 被注入
  分发故障。装载计数 1/2，`unanimous` 阈值 2 未满足，界面明确显示“禁止激活”。

恢复操作路径（页面按钮即可完成）：

1. “清除注入故障”；
2. 对 `edge-node-b` 重试“分发”，再“确认已装载”——达到 2/2 阈值；
3. “按策略激活”，进入双证书重叠窗口；
4. “推进 60 分钟”到重叠窗口结束；
5. “退出旧证书” → “完成轮换”。

## 状态机

一次轮换 `rotation` 的阶段（全部来自事件重放）：

```
proposed ──issue──▶ issued ──所有实例投递成功──▶ distributed
  │                    │                             │
  │（注入重复序列号等）  │（人工标记失败）              ▼
 └──────────────────▶ failed ◀────────────────── active（新+旧双证书重叠）
                                                      │ 重叠窗口结束
                                                      ▼
                                                  retiring ──▶ completed

distributed/active/… 任一进行中阶段 ──rollback──▶ rolling_back ──▶ completed(rolledBack=true)
                                或无可恢复/旧证书已过期 ──▶ manual_action_required
```

- 终态：`completed`、`failed`、`manual_action_required`。
- `rolling_back` 是瞬时阶段：恢复成功后落到 `completed` 且 `rolledBack=true`；
  无法恢复时落到 `manual_action_required`（只给人工处理状态，**绝不假装恢复**）。
- 每个实例有独立状态：投递尝试次数、是否已接收（delivered/received）、是否已装载（loaded）。
- **激活策略**：必须全部实例投递成功，并且“已装载”计数达到阈值。
  - `quorum`：`floor(N/2)+1`；`unanimous`/`all`：`N`。阈值也可显式指定。
  - 未达阈值时 `activate` 返回 400（`threshold_not_met`），任何路径都不会把半完成轮换判成功。

### 统一的区间约定（闭/开一致）

全系统统一使用**左闭右开**半开区间：

- 证书有效期：`[NotBefore, NotAfter)`；
- 双证书重叠窗口：`[ActivatedAt, ActivatedAt + Overlap)`。

临界时刻语义一致：恰好在 `NotAfter` 或 `ActivatedAt+Overlap` 这一瞬间，旧证书/重叠期
已结束；在该时刻之前退出旧证书会被拒绝（`overlap_open`）。

## 秘密边界（Private Key Boundary）

- 假签发端生成的合成私钥仅作为字节交给 `KeySlot`，使用 **AES-GCM**（随机 12B nonce、
  16B tag）加密后写入 SQLite 的 `key_material` 表；主密钥保存在独立的本地密钥文件。
- 证书**描述**（`CertificateDescription`）只含序列号、Key ID、Subject、SAN、有效期、指纹，
  不含私钥，也没有任何解密 API。
- 网页、JSON API 响应、事件负载、控制台日志**都不会回显私钥**。日志只记录序列号与 key id。
- 测试 `Private_Key_Never_Appears_In_Api_Pages_Or_Database_Plaintext` 会扫描 API 文本、
  首页 HTML 以及 `.db`/`.db-wal` 原始字节，确保私钥串不以明文落盘。

## 崩溃恢复点（Crash Recovery）

状态采用**事件溯源**：

- 所有状态变化都作为不可变事件写入 `events`（全局 `seq`，每轮换内单调 `version`）。
- 命令在单个 SQLite 事务内完成“写事件 + 写幂等记录 + 写序列号/密钥材料”，提交前崩溃
  整体回滚，提交后从事件重放即可还原。
- 典型演练点：进程在“写实例确认”与“汇总激活”之间退出。重启后聚合由事件重建，
  已装载计数如实恢复为 1/2，阈值仍不满足，**不会直接判成功**（见
  `Below_Threshold_Never_Activates_Even_After_Restart`）。
- 可变时钟推进也持久化（`kv.clock.now`），重启后时钟、证书有效期、重叠窗口一致恢复。
- 已签发序列号持久化于 `issued_serials`（主键唯一），重启后假 CA 计数器从历史继续，
  不会重号。

## 幂等与并发

- 每个写命令必须带幂等键（请求头 `Idempotency-Key` 或 body 字段 `idempotencyKey`）。
- 服务端对幂等键保存请求体哈希：相同键 + 相同请求 → 重放，不重复签发、不重复记确认；
  相同键 + 不同请求体 → 409 `idempotency_key_mismatch`。
- 推进类命令可带 `expectedVersion`（轮换当前版本号）。两个操作者同时推进时，
  旧版本号请求得到 **409 `version_conflict`，响应体里带完整 `serverState`**，
  调用方据此刷新后重试。
- 失败分发/确认按业务幂等：已成功投递的实例再次投递不新增事件；已确认的实例重复确认
  不新增确认事件。

## 半完成分发不污染下一次轮换

- 每个轮换在 `proposed` 时拥有全新的实例进度集合；旧轮换的投递/确认不继承。
- 假 CA 支持故障注入：
  - 注入某实例下一次分发失败（`/api/drill/inject/instance-failure`）；
  - 注入“下一次签发序列号重复”（`/api/drill/inject/duplicate-serial`），服务端依据
    唯一约束拒绝并使该轮换进入 `failed`（事件 `IssueRejectedByCa`），下一次签发得到新号。
- 只有在上一个轮换到达终态后才能发起下一次轮换，从源头隔离半完成分发。

## JSON API

| 方法/路径 | 说明 |
| --- | --- |
| `GET /api/state` | 当前活动证书、候选证书、各实例确认、策略判定、全部轮换与完整事件序列 |
| `POST /api/rotations` | 发起轮换（进入 proposed） |
| `POST /api/rotations/issue` | 假 CA 签发描述证书（私钥入加密密钥槽） |
| `POST /api/rotations/distribute` | 向某实例分发（注入失败时记录失败事件） |
| `POST /api/rotations/acknowledge` | 实例确认 `{instanceId, loaded}`，记录已接收/已装载 |
| `POST /api/rotations/activate` | 阈值达标后激活，开启半开重叠窗口 |
| `POST /api/rotations/retire` | 重叠窗口结束后退出旧证书 |
| `POST /api/rotations/complete` | 完成轮换 |
| `POST /api/rotations/rollback` | 回滚到上一个仍有效证书；无可用/已过期 → 人工处理 |
| `POST /api/rotations/fail` | 演练用：人工标记失败 |
| `POST /api/drill/clock/advance` | 推进可变时钟 `{minutes}` |
| `POST /api/drill/inject/instance-failure` | 注入实例分发失败 `{instanceId}` |
| `POST /api/drill/inject/clear-failures` | 清除注入的分发故障 |
| `POST /api/drill/inject/duplicate-serial` | 注入下一次序列号重复 |

错误：业务规则违反返回 400 与稳定错误码（如 `threshold_not_met`、`overlap_open`、
`not_delivered`）；版本冲突返回 409 且包含 `serverState`。

## 代码结构

- `src/Clock.cs`：`Clock` 抽象与可持久化 `MutableClock`。
- `src/FakeCa.cs` / `src/Certificate.cs`：确定性假签发端与证书描述（序列号/Key ID/SAN/有效期/指纹）。
- `src/KeySlot.cs`：AES-GCM 加密密钥槽（无解密出口）。
- `src/Events.cs`：不可变事件定义。
- `src/EventStore.cs`：SQLite schema、事件追加/重放、幂等表、序列号唯一表、kv。
- `src/RotationState.cs`：聚合与事件折叠（状态机规则所在）。
- `src/DrillEngine.cs`：命令处理、策略阈值、区间判定、回滚/人工处理、版本号与快照。
- `src/DemoSeeder.cs`：预置场景。
- `src/Endpoints.cs` / `src/Program.cs`：JSON API、DI、静态网页与启动播种。
- `wwwroot/`：单页控制台（活动/候选证书、实例确认、策略判定、事件序列、操作与注入）。
- `tests/`：xUnit + `WebApplicationFactory`，每个用例独立临时 SQLite 目录。

## 测试覆盖（18 个，无等待）

黄金路径与半开区间边界、证书描述字段、阈值不足禁止激活（含崩溃恢复）、恢复后达阈值、
幂等不重复签发/确认、幂等键冲突、半完成分发不污染新轮换、注入重复序列号、失败恢复到完成、
回滚恢复有效旧证书、过期旧证书仅人工处理、首个轮换回滚人工处理、版本冲突含服务器状态、
私钥不出 API/页面/数据库明文、首页标题、预置场景恢复。
