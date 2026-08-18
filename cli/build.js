import fs from "fs";
import path from "path";
import { spawnSync } from "child_process";
import { fileURLToPath } from "url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.join(__dirname, "..");
const HOST = path.join(ROOT, "host");
const OUT = path.join(ROOT, "build", "windows");
const PAYLOAD = path.join(OUT, "payload");

function run(cmd, args, cwd) {
  // Quote args that contain spaces when using shell:true on Windows.
  const rendered = args.map((arg) => (/\s/.test(arg) ? `"${arg}"` : arg));
  console.log(`> ${cmd} ${rendered.join(" ")}`);
  const result = spawnSync(cmd, rendered, { cwd, stdio: "inherit", shell: true });
  if (result.status !== 0) {
    process.exit(result.status || 1);
  }
}

function copyDir(src, dest) {
  fs.mkdirSync(dest, { recursive: true });
  for (const entry of fs.readdirSync(src, { withFileTypes: true })) {
    const from = path.join(src, entry.name);
    const to = path.join(dest, entry.name);
    if (entry.isDirectory()) {
      copyDir(from, to);
      continue;
    }
    try {
      fs.copyFileSync(from, to);
    } catch (err) {
      console.warn(`Skip locked file ${to}: ${err.message}`);
    }
  }
}

function rimrafBestEffort(target) {
  try {
    fs.rmSync(target, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  } catch (err) {
    console.warn(`Could not fully remove ${target}: ${err.message}`);
    console.warn("Continuing with in-place overwrite (close TomorrowOS.Player.exe if files stay locked).");
  }
}

rimrafBestEffort(OUT);
fs.mkdirSync(PAYLOAD, { recursive: true });

const playerOut = path.join(OUT, "player");
const watchdogOut = path.join(OUT, "watchdog");
const setupOut = path.join(OUT, "setup");

run(
  "dotnet",
  [
    "publish",
    "TomorrowOS.Player/TomorrowOS.Player.csproj",
    "-c",
    "Release",
    "-r",
    "win-x64",
    "--self-contained",
    "true",
    "-o",
    playerOut
  ],
  HOST
);

// Single-file Watchdog so its runtime DLLs never overwrite Player's WPF assemblies.
run(
  "dotnet",
  [
    "publish",
    "TomorrowOS.Watchdog/TomorrowOS.Watchdog.csproj",
    "-c",
    "Release",
    "-r",
    "win-x64",
    "--self-contained",
    "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-o",
    watchdogOut
  ],
  HOST
);

run(
  "dotnet",
  [
    "publish",
    "TomorrowOS.Setup/TomorrowOS.Setup.csproj",
    "-c",
    "Release",
    "-r",
    "win-x64",
    "--self-contained",
    "true",
    "-o",
    setupOut
  ],
  HOST
);

// Payload = full Player output + Watchdog exe only.
copyDir(playerOut, PAYLOAD);
const watchdogExe = path.join(watchdogOut, "TomorrowOS.Watchdog.exe");
if (!fs.existsSync(watchdogExe)) {
  console.error("Watchdog exe missing after publish:", watchdogExe);
  process.exit(1);
}
fs.copyFileSync(watchdogExe, path.join(PAYLOAD, "TomorrowOS.Watchdog.exe"));

const hardeningSrc = path.join(ROOT, "hardening");
copyDir(hardeningSrc, path.join(PAYLOAD, "hardening"));

// Bundle payload next to Setup.exe for interactive/silent installers
copyDir(PAYLOAD, path.join(setupOut, "payload"));
copyDir(hardeningSrc, path.join(setupOut, "hardening"));

const setupExe = path.join(setupOut, "TomorrowOS-Windows-Setup.exe");
if (fs.existsSync(setupExe)) {
  // Keep a convenience copy at the build root (run from setup/ for full deps + UI).
  fs.copyFileSync(setupExe, path.join(OUT, "TomorrowOS-Windows-Setup.exe"));
}

// Sanity check: Player WPF assembly must remain the full build, not a stub.
const windowsBase = path.join(PAYLOAD, "WindowsBase.dll");
const wbSize = fs.existsSync(windowsBase) ? fs.statSync(windowsBase).size : 0;
if (wbSize < 500_000) {
  console.error(
    `WindowsBase.dll looks wrong in payload (${wbSize} bytes). Player deps may be corrupted.`
  );
  process.exit(1);
}

const installerUi = path.join(setupOut, "wwwroot", "installer.html");
if (!fs.existsSync(installerUi)) {
  console.error("Setup UI missing after publish:", installerUi);
  process.exit(1);
}

console.log(`Build complete: ${OUT}`);
console.log(`WindowsBase.dll: ${(wbSize / 1024).toFixed(0)} KB`);
console.log("Interactive: build/windows/setup/TomorrowOS-Windows-Setup.exe");
console.log("Run player: build/windows/payload/TomorrowOS.Player.exe");
console.log(
  'Silent: TomorrowOS-Windows-Setup.exe /silent /cms "https://cms" /passcode "secret" /orientation landscape'
);
