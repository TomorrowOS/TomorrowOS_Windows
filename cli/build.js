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

function copyDir(src, dest, skipExt = []) {
  fs.mkdirSync(dest, { recursive: true });
  const skip = new Set(skipExt.map((e) => e.toLowerCase()));
  for (const entry of fs.readdirSync(src, { withFileTypes: true })) {
    const from = path.join(src, entry.name);
    const to = path.join(dest, entry.name);
    if (entry.isDirectory()) {
      copyDir(from, to, skipExt);
      continue;
    }
    const ext = path.extname(entry.name).toLowerCase();
    if (skip.has(ext)) continue;
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
const uninstallOut = path.join(OUT, "uninstall");
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

// Payload = full Player output + Watchdog exe only. Must exist BEFORE Setup publish
// so it can be packed into the single-file installer.
copyDir(playerOut, PAYLOAD, [".pdb"]);
const watchdogExe = path.join(watchdogOut, "TomorrowOS.Watchdog.exe");
if (!fs.existsSync(watchdogExe)) {
  console.error("Watchdog exe missing after publish:", watchdogExe);
  process.exit(1);
}
fs.copyFileSync(watchdogExe, path.join(PAYLOAD, "TomorrowOS.Watchdog.exe"));

run(
  "dotnet",
  [
    "publish",
    "TomorrowOS.Uninstall/TomorrowOS.Uninstall.csproj",
    "-c",
    "Release",
    "-r",
    "win-x64",
    "--self-contained",
    "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-o",
    uninstallOut
  ],
  HOST
);

const uninstallExe = path.join(uninstallOut, "TomorrowOS.Uninstall.exe");
if (!fs.existsSync(uninstallExe)) {
  console.error("Uninstall exe missing after publish:", uninstallExe);
  process.exit(1);
}
fs.copyFileSync(uninstallExe, path.join(PAYLOAD, "TomorrowOS.Uninstall.exe"));

const hardeningSrc = path.join(ROOT, "hardening");
copyDir(hardeningSrc, path.join(PAYLOAD, "hardening"));

const setupBundle = path.join(HOST, "TomorrowOS.Setup", "bundle", "payload");
rimrafBestEffort(setupBundle);
copyDir(PAYLOAD, setupBundle, [".pdb"]);

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
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:IncludeAllContentForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-o",
    setupOut
  ],
  HOST
);

const packedSetup = path.join(setupOut, "TomorrowOS-Windows-Setup.exe");
if (!fs.existsSync(packedSetup)) {
  console.error("Single-file Setup.exe missing after publish:", packedSetup);
  process.exit(1);
}

// Shareable artifact: one exe at the build root.
const shareableSetup = path.join(OUT, "TomorrowOS-Windows-Setup.exe");
fs.copyFileSync(packedSetup, shareableSetup);

// Sanity check: Player WPF assembly must remain the full build, not a stub.
const windowsBase = path.join(PAYLOAD, "WindowsBase.dll");
const wbSize = fs.existsSync(windowsBase) ? fs.statSync(windowsBase).size : 0;
if (wbSize < 500_000) {
  console.error(
    `WindowsBase.dll looks wrong in payload (${wbSize} bytes). Player deps may be corrupted.`
  );
  process.exit(1);
}

const bundledPlayer = path.join(setupBundle, "TomorrowOS.Player.exe");
if (!fs.existsSync(bundledPlayer)) {
  console.error("Setup bundle is missing TomorrowOS.Player.exe:", bundledPlayer);
  process.exit(1);
}

const packedSize = fs.statSync(packedSetup).size;
if (packedSize < 20_000_000) {
  console.error(
    `Packed Setup.exe is too small (${(packedSize / 1024 / 1024).toFixed(1)} MB). Payload may not have been embedded.`
  );
  process.exit(1);
}

console.log(`Build complete: ${OUT}`);
console.log(`WindowsBase.dll: ${(wbSize / 1024).toFixed(0)} KB`);
console.log(`Share this file: ${shareableSetup}`);
console.log(`Packed Setup size: ${(packedSize / 1024 / 1024).toFixed(1)} MB`);
console.log("Run player (dev): build/windows/payload/TomorrowOS.Player.exe");
console.log(
  'Silent: build\\windows\\TomorrowOS-Windows-Setup.exe /silent /cms "https://cms" /passcode "secret" /orientation landscape'
);
