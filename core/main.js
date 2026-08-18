const TOMORROWOS_CONFIG = window.TOMORROWOS_CONFIG || {};

const CMS_ENDPOINT_STORAGE_KEY = "tomorrowos.cmsEndpoint";
const VALID_CONFIG_ORIENTATIONS = new Set(["landscape", "portrait-right", "portrait-left"]);
const VALID_CONTENT_FITS = new Set(["contain", "cover", "stretch"]);
const CONTENT_FIT_KEY = "tomorrowos.contentFit";
const BOOT_CMS_RETRY_MS = 5000;
let bootCmsRetryTimer = null;

/** Current CMS WebSocket URL (set after storage or user input). */
let cmsEndpoint = "";
/** HTTP origin derived from cmsEndpoint (for brand.json, assets). */
let httpEndpoint = "";
/** Active CMS WebSocket (if connect() has run). */
let cmsWebSocket = null;
/** False while CMS WebSocket is down (idle static page shows reconnecting). */
let cmsSocketConnected = false;

const CMS_PING_INTERVAL_MS = 15000;
const CMS_PING_MAX_MISSES = 2;
const CMS_HTTP_PROBE_TIMEOUT_MS = 5000;
const CMS_CONNECT_TIMEOUT_MS = 12000;
const CMS_RECONNECT_DELAY_MS = 1000;

let cmsPingTimer = null;
let cmsPingAwaitingPong = false;
let cmsPingMisses = 0;
let cmsConnectTimeoutTimer = null;
let cmsWatchdogHttpInFlight = false;

function stopCmsPing() {
  if (cmsPingTimer) {
    clearInterval(cmsPingTimer);
    cmsPingTimer = null;
  }
  cmsPingAwaitingPong = false;
  cmsPingMisses = 0;
}

function clearCmsConnectTimeout() {
  if (cmsConnectTimeoutTimer) {
    clearTimeout(cmsConnectTimeoutTimer);
    cmsConnectTimeoutTimer = null;
  }
}

function scheduleCmsConnectTimeout(socket) {
  clearCmsConnectTimeout();
  cmsConnectTimeoutTimer = setTimeout(() => {
    cmsConnectTimeoutTimer = null;
    if (socket.readyState === WebSocket.CONNECTING) {
      closeCmsSocketForReconnect(socket, "CMS connect timeout");
    }
  }, CMS_CONNECT_TIMEOUT_MS);
}

function closeCmsSocketForReconnect(socket, reason) {
  console.warn(`[TomorrowOS] ${reason} — closing socket`);
  stopCmsPing();
  if (!socket) return;
  const state = socket.readyState;
  if (state !== WebSocket.OPEN && state !== WebSocket.CONNECTING) return;
  try {
    socket.close();
  } catch (_) {}
}

async function probeCmsHttpReachable() {
  if (!httpEndpoint) return false;

  // Windows WebView2 serves from https://tomorrowos.app — browser fetch to the CMS
  // is often blocked by CORS and would falsely tear down a healthy WebSocket.
  const platformProbe = getPlatform()?.httpProbe;
  if (typeof platformProbe === "function") {
    try {
      return !!(await platformProbe(`${httpEndpoint}/brand.json`));
    } catch {
      return false;
    }
  }

  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), CMS_HTTP_PROBE_TIMEOUT_MS);
    const res = await fetch(`${httpEndpoint}/brand.json`, {
      cache: "no-store",
      signal: controller.signal
    });
    clearTimeout(timer);
    return res.ok;
  } catch {
    return false;
  }
}

/** Ask the current HTTP CMS whether this device is live-connected (not the old WS peer). */
async function probeCmsDeviceConnectedOnServer() {
  const deviceId = localStorage.getItem("pairedDeviceId");
  if (!deviceId || !httpEndpoint) return null;

  const platformGetJson = getPlatform()?.httpGetJson;
  if (typeof platformGetJson === "function") {
    try {
      const data = await platformGetJson(`${httpEndpoint}/devices`);
      const devices = Array.isArray(data?.devices) ? data.devices : [];
      const record = devices.find((entry) => entry.deviceId === deviceId);
      if (!record) return false;
      return !!record.connected;
    } catch {
      return null;
    }
  }

  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), CMS_HTTP_PROBE_TIMEOUT_MS);
    const res = await fetch(`${httpEndpoint}/devices`, {
      cache: "no-store",
      signal: controller.signal
    });
    clearTimeout(timer);
    if (!res.ok) return null;
    const data = await res.json();
    const devices = Array.isArray(data.devices) ? data.devices : [];
    const record = devices.find((entry) => entry.deviceId === deviceId);
    if (!record) return false;
    return !!record.connected;
  } catch {
    return null;
  }
}

async function checkCmsHttpLiveness(socket) {
  if (!socket || socket.readyState !== WebSocket.OPEN) return;
  if (cmsWatchdogHttpInFlight) return;
  cmsWatchdogHttpInFlight = true;
  try {
    const httpOk = await probeCmsHttpReachable();
    if (!httpOk) {
      closeCmsSocketForReconnect(socket, "CMS HTTP unreachable");
      return;
    }

    if (cmsSocketConnected) {
      const serverConnected = await probeCmsDeviceConnectedOnServer();
      if (serverConnected === false) {
        closeCmsSocketForReconnect(socket, "CMS device not registered on server");
        return;
      }
    }

    if (cmsPingAwaitingPong && cmsPingMisses >= 1) {
      closeCmsSocketForReconnect(socket, "CMS HTTP ok but WebSocket stale");
    }
  } finally {
    cmsWatchdogHttpInFlight = false;
  }
}

function sendCmsPing(socket) {
  if (!socket || socket.readyState !== WebSocket.OPEN) return;

  if (cmsPingAwaitingPong) {
    cmsPingMisses += 1;
    if (cmsPingMisses >= CMS_PING_MAX_MISSES) {
      closeCmsSocketForReconnect(socket, "CMS ping timeout");
      return;
    }
  }

  try {
    socket.send(
      JSON.stringify({
        type: "device.ping",
        timestamp: new Date().toISOString()
      })
    );
    cmsPingAwaitingPong = true;
  } catch (err) {
    console.warn("[TomorrowOS] CMS ping send failed:", err);
    closeCmsSocketForReconnect(socket, "CMS ping send failed");
  }
}

function runCmsWatchdog(socket) {
  void checkCmsHttpLiveness(socket);
  sendCmsPing(socket);
}

function startCmsPing(socket) {
  stopCmsPing();
  runCmsWatchdog(socket);
  cmsPingTimer = setInterval(() => runCmsWatchdog(socket), CMS_PING_INTERVAL_MS);
}

function handleCmsPong() {
  cmsPingAwaitingPong = false;
  cmsPingMisses = 0;
}

function reportDeviceLog(level, message, details = {}, source = "player") {
  const socket = cmsWebSocket;
  if (!socket || socket.readyState !== WebSocket.OPEN) return;
  try {
    socket.send(
      JSON.stringify({
        type: "device.log",
        level: String(level || "info").toLowerCase(),
        message: String(message || "log"),
        source,
        timestamp: new Date().toISOString(),
        details
      })
    );
  } catch (_) {}
}

function getHttpEndPoint(cmsEp) {
  return cmsEp.replace("ws://", "http://").replace("wss://", "https://");
}

function setCmsEndpoints(wsUrl) {
  const trimmed = String(wsUrl || "").trim();
  cmsEndpoint = trimmed;
  if (!trimmed) {
    httpEndpoint = "";
    return;
  }
  const http = getHttpEndPoint(trimmed);
  try {
    httpEndpoint = new URL(http).origin;
  } catch {
    httpEndpoint = http.replace(/\/+$/, "");
  }
}

function getStoredCmsEndpoint() {
  try {
    return localStorage.getItem(CMS_ENDPOINT_STORAGE_KEY) || "";
  } catch {
    return "";
  }
}

function persistCmsEndpoint(wsUrl) {
  try {
    localStorage.setItem(CMS_ENDPOINT_STORAGE_KEY, String(wsUrl || "").trim());
  } catch {
    /* ignore quota / private mode */
  }
}

/**
 * Normalize user input to ws:// or wss:// URL.
 * @returns {string|null}
 */
function normalizeCmsEndpointInput(raw) {
  let s = String(raw || "").trim();
  if (!s) return null;
  if (/^https:\/\//i.test(s)) s = s.replace(/^https:\/\//i, "wss://");
  else if (/^http:\/\//i.test(s)) s = s.replace(/^http:\/\//i, "ws://");
  if (!/^wss?:\/\//i.test(s)) return null;
  try {
    const u = new URL(s);
    if (u.protocol !== "ws:" && u.protocol !== "wss:") return null;
    return u.href;
  } catch {
    return null;
  }
}

/**
 * Candidate WebSocket URLs for one CMS host.
 * Vercel Functions WebSockets mount under `/api` — try that when origin WS fails.
 * @param {string} wsUrl
 * @returns {string[]}
 */
function cmsWebSocketCandidates(wsUrl) {
  let u;
  try {
    u = new URL(wsUrl);
  } catch {
    return [wsUrl];
  }
  const candidates = [];
  const push = (href) => {
    if (href && !candidates.includes(href)) candidates.push(href);
  };
  push(u.href);
  const base = `${u.protocol}//${u.host}`;
  const path = u.pathname || "/";
  if (path === "/" || path === "") {
    push(`${base}/`);
    push(`${base}/api`);
    push(`${base}/api/`);
  } else if (path === "/api" || path === "/api/") {
    push(`${base}/`);
    push(`${base}/api`);
  }
  if (/\.vercel\.app$/i.test(u.hostname) || /\.vercel\.dev$/i.test(u.hostname)) {
    push(`${base}/`);
    push(`${base}/api`);
    push(`${base}/api/`);
  }
  return candidates;
}

/**
 * Open a short-lived WebSocket to verify the CMS accepts connections.
 * @param {string} wsUrl
 * @param {number} timeoutMs
 * @returns {Promise<boolean>}
 */
function verifyCmsWebSocketReachable(wsUrl, timeoutMs) {
  return new Promise((resolve) => {
    let settled = false;
    const done = (ok) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      try {
        ws.close();
      } catch {
        /* ignore */
      }
      resolve(ok);
    };

    const timer = setTimeout(() => done(false), timeoutMs);

    let ws;
    try {
      ws = new WebSocket(wsUrl);
    } catch {
      clearTimeout(timer);
      resolve(false);
      return;
    }

    ws.onopen = () => done(true);
    ws.onerror = () => done(false);
    ws.onclose = () => {
      if (!settled) done(false);
    };
  });
}

/**
 * Try WebSocket candidates until one connects. Returns the working URL or null.
 * @param {string} wsUrl
 * @param {number} timeoutMs
 * @returns {Promise<string|null>}
 */
async function resolveReachableCmsWebSocket(wsUrl, timeoutMs) {
  const candidates = cmsWebSocketCandidates(wsUrl);
  const perTry = Math.max(2500, Math.floor((timeoutMs || 12000) / candidates.length));
  for (const candidate of candidates) {
    // eslint-disable-next-line no-await-in-loop
    const ok = await verifyCmsWebSocketReachable(candidate, perTry);
    if (ok) return candidate;
  }
  return null;
}

const cmsSetupScreen = document.getElementById("cmsSetupScreen");
const cmsEndpointInput = document.getElementById("cmsEndpointInput");
const cmsEndpointSaveBtn = document.getElementById("cmsEndpointSaveBtn");
const cmsSetupError = document.getElementById("cmsSetupError");

/** When true, custom on-screen keyboard navigation is hooked up. */
let cmsSetupRemoteKeysAttached = false;
let cmsKeyboardBuilt = false;
let cmsKeyboardOpen = false;
let cmsKeyboardMode = "letters"; // "letters" | "symbols"

// Two keyboard layouts. The user toggles between them via the SYM/ABC key.
// `char` = inserts that literal; `insert` = inserts a longer string;
// `action` = special action (backspace/clear/done/togglemode).
const CMS_KEYBOARD_LAYOUTS = {
  letters: [
    [
      { char: "1" }, { char: "2" }, { char: "3" }, { char: "4" }, { char: "5" },
      { char: "6" }, { char: "7" }, { char: "8" }, { char: "9" }, { char: "0" },
    ],
    [
      { char: "q" }, { char: "w" }, { char: "e" }, { char: "r" }, { char: "t" },
      { char: "y" }, { char: "u" }, { char: "i" }, { char: "o" }, { char: "p" },
    ],
    [
      { char: "a" }, { char: "s" }, { char: "d" }, { char: "f" }, { char: "g" },
      { char: "h" }, { char: "j" }, { char: "k" }, { char: "l" }, { char: "-" },
    ],
    [
      { char: "z" }, { char: "x" }, { char: "c" }, { char: "v" }, { char: "b" },
      { char: "n" }, { char: "m" }, { char: "." }, { char: "," }, { char: ";" },
    ],
    [
      { label: "#+=", action: "togglemode", wide: 2, accent: true },
      { label: "SPACE", insert: " ", action: "insert", wide: 3, accent: true },
      { label: "DEL", action: "backspace", accent: true },
      { label: "CLR", action: "clear", accent: true },
      { label: "DONE", action: "done", wide: 3, accent: true },
    ],
  ],
  symbols: [
    [
      { char: "1" }, { char: "2" }, { char: "3" }, { char: "4" }, { char: "5" },
      { char: "6" }, { char: "7" }, { char: "8" }, { char: "9" }, { char: "0" },
    ],
    [
      { char: "!" }, { char: "@" }, { char: "#" }, { char: "$" }, { char: "%" },
      { char: "^" }, { char: "&" }, { char: "*" }, { char: "(" }, { char: ")" },
    ],
    [
      { char: ":" }, { char: "/" }, { char: "_" }, { char: "?" }, { char: "=" },
      { char: "+" }, { char: ";" }, { char: "'" }, { char: "\"" }, { char: "`" },
    ],
    [
      { char: "{" }, { char: "}" }, { char: "[" }, { char: "]" }, { char: "<" },
      { char: ">" }, { char: "|" }, { char: "\\" }, { char: "~" }, { char: "." },
    ],
    [
      { label: "ABC", action: "togglemode", wide: 2, accent: true },
      { label: "SPACE", insert: " ", action: "insert", wide: 3, accent: true },
      { label: "DEL", action: "backspace", accent: true },
      { label: "CLR", action: "clear", accent: true },
      { label: "DONE", action: "done", wide: 3, accent: true },
    ],
  ],
};

function getCmsKeyboardLayout() {
  return CMS_KEYBOARD_LAYOUTS[cmsKeyboardMode] || CMS_KEYBOARD_LAYOUTS.letters;
}

function buildCmsKeyboard(force = false) {
  const host = document.getElementById("cmsKeyboard");
  if (!host) return;
  if (cmsKeyboardBuilt && !force) return;
  cmsKeyboardBuilt = true;
  host.innerHTML = "";
  getCmsKeyboardLayout().forEach((row, rowIdx) => {
    const rowEl = document.createElement("div");
    rowEl.className = "cms-kb-row";
    row.forEach((spec, colIdx) => {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "cms-key";
      if (spec.accent) btn.classList.add("cms-key-action");
      if (spec.wide === 2) btn.classList.add("wide-2");
      if (spec.wide === 3) btn.classList.add("wide-3");
      btn.textContent = spec.label || spec.char;
      btn.dataset.row = String(rowIdx);
      btn.dataset.col = String(colIdx);
      btn.tabIndex = 0;
      btn.addEventListener("click", (ev) => {
        ev.preventDefault();
        ev.stopPropagation();
        applyKeyboardSpec(spec);
      });
      rowEl.appendChild(btn);
    });
    host.appendChild(rowEl);
  });
}

function applyKeyboardSpec(spec) {
  if (!cmsEndpointInput) return;
  if (spec.action === "backspace") {
    cmsEndpointInput.value = cmsEndpointInput.value.slice(0, -1);
    return;
  }
  if (spec.action === "clear") {
    cmsEndpointInput.value = "";
    return;
  }
  if (spec.action === "done") {
    closeCmsKeyboard({ focusTarget: "connect" });
    return;
  }
  if (spec.action === "togglemode") {
    toggleCmsKeyboardMode();
    return;
  }
  if (spec.action === "insert" && typeof spec.insert === "string") {
    cmsEndpointInput.value += spec.insert;
    return;
  }
  if (typeof spec.char === "string") {
    cmsEndpointInput.value += spec.char;
  }
}

function openCmsKeyboard() {
  const host = document.getElementById("cmsKeyboard");
  if (!host) return;
  buildCmsKeyboard();
  host.classList.add("cms-keyboard--visible");
  cmsKeyboardOpen = true;
  requestAnimationFrame(() => focusKeyboardKey(0, 0));
}

function closeCmsKeyboard(options = {}) {
  const host = document.getElementById("cmsKeyboard");
  if (host) host.classList.remove("cms-keyboard--visible");
  cmsKeyboardOpen = false;
  const target = options.focusTarget || "input";
  if (target === "connect") {
    focusCmsConnectButton();
  } else {
    focusCmsEndpointInput();
  }
}

function toggleCmsKeyboardMode() {
  cmsKeyboardMode = cmsKeyboardMode === "letters" ? "symbols" : "letters";
  // Find toggle key's coordinates in the NEW layout so focus lands on the
  // same button (which now shows the other label).
  const layout = getCmsKeyboardLayout();
  let toggleRow = layout.length - 1;
  let toggleCol = 0;
  for (let r = 0; r < layout.length; r += 1) {
    for (let c = 0; c < layout[r].length; c += 1) {
      if (layout[r][c].action === "togglemode") {
        toggleRow = r; toggleCol = c; break;
      }
    }
  }
  buildCmsKeyboard(true);
  requestAnimationFrame(() => focusKeyboardKey(toggleRow, toggleCol));
}

function focusCmsEndpointInput() {
  if (!cmsEndpointInput) return;
  try { cmsEndpointInput.focus({ preventScroll: true }); } catch (_) {
    try { cmsEndpointInput.focus(); } catch (__) {}
  }
}

function getKeyboardKeys() {
  const host = document.getElementById("cmsKeyboard");
  if (!host) return [];
  return Array.from(host.querySelectorAll(".cms-key"));
}

function getKeyboardKey(rowIdx, colIdx) {
  const host = document.getElementById("cmsKeyboard");
  if (!host) return null;
  return host.querySelector(
    `.cms-key[data-row="${rowIdx}"][data-col="${colIdx}"]`
  );
}

function focusKeyboardKey(rowIdx, colIdx) {
  const layout = getCmsKeyboardLayout();
  const totalRows = layout.length;
  if (rowIdx < 0) rowIdx = 0;
  if (rowIdx >= totalRows) rowIdx = totalRows - 1;
  const row = layout[rowIdx];
  if (colIdx < 0) colIdx = 0;
  if (colIdx >= row.length) colIdx = row.length - 1;
  const key = getKeyboardKey(rowIdx, colIdx);
  if (key) {
    try { key.focus({ preventScroll: true }); } catch (_) { try { key.focus(); } catch (__) {} }
  }
}

function focusCmsConnectButton() {
  if (!cmsEndpointSaveBtn) return;
  try { cmsEndpointSaveBtn.focus({ preventScroll: true }); } catch (_) {
    try { cmsEndpointSaveBtn.focus(); } catch (__) {}
  }
}

function isCmsKeyboardKey(el) {
  return !!(el && el.classList && el.classList.contains("cms-key"));
}

function isPrintableCmsKey(ev) {
  if (ev.ctrlKey || ev.altKey || ev.metaKey) return false;
  const k = ev.key;
  return typeof k === "string" && k.length === 1;
}

/** USB / external keyboard ??URL field (input stays readonly to avoid Tizen IME). */
function tryApplyCmsPhysicalKeyboard(ev) {
  if (!cmsEndpointInput || cmsSetupScreen?.style.display === "none") return false;

  if (isPrintableCmsKey(ev)) {
    ev.preventDefault();
    ev.stopPropagation();
    cmsEndpointInput.value += ev.key;
    return true;
  }

  const k = ev.key;
  const code = ev.keyCode;
  const isBackspace = k === "Backspace" || code === 8;
  if (isBackspace) {
    ev.preventDefault();
    ev.stopPropagation();
    cmsEndpointInput.value = cmsEndpointInput.value.slice(0, -1);
    return true;
  }

  return false;
}

function onCmsSetupDocumentKeyDown(ev) {
  if (!cmsEndpointInput || cmsSetupScreen?.style.display === "none") return;

  if (tryApplyCmsPhysicalKeyboard(ev)) return;

  const k = ev.key;
  const code = ev.keyCode;
  const isEnter = k === "Enter" || code === 13;
  const isLeft = k === "ArrowLeft" || code === 37;
  const isRight = k === "ArrowRight" || code === 39;
  const isUp = k === "ArrowUp" || code === 38;
  const isDown = k === "ArrowDown" || code === 40;
  const isDelKey = k === "Delete" || code === 8 || k === "Backspace";
  // Tizen TV "Back" / "Return" key on the remote.
  const isTvBack =
    k === "XF86Back" || k === "GoBack" || k === "Escape" || code === 10009 || code === 27;

  const ae = document.activeElement;
  const onConnect = ae === cmsEndpointSaveBtn;
  const onInput = ae === cmsEndpointInput;
  const onKey = isCmsKeyboardKey(ae);

  // -- Keyboard is OPEN --
  if (cmsKeyboardOpen) {
    if (isTvBack) {
      ev.preventDefault(); ev.stopPropagation();
      closeCmsKeyboard({ focusTarget: "input" });
      return;
    }

    if (onKey) {
      const layout = getCmsKeyboardLayout();
      const rowIdx = Number(ae.dataset.row);
      const colIdx = Number(ae.dataset.col);
      const lastRowIdx = layout.length - 1;
      const row = layout[rowIdx];

      if (isLeft) {
        ev.preventDefault(); ev.stopPropagation();
        const nextCol = colIdx > 0 ? colIdx - 1 : row.length - 1;
        focusKeyboardKey(rowIdx, nextCol);
        return;
      }
      if (isRight) {
        ev.preventDefault(); ev.stopPropagation();
        const nextCol = colIdx < row.length - 1 ? colIdx + 1 : 0;
        focusKeyboardKey(rowIdx, nextCol);
        return;
      }
      if (isUp) {
        ev.preventDefault(); ev.stopPropagation();
        if (rowIdx > 0) {
          const targetRow = layout[rowIdx - 1];
          const targetCol = Math.min(colIdx, targetRow.length - 1);
          focusKeyboardKey(rowIdx - 1, targetCol);
        }
        return;
      }
      if (isDown) {
        ev.preventDefault(); ev.stopPropagation();
        if (rowIdx < lastRowIdx) {
          const targetRow = layout[rowIdx + 1];
          const targetCol = Math.min(colIdx, targetRow.length - 1);
          focusKeyboardKey(rowIdx + 1, targetCol);
        }
        return;
      }
      if (isEnter) {
        ev.preventDefault(); ev.stopPropagation();
        applyKeyboardSpec(layout[rowIdx][colIdx]);
        return;
      }
      if (isDelKey) {
        ev.preventDefault(); ev.stopPropagation();
        cmsEndpointInput.value = cmsEndpointInput.value.slice(0, -1);
        return;
      }
      return;
    }

    // Focus on URL field while on-screen keyboard is open ??route remote to virtual keys.
    if (onInput && (isEnter || isUp || isDown || isLeft || isRight)) {
      ev.preventDefault(); ev.stopPropagation();
      if (isDown) {
        closeCmsKeyboard({ focusTarget: "connect" });
      } else {
        focusKeyboardKey(0, 0);
      }
      return;
    }

    // Stray focus while keyboard is open ??pull it back to the first key.
    if (isEnter || isUp || isDown || isLeft || isRight) {
      ev.preventDefault(); ev.stopPropagation();
      focusKeyboardKey(0, 0);
    }
    return;
  }

  // -- Keyboard is CLOSED --
  if (onInput) {
    if (isEnter || isUp || isLeft || isRight) {
      ev.preventDefault(); ev.stopPropagation();
      openCmsKeyboard();
      return;
    }
    if (isDown) {
      ev.preventDefault(); ev.stopPropagation();
      focusCmsConnectButton();
      return;
    }
    return;
  }

  if (onConnect) {
    if (isUp) {
      ev.preventDefault(); ev.stopPropagation();
      focusCmsEndpointInput();
      return;
    }
    if (isEnter) {
      ev.preventDefault(); ev.stopPropagation();
      void onCmsEndpointSaveClick();
      return;
    }
    return;
  }

  // Focus is on body/document ??pull it into the input.
  if (isEnter || isUp || isDown || isLeft || isRight) {
    ev.preventDefault(); ev.stopPropagation();
    focusCmsEndpointInput();
  }
}

function attachCmsSetupRemoteKeys() {
  if (cmsSetupRemoteKeysAttached) return;
  document.addEventListener("keydown", onCmsSetupDocumentKeyDown, true);
  cmsSetupRemoteKeysAttached = true;
}

function detachCmsSetupRemoteKeys() {
  if (!cmsSetupRemoteKeysAttached) return;
  document.removeEventListener("keydown", onCmsSetupDocumentKeyDown, true);
  cmsSetupRemoteKeysAttached = false;
}

function showCmsSetupUI() {
  stopPairingCodeRollAnimation();
  if (cmsSetupScreen) cmsSetupScreen.style.display = "block";
  if (pairingArea) pairingArea.style.display = "none";
  cmsKeyboardMode = "letters";
  buildCmsKeyboard(true);
  // Keyboard starts closed; user opens it by pressing Enter on the URL field.
  const host = document.getElementById("cmsKeyboard");
  if (host) host.classList.remove("cms-keyboard--visible");
  cmsKeyboardOpen = false;
  attachCmsSetupRemoteKeys();
  if (isBrightSignRuntime() && cmsEndpointInput) {
    cmsEndpointInput.value = `${BRIGHTSIGN_DEFAULT_CMS_HTTP}/`;
    scheduleBrightsignCmsAutoConnect();
  } else {
    requestAnimationFrame(() => focusCmsEndpointInput());
    setTimeout(focusCmsEndpointInput, 80);
    setTimeout(focusCmsEndpointInput, 300);
  }
}

function isPortraitOrientation() {
  const orient = getSavedOrientation();
  return orient === "portrait-right" || orient === "portrait-left";
}

/**
 * BrightSign HWZ video renders on a hardware plane that ignores CSS transforms.
 * Portrait UI rotates via CSS on <body>; video needs a matching HWZ transform:
 *   portrait-right  → clockwise 90°  (CSS rotate(90deg))
 *   portrait-left   → clockwise 270°  (CSS rotate(-90deg))
 * HWZ rot90/rot270 are opposite to the CSS rotation direction on our layout,
 * so portrait-right uses rot270 and portrait-left uses rot90.
 */
function applyBrightSignVideoPlaybackAttributes(video) {
  if (!isBrightSignRuntime() || !video) return;

  video.setAttribute("viewmode", "scale-to-fill");

  const orient = getSavedOrientation();
  if (orient === "portrait-right") {
    video.setAttribute("hwz", "z-index:1; transform:rot270;");
  } else if (orient === "portrait-left") {
    video.setAttribute("hwz", "z-index:1; transform:rot90;");
  } else {
    video.setAttribute("hwz", "z-index:1;");
  }
}

/** Content coordinates: portrait swaps physical width/height. */
function getLogicalDisplaySize() {
  const physicalW = window.screen.width || 1920;
  const physicalH = window.screen.height || 1080;
  if (isPortraitOrientation()) {
    return { width: physicalH, height: physicalW };
  }
  return { width: physicalW, height: physicalH };
}

function isLayoutViewportInflated() {
  // Compare against physical screen (HtmlWidget / graphics plane), not logical portrait size.
  const sw = Number(window.screen && window.screen.width) || 0;
  const sh = Number(window.screen && window.screen.height) || 0;
  const dw = document.documentElement.clientWidth || 0;
  const dh = document.documentElement.clientHeight || 0;
  if (!dw || !dh || !sw || !sh) return false;
  return dw > sw * 1.15 || dh > sh * 1.15;
}

/**
 * BrightSign-safe layout correction for Series 5 inflated Chromium viewports.
 * - Does not rewrite <meta viewport>
 * - Does not pin html/body width/height (that blanked Series 5 previously)
 * - Only nudges landscape body with margin when client box is larger than screen
 *   so flex-centered UI sits in the visible area instead of the bottom-right.
 */
function resetDocumentLayoutViewport() {
  try {
    window.scrollTo(0, 0);
  } catch (_) {}

  const body = document.body;
  if (body) {
    if (
      isBrightSignRuntime() &&
      !isPortraitOrientation() &&
      isLayoutViewportInflated()
    ) {
      const sw = Number(window.screen && window.screen.width) || 0;
      const sh = Number(window.screen && window.screen.height) || 0;
      const cw = document.documentElement.clientWidth || 0;
      const ch = document.documentElement.clientHeight || 0;
      const dx = Math.max(0, Math.round((cw - sw) / 2));
      const dy = Math.max(0, Math.round((ch - sh) / 2));
      body.style.marginLeft = dx ? `-${dx}px` : "";
      body.style.marginTop = dy ? `-${dy}px` : "";
    } else {
      body.style.marginLeft = "";
      body.style.marginTop = "";
    }
  }

  void document.documentElement.offsetHeight;
}

function invalidateIdleStateFramePriming() {
  idleStatePrimed = false;
  idleStateLoadedSrc = null;
  idleStatePrimePromise = null;
}

function getIdleStatePage() {
  return isPortraitOrientation()
    ? "./quiet-state-portrait.html"
    : "./quiet-state-landscape.html";
}

function bindIdleStateFrame(options = {}) {
  if (!idleStateFrame) return;
  let api = null;
  try {
    api = idleStateFrame.contentWindow?.TomorrowQuietState;
  } catch (_) {
    return;
  }
  if (!api) return;

  const brand = currentBrand || {
    name: "TomorrowOS",
    backgroundColor: "#050505",
    textColor: "#ffffff"
  };
  api.applyBrand(brand, httpEndpoint || "");

  const statusSub = options.statusSub || "";
  if (!cmsSocketConnected) {
    api.setStatus(
      "Disconnected",
      statusSub || "Connection lost - reconnecting to CMS"
    );
  } else if (options.variant === "fallback") {
    api.setStatus("Connected", statusSub || "Awaiting content");
  } else {
    api.setStatus("Connected", statusSub || "Paired - awaiting content");
  }
  api.startClock();
}

function isIdleScreenVisible() {
  return idleStateShell?.style.display !== "none";
}

function refreshIdleScreenConnectionStatus() {
  if (!isIdleScreenVisible() || !idleStateLastOptions) return;
  bindIdleStateFrame(idleStateLastOptions);
}

function primeIdleStateFrame() {
  if (!idleStateFrame) return Promise.resolve();

  const page = getIdleStatePage();
  if (idleStatePrimed && idleStateLoadedSrc === page) {
    return Promise.resolve();
  }
  if (idleStatePrimePromise) return idleStatePrimePromise;

  idleStatePrimePromise = new Promise((resolve) => {
    const finishPrime = () => {
      idleStateLoadedSrc = page;
      idleStatePrimed = true;
      idleStatePrimePromise = null;
      bindIdleStateFrame({ variant: "paired" });
      resolve();
    };

    const tryBind = () => {
      try {
        return !!idleStateFrame.contentWindow?.TomorrowQuietState;
      } catch (_) {
        return false;
      }
    };

    if (idleStateLoadedSrc === page && tryBind()) {
      finishPrime();
      return;
    }

    idleStateFrame.onload = () => {
      if (tryBind()) {
        finishPrime();
        return;
      }
      requestAnimationFrame(() => finishPrime());
    };

    if (idleStateLoadedSrc !== page) {
      idleStateFrame.src = page;
      return;
    }

    const waitForBind = (attemptsLeft) => {
      if (tryBind()) {
        finishPrime();
        return;
      }
      if (attemptsLeft <= 0) {
        finishPrime();
        return;
      }
      requestAnimationFrame(() => waitForBind(attemptsLeft - 1));
    };
    waitForBind(120);
  });

  return idleStatePrimePromise;
}

/** Visual-only idle overlay. Never modifies contentArea or contentLayersReady. */
function showIdleScreen(options = {}) {
  idleStateLastOptions = { ...options };
  if (!idleStateShell || !idleStateFrame) {
    return Promise.resolve();
  }

  resetDocumentLayoutViewport();
  if (pairingArea) pairingArea.style.display = "none";
  if (contentPanel) contentPanel.style.display = "none";

  const forceReload =
    options.forceIdleReload === true || isLayoutViewportInflated();
  if (forceReload) {
    invalidateIdleStateFramePriming();
  }

  return primeIdleStateFrame().then(() => {
    bindIdleStateFrame(options);
    idleStateShell.style.display = "block";
    idleStateShell.setAttribute("aria-hidden", "false");
  });
}

function hideIdleScreen() {
  if (!idleStateShell) return;
  idleStateShell.style.display = "none";
  idleStateShell.setAttribute("aria-hidden", "true");
  try {
    idleStateFrame?.contentWindow?.TomorrowQuietState?.stopClock();
  } catch (_) {}
}

function renderBrandFallback() {
  return showIdleScreen({ variant: "fallback" });
}

function showPairedIdleScreen() {
  return showIdleScreen({ variant: "paired" });
}

const PAIRING_CODE_ROLL_CHARS = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
const PAIRING_CODE_ROLL_INTERVAL_MS = 48;
let pairingCodeRollTimer = null;

function stopPairingCodeRollAnimation() {
  if (pairingCodeRollTimer) {
    clearInterval(pairingCodeRollTimer);
    pairingCodeRollTimer = null;
  }
  if (activationHeadlineEl) {
    activationHeadlineEl.classList.remove("pairing-code-rolling");
  }
}

function randomPairingCodeRollText(length = 6) {
  let out = "";
  for (let i = 0; i < length; i += 1) {
    out += PAIRING_CODE_ROLL_CHARS[
      Math.floor(Math.random() * PAIRING_CODE_ROLL_CHARS.length)
    ];
  }
  return out;
}

function startPairingCodeRollAnimation() {
  stopPairingCodeRollAnimation();
  hideIdleScreen();
  if (pairingArea) pairingArea.style.display = "block";
  if (contentPanel) contentPanel.style.display = "none";
  setPairingActivationUiVisible(true);
  if (!activationHeadlineEl) return;

  activationHeadlineEl.classList.add("pairing-code-display", "pairing-code-rolling");
  activationHeadlineEl.textContent = randomPairingCodeRollText(6);
  if (activationInstructionsEl) {
    activationInstructionsEl.textContent = "Generating pairing code...";
  }

  pairingCodeRollTimer = setInterval(() => {
    if (!activationHeadlineEl?.classList.contains("pairing-code-rolling")) {
      stopPairingCodeRollAnimation();
      return;
    }
    activationHeadlineEl.textContent = randomPairingCodeRollText(6);
  }, PAIRING_CODE_ROLL_INTERVAL_MS);
}

function showPairingUI() {
  detachCmsSetupRemoteKeys();
  if (cmsSetupScreen) cmsSetupScreen.style.display = "none";
  hideIdleScreen();
  if (contentPanel) contentPanel.style.display = "none";
  if (pairingArea) pairingArea.style.display = "block";
}

function showPairingUiWaitingForCode() {
  showPairingUI();
  startPairingCodeRollAnimation();
}

function setPairingActivationUiVisible(visible) {
  const display = visible ? "" : "none";
  if (activationHeadlineEl) activationHeadlineEl.style.display = display;
  if (activationInstructionsEl) activationInstructionsEl.style.display = display;
}

function restoreActivationBrandCopy() {
  stopPairingCodeRollAnimation();
  setPairingActivationUiVisible(true);
  if (currentBrand) {
    applyBrand(currentBrand);
    return;
  }
  if (activationHeadlineEl) {
    activationHeadlineEl.textContent = "Connect this screen";
    activationHeadlineEl.classList.remove("pairing-code-display", "pairing-code-rolling");
  }
  if (activationInstructionsEl) {
    activationInstructionsEl.textContent =
      "Enter the code in your dashboard to activate this screen.";
  }
}

function showUnpairedPairingCode(code) {
  stopPairingCodeRollAnimation();
  hideIdleScreen();
  if (pairingArea) pairingArea.style.display = "block";
  if (contentPanel) contentPanel.style.display = "none";
  setPairingActivationUiVisible(true);
  if (activationHeadlineEl) {
    activationHeadlineEl.textContent = String(code || "------")
      .toUpperCase()
      .replace(/[^0-9A-Z]/g, "");
    activationHeadlineEl.classList.add("pairing-code-display");
  }
  if (activationInstructionsEl) {
    activationInstructionsEl.textContent = "Enter this code in the CMS pairing page";
  }
}

function isShowingPairingCode() {
  return (
    !!activationHeadlineEl?.classList.contains("pairing-code-display") ||
    !!activationHeadlineEl?.classList.contains("pairing-code-rolling")
  );
}

function isDevicePaired() {
  return !!(
    localStorage.getItem("pairedDeviceId") &&
    localStorage.getItem("pairingToken")
  );
}

function policyHasActivePlaylistNow(policy) {
  if (!policy || !policyHasPlayableContent(policy)) return false;
  return !!pickActiveScheduledPlaylist(policy);
}

function setPairingStatusMessage(message) {
  if (activationInstructionsEl && message) {
    activationInstructionsEl.textContent = message;
  }
}

// ============================================================
// First-launch intro screen (orientation picker)
// ============================================================
const INTRO_COMPLETED_KEY = "tomorrowos.introCompleted";
const ORIENTATION_KEY = "tomorrowos.orientation";
const INTRO_HERO_TEXT = "TOMORROWOS";
/** BrightSign: no TV remote — auto-confirm after idle period. */
const BRIGHTSIGN_AUTO_CONFIRM_MS = 5000;
const BRIGHTSIGN_DEFAULT_CMS_HTTP = "http://192.168.1.105:3000";
let brightsignAutoConfirmTimer = null;

function clearBrightsignAutoConfirm() {
  if (brightsignAutoConfirmTimer) {
    clearTimeout(brightsignAutoConfirmTimer);
    brightsignAutoConfirmTimer = null;
  }
}

function scheduleBrightsignAutoConfirm(callback, delayMs = BRIGHTSIGN_AUTO_CONFIRM_MS) {
  if (!isBrightSignRuntime()) return;
  clearBrightsignAutoConfirm();
  brightsignAutoConfirmTimer = setTimeout(() => {
    brightsignAutoConfirmTimer = null;
    callback();
  }, delayMs);
}

function scheduleBrightsignCmsAutoConnect() {
  if (!isBrightSignRuntime()) return;
  if (!cmsSetupScreen || cmsSetupScreen.style.display === "none") return;
  scheduleBrightsignAutoConfirm(() => {
    void onCmsEndpointSaveClick({ source: "brightsign-auto" });
  });
}

let introInitialized = false;
let introSelectedOrient = null;
let introKeyHandlerAttached = false;
let introReselectMode = false;
let redKeyHandlerAttached = false;

function isIntroCompleted() {
  try {
    return localStorage.getItem(INTRO_COMPLETED_KEY) === "1";
  } catch (_) {
    return false;
  }
}

function markIntroCompleted(orientation) {
  try {
    localStorage.setItem(INTRO_COMPLETED_KEY, "1");
    if (orientation) localStorage.setItem(ORIENTATION_KEY, orientation);
  } catch (_) {}
}

function getSavedOrientation() {
  try {
    return localStorage.getItem(ORIENTATION_KEY) || "landscape";
  } catch (_) {
    return "landscape";
  }
}

function normalizeContentFit(value) {
  const fit = String(value || "").trim().toLowerCase();
  return VALID_CONTENT_FITS.has(fit) ? fit : "cover";
}

/** CSS object-fit value for the configured contentFit (stretch → fill). */
function cssObjectFitForContentFit(fit = getSavedContentFit()) {
  const normalized = normalizeContentFit(fit);
  if (normalized === "contain") return "contain";
  if (normalized === "stretch") return "fill";
  return "cover";
}

function getSavedContentFit() {
  try {
    return normalizeContentFit(localStorage.getItem(CONTENT_FIT_KEY) || TOMORROWOS_CONFIG.contentFit);
  } catch (_) {
    return normalizeContentFit(TOMORROWOS_CONFIG.contentFit);
  }
}

function markContentFit(fit) {
  const normalized = normalizeContentFit(fit);
  try {
    localStorage.setItem(CONTENT_FIT_KEY, normalized);
  } catch (_) {}
  return normalized;
}

function applyContentFit() {
  const fit = getSavedContentFit();
  const html = document.documentElement;
  if (!html) return fit;
  html.classList.remove(
    "content-fit-contain",
    "content-fit-cover",
    "content-fit-stretch"
  );
  html.classList.add(`content-fit-${fit}`);
  return fit;
}

function showIntroUI() {
  const introScreen = document.getElementById("introScreen");
  const introAmbient = document.getElementById("introAmbient");
  if (cmsSetupScreen) cmsSetupScreen.style.display = "none";
  if (pairingArea) pairingArea.style.display = "none";
  if (introAmbient) introAmbient.style.display = "block";
  if (introScreen) introScreen.style.display = "flex";
  initIntroScreen();
}

function hideIntroUI() {
  clearBrightsignAutoConfirm();
  const introScreen = document.getElementById("introScreen");
  const introAmbient = document.getElementById("introAmbient");
  if (introScreen) introScreen.style.display = "none";
  if (introAmbient) introAmbient.style.display = "none";
  detachIntroKeyHandler();
}

function initIntroScreen() {
  if (introInitialized) return;
  introInitialized = true;

  const heroLetters = document.getElementById("introHeroLetters");
  const hero = document.getElementById("introHero");
  const tagline = document.getElementById("introTagline");
  const orientSection = document.getElementById("introOrientSection");
  const nextSection = document.getElementById("introNextSection");

  let i = 0;
  function typeHero() {
    if (!heroLetters) return;
    if (i <= INTRO_HERO_TEXT.length) {
      heroLetters.textContent = INTRO_HERO_TEXT.slice(0, i);
      i += 1;
      setTimeout(typeHero, 140 + Math.random() * 60);
    } else {
      setTimeout(() => {
        if (hero) hero.classList.add("done");
        if (tagline) tagline.classList.add("show");
      }, 500);
      setTimeout(() => {
        if (orientSection) orientSection.classList.add("show");
        const firstCard = document.querySelector(".orient-card");
        if (firstCard) {
          try { firstCard.focus(); } catch (_) {}
        }
      }, 1300);
      setTimeout(() => {
        if (nextSection) nextSection.classList.add("show");
        if (isBrightSignRuntime()) {
          const landscape = document.querySelector('.orient-card[data-orient="landscape"]');
          if (landscape) selectOrient(landscape);
          scheduleBrightsignAutoConfirm(() => {
            if (introSelectedOrient) onIntroContinue();
          });
        }
      }, 1900);
    }
  }
  setTimeout(typeHero, 600);

  document.querySelectorAll(".orient-card").forEach((card) => {
    card.addEventListener("click", () => {
      clearBrightsignAutoConfirm();
      selectOrient(card);
    });
  });

  const nextBtn = document.getElementById("introNextBtn");
  if (nextBtn) {
    nextBtn.addEventListener("click", onIntroContinue);
  }

  attachIntroKeyHandler();
}

function selectOrient(card) {
  if (!card) return;
  clearBrightsignAutoConfirm();
  document.querySelectorAll(".orient-card").forEach((c) => c.classList.remove("selected"));
  card.classList.add("selected");
  introSelectedOrient = card.dataset.orient || "landscape";
  const nextBtn = document.getElementById("introNextBtn");
  if (nextBtn) nextBtn.classList.add("enabled");
}

function normalizeRemoteKeyEvent(ev) {
  const code = ev.keyCode;
  const key = ev.key;
  if (key === "ArrowLeft" || code === 37) return { key: "ArrowLeft", code: 37 };
  if (key === "ArrowUp" || code === 38) return { key: "ArrowUp", code: 38 };
  if (key === "ArrowRight" || code === 39) return { key: "ArrowRight", code: 39 };
  if (key === "ArrowDown" || code === 40) return { key: "ArrowDown", code: 40 };
  if (key === "Enter" || code === 13) return { key: "Enter", code: 13 };
  if (key === " " || code === 32) return { key: " ", code: 32 };
  return { key, code };
}

function onIntroKeyDown(ev) {
  const normalized = normalizeRemoteKeyEvent(ev);
  const key = normalized.key;
  const code = normalized.code;

  const cards = Array.from(document.querySelectorAll(".orient-card"));
  if (key === "1" || code === 49 || code === 97) {
    ev.preventDefault();
    if (cards[0]) selectOrient(cards[0]);
    return;
  }
  if (key === "2" || code === 50 || code === 98) {
    ev.preventDefault();
    if (cards[1]) selectOrient(cards[1]);
    return;
  }
  if (key === "3" || code === 51 || code === 99) {
    ev.preventDefault();
    if (cards[2]) selectOrient(cards[2]);
    return;
  }

  // Back / Return during re-selection cancels and reloads.
  const isBackKey = key === "XF86Back" || key === "GoBack" || key === "Escape" || code === 10009 || code === 27;
  if (isBackKey && introReselectMode) {
    ev.preventDefault();
    ev.stopPropagation();
    introReselectMode = false;
    markOrientReloadPending();
    location.reload();
    return;
  }

  const introScreen = document.getElementById("introScreen");
  const isPortraitLayout = !!(introScreen && introScreen.classList.contains("intro-portrait"));
  const prevCardKey = isPortraitLayout ? "ArrowUp" : "ArrowLeft";
  const nextCardKey = isPortraitLayout ? "ArrowDown" : "ArrowRight";

  const nextBtn = document.getElementById("introNextBtn");
  const focused = document.activeElement;
  const idx = cards.indexOf(focused);

  if (key === prevCardKey && idx > 0) {
    ev.preventDefault();
    cards[idx - 1].focus();
    return;
  }
  if (key === nextCardKey && idx >= 0 && idx < cards.length - 1) {
    ev.preventDefault();
    cards[idx + 1].focus();
    return;
  }
  if (key === "ArrowDown" && idx >= 0 && nextBtn && nextBtn.classList.contains("enabled")) {
    ev.preventDefault();
    nextBtn.focus();
    return;
  }
  if (key === "ArrowUp" && focused === nextBtn) {
    ev.preventDefault();
    const sel = cards.find((c) => c.classList.contains("selected")) || cards[0];
    if (sel) sel.focus();
    return;
  }
  if (key === "Enter" || key === " ") {
    if (focused && focused.classList && focused.classList.contains("orient-card")) {
      ev.preventDefault();
      selectOrient(focused);
      return;
    }
    if (focused === nextBtn && nextBtn.classList.contains("enabled")) {
      ev.preventDefault();
      onIntroContinue();
    }
  }
}

function attachIntroKeyHandler() {
  if (introKeyHandlerAttached) return;
  document.addEventListener("keydown", onIntroKeyDown, true);
  introKeyHandlerAttached = true;
}

function detachIntroKeyHandler() {
  if (!introKeyHandlerAttached) return;
  document.removeEventListener("keydown", onIntroKeyDown, true);
  introKeyHandlerAttached = false;
}

function onIntroContinue() {
  if (!introSelectedOrient) return;
  clearBrightsignAutoConfirm();
  const oldOrient = getSavedOrientation();
  const newOrient = introSelectedOrient;
  markIntroCompleted(newOrient);

  // During orientation re-selection (Red A flow), reload the page so the
  // app reinitializes cleanly with the new orientation regardless of
  // whether it changed ??keeps state simple and avoids half-rotated UI.
  if (introReselectMode) {
    introReselectMode = false;
    markOrientReloadPending();
    location.reload();
    return;
  }

  hideIntroUI();
  proceedAfterIntro();
}

// ============================================================
// Orientation re-selection via the remote's RED (A) button.
// Available any time after the user has finished the first-launch
// intro. Pressing RED stops current playback and re-shows the intro
// using the layout that matches the CURRENT orientation:
//   - landscape  ?? landscape intro layout
//   - portrait-* ?? portrait intro layout (vertical card stack)
// ============================================================
function resetIntroState() {
  introInitialized = false;
  introSelectedOrient = null;

  const heroLetters = document.getElementById("introHeroLetters");
  const hero = document.getElementById("introHero");
  const tagline = document.getElementById("introTagline");
  const orientSection = document.getElementById("introOrientSection");
  const nextSection = document.getElementById("introNextSection");
  const nextBtn = document.getElementById("introNextBtn");

  if (heroLetters) heroLetters.textContent = "";
  if (hero) hero.classList.remove("done");
  if (tagline) tagline.classList.remove("show");
  if (orientSection) orientSection.classList.remove("show");
  if (nextSection) nextSection.classList.remove("show");
  if (nextBtn) nextBtn.classList.remove("enabled");

  document.querySelectorAll(".orient-card").forEach((c) => c.classList.remove("selected"));
}

function applyIntroLayoutForCurrentOrient() {
  const introScreen = document.getElementById("introScreen");
  if (!introScreen) return;
  const orient = getSavedOrientation();
  if (orient === "portrait-right" || orient === "portrait-left") {
    introScreen.classList.add("intro-portrait");
  } else {
    introScreen.classList.remove("intro-portrait");
  }
}

function enterOrientationReselect() {
  if (introReselectMode) return;
  introReselectMode = true;

  console.log("[TomorrowOS][resume] enterOrientationReselect ??current player state", {
    activePlaylistKey: playerState.activePlaylistKey,
    currentItemIndex: playerState.currentItemIndex,
    currentItemStartedAt: playerState.currentItemStartedAt,
    hasPolicy: !!playerState.policy
  });

  // Snapshot what's currently playing BEFORE we tear it down ??the
  // reload after orientation change will read this and resume from
  // the same item + position.
  try { saveResumeState({ reason: "orient" }); } catch (err) {
    console.error("[TomorrowOS][resume] saveResumeState threw:", err);
  }
  try {
    if (playerState.policy) persistPolicyCache(playerState.policy);
  } catch (err) {
    console.error("[TomorrowOS][resume] persistPolicyCache on orient threw:", err);
  }

  // Stop everything currently happening ??the page will reload after
  // the user makes a selection (or cancels via Back).
  try { stopPlayback(); } catch (_) {}
  detachCmsSetupRemoteKeys();

  if (cmsSetupScreen) cmsSetupScreen.style.display = "none";
  if (pairingArea) pairingArea.style.display = "none";
  const contentPanel = document.getElementById("contentPanel");
  if (contentPanel) contentPanel.style.display = "none";

  applyIntroLayoutForCurrentOrient();
  resetIntroState();
  showIntroUI();
}

function registerTvColorKeys() {
  // BrightSign: remote input via USB keyboard or CEC bridge in platform-brightsign.js.
}

const HIDDEN_MENU_ZERO_PRESS_COUNT = 4;
const HIDDEN_MENU_ZERO_SEQUENCE_MS = 2500;

let hiddenDeviceMenuOpen = false;
let hiddenMenuZeroPressCount = 0;
let hiddenMenuZeroPressTimer = null;
let hiddenMenuHandlerAttached = false;

function isTvReturnKey(ev) {
  const k = ev.key;
  const code = ev.keyCode;
  return (
    k === "XF86Back" ||
    k === "GoBack" ||
    k === "Escape" ||
    k === "Return" ||
    code === 10009 ||
    code === 27 ||
    code === 461
  );
}

function isHiddenMenuZeroKey(ev) {
  const k = ev.key;
  const code = ev.keyCode;
  return k === "0" || code === 48 || code === 96;
}

function resetHiddenMenuZeroSequence() {
  hiddenMenuZeroPressCount = 0;
  if (hiddenMenuZeroPressTimer) {
    clearTimeout(hiddenMenuZeroPressTimer);
    hiddenMenuZeroPressTimer = null;
  }
}

function formatHiddenMenuValue(value) {
  if (value == null || value === "") return "--";
  return String(value);
}

function formatHiddenMenuUptime(sec) {
  if (!Number.isFinite(sec) || sec < 0) return "--";
  const totalSec = Math.floor(sec);
  const days = Math.floor(totalSec / 86400);
  const hours = Math.floor((totalSec % 86400) / 3600);
  const minutes = Math.floor((totalSec % 3600) / 60);
  const seconds = totalSec % 60;
  const parts = [];
  if (days > 0) parts.push(`${days}d`);
  if (days > 0 || hours > 0) parts.push(`${hours}h`);
  if (days > 0 || hours > 0 || minutes > 0) parts.push(`${minutes}m`);
  parts.push(`${seconds}s`);
  return parts.join(" ");
}

function onHiddenMenuKeyDown(ev) {
  if (hiddenDeviceMenuOpen) {
    if (!isTvReturnKey(ev)) return;
    ev.preventDefault();
    ev.stopPropagation();
    closeHiddenDeviceMenu();
    return;
  }

  if (!isHiddenMenuZeroKey(ev)) {
    resetHiddenMenuZeroSequence();
    return;
  }

  ev.preventDefault();
  ev.stopPropagation();

  hiddenMenuZeroPressCount += 1;
  clearTimeout(hiddenMenuZeroPressTimer);
  hiddenMenuZeroPressTimer = setTimeout(resetHiddenMenuZeroSequence, HIDDEN_MENU_ZERO_SEQUENCE_MS);

  if (hiddenMenuZeroPressCount < HIDDEN_MENU_ZERO_PRESS_COUNT) return;

  resetHiddenMenuZeroSequence();
  openHiddenDeviceMenu();
}

function attachHiddenMenuHandler() {
  if (hiddenMenuHandlerAttached) return;
  document.addEventListener("keydown", onHiddenMenuKeyDown, true);
  hiddenMenuHandlerAttached = true;
  console.log("[TomorrowOS] hidden menu listener attached (0000)");
}

function onRedKeyDown(ev) {
  // 403 = Samsung Tizen Red (A). 116 / "ColorF0Red" / "MediaRed" cover
  // alternative firmware variants we've seen in the field.
  const isRedKey =
    ev.keyCode === 403 ||
    ev.keyCode === 116 ||
    ev.key === "ColorF0Red" ||
    ev.key === "MediaRed" ||
    ev.code === "ColorF0Red";
  if (!isRedKey) return;
  console.log("[TomorrowOS] Red(A) key detected", {
    keyCode: ev.keyCode,
    key: ev.key,
    introReselectMode,
    introCompleted: isIntroCompleted(),
  });
  if (introReselectMode) return;
  // On first launch (no orientation completed yet) the intro is already
  // showing; don't re-trigger it.
  if (!isIntroCompleted()) return;
  ev.preventDefault();
  ev.stopPropagation();
  enterOrientationReselect();
}

function attachRedKeyHandler() {
  if (redKeyHandlerAttached) return;
  // Use capture-phase so we run before any per-screen handler can swallow it.
  document.addEventListener("keydown", onRedKeyDown, true);
  window.addEventListener("keydown", onRedKeyDown, true);
  redKeyHandlerAttached = true;
  console.log("[TomorrowOS] red-key listener attached");
}

function applyOrientation() {
  const orient = getSavedOrientation();
  const html = document.documentElement;
  if (!html) return;
  html.classList.remove(
    "orient-landscape",
    "orient-portrait-right",
    "orient-portrait-left"
  );
  html.classList.add(`orient-${orient}`);
  resetDocumentLayoutViewport();

  const page = getIdleStatePage();
  const orientChanged = idleStateLoadedSrc && idleStateLoadedSrc !== page;
  if (orientChanged && idleStateShell?.style.display !== "none" && idleStateLastOptions) {
    invalidateIdleStateFramePriming();
    idleStateShell.style.display = "none";
    idleStateShell.setAttribute("aria-hidden", "true");
    showIdleScreen(idleStateLastOptions);
  }
}

function proceedAfterIntro() {
  applyOrientation();
  applyContentFit();
  // Dump full localStorage snapshot of resume-related keys so we can debug.
  try {
    console.log("[TomorrowOS][resume] proceedAfterIntro startup snapshot", {
      resumeState: localStorage.getItem(RESUME_STATE_KEY),
      policyCacheBytes: (localStorage.getItem(POLICY_CACHE_KEY) || "").length,
      pairedDeviceId: localStorage.getItem("pairedDeviceId"),
      orientation: localStorage.getItem(ORIENTATION_KEY),
      contentFit: localStorage.getItem(CONTENT_FIT_KEY)
    });
  } catch (_) {}
  const stored = getStoredCmsEndpoint();
  if (stored) {
    setCmsEndpoints(stored);
    const resumeContent = shouldResumeContentOnStartup();
    if (!isDevicePaired()) {
      discardStalePlaybackCache();
      showPairingUiWaitingForCode();
    } else if (resumeContent) {
      hidePairingShowContentShell();
    } else if (!shouldShowBrandFallbackOnStartup()) {
      showPairingUI();
    }

    const showFallbackOnStart = shouldShowBrandFallbackOnStartup();
    loadBrand()
      .catch((err) => console.error("[TomorrowOS] loadBrand on start:", err))
      .then(() => primeIdleStateFrame())
      .then(() => {
        if (showFallbackOnStart) return showPairedIdleScreen();
      });

    if (resumeContent && isDevicePaired()) {
      hydratePlaybackFromCache(
        peekRebootResumePending()
          ? "startup-reboot"
          : peekOrientReloadPending()
            ? "startup-orient"
            : peekRepairResumePending()
              ? "startup-repair"
              : "startup"
      ).catch((err) => {
        console.error("[TomorrowOS][resume] startup hydrate failed:", err);
      });
    }

    ensureOnOffTimerScheduler();
    connect();
  } else {
    const message = "CMS endpoint missing — check core/config.js";
    console.error("[TomorrowOS]", message);
    if (typeof window.__tomorrowShowBootError === "function") {
      window.__tomorrowShowBootError(message);
    }
  }
}

function applyBootConfig() {
  const orientation = VALID_CONFIG_ORIENTATIONS.has(TOMORROWOS_CONFIG.orientation)
    ? TOMORROWOS_CONFIG.orientation
    : "landscape";
  if (TOMORROWOS_CONFIG.orientation !== orientation) {
    console.warn(
      "[TomorrowOS] Invalid orientation in config.js, using landscape:",
      TOMORROWOS_CONFIG.orientation
    );
  }

  markIntroCompleted(orientation);

  const contentFit = markContentFit(TOMORROWOS_CONFIG.contentFit);
  applyContentFit();

  const normalized = normalizeCmsEndpointInput(TOMORROWOS_CONFIG.cmsEndpoint);
  if (!normalized) {
    if (isWindowsRuntime()) {
      console.warn("[TomorrowOS] cmsEndpoint missing — showing setup UI");
      return "needs-setup";
    }
    const message = `Invalid cmsEndpoint in config.js: ${TOMORROWOS_CONFIG.cmsEndpoint}`;
    console.error("[TomorrowOS]", message);
    if (typeof window.__tomorrowShowBootError === "function") {
      window.__tomorrowShowBootError(message);
    }
    return false;
  }

  persistCmsEndpoint(normalized);
  setCmsEndpoints(normalized);
  console.log("[TomorrowOS] Boot config applied", { orientation, contentFit, cmsEndpoint: normalized });
  return true;
}

async function ensureCmsReachableAndProceed() {
  const wsUrl = getStoredCmsEndpoint();
  if (!wsUrl) {
    if (typeof window.__tomorrowShowBootError === "function") {
      window.__tomorrowShowBootError("CMS endpoint missing — check core/config.js");
    }
    return;
  }

  if (typeof window.__tomorrowShowBootError === "function") {
    window.__tomorrowShowBootError("Connecting to CMS...");
  }

  const reachable = await resolveReachableCmsWebSocket(wsUrl, 15000);
  if (!reachable) {
    const message = `Could not connect to CMS at ${wsUrl}. Retrying in ${BOOT_CMS_RETRY_MS / 1000}s...`;
    console.warn("[TomorrowOS]", message);
    if (typeof window.__tomorrowShowBootError === "function") {
      window.__tomorrowShowBootError(message);
    }
    bootCmsRetryTimer = setTimeout(() => {
      void ensureCmsReachableAndProceed();
    }, BOOT_CMS_RETRY_MS);
    return;
  }

  if (reachable !== wsUrl) {
    persistCmsEndpoint(reachable);
    setCmsEndpoints(reachable);
  }

  if (bootCmsRetryTimer) {
    clearTimeout(bootCmsRetryTimer);
    bootCmsRetryTimer = null;
  }

  const bootBanner = document.getElementById("tomorrowBootBanner");
  if (bootBanner) bootBanner.classList.remove("is-visible");

  proceedAfterIntro();
}

async function onCmsEndpointSaveClick(options = {}) {
  if (!cmsEndpointInput || !cmsEndpointSaveBtn || !cmsSetupError) return;
  if (cmsEndpointSaveBtn.disabled) return;

  clearBrightsignAutoConfirm();

  const normalized = normalizeCmsEndpointInput(cmsEndpointInput.value);
  cmsSetupError.textContent = "";

  if (!normalized) {
    cmsSetupError.textContent =
      "Enter a valid URL starting with http:// or https:// (e.g. http://192.168.1.10:3000/)";
    requestAnimationFrame(() => focusCmsEndpointInput());
    return;
  }

  cmsEndpointSaveBtn.disabled = true;
  cmsSetupError.textContent = "Connecting...";

  const reachable = await resolveReachableCmsWebSocket(normalized, 15000);
  cmsEndpointSaveBtn.disabled = false;

  if (!reachable) {
    cmsSetupError.textContent =
      "Could not connect. Check the URL, firewall, and that the CMS is running.";
    if (options.source === "brightsign-auto") {
      scheduleBrightsignCmsAutoConnect();
    } else {
      requestAnimationFrame(() => focusCmsConnectButton());
    }
    return;
  }

  persistCmsEndpoint(reachable);
  setCmsEndpoints(reachable);
  clearPairedState();
  discardStalePlaybackCache();
  showPairingUiWaitingForCode();
  cmsSetupError.textContent = "";

  try {
    await loadBrand();
    await primeIdleStateFrame();
  } catch (err) {
    console.error("[TomorrowOS] loadBrand after connect:", err);
  }
  connect();
}

const activationHeadlineEl = document.getElementById("activationHeadline");
const activationInstructionsEl = document.getElementById("activationInstructions");

const DownloadStatus = document.getElementById("DownloadStatus");

const contentArea = document.getElementById("contentArea");
const contentPanel = document.getElementById("contentPanel");
const pairingArea = document.getElementById("pairingArea");
const idleStateShell = document.getElementById("idleStateShell");
const idleStateFrame = document.getElementById("idleStateFrame");
let idleStateLoadedSrc = null;
let idleStateLastOptions = null;
let idleStatePrimed = false;
let idleStatePrimePromise = null;
const brandLogoEl = document.getElementById("brandLogo");
const MIN_POLICY_SCHEDULER_MS = 1000;
const MAX_POLICY_SCHEDULER_MS = 60000;
const CONTENT_CROSSFADE_MS = 300;
const BRIGHTSIGN_HTML_HANDOFF_PAINT_MS = 120;
const BRIGHTSIGN_IMAGE_TO_VIDEO_HIDE_DELAY_MS = 300;
const BRIGHTSIGN_IMAGE_TO_VIDEO_SWAP_DELAY_MS = 420;

let currentBrand = null;
const playerState = {
  policy: null,
  activePlaylist: null,
  activePlaylistKey: null,
  playlistPlaybackActive: false,
  playlistTimer: null,
  contentTimer: null,
  activeElement: null,
  activeWidget: null,
  backgroundCacheSession: 0,
  contentLayerActive: 0,
  contentLayersReady: false,
  currentItemIndex: 0,
  currentItemStartedAt: 0,
  currentItemDurationMs: 0,
  /** Local file path of the video currently mounted for playback. */
  activeVideoLocalPath: null,
  pendingVideoRemount: false,
  /** Resume hint only (re-pair/reboot); never continue a playlist removed from policy. */
  orphanedPlayback: null,
  /** Next image pre-mounted on the back layer (image->image and video->image handoff). */
  prefetchedImageItem: null,
  /** Tracks in-flight image prefetch to avoid decode at handoff. */
  prefetchedImageTask: null,
  /** Frozen item list for the current loop; hot-updates apply on next wrap to index 0. */
  playlistLoopItems: null,
  /** In-memory resume points for playlists interrupted by scheduled takeovers. */
  playlistHandoffResume: {}
};

/** Bumped on each applyPolicy so stale scheduler ticks are ignored. */
let policyApplyGeneration = 0;

/** Bumped on stop/teardown so in-flight playlist advances cannot remount video. */
let playbackGeneration = 0;

function bumpPlaybackGeneration() {
  playbackGeneration += 1;
  return playbackGeneration;
}

function isPlaybackGenerationCurrent(gen) {
  return gen === playbackGeneration;
}

/** Serializes re-pair cache hydration (pairing.verified vs setPolicy race). */
let repairRehydratePromise = null;

// ============================================================
// Playback resume across reload (used by orientation re-select)
// ============================================================
const RESUME_STATE_KEY = "tomorrowos.resumeState";
const POLICY_CACHE_KEY = "tomorrowos.cachedPolicy";
const ORIENT_RELOAD_KEY = "tomorrowos.orientReload";
const REBOOT_RESUME_KEY = "tomorrowos.rebootResume";
const REPAIR_RESUME_KEY = "tomorrowos.repairResume";
const REPAIR_RESUME_META_KEY = "tomorrowos.repairResumeMeta";
const ON_OFF_TIMER_KEY = "tomorrowos.onOffTimer";
const ON_OFF_TIMER_TICK_MS = 30 * 1000;
const RESUME_MAX_AGE_MS = 60000;
const RESUME_REBOOT_MAX_AGE_MS = 30 * 60 * 1000;
const RESUME_REPAIR_MAX_AGE_MS = 24 * 60 * 60 * 1000;

let onOffTimerTick = null;
let lastAppliedPanelMute = null;

function parseHhMmToMinutes(value) {
  const match = /^([01]\d|2[0-3]):([0-5]\d)$/.exec(String(value || "").trim());
  if (!match) return null;
  return Number(match[1]) * 60 + Number(match[2]);
}

function normalizeOnOffTimerLocal(input) {
  if (!input || typeof input !== "object") return null;
  const turnOnAt = String(input.turnOnAt || "").trim();
  const turnOffAt = String(input.turnOffAt || "").trim();
  if (parseHhMmToMinutes(turnOnAt) == null || parseHhMmToMinutes(turnOffAt) == null) {
    return null;
  }
  if (turnOnAt === turnOffAt) return null;
  return { turnOnAt, turnOffAt };
}

function loadOnOffTimer() {
  try {
    const raw = localStorage.getItem(ON_OFF_TIMER_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    const normalized = normalizeOnOffTimerLocal(parsed);
    // Drop legacy `enabled` from device storage (schedule = on/off times only).
    if (
      normalized &&
      parsed &&
      typeof parsed === "object" &&
      Object.prototype.hasOwnProperty.call(parsed, "enabled")
    ) {
      try {
        localStorage.setItem(ON_OFF_TIMER_KEY, JSON.stringify(normalized));
      } catch (_) {}
    }
    return normalized;
  } catch (_) {
    return null;
  }
}

function persistOnOffTimer(timer) {
  const normalized = normalizeOnOffTimerLocal(timer);
  if (!normalized) {
    try {
      localStorage.removeItem(ON_OFF_TIMER_KEY);
    } catch (_) {}
    return null;
  }
  try {
    localStorage.setItem(ON_OFF_TIMER_KEY, JSON.stringify(normalized));
  } catch (_) {}
  return normalized;
}

async function setPanelMuteState(muted) {
  const platform = getPlatform();
  if (typeof platform?.setDisplayMuted !== "function") {
    throw new Error("BrightSign display mute API not available");
  }
  // Power-save disables HDMI output; player stays powered / CMS-connected.
  await platform.setDisplayMuted(muted);
  lastAppliedPanelMute = muted;
}

function shouldScreenBeOn(timer, now = new Date()) {
  if (!timer) return true;
  const onMinutes = parseHhMmToMinutes(timer.turnOnAt);
  const offMinutes = parseHhMmToMinutes(timer.turnOffAt);
  if (onMinutes == null || offMinutes == null) return true;
  const nowMinutes = now.getHours() * 60 + now.getMinutes();
  if (onMinutes < offMinutes) {
    return nowMinutes >= onMinutes && nowMinutes < offMinutes;
  }
  // Overnight window (e.g. on 22:00, off 06:00).
  return nowMinutes >= onMinutes || nowMinutes < offMinutes;
}

async function applyOnOffTimerNow(reason) {
  const timer = loadOnOffTimer();
  if (!timer) {
    // No schedule: always restore HDMI output. Leaving power-save on after
    // clear/reboot made BrightSign displays permanently blank.
    if (lastAppliedPanelMute === false) {
      return { applied: false, muted: false, timer: null };
    }
    try {
      await setPanelMuteState(false);
      console.log(
        "[TomorrowOS][onOffTimer] power-save OFF (no timer)",
        reason || ""
      );
      return { applied: true, muted: false, timer: null };
    } catch (err) {
      console.error("[TomorrowOS][onOffTimer] unmute failed:", err);
      return {
        applied: false,
        muted: lastAppliedPanelMute === true,
        timer: null,
        error: err.message
      };
    }
  }

  const screenOn = shouldScreenBeOn(timer);
  const muted = !screenOn;
  if (lastAppliedPanelMute === muted) {
    return { applied: false, muted, timer };
  }
  try {
    await setPanelMuteState(muted);
    console.log(
      "[TomorrowOS][onOffTimer] power-save",
      muted ? "ON" : "OFF",
      timer.turnOnAt,
      "→",
      timer.turnOffAt,
      reason || ""
    );
    return { applied: true, muted, timer };
  } catch (err) {
    console.error("[TomorrowOS][onOffTimer] setDisplayMuted failed:", err);
    return { applied: false, muted, timer, error: err.message };
  }
}

function ensureOnOffTimerScheduler() {
  if (onOffTimerTick) return;
  void applyOnOffTimerNow("scheduler-start");
  onOffTimerTick = setInterval(() => {
    void applyOnOffTimerNow("tick");
  }, ON_OFF_TIMER_TICK_MS);
}

async function setOnOffTimerFromCms(timerInput) {
  const clearRequested =
    timerInput == null ||
    timerInput === false ||
    (typeof timerInput === "object" &&
      (timerInput.clear === true ||
        (Object.keys(timerInput).length === 0)));

  if (clearRequested) {
    persistOnOffTimer(null);
    const result = await applyOnOffTimerNow("cms-clear");
    return {
      onOffTimer: null,
      muted: Boolean(result.muted),
      applied: Boolean(result.applied),
      message: "On/off timer cleared; display power-save turned off"
    };
  }

  const normalized = persistOnOffTimer(timerInput);
  if (!normalized) {
    throw new Error("Invalid on/off timer (turnOnAt/turnOffAt required, HH:mm, different)");
  }
  ensureOnOffTimerScheduler();
  const result = await applyOnOffTimerNow("cms-set");
  return {
    onOffTimer: normalized,
    muted: Boolean(result.muted),
    applied: Boolean(result.applied),
    // Help CMS/debug distinguish "already in desired state" from failure.
    screenOn: result.muted ? false : true,
    message: result.muted
      ? "On/off timer saved; display quiet (black overlay, device stays online)"
      : "On/off timer saved; display active"
  };
}

function getResumeMaxAgeMs(state) {
  if (state?.reason === "reboot") return RESUME_REBOOT_MAX_AGE_MS;
  if (state?.reason === "repair") return RESUME_REPAIR_MAX_AGE_MS;
  return RESUME_MAX_AGE_MS;
}

function markOrientReloadPending() {
  try {
    sessionStorage.setItem(ORIENT_RELOAD_KEY, "1");
  } catch (_) {}
}

function peekOrientReloadPending() {
  try {
    return sessionStorage.getItem(ORIENT_RELOAD_KEY) === "1";
  } catch (_) {
    return false;
  }
}

function clearOrientReloadPending() {
  try {
    sessionStorage.removeItem(ORIENT_RELOAD_KEY);
  } catch (_) {}
}

function markRebootResumePending() {
  try {
    localStorage.setItem(REBOOT_RESUME_KEY, "1");
  } catch (_) {}
}

function peekRebootResumePending() {
  try {
    return localStorage.getItem(REBOOT_RESUME_KEY) === "1";
  } catch (_) {
    return false;
  }
}

function clearRebootResumePending() {
  try {
    localStorage.removeItem(REBOOT_RESUME_KEY);
  } catch (_) {}
}

function markRepairResumePending() {
  try {
    localStorage.setItem(REPAIR_RESUME_KEY, "1");
  } catch (_) {}
}

function peekRepairResumePending() {
  try {
    return localStorage.getItem(REPAIR_RESUME_KEY) === "1";
  } catch (_) {
    return false;
  }
}

function clearRepairResumePending() {
  try {
    localStorage.removeItem(REPAIR_RESUME_KEY);
  } catch (_) {}
  clearRepairResumeMeta();
}

function clearRepairResumeMeta() {
  try {
    localStorage.removeItem(REPAIR_RESUME_META_KEY);
  } catch (_) {}
}

function loadRepairResumeMeta() {
  try {
    const raw = localStorage.getItem(REPAIR_RESUME_META_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== "object") return null;
    return parsed;
  } catch (_) {
    return null;
  }
}

function peekResumeStatePlaylistKey() {
  try {
    const raw = localStorage.getItem(RESUME_STATE_KEY);
    if (!raw) return null;
    const state = JSON.parse(raw);
    if (!state || typeof state !== "object") return null;
    if (Date.now() - (state.savedAt || 0) > getResumeMaxAgeMs(state)) return null;
    return state.playlistKey || null;
  } catch (_) {
    return null;
  }
}

function isResumePlaybackPending() {
  return (
    peekOrientReloadPending() ||
    peekRebootResumePending() ||
    peekRepairResumePending() ||
    hasPendingResumeState()
  );
}

function shouldSkipVideoSeekOnResume() {
  return (
    peekOrientReloadPending() ||
    peekRebootResumePending() ||
    peekRepairResumePending()
  );
}

function delayMs(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function waitForNextPaintFrames(frameCount = 2) {
  return new Promise((resolve) => {
    const step = (remaining) => {
      if (remaining <= 0) {
        resolve();
        return;
      }
      requestAnimationFrame(() => step(remaining - 1));
    };
    step(Math.max(1, frameCount));
  });
}

function persistPolicyCache(policy) {
  try {
    if (!policy) {
      localStorage.removeItem(POLICY_CACHE_KEY);
      console.log("[TomorrowOS][resume] policy cache cleared (null policy)");
      return;
    }
    const payload = JSON.stringify({ policy, savedAt: Date.now() });
    localStorage.setItem(POLICY_CACHE_KEY, payload);
    console.log(
      "[TomorrowOS][resume] policy cached to localStorage",
      "bytes:", payload.length,
      "playlists:", Array.isArray(policy?.playlists) ? policy.playlists.length : 0
    );
  } catch (err) {
    console.error("[TomorrowOS][resume] persistPolicyCache failed:", err);
  }
}

function loadPolicyCache() {
  try {
    const raw = localStorage.getItem(POLICY_CACHE_KEY);
    if (!raw) {
      console.log("[TomorrowOS][resume] loadPolicyCache: nothing in localStorage");
      return null;
    }
    const parsed = JSON.parse(raw);
    if (!parsed || !parsed.policy) {
      console.warn("[TomorrowOS][resume] loadPolicyCache: parsed but no .policy field");
      return null;
    }
    console.log(
      "[TomorrowOS][resume] loadPolicyCache: hit",
      "savedAt:", new Date(parsed.savedAt || 0).toISOString(),
      "playlists:", Array.isArray(parsed.policy.playlists) ? parsed.policy.playlists.length : 0
    );
    return parsed.policy;
  } catch (err) {
    console.error("[TomorrowOS][resume] loadPolicyCache failed:", err);
    return null;
  }
}

function clearPolicyCache() {
  try {
    localStorage.removeItem(POLICY_CACHE_KEY);
  } catch (_) {}
}

function clearPersistedResumeState() {
  try {
    localStorage.removeItem(RESUME_STATE_KEY);
  } catch (_) {}
}

/** Drop cached policy/resume flags when the device must not auto-play. */
function discardStalePlaybackCache() {
  clearPolicyCache();
  clearPersistedResumeState();
  clearRebootResumePending();
  clearOrientReloadPending();
  clearRepairResumePending();
}

/**
 * Before CMS unpair: remember playback if content was on screen so re-pair can resume.
 * If only static was showing, clear repair snapshot (static after re-pair).
 */
function wasDisplayingPlaybackContent() {
  if (playerState.playlistPlaybackActive) return true;
  if (playerState.activePlaylistKey && playerState.policy) return true;
  const contentVisible =
    contentPanel?.style.display !== "none" &&
    contentArea?.style.display !== "none";
  return contentVisible && !isIdleScreenVisible();
}

function prepareRepairResumeSnapshot() {
  const hadActiveContent = wasDisplayingPlaybackContent();
  if (!hadActiveContent) {
    clearRepairResumePending();
    clearPolicyCache();
    clearPersistedResumeState();
    console.log("[TomorrowOS][resume] unpair with no active playback ??static after re-pair");
    return;
  }

  if (playerState.policy) persistPolicyCache(playerState.policy);
  saveResumeState({ reason: "repair" });
  markRepairResumePending();
  try {
    localStorage.setItem(
      REPAIR_RESUME_META_KEY,
      JSON.stringify({
        playlistKey: playerState.activePlaylistKey,
        savedAt: Date.now()
      })
    );
  } catch (err) {
    console.warn("[TomorrowOS][resume] repair meta save failed:", err);
  }
  console.log("[TomorrowOS][resume] saved playback snapshot before unpair for re-pair resume", {
    playlistKey: playerState.activePlaylistKey
  });
}

function primeResumePlaybackHints(policy) {
  if (!policy) return;
  const key =
    loadRepairResumeMeta()?.playlistKey || peekResumeStatePlaylistKey();
  if (!key) return;
  const playlist = findPlaylistInPolicyByKey(policy, key);
  if (!playlist) return;
  playerState.orphanedPlayback = { playlist, key };
}

function pickStoredResumePlaylist(policy) {
  const key =
    loadRepairResumeMeta()?.playlistKey || peekResumeStatePlaylistKey();
  if (key) {
    const playlist = findPlaylistInPolicyByKey(policy, key);
    if (playlist) return { playlist, key };
  }
  return pickFirstPlayablePlaylist(policy);
}

function shouldHydratePlaybackFromCache() {
  if (!isDevicePaired()) return false;
  const cached = loadPolicyCache();
  if (!cached || !policyHasPlayableContent(cached)) return false;
  if (isResumePlaybackPending()) return true;
  return policyHasActivePlaylistNow(cached);
}

async function completeRepairRehydrate(source = "re-pair") {
  if (!peekRepairResumePending()) return false;
  if (repairRehydratePromise) return repairRehydratePromise;
  repairRehydratePromise = hydratePlaybackFromCache(source).finally(() => {
    repairRehydratePromise = null;
  });
  return repairRehydratePromise;
}

function scheduleRepairResumeFallbackCheck() {
  setTimeout(() => {
    if (!peekRepairResumePending()) return;
    if (playerState.playlistPlaybackActive) {
      clearRepairResumePending();
      return;
    }
    console.warn("[TomorrowOS][resume] re-pair resume timed out ??showing static");
    clearRepairResumePending();
    showPairedIdleScreen();
  }, 8000);
}

async function hydratePlaybackFromCache(source = "cache") {
  if (!shouldHydratePlaybackFromCache()) {
    console.log("[TomorrowOS][resume] hydrate skipped shouldHydrate=false", source, {
      paired: isDevicePaired(),
      repair: peekRepairResumePending(),
      reboot: peekRebootResumePending(),
      cacheBytes: (localStorage.getItem(POLICY_CACHE_KEY) || "").length
    });
    return false;
  }
  if (playerState.playlistPlaybackActive) {
    hidePairingShowContentShell();
    hideIdleScreen();
    return true;
  }

  const cached = loadPolicyCache();
  if (!cached) {
    console.warn("[TomorrowOS][resume] hydrate skipped - no cached policy", source);
    return false;
  }

  hidePairingShowContentShell();
  hideIdleScreen();
  primeResumePlaybackHints(cached);

  try {
    console.log("[TomorrowOS][resume] hydrating playback from cache", source);
    await applyPolicy(cached);
    if (playerState.playlistPlaybackActive) {
      console.log("[TomorrowOS][resume] playback resumed", source);
      return true;
    }
    console.log("[TomorrowOS][resume] hydrate did not start playback", source);
    return false;
  } catch (err) {
    console.error("[TomorrowOS][resume] hydrate failed", source, err);
    return false;
  }
}

function resetPlaybackSession(options = {}) {
  playerState.policy = null;
  playerState.activePlaylist = null;
  playerState.activePlaylistKey = null;
  playerState.playlistPlaybackActive = false;
  playerState.orphanedPlayback = null;
  playerState.playlistHandoffResume = {};
  playerState.contentLayersReady = false;
  playerState.contentLayerActive = 0;
  playerState.activeElement = null;
  playerState.backgroundCacheSession += 1;

  clearPolicySchedulerTimer();
  clearPlaybackTimers();
  stopActiveWidget();
  stopPlayback();
  cancelActiveDownloads();
  clearInFlightCacheMarkers();
  clearInFlightWidgetMarkers();
  clearImageDecodeWarmCache();

  releaseAllPlaybackVideos();
  if (contentArea) {
    contentArea.innerHTML = "";
    contentArea.style.display = "none";
  }

  if (!options.preserveRepairResume) {
    discardStalePlaybackCache();
  }
}

function policyHasPlayableContent(policy) {
  const playlists = Array.isArray(policy?.playlists) ? policy.playlists : [];
  for (let i = 0; i < playlists.length; i += 1) {
    const items = Array.isArray(playlists[i]?.items) ? playlists[i].items : [];
    for (let j = 0; j < items.length; j += 1) {
      const url = items[j]?.url;
      if (typeof url === "string" && url.trim()) return true;
    }
  }
  return false;
}

function hidePairingShowContentShell() {
  hideIdleScreen();
  if (pairingArea) pairingArea.style.display = "none";
  if (contentPanel) contentPanel.style.display = "block";
  if (contentArea) contentArea.style.display = "block";
}

function shouldResumeContentOnStartup() {
  return shouldHydratePlaybackFromCache();
}

function shouldShowBrandFallbackOnStartup() {
  if (!isDevicePaired()) return false;
  if (shouldResumeContentOnStartup()) return false;
  return true;
}

function hasPendingResumeState() {
  try {
    const raw = localStorage.getItem(RESUME_STATE_KEY);
    if (!raw) {
      console.log("[TomorrowOS][resume] hasPendingResumeState: no entry");
      return false;
    }
    const state = JSON.parse(raw);
    if (!state || typeof state !== "object") {
      console.warn("[TomorrowOS][resume] hasPendingResumeState: bad shape");
      return false;
    }
    const age = Date.now() - (state.savedAt || 0);
    const maxAge = getResumeMaxAgeMs(state);
    if (age > maxAge) {
      console.warn("[TomorrowOS][resume] hasPendingResumeState: expired", "ageMs:", age, "maxAge:", maxAge);
      return false;
    }
    console.log("[TomorrowOS][resume] hasPendingResumeState: present", state);
    return true;
  } catch (err) {
    console.error("[TomorrowOS][resume] hasPendingResumeState failed:", err);
    return false;
  }
}

function resolvePlaylistKey(playlist) {
  if (!playlist || !playerState.policy) return null;
  const playlists = Array.isArray(playerState.policy.playlists)
    ? playerState.policy.playlists
    : [];
  for (let i = 0; i < playlists.length; i += 1) {
    if (playlists[i] === playlist) return getPlaylistKey(playlist, i);
  }
  const id = playlist.id != null ? String(playlist.id) : "";
  const name = playlist.name != null ? String(playlist.name) : "";
  for (let i = 0; i < playlists.length; i += 1) {
    const candidate = playlists[i];
    if (id && candidate?.id != null && String(candidate.id) === id) {
      return getPlaylistKey(candidate, i);
    }
    if (name && candidate?.name != null && String(candidate.name) === name) {
      return getPlaylistKey(candidate, i);
    }
  }
  return null;
}

function ensureActivePlaylistContext(playlist) {
  if (playlist) playerState.activePlaylist = playlist;
  if (!playerState.activePlaylistKey && playlist) {
    const key = resolvePlaylistKey(playlist);
    if (key) playerState.activePlaylistKey = key;
  }
  if (!playerState.activePlaylistKey && playerState.policy) {
    const active = pickActivePlaylist(playerState.policy);
    if (active) {
      playerState.activePlaylist = active.playlist;
      playerState.activePlaylistKey = active.key;
    }
  }
}

function saveResumeState(options = {}) {
  try {
    if (playerState.activePlaylist) {
      ensureActivePlaylistContext(playerState.activePlaylist);
    } else if (playerState.policy) {
      ensureActivePlaylistContext(null);
    }

    if (!playerState.activePlaylistKey) {
      console.warn(
        "[TomorrowOS][resume] saveResumeState skipped ??no playlist key",
        {
          playlistPlaybackActive: playerState.playlistPlaybackActive,
          hasPolicy: !!playerState.policy
        }
      );
      return;
    }
    const startedAt = playerState.currentItemStartedAt || Date.now();
    const elapsedMs = Math.max(0, Date.now() - startedAt);
    const state = {
      playlistKey: playerState.activePlaylistKey,
      itemIndex: playerState.currentItemIndex || 0,
      elapsedMs,
      itemDurationMs: playerState.currentItemDurationMs || 0,
      savedAt: Date.now(),
      reason:
        options.reason === "reboot"
          ? "reboot"
          : options.reason === "repair"
            ? "repair"
            : "orient"
    };
    localStorage.setItem(RESUME_STATE_KEY, JSON.stringify(state));
    console.log("[TomorrowOS][resume] saveResumeState wrote:", state);
  } catch (err) {
    console.error("[TomorrowOS][resume] saveResumeState failed:", err);
  }
}

function consumeResumeState(activePlaylistKey) {
  try {
    const raw = localStorage.getItem(RESUME_STATE_KEY);
    if (!raw) return null;
    localStorage.removeItem(RESUME_STATE_KEY);
    const state = JSON.parse(raw);
    if (!state || typeof state !== "object") return null;
    if (Date.now() - (state.savedAt || 0) > getResumeMaxAgeMs(state)) return null;
    if (state.playlistKey !== activePlaylistKey) return null;
    const idx = Number(state.itemIndex);
    if (!Number.isFinite(idx) || idx < 0) return null;
    return {
      itemIndex: idx,
      elapsedMs: Math.max(0, Number(state.elapsedMs) || 0)
    };
  } catch (_) {
    return null;
  }
}

function savePlaylistHandoffResume() {
  const key = playerState.activePlaylistKey;
  const playlist = playerState.activePlaylist;
  if (!key || !playlist || !playerState.playlistPlaybackActive) return;

  const loopItems = getCurrentLoopItems(playlist);
  if (!loopItems.length) return;
  const safeIndex = Math.max(
    0,
    Math.min(Number(playerState.currentItemIndex) || 0, loopItems.length - 1)
  );
  const item = loopItems[safeIndex];
  const itemUrl = getPlaylistItemUrl(item);
  if (!itemUrl) return;

  playerState.playlistHandoffResume[key] = {
    itemIndex: safeIndex,
    itemUrl,
    itemType: getContentType(itemUrl, item?.type),
    savedAt: Date.now()
  };

  console.log("[TomorrowOS][handoff-resume] saved interrupted playlist", {
    key,
    itemIndex: safeIndex,
    itemUrl: itemUrl.slice(-64)
  });
}

function consumePlaylistHandoffResume(playlistKey, playlist) {
  const state = playerState.playlistHandoffResume?.[playlistKey];
  if (!state) return null;
  delete playerState.playlistHandoffResume[playlistKey];

  const items = getPlayableItems(playlist);
  const idx = Number(state.itemIndex);
  if (!Number.isFinite(idx) || idx < 0 || idx >= items.length) return null;

  const item = items[idx];
  const itemUrl = getPlaylistItemUrl(item);
  const itemType = getContentType(itemUrl, item?.type);
  if (itemUrl !== state.itemUrl || itemType !== state.itemType) {
    console.log("[TomorrowOS][handoff-resume] discarded because item changed", {
      playlistKey,
      itemIndex: idx
    });
    return null;
  }

  console.log("[TomorrowOS][handoff-resume] consumed interrupted playlist", {
    playlistKey,
    itemIndex: idx,
    restartFromBeginning: true
  });

  return {
    itemIndex: idx,
    elapsedMs: 0
  };
}

const VIDEO_CACHE_STORAGE_KEY = "tomorrowos.videoCacheByUrl";
const IMAGE_CACHE_STORAGE_KEY = "tomorrowos.imageCacheByUrl";
const VIDEO_CACHE_DEDUP_STORAGE_KEY = "tomorrowos.videoCacheByDedup";
const IMAGE_CACHE_DEDUP_STORAGE_KEY = "tomorrowos.imageCacheByDedup";
const WIDGET_CACHE_STORAGE_KEY = "tomorrowos.widgetCacheBySource";
const VIDEO_EXTENSIONS = [".mp4", ".webm", ".m4v", ".mov", ".mkv", ".avi", ".3gp"];
const IMAGE_EXTENSIONS = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"];
const DEFAULT_WIDGET_ENTRY_FILE = "index.html";
const mediaCache = {
  videosByUrl: loadCacheIndex(VIDEO_CACHE_STORAGE_KEY),
  imagesByUrl: loadCacheIndex(IMAGE_CACHE_STORAGE_KEY),
  videosByDedup: loadCacheIndex(VIDEO_CACHE_DEDUP_STORAGE_KEY),
  imagesByDedup: loadCacheIndex(IMAGE_CACHE_DEDUP_STORAGE_KEY),
  widgetsBySource: loadCacheIndex(WIDGET_CACHE_STORAGE_KEY)
};
const inFlightCacheByKey = {};
const inFlightWidgetByKey = {};
const activeDownloads = {};


function isBrightSignRuntime() {
  return window.TomorrowPlatform?.id === "brightsign";
}

function isWindowsRuntime() {
  return window.TomorrowPlatform?.id === "windows";
}

function isNativePlayerRuntime() {
  return isBrightSignRuntime() || isWindowsRuntime();
}

function getPlatform() {
  return window.TomorrowPlatform || null;
}

function toBrightSignMediaUrl(localPath) {
  if (!isNativePlayerRuntime() || typeof localPath !== "string" || !localPath.trim()) {
    return localPath;
  }
  if (/^(https?:|file:|data:|blob:)/i.test(localPath)) return localPath;
  const platform = window.TomorrowPlatform;
  if (platform?.statLocalFile) {
    const stat = platform.statLocalFile(localPath);
    if (stat?.fileUri) return stat.fileUri;
  }
  if (platform?.toAbsPath && platform?.toFileUri) {
    return platform.toFileUri(platform.toAbsPath(localPath));
  }
  return localPath;
}

function getDeviceSerialNumber() {
  return getPlatform()?.getDeviceSerialNumber() || null;
}

const deviceId =
  getDeviceSerialNumber() ||
  localStorage.getItem("deviceId") ||
  null;

if (deviceId) localStorage.setItem("deviceId", deviceId);

// After switching identity to serial number, drop stale DUID-based pairing.
const serialForIdentity = getDeviceSerialNumber();
if (serialForIdentity) {
  const pairedId = localStorage.getItem("pairedDeviceId");
  if (pairedId && pairedId !== serialForIdentity) {
    localStorage.removeItem("pairedDeviceId");
    localStorage.removeItem("pairingToken");
    localStorage.removeItem("pairedAt");
  }
}

const PLAYER_SESSION_BOOT_MS_KEY = "tomorrowos.playerSessionBootMs";

function getPlayerBootUptimeSec() {
  if (isNativePlayerRuntime()) {
    const uptimeSec = window.TomorrowPlatform.getBootUptimeSec();
    if (Number.isFinite(uptimeSec) && uptimeSec >= 0) return uptimeSec;
  }

  let bootMs = Number(sessionStorage.getItem(PLAYER_SESSION_BOOT_MS_KEY));
  if (!Number.isFinite(bootMs) || bootMs <= 0) {
    bootMs = Date.now();
    sessionStorage.setItem(PLAYER_SESSION_BOOT_MS_KEY, String(bootMs));
  }
  return Math.max(0, (Date.now() - bootMs) / 1000);
}

function buildDeviceHandshake() {
  const info = getDeviceInfo();
  const model = info.model || null;
  const serialNumber = info.serialNumber || getDeviceSerialNumber() || deviceId;
  const bootUptimeSec = getPlayerBootUptimeSec();

  let platformId = "web";
  let defaultName = "TomorrowOS Player (dev)";
  let systemName = "web";
  if (isWindowsRuntime()) {
    platformId = "windows";
    defaultName = "Windows Player";
    systemName = "Windows";
  } else if (isBrightSignRuntime()) {
    platformId = "brightsign";
    defaultName = "BrightSign Player";
    systemName = "BrightSignOS";
  }

  return {
    platform: platformId,
    playerVersion: "1.0.0",
    deviceName: model || defaultName,
    system: systemName,
    serialNumber,
    bootUptimeSec,
    bootedAt: new Date(Date.now() - bootUptimeSec * 1000).toISOString()
  };
}

function clearPairedState() {
  localStorage.removeItem("pairedDeviceId");
  localStorage.removeItem("pairingToken");
  localStorage.removeItem("pairedAt");
}

function returnToPairingScreen(socket) {
  prepareRepairResumeSnapshot();
  clearPairedState();
  resetPlaybackSession({ preserveRepairResume: true });
  showPairingUiWaitingForCode();

  if (socket && socket.readyState === WebSocket.OPEN) {
    socket.send(
      JSON.stringify({
        type: "device.hello",
        deviceId,
        ...buildDeviceHandshake()
      })
    );
  }
}

function connect() {
  if (!cmsEndpoint) {
    setPairingStatusMessage("CMS address not configured.");
    return;
  }

  setPairingStatusMessage("Connecting to CMS...");

  cmsSocketConnected = false;
  refreshIdleScreenConnectionStatus();
  stopCmsPing();
  clearCmsConnectTimeout();

  const staleSocket = cmsWebSocket;
  if (
    staleSocket &&
    staleSocket.readyState !== WebSocket.CLOSED &&
    staleSocket.readyState !== WebSocket.CLOSING
  ) {
    try {
      staleSocket.close();
    } catch (_) {}
  }

  const socket = new WebSocket(cmsEndpoint);
  cmsWebSocket = socket;
  scheduleCmsConnectTimeout(socket);

  socket.onopen = () => {
    clearCmsConnectTimeout();
    const pairedDeviceId = localStorage.getItem("pairedDeviceId");
    const pairingToken = localStorage.getItem("pairingToken");

    if (pairedDeviceId && pairingToken) {
      if (pairingArea) pairingArea.style.display = "none";
      socket.send(JSON.stringify({
        type: "device.resume",
        deviceId: pairedDeviceId,
        pairingToken,
        ...buildDeviceHandshake()
      }));
    } else {
      socket.send(JSON.stringify({
        type: "device.hello",
        deviceId,
        ...buildDeviceHandshake()
      }));

      startPairingCodeRollAnimation();
      setPairingStatusMessage("Connected. Requesting pairing code...");
    }

    startCmsPing(socket);
  };

  socket.onmessage = async (event) => {
    const msg = JSON.parse(event.data);

    if (msg.type === "device.pong") {
      handleCmsPong();
      return;
    }

    if (msg.type === "brand.snapshot" && msg.brand) {
      applyBrandFromCms(msg.brand);
    }

    if (msg.type === "pairing.code" && msg.method === "tomorrowos.pairing.createCode") {
      // Ignore stale codes after pairing.verified (late hello / reconnect race).
      if (!isDevicePaired()) {
        showUnpairedPairingCode(msg.code);
      }
    }

    if (msg.type === "device.resumed" && msg.method === "tomorrowos.pairing.resume") {
      cmsSocketConnected = true;
      ensureOnOffTimerScheduler();
      void applyOnOffTimerNow("device.resumed");
      if (playerState.playlistPlaybackActive) {
        refreshIdleScreenConnectionStatus();
      } else if (peekRepairResumePending()) {
        const ok = await completeRepairRehydrate("device.resumed");
        if (!ok && !playerState.playlistPlaybackActive) {
          scheduleRepairResumeFallbackCheck();
        }
      } else if (!(await hydratePlaybackFromCache("device.resumed"))) {
        showPairedIdleScreen();
      }
    }

    if (msg.type === "device.resume.failed") {
      clearPairedState();
      resetPlaybackSession();
      discardStalePlaybackCache();

      showPairingUiWaitingForCode();

      socket.send(JSON.stringify({
        type: "device.hello",
        deviceId,
        ...buildDeviceHandshake()
      }));
    }

    if (msg.type === "pairing.verified" && msg.method === "tomorrowos.pairing.verify") {
      localStorage.setItem("pairedDeviceId", msg.deviceId);
      localStorage.setItem("pairingToken", msg.pairingToken);
      localStorage.setItem("pairedAt", new Date().toISOString());

      cmsSocketConnected = true;
      ensureOnOffTimerScheduler();
      void applyOnOffTimerNow("pairing.verified");
      if (peekRepairResumePending()) {
        const ok = await completeRepairRehydrate("pairing.verified");
        if (!ok && !playerState.playlistPlaybackActive) {
          scheduleRepairResumeFallbackCheck();
        }
      } else if (!(await hydratePlaybackFromCache("pairing.verified"))) {
        showPairedIdleScreen();
      }
    }

    if (msg.type === "pairing.unpaired" && msg.method === "tomorrowos.pairing.unpair") {
      returnToPairingScreen(socket);
    }


    //Get Device Info

    if (msg.type === "command" && msg.method === "device.info.get") {
      try {
        const info = getDeviceInfo();

        socket.send(JSON.stringify({
          type: "command.result",
          commandId: msg.commandId,
          method: msg.method,
          status: "success",
          data: info
        }));

        } catch (err) {
          socket.send(JSON.stringify({
            type: "command.result",
            commandId: msg.commandId,
            method: msg.method,
            status: "failed",
            error: err.message
          }));
        }
    }

    if (msg.type === "command" && msg.method === "device.info.getCapabilities") {
      try {
        const capabilities = getDeviceCapabilities();
        socket.send(JSON.stringify({
          type: "command.result",
          commandId: msg.commandId,
          method: msg.method,
          status: "success",
          data: capabilities
        }));
      } catch (err) {
        socket.send(JSON.stringify({
          type: "command.result",
          commandId: msg.commandId,
          method: msg.method,
          status: "failed",
          error: err.message
        }));
      }
    }

    if (msg.type === "command" && msg.method === "device.telemetry.captureScreen") {
      try {
        const screenshot = await captureDeviceScreenshot();
        socket.send(JSON.stringify({
          type: "command.result",
          commandId: msg.commandId,
          method: msg.method,
          status: "success",
          data: { screenshot }
        }));
      } catch (err) {
        socket.send(JSON.stringify({
          type: "command.result",
          commandId: msg.commandId,
          method: msg.method,
          status: "failed",
          error: err.message
        }));
      }
    }

    //reboot Device
    if (msg.type === "command" && msg.method === "device.power.reboot") {
      try {
        if (!getPlatform()?.rebootDevice) {
          throw new Error("Device reboot API not available");
        }

        socket.send(JSON.stringify({
          type: "command.result",
          commandId: msg.commandId,
          method: msg.method,
          status: "accepted",
          data: {
            message: "Reboot command accepted"
          }
        }));

        try {
          socket.close();
        } catch (_) {}

        try {
          saveResumeState({ reason: "reboot" });
          if (playerState.policy) persistPolicyCache(playerState.policy);
          markRebootResumePending();
          console.log("[TomorrowOS][resume] saved playback state before reboot");
        } catch (resumeErr) {
          console.error("[TomorrowOS][resume] pre-reboot save failed:", resumeErr);
        }

        setTimeout(() => {
          getPlatform().rebootDevice();
        }, 150);
    

      } catch (err) {
        socket.send(JSON.stringify({
          type: "command.result",
          commandId: msg.commandId,
          method: msg.method,
          status: "failed",
          error: err.message
        }));
      }
    }

    if (msg.type === "command" && msg.method === "device.display.setOnOffTimer") {
      try {
        const params = msg.params && typeof msg.params === "object" ? msg.params : {};
        const timerInput =
          Object.prototype.hasOwnProperty.call(params, "onOffTimer")
            ? params.onOffTimer
            : params;
        const data = await setOnOffTimerFromCms(timerInput);
        socket.send(JSON.stringify({
          type: "command.result",
          commandId: msg.commandId,
          method: msg.method,
          status: "success",
          data
        }));
      } catch (err) {
        socket.send(JSON.stringify({
          type: "command.result",
          commandId: msg.commandId,
          method: msg.method,
          status: "failed",
          error: err.message
        }));
      }
    }



//Handle setPolicy 
  if (msg.type === "command" && msg.method === "device.content.setPolicy") {
    const policy = normalizePolicy(msg.params);

    if (peekRepairResumePending()) {
      primeResumePlaybackHints(policy);
      if (!playerState.playlistPlaybackActive) {
        await completeRepairRehydrate("setPolicy");
      }
    } else if (peekRebootResumePending()) {
      primeResumePlaybackHints(policy);
    }

    try{
      const result = await applyPolicy(policy, { announceOnFallback: !peekRepairResumePending() });
      socket.send(JSON.stringify({
        type: "command.result",
        commandId: msg.commandId,
        method: msg.method,
        status: "success",
        data: result
      }));
    }catch (err) {
      socket.send(JSON.stringify({
        type: "command.result",
        commandId: msg.commandId,
        method: msg.method,
        status: "failed",
        error: err.message,
        stack: err.stack
      }));
    }
  }

  if (msg.type === "command" && msg.method === "device.content.clear") {
    try {
      const result = await clearContentPolicy();
      socket.send(JSON.stringify({
        type: "command.result",
        commandId: msg.commandId,
        method: msg.method,
        status: "success",
        data: result
      }));
    } catch (err) {
      socket.send(JSON.stringify({
        type: "command.result",
        commandId: msg.commandId,
        method: msg.method,
        status: "failed",
        error: err.message
      }));
    }
  }

    
  };

  socket.onclose = () => {
    clearCmsConnectTimeout();
    stopCmsPing();
    const shouldReconnect = cmsWebSocket === socket;
    if (shouldReconnect) cmsWebSocket = null;
    cmsSocketConnected = false;
    refreshIdleScreenConnectionStatus();

    const pairedDeviceId = localStorage.getItem("pairedDeviceId");
    const pairingToken = localStorage.getItem("pairingToken");
    if (!pairedDeviceId || !pairingToken) {
      setPairingStatusMessage("Disconnected. Retrying...");
    }
    if (shouldReconnect) setTimeout(connect, CMS_RECONNECT_DELAY_MS);
  };

  socket.onerror = () => {
    cmsSocketConnected = false;
    refreshIdleScreenConnectionStatus();

    const pairedDeviceId = localStorage.getItem("pairedDeviceId");
    const pairingToken = localStorage.getItem("pairingToken");
    if (!pairedDeviceId || !pairingToken) {
      setPairingStatusMessage("WebSocket error");
    }
  };
}

// connect();



async function loadBrand() {
  if (!httpEndpoint) return;
  try {
    const response = await fetch(`${httpEndpoint}/brand.json`);
    if (!response.ok) {
      console.error("[TomorrowOS] brand.json HTTP", response.status);
      return;
    }
    applyBrandFromCms(await response.json());
  } catch (err) {
    console.error("[TomorrowOS] loadBrand:", err);
  }
}

function applyBrandFromCms(brand) {
  if (!brand || typeof brand !== "object") return;
  currentBrand = brand;
  applyBrand(brand);
  if (getStoredCmsEndpoint()) {
    primeIdleStateFrame().catch((err) => {
      console.error("[TomorrowOS] primeIdleStateFrame after brand:", err);
    });
  }
}

// Set brand

function applyBrand(brand) {
  document.documentElement.style.setProperty("--color-primary", brand.primaryColor);
  document.documentElement.style.setProperty("--color-background", brand.backgroundColor || "#FAFAF9");
  document.documentElement.style.setProperty("--color-text", brand.textColor || "#0A0908");

  const brandNameEl = document.getElementById("brandName");
  const brandTaglineEl = document.getElementById("brandTagline");
  if (brandNameEl) brandNameEl.textContent = brand.name || "TomorrowOS";
  if (brandTaglineEl) brandTaglineEl.textContent = brand.tagline || "";

  if (brand.logoPath && httpEndpoint) {
    const logoEl = document.getElementById("brandLogo");
    if (logoEl) {
      const logoPath = String(brand.logoPath).replace(/^\.\//, "");
      logoEl.src = `${httpEndpoint}/${logoPath}`;
    }
  }

  if (!isShowingPairingCode()) {
    if (activationHeadlineEl) {
      activationHeadlineEl.textContent =
        brand.activationScreen?.headline || "Connect this screen";
    }
    if (activationInstructionsEl) {
      activationInstructionsEl.textContent =
        brand.activationScreen?.instructions ||
        "Enter the code in your dashboard to activate this screen.";
    }
  }

  if (
    idleStateShell?.style.display !== "none" &&
    idleStateLastOptions
  ) {
    bindIdleStateFrame(idleStateLastOptions);
  }
}

function escapeHiddenMenuHtml(text) {
  return String(text ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function getHiddenDeviceMenuRows() {
  const info = getDeviceInfo();
  const pairedDeviceId = localStorage.getItem("pairedDeviceId");
  const pairingToken = localStorage.getItem("pairingToken");
  let uptimeSec = null;
  try {
    uptimeSec = getPlayerBootUptimeSec();
  } catch (err) {
    console.warn("[TomorrowOS] hidden menu uptime:", err);
  }

  let cmsConnection = "Not connected";
  if (cmsWebSocket) {
    if (cmsWebSocket.readyState === WebSocket.OPEN) cmsConnection = "Connected";
    else if (cmsWebSocket.readyState === WebSocket.CONNECTING) cmsConnection = "Connecting";
    else cmsConnection = "Disconnected";
  }

  let playerVersion = "1.0.0";
  try {
    playerVersion = buildDeviceHandshake().playerVersion || playerVersion;
  } catch (_) {}

  return [
    ["DUID", info.deviceId],
    ["Serial number", info.serialNumber || getDeviceSerialNumber()],
    ["Model", info.model],
    ["Firmware", info.firmware],
    ["Platform", isWindowsRuntime() ? "windows" : isBrightSignRuntime() ? "brightsign" : "web"],
    ["Player version", playerVersion],
    ["CMS endpoint", cmsEndpoint || getStoredCmsEndpoint()],
    ["CMS connection", cmsConnection],
    ["Paired", pairedDeviceId && pairingToken ? "Yes" : "No"],
    ["Orientation", getSavedOrientation()],
    ["Network online", info.online ? "Yes" : "No"],
    ["System uptime", formatHiddenMenuUptime(uptimeSec)]
  ];
}

function renderHiddenDeviceMenu() {
  const listEl = document.getElementById("hiddenDeviceMenuList");
  if (!listEl) {
    console.error("[TomorrowOS] hiddenDeviceMenuList element not found");
    return;
  }

  let rows;
  try {
    rows = getHiddenDeviceMenuRows();
  } catch (err) {
    console.error("[TomorrowOS] hidden menu data failed:", err);
    rows = [["Error", err?.message || "Failed to load device info"]];
  }

  listEl.innerHTML = rows
    .map(
      ([label, value]) =>
        `<div class="hidden-device-menu-row"><div class="hidden-device-menu-label">${escapeHiddenMenuHtml(
          label
        )}</div><div class="hidden-device-menu-value">${escapeHiddenMenuHtml(
          formatHiddenMenuValue(value)
        )}</div></div>`
    )
    .join("");
}

function openHiddenDeviceMenu() {
  const menuEl = document.getElementById("hiddenDeviceMenu");
  if (!menuEl) {
    console.error("[TomorrowOS] hiddenDeviceMenu element not found");
    return;
  }

  renderHiddenDeviceMenu();
  menuEl.style.display = "flex";
  menuEl.setAttribute("aria-hidden", "false");
  hiddenDeviceMenuOpen = true;
  console.log("[TomorrowOS] hidden device menu opened");
}

function closeHiddenDeviceMenu() {
  const menuEl = document.getElementById("hiddenDeviceMenu");
  if (!menuEl) return;
  menuEl.style.display = "none";
  menuEl.setAttribute("aria-hidden", "true");
  hiddenDeviceMenuOpen = false;
  resetHiddenMenuZeroSequence();
  console.log("[TomorrowOS] hidden device menu closed");
}

function removeStaticBootShell() {
  const shell = document.getElementById("staticBootShell");
  if (shell) shell.remove();
}

window.onload = function () {
  removeStaticBootShell();
  resetDocumentLayoutViewport();

  const bootResult = applyBootConfig();
  if (bootResult === false) return;
  if (bootResult === "needs-setup") {
    showCmsSetupUI();
    return;
  }

  void ensureCmsReachableAndProceed();
};

function getDeviceInfo() {
  return getPlatform()?.getDeviceInfo() || {
    online: navigator.onLine,
    deviceId: null,
    model: null,
    firmware: null,
    serialNumber: null
  };
}

function getDeviceCapabilities() {
  const isPlayerRuntime = isNativePlayerRuntime();
  const timerSupported =
    isPlayerRuntime && typeof getPlatform()?.canSetDisplayMute === "function"
      ? !!getPlatform().canSetDisplayMute()
      : false;

  if (isPlayerRuntime) {
    return {
      capabilities: {
        "device.info.get": {
          supported: true
        },
        "device.power.reboot": {
          supported: true
        },
        "device.content.setPolicy": {
          supported: true
        },
        "device.content.clear": {
          supported: true
        },
        "device.telemetry.captureScreen": {
          supported: true
        },
        "device.display.setOnOffTimer": {
          supported: timerSupported
        }
      }
    };
  }

  return {
    capabilities: {
      "device.info.get": {
        supported: false
      },
      "device.power.reboot": {
        supported: false
      },
      "device.content.setPolicy": {
        supported: false
      },
      "device.content.clear": {
        supported: false
      },
      "device.telemetry.captureScreen": {
        supported: false
      },
      "device.display.setOnOffTimer": {
        supported: false
      }
    }
  };
}

const SCREENSHOT_MAX_DIMENSION = 1280;

function getScreenshotViewport() {
  const width = Math.max(
    1,
    window.innerWidth || 0,
    document.documentElement?.clientWidth || 0,
    document.body?.clientWidth || 0
  );
  const height = Math.max(
    1,
    window.innerHeight || 0,
    document.documentElement?.clientHeight || 0,
    document.body?.clientHeight || 0
  );
  const scale = Math.min(1, SCREENSHOT_MAX_DIMENSION / Math.max(width, height));
  return {
    width,
    height,
    canvasWidth: Math.max(1, Math.round(width * scale)),
    canvasHeight: Math.max(1, Math.round(height * scale)),
    scale
  };
}

function getActiveScreenshotElement() {
  const active = playerState.activeElement;
  if (active && (active.tagName === "IMG" || active.tagName === "VIDEO")) {
    return active;
  }

  const visibleLayer = document.querySelector(".content-layer--visible");
  if (visibleLayer) {
    const media = visibleLayer.querySelector("img, video");
    if (media) return media;
  }

  return null;
}

function encodeScreenshotCanvas(canvas, source) {
  const mimeType = "image/jpeg";
  const dataUrl = canvas.toDataURL(mimeType, 0.82);
  const comma = dataUrl.indexOf(",");
  if (comma < 0) throw new Error("Screenshot encoding failed");

  return {
    mimeType,
    dataBase64: dataUrl.slice(comma + 1),
    capturedAt: new Date().toISOString(),
    width: canvas.width,
    height: canvas.height,
    source
  };
}

function captureStatusScreenshot(reason) {
  const viewport = getScreenshotViewport();
  const canvas = document.createElement("canvas");
  canvas.width = viewport.canvasWidth;
  canvas.height = viewport.canvasHeight;
  const ctx = canvas.getContext("2d");
  if (!ctx) throw new Error("Canvas 2D context unavailable");

  const gradient = ctx.createLinearGradient(0, 0, canvas.width, canvas.height);
  gradient.addColorStop(0, "#111827");
  gradient.addColorStop(1, "#0a0908");
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, canvas.width, canvas.height);

  const title = currentBrand?.name || "TomorrowOS";
  const deviceId = localStorage.getItem("pairedDeviceId") || getDeviceInfo()?.deviceId || "unpaired";
  const playlistName = playerState.activePlaylist?.name || playerState.activePlaylist?.id || "No playlist playing";
  const lines = [
    title,
    reason,
    `Device: ${deviceId}`,
    `Playlist: ${playlistName}`,
    `Captured: ${new Date().toLocaleString()}`
  ];

  ctx.fillStyle = "#f9fafb";
  ctx.textBaseline = "top";
  ctx.font = `600 ${Math.max(24, Math.round(canvas.width * 0.045))}px sans-serif`;
  ctx.fillText(lines[0], Math.round(canvas.width * 0.07), Math.round(canvas.height * 0.12));
  ctx.font = `400 ${Math.max(16, Math.round(canvas.width * 0.022))}px sans-serif`;
  let y = Math.round(canvas.height * 0.24);
  for (let i = 1; i < lines.length; i += 1) {
    ctx.fillText(lines[i], Math.round(canvas.width * 0.07), y);
    y += Math.round(canvas.height * 0.075);
  }

  return encodeScreenshotCanvas(canvas, "status");
}

async function captureNativeBrightSignScreen() {
  const capture = getPlatform()?.captureDeviceScreenshot;
  if (typeof capture !== "function") {
    throw new Error("BrightSign screenshot API not available");
  }
  return capture();
}

async function captureDeviceScreenshot() {
  try {
    return await captureNativeBrightSignScreen();
  } catch (err) {
    console.warn("[TomorrowOS][screenshot] native capture failed, using fallback:", err);
  }

  const el = getActiveScreenshotElement();
  if (el?.tagName === "IMG") {
    if (!el.complete) {
      await new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error("Image not ready for screenshot")), 3000);
        el.addEventListener("load", () => {
          clearTimeout(timer);
          resolve();
        }, { once: true });
        el.addEventListener("error", () => {
          clearTimeout(timer);
          reject(new Error("Image failed before screenshot"));
        }, { once: true });
      });
    }

    const viewport = getScreenshotViewport();
    const canvas = document.createElement("canvas");
    canvas.width = viewport.canvasWidth;
    canvas.height = viewport.canvasHeight;

    const ctx = canvas.getContext("2d");
    if (!ctx) throw new Error("Canvas 2D context unavailable");
    ctx.fillStyle = "#000";
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    const rect = el.getBoundingClientRect();
    ctx.drawImage(
      el,
      Math.round(rect.left * viewport.scale),
      Math.round(rect.top * viewport.scale),
      Math.round(rect.width * viewport.scale),
      Math.round(rect.height * viewport.scale)
    );

    return encodeScreenshotCanvas(canvas, "media-element");
  }

  return captureStatusScreenshot("Native screenshot unavailable on this device.");
}


function normalizePolicy(params = {}) {
  const explicitPolicy = params.policy && typeof params.policy === "object" ? params.policy : null;
  if (explicitPolicy) {
    return explicitPolicy;
  }

  if (Array.isArray(params.playlist)) {
    return {
      playlists: [{ id: "default", name: "Default", items: params.playlist }]
    };
  }

  return {
    playlists: []
  };
}

function clearPlaybackTimers() {
  if (playerState.contentTimer) {
    clearTimeout(playerState.contentTimer);
    playerState.contentTimer = null;
  }
}

function clearPolicySchedulerTimer() {
  if (playerState.playlistTimer) {
    clearTimeout(playerState.playlistTimer);
    playerState.playlistTimer = null;
  }
}

function stopActiveWidget() {
  if (!playerState.activeWidget) return;

  try {
    if (typeof playerState.activeWidget.destroy === "function") {
      playerState.activeWidget.destroy();
    }
  } catch (err) {
    setDebug(`Widget destroy failed: ${err.message}`);
  }

  playerState.activeWidget = null;
}

function pauseMediaInLayer(layerEl) {
  if (!layerEl) return;
  layerEl.querySelectorAll("video").forEach((video) => {
    try {
      video.pause();
    } catch (_) {}
  });
}

function isBrightSignRoVideoElement(el) {
  return el?.tagName === "ROVIDEOPLAYER";
}

let brightSignRoVideoNextSlot = 0;
let brightSignRoVideoNextZIndex = 2;

function getNextBrightSignRoVideoSlot() {
  const activeSlot = Number(playerState.activeElement?.slot);
  if (isBrightSignRoVideoElement(playerState.activeElement) && (activeSlot === 0 || activeSlot === 1)) {
    return activeSlot === 0 ? 1 : 0;
  }

  const slot = brightSignRoVideoNextSlot === 1 ? 1 : 0;
  brightSignRoVideoNextSlot = slot === 0 ? 1 : 0;
  return slot;
}

function getNextBrightSignRoVideoZIndex() {
  brightSignRoVideoNextZIndex += 1;
  if (brightSignRoVideoNextZIndex > 1000) brightSignRoVideoNextZIndex = 2;
  return brightSignRoVideoNextZIndex;
}

function stopBrightSignRoVideoPlayer(options = {}) {
  if (!isBrightSignRuntime()) return;
  const platform = getPlatform();
  if (typeof platform?.stopRoVideoPlayer === "function") {
    return platform.stopRoVideoPlayer(options);
  }
  return null;
}

function stopAllBrightSignRoVideoPlayersForHtml() {
  return stopBrightSignRoVideoPlayer({ stopAll: true, showHtml: true });
}

async function handoffBrightSignRoVideoToHtml() {
  await waitForNextPaintFrames(2);
  await delayMs(BRIGHTSIGN_HTML_HANDOFF_PAINT_MS);
  await stopAllBrightSignRoVideoPlayersForHtml();
}

/** Fully detach a BrightSign HTML video so HWZ does not keep the last frame. */
function releaseHtmlVideoElement(video) {
  if (!video || video.tagName !== "VIDEO") return;
  try {
    video.style.visibility = "hidden";
    video.style.display = "none";
  } catch (_) {}
  try {
    video.pause();
  } catch (_) {}
  try {
    video.removeAttribute("src");
  } catch (_) {}
  try {
    video.querySelectorAll("source").forEach((source) => source.remove());
  } catch (_) {}
  try {
    video.load();
  } catch (_) {}
  try {
    if (video.parentNode) video.parentNode.removeChild(video);
  } catch (_) {}
}

function releaseVideosInLayer(layerEl, options = {}) {
  if (!layerEl) return;
  const skipSlot = options.skipSlot === 1 ? 1 : options.skipSlot === 0 ? 0 : null;
  layerEl.querySelectorAll("[data-ro-video-player='true']").forEach((marker) => {
    if (playerState.activeElement?.marker === marker) return;

    const markerSlot = Number(marker.getAttribute("data-ro-video-slot"));
    const slot = markerSlot === 1 ? 1 : 0;
    if (slot === skipSlot) return;

    const videoStillActive = isBrightSignRoVideoElement(playerState.activeElement);
    if (videoStillActive) return;

    stopBrightSignRoVideoPlayer({ stopAll: true, showHtml: true });
  });
  layerEl.querySelectorAll("video").forEach((video) => releaseHtmlVideoElement(video));
  layerEl.innerHTML = "";
}

function releaseAllPlaybackVideos() {
  if (playerState.activeElement?.tagName === "VIDEO") {
    releaseHtmlVideoElement(playerState.activeElement);
  }
  if (isBrightSignRoVideoElement(playerState.activeElement)) {
    stopBrightSignRoVideoPlayer({ stopAll: true, showHtml: true });
  }
  releaseVideosInLayer(document.getElementById("contentLayer0"));
  releaseVideosInLayer(document.getElementById("contentLayer1"));
  playerState.activeElement = null;
  playerState.activeVideoLocalPath = null;
}

/** Hide and clear both content layers so stale frames are not visible during policy switches. */
function clearVisibleContentLayers() {
  const layer0 = document.getElementById("contentLayer0");
  const layer1 = document.getElementById("contentLayer1");
  releaseAllPlaybackVideos();
  if (layer0) layer0.classList.remove("content-layer--visible");
  if (layer1) layer1.classList.remove("content-layer--visible");
  playerState.prefetchedImageItem = null;
  playerState.prefetchedImageTask = null;
}

function clearPrefetchedImage() {
  playerState.prefetchedImageItem = null;
  playerState.prefetchedImageTask = null;
}

/** Decoded off-DOM images keyed by local cache path ? warmed at playlist start. */
const imageDecodeWarmByPath = new Map();

function clearImageDecodeWarmCache() {
  imageDecodeWarmByPath.clear();
  const pool = document.getElementById("imageDecodePool");
  if (pool) pool.innerHTML = "";
}

function getImageDecodePool() {
  let pool = document.getElementById("imageDecodePool");
  if (!pool) {
    pool = document.createElement("div");
    pool.id = "imageDecodePool";
    pool.setAttribute("aria-hidden", "true");
    pool.style.cssText =
      "position:fixed;left:-9999px;top:0;width:1px;height:1px;opacity:0;pointer-events:none;overflow:hidden";
    document.body.appendChild(pool);
  }
  return pool;
}

function decodeImageOffDom(localPath) {
  return new Promise((resolve, reject) => {
    const img = document.createElement("img");
    img.style.cssText =
      "display:block;width:1px;height:1px;max-width:1px;max-height:1px;object-fit:contain";
    const done = () => {
      try {
        getImageDecodePool().appendChild(img);
      } catch (_) {}
      resolve(img);
    };
    img.onload = () => {
      if (typeof img.decode === "function") {
        img.decode().then(done).catch(done);
        return;
      }
      done();
    };
    img.onerror = () => reject(new Error("Image decode warm failed"));
    img.src = toBrightSignMediaUrl(localPath);
  });
}

function ensureImageDecoded(localPath) {
  if (!localPath) return Promise.resolve(null);
  let entry = imageDecodeWarmByPath.get(localPath);
  if (!entry) {
    entry = { promise: null, img: null };
    entry.promise = decodeImageOffDom(localPath).then((img) => {
      entry.img = img;
      return img;
    });
    imageDecodeWarmByPath.set(localPath, entry);
  }
  return entry.promise;
}

function startPlaylistImageDecodeWarmup(playlist) {
  const sessionId = playerState.backgroundCacheSession;
  warmPlaylistImagesDecode(playlist, sessionId).catch(() => {});
}

async function warmPlaylistImagesDecode(playlist, sessionId) {
  const items = getPlayableItems(playlist);
  for (let i = 0; i < items.length; i += 1) {
    if (sessionId !== playerState.backgroundCacheSession) return;
    if (!playerState.playlistPlaybackActive) return;

    const item = items[i];
    const rawUrl = typeof item === "string" ? item : item?.url;
    if (typeof rawUrl !== "string" || !rawUrl.trim()) continue;

    const url = rawUrl.trim();
    if (getContentType(url, item?.type) !== "image") continue;

    try {
      const cache = await ensureContentCached(item, { silent: true });
      if (sessionId !== playerState.backgroundCacheSession) return;
      if (cache.localPath) await ensureImageDecoded(cache.localPath);
    } catch (_) {}
  }
}

function tryConsumePrefetchedImage(url, index) {
  const pref = playerState.prefetchedImageItem;
  if (!pref || pref.url !== url || pref.index !== index) return null;

  const backLayer = getBackContentLayer();
  const img = backLayer?.querySelector("img");
  if (!img) {
    clearPrefetchedImage();
    return null;
  }

  clearPrefetchedImage();
  return {
    element: img,
    cacheHit: pref.cacheHit,
    localPath: pref.localPath
  };
}

async function prefetchNextImageOnBackLayer(playlist, nextIndex, options = {}) {
  const items = options.loopItems || getPlayableItems(playlist);
  if (items.length <= 1) return;

  const safeIndex = ((nextIndex % items.length) + items.length) % items.length;
  const item = items[safeIndex];
  const rawUrl = typeof item === "string" ? item : item?.url;
  if (typeof rawUrl !== "string" || !rawUrl.trim()) return;

  const url = rawUrl.trim();
  if (getContentType(url, item?.type) !== "image") return;

  const taskKey = `${safeIndex}:${url}`;
  const task = (async () => {
    const cache = await ensureContentCached(item, { silent: true });
    if (!playerState.playlistPlaybackActive || !cache.localPath) return;

    const backLayer = getBackContentLayer();
    if (!backLayer) return;

    clearContentLayer(backLayer);
    await mountImageInLayer(backLayer, cache.localPath);
    playerState.prefetchedImageItem = {
      index: safeIndex,
      url,
      localPath: cache.localPath,
      cacheHit: cache.hit
    };
    console.log(`[TomorrowOS][v2i] prefetched on back layer ${url.slice(-32)}`);
  })()
    .catch(() => {
      clearPrefetchedImage();
    })
    .finally(() => {
      if (playerState.prefetchedImageTask && playerState.prefetchedImageTask.key === taskKey) {
        playerState.prefetchedImageTask = null;
      }
    });

  playerState.prefetchedImageTask = { key: taskKey, promise: task };
  await task;
}

async function waitForPrefetchedImageForCurrentItem(url, index, options = {}) {
  const key = `${index}:${url}`;
  const task = playerState.prefetchedImageTask;
  if (!task || task.key !== key || !task.promise) return;
  if (!options.blockUntilReady) return;
  try {
    await task.promise;
  } catch (_) {}
}

function shouldClearContentForPolicyChange(policy) {
  if (!playerState.playlistPlaybackActive && playerState.activePlaylistKey == null) {
    return false;
  }

  let active = pickActivePlaylist(policy);
  if (!active && isResumePlaybackPending()) {
    active = pickStoredResumePlaylist(policy);
  }
  if (!active) return true;

  const isSamePlaylist =
    playerState.activePlaylistKey === active.key &&
    playerState.activePlaylist !== null &&
    playerState.playlistPlaybackActive;
  return !isSamePlaylist;
}

/** Playlist A → playlist B (takeover / schedule handoff): keep current frame until the next item is ready. */
function isSeamlessPlaylistSwitch(policy) {
  if (!playerState.playlistPlaybackActive && playerState.activePlaylistKey == null) {
    return false;
  }

  let incoming = pickActivePlaylist(policy);
  if (!incoming && isResumePlaybackPending()) {
    incoming = pickStoredResumePlaylist(policy);
  }
  if (!incoming) return false;
  if (getPlayableItems(incoming.playlist).length === 0) return false;

  const isSamePlaylist =
    playerState.activePlaylistKey === incoming.key &&
    playerState.activePlaylist !== null &&
    playerState.playlistPlaybackActive;
  return !isSamePlaylist;
}

function resolveIncomingActivePlaylist(policy) {
  let incoming = pickActivePlaylist(policy);
  if (!incoming && isResumePlaybackPending()) {
    incoming = pickStoredResumePlaylist(policy);
  }
  return incoming;
}

async function preCacheIncomingPlaylistFirstItem(policy, startIndex = 0) {
  const incoming = resolveIncomingActivePlaylist(policy);
  if (!incoming) return;
  const items = getPlayableItems(incoming.playlist);
  if (!items.length) return;
  const safeIndex = ((startIndex % items.length) + items.length) % items.length;
  await ensureContentCached(items[safeIndex], { silent: true });
}

/** Stop timers/state for a playlist change but leave the visible layer showing until the next swap. */
function beginPlaylistSwitch() {
  bumpPlaybackGeneration();
  clearPlaybackTimers();
  stopActiveWidget();
  clearPrefetchedImage();
  playerState.playlistLoopItems = null;
  playerState.playlistPlaybackActive = false;
}

function stopPlayback() {
  bumpPlaybackGeneration();
  clearPlaybackTimers();
  stopActiveWidget();
  clearVisibleContentLayers();
  resetDocumentLayoutViewport();
  playerState.playlistPlaybackActive = false;
  playerState.playlistLoopItems = null;
}

function toMinutes(hhmm) {
  if (typeof hhmm !== "string" || !hhmm.includes(":")) return null;
  const parts = hhmm.trim().split(":");
  const hh = Number(parts[0]);
  const mm = Number(parts[1]);
  if (Number.isNaN(hh) || Number.isNaN(mm)) return null;
  return (hh * 60) + mm;
}

function formatLocalDateTime(date) {
  const pad = (n) => String(n).padStart(2, "0");
  return (
    `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}`
  );
}

/** @param {string} ymd `YYYY-MM-DD` */
function parseDateOnly(ymd) {
  if (typeof ymd !== "string") return null;
  const match = ymd.trim().match(/^(\d{4})-(\d{2})-(\d{2})$/);
  if (!match) return null;
  const year = Number(match[1]);
  const month = Number(match[2]) - 1;
  const day = Number(match[3]);
  const date = new Date(year, month, day);
  if (
    date.getFullYear() !== year ||
    date.getMonth() !== month ||
    date.getDate() !== day
  ) {
    return null;
  }
  return date;
}

function startOfLocalDay(date) {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate());
}

function scheduleHasDateTimeBounds(schedule) {
  if (!schedule) return false;
  return !!(
    schedule.startDate ||
    schedule.endDate ||
    schedule.start ||
    schedule.end
  );
}

/** Combine YYYY-MM-DD + HH:mm into one local Date (device clock). */
function parseScheduleDateTime(dateYmd, hhmm) {
  const hasDate = typeof dateYmd === "string" && !!dateYmd.trim();
  const hasTime = typeof hhmm === "string" && !!hhmm.trim();
  if (!hasDate && !hasTime) return null;

  const base = hasDate ? parseDateOnly(dateYmd) : startOfLocalDay(new Date());
  if (!base) return null;

  const mins = hasTime ? toMinutes(hhmm) : 0;
  if (hasTime && mins === null) return null;

  return new Date(
    base.getFullYear(),
    base.getMonth(),
    base.getDate(),
    Math.floor(mins / 60),
    mins % 60,
    0,
    0
  );
}

/** Inclusive start of the run window (local device time). */
function getScheduleStartMs(schedule) {
  if (!scheduleHasDateTimeBounds(schedule)) return null;
  const dt = parseScheduleDateTime(schedule.startDate, schedule.start);
  return dt ? dt.getTime() : null;
}

/**
 * Exclusive end of the run window (local device time).
 * e.g. end May 8 14:00 ??active until 14:00:00, not after.
 */
function getScheduleEndMs(schedule) {
  if (!scheduleHasDateTimeBounds(schedule)) return null;

  const hasEndDate = typeof schedule.endDate === "string" && !!schedule.endDate.trim();
  const hasEndTime = typeof schedule.end === "string" && !!schedule.end.trim();

  if (!hasEndDate && !hasEndTime) return null;

  if (hasEndDate && !hasEndTime) {
    const day = parseDateOnly(schedule.endDate);
    if (!day) return null;
    return new Date(day.getFullYear(), day.getMonth(), day.getDate() + 1, 0, 0, 0, 0).getTime();
  }

  const dt = parseScheduleDateTime(schedule.endDate, schedule.end);
  return dt ? dt.getTime() : null;
}

function isPlaylistInScheduleWindow(schedule, now = new Date()) {
  if (!scheduleHasDateTimeBounds(schedule)) return true;

  const nowMs = now.getTime();
  const startMs = getScheduleStartMs(schedule);
  const endMs = getScheduleEndMs(schedule);

  if (startMs !== null && nowMs < startMs) return false;
  if (endMs !== null && nowMs >= endMs) return false;
  return true;
}

function describePlaylistScheduleStatus(playlist, now = new Date()) {
  const schedule = playlist?.schedule;
  const deviceLocalTime = formatLocalDateTime(now);
  const playableCount = getPlayableItems(playlist).length;

  if (!schedule) {
    return {
      playlistId: playlist?.id,
      name: playlist?.name,
      active: playableCount > 0,
      deviceLocalTime,
      playableCount,
      schedule: null,
      reasons: playableCount > 0 ? [] : ["no_playable_items"]
    };
  }

  const inWindow = isPlaylistInScheduleWindow(schedule, now);
  const startMs = getScheduleStartMs(schedule);
  const endMs = getScheduleEndMs(schedule);
  const reasons = [];

  if (playableCount === 0) reasons.push("no_playable_items");
  if (!inWindow) {
    const fromLabel = `${schedule.startDate || "any"} ${schedule.start || "00:00"}`.trim();
    const untilLabel = `${schedule.endDate || "any"} ${schedule.end || "23:59"}`.trim();
    reasons.push(`outside_run_${fromLabel}_to_${untilLabel}`);
  }

  return {
    playlistId: playlist?.id,
    name: playlist?.name,
    active: playableCount > 0 && inWindow,
    deviceLocalTime,
    playableCount,
    schedule,
    checks: { inWindow, startMs, endMs },
    reasons
  };
}

function buildPolicyScheduleChecks(policy, now = new Date()) {
  const playlists = Array.isArray(policy?.playlists) ? policy.playlists : [];
  return playlists.map((pl) => describePlaylistScheduleStatus(pl, now));
}

function isPlaylistActive(playlist, now = new Date()) {
  const schedule = playlist?.schedule;
  if (!schedule) return true;
  return isPlaylistInScheduleWindow(schedule, now);
}

function getPlayableItems(playlist) {
  return Array.isArray(playlist?.items)
    ? playlist.items.filter((item) => typeof item?.url === "string" && item.url.trim())
    : [];
}

function resolveLatestActivePlaylist(fallbackPlaylist) {
  if (playerState.activePlaylistKey && playerState.policy) {
    return (
      findPlaylistInPolicyByKey(playerState.policy, playerState.activePlaylistKey) ||
      fallbackPlaylist
    );
  }
  return fallbackPlaylist;
}

function clearPlaylistLoopSnapshot() {
  playerState.playlistLoopItems = null;
}

/** Start a new loop at index 0; mid-loop advances keep the frozen snapshot. */
function ensurePlaylistLoopSnapshot(latestPlaylist, startIndex) {
  if (startIndex === 0) {
    playerState.playlistLoopItems = getPlayableItems(latestPlaylist).slice();
    return playerState.playlistLoopItems;
  }
  if (Array.isArray(playerState.playlistLoopItems) && playerState.playlistLoopItems.length) {
    return playerState.playlistLoopItems;
  }
  playerState.playlistLoopItems = getPlayableItems(latestPlaylist).slice();
  return playerState.playlistLoopItems;
}

function getCurrentLoopItems(latestPlaylist) {
  if (Array.isArray(playerState.playlistLoopItems) && playerState.playlistLoopItems.length) {
    return playerState.playlistLoopItems;
  }
  return getPlayableItems(latestPlaylist);
}

function getPlaylistItemUrl(item) {
  if (typeof item === "string") return item.trim();
  return typeof item?.url === "string" ? item.url.trim() : "";
}

function playlistItemsSignature(items) {
  if (!Array.isArray(items)) return "";
  return items
    .map((item) => {
      const url = getPlaylistItemUrl(item);
      const type = item?.type || getContentType(url);
      const dur = Number.isFinite(item?.durationMs) ? item.durationMs : "";
      return `${type}\0${url}\0${dur}`;
    })
    .join("\n");
}

function hasPlaylistCompositionChanged(frozenItems, latestPlaylist) {
  return playlistItemsSignature(frozenItems) !== playlistItemsSignature(
    getPlayableItems(latestPlaylist)
  );
}

function isActiveVideoPlayback() {
  return (
    playerState.activeElement?.tagName === "VIDEO" ||
    isBrightSignRoVideoElement(playerState.activeElement)
  );
}

/** In-place single-video loop only when URL unchanged and video plane is still active. */
function canUseSingleVideoInPlaceLoop(frozenItems, latestPlaylist) {
  if (!isSingleVideoPlaylist(latestPlaylist)) return false;
  if (!isActiveVideoPlayback()) return false;
  const latest = getPlayableItems(latestPlaylist);
  if (latest.length !== 1 || frozenItems.length !== 1) return false;
  return getPlaylistItemUrl(frozenItems[0]) === getPlaylistItemUrl(latest[0]);
}

function isSameSingleImageLoop(frozenItems, latestItems) {
  if (frozenItems.length !== 1 || latestItems.length !== 1) return false;
  if (getContentType(getPlaylistItemUrl(frozenItems[0]), frozenItems[0]?.type) !== "image") {
    return false;
  }
  return getPlaylistItemUrl(frozenItems[0]) === getPlaylistItemUrl(latestItems[0]);
}

async function advancePlaylistAfterItem(playlist, fromIndex) {
  const playbackGen = playbackGeneration;
  if (!playerState.playlistPlaybackActive) return;

  const latestPlaylist = resolveLatestActivePlaylist(playlist);
  const loopItems = getCurrentLoopItems(latestPlaylist);
  if (!loopItems.length) {
    await showBrandFallback();
    return;
  }

  const atLastInLoop = fromIndex >= loopItems.length - 1;
  const frozenBeforeRefresh = loopItems;
  let nextIndex;

  if (atLastInLoop) {
    nextIndex = 0;
    clearPrefetchedImage();
    playerState.playlistLoopItems = getPlayableItems(latestPlaylist).slice();
    console.log("[TomorrowOS] playlist loop complete — next loop uses latest items", {
      itemCount: playerState.playlistLoopItems.length
    });
  } else {
    nextIndex = fromIndex + 1;
  }

  if (!atLastInLoop) {
    try {
      if (!isPlaybackGenerationCurrent(playbackGen)) return;
      await playPlaylistSequence(latestPlaylist, nextIndex, 0, playbackGen);
    } catch (err) {
      if (!isPlaybackGenerationCurrent(playbackGen)) return;
      console.error("[TomorrowOS][video] playlist advance failed", {
        playlistId: latestPlaylist?.id || "unnamed",
        nextIndex,
        err: err?.message || String(err)
      });
      reportDeviceLog("error", "playlist advance failed", {
        playlistId: latestPlaylist?.id || "unnamed",
        nextIndex,
        err: err?.message || String(err)
      }, "playback");
      setDebug(`Playlist item failed: ${err.message}`);
      await showBrandFallback();
    }
    return;
  }

  if (!isPlaybackGenerationCurrent(playbackGen)) return;

  const latestLoopItems = playerState.playlistLoopItems || [];
  if (!latestLoopItems.length) {
    await showBrandFallback();
    return;
  }

  const compositionChanged = hasPlaylistCompositionChanged(
    frozenBeforeRefresh,
    latestPlaylist
  );

  if (
    !compositionChanged &&
    canUseSingleVideoInPlaceLoop(frozenBeforeRefresh, latestPlaylist)
  ) {
    try {
      const cache = await ensureContentCached(latestLoopItems[0], { silent: true });
      if (!isPlaybackGenerationCurrent(playbackGen)) return;
      const playPath = cache.localPath || playerState.activeVideoLocalPath;
      if (playPath && (await replayVideoInPlace(playPath, 0))) {
        playerState.activeVideoLocalPath = playPath;
        const loopDurationMs =
          playerState.currentItemDurationMs ||
          getContentDurationMs(latestLoopItems[0], { type: "video" });
        playerState.currentItemIndex = 0;
        playerState.currentItemStartedAt = Date.now();
        schedulePlaylistAdvance(latestPlaylist, 0, loopDurationMs, 0);
        return;
      }
    } catch (err) {
      console.warn("[TomorrowOS][video] in-place loop replay failed, remounting:", err);
    }
  }

  if (!compositionChanged && isSameSingleImageLoop(frozenBeforeRefresh, latestLoopItems)) {
    const durationMs = getContentDurationMs(latestLoopItems[0], { type: "image" });
    playerState.currentItemIndex = 0;
    playerState.currentItemStartedAt = Date.now();
    playerState.currentItemDurationMs = durationMs;
    schedulePlaylistAdvance(latestPlaylist, 0, durationMs, 0);
    return;
  }

  try {
    if (!isPlaybackGenerationCurrent(playbackGen)) return;
    await playPlaylistSequence(latestPlaylist, 0, 0, playbackGen);
  } catch (err) {
    if (!isPlaybackGenerationCurrent(playbackGen)) return;
    console.error("[TomorrowOS][video] playlist loop restart failed", {
      playlistId: latestPlaylist?.id || "unnamed",
      compositionChanged,
      err: err?.message || String(err)
    });
    reportDeviceLog("error", "playlist loop restart failed", {
      playlistId: latestPlaylist?.id || "unnamed",
      compositionChanged,
      err: err?.message || String(err)
    }, "playback");
    setDebug(`Playlist item failed: ${err.message}`);
    await showBrandFallback();
  }
}

function isHotUpdateForActivePlaylist(policy) {
  if (!playerState.playlistPlaybackActive || !playerState.activePlaylistKey) {
    return false;
  }
  const active = pickActiveScheduledPlaylist(policy);
  return !!(active && active.key === playerState.activePlaylistKey);
}

function scheduleHotPlaylistItemPrefetch(playlist) {
  if (!playlist) return;
  const sessionId = playerState.backgroundCacheSession;
  const items = getPlayableItems(playlist);
  (async () => {
    for (let i = 0; i < items.length; i += 1) {
      if (sessionId !== playerState.backgroundCacheSession) return;
      if (!playerState.playlistPlaybackActive) return;
      try {
        await ensureContentCached(items[i], { silent: true });
      } catch (_) {}
    }
    if (sessionId !== playerState.backgroundCacheSession) return;
    warmPlaylistImagesDecode(playlist, sessionId).catch(() => {});
  })();
}

function getPlaylistKey(playlist, index) {
  if (playlist?.id) return `id:${playlist.id}`;
  if (playlist?.name) return `name:${playlist.name}`;
  return `idx:${index}`;
}

function findPlaylistInPolicyByKey(policy, key) {
  const playlists = Array.isArray(policy?.playlists) ? policy.playlists : [];
  for (let i = 0; i < playlists.length; i += 1) {
    const playlist = playlists[i];
    if (getPlaylistKey(playlist, i) === key) return playlist;
  }
  return null;
}

/** When several playlists are active, prefer the run window that started most recently.
 *  Always-on (no schedule) playlists score 0; on ties, prefer the later entry in the
 *  policy array so a later-published unscheduled playlist overrides earlier ones.
 */
function scheduleActivePriorityScore(schedule) {
  return getScheduleStartMs(schedule) ?? 0;
}

function pickActiveScheduledPlaylist(policy, now = new Date()) {
  const playlists = Array.isArray(policy?.playlists) ? policy.playlists : [];
  let best = null;
  let bestScore = -Infinity;

  for (let i = 0; i < playlists.length; i += 1) {
    const playlist = playlists[i];
    if (getPlayableItems(playlist).length === 0) continue;
    if (!isPlaylistActive(playlist, now)) continue;

    const score = scheduleActivePriorityScore(playlist.schedule);
    // Use >= so later equal-score playlists (typically later-published always-on) win.
    if (score >= bestScore) {
      bestScore = score;
      best = {
        playlist,
        key: getPlaylistKey(playlist, i)
      };
    }
  }

  return best;
}

function computeNextPolicySchedulerDelay(policy, now = new Date()) {
  const playlists = Array.isArray(policy?.playlists) ? policy.playlists : [];
  let nextAt = now.getTime() + MAX_POLICY_SCHEDULER_MS;
  const nowMs = now.getTime();

  for (let p = 0; p < playlists.length; p += 1) {
    const schedule = playlists[p]?.schedule;
    if (!schedule || !scheduleHasDateTimeBounds(schedule)) continue;

    const startMs = getScheduleStartMs(schedule);
    const endMs = getScheduleEndMs(schedule);
    if (startMs !== null && startMs > nowMs && startMs < nextAt) nextAt = startMs;
    if (endMs !== null && endMs > nowMs && endMs < nextAt) nextAt = endMs;
  }

  const delay = nextAt - nowMs;
  if (!Number.isFinite(delay) || delay <= 0) return MIN_POLICY_SCHEDULER_MS;
  return Math.max(MIN_POLICY_SCHEDULER_MS, Math.min(MAX_POLICY_SCHEDULER_MS, delay));
}

function pickFirstPlayablePlaylist(policy) {
  const playlists = Array.isArray(policy?.playlists) ? policy.playlists : [];
  for (let i = 0; i < playlists.length; i += 1) {
    const playlist = playlists[i];
    if (getPlayableItems(playlist).length === 0) continue;
    return {
      playlist,
      key: getPlaylistKey(playlist, i)
    };
  }
  return null;
}

function captureOrphanedPlaybackBeforePolicyUpdate(nextPolicy) {
  if (
    !playerState.playlistPlaybackActive ||
    !playerState.activePlaylistKey ||
    !playerState.activePlaylist
  ) {
    return;
  }
  const key = playerState.activePlaylistKey;
  if (findPlaylistInPolicyByKey(nextPolicy, key)) {
    return;
  }

  // Current playlist was unassigned — stop it and hand off to the next scheduled
  // playlist in the updated policy (e.g. nested inner A removed → outer B plays).
  playerState.orphanedPlayback = null;
}

function pickActivePlaylist(policy) {
  const resumePlayback = isResumePlaybackPending();

  const fromPolicy = pickActiveScheduledPlaylist(policy);
  if (fromPolicy) {
    playerState.orphanedPlayback = null;
    return fromPolicy;
  }

  if (playerState.orphanedPlayback) {
    const orphan = playerState.orphanedPlayback;
    const stillInPolicy = findPlaylistInPolicyByKey(policy, orphan.key);
    if (
      stillInPolicy &&
      (resumePlayback || isPlaylistActive(orphan.playlist, new Date()))
    ) {
      return { playlist: orphan.playlist, key: orphan.key };
    }
    playerState.orphanedPlayback = null;
  }

  if (resumePlayback) {
    return pickStoredResumePlaylist(policy);
  }

  return null;
}

function scheduleNextPolicyTick(applyGen = policyApplyGeneration) {
  clearPolicySchedulerTimer();

  const delayMs = playerState.policy
    ? computeNextPolicySchedulerDelay(playerState.policy)
    : MAX_POLICY_SCHEDULER_MS;

  playerState.playlistTimer = setTimeout(async () => {
    if (applyGen !== policyApplyGeneration) return;
    if (!playerState.policy) return;

    try {
      await syncPolicyPlayback({ applyGen });
    } catch (err) {
      setDebug(`Playlist refresh failed: ${err.message}`);
      await showBrandFallback();
    } finally {
      if (playerState.policy && applyGen === policyApplyGeneration) {
        scheduleNextPolicyTick(applyGen);
      }
    }
  }, delayMs);
}

async function applyPolicy(policy, options = {}) {
  if (!isDevicePaired()) {
    console.warn("[TomorrowOS] applyPolicy ignored ??device is not paired");
    return { status: "ignored", reason: "not_paired" };
  }

  clearPolicySchedulerTimer();
  const applyGen = ++policyApplyGeneration;

  const policyForState = policy && typeof policy === "object" ? { ...policy } : policy;

  console.log(
    "[TomorrowOS][resume] applyPolicy called",
    "playlists:", Array.isArray(policy?.playlists) ? policy.playlists.length : 0,
    "options:", options,
    "repair:", peekRepairResumePending()
  );
  captureOrphanedPlaybackBeforePolicyUpdate(policy);
  if (policy?.syncMode === "latest" && !isResumePlaybackPending()) {
    playerState.orphanedPlayback = null;
  }
  if (isResumePlaybackPending()) {
    primeResumePlaybackHints(policyForState);
  }
  const hotUpdate = isHotUpdateForActivePlaylist(policy);
  playerState.policy = policyForState;
  if (!hotUpdate) {
    playerState.backgroundCacheSession += 1;
    clearImageDecodeWarmCache();
  }
  if (shouldClearContentForPolicyChange(policyForState)) {
    clearPlaybackTimers();
    if (isSeamlessPlaylistSwitch(policyForState)) {
      clearPrefetchedImage();
      stopActiveWidget();
    } else {
      bumpPlaybackGeneration();
      clearVisibleContentLayers();
    }
  }
  // Persist a snapshot so we can resume after an orientation-change reload
  // even if the CMS doesn't re-push the policy right away.
  persistPolicyCache(policyForState);
  const seamlessSwitch = isSeamlessPlaylistSwitch(policyForState);
  const switchPrefetchPromise = seamlessSwitch
    ? preCacheIncomingPlaylistFirstItem(policyForState).catch(() => {})
    : null;
  preCacheFirstPolicyContent(policyForState).catch((err) => {
    setDebug(`Initial pre-cache failed: ${err.message}`);
  });
  if (switchPrefetchPromise) {
    await switchPrefetchPromise;
  }
  const result = await syncPolicyPlayback({ ...options, applyGen, hotUpdate });
  if (applyGen !== policyApplyGeneration) {
    return { status: "ignored", reason: "superseded", prior: result };
  }
  scheduleNextPolicyTick(applyGen);
  return result;
}

async function clearContentPolicy() {
  playerState.backgroundCacheSession += 1;
  clearImageDecodeWarmCache();
  playerState.policy = null;
  playerState.activePlaylist = null;
  playerState.activePlaylistKey = null;
  playerState.playlistPlaybackActive = false;
  playerState.orphanedPlayback = null;
  playerState.playlistHandoffResume = {};

  clearPolicySchedulerTimer();
  stopPlayback();
  cancelActiveDownloads();
  clearInFlightCacheMarkers();
  clearInFlightWidgetMarkers();
  clearPolicyCache();

  await showBrandFallback("Content cleared. Waiting for next policy.", {
    forceIdleReload: true
  });

  return {
    status: "cleared",
    fallback: "brand"
  };
}

function clearInFlightCacheMarkers() {
  const keys = Object.keys(inFlightCacheByKey);
  for (let i = 0; i < keys.length; i += 1) {
    delete inFlightCacheByKey[keys[i]];
  }
}

function clearInFlightWidgetMarkers() {
  const keys = Object.keys(inFlightWidgetByKey);
  for (let i = 0; i < keys.length; i += 1) {
    delete inFlightWidgetByKey[keys[i]];
  }
}

async function syncPolicyPlayback(options = {}) {
  const applyGen = options.applyGen;
  if (applyGen != null && applyGen !== policyApplyGeneration) {
    return { status: "ignored", reason: "stale_apply" };
  }

  let active = pickActivePlaylist(playerState.policy);
  if (!active && isResumePlaybackPending()) {
    active = pickStoredResumePlaylist(playerState.policy);
  }
  if (!active) {
    const hasPlayable = !!pickFirstPlayablePlaylist(playerState.policy);
    const fallbackMessage =
      options.announceOnFallback && !hasPlayable
        ? "Set policy successfully. Waiting for scheduled playlist."
        : "";
    const resumePending = isResumePlaybackPending();
    const shouldRenderFallback =
      !resumePending &&
      (playerState.activePlaylistKey !== null ||
        !!fallbackMessage ||
        !!playerState.orphanedPlayback);
    if (shouldRenderFallback) {
      stopPlayback();
      await showBrandFallback(fallbackMessage);
    }

    playerState.activePlaylist = null;
    playerState.activePlaylistKey = null;
    return {
      status: "fallback",
      reason: "no_active_playlist",
      fallback: "brand",
      scheduleChecks: buildPolicyScheduleChecks(playerState.policy)
    };
  }

  const isSamePlaylistRunning =
    playerState.activePlaylistKey === active.key &&
    playerState.activePlaylist !== null &&
    playerState.playlistPlaybackActive;

  if (isSamePlaylistRunning) {
    // Hot-update: refresh policy data but keep the current loop snapshot so new
    // assets join at the end on the next wrap — no mid-loop interrupt or black frame.
    playerState.activePlaylist = active.playlist;
    if (options.hotUpdate) {
      scheduleHotPlaylistItemPrefetch(active.playlist);
      console.log("[TomorrowOS] hot-update deferred until next loop", {
        currentLoopItems: playerState.playlistLoopItems?.length ?? 0,
        latestItems: getPlayableItems(active.playlist).length
      });
    }
    return {
      status: "playing",
      playlistId: active.playlist.id || "unnamed",
      itemCount: getPlayableItems(active.playlist).length,
      updated: true,
      deferredUntilLoop: options.hotUpdate === true
    };
  }

  if (!isSamePlaylistRunning) {
    if (
      playerState.playlistPlaybackActive &&
      playerState.activePlaylistKey &&
      playerState.activePlaylist
    ) {
      savePlaylistHandoffResume();
    }

    const resume =
      consumeResumeState(active.key) ||
      consumePlaylistHandoffResume(active.key, active.playlist);
    const startIndex = resume ? resume.itemIndex : 0;
    let initialSeekMs = resume ? resume.elapsedMs : 0;
    // After orientation reload, resume the same item from the start.
    if (resume && shouldSkipVideoSeekOnResume()) {
      initialSeekMs = 0;
      console.log(
        "[TomorrowOS][resume] post-reload resume — skipping video seek, same item index",
        startIndex,
        peekRebootResumePending()
          ? "(reboot)"
          : peekRepairResumePending()
            ? "(re-pair)"
            : "(orient)"
      );
    }

    const hadVisiblePlayback =
      playerState.playlistPlaybackActive ||
      playerState.activePlaylistKey != null ||
      Boolean(getFrontContentLayer()?.classList.contains("content-layer--visible"));

    if (hadVisiblePlayback) {
      beginPlaylistSwitch();
      playerState.activePlaylist = active.playlist;
      playerState.activePlaylistKey = active.key;
      await preCacheIncomingPlaylistFirstItem(playerState.policy, startIndex).catch(() => {});
    } else {
      stopPlayback();
      playerState.activePlaylist = active.playlist;
      playerState.activePlaylistKey = active.key;
    }

    await playPlaylistSequence(active.playlist, startIndex, initialSeekMs);
  }

  return {
    status: "playing",
    playlistId: active.playlist.id || "unnamed",
    itemCount: getPlayableItems(active.playlist).length
  };
}

function isSingleVideoPlaylist(playlist) {
  const items = getPlayableItems(playlist);
  if (items.length !== 1) return false;
  const item = items[0];
  return getContentType(item?.url, item?.type) === "video";
}

async function teardownVideoPlaybackForRemount() {
  const front = getFrontContentLayer();
  const frontHasVideo =
    playerState.activeElement?.tagName === "VIDEO" ||
    isBrightSignRoVideoElement(playerState.activeElement) ||
    Boolean(front?.querySelector("video")) ||
    Boolean(front?.querySelector("[data-ro-video-player='true']"));

  if (!frontHasVideo) return;

  pauseMediaInLayer(front);
  pauseMediaInLayer(getBackContentLayer());
}

async function replayVideoInPlace(localPath, seekMs = 0) {
  if (!localPath || !playerState.playlistPlaybackActive) return false;

  const el = playerState.activeElement;
  if (isBrightSignRoVideoElement(el)) {
    try {
      await getPlatform()?.playRoVideoPlayer?.(localPath, {
        seekMs,
        loop: true,
        orientation: getSavedOrientation(),
        slot: el.slot === 1 ? 1 : 0,
        zIndex: el.zIndex || 2
      });
      return true;
    } catch (err) {
      console.warn("[TomorrowOS][video] roVideoPlayer in-place replay failed:", err);
    }
  }

  if (el && el.tagName === "VIDEO") {
    try {
      if (seekMs > 0) el.currentTime = seekMs / 1000;
      else el.currentTime = 0;
      await el.play();
      return true;
    } catch (err) {
      console.warn("[TomorrowOS][video] HTML in-place replay failed:", err);
    }
  }

  return false;
}

function schedulePlaylistAdvance(playlist, fromIndex, durationMs, initialSeekMs = 0) {
  const latestPlaylist = resolveLatestActivePlaylist(playlist);
  const loopItems = getCurrentLoopItems(latestPlaylist);
  if (!loopItems.length) return;

  const safeIndex = ((fromIndex % loopItems.length) + loopItems.length) % loopItems.length;
  const atLastInLoop = safeIndex >= loopItems.length - 1;
  const remainingMs = Math.max(1000, durationMs - (initialSeekMs || 0));
  clearPlaybackTimers();

  let invoked = false;
  const invokeAdvance = () => {
    if (invoked) return;
    invoked = true;
    clearPlaybackTimers();
    void advancePlaylistAfterItem(playlist, fromIndex);
  };

  playerState.contentTimer = setTimeout(invokeAdvance, remainingMs);
}

async function playPlaylistSequence(playlist, startIndex, initialSeekMs = 0, expectedGen = playbackGeneration) {
  if (!isPlaybackGenerationCurrent(expectedGen)) return;
  playerState.playlistPlaybackActive = true;
  const latestPlaylist = resolveLatestActivePlaylist(playlist);
  ensureActivePlaylistContext(latestPlaylist);

  try {
    const loopItems = ensurePlaylistLoopSnapshot(latestPlaylist, startIndex);
    if (!isPlaybackGenerationCurrent(expectedGen)) return;
    if (loopItems.length === 0) {
      await showBrandFallback();
      return;
    }

    const safeIndex = ((startIndex % loopItems.length) + loopItems.length) % loopItems.length;
    const item = loopItems[safeIndex];

    const atLastInLoop = safeIndex >= loopItems.length - 1;
    let nextItem;
    if (!atLastInLoop) {
      nextItem = loopItems[safeIndex + 1];
    } else {
      const nextLoopItems = getPlayableItems(latestPlaylist);
      nextItem = nextLoopItems.length ? nextLoopItems[0] : loopItems[0];
    }
    const nextUrl = typeof nextItem === "string" ? nextItem : nextItem?.url;
    const nextType = nextUrl ? getContentType(String(nextUrl).trim(), nextItem?.type) : null;
    if (loopItems.length > 1) {
      ensureContentCached(nextItem, { silent: true }).catch(() => {});
    }

    if (safeIndex === 0 && !initialSeekMs) {
      startBackgroundCacheForRemainingItems(latestPlaylist, 1);
      startPlaylistImageDecodeWarmup(latestPlaylist);
    }

    const playback = await showContent(item, {
      playlistId: latestPlaylist.id || "unnamed",
      index: safeIndex,
      total: loopItems.length,
      initialSeekMs: initialSeekMs || 0,
      playlist: latestPlaylist,
      nextIndex: atLastInLoop ? 0 : safeIndex + 1,
      nextType,
      playbackGen: expectedGen
    });

    if (!isPlaybackGenerationCurrent(expectedGen)) return;

    const durationMs = getContentDurationMs(item, playback);
    playerState.currentItemIndex = safeIndex;
    playerState.currentItemStartedAt = Date.now() - (initialSeekMs || 0);
    playerState.currentItemDurationMs = durationMs;

    if (loopItems.length > 1 && nextType === "image") {
      if (atLastInLoop) {
        void prefetchNextImageOnBackLayer(latestPlaylist, 0);
      } else {
        void prefetchNextImageOnBackLayer(latestPlaylist, safeIndex + 1, {
          loopItems
        });
      }
    }

    schedulePlaylistAdvance(latestPlaylist, safeIndex, durationMs, initialSeekMs || 0);
  } catch (err) {
    console.error("[TomorrowOS][video] playPlaylistSequence failed", {
      playlistId: playlist?.id || "unnamed",
      requestedIndex: startIndex,
      activeVideoLocalPath: playerState.activeVideoLocalPath || null,
      pendingVideoRemount: playerState.pendingVideoRemount === true,
      err: err?.message || String(err)
    });
    reportDeviceLog("error", "playPlaylistSequence failed", {
      playlistId: playlist?.id || "unnamed",
      requestedIndex: startIndex,
      activeVideoLocalPath: playerState.activeVideoLocalPath || null,
      pendingVideoRemount: playerState.pendingVideoRemount === true,
      err: err?.message || String(err)
    }, "playback");
    setDebug(`Playlist item failed: ${err.message}`);
    await showBrandFallback();
  } finally {
    clearOrientReloadPending();
    clearRebootResumePending();
    clearRepairResumePending();
  }
}

function getContentDurationMs(item, playback) {
  if (Number.isFinite(item?.durationMs) && item.durationMs >= 1000) {
    return item.durationMs;
  }

  if (playback.type === "image") return 10000;
  if (playback.type === "video") return 30000;
  if (playback.type === "widget") return 20000;
  return 15000;
}

function getContentType(url, explicitType) {
  if (typeof explicitType === "string" && explicitType.trim()) {
    return explicitType.toLowerCase();
  }

  const lower = getLowerPathFromUrl(url);
  if (IMAGE_EXTENSIONS.some((ext) => lower.endsWith(ext))) return "image";
  if (VIDEO_EXTENSIONS.some((ext) => lower.endsWith(ext))) return "video";
  if (lower.endsWith(".wgt") || lower.endsWith(".zip")) return "widget";
  return "web";
}

function contentLayersExistInDom() {
  const layer0 = document.getElementById("contentLayer0");
  const layer1 = document.getElementById("contentLayer1");
  return !!(
    layer0 &&
    layer1 &&
    contentArea &&
    contentArea.contains(layer0) &&
    contentArea.contains(layer1)
  );
}

function prepareContentBackLayer() {
  ensureContentLayers();
  const backLayer = getBackContentLayer();
  if (!backLayer) {
    throw new Error("Content back layer is not available");
  }
  clearContentLayer(backLayer);
  return backLayer;
}

function ensureContentLayers() {
  if (playerState.contentLayersReady && contentLayersExistInDom()) return;

  playerState.contentLayersReady = false;
  playerState.contentLayerActive = 0;
  if (!contentArea) {
    throw new Error("Content area element is missing");
  }

  contentArea.innerHTML = "";
  contentArea.style.position = "relative";
  contentArea.style.display = "block";
  contentArea.style.alignItems = "";
  contentArea.style.justifyContent = "";

  for (let i = 0; i < 2; i += 1) {
    const layer = document.createElement("div");
    layer.id = `contentLayer${i}`;
    layer.className = "content-layer";
    if (i === 0) layer.classList.add("content-layer--visible");
    contentArea.appendChild(layer);
  }

  playerState.contentLayerActive = 0;
  playerState.contentLayersReady = true;
}

function getContentLayer(index) {
  return document.getElementById(`contentLayer${index}`);
}

function getFrontContentLayer() {
  return getContentLayer(playerState.contentLayerActive);
}

function getBackContentLayer() {
  return getContentLayer(1 - playerState.contentLayerActive);
}

function clearContentLayer(layerEl) {
  releaseVideosInLayer(layerEl);
}

function swapContentLayers(options = {}) {
  const instant = options.instant === true;
  const frontIdx = playerState.contentLayerActive;
  const backIdx = 1 - frontIdx;
  const front = getContentLayer(frontIdx);
  const back = getContentLayer(backIdx);
  if (!front || !back) return;

  if (instant) {
    front.classList.add("content-layer--instant-swap");
    back.classList.add("content-layer--instant-swap");
    back.classList.add("content-layer--visible");
    front.classList.remove("content-layer--visible");
    playerState.contentLayerActive = backIdx;
    void back.offsetHeight;
    releaseVideosInLayer(front);
    requestAnimationFrame(() => {
      front.classList.remove("content-layer--instant-swap");
      back.classList.remove("content-layer--instant-swap");
    });
    return;
  }

  back.classList.add("content-layer--visible");
  front.classList.remove("content-layer--visible");

  const finalize = () => {
    releaseVideosInLayer(front);
  };

  let done = false;
  const onTransitionEnd = (event) => {
    if (done || event.propertyName !== "opacity") return;
    done = true;
    back.removeEventListener("transitionend", onTransitionEnd);
    finalize();
  };

  back.addEventListener("transitionend", onTransitionEnd);
  setTimeout(() => {
    if (done) return;
    done = true;
    back.removeEventListener("transitionend", onTransitionEnd);
    finalize();
  }, CONTENT_CROSSFADE_MS + 80);

  playerState.contentLayerActive = backIdx;
}

function loadImageElementForDisplay(src) {
  return new Promise((resolve, reject) => {
    const img = document.createElement("img");
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error("Image load failed"));
    img.src = toBrightSignMediaUrl(src);
  });
}

function downscaleImageToDisplayCap(sourceImg, capW, capH) {
  const nw = sourceImg.naturalWidth || 0;
  const nh = sourceImg.naturalHeight || 0;
  if (!nw || !nh || (nw <= capW && nh <= capH)) {
    return null;
  }

  const scale = Math.min(capW / nw, capH / nh, 1);
  const targetW = Math.max(1, Math.round(nw * scale));
  const targetH = Math.max(1, Math.round(nh * scale));
  const canvas = document.createElement("canvas");
  canvas.width = targetW;
  canvas.height = targetH;
  const ctx = canvas.getContext("2d");
  if (!ctx) return null;
  ctx.drawImage(sourceImg, 0, 0, targetW, targetH);

  try {
    return canvas.toDataURL("image/jpeg", 0.92);
  } catch (_) {
    try {
      return canvas.toDataURL("image/png");
    } catch (_) {
      return null;
    }
  }
}

async function resolveDisplayImageUrl(url, warmedImg) {
  const { width: capW, height: capH } = getLogicalDisplaySize();
  try {
    const source =
      warmedImg && warmedImg.naturalWidth > 0
        ? warmedImg
        : await loadImageElementForDisplay(url);
    const scaled = downscaleImageToDisplayCap(source, capW, capH);
    return scaled || url;
  } catch (_) {
    return url;
  }
}

function mountImageInLayer(layerEl, url, options = {}) {
  if (!layerEl) {
    return Promise.reject(new Error("Image layer element is not available"));
  }

  const mountDecoded = (displayUrl) =>
    new Promise((resolve, reject) => {
      const img = document.createElement("img");
      img.style.width = "100%";
      img.style.height = "100%";
      img.style.maxWidth = "100%";
      img.style.maxHeight = "100%";
      img.style.objectFit = cssObjectFitForContentFit();
      img.style.display = "block";
      img.style.backgroundColor = "#000";

      const finish = () => {
        const nw = img.naturalWidth || 0;
        const nh = img.naturalHeight || 0;
        if (nw > 0 && nh > 0) {
          img.setAttribute("width", String(nw));
          img.setAttribute("height", String(nh));
        }
        resolve(img);
      };
      const onReady = () => {
        if (typeof img.decode === "function") {
          img.decode().then(finish).catch(finish);
          return;
        }
        finish();
      };

      img.onload = onReady;
      img.onerror = () => reject(new Error("Image load failed"));
      layerEl.appendChild(img);
      img.src = displayUrl;
      if (img.complete && img.naturalWidth > 0) onReady();
    });

  return ensureImageDecoded(url)
    .catch(() => null)
    .then((warmed) => resolveDisplayImageUrl(url, warmed?.img || warmed))
    .then((displayUrl) => mountDecoded(displayUrl));
}

function mountVideoInLayer(layerEl, url, options = {}) {
  if (isBrightSignRuntime()) {
    return mountBrightSignRoVideoInLayer(layerEl, url, options);
  }
  return mountHtmlVideoInLayer(layerEl, url, options);
}

function mountBrightSignRoVideoInLayer(layerEl, url, options = {}) {
  if (!layerEl) {
    return Promise.reject(new Error("Video layer element is not available"));
  }

  const platform = getPlatform();
  if (typeof platform?.playRoVideoPlayer !== "function") {
    return Promise.reject(new Error("BrightSign roVideoPlayer bridge is not available"));
  }

  const slot = getNextBrightSignRoVideoSlot();
  const zIndex = getNextBrightSignRoVideoZIndex();
  releaseVideosInLayer(layerEl, { skipSlot: slot });
  const marker = document.createElement("div");
  marker.className = "content-video content-video--ro";
  marker.setAttribute("data-ro-video-player", "true");
  marker.setAttribute("data-ro-video-slot", String(slot));
  marker.style.width = "100%";
  marker.style.height = "100%";
  marker.style.backgroundColor = "transparent";
  layerEl.appendChild(marker);

  const roVideoElement = {
    tagName: "ROVIDEOPLAYER",
    localPath: url,
    slot,
    zIndex,
    layerEl,
    marker,
    play: () =>
      platform.playRoVideoPlayer(url, {
        seekMs: 0,
        loop: options.loop === true,
        orientation: getSavedOrientation(),
        slot,
        zIndex,
        hideHtmlDelayMs: Math.max(0, Number(options.hideHtmlDelayMs) || 0)
      }),
    pause: () => platform.stopRoVideoPlayer?.({ stopAll: true, showHtml: true }),
    stop: () => platform.stopRoVideoPlayer?.({ stopAll: true, showHtml: true })
  };

  return platform
    .playRoVideoPlayer(url, {
      seekMs: Math.max(0, Number(options.seekMs) || 0),
      loop: options.loop === true,
      orientation: getSavedOrientation(),
      slot,
      zIndex,
      hideHtmlDelayMs: Math.max(0, Number(options.hideHtmlDelayMs) || 0)
    })
    .then(() => roVideoElement)
    .catch((err) => {
      try {
        marker.remove();
      } catch (_) {}
      const localFileStat = platform?.statLocalFile?.(url) || null;
      const errDetails = {
        localPath: url,
        fileSize: localFileStat?.size ?? null,
        absPath: localFileStat?.absPath ?? null,
        err: err?.message || String(err)
      };
      console.error("[TomorrowOS][video] roVideoPlayer load failed", errDetails);
      reportDeviceLog("error", "roVideoPlayer load failed", errDetails, "ro-video-player");
      throw err;
    });
}

function mountHtmlVideoInLayer(layerEl, url, options = {}) {
  if (!layerEl) {
    return Promise.reject(new Error("Video layer element is not available"));
  }
  releaseVideosInLayer(layerEl);
  const seekMs = Math.max(0, Number(options.seekMs) || 0);
  return new Promise((resolve, reject) => {
    const video = document.createElement("video");
    video.className = "content-video content-video--loading";
    video.preload = "auto";
    video.autoplay = true;
    video.loop = options.loop === true;
    video.muted = true;
    video.controls = false;
    video.playsInline = true;
    video.style.width = "100%";
    video.style.height = "100%";
    video.style.objectFit = cssObjectFitForContentFit();
    video.style.backgroundColor = "#000";
    applyBrightSignVideoPlaybackAttributes(video);

    let settled = false;
    const settle = () => {
      if (settled) return;
      settled = true;
      cleanup();
      video.classList.remove("content-video--loading");
      resolve(video);
    };

    const cleanup = () => {
      clearTimeout(fallbackTimer);
      video.removeEventListener("playing", onPlaying);
      video.removeEventListener("error", onError);
    };

    const onPlaying = () => {
      requestAnimationFrame(() => {
        requestAnimationFrame(settle);
      });
    };

    const playUrl = toBrightSignMediaUrl(url);
    const localFileStat = getPlatform()?.statLocalFile?.(url) || null;

    const onError = () => {
      const mediaErr = video.error;
      const errDetails = {
        src: video.currentSrc || video.src || playUrl,
        localPath: url,
        fileUri: playUrl,
        fileSize: localFileStat?.size ?? null,
        absPath: localFileStat?.absPath ?? null,
        code: mediaErr?.code ?? null,
        message: mediaErr?.message ?? null,
        readyState: video.readyState,
        networkState: video.networkState
      };
      console.error("[TomorrowOS][video] HTML video load failed", errDetails);
      reportDeviceLog("error", "HTML video load failed", errDetails, "html-video");
      cleanup();
      reject(new Error(`Video load failed (code=${mediaErr?.code ?? "unknown"})`));
    };

    const fallbackTimer = setTimeout(() => {
      if (video.readyState >= 2) settle();
    }, 12000);

    if (seekMs > 0) {
      const onLoadedMeta = () => {
        try { video.currentTime = seekMs / 1000; } catch (_) {}
      };
      video.addEventListener("loadedmetadata", onLoadedMeta, { once: true });
    }

    video.addEventListener("playing", onPlaying, { once: true });
    video.addEventListener("error", onError, { once: true });

    layerEl.appendChild(video);
    video.src = playUrl;

    const playPromise = video.play();
    if (playPromise && typeof playPromise.catch === "function") {
      playPromise.catch(() => {
        if (video.readyState >= 2) onPlaying();
      });
    }
  });
}

function mountIframeInLayer(layerEl, url, options = {}) {
  if (!layerEl) {
    return Promise.reject(new Error("Widget layer element is not available"));
  }
  return new Promise((resolve) => {
    const iframe = document.createElement("iframe");
    iframe.src = url;
    iframe.style.width = "100%";
    iframe.style.height = "100%";
    iframe.style.border = "0";
    if (options.widget) {
      iframe.setAttribute("data-content-type", "widget");
    }

    let settled = false;
    const finish = () => {
      if (settled) return;
      settled = true;
      resolve(iframe);
    };

    iframe.addEventListener("load", finish);
    layerEl.appendChild(iframe);
    setTimeout(finish, 800);
  });
}

async function showBrandFallback(message = "", options = {}) {
  releaseAllPlaybackVideos();
  if (contentPanel) contentPanel.style.display = "none";

  const statusSub =
    message && String(message).trim()
      ? String(message).trim()
      : "Awaiting content";
  await showIdleScreen({
    variant: "fallback",
    statusSub,
    forceIdleReload: options.forceIdleReload === true
  });
  return {
    status: "fallback",
    fallback: "brand"
  };
}


//Show content
async function showContent(content, context = {}) {
  const expectedGen = Number.isFinite(context.playbackGen)
    ? context.playbackGen
    : playbackGeneration;
  const rawUrl = typeof content === "string" ? content : content?.url;
  if (typeof rawUrl !== "string" || !rawUrl.trim()) {
    throw new Error("Content URL is required");
  }

  const url = rawUrl.trim();
  const type = getContentType(url, content?.type);
  const wasBrightSignRoVideo = isBrightSignRoVideoElement(playerState.activeElement);

  hideIdleScreen();
  if (pairingArea) pairingArea.style.display = "none";
  contentPanel.style.display = "block";
  if (contentArea) contentArea.style.display = "block";

  setDebug(
    `Playing ${type} ${context.playlistId ? `[${context.playlistId} ${context.index + 1}/${context.total}]` : ""}`
  );

  if (type === "image") {
    const cache = await ensureContentCached(content);
    if (!playerState.playlistPlaybackActive || !isPlaybackGenerationCurrent(expectedGen)) {
      throw new Error("Playback cancelled before image mount");
    }
    if (!cache.localPath) {
      throw new Error("Image cache path not available");
    }

    let prefetched = tryConsumePrefetchedImage(url, context.index);
    if (prefetched) {
      playerState.activeElement = prefetched.element;
      playerState.activeVideoLocalPath = null;
      swapContentLayers({ instant: true });
      if (wasBrightSignRoVideo) await handoffBrightSignRoVideoToHtml();
      return {
        type: "image",
        content_url: url,
        cached: true,
        cache_hit: prefetched.cacheHit,
        local_path: prefetched.localPath
      };
    }

    await waitForPrefetchedImageForCurrentItem(url, context.index);

    prefetched = tryConsumePrefetchedImage(url, context.index);
    if (prefetched) {
      playerState.activeElement = prefetched.element;
      playerState.activeVideoLocalPath = null;
      swapContentLayers({ instant: true });
      if (wasBrightSignRoVideo) await handoffBrightSignRoVideoToHtml();
      return {
        type: "image",
        content_url: url,
        cached: true,
        cache_hit: prefetched.cacheHit,
        local_path: prefetched.localPath
      };
    }

    setDebug(cache.hit ? "Image cache hit. Playing local cached image..." : "Image cached. Playing local image...");
    const backLayer = prepareContentBackLayer();
    playerState.activeElement = await mountImageInLayer(backLayer, cache.localPath);
    playerState.activeVideoLocalPath = null;
    swapContentLayers({ instant: true });
    if (wasBrightSignRoVideo) await handoffBrightSignRoVideoToHtml();
    return {
      type: "image",
      content_url: url,
      cached: true,
      cache_hit: cache.hit,
      local_path: cache.localPath
    };
  }

  let backLayer = null;

  if (type === "video") {
    const cache = await ensureContentCached(content);
    if (!playerState.playlistPlaybackActive || !isPlaybackGenerationCurrent(expectedGen)) {
      throw new Error("Playback cancelled before video mount");
    }
    ensureContentLayers();
    backLayer = getBackContentLayer();
    if (!backLayer) {
      throw new Error("Content back layer is not available");
    }
    if (!cache.localPath) {
      throw new Error("Video cache path not available");
    }
    setDebug(cache.hit ? "Video cache hit. Playing local cached video..." : "Video cached. Playing local video...");
    const remountingVideo = !!playerState.activeVideoLocalPath;
    const brightSignHtmlToVideoHandoff = isBrightSignRuntime() && !wasBrightSignRoVideo;
    playerState.pendingVideoRemount = remountingVideo;
    try {
      playerState.activeElement = await mountVideoInLayer(
        backLayer,
        cache.localPath,
        {
          seekMs: Number(context?.initialSeekMs) || 0,
          isRemount: remountingVideo,
          loop: Number(context?.total) <= 1,
          hideHtmlDelayMs: wasBrightSignRoVideo ? 0 : BRIGHTSIGN_IMAGE_TO_VIDEO_HIDE_DELAY_MS
        }
      );
    } finally {
      playerState.pendingVideoRemount = false;
    }
    if (!isPlaybackGenerationCurrent(expectedGen)) {
      throw new Error("Playback cancelled after video mount");
    }
    if (brightSignHtmlToVideoHandoff) {
      await delayMs(BRIGHTSIGN_IMAGE_TO_VIDEO_SWAP_DELAY_MS);
      if (!isPlaybackGenerationCurrent(expectedGen)) {
        throw new Error("Playback cancelled during video handoff");
      }
    }
    playerState.activeVideoLocalPath = cache.localPath;
    swapContentLayers({ instant: true });
    return {
      type: "video",
      content_url: url,
      cached: true,
      cache_hit: cache.hit,
      local_path: cache.localPath
    };
  }

  if (type === "widget") {
    const widget = await prepareWidgetPackage(content);
    if (!playerState.playlistPlaybackActive) {
      throw new Error("Playback cancelled before widget mount");
    }
    backLayer = prepareContentBackLayer();
    playerState.activeElement = await mountIframeInLayer(backLayer, widget.launchUrl, { widget: true });
    playerState.activeVideoLocalPath = null;
    swapContentLayers();
    if (wasBrightSignRoVideo) await handoffBrightSignRoVideoToHtml();
    playerState.activeWidget = {
      destroy: () => {
        playerState.activeWidget = null;
      }
    };

    return {
      type: "widget",
      content_url: url,
      cached: true,
      local_path: widget.localPath,
      launch_url: widget.launchUrl
    };
  }

  backLayer = prepareContentBackLayer();
  playerState.activeElement = await mountIframeInLayer(backLayer, url);
  playerState.activeVideoLocalPath = null;
  swapContentLayers();
  if (wasBrightSignRoVideo) await handoffBrightSignRoVideoToHtml();

  return {
    type: "web",
    content_url: url,
    cached: false
  };
}

async function preCacheFirstPolicyContent(policy) {
  const playlists = Array.isArray(policy?.playlists) ? policy.playlists : [];
  for (let i = 0; i < playlists.length; i += 1) {
    const items = getPlayableItems(playlists[i]);
    if (items.length === 0) continue;
    await ensureContentCached(items[0], { silent: true });
    return;
  }
}

function startBackgroundCacheForRemainingItems(playlist, startIndex) {
  const sessionId = playerState.backgroundCacheSession;
  cachePlaylistSequentially(playlist, startIndex, sessionId).catch((err) => {
    setDebug(`Background cache failed: ${err.message}`);
  });
}

async function cachePlaylistSequentially(playlist, startIndex, sessionId) {
  const items = getPlayableItems(playlist);
  if (items.length <= 1) return;

  for (let i = Math.max(startIndex, 0); i < items.length; i += 1) {
    if (sessionId !== playerState.backgroundCacheSession) return;
    await ensureContentCached(items[i], { silent: true });
  }
}

function getCacheRequestKey(type, url) {
  const dedupKeys = getMediaDedupKeys(url);
  if (dedupKeys.length > 0) return `${type}:${dedupKeys[0]}`;
  return `${type}:${url}`;
}

async function ensureContentCached(content, options = {}) {
  const rawUrl = typeof content === "string" ? content : content?.url;
  if (typeof rawUrl !== "string" || !rawUrl.trim()) {
    return { localPath: null, hit: false, cached: false };
  }

  const url = rawUrl.trim();
  const type = getContentType(url, content?.type);

  if (type !== "image" && type !== "video") {
    return { localPath: null, hit: false, cached: false };
  }

  const cachedPath = await getCachedMediaPath(type, url);
  if (cachedPath) {
    return { localPath: cachedPath, hit: true, cached: true };
  }

  const cacheKey = getCacheRequestKey(type, url);
  if (!inFlightCacheByKey[cacheKey]) {
    inFlightCacheByKey[cacheKey] = (async () => {
      if (!options.silent) {
        setDebug(`${type === "video" ? "Video" : "Image"} detected. Starting cache download...`);
      }

      const ext = type === "video"
        ? getFileExtensionFromUrl(url, ".mp4")
        : getFileExtensionFromUrl(url, ".jpg");
      const prefix = type === "video" ? "video" : "image";
      const fileName = sanitizeFileName(`${prefix}-${Date.now()}${ext}`, `${prefix}${ext}`);
      const localPath = await downloadToCache(url, fileName);
      rememberMediaCache(type, url, localPath);
      return localPath;
    })().finally(() => {
      delete inFlightCacheByKey[cacheKey];
    });
  }

  const localPath = await inFlightCacheByKey[cacheKey];
  if (!options.silent) {
    setDebug("Download finished. Playing local media...");
  }

  return { localPath, hit: false, cached: true };
}


// Create local folders
function ensureDir(path) {
  return new Promise((resolve, reject) => {
    getPlatform().filesystem.resolve(
      path,
      (dir) => resolve(dir),
      (err) => {

        const parts = path.split("/");
        let current = "";

        function createNext(i) {
          if (i >= parts.length) return resolve();

          current += (i === 0 ? "" : "/") + parts[i];

          getPlatform().filesystem.resolve(
            current,
            () => createNext(i + 1),
            () => {
              getPlatform().filesystem.resolve(
                parts.slice(0, i).join("/") || "Downloads",
                (parent) => {
                  parent.createDirectory(parts[i]);
                  createNext(i + 1);
                },
                reject,
                "rw"
              );
            },
            "rw"
          );
        }

        createNext(0);
      },
      "rw"
    );
  });
}


async function initStorage() {
  await ensureDir("downloads/tomorrowos");
  await ensureDir("downloads/tomorrowos/staging");
  await ensureDir("downloads/tomorrowos/current");
}

const storageReady = initStorage();



function setDebug(message) {
  console.log("[TomorrowOS]", message);
  DownloadStatus.textContent = message;
}

function loadCacheIndex(storageKey) {
  try {
    const raw = localStorage.getItem(storageKey);
    if (!raw) return {};
    const parsed = JSON.parse(raw);
    return parsed && typeof parsed === "object" ? parsed : {};
  } catch (err) {
    return {};
  }
}

function persistCacheIndex(storageKey, cacheMap) {
  try {
    localStorage.setItem(storageKey, JSON.stringify(cacheMap));
  } catch (err) {
    setDebug(`Persist cache failed: ${err.message}`);
  }
}

function getCacheBucket(type) {
  if (type === "video") {
    return {
      map: mediaCache.videosByUrl,
      key: VIDEO_CACHE_STORAGE_KEY
    };
  }

  if (type === "image") {
    return {
      map: mediaCache.imagesByUrl,
      key: IMAGE_CACHE_STORAGE_KEY
    };
  }

  throw new Error(`Unsupported cache type: ${type}`);
}

function getDedupCacheBucket(type) {
  if (type === "video") {
    return {
      map: mediaCache.videosByDedup,
      key: VIDEO_CACHE_DEDUP_STORAGE_KEY
    };
  }

  if (type === "image") {
    return {
      map: mediaCache.imagesByDedup,
      key: IMAGE_CACHE_DEDUP_STORAGE_KEY
    };
  }

  throw new Error(`Unsupported cache type: ${type}`);
}

/** Keys for matching prior downloads (content hash prefix and/or logical filename). */
function getMediaDedupKeys(url) {
  const base = getLowerPathFromUrl(url).split("/").pop() || "";
  if (!base) return [];

  const keys = [];
  const hashPrefix = base.match(/^([0-9a-f]{16})-/i);
  if (hashPrefix) keys.push(`h:${hashPrefix[1].toLowerCase()}`);

  const logicalName = base
    .replace(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}-/i, "")
    .replace(/^[0-9a-f]{16}-/i, "");
  if (logicalName) keys.push(`n:${logicalName.toLowerCase()}`);

  return keys;
}

function findCachedPathByDedupKeys(type, url) {
  const dedupBucket = getDedupCacheBucket(type);
  const keys = getMediaDedupKeys(url);
  for (let i = 0; i < keys.length; i += 1) {
    const hit = dedupBucket.map[keys[i]];
    if (hit) return hit;
  }
  return null;
}

function rememberMediaCache(type, url, localPath) {
  const bucket = getCacheBucket(type);
  bucket.map[url] = localPath;
  persistCacheIndex(bucket.key, bucket.map);

  const dedupBucket = getDedupCacheBucket(type);
  const dedupKeys = getMediaDedupKeys(url);
  for (let i = 0; i < dedupKeys.length; i += 1) {
    dedupBucket.map[dedupKeys[i]] = localPath;
  }
  if (dedupKeys.length > 0) {
    persistCacheIndex(dedupBucket.key, dedupBucket.map);
  }
}

function dropMediaCache(type, url) {
  const bucket = getCacheBucket(type);
  if (!bucket.map[url]) return;
  delete bucket.map[url];
  persistCacheIndex(bucket.key, bucket.map);
}

function isLocalPathAvailable(path) {
  if (typeof path !== "string" || !path.trim()) return Promise.resolve(false);
  if (!getPlatform()?.filesystem?.resolve) return Promise.resolve(true);

  return new Promise((resolve) => {
    getPlatform().filesystem.resolve(
      path,
      () => resolve(true),
      () => resolve(false),
      "r"
    );
  });
}

async function getCachedMediaPath(type, url) {
  const bucket = getCacheBucket(type);
  let localPath = bucket.map[url] || findCachedPathByDedupKeys(type, url);
  if (!localPath) return null;

  const available = await isLocalPathAvailable(localPath);
  if (!available) {
    dropMediaCache(type, url);
    return null;
  }

  if (!bucket.map[url]) {
    rememberMediaCache(type, url, localPath);
  }

  return localPath;
}

function getLowerPathFromUrl(url) {
  if (typeof url !== "string") return "";
  try {
    const parsed = new URL(url);
    return parsed.pathname.toLowerCase();
  } catch (err) {
    return url.split("?")[0].split("#")[0].toLowerCase();
  }
}

function getFileExtensionFromUrl(url, fallbackExt) {
  const lowerPath = getLowerPathFromUrl(url);
  const dotIndex = lowerPath.lastIndexOf(".");
  if (dotIndex < 0) return fallbackExt;
  const ext = lowerPath.slice(dotIndex);
  if (!/^\.[a-z0-9]+$/.test(ext)) return fallbackExt;
  return ext;
}

function resolveContentDownloadUrl(url) {
  const trimmed = String(url || "").trim();
  if (!trimmed) return trimmed;
  if (/^https?:\/\//i.test(trimmed)) return trimmed;
  if (/^wss?:\/\//i.test(trimmed)) {
    return trimmed.replace(/^wss:/i, "https:").replace(/^ws:/i, "http:");
  }
  if (!httpEndpoint) return trimmed;
  try {
    return new URL(trimmed, `${httpEndpoint}/`).href;
  } catch (_) {
    return trimmed;
  }
}

async function downloadToCache(url, fileName) {
  await storageReady;

  const downloadUrl = resolveContentDownloadUrl(url);
  if (!downloadUrl || !/^https?:\/\//i.test(downloadUrl)) {
    throw new Error(`download URL is not absolute: ${String(url || "").slice(0, 160)}`);
  }

  return new Promise((resolve, reject) => {
    try {
      setDebug("Preparing download...");

      const platform = getPlatform();
      if (!platform?.download) {
        throw new Error("TomorrowPlatform download API not available");
      }

      const destination = "downloads/tomorrowos/staging";

      setDebug("Creating download request...");

      const request = new platform.DownloadRequest(
        downloadUrl,
        destination,
        fileName
      );

      const listener = {
        onprogress: function (id, receivedSize, totalSize) {
          setDebug(`Downloading... ${receivedSize}/${totalSize}`);
        },

        oncompleted: function (id, fullPath) {
          delete activeDownloads[id];
          setDebug(`Download completed: ${fullPath}`);
          resolve(fullPath);
        },

        onfailed: function (id, error) {
          delete activeDownloads[id];
          setDebug(`Download failed: ${error.name} ${error.message}`);
          console.error("[TomorrowOS][download] failed", {
            sourceUrl: String(url || "").slice(-160),
            downloadUrl: String(downloadUrl || "").slice(-160),
            fileName,
            error: error?.message || String(error)
          });
          reject(new Error(error.name + ": " + error.message));
        },

        oncanceled: function (id) {
          delete activeDownloads[id];
          setDebug(`Download canceled: ${id}`);
          reject(new Error("Download canceled"));
        },

        onpaused: function (id) {
          setDebug(`Download paused: ${id}`);
        }
      };

      setDebug("Starting download...");

      const downloadId = platform.download.start(request, listener);
      activeDownloads[downloadId] = true;

      setDebug(`Download started: ${downloadId}`);

    } catch (err) {
      setDebug(`Download setup failed: ${err.message}`);
      reject(new Error(`downloadToCache setup failed: ${err.message}`));
    }
  });
}

function cancelActiveDownloads() {
  if (!getPlatform()?.download?.cancel) return;

  const downloadIds = Object.keys(activeDownloads);
  for (let i = 0; i < downloadIds.length; i += 1) {
    const idText = downloadIds[i];
    const id = Number(idText);
    try {
      getPlatform().download.cancel(Number.isNaN(id) ? idText : id);
    } catch (err) {
      setDebug(`Cancel download failed (${idText}): ${err.message}`);
    } finally {
      delete activeDownloads[idText];
    }
  }
}

function sanitizeFileName(baseName, fallback) {
  if (typeof baseName !== "string" || !baseName.trim()) return fallback;
  return baseName.replace(/[^\w.\-]/g, "_");
}

async function prepareWidgetPackage(url) {
  const source = typeof url === "string" ? { url } : (url || {});
  const packageUrl = typeof source.url === "string" ? source.url.trim() : "";
  const explicitLaunchUrl = typeof source.launchUrl === "string"
    ? source.launchUrl.trim()
    : (typeof source.entryUrl === "string" ? source.entryUrl.trim() : "");
  const lowerPackageUrl = packageUrl.toLowerCase();
  const isPackageUrl = lowerPackageUrl.endsWith(".wgt") || lowerPackageUrl.endsWith(".zip");
  const entryFile = typeof source.entryFile === "string" && source.entryFile.trim()
    ? source.entryFile.trim()
    : DEFAULT_WIDGET_ENTRY_FILE;
  const widgetKey = getWidgetCacheKey(source);

  // Stable V1 behavior:
  // - .zip/.wgt is treated as package source for local caching and local launch
  // - launchUrl/entryUrl (or direct html url) is used for non-package widget urls
  const launchUrl = explicitLaunchUrl || (!isPackageUrl ? packageUrl : "");
  if (!isPackageUrl && !launchUrl) {
    throw new Error("Widget URL is required");
  }

  let localPath = null;
  if (isPackageUrl) {
    const cachedEntryUrl = await getCachedWidgetEntryPath(widgetKey);
    if (cachedEntryUrl) {
      return {
        localPath: mediaCache.widgetsBySource[widgetKey]?.packagePath || null,
        launchUrl: cachedEntryUrl
      };
    }

    if (!inFlightWidgetByKey[widgetKey]) {
      inFlightWidgetByKey[widgetKey] = (async () => {
        const ext = lowerPackageUrl.endsWith(".wgt") ? ".wgt" : ".zip";
        const fileName = sanitizeFileName(`widget-${Date.now()}${ext}`, `widget${ext}`);
        const packagePath = await downloadToCache(packageUrl, fileName);
        const extractDir = await extractWidgetArchiveToLocal(packagePath, widgetKey);
        const localEntryPath = await findWidgetEntryPath(extractDir, entryFile);
        if (!localEntryPath) {
          throw new Error(`Widget entry file not found after extract: ${entryFile}`);
        }
        const localEntryUrl = await toLocalFileUrl(localEntryPath);

        rememberWidgetCache(widgetKey, {
          packagePath,
          extractDir,
          localEntryPath,
          localEntryUrl
        });

        return {
          localPath: packagePath,
          launchUrl: localEntryUrl
        };
      })().finally(() => {
        delete inFlightWidgetByKey[widgetKey];
      });
    }

    return inFlightWidgetByKey[widgetKey];
  }

  return {
    localPath,
    launchUrl
  };
}

function getWidgetCacheKey(source = {}) {
  const packageUrl = typeof source.url === "string" ? source.url.trim() : "";
  const version = typeof source.version === "string" && source.version.trim()
    ? source.version.trim()
    : "v1";
  return `${packageUrl}::${version}`;
}

function getWidgetFolderName(cacheKey) {
  let hash = 0;
  for (let i = 0; i < cacheKey.length; i += 1) {
    hash = ((hash << 5) - hash) + cacheKey.charCodeAt(i);
    hash |= 0;
  }
  return `widget_${Math.abs(hash)}`;
}

function rememberWidgetCache(cacheKey, data) {
  mediaCache.widgetsBySource[cacheKey] = data;
  persistCacheIndex(WIDGET_CACHE_STORAGE_KEY, mediaCache.widgetsBySource);
}

function dropWidgetCache(cacheKey) {
  if (!mediaCache.widgetsBySource[cacheKey]) return;
  delete mediaCache.widgetsBySource[cacheKey];
  persistCacheIndex(WIDGET_CACHE_STORAGE_KEY, mediaCache.widgetsBySource);
}

async function getCachedWidgetEntryPath(cacheKey) {
  const record = mediaCache.widgetsBySource[cacheKey];
  if (!record || typeof record.localEntryPath !== "string") return null;

  const available = await isLocalPathAvailable(record.localEntryPath);
  if (!available) {
    dropWidgetCache(cacheKey);
    return null;
  }

  if (typeof record.localEntryUrl === "string" && record.localEntryUrl.trim()) {
    return record.localEntryUrl;
  }

  const localEntryUrl = await toLocalFileUrl(record.localEntryPath);
  rememberWidgetCache(cacheKey, {
    ...record,
    localEntryUrl
  });
  return localEntryUrl;
}

async function extractWidgetArchiveToLocal(packagePath, cacheKey) {
  await storageReady;
  if (!getPlatform()?.archive?.open) {
    throw new Error("TomorrowPlatform archive API not available for widget extraction");
  }

  const folderName = getWidgetFolderName(cacheKey);
  const targetDir = `downloads/tomorrowos/current/widgets/${folderName}_${Date.now()}`;
  await ensureDir("downloads/tomorrowos/current/widgets");
  await ensureDir(targetDir);

  return new Promise((resolve, reject) => {
    try {
      getPlatform().archive.open(
        packagePath,
        "r",
        (archive) => {
          try {
            archive.extractAll(
              targetDir,
              () => {
                archive.close();
                resolve(targetDir);
              },
              (err) => {
                archive.close();
                reject(new Error(`Widget extract failed: ${err.message || err.name || err}`));
              }
            );
          } catch (err) {
            try {
              archive.close();
            } catch (_) {
              // ignore close errors after extract failure
            }
            reject(new Error(`Widget extract setup failed: ${err.message}`));
          }
        },
        (err) => {
          reject(new Error(`Open widget archive failed: ${err.message || err.name || err}`));
        }
      );
    } catch (err) {
      reject(new Error(`Archive API error: ${err.message}`));
    }
  });
}

async function findWidgetEntryPath(extractDir, entryFile) {
  const normalizedEntry = String(entryFile || DEFAULT_WIDGET_ENTRY_FILE).replace(/^\/+/, "").trim();
  if (!normalizedEntry) return null;

  const direct = `${extractDir}/${normalizedEntry}`.replace(/\\/g, "/");
  if (await isLocalPathAvailable(direct)) return direct;

  const fileName = normalizedEntry.split("/").pop();
  if (!fileName) return null;

  const found = await findFileByNameRecursively(extractDir, fileName, 4);
  return found;
}

function findFileByNameRecursively(rootPath, fileName, maxDepth) {
  if (!getPlatform()?.filesystem?.resolve) return Promise.resolve(null);

  function walk(path, depthLeft) {
    return new Promise((resolve) => {
      getPlatform().filesystem.resolve(
        path,
        (entry) => {
          if (!entry || !entry.isDirectory || depthLeft < 0) return resolve(null);
          entry.listFiles(
            async (children) => {
              for (let i = 0; i < children.length; i += 1) {
                const child = children[i];
                if (!child) continue;
                if (!child.isDirectory && child.name === fileName) {
                  return resolve(child.fullPath || null);
                }
              }

              for (let i = 0; i < children.length; i += 1) {
                const child = children[i];
                if (!child?.isDirectory) continue;
                const found = await walk(child.fullPath, depthLeft - 1);
                if (found) return resolve(found);
              }

              return resolve(null);
            },
            () => resolve(null)
          );
        },
        () => resolve(null),
        "r"
      );
    });
  }

  return walk(rootPath, maxDepth);
}

function toLocalFileUrl(path) {
  if (typeof path !== "string" || !path.trim()) return Promise.resolve(path);
  if (isNativePlayerRuntime()) {
    return Promise.resolve(toBrightSignMediaUrl(path));
  }
  if (!getPlatform()?.filesystem?.resolve) return Promise.resolve(path);

  return new Promise((resolve) => {
    getPlatform().filesystem.resolve(
      path,
      (entry) => {
        if (typeof entry?.toURI === "function") {
          return resolve(entry.toURI());
        }
        return resolve(toBrightSignMediaUrl(entry?.fullPath || path));
      },
      () => resolve(toBrightSignMediaUrl(path)),
      "r"
    );
  });
}
