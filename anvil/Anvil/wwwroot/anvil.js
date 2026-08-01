"use strict";

// Plain browser JavaScript by design: Anvil remains a zero-build, offline-friendly lab cockpit.
const api = "anvil/api";
const pending = new Set();
const workerCards = new Map();
const localActivity = [];
const el = (id) => document.getElementById(id);
const cssVar = (name, fallback) => getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;

let selectedWorkload = "noOp";
let selectedActivityFilter = "important";
let currentState = null;
let previousHealthyState = null;
let lastServerEvents = [];
let backendUnavailable = false;
let databaseUnavailable = false;
let pollInFlight = false;
let pollTimer = null;
let activitySequence = 0;

const WORKLOADS = {
  noOp: {
    label: "No-op throughput",
    description: "The handler performs no application work. This primarily exposes Acta, database, serialization, claim, execution, and completion overhead.",
    loads: [10000, 100000, 1000000],
    defaultLoad: 100000,
  },
  steady: {
    label: "Steady",
    description: "Small asynchronous jobs model ordinary I/O-shaped application work.",
    loads: [1000, 10000, 100000],
    defaultLoad: 10000,
  },
  crashRecovery: {
    label: "Crash recovery",
    description: "Slow durable jobs remain active long enough to crash a worker and observe lease recovery.",
    loads: [100, 1000, 10000],
    defaultLoad: 1000,
  },
  retryAndFailure: {
    label: "Retry and failure",
    description: "Most jobs fail once and recover. A smaller set exhausts retries and becomes terminally failed.",
    loads: [1000, 10000, 100000],
    defaultLoad: 10000,
  },
  fanOut: {
    label: "Fan-out",
    description: "Each parent job starts five children and durably joins on all of them, demoing parent/child job lineage.",
    loads: [10, 100, 1000],
    defaultLoad: 100,
  },
};

const number = (value) => Number(value || 0).toLocaleString();
const time = (iso) => new Date(iso).toLocaleTimeString([], { hour12: false });
const dateTime = (iso) => iso ? new Date(iso).toLocaleString() : "·";
const capitalize = (value) => value ? value[0].toUpperCase() + value.slice(1) : "·";
const flash = (text) => { el("flash").textContent = text || ""; };

function ago(iso) {
  if (!iso) return "·";
  const seconds = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  return seconds < 60 ? `${seconds} second${seconds === 1 ? "" : "s"} ago` : `${Math.round(seconds / 60)} minutes ago`;
}

function formatDuration(seconds) {
  if (seconds == null) return "·";
  if (seconds >= 120) return seconds % 60 === 0 ? `${seconds / 60}m` : `${(seconds / 60).toFixed(1)}m`;
  return `${seconds}s`;
}

function addActivity(text, categories = ["important"], at = new Date().toISOString()) {
  localActivity.unshift({ id: ++activitySequence, timeUtc: at, text, categories });
  if (localActivity.length > 200) localActivity.length = 200;
}

function selectWorkload(workload) {
  selectedWorkload = workload;
  const config = WORKLOADS[workload];
  document.querySelectorAll("#workload-seg [data-workload]").forEach((button) => {
    const active = button.dataset.workload === workload;
    button.classList.toggle("on", active);
    button.setAttribute("aria-pressed", String(active));
  });
  el("workload-hint").textContent = config.description;

  const load = el("sel-load");
  load.replaceChildren(...config.loads.map((value) => {
    const option = document.createElement("option");
    option.value = String(value);
    option.textContent = value >= 1000000 ? "1,000,000" : number(value);
    option.selected = value === config.defaultLoad;
    return option;
  }));
  renderRunReadout();
}

function renderRunReadout() {
  const config = WORKLOADS[selectedWorkload];
  el("run-readout").innerHTML =
    `<div><span class="rk">Workload</span><b>${config.label}</b></div>` +
    `<div><span class="rk">Jobs</span><b>${number(el("sel-load").value)}</b></div>` +
    `<div><span class="rk">Processes</span><b>${number(el("sel-workers").value)}</b></div>` +
    `<div><span class="rk">Worker preset</span><b>4 executors · Direct</b></div>`;
}

function buildRunSpec() {
  return {
    workload: selectedWorkload,
    load: Number(el("sel-load").value),
    workerCount: Number(el("sel-workers").value),
  };
}

async function call(key, method, path, label, body) {
  if (pending.has(key)) return null;
  pending.add(key);
  flash(`${label}…`);
  updatePendingControls();
  try {
    const options = { method };
    if (body !== undefined) {
      options.headers = { "Content-Type": "application/json" };
      options.body = JSON.stringify(body);
    }
    const response = await fetch(api + path, options);
    const data = await response.json().catch(() => null);
    if (!response.ok) {
      flash((data && (data.message || data.title)) || "Request failed.");
      return null;
    }
    flash((data && data.message) || `${label} accepted.`);
    return data;
  } catch (error) {
    flash(`Network error: ${error.message}`);
    return null;
  } finally {
    pending.delete(key);
    updatePendingControls();
  }
}

function updatePendingControls() {
  const seeding = currentState && currentState.seeding && currentState.seeding.active;
  const ready = currentState && currentState.ready && !currentState.dbError;
  el("btn-run").disabled = pending.has("run") || seeding || !ready;
  el("btn-spawn").disabled = pending.has("spawn");
  el("btn-fault-crashes").disabled = pending.has("fault:crashes");
  el("btn-fault-pressure").disabled = pending.has("fault:pressure");
  const pressureActive = !!(currentState && currentState.faults && currentState.faults.queuePressureActive);
  el("sel-pressure-rate").disabled = pressureActive || pending.has("fault:pressure");
  el("btn-fault-outbox").disabled = pending.has("fault:outbox");
  const outboxActive = !!(currentState && currentState.faults && currentState.faults.outboxPressureActive);
  el("sel-outbox-rate").disabled = outboxActive || pending.has("fault:outbox");
  workerCards.forEach((card) => updateWorkerActions(card));
}

function drawScope(canvas, values, color) {
  const context = canvas.getContext("2d");
  const width = canvas.width;
  const height = canvas.height;
  context.clearRect(0, 0, width, height);
  context.strokeStyle = cssVar("--grid", "rgba(36,86,214,0.1)");
  context.lineWidth = 1;
  for (let x = 0; x <= width; x += width / 8) {
    context.beginPath(); context.moveTo(x, 0); context.lineTo(x, height); context.stroke();
  }
  for (let y = 0; y <= height; y += height / 4) {
    context.beginPath(); context.moveTo(0, y); context.lineTo(width, y); context.stroke();
  }
  if (!values.length) return;

  const max = Math.max(1, ...values);
  const padding = 6;
  const step = width / Math.max(1, values.length - 1);
  context.beginPath();
  values.forEach((value, index) => {
    const x = index * step;
    const y = height - padding - (value / max) * (height - padding * 2);
    index === 0 ? context.moveTo(x, y) : context.lineTo(x, y);
  });
  context.strokeStyle = color;
  context.lineWidth = 1.8;
  context.stroke();
}

function renderScopes(telemetry) {
  const series = (telemetry && telemetry.series) || [];
  const rates = [];
  for (let i = 1; i < series.length; i++) {
    const elapsed = (new Date(series[i].timeUtc) - new Date(series[i - 1].timeUtc)) / 1000;
    rates.push(elapsed > 0 ? Math.max(0, (series[i].done - series[i - 1].done) / elapsed) : 0);
  }
  const accent = cssVar("--accent", "#2456d6");
  drawScope(el("scope-throughput"), rates, accent);
  drawScope(el("scope-queue"), series.map((point) => point.ready), cssVar("--muted", "#67718a"));
  drawScope(el("scope-exec"), series.map((point) => point.executing), accent);
  el("rd-throughput").textContent = number(Math.round((telemetry && telemetry.donePerSecond) || 0));
  el("rd-queue").textContent = number(series.length ? series[series.length - 1].ready : 0);
  el("rd-exec").textContent = number(series.length ? series[series.length - 1].executing : 0);
}

function countCard(label, value, note = "", style = "") {
  return `<div class="count ${style}"><div class="k">${label}</div><div class="v">${number(value)}${note ? `<span class="note"> ${note}</span>` : ""}</div></div>`;
}

function renderCounts(counts) {
  // Healthy means failed EXACTLY matches expected: a probe that should have failed but did not is
  // as wrong as an unexpected failure. Expected failures are counted upfront at seed time while
  // actual failures accrue as jobs run, so a shortfall only reads bad once the queue has settled.
  const settled = counts.ready === 0 && counts.executing === 0;
  const failureStyle =
    counts.failed > counts.expectedFailed ? "bad"
    : counts.failed < counts.expectedFailed ? (settled ? "bad" : "")
    : counts.expectedFailed > 0 ? "expected" : "";
  const expected = `/ ${number(counts.expectedFailed)} expected`;
  el("counts").innerHTML = [
    countCard("Total", counts.total),
    countCard("Ready", counts.ready),
    countCard("Executing", counts.executing, "", "exec"),
    countCard("Done", counts.done, "", "ok"),
    countCard("Failed", counts.failed, expected, failureStyle),
  ].join("");
}

function renderMetrics(state) {
  renderScopes(state.telemetry);
  renderCounts(state.counts);
}

function renderProvider(provider) {
  const value = (provider || "").toLowerCase();
  el("provider-current").textContent = value.startsWith("sqlite") ? "SQLite" : value === "mssql" || value === "sqlserver" ? "SQL Server" : "PostgreSQL";
}

function setLed(id, state) {
  const node = el(id);
  node.classList.remove("on", "warn", "bad");
  if (state) node.classList.add(state);
}

function renderLeds(state) {
  const summary = state.workerSummary || {};
  const activeWorkers = (summary.healthy || 0) + (summary.starting || 0) + (summary.draining || 0);
  const readyState = state.dbError ? "bad" : state.seeding.active || summary.starting > 0 ? "warn" : state.ready ? "on" : "";
  setLed("led-ready", readyState);
  setLed("led-workers", activeWorkers > 0 ? "on" : "bad");
  el("led-workers-v").textContent = number(activeWorkers);
}

function renderSeeding(seed) {
  const box = el("seeding");
  if (!seed || seed.target <= 0) {
    box.hidden = true;
    el("center-seeding").hidden = true;
    return;
  }
  box.hidden = false;
  const percent = Math.min(100, seed.target ? seed.processed / seed.target * 100 : 0);
  el("seeding-fill").style.width = `${percent}%`;
  el("seeding-v").textContent = `${number(seed.processed)} / ${number(seed.target)}${seed.active ? " …" : seed.error ? " ERROR" : " ✓"}`;
  el("seeding-rate").textContent = `${number(Math.round(seed.perSecond || 0))} jobs/s`;
  el("seeding-detail").textContent = seed.error
    ? seed.error
    : `${number(seed.inserted)} inserted${seed.deduplicated ? ` · ${number(seed.deduplicated)} already existed` : ""}`;
  const center = el("center-seeding");
  center.hidden = !seed.active;
  center.textContent = seed.active ? `Seeding ${number(seed.processed)} of ${number(seed.target)} · ${number(Math.round(seed.perSecond || 0))}/s` : "";
}

function renderFaults(faults) {
  const crashes = el("btn-fault-crashes");
  crashes.textContent = faults.continuousCrashesActive ? "STOP" : "START";
  el("fault-crashes-status").textContent = faults.continuousCrashesActive
    ? `Running · ${number(faults.workersCrashed)} workers crashed`
    : faults.workersCrashed ? `${number(faults.workersCrashed)} workers crashed` : "Stopped";

  const pressure = el("btn-fault-pressure");
  pressure.textContent = faults.queuePressureActive ? "STOP" : "START";
  el("fault-pressure-status").textContent = faults.queuePressureActive
    ? `Running · ${number(faults.pressureJobsAdded)} jobs added`
    : faults.pressureJobsAdded ? `${number(faults.pressureJobsAdded)} jobs added` : "Stopped";
  el("sel-pressure-rate").disabled = faults.queuePressureActive || pending.has("fault:pressure");

  const outbox = el("btn-fault-outbox");
  outbox.textContent = faults.outboxPressureActive ? "STOP" : "START";
  el("fault-outbox-status").textContent = faults.outboxPressureActive
    ? `Running · ${number(faults.outboxRowsStaged)} rows staged`
    : faults.outboxRowsStaged ? `${number(faults.outboxRowsStaged)} rows staged` : "Stopped";
  el("sel-outbox-rate").disabled = faults.outboxPressureActive || pending.has("fault:outbox");
  const backlog = currentState && currentState.outbox;
  el("outbox-backlog").hidden = !backlog || (!backlog.pending && !backlog.quarantined && !faults.outboxRowsStaged);
  el("outbox-backlog").textContent = backlog
    ? `Source backlog: ${number(backlog.pending)} pending · ${number(backlog.quarantined)} quarantined`
    : "";

  el("fault-error").hidden = !faults.lastError;
  el("fault-error").textContent = faults.lastError || "";
}

function renderBeats(beats) {
  el("beats").textContent = beats
    ? `heartbeat ${formatDuration(beats.heartbeatSeconds)} · worker dead ${formatDuration(beats.deadAfterSeconds)} · lease ${formatDuration(beats.leaseTtlSeconds)}`
    : "";
}

function renderWorkerSummary(summary) {
  const labels = [
    ["healthy", summary.healthy], ["starting", summary.starting], ["draining", summary.draining],
    ["awaiting recovery", summary.awaitingRecovery], ["recovered", summary.recovered],
    ["stopped", summary.stopped], ["external", summary.external],
  ].filter(([, count]) => count > 0).map(([label, count]) => `${number(count)} ${label}`);
  el("rack-status").textContent = labels.join(" · ") || "No workers";
}

function createDetailRow(term) {
  const dt = document.createElement("dt");
  dt.textContent = term;
  const dd = document.createElement("dd");
  return { dt, dd };
}

function createWorkerCard(key) {
  const root = document.createElement("article");
  const head = document.createElement("div");
  head.className = "worker-head";
  const name = document.createElement("div");
  name.className = "worker-name";
  const badge = document.createElement("span");
  badge.className = "badge worker-state";
  head.append(name, badge);

  const message = document.createElement("p");
  message.className = "worker-message";
  const heartbeat = document.createElement("p");
  heartbeat.className = "worker-meta";
  const recovery = document.createElement("p");
  recovery.className = "worker-recovery";

  const details = document.createElement("details");
  details.className = "worker-details";
  const summary = document.createElement("summary");
  summary.textContent = "Technical details";
  const list = document.createElement("dl");
  const rows = {
    process: createDetailRow("Process"), database: createDetailRow("Database"), pid: createDetailRow("PID"),
    exitCode: createDetailRow("Exit code"), heartbeat: createDetailRow("Exact heartbeat"),
    exited: createDetailRow("Exited"), error: createDetailRow("Last error"),
  };
  Object.values(rows).forEach((row) => list.append(row.dt, row.dd));
  details.append(summary, list);

  const actions = document.createElement("div");
  actions.className = "worker-actions";
  const drain = document.createElement("button");
  drain.className = "drain-btn";
  drain.textContent = "DRAIN";
  drain.dataset.workerAction = "drain";
  const crash = document.createElement("button");
  crash.className = "danger crash-btn";
  crash.textContent = "CRASH";
  crash.dataset.workerAction = "crash";
  actions.append(drain, crash);
  root.append(head, heartbeat, message, recovery, actions, details);

  const card = { key, root, name, badge, message, heartbeat, recovery, details, rows, actions, drain, crash, worker: null };
  workerCards.set(key, card);
  el("workers").append(root);
  return card;
}

function workerKey(worker) {
  return worker.managed ? `managed:${worker.id}` : `external:${worker.name}`;
}

function updateWorkerActions(card) {
  const worker = card.worker;
  if (!worker) return;
  const crashKey = `crash:${worker.id}`;
  const drainKey = `drain:${worker.id}`;
  card.crash.hidden = !worker.canCrash;
  card.drain.hidden = !worker.canDrain;
  card.crash.disabled = pending.has(crashKey);
  card.drain.disabled = pending.has(drainKey);
  card.crash.dataset.actionKey = crashKey;
  card.drain.dataset.actionKey = drainKey;
}

function updateWorkerCard(card, worker) {
  card.worker = worker;
  card.root.className = `worker ${worker.displayState}`;
  card.name.textContent = worker.name;
  card.badge.className = `badge worker-state ${worker.displayState}`;
  card.badge.textContent = worker.displayTitle;
  card.heartbeat.textContent = worker.lastSeenAtUtc
    ? `Heartbeat ${ago(worker.lastSeenAtUtc)}${worker.pid ? ` · PID ${worker.pid}` : ""}`
    : worker.pid ? `PID ${worker.pid}` : "No heartbeat registered";
  card.message.textContent = worker.displayMessage;
  card.recovery.hidden = worker.approximateRecoveryRemainingSeconds == null;
  card.recovery.textContent = worker.approximateRecoveryRemainingSeconds == null
    ? ""
    : `Recovery is expected in approximately ${formatDuration(worker.approximateRecoveryRemainingSeconds)}.`;

  card.rows.process.dd.textContent = capitalize(worker.processStatus);
  card.rows.database.dd.textContent = capitalize(worker.databaseStatus);
  card.rows.pid.dd.textContent = worker.pid == null ? "·" : String(worker.pid);
  card.rows.exitCode.dd.textContent = worker.exitCode == null ? "·" : String(worker.exitCode);
  card.rows.heartbeat.dd.textContent = dateTime(worker.lastSeenAtUtc);
  card.rows.exited.dd.textContent = dateTime(worker.processExitedAtUtc);
  card.rows.error.dd.textContent = worker.lastErrorLine || "·";
  updateWorkerActions(card);
}

function renderWorkers(workers) {
  const current = new Set();
  workers.forEach((worker) => {
    const key = workerKey(worker);
    current.add(key);
    const card = workerCards.get(key) || createWorkerCard(key);
    updateWorkerCard(card, worker);
  });
  workerCards.forEach((card, key) => {
    if (!current.has(key)) {
      card.root.remove();
      workerCards.delete(key);
    }
  });

  let empty = el("rack-empty");
  if (!workers.length && !empty) {
    empty = document.createElement("p");
    empty.id = "rack-empty";
    empty.className = "rack-empty";
    empty.textContent = "No workers. Spawn one to begin.";
    el("workers").append(empty);
  } else if (workers.length && empty) {
    empty.remove();
  }
}

function eventIsFailure(event) {
  return /fail/i.test(event.executionStatus || "") || /fail/i.test(event.toStatus || "") || /retry|lease.?expir|orphan/i.test(event.reason || "");
}

function eventIsWorker(event) {
  return /worker/i.test(event.eventCode || "") || !!event.workerName;
}

function eventIsImportant(event) {
  return eventIsFailure(event) || /worker|cancel|dead|reclaim/i.test(event.eventCode || "") || /lease.?expir|orphan|retry/i.test(event.reason || "");
}

function humanEventCode(code) {
  return String(code || "Activity").replace(/([a-z])([A-Z])/g, "$1 $2").replace(/^Job /, "");
}

function serverEventText(event) {
  const pieces = [humanEventCode(event.eventCode)];
  if (event.workerName) pieces.push(event.workerName);
  if (event.fromStatus && event.toStatus) pieces.push(`${event.fromStatus} → ${event.toStatus}`);
  else if (event.toStatus) pieces.push(event.toStatus);
  if (event.reason) pieces.push(event.reason);
  if (event.durationMs != null) pieces.push(`${event.durationMs}ms`);
  return pieces.join(" · ");
}

function renderActivity(events) {
  const box = el("events");
  const nearTop = box.scrollTop < 24;
  const previousScroll = box.scrollTop;
  const entries = localActivity
    .filter((entry) => selectedActivityFilter === "all" || entry.categories.includes(selectedActivityFilter))
    .map((entry) => ({ timeUtc: entry.timeUtc, text: entry.text, css: "local" }));

  events.forEach((event) => {
    const include = selectedActivityFilter === "all"
      || selectedActivityFilter === "important" && eventIsImportant(event)
      || selectedActivityFilter === "workers" && eventIsWorker(event)
      || selectedActivityFilter === "failures" && eventIsFailure(event);
    if (include) entries.push({ timeUtc: event.timeUtc, text: serverEventText(event), css: eventIsFailure(event) ? "failure" : eventIsWorker(event) ? "worker-event" : "" });
  });

  entries.sort((a, b) => new Date(b.timeUtc) - new Date(a.timeUtc));
  const visible = entries.slice(0, 100);
  box.replaceChildren(...(visible.length ? visible.map((entry) => {
    const item = document.createElement("li");
    item.className = entry.css;
    const stamp = document.createElement("span");
    stamp.className = "ev-time";
    stamp.textContent = time(entry.timeUtc);
    const text = document.createElement("span");
    text.className = "ev-body";
    text.textContent = entry.text;
    item.append(stamp, text);
    return item;
  }) : [(() => {
    const item = document.createElement("li");
    item.className = "empty-activity";
    item.textContent = "No activity in this view yet.";
    return item;
  })()]));
  box.scrollTop = nearTop ? 0 : previousScroll;
}

function trackHealthyState(state) {
  const previous = previousHealthyState;
  if (previous) {
    if (!previous.seeding.active && state.seeding.active) addActivity(`Seeding started for ${number(state.seeding.target)} jobs.`);
    if (previous.seeding.active && !state.seeding.active) {
      addActivity(state.seeding.error ? `Seeding failed: ${state.seeding.error}` : `Seeding completed: ${number(state.seeding.processed)} jobs processed.`, state.seeding.error ? ["important", "failures"] : ["important"]);
    }
    const completed = state.counts.done - previous.counts.done;
    if (completed > 0) addActivity(`${number(completed)} jobs completed.`);

    const oldWorkers = new Map(previous.workers.map((worker) => [workerKey(worker), worker]));
    state.workers.forEach((worker) => {
      const old = oldWorkers.get(workerKey(worker));
      if (!old) addActivity(`${worker.name} process appeared.`, ["important", "workers"]);
      else if (old.displayState !== worker.displayState) {
        if (worker.displayState === "crashed") addActivity(`${worker.name} crashed. Its current leases remain valid.`, ["important", "workers", "failures"]);
        else if (worker.displayState === "recovered") addActivity(`${worker.name} was marked dead; abandoned work can be reclaimed.`, ["important", "workers"]);
        else if (worker.displayState === "draining") addActivity(`${worker.name} is draining gracefully.`, ["important", "workers"]);
        else if (worker.displayState === "stopped") addActivity(`${worker.name} stopped.`, ["important", "workers"]);
      }
    });

    if (previous.faults.continuousCrashesActive !== state.faults.continuousCrashesActive) {
      addActivity(`Continuous crashes ${state.faults.continuousCrashesActive ? "started" : "stopped"}.`, ["important", "workers"]);
    }
    if (previous.faults.queuePressureActive !== state.faults.queuePressureActive) {
      addActivity(`Queue pressure ${state.faults.queuePressureActive ? "started" : "stopped"}.`);
    }
    if (previous.faults.outboxPressureActive !== state.faults.outboxPressureActive) {
      addActivity(`Outbox pressure ${state.faults.outboxPressureActive ? "started" : "stopped"}.`);
    }
  }
  previousHealthyState = state;
}

function render(state) {
  currentState = state;
  renderProvider(state.provider);
  el("namespace-name").textContent = state.namespaceName;
  el("acta-link").href = `acta/#/?ns=${encodeURIComponent(state.namespaceName)}`;

  if (state.dbError && !databaseUnavailable) {
    databaseUnavailable = true;
    addActivity("Database unavailable; retaining the last successful metrics.", ["important", "failures"]);
  } else if (!state.dbError && databaseUnavailable) {
    databaseUnavailable = false;
    addActivity("Database reconnected.");
  }

  const notice = el("dbnotice");
  notice.hidden = !state.dbError;
  notice.textContent = state.dbError ? `DATABASE UNAVAILABLE: ${state.dbError}. Last successful metrics remain visible.` : "";

  renderLeds(state);
  renderSeeding(state.seeding);
  renderFaults(state.faults);
  renderBeats(state.beats);
  renderWorkerSummary(state.workerSummary);
  renderWorkers(state.workers);
  if (!state.dbError) {
    trackHealthyState(state);
    renderMetrics(state);
    lastServerEvents = state.recentEvents || [];
  }
  renderActivity(lastServerEvents);
  updatePendingControls();
}

async function poll() {
  if (pollInFlight) return;
  pollInFlight = true;
  clearTimeout(pollTimer);
  try {
    const response = await fetch(api + "/state");
    if (!response.ok) throw new Error(`status ${response.status}`);
    const state = await response.json();
    if (backendUnavailable) {
      backendUnavailable = false;
      addActivity("Anvil backend reconnected.");
    }
    el("offline").hidden = true;
    render(state);
  } catch {
    if (!backendUnavailable) {
      backendUnavailable = true;
      addActivity("Anvil backend unavailable; retaining the last successful view.", ["important", "failures"]);
    }
    el("offline").hidden = false;
    renderActivity(lastServerEvents);
  } finally {
    pollInFlight = false;
    pollTimer = setTimeout(poll, document.hidden ? 3000 : 1000);
  }
}

el("workload-seg").addEventListener("click", (event) => {
  const button = event.target.closest("[data-workload]");
  if (button) selectWorkload(button.dataset.workload);
});
el("sel-load").addEventListener("change", renderRunReadout);
el("sel-workers").addEventListener("change", renderRunReadout);
el("activity-filters").addEventListener("click", (event) => {
  const button = event.target.closest("[data-filter]");
  if (!button) return;
  selectedActivityFilter = button.dataset.filter;
  document.querySelectorAll("#activity-filters [data-filter]").forEach((candidate) => candidate.setAttribute("aria-pressed", String(candidate === button)));
  renderActivity(lastServerEvents);
});

el("btn-run").addEventListener("click", async () => {
  const spec = buildRunSpec();
  const response = await call("run", "POST", "/run", "Starting run", spec);
  if (response && response.accepted) {
    addActivity(`Started ${number(spec.load)} ${WORKLOADS[selectedWorkload].label.toLowerCase()} jobs.`);
    await poll();
  }
});

el("btn-spawn").addEventListener("click", async () => {
  if (await call("spawn", "POST", "/workers", "Spawning worker")) await poll();
});

el("btn-fault-crashes").addEventListener("click", async () => {
  const active = !!(currentState && currentState.faults.continuousCrashesActive);
  if (await call("fault:crashes", "POST", `/faults/crashes/${active ? "stop" : "start"}`, `${active ? "Stopping" : "Starting"} continuous crashes`)) await poll();
});

el("btn-fault-pressure").addEventListener("click", async () => {
  const active = !!(currentState && currentState.faults.queuePressureActive);
  const body = active ? undefined : { jobsPerSecond: Number(el("sel-pressure-rate").value) };
  if (await call("fault:pressure", "POST", `/faults/pressure/${active ? "stop" : "start"}`, `${active ? "Stopping" : "Starting"} queue pressure`, body)) await poll();
});

el("btn-fault-outbox").addEventListener("click", async () => {
  const active = !!(currentState && currentState.faults.outboxPressureActive);
  const body = active ? undefined : { jobsPerSecond: Number(el("sel-outbox-rate").value) };
  if (await call("fault:outbox", "POST", `/faults/outbox/${active ? "stop" : "start"}`, `${active ? "Stopping" : "Starting"} outbox pressure`, body)) await poll();
});

el("workers").addEventListener("click", async (event) => {
  const button = event.target.closest("button[data-worker-action]");
  if (!button) return;
  const card = [...workerCards.values()].find((candidate) => candidate.root.contains(button));
  if (!card || !card.worker || card.worker.id == null) return;
  const worker = card.worker;
  const action = button.dataset.workerAction;
  const key = `${action}:${worker.id}`;
  const response = await call(key, "POST", `/workers/${worker.id}/${action}`, `${action === "crash" ? "Crashing" : "Draining"} ${worker.name}`);
  if (response && action === "drain") addActivity(`Graceful drain requested for ${worker.name}.`, ["important", "workers"]);
  if (response) await poll();
});

document.addEventListener("visibilitychange", () => {
  clearTimeout(pollTimer);
  if (!document.hidden) poll();
  else pollTimer = setTimeout(poll, 3000);
});

selectWorkload("noOp");
updatePendingControls();
poll();
