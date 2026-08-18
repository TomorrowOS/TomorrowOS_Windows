from pathlib import Path

path = Path(__file__).resolve().parents[1] / "core" / "main.js"
text = path.read_text(encoding="utf-8")

old = """function isBrightSignRuntime() {
  return window.TomorrowPlatform?.id === "brightsign";
}

function getPlatform() {
  return window.TomorrowPlatform || null;
}

function toBrightSignMediaUrl(localPath) {
  if (!isBrightSignRuntime() || typeof localPath !== "string" || !localPath.trim()) {
    return localPath;
  }"""

new = """function isBrightSignRuntime() {
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
  }"""

if old not in text:
    raise SystemExit("block1 not found")
text = text.replace(old, new, 1)

text = text.replace(
    """function getPlayerBootUptimeSec() {
  if (isBrightSignRuntime()) {
    const uptimeSec = window.TomorrowPlatform.getBootUptimeSec();
    if (Number.isFinite(uptimeSec) && uptimeSec >= 0) return uptimeSec;
  }""",
    """function getPlayerBootUptimeSec() {
  if (isNativePlayerRuntime()) {
    const uptimeSec = window.TomorrowPlatform.getBootUptimeSec();
    if (Number.isFinite(uptimeSec) && uptimeSec >= 0) return uptimeSec;
  }""",
    1,
)

text = text.replace(
    """  const platformId = isBrightSignRuntime() ? "brightsign" : "web";
  const defaultName = isBrightSignRuntime() ? "BrightSign Player" : "BrightSign Player (dev)";

  return {
    platform: platformId,
    playerVersion: "1.0.0",
    deviceName: model || defaultName,
    system: "BrightSignOS",""",
    """  let platformId = "web";
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
    system: systemName,""",
    1,
)

text = text.replace(
    """    ["Platform", isBrightSignRuntime() ? "brightsign" : "web"],""",
    """    ["Platform", isWindowsRuntime() ? "windows" : isBrightSignRuntime() ? "brightsign" : "web"],""",
    1,
)

text = text.replace(
    """function getDeviceCapabilities() {
  const isPlayerRuntime = isBrightSignRuntime();""",
    """function getDeviceCapabilities() {
  const isPlayerRuntime = isNativePlayerRuntime();""",
    1,
)

text = text.replace(
    """function toLocalFileUrl(path) {
  if (typeof path !== "string" || !path.trim()) return Promise.resolve(path);
  if (isBrightSignRuntime()) {
    return Promise.resolve(toBrightSignMediaUrl(path));
  }""",
    """function toLocalFileUrl(path) {
  if (typeof path !== "string" || !path.trim()) return Promise.resolve(path);
  if (isNativePlayerRuntime()) {
    return Promise.resolve(toBrightSignMediaUrl(path));
  }""",
    1,
)

old_boot = """function applyBootConfig() {
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

  const normalized = normalizeCmsEndpointInput(TOMORROWOS_CONFIG.cmsEndpoint);
  if (!normalized) {
    const message = `Invalid cmsEndpoint in config.js: ${TOMORROWOS_CONFIG.cmsEndpoint}`;
    console.error("[TomorrowOS]", message);
    if (typeof window.__tomorrowShowBootError === "function") {
      window.__tomorrowShowBootError(message);
    }
    return false;
  }

  persistCmsEndpoint(normalized);
  setCmsEndpoints(normalized);
  console.log("[TomorrowOS] Boot config applied", { orientation, cmsEndpoint: normalized });
  return true;
}"""

new_boot = """function applyBootConfig() {
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
  console.log("[TomorrowOS] Boot config applied", { orientation, cmsEndpoint: normalized });
  return true;
}"""

if old_boot not in text:
    raise SystemExit("applyBootConfig block not found")
text = text.replace(old_boot, new_boot, 1)

old_onload = """window.onload = function () {
  removeStaticBootShell();
  resetDocumentLayoutViewport();

  if (!applyBootConfig()) return;

  void ensureCmsReachableAndProceed();
};"""

new_onload = """window.onload = function () {
  removeStaticBootShell();
  resetDocumentLayoutViewport();

  const bootResult = applyBootConfig();
  if (bootResult === false) return;
  if (bootResult === "needs-setup") {
    showCmsSetupUI();
    return;
  }

  void ensureCmsReachableAndProceed();
};"""

if old_onload not in text:
    raise SystemExit("onload block not found")
text = text.replace(old_onload, new_onload, 1)

path.write_text(text, encoding="utf-8")
print("main.js patched OK")
