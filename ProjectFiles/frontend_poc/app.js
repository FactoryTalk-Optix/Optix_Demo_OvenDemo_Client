const ovenData = {
  ovenName: "C0CV6M3-8cc8",
  ovenId: "8cc8e346-a4f1-42ea-8954-0bca8be5594b",
  currentRecipe: "Oven Recipe 1",
  lastJobStarted: "Apr 9, 2026, 1:03:08 PM",
  cycles: 0,
  currentTemp: 388.4,    // °F
  setpoint: 392,          // °F
  phase: 1,
  totalPhases: 3,
  heating: true,
  inputFan: 20,
  outputFan: 20,
  powerKw: 190,
  gasNm3h: 882.9,         // ft³/h
  lastUpdate: "Apr 9, 2026, 1:03:08 PM",
  phases: [
    { id: 1, name: "Ramp-up",   duration: 30,  setpoint: 392, progress: 20, status: "running" },
    { id: 2, name: "Baking",    duration: 120, setpoint: 450, progress: 0,  status: "pending" },
    { id: 3, name: "Cool-down", duration: 30,  setpoint: 150, progress: 0,  status: "pending" }
  ],
  lastHoursPower: [150, 160, 155, 165, 175, 185, 190, 190],
  lastHoursGas:   [700, 720, 740, 780, 820, 840, 870, 882],
  cycleYield: [85, 91, 88, 92, 94, 90, 95]
};

const bindingData = {
  isBound: false,
  selectedSlotId: null,
  selectedLocation: "Milan",
  slots: [
    { id: "MI-L1-OVN-01", location: "Milan", line: "Line 1", free: true },
    { id: "MI-L1-OVN-02", location: "Milan", line: "Line 1", free: false },
    { id: "MI-L2-OVN-01", location: "Milan", line: "Line 2", free: true },
    { id: "TO-L1-OVN-01", location: "Turin", line: "Line 1", free: true },
    { id: "BO-L1-OVN-01", location: "Bologna", line: "Line 1", free: true },
    { id: "RL-LAB-OVN-01", location: "RemoteLab", line: "Lab", free: true }
  ]
};

// ── Helpers ──────────────────────────────────────────────
function byId(id) {
  return document.getElementById(id);
}

/**
 * Format a number to `dec` decimal places and append `unit`.
 * Returns "—" for null/undefined.
 */
function formatNum(v, dec, unit = "") {
  if (v == null) return "—";
  return `${Number(v).toFixed(dec)}${unit}`;
}

// ── Theme toggle ─────────────────────────────────────────
function initThemeToggle() {
  const btn = byId("themeToggle");
  if (!btn) return;

  const saved = localStorage.getItem("poc-theme");
  if (saved) document.documentElement.setAttribute("data-theme", saved);

  const isDark = () => {
    const attr = document.documentElement.getAttribute("data-theme");
    if (attr === "dark") return true;
    if (attr === "light") return false;
    return window.matchMedia("(prefers-color-scheme: dark)").matches;
  };

  const sync = () => { btn.textContent = isDark() ? "☀️" : "🌙"; };
  sync();

  btn.addEventListener("click", () => {
    const next = isDark() ? "light" : "dark";
    document.documentElement.setAttribute("data-theme", next);
    localStorage.setItem("poc-theme", next);
    sync();
  });
}

function setText(id, value) {
  const el = byId(id);
  if (el) el.textContent = value;
}

function renderSharedHeader() {
  setText("ovenName", ovenData.ovenName);
  setText("ovenId", ovenData.ovenId);
}

function renderDashboard() {
  // Machine header
  renderSharedHeader();

  const heatPill = byId("heatPill");
  if (heatPill) {
    heatPill.textContent = ovenData.heating ? "Heating" : "Idle";
    heatPill.className = `pill ${ovenData.heating ? "ok" : "warn"}`;
  }

  // Process data card
  setText("currentRecipe",  ovenData.currentRecipe);
  setText("lastJobStarted", ovenData.lastJobStarted);
  setText("cyclesNow",      ovenData.cycles);
  setText("phaseNow",       `Phase ${ovenData.phase}`);
  setText("tempNow",        formatNum(ovenData.currentTemp, 1, " °F"));
  setText("lastUpdate",     ovenData.lastUpdate);

  // Phase config card (pull from current phase)
  const curPhase = ovenData.phases[ovenData.phase - 1];
  setText("phaseDuration",  curPhase ? `${curPhase.duration} s` : "—");
  setText("setpoint",       formatNum(ovenData.setpoint, 0, " °F"));
  setText("fanIn",          `${ovenData.inputFan} %`);
  setText("fanOut",         `${ovenData.outputFan} %`);

  // Consumption row
  setText("gasNow",  formatNum(ovenData.gasNm3h, 1, " ft³/h"));
  setText("powNow",  `${ovenData.powerKw} kW`);
}

function renderRecipeTimeline() {
  const list = byId("phaseList");
  if (!list) return;

  list.innerHTML = "";
  ovenData.phases.forEach((p) => {
    const li = document.createElement("li");
    li.innerHTML = `
      <div class="head">
        <strong>Phase ${p.id} - ${p.name}</strong>
        <span>${p.duration} min | SP ${p.setpoint} C</span>
      </div>
      <div class="status-row">
        <span class="pill ${p.status === "done" ? "ok" : p.status === "running" ? "warn" : "danger"}">
          ${p.status.toUpperCase()}
        </span>
      </div>
      <div class="progress"><span style="width:${p.progress}%"></span></div>
    `;
    list.appendChild(li);
  });
}

function drawLineChart(canvasId, values, color, maxYLabel) {
  const cv = byId(canvasId);
  if (!cv) return;

  const dpr = window.devicePixelRatio || 1;
  const w = cv.clientWidth;
  const h = cv.clientHeight;
  cv.width = Math.floor(w * dpr);
  cv.height = Math.floor(h * dpr);

  const ctx = cv.getContext("2d");
  ctx.scale(dpr, dpr);
  ctx.clearRect(0, 0, w, h);

  const pad = { t: 18, r: 20, b: 28, l: 28 };
  const gw = w - pad.l - pad.r;
  const gh = h - pad.t - pad.b;

  ctx.strokeStyle = "#e2d5c3";
  ctx.lineWidth = 1;
  for (let i = 0; i <= 4; i += 1) {
    const y = pad.t + (gh / 4) * i;
    ctx.beginPath();
    ctx.moveTo(pad.l, y);
    ctx.lineTo(w - pad.r, y);
    ctx.stroke();
  }

  const min = Math.min(...values);
  const max = Math.max(...values);
  const span = Math.max(1, max - min);

  ctx.strokeStyle = color;
  ctx.lineWidth = 2.5;
  ctx.beginPath();
  values.forEach((v, i) => {
    const x = pad.l + (gw * i) / (values.length - 1);
    const y = pad.t + gh - ((v - min) / span) * gh;
    if (i === 0) ctx.moveTo(x, y);
    else ctx.lineTo(x, y);
  });
  ctx.stroke();

  ctx.fillStyle = "#5e513f";
  ctx.font = "12px Space Grotesk, sans-serif";
  ctx.fillText(`max ${maxYLabel}: ${max}`, pad.l, 12);
}

function renderStats() {
  drawLineChart("powerChart", ovenData.lastHoursPower, "#c65102", "kW");
  drawLineChart("gasChart", ovenData.lastHoursGas, "#0f766e", "Nm3/h");

  const list = byId("yieldList");
  if (!list) return;

  list.innerHTML = "";
  ovenData.cycleYield.forEach((y, i) => {
    const li = document.createElement("li");
    li.innerHTML = `<span>Cycle ${i + 1}</span><strong>${y}%</strong>`;
    list.appendChild(li);
  });
}

function getBindingCandidates(filterText) {
  const query = (filterText || "").toLowerCase().trim();
  return bindingData.slots.filter((s) => {
    if (s.location !== bindingData.selectedLocation) return false;
    if (!s.free) return false;
    if (!query) return true;
    return `${s.id} ${s.location} ${s.line}`.toLowerCase().includes(query);
  });
}

function setBindingStateText(text) {
  const el = byId("bindingState");
  if (!el) return;
  el.textContent = text;
}

function renderSlotList(filterText = "") {
  const list = byId("slotList");
  if (!list) return;

  const items = getBindingCandidates(filterText);
  list.innerHTML = "";

  if (!items.length) {
    const empty = document.createElement("li");
    empty.className = "slot-item";
    empty.innerHTML = "<div>No free slots match current filter.</div>";
    list.appendChild(empty);
    return;
  }

  items.forEach((slot) => {
    const li = document.createElement("li");
    const selected = bindingData.selectedSlotId === slot.id;
    li.className = "slot-item";
    li.innerHTML = `
      <div>
        <strong>${slot.id}</strong>
        <div class="slot-meta">${slot.location} | ${slot.line}</div>
      </div>
      <button data-slot-id="${slot.id}" ${bindingData.isBound ? "disabled" : ""}>${selected ? "Selected" : "Select"}</button>
    `;
    list.appendChild(li);
  });

  list.querySelectorAll("button[data-slot-id]").forEach((btn) => {
    btn.addEventListener("click", () => {
      if (bindingData.isBound) return;
      bindingData.selectedSlotId = btn.getAttribute("data-slot-id");
      const bindBtn = byId("bindBtn");
      if (bindBtn) bindBtn.disabled = false;
      setBindingStateText(`Selected slot: ${bindingData.selectedSlotId}. Click Confirm Binding to persist.`);
      renderSlotList(byId("slotFilter")?.value || "");
    });
  });
}

function renderBindingSetup() {
  const locationSelect = byId("locationSelect");
  const bindBtn        = byId("bindBtn");
  const adminBtn       = byId("simulateAdminBtn");
  if (!locationSelect || !bindBtn) return;

  locationSelect.value = bindingData.selectedLocation;

  locationSelect.addEventListener("change", () => {
    bindingData.selectedLocation = locationSelect.value;
  });

  bindBtn.addEventListener("click", () => {
    bindingData.isBound = true;
    bindingData.boundLocation = locationSelect.value;
    const state = byId("bindingState");
    if (state) {
      state.removeAttribute("hidden");
      state.textContent = `Bound to ${locationSelect.options[locationSelect.selectedIndex].text}. Retentive lock active — only admin can change this.`;
    }
    bindBtn.disabled = true;
    locationSelect.disabled = true;
  });

  if (adminBtn) {
    adminBtn.addEventListener("click", () => {
      bindingData.isBound = false;
      bindBtn.disabled = false;
      locationSelect.disabled = false;
      const state = byId("bindingState");
      if (state) {
        state.removeAttribute("hidden");
        state.textContent = "Admin override active. Binding can be changed.";
      }
    });
  }
}

window.addEventListener("resize", () => {
  if (byId("powerChart") || byId("gasChart")) renderStats();
});

window.addEventListener("DOMContentLoaded", () => {
  initThemeToggle();
  renderDashboard();
  renderRecipeTimeline();
  renderStats();
  renderBindingSetup();
});
