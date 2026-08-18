/**
 * Brand + status binding for quiet-state-*.html (iframe overlay; playback is separate).
 */
(function () {
  let clockTimer = null;

  function clampByte(n) {
    return Math.max(0, Math.min(255, Math.round(n)));
  }

  function parseHexColor(hex) {
    const s = String(hex || "").trim();
    const m = s.match(/^#?([0-9a-f]{6})$/i);
    if (!m) return null;
    const n = parseInt(m[1], 16);
    return {
      r: (n >> 16) & 255,
      g: (n >> 8) & 255,
      b: n & 255
    };
  }

  function mixRgb(a, b, t) {
    return {
      r: clampByte(a.r + (b.r - a.r) * t),
      g: clampByte(a.g + (b.g - a.g) * t),
      b: clampByte(a.b + (b.b - a.b) * t)
    };
  }

  function rgbToCss(c) {
    return `rgb(${c.r}, ${c.g}, ${c.b})`;
  }

  function derivePalette(brand) {
    const bg = parseHexColor(brand?.backgroundColor) || { r: 5, g: 5, b: 5 };
    const text = parseHexColor(brand?.textColor) || { r: 255, g: 255, b: 255 };
    const dim = mixRgb(text, bg, 0.45);
    const dimmer = mixRgb(text, bg, 0.62);
    const label = mixRgb(text, bg, 0.28);
    return { bg, text, dim, dimmer, label };
  }

  function applyCssVars(palette) {
    const root = document.documentElement;
    root.style.setProperty("--bg", rgbToCss(palette.bg));
    root.style.setProperty("--text", rgbToCss(palette.text));
    root.style.setProperty("--text-dim", rgbToCss(palette.dim));
    root.style.setProperty("--text-dimmer", rgbToCss(palette.dimmer));
    root.style.setProperty("--text-label", rgbToCss(palette.label));
    document.body.style.background = rgbToCss(palette.bg);
    document.body.style.color = rgbToCss(palette.text);
  }

  function resolveLogoUrl(brand, httpBase) {
    if (!brand?.logoPath || !httpBase) return "";
    const path = String(brand.logoPath).replace(/^\.\//, "");
    return `${String(httpBase).replace(/\/$/, "")}/${path}`;
  }

  function setHeroMarkLogo(markEl, brand, httpBase) {
    if (!markEl) return;
    markEl.classList.remove("pairing-code", "has-logo");
    const url = resolveLogoUrl(brand, httpBase);
    markEl.innerHTML = "";
    if (url) {
      markEl.classList.add("has-logo");
      const img = document.createElement("img");
      img.src = url;
      img.alt = brand?.name || "Logo";
      img.className = "hero-mark-img";
      markEl.appendChild(img);
      return;
    }
    const initial =
      String(brand?.name || "T").trim().charAt(0).toUpperCase() || "T";
    markEl.textContent = initial;
  }



  function updateClock() {
    const now = new Date();
    const hh = String(now.getHours()).padStart(2, "0");
    const mm = String(now.getMinutes()).padStart(2, "0");
    const timeEl = document.getElementById("clockTime");
    if (timeEl) timeEl.textContent = `${hh}:${mm}`;
  }

  function setStatus(primary, secondary) {
    const em = document.querySelector(".status-text .em");
    const dim = document.querySelector(".status-text .dim");
    if (em) em.textContent = primary || "Connected";
    if (dim) dim.textContent = secondary || "";
  }

  function markBrandReady() {
    document.documentElement.classList.add("qs-ready");
  }

  window.TomorrowQuietState = {
    applyBrand(brand, httpBase) {
      const palette = derivePalette(brand);
      applyCssVars(palette);
      const nameEl = document.querySelector(".hero-name");
      const tagEl = document.querySelector(".hero-tagline");
      const markEl = document.querySelector(".hero-mark");
      const brandName = brand?.name || "TomorrowOS";
      if (nameEl) {
        nameEl.textContent = brandName;
        const primary = parseHexColor(brand?.primaryColor);
        nameEl.style.color = primary ? rgbToCss(primary) : rgbToCss(palette.text);
      }
      if (tagEl) {
        tagEl.textContent =
          brand?.tagline ||
          brand?.activationScreen?.headline ||
          "This screen is ready.";
      }
      setHeroMarkLogo(markEl, brand, httpBase);
      if (brand?.fontFamily) {
        document.body.style.fontFamily = `'${brand.fontFamily}', 'Inter Tight', system-ui, sans-serif`;
      }
      document.querySelectorAll(".diag-row").forEach((row) => {
        const key = row.querySelector(".diag-key");
        if (key && key.textContent.trim() === "Network") {
          const val = row.querySelector(".diag-val");
          if (val) val.textContent = brandName;
        }
      });
      markBrandReady();
    },

    setStatus,

    startClock() {
      if (clockTimer) return;
      updateClock();
      clockTimer = setInterval(updateClock, 1000);
    },

    stopClock() {
      if (clockTimer) {
        clearInterval(clockTimer);
        clockTimer = null;
      }
    }
  };
})();
