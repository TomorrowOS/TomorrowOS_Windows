/**
 * Windows platform layer for TomorrowOS player.
 * Talks to the .NET WebView2 host via chrome.webview postMessage.
 * Exposes the same window.TomorrowPlatform surface as BrightSign.
 */
try {
(function () {
  const PLATFORM_ID = "windows";
  const pending = {};
  let nextRequestId = 1;
  let nextDownloadId = 1;
  const activeDownloadIds = {};
  let hostReady = false;
  let storageRoot = "";
  let cacheVirtualHost = "https://tomorrowos.cache/";

  function setStaticBootStatus(message) {
    const el = document.getElementById("staticBootStatus");
    if (el) el.textContent = String(message || "");
  }

  setStaticBootStatus("Connecting to Windows host...");

  function hasHostBridge() {
    return !!(window.chrome && chrome.webview && typeof chrome.webview.postMessage === "function");
  }

  function invoke(method, params) {
    if (!hasHostBridge()) {
      return Promise.reject(new Error("WebView2 host bridge unavailable"));
    }

    const id = String(nextRequestId++);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        delete pending[id];
        reject(new Error(`Host call timed out: ${method}`));
      }, 120000);

      pending[id] = {
        resolve: (value) => {
          clearTimeout(timer);
          resolve(value);
        },
        reject: (err) => {
          clearTimeout(timer);
          reject(err);
        }
      };

      chrome.webview.postMessage(
        JSON.stringify({
          id,
          method,
          params: params || {}
        })
      );
    });
  }

  function onHostMessage(raw) {
    let data = raw;
    if (typeof raw === "string") {
      try {
        data = JSON.parse(raw);
      } catch (_) {
        return;
      }
    }
    if (!data || typeof data !== "object") return;

    if (data.type === "host.ready") {
      hostReady = true;
      storageRoot = data.storageRoot || storageRoot;
      cacheVirtualHost = data.cacheVirtualHost || cacheVirtualHost;
      setStaticBootStatus("Windows host ready");
      return;
    }

    if (data.type === "host.event" && data.event === "heartbeat.ack") {
      return;
    }

    const id = data.id != null ? String(data.id) : "";
    const waiter = pending[id];
    if (!waiter) return;
    delete pending[id];

    if (data.ok === false) {
      waiter.reject(new Error(data.error || "Host call failed"));
      return;
    }
    waiter.resolve(data.result);
  }

  if (hasHostBridge()) {
    try {
      chrome.webview.addEventListener("message", (ev) => onHostMessage(ev.data));
    } catch (_) {
      // Older WebView2 bindings
      window.chrome.webview.addEventListener("message", (ev) => onHostMessage(ev.data));
    }
  }

  function normalizeRelPath(input) {
    return String(input || "")
      .replace(/\\/g, "/")
      .replace(/^\/+/, "")
      .replace(/\/+$/, "");
  }

  function toAbsPath(relPath) {
    const rel = normalizeRelPath(relPath);
    if (!storageRoot) return rel;
    if (!rel) return storageRoot.replace(/\\/g, "/");
    return `${storageRoot.replace(/\\/g, "/")}/${rel}`;
  }

  function toFileUri(absPath) {
    const normalized = String(absPath || "").replace(/\\/g, "/");
    if (/^https?:\/\//i.test(normalized) || /^file:\/\//i.test(normalized)) {
      return normalized;
    }
    // Prefer virtual host mapping so media plays inside WebView2 securely.
    if (storageRoot) {
      const root = storageRoot.replace(/\\/g, "/").replace(/\/+$/, "");
      if (normalized.toLowerCase().startsWith(root.toLowerCase() + "/")) {
        const rel = normalized.slice(root.length + 1);
        return cacheVirtualHost.replace(/\/?$/, "/") + rel;
      }
    }
    if (/^[a-zA-Z]:\//.test(normalized)) {
      return "file:///" + normalized;
    }
    return "file://" + normalized;
  }

  function makeDirEntry(fullPathRel, isDirectory, absPath) {
    const name = String(fullPathRel || "").split("/").pop() || "";
    return {
      isDirectory: !!isDirectory,
      name,
      fullPath: normalizeRelPath(fullPathRel),
      toURI() {
        return toFileUri(absPath || toAbsPath(fullPathRel));
      },
      createDirectory(childName) {
        return invoke("fs.mkdir", { path: `${normalizeRelPath(fullPathRel)}/${childName}` });
      },
      listFiles(success, error) {
        invoke("fs.list", { path: normalizeRelPath(fullPathRel) })
          .then((children) => {
            const mapped = (children || []).map((child) =>
              makeDirEntry(child.fullPath, child.isDirectory, child.absPath)
            );
            if (typeof success === "function") success(mapped);
          })
          .catch((err) => {
            if (typeof error === "function") error(err);
          });
      }
    };
  }

  function filesystemResolve(pathInput, onsuccess, onerror, mode) {
    invoke("fs.resolve", { path: pathInput, mode: mode || "r" })
      .then((entry) => {
        if (!entry) {
          if (typeof onerror === "function") onerror(new Error(`Path not found: ${pathInput}`));
          return;
        }
        if (typeof onsuccess === "function") {
          onsuccess(makeDirEntry(entry.fullPath, entry.isDirectory, entry.absPath));
        }
      })
      .catch((err) => {
        if (typeof onerror === "function") onerror(err);
      });
  }

  function DownloadRequest(url, destination, fileName) {
    this.url = url;
    this.destination = destination;
    this.fileName = fileName;
  }

  function downloadStart(request, listener) {
    const id = String(nextDownloadId++);
    activeDownloadIds[id] = true;

    invoke("download.start", {
      id,
      url: request.url,
      destination: request.destination,
      fileName: request.fileName
    })
      .then((result) => {
        if (!activeDownloadIds[id]) {
          if (typeof listener?.oncanceled === "function") listener.oncanceled(id);
          return;
        }
        delete activeDownloadIds[id];
        if (typeof listener?.oncompleted === "function") {
          listener.oncompleted(id, result.fullPath);
        }
      })
      .catch((err) => {
        delete activeDownloadIds[id];
        if (typeof listener?.onfailed === "function") {
          listener.onfailed(id, { name: "DownloadError", message: err.message || String(err) });
        }
      });

    return id;
  }

  function extractArchive(pathInput, targetDir) {
    return invoke("archive.extract", {
      zipPath: pathInput,
      targetDir
    });
  }

  window.TomorrowPlatform = {
    id: PLATFORM_ID,
    getStorageRoot() {
      return storageRoot || "C:/ProgramData/TomorrowOS";
    },
    toAbsPath,
    toFileUri,
    statLocalFile(relPath) {
      // Synchronous API used by main.js — return cached-style sync via last known mapping.
      // Host fills virtual URIs; for sync callers we synthesise from relative path.
      const absPath = toAbsPath(relPath);
      return {
        absPath,
        size: null,
        fileUri: toFileUri(absPath)
      };
    },
    async captureDeviceScreenshot() {
      return invoke("device.captureScreenshot", {});
    },
    getDeviceSerialNumber() {
      return window.__tomorrowWindowsDeviceCache?.serialNumber || null;
    },
    getDeviceInfo() {
      return (
        window.__tomorrowWindowsDeviceCache || {
          online: navigator.onLine,
          deviceId: null,
          model: null,
          firmware: null,
          serialNumber: null
        }
      );
    },
    getBootUptimeSec() {
      const cached = window.__tomorrowWindowsDeviceCache?.bootUptimeSec;
      return Number.isFinite(cached) ? cached : 0;
    },
    rebootDevice() {
      void invoke("device.reboot", {});
    },
    canSetDisplayMute() {
      return true;
    },
    async setDisplayMuted(muted) {
      return invoke("display.setMuted", { muted: !!muted });
    },
    heartbeat() {
      // Fire-and-forget: never block the page on host reply (avoids HOST CALL TIMEOUT storms).
      try {
        if (!hasHostBridge()) return;
        chrome.webview.postMessage(
          JSON.stringify({
            id: String(nextRequestId++),
            method: "app.heartbeat",
            params: { ts: Date.now() }
          })
        );
      } catch (_) {}
    },
    async httpProbe(url) {
      const result = await invoke("http.probe", { url: String(url || "") });
      return !!(result && result.ok);
    },
    async httpGetJson(url) {
      const result = await invoke("http.getJson", { url: String(url || "") });
      return result && Object.prototype.hasOwnProperty.call(result, "json")
        ? result.json
        : result;
    },
    isRuntime() {
      return true;
    },
    DownloadRequest,
    filesystem: {
      resolve: filesystemResolve
    },
    download: {
      start: downloadStart,
      cancel(id) {
        delete activeDownloadIds[String(id)];
        void invoke("download.cancel", { id: String(id) });
      }
    },
    archive: {
      open(pathInput, mode, onsuccess, onerror) {
        if (mode !== "r") {
          if (typeof onerror === "function") {
            onerror({ name: "NotSupported", message: "Only read mode supported" });
          }
          return;
        }
        const archive = {
          extractAll(targetDir, onExtractSuccess, onExtractError) {
            extractArchive(pathInput, targetDir)
              .then(() => {
                if (typeof onExtractSuccess === "function") onExtractSuccess();
              })
              .catch((err) => {
                if (typeof onExtractError === "function") {
                  onExtractError({ name: "ExtractError", message: err.message });
                }
              });
          },
          close() {}
        };
        if (typeof onsuccess === "function") setTimeout(() => onsuccess(archive), 0);
      }
    }
  };

  // Warm device info + storage roots before main.js boots heavily.
  (async function bootstrapHost() {
    if (!hasHostBridge()) {
      setStaticBootStatus("WebView2 bridge missing — open via TomorrowOS.Player.exe");
      const showErr = window.__tomorrowShowBootError;
      if (typeof showErr === "function") {
        showErr("Windows host bridge not available. Launch TomorrowOS.Player.exe.");
      }
      return;
    }

    try {
      const ready = await invoke("host.getBootstrap", {});
      hostReady = true;
      storageRoot = ready.storageRoot || storageRoot;
      cacheVirtualHost = ready.cacheVirtualHost || cacheVirtualHost;
      window.__tomorrowWindowsDeviceCache = ready.deviceInfo || null;
      setStaticBootStatus("Platform ready. Loading app...");
      console.log("[TomorrowOS] Windows platform layer ready", {
        storageRoot,
        cacheVirtualHost,
        deviceInfo: !!ready.deviceInfo
      });

      // Keep watchdog happy while the player is alive.
      window.TomorrowPlatform.heartbeat();
      setInterval(() => {
        try {
          window.TomorrowPlatform.heartbeat();
        } catch (_) {}
      }, 5000);
    } catch (err) {
      console.error("[TomorrowOS] Windows host bootstrap failed:", err);
      setStaticBootStatus("Host bootstrap failed");
      const showErr = window.__tomorrowShowBootError;
      if (typeof showErr === "function") {
        showErr("Host bootstrap failed: " + (err && err.message ? err.message : err));
      }
    }
  })();
})();
} catch (bootErr) {
  console.error("[TomorrowOS] platform-windows failed:", bootErr);
  const showErr = window.__tomorrowShowBootError;
  if (typeof showErr === "function") {
    showErr("Platform init failed: " + (bootErr && bootErr.message ? bootErr.message : bootErr));
  }
}
