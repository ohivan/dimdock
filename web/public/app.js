const CONFIG = {
  repo: "ohivan/dimdock",
  wingetId: "ohivan.DimDock",
  fallbackVersion: "v1.0.0",
};

function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value));
}

function initCopyButtons() {
  const cmdText = `winget install ${CONFIG.wingetId}`;
  document.querySelectorAll("[data-winget-cmd]").forEach((el) => {
    el.textContent = cmdText;
  });

  document.querySelectorAll("[data-copy-winget]").forEach((btn) => {
    let resetTimer = null;
    btn.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(cmdText);
      } catch {
        const temp = document.createElement("textarea");
        temp.value = cmdText;
        temp.style.position = "fixed";
        temp.style.opacity = "0";
        document.body.appendChild(temp);
        temp.select();
        document.execCommand("copy");
        temp.remove();
      }

      const label = btn.querySelector(".copy-label");
      btn.classList.add("is-copied");
      if (label) label.textContent = "Copied";

      clearTimeout(resetTimer);
      resetTimer = setTimeout(() => {
        btn.classList.remove("is-copied");
        if (label) label.textContent = "Copy";
      }, 1800);
    });
  });
}

async function hydrateLatestRelease() {
  try {
    const res = await fetch(`https://api.github.com/repos/${CONFIG.repo}/releases/latest`, {
      headers: { Accept: "application/vnd.github+json" },
    });
    if (!res.ok) return;

    const release = await res.json();
    const version = release.tag_name || CONFIG.fallbackVersion;
    const assets = Array.isArray(release.assets) ? release.assets : [];
    const preferredAsset =
      assets.find((a) => /^DimDock-win-Setup\.exe$/i.test(a.name)) ||
      assets.find((a) => /setup\.exe$/i.test(a.name)) ||
      assets.find((a) => /\.(exe|msi|msix)$/i.test(a.name));

    document.querySelectorAll("[data-version-badge]").forEach((el) => {
      el.textContent = version;
    });

    if (preferredAsset?.browser_download_url) {
      document.querySelectorAll("[data-download-link]").forEach((link) => {
        link.setAttribute("href", preferredAsset.browser_download_url);
      });
    } else if (release.html_url) {
      document.querySelectorAll("[data-download-link]").forEach((link) => {
        link.setAttribute("href", release.html_url);
      });
    }
  } catch {
    // Keep static fallback links when offline or before first release is published.
  }
}

function initPixelDitherSurfaces() {
  const containers = document.querySelectorAll("[data-pixel-surface]");
  if (!containers.length) return;

  const reducedMotion = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
  const bayer4 = [
    0, 8, 2, 10,
    12, 4, 14, 6,
    3, 11, 1, 9,
    15, 7, 13, 5,
  ];
  const cellSize = 7;

  containers.forEach((container, surfaceIndex) => {
    const canvas = document.createElement("canvas");
    canvas.className = "pixel-dither-canvas";
    canvas.setAttribute("aria-hidden", "true");
    container.prepend(canvas);

    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    let cols = 0;
    let rows = 0;
    let imgData = null;
    let isVisible = true;
    let pointerX = -999;
    let pointerY = -999;
    let hoverStrength = 0;
    let hovering = false;

    const resize = () => {
      const rect = container.getBoundingClientRect();
      cols = Math.max(1, Math.ceil(rect.width / cellSize));
      rows = Math.max(1, Math.ceil(rect.height / cellSize));
      canvas.width = cols;
      canvas.height = rows;
      imgData = ctx.createImageData(cols, rows);
    };

    const drawFrame = (timeSec) => {
      if (!imgData) return;
      const data = imgData.data;
      hoverStrength += ((hovering ? 1 : 0) - hoverStrength) * 0.14;

      const phase = timeSec * 0.45 + surfaceIndex * 2.1;

      for (let y = 0; y < rows; y++) {
        const ny = y * 0.11;
        const bayerRow = (y & 3) * 4;
        for (let x = 0; x < cols; x++) {
          const nx = x * 0.085;
          const wave =
            0.5 +
            0.26 * Math.sin(nx * 1.15 + phase + Math.cos(ny * 0.9 - phase * 0.6)) +
            0.24 * Math.cos(ny * 1.35 - phase * 0.8 + nx * 0.5);

          let glow = 0;
          if (hoverStrength > 0.01) {
            const dx = x - pointerX;
            const dy = y - pointerY;
            const distSq = dx * dx + dy * dy;
            glow = Math.exp(-distSq / 320) * hoverStrength;
          }

          const intensity = wave * 0.56 + glow * 0.52;
          const threshold = (bayer4[bayerRow + (x & 3)] + 0.5) / 16;
          const idx = (y * cols + x) * 4;

          if (intensity > threshold) {
            const mix = clamp(glow * 1.4, 0, 1);
            // Blend from subtle slate-blue pixel grain to DimDock sky-blue (#4cc2ff) near hover
            data[idx] = Math.round(92 + (76 - 92) * mix);
            data[idx + 1] = Math.round(132 + (194 - 132) * mix);
            data[idx + 2] = Math.round(168 + (255 - 168) * mix);
            data[idx + 3] = Math.round(28 + mix * 54);
          } else {
            data[idx + 3] = 0;
          }
        }
      }

      ctx.putImageData(imgData, 0, 0);
    };

    resize();
    drawFrame(0);

    if (typeof ResizeObserver !== "undefined") {
      new ResizeObserver(() => {
        resize();
        drawFrame(performance.now() * 0.001);
      }).observe(container);
    }

    if (typeof IntersectionObserver !== "undefined") {
      new IntersectionObserver(
        (entries) => {
          isVisible = entries[0]?.isIntersecting ?? true;
        },
        { threshold: 0.01 }
      ).observe(container);
    }

    container.addEventListener("pointermove", (event) => {
      const rect = container.getBoundingClientRect();
      pointerX = (event.clientX - rect.left) / cellSize;
      pointerY = (event.clientY - rect.top) / cellSize;
      hovering = true;
    });

    container.addEventListener("pointerleave", () => {
      hovering = false;
    });

    if (!reducedMotion) {
      let lastTick = 0;
      const tick = (now) => {
        if (isVisible && now - lastTick >= 55) {
          lastTick = now;
          drawFrame(now * 0.001);
        }
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    }
  });
}

/* Compact 1:1 pixel-art circular-arc fade that multiplies into the live GIF animation and Bayer-dithers into #0b1622 */
function initHeroPixelFade() {
  const heroMedia = document.getElementById("hero-media");
  if (!heroMedia) return;

  const ART_W = 500;
  const ART_H = 288;
  const BG_R = 11;
  const BG_G = 22;
  const BG_B = 34; // #0b1622 exact site base

  // 6-step cool river-twilight multiply palette (preserves 100% of live GIF water motion underneath)
  const TINT_STEPS = [
    [224, 236, 246], // subtle cool shade
    [176, 198, 218], // soft twilight blue
    [126, 154, 180], // mid river slate
    [82, 112, 140],  // deep steel blue
    [46, 72, 98],    // dark river navy
    [22, 42, 62],    // near-night base
  ];

  // 4x4 ordered Bayer matrix for crisp pixel-art dithering
  const bayer4 = [
    0, 8, 2, 10,
    12, 4, 14, 6,
    3, 11, 1, 9,
    15, 7, 13, 5,
  ];

  const hash1 = (n) => {
    const x = Math.sin(n * 127.1 + 311.7) * 43758.5453123;
    return x - Math.floor(x);
  };

  const smoothNoise1D = (x, seed) => {
    const i = Math.floor(x);
    const f = x - i;
    const u = f * f * (3 - 2 * f);
    return hash1(i + seed * 97.3) * (1 - u) + hash1(i + 1 + seed * 97.3) * u;
  };

  // Layer 1: Multiply tint canvas (darkens live GIF pixels in crisp Bayer-dithered steps)
  const tintCanvas = document.createElement("canvas");
  tintCanvas.className = "hero-water-tint";
  tintCanvas.width = ART_W;
  tintCanvas.height = ART_H;
  tintCanvas.setAttribute("aria-hidden", "true");
  heroMedia.appendChild(tintCanvas);
  const tintCtx = tintCanvas.getContext("2d");

  // Layer 2: Bayer-dithered #0b1622 settle canvas (completes seamless blend into site background)
  const blendCanvas = document.createElement("canvas");
  blendCanvas.className = "hero-water-blend";
  blendCanvas.width = ART_W;
  blendCanvas.height = ART_H;
  blendCanvas.setAttribute("aria-hidden", "true");
  heroMedia.appendChild(blendCanvas);
  const blendCtx = blendCanvas.getContext("2d");

  if (!tintCtx || !blendCtx) return;

  let sampledLum = null;
  let visLeftCol = 0;
  let visRightCol = ART_W - 1;
  let bottomRow = ART_H - 1;
  let bandRows = 16;
  let arcDepth = 8;
  let visibleStartRow = 230;
  let isVisible = true;
  const reducedMotion = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;

  const syncLayout = () => {
    const rect = heroMedia.getBoundingClientRect();
    const w = Math.max(1, Math.round(rect.width));
    const h = Math.max(1, Math.round(rect.height));

    const artPx = Math.max(w / ART_W, h / ART_H);
    const renderedW = ART_W * artPx;
    const renderedH = ART_H * artPx;
    const offsetX = (w - renderedW) * 0.5;
    const offsetY = (h - renderedH) * 0.5;

    for (const c of [tintCanvas, blendCanvas]) {
      c.style.left = `${offsetX}px`;
      c.style.top = `${offsetY}px`;
      c.style.width = `${renderedW}px`;
      c.style.height = `${renderedH}px`;
    }

    visLeftCol = clamp(Math.floor(-offsetX / artPx), 0, ART_W - 1);
    visRightCol = clamp(Math.ceil((w - offsetX) / artPx), 0, ART_W - 1);
    // Compact bottom transition height (~7.5% band + ~3.5% circular arc) with guaranteed solid floor
    bottomRow = clamp(Math.floor((h - offsetY) / artPx), 16, ART_H - 1);
    bandRows = Math.max(10, Math.round((h * 0.075) / artPx));
    arcDepth = Math.max(5, Math.round((h * 0.035) / artPx));
    visibleStartRow = Math.max(0, bottomRow - bandRows - arcDepth - 6);
  };

  const drawFrame = (timeSec) => {
    const rStart = clamp(visibleStartRow, 0, ART_H - 1);
    const bandH = ART_H - rStart;
    if (bandH <= 0) return;

    tintCtx.clearRect(0, 0, ART_W, ART_H);
    blendCtx.clearRect(0, 0, ART_W, ART_H);

    const tintImg = tintCtx.createImageData(ART_W, bandH);
    const blendImg = blendCtx.createImageData(ART_W, bandH);
    const tOut = tintImg.data;
    const bOut = blendImg.data;

    const t = reducedMotion ? 0 : timeSec;
    const midCol = (visLeftCol + visRightCol) * 0.5;
    const halfSpan = Math.max(1, (visRightCol - visLeftCol) * 0.5);
    const solidFloorRow = bottomRow - 2;

    // Gentle circular arc falloff across the visible viewport width
    const colBaseEdge = new Float32Array(ART_W);
    for (let col = 0; col < ART_W; col++) {
      const u = clamp((col - midCol) / halfSpan, -1, 1);
      const circularLift = (1 - Math.sqrt(Math.max(0, 1 - u * u * 0.72))) * arcDepth;
      colBaseEdge[col] = solidFloorRow - bandRows - circularLift;
    }

    const numTintSteps = TINT_STEPS.length;

    for (let row = rStart; row < ART_H; row++) {
      const rowLocal = row - rStart;

      // Solid #0b1622 floor so the bottom never clips
      if (row >= solidFloorRow) {
        for (let col = 0; col < ART_W; col++) {
          const idx = (rowLocal * ART_W + col) * 4;
          bOut[idx] = BG_R;
          bOut[idx + 1] = BG_G;
          bOut[idx + 2] = BG_B;
          bOut[idx + 3] = 255;
        }
        continue;
      }

      const bayerRow = (row & 3) * 4;
      const dir = (row & 1) === 0 ? 1 : -1;
      const rowDrift = t * (0.36 + hash1(row * 3.7) * 0.28) * dir;
      const streakScale = 0.06 + hash1(row * 5.3) * 0.04;

      for (let col = 0; col < ART_W; col++) {
        const progress = (row - colBaseEdge[col]) / bandRows;
        if (progress < -0.25) continue;

        const idx = (rowLocal * ART_W + col) * 4;
        const bayerNorm = (bayer4[bayerRow + (col & 3)] + 0.5) / 16; // 0..1

        // Horizontal ripple streak + water luminance interaction
        const streak =
          smoothNoise1D(col * streakScale + rowDrift, row) * 0.65 +
          smoothNoise1D(col * 0.16 - rowDrift * 1.25, row + 41) * 0.35;
        const lum = sampledLum ? sampledLum[row * ART_W + col] : 0.48;
        const lumHold = (lum - 0.48) * 0.18;

        const field = progress + (streak - 0.5) * 0.28 - lumHold;

        // Layer 1 (Multiply): Bayer-dithered 6-step darkening directly over the live animated GIF
        const tintPos = field * numTintSteps;
        const stepIdx = Math.floor(tintPos + bayerNorm - 0.5);
        if (stepIdx >= 0) {
          const clampedStep = clamp(stepIdx, 0, numTintSteps - 1);
          const color = TINT_STEPS[clampedStep];
          tOut[idx] = color[0];
          tOut[idx + 1] = color[1];
          tOut[idx + 2] = color[2];
          tOut[idx + 3] = 255;
        }

        // Layer 2 (Normal): Crisp Bayer-dithered scatter of #0b1622 in the lower half of the band
        const settleT = clamp((field - 0.42) / 0.56, 0, 1);
        if (settleT > bayerNorm) {
          bOut[idx] = BG_R;
          bOut[idx + 1] = BG_G;
          bOut[idx + 2] = BG_B;
          bOut[idx + 3] = 255;
        }
      }
    }

    tintCtx.putImageData(tintImg, 0, rStart);
    blendCtx.putImageData(blendImg, 0, rStart);
  };

  // Sample hero-bg.gif onto a 500x288 offscreen canvas for water luminance interaction
  const sourceImg = new Image();
  sourceImg.onload = () => {
    const sampleCanvas = document.createElement("canvas");
    sampleCanvas.width = ART_W;
    sampleCanvas.height = ART_H;
    const sCtx = sampleCanvas.getContext("2d", { willReadFrequently: true });
    if (!sCtx) return;

    sCtx.imageSmoothingEnabled = false;
    sCtx.drawImage(sourceImg, 0, 0, ART_W, ART_H);
    const data = sCtx.getImageData(0, 0, ART_W, ART_H).data;
    sampledLum = new Float32Array(ART_W * ART_H);
    for (let i = 0, p = 0; i < data.length; i += 4, p++) {
      sampledLum[p] = (0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2]) / 255;
    }
    drawFrame(performance.now() * 0.001);
  };
  sourceImg.src = "./hero-bg.gif";

  syncLayout();
  drawFrame(0);

  if (typeof IntersectionObserver !== "undefined") {
    new IntersectionObserver(
      (entries) => {
        isVisible = entries[0]?.isIntersecting ?? true;
      },
      { threshold: 0.01 }
    ).observe(heroMedia);
  }

  if (!reducedMotion) {
    let lastTick = 0;
    const tick = (now) => {
      if (isVisible && now - lastTick >= 75) {
        lastTick = now;
        drawFrame(now * 0.001);
      }
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  }

  let resizeTimer = null;
  const scheduleSync = () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => {
      syncLayout();
      drawFrame(performance.now() * 0.001);
    }, 60);
  };

  if (typeof ResizeObserver !== "undefined") {
    new ResizeObserver(scheduleSync).observe(heroMedia);
  } else {
    window.addEventListener("resize", scheduleSync, { passive: true });
  }
}

function initHeroFlyoutPanel() {
  const hero = document.querySelector(".hero");
  const flyout = document.getElementById("hero-flyout");
  if (!hero || !flyout) return;

  // Interactive 3-monitor state for the hero WinUI 3 flyout
  const heroState = {
    monitors: [
      { id: "hero-mon-1", brightness: 82 },
      { id: "hero-mon-2", brightness: 64 },
      { id: "hero-mon-3", brightness: 48 },
    ],
    linked: false,
  };

  const syncHeroSliders = () => {
    for (const monitor of heroState.monitors) {
      const input = document.querySelector(`[data-hero-slider-input="${monitor.id}"]`);
      const fill = document.querySelector(`[data-hero-slider-fill="${monitor.id}"]`);
      const thumb = document.querySelector(`[data-hero-slider-thumb="${monitor.id}"]`);
      const valueEl = document.querySelector(`[data-hero-monitor-value="${monitor.id}"]`);

      if (input) input.value = String(monitor.brightness);
      if (fill) fill.style.width = `${monitor.brightness}%`;
      if (thumb) thumb.style.left = `${monitor.brightness}%`;
      if (valueEl) valueEl.textContent = String(monitor.brightness);
    }

    const linkBtn = document.getElementById("hero-link-btn");
    if (linkBtn) {
      linkBtn.setAttribute("aria-pressed", String(heroState.linked));
    }
  };

  const setHeroBrightness = (id, nextValue) => {
    const clamped = clamp(Math.round(nextValue), 0, 100);
    if (heroState.linked) {
      for (const monitor of heroState.monitors) {
        monitor.brightness = clamped;
      }
    } else {
      const target = heroState.monitors.find((m) => m.id === id);
      if (target) target.brightness = clamped;
    }
    syncHeroSliders();
  };

  for (const monitor of heroState.monitors) {
    const input = document.querySelector(`[data-hero-slider-input="${monitor.id}"]`);
    const wrap = document.querySelector(`[data-hero-slider-wrap="${monitor.id}"]`);
    const row = document.querySelector(`[data-hero-monitor-id="${monitor.id}"]`);

    if (input) {
      input.addEventListener("input", () => {
        setHeroBrightness(monitor.id, Number(input.value));
      });
      input.addEventListener("pointerdown", () => wrap?.classList.add("is-active"));
      input.addEventListener("pointerup", () => wrap?.classList.remove("is-active"));
      input.addEventListener("pointercancel", () => wrap?.classList.remove("is-active"));
      input.addEventListener("blur", () => wrap?.classList.remove("is-active"));
    }

    if (row) {
      row.addEventListener(
        "wheel",
        (event) => {
          event.preventDefault();
          const notches = event.deltaY < 0 ? 1 : -1;
          setHeroBrightness(monitor.id, monitor.brightness + notches * 5);
        },
        { passive: false }
      );
    }
  }

  document.getElementById("hero-link-btn")?.addEventListener("click", () => {
    heroState.linked = !heroState.linked;
    if (heroState.linked) {
      const avg = Math.round(
        heroState.monitors.reduce((sum, m) => sum + m.brightness, 0) / heroState.monitors.length
      );
      for (const monitor of heroState.monitors) {
        monitor.brightness = avg;
      }
    }
    syncHeroSliders();
  });

  syncHeroSliders();

  // Subtle 3D perspective angled toward the hero text + smooth mouse-follow tilt
  const reducedMotion = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
  const compactMedia = window.matchMedia?.("(max-width: 920px)");
  if (reducedMotion) return;

  const getBaseTilt = () => (compactMedia?.matches ? { rx: 0, ry: 0 } : { rx: 2.2, ry: -7.5 });

  let base = getBaseTilt();
  let targetRx = base.rx;
  let targetRy = base.ry;
  let currentRx = base.rx;
  let currentRy = base.ry;
  let targetSheenX = 24;
  let targetSheenY = 18;
  let currentSheenX = 24;
  let currentSheenY = 18;
  let rafId = 0;

  const applyTransform = () => {
    const shadowX = clamp(-currentRy * 2.1, -26, 26);
    const shadowY = clamp(24 + currentRx * 1.4, 14, 38);
    flyout.style.setProperty("--hero-rx", `${currentRx.toFixed(3)}deg`);
    flyout.style.setProperty("--hero-ry", `${currentRy.toFixed(3)}deg`);
    flyout.style.setProperty("--hero-sheen-x", `${currentSheenX.toFixed(1)}%`);
    flyout.style.setProperty("--hero-sheen-y", `${currentSheenY.toFixed(1)}%`);
    flyout.style.setProperty("--hero-shadow-x", `${shadowX.toFixed(1)}px`);
    flyout.style.setProperty("--hero-shadow-y", `${shadowY.toFixed(1)}px`);
  };

  const step = () => {
    const ease = 0.085;
    currentRx += (targetRx - currentRx) * ease;
    currentRy += (targetRy - currentRy) * ease;
    currentSheenX += (targetSheenX - currentSheenX) * ease;
    currentSheenY += (targetSheenY - currentSheenY) * ease;
    applyTransform();

    const settled =
      Math.abs(targetRx - currentRx) < 0.008 &&
      Math.abs(targetRy - currentRy) < 0.008 &&
      Math.abs(targetSheenX - currentSheenX) < 0.05 &&
      Math.abs(targetSheenY - currentSheenY) < 0.05;

    if (!settled) {
      rafId = requestAnimationFrame(step);
    } else {
      currentRx = targetRx;
      currentRy = targetRy;
      currentSheenX = targetSheenX;
      currentSheenY = targetSheenY;
      applyTransform();
      rafId = 0;
    }
  };

  const kick = () => {
    if (!rafId) rafId = requestAnimationFrame(step);
  };

  hero.addEventListener("pointermove", (event) => {
    if (event.pointerType === "touch") return;
    base = getBaseTilt();
    const rect = hero.getBoundingClientRect();
    const nx = clamp(((event.clientX - rect.left) / rect.width - 0.5) * 2, -1, 1);
    const ny = clamp(((event.clientY - rect.top) / rect.height - 0.5) * 2, -1, 1);

    const maxYaw = compactMedia?.matches ? 3.5 : 5.2;
    const maxPitch = compactMedia?.matches ? 3.0 : 4.0;

    targetRy = base.ry + nx * maxYaw;
    targetRx = base.rx - ny * maxPitch;
    targetSheenX = 50 + nx * 18;
    targetSheenY = 50 + ny * 18;
    kick();
  });

  hero.addEventListener("pointerleave", () => {
    base = getBaseTilt();
    targetRx = base.rx;
    targetRy = base.ry;
    targetSheenX = 32;
    targetSheenY = 22;
    kick();
  });

  compactMedia?.addEventListener?.("change", () => {
    base = getBaseTilt();
    targetRx = base.rx;
    targetRy = base.ry;
    kick();
  });

  applyTransform();
}

initHeroPixelFade();
initHeroFlyoutPanel();
initCopyButtons();
initPixelDitherSurfaces();
hydrateLatestRelease();
