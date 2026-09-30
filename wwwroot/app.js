let state = null;

function uuid() {
  if (window.crypto?.randomUUID) return window.crypto.randomUUID();
  return "idem-" + Date.now() + "-" + Math.random().toString(16).slice(2);
}

async function post(path, body) {
  const idem = uuid();
  const res = await fetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json", "Idempotency-Key": idem },
    body: JSON.stringify(body || {})
  });
  const data = await res.json();
  if (!res.ok) {
    const err = new Error(data.error || ("HTTP " + res.status));
    err.payload = data;
    throw err;
  }
  return data;
}

function fmtDate(value) {
  if (!value) return "—";
  return new Date(value).toISOString();
}

function certHtml(cert, activeSerial) {
  if (!cert) return '<div class="cert empty">暂无</div>';
  const now = state ? new Date(state.now) : null;
  const valid = now && new Date(cert.notBefore) <= now && now < new Date(cert.notAfter);
  const cls = valid ? "valid" : "invalid";
  const label = valid ? "当前有效" : "当前不在有效期（[notBefore,notAfter) 半开区间）";
  return `
    <div class="cert">
      <div class="row"><span class="k ${cls}">有效性</span><span class="v ${cls}">${label}</span></div>
      <div class="row"><span class="k">序列号</span><span class="v">${cert.serialNumber}</span></div>
      <div class="row"><span class="k">Key ID</span><span class="v">${cert.keyId}</span></div>
      <div class="row"><span class="k">Subject</span><span class="v">${cert.subject}</span></div>
      <div class="row"><span class="k">SAN</span><span class="v">${(cert.subjectAlternativeNames || []).join(", ")}</span></div>
      <div class="row"><span class="k">有效期</span><span class="v">[${fmtDate(cert.notBefore)} , ${fmtDate(cert.notAfter)})</span></div>
      <div class="row"><span class="k">指纹</span><span class="v">${cert.thumbprint}</span></div>
    </div>`;
}

function renderCertificates() {
  document.getElementById("active-cert").innerHTML = certHtml(state.activeCertificate);
  const current = state.currentRotation;
  document.getElementById("candidate-cert").innerHTML = certHtml(current?.candidate || null);
}

function renderRotation() {
  const current = state.currentRotation;
  const badge = document.getElementById("phase-badge");
  const meta = document.getElementById("rotation-meta");
  const policy = document.getElementById("policy-judgement");
  const manual = document.getElementById("manual-banner");
  const failure = document.getElementById("failure-banner");

  manual.classList.add("hidden");
  failure.classList.add("hidden");

  if (!current) {
    badge.textContent = "无进行中轮换";
    badge.className = "badge";
    meta.innerHTML = '<p style="color:var(--muted)">没有进行中的轮换，可在下方发起新的一次。</p>';
    policy.innerHTML = "";
    setActionsForRotation(null);
    return;
  }

  badge.textContent = current.phase;
  badge.className = "badge " + current.phase;

  let prevLine = "无";
  if (current.previousCertificate) {
    const validity = current.previousCurrentlyValid
      ? '<span class="tag yes">仍有效可回滚</span>'
      : '<span class="tag err">已过期，只能人工处理</span>';
    prevLine = `${current.previousCertificate.serialNumber} ${validity}`;
  }

  meta.innerHTML = `
    <div class="cert" style="margin-top:8px">
      <div class="row"><span class="k">轮换标识</span><span class="v">${current.rotationId}（版本 ${current.version}）</span></div>
      <div class="row"><span class="k">目标实例</span><span class="v">${current.instances.join(", ")}</span></div>
      <div class="row"><span class="k">激活策略</span><span class="v">${current.policyMode}，阈值 ${current.threshold}（${current.instances.length} 个实例）</span></div>
      <div class="row"><span class="k">重叠窗口</span><span class="v">${current.overlapMinutes} 分钟，半开区间 [激活时刻, 退出时刻)</span></div>
      <div class="row"><span class="k">上一证书</span><span class="v">${prevLine}</span></div>
      ${current.activatedAt ? `<div class="row"><span class="k">激活时刻</span><span class="v">${fmtDate(current.activatedAt)}</span></div>` : ""}
      ${current.retireNotBefore ? `<div class="row"><span class="k">允许退出</span><span class="v">>= ${fmtDate(current.retireNotBefore)}</span></div>` : ""}
    </div>`;

  const delivery = current.allDelivered
    ? '<span class="met">全部实例已接收证书包</span>'
    : '<span class="unmet">仍有实例未收到证书包</span>';
  const threshold = current.thresholdMet
    ? `<span class="met">已装载 ${current.loadedCount}/${current.instances.length}，满足阈值 ${current.threshold}</span>`
    : `<span class="unmet">已装载 ${current.loadedCount}/${current.instances.length}，未达阈值 ${current.threshold}（禁止激活/禁止判成功）</span>`;
  const activate = current.canActivate
    ? '<span class="met">→ 可以激活</span>'
    : '<span class="unmet">→ 尚不能激活</span>';
  const retire = current.canRetire
    ? '<span class="met">重叠窗口已结束，可以退出旧证书</span>'
    : (current.phase === "active"
      ? '<span class="unmet">仍在双证书重叠窗口内，保持新旧并存</span>'
      : "");

  policy.innerHTML = `
    <div>分发条件：${delivery}</div>
    <div>装载阈值：${threshold}</div>
    <div>激活判定：${activate}</div>
    ${retire ? `<div>旧证书退出：${retire}</div>` : ""}`;

  if (current.needsManualAction) {
    manual.textContent = "需要人工处理：" + (current.manualReason || "旧证书无法自动恢复");
    manual.classList.remove("hidden");
  }
  if (current.phase === "failed") {
    failure.textContent = `轮换失败（${current.failedAtPhase || "?"}）：${current.failureReason || ""}`;
    failure.classList.remove("hidden");
  }

  setActionsForRotation(current);
}

function setActionsForRotation(current) {
  const phase = current?.phase;
  const inDistribute = phase === "issued" || phase === "distributed";
  document.getElementById("btn-distribute-all").disabled = !inDistribute;
  document.getElementById("btn-activate").disabled = !current?.canActivate;
  document.getElementById("btn-retire").disabled = !current?.canRetire;
  document.getElementById("btn-complete").disabled = phase !== "retiring";
  document.getElementById("btn-rollback").disabled = !current?.canRollback;
  document.getElementById("btn-fail").disabled = !current;
}

function renderInstances() {
  const current = state.currentRotation;
  const tbody = document.getElementById("instance-rows");
  if (!current) {
    tbody.innerHTML = '<tr><td colspan="6" style="color:var(--muted)">无进行中轮换</td></tr>';
    return;
  }

  tbody.innerHTML = current.instancesProgress.map((p) => {
    const delivered = p.delivered
      ? '<span class="tag yes">已接收</span>'
      : '<span class="tag no">未接收</span>';
    const loaded = p.loaded
      ? '<span class="tag yes">已装载</span>'
      : '<span class="tag no">未装载</span>';
    const error = p.lastDeliveryError
      ? `<span class="tag err">${p.lastDeliveryError}（尝试 ${p.deliveryAttempts} 次）</span>`
      : "—";
    const canAck = p.delivered && !p.loaded && (current.phase === "issued" || current.phase === "distributed");
    const canRetry = !p.delivered && (current.phase === "issued" || current.phase === "distributed");
    return `<tr>
      <td>${p.instanceId}</td>
      <td>${p.deliveryAttempts}</td>
      <td>${delivered}<div style="color:var(--muted)">${p.receivedAt ? fmtDate(p.receivedAt) : ""}</div></td>
      <td>${loaded}<div style="color:var(--muted)">${p.loadedAt ? fmtDate(p.loadedAt) : ""}</div></td>
      <td>${error}</td>
      <td>
        ${canRetry ? `<button data-action="retry" data-instance="${p.instanceId}">重试分发</button>` : ""}
        ${canAck ? `<button data-action="ack" data-instance="${p.instanceId}">确认已装载</button>` : ""}
      </td>
    </tr>`;
  }).join("");

  tbody.querySelectorAll("button").forEach((btn) => {
    btn.addEventListener("click", () => onInstanceAction(btn.dataset.action, btn.dataset.instance));
  });
}

function renderEvents() {
  const rows = state.events.map((e) => {
    const detail = JSON.stringify(e.payload, null, 0);
    return `<tr>
      <td>${e.seq}</td>
      <td>${e.rotationId}</td>
      <td>${e.version}</td>
      <td>${fmtDate(e.occurredAt)}</td>
      <td>${e.type}</td>
      <td class="detail">${detail.replace(/</g, "&lt;")}</td>
      <td>${e.idempotencyKey.startsWith("seed-") ? e.idempotencyKey : e.idempotencyKey.slice(0, 8) + "…"}</td>
    </tr>`;
  }).join("");
  document.getElementById("event-rows").innerHTML = rows;
}

function render() {
  document.getElementById("clock-now").textContent = new Date(state.now).toISOString();
  document.getElementById("clock-type").textContent = state.clockType === "mutable" ? "可替换/可推进" : "系统时钟";
  renderCertificates();
  renderRotation();
  renderInstances();
  renderEvents();
}

async function refresh() {
  const res = await fetch("/api/state");
  state = await res.json();
  render();
}

function showResult(data, error) {
  const box = document.getElementById("action-result");
  box.classList.remove("hidden");
  if (error) {
    box.textContent = "❌ " + data.error + (data.code ? "（" + data.code + "）" : "");
    if (data.serverState) {
      box.textContent += "\n\n服务器当前状态版本：" +
        (data.serverState.currentRotation?.rotationId || "-") +
        " v" + (data.serverState.currentRotation?.version ?? "-");
    }
  } else {
    box.textContent = "✅ 已接受，最新阶段：" + (data.currentRotation?.phase || "（无进行中轮换）");
  }
}

async function guarded(fn) {
  try {
    const next = await fn();
    if (next) state = next;
    render();
    showResult(next || state, false);
  } catch (err) {
    showResult(err.payload || { error: err.message }, true);
    await refresh();
  }
}

async function distributeAll() {
  const current = state.currentRotation;
  if (!current) return;
  for (const p of current.instancesProgress) {
    if (!p.delivered) {
      await post("/api/rotations/distribute", { instanceId: p.instanceId });
    }
  }
}

async function onInstanceAction(action, instanceId) {
  await guarded(async () => {
    if (action === "retry") {
      return post("/api/rotations/distribute", { instanceId });
    }
    if (action === "ack") {
      return post("/api/rotations/acknowledge", { instanceId, loaded: true });
    }
  });
}

function bind() {
  document.getElementById("btn-advance").addEventListener("click", () =>
    guarded(() => post("/api/drill/clock/advance", { minutes: 60 })));

  document.getElementById("btn-distribute-all").addEventListener("click", async () => {
    await guarded(async () => {
      await distributeAll();
      return state;
    });
  });

  document.getElementById("btn-activate").addEventListener("click", () =>
    guarded(() => post("/api/rotations/activate", { expectedVersion: state.currentRotation?.version })));
  document.getElementById("btn-retire").addEventListener("click", () =>
    guarded(() => post("/api/rotations/retire", { expectedVersion: state.currentRotation?.version })));
  document.getElementById("btn-complete").addEventListener("click", () =>
    guarded(() => post("/api/rotations/complete", { expectedVersion: state.currentRotation?.version })));
  document.getElementById("btn-rollback").addEventListener("click", () =>
    guarded(() => post("/api/rotations/rollback", { expectedVersion: state.currentRotation?.version })));
  document.getElementById("btn-fail").addEventListener("click", () =>
    guarded(() => post("/api/rotations/fail", { expectedVersion: state.currentRotation?.version })));

  document.getElementById("btn-inject-fail").addEventListener("click", () =>
    guarded(async () => {
      await post("/api/drill/inject/instance-failure", { instanceId: "edge-node-b" });
      return state;
    }));
  document.getElementById("btn-clear-fail").addEventListener("click", () =>
    guarded(async () => {
      await post("/api/drill/inject/clear-failures", {});
      return state;
    }));
  document.getElementById("btn-inject-dup").addEventListener("click", () =>
    guarded(async () => {
      await post("/api/drill/inject/duplicate-serial", {});
      return state;
    }));

  document.getElementById("btn-start").addEventListener("click", () =>
    guarded(async () => {
      const instances = document.getElementById("new-instances").value
        .split(",").map((s) => s.trim()).filter(Boolean);
      return post("/api/rotations", {
        rotationId: document.getElementById("new-id").value || null,
        instances: instances.length ? instances : null,
        policyMode: document.getElementById("new-policy").value,
        overlapMinutes: Number(document.getElementById("new-overlap").value),
        lifetimeDays: Number(document.getElementById("new-lifetime").value)
      });
    }));
}

bind();
refresh();
