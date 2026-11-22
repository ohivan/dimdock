const CONFIG = {
  repo: "ohivan/dimdock",
  wingetId: "ohivan.DimDock",
  fallbackVersion: "v1.0.0",
};

const state = {
  monitors: [
    { id: "mon-1", name: 'LG UltraGear 27"', brightness: 75 },
    { id: "mon-2", name: "Dell U2723QE", brightness: 60 },
  ],
  linked: false,
  startupEnabled: true,
  scrollStep: 5,
  stepOptions: [1, 2, 5, 10],
  activeTab: "flyout",
  flyoutTheme: "dark",
};

function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value));
}

function getAverageBrightness() {
  const sum = state.monitors.reduce((acc, m) => acc + m.brightness, 0);
  return Math.round(sum / state.monitors.length);
}

/* Matches TrayIconService.CreateSunIcon geometry in src/DimDock/Services/TrayIconService.cs */
function renderDynamicSunIcon(percent) {
  const svg = document.getElementById("dynamic-sun-svg");
  if (!svg) return;

  const bucket = clamp(Math.round(percent / 5) * 5, 0, 100);
  const t = bucket / 100;
  const center = 8;
  const ringRadius = 2.1 + (3.0 - 2.1) * t;
  const rayInner = 4.1 + (5.0 - 4.1) * t;
  const rayOuter = 6.7 + (8.0 - 6.7) * t;

  let rays = "";
  for (let i = 0; i < 8; i++) {
    const angle = i * (Math.PI / 4);
    const x1 = (center + Math.cos(angle) * rayInner).toFixed(2);
    const y1 = (center + Math.sin(angle) * rayInner).toFixed(2);
    const x2 = (center + Math.cos(angle) * rayOuter).toFixed(2);
    const y2 = (center + Math.sin(angle) * rayOuter).toFixed(2);
    rays += `<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" />`;
  }

  svg.innerHTML = `<g stroke="currentColor" stroke-width="1.45" stroke-linecap="butt"><circle cx="8" cy="8" r="${ringRadius.toFixed(2)}" />${rays}</g>`;
}

function syncDemoUI() {
  for (const monitor of state.monitors) {
    const input = document.querySelector(`[data-slider-input="${monitor.id}"]`);
    const fill = document.querySelector(`[data-slider-fill="${monitor.id}"]`);
    const thumb = document.querySelector(`[data-slider-thumb="${monitor.id}"]`);
    const valueEl = document.querySelector(`[data-monitor-value="${monitor.id}"]`);

    if (input) input.value = String(monitor.brightness);
    if (fill) fill.style.width = `${monitor.brightness}%`;
    if (thumb) thumb.style.left = `${monitor.brightness}%`;
    if (valueEl) valueEl.textContent = String(monitor.brightness);
  }

  const avg = getAverageBrightness();
  renderDynamicSunIcon(avg);

  const stage = document.getElementById("desktop-stage");
  if (stage) {
    const minB = 0.28;
    const maxB = 1.12;
    const factor = minB + (maxB - minB) * (avg / 100);
    stage.style.setProperty("--stage-brightness", factor.toFixed(3));
  }

  const tooltip = document.getElementById("tray-tooltip");
  if (tooltip) {
    tooltip.innerHTML = state.monitors
      .map((m) => `${m.name}: ${m.brightness}%`)
      .join("<br />");
  }

  const linkBtn = document.getElementById("demo-link-btn");
  if (linkBtn) {
    linkBtn.setAttribute("aria-pressed", String(state.linked));
  }

  const menuLinkBtn = document.getElementById("menu-link-btn");
  const menuLinkSwitch = document.getElementById("menu-link-switch");
  if (menuLinkBtn) menuLinkBtn.setAttribute("aria-pressed", String(state.linked));
  if (menuLinkSwitch) menuLinkSwitch.classList.toggle("is-checked", state.linked);

  const stepVal = document.getElementById("menu-step-value");
  if (stepVal) stepVal.textContent = `${state.scrollStep}%`;
}

function setMonitorBrightness(id, nextValue) {
  const clamped = clamp(Math.round(nextValue), 0, 100);
  if (state.linked) {
    for (const monitor of state.monitors) {
      monitor.brightness = clamped;
    }
  } else {
    const target = state.monitors.find((m) => m.id === id);
    if (target) target.brightness = clamped;
  }
  syncDemoUI();
}

function stepAllMonitors(notches) {
  const delta = notches * state.scrollStep;
  if (state.linked) {
    const synced = clamp(getAverageBrightness() + delta, 0, 100);
    for (const monitor of state.monitors) {
      monitor.brightness = synced;
    }
  } else {
    for (const monitor of state.monitors) {
      monitor.brightness = clamp(monitor.brightness + delta, 0, 100);
    }
  }
  syncDemoUI();
}

function setPreviewTab(tab) {
  state.activeTab = tab;
  const flyout = document.getElementById("demo-flyout");
  const menu = document.getElementById("demo-menu");
  if (flyout) flyout.classList.toggle("is-hidden", tab !== "flyout");
  if (menu) menu.classList.toggle("is-hidden", tab !== "menu");

  document.querySelectorAll(".segmented-btn[data-preview-tab]").forEach((btn) => {
    const active = btn.getAttribute("data-preview-tab") === tab;
    btn.classList.toggle("is-active", active);
    btn.setAttribute("aria-selected", String(active));
  });
}

function initSlidersAndTray() {
  for (const monitor of state.monitors) {
    const input = document.querySelector(`[data-slider-input="${monitor.id}"]`);
    const wrap = document.querySelector(`[data-slider-wrap="${monitor.id}"]`);
    const row = document.querySelector(`[data-monitor-id="${monitor.id}"]`);

    if (input) {
      input.addEventListener("input", () => {
        setMonitorBrightness(monitor.id, Number(input.value));
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
          setMonitorBrightness(monitor.id, monitor.brightness + notches * state.scrollStep);
        },
        { passive: false }
      );
    }
  }

  const toggleLink = () => {
    state.linked = !state.linked;
    if (state.linked) {
      const avg = getAverageBrightness();
      for (const monitor of state.monitors) {
        monitor.brightness = avg;
      }
    }
    syncDemoUI();
  };

  document.getElementById("demo-link-btn")?.addEventListener("click", toggleLink);
  document.getElementById("menu-link-btn")?.addEventListener("click", toggleLink);

  const startupBtn = document.getElementById("menu-startup-btn");
  startupBtn?.addEventListener("click", () => {
    state.startupEnabled = !state.startupEnabled;
    startupBtn.setAttribute("aria-pressed", String(state.startupEnabled));
    startupBtn.querySelector(".fluent-switch")?.classList.toggle("is-checked", state.startupEnabled);
  });

  document.getElementById("menu-step-btn")?.addEventListener("click", () => {
    const idx = state.stepOptions.indexOf(state.scrollStep);
    state.scrollStep = state.stepOptions[(idx + 1) % state.stepOptions.length];
    syncDemoUI();
  });

  document.getElementById("menu-refresh-btn")?.addEventListener("click", () => {
    state.monitors[0].brightness = 75;
    state.monitors[1].brightness = 60;
    syncDemoUI();
    setPreviewTab("flyout");
  });

  document.querySelectorAll("[data-preview-tab]").forEach((el) => {
    el.addEventListener("click", () => {
      const target = el.getAttribute("data-preview-tab");
      if (target) setPreviewTab(target);
    });
  });

  const flyoutThemeBtn = document.getElementById("flyout-theme-toggle");
  const flyoutThemeLabel = document.querySelector("[data-flyout-theme-label]");
  const stage = document.getElementById("desktop-stage");
  flyoutThemeBtn?.addEventListener("click", () => {
    state.flyoutTheme = state.flyoutTheme === "dark" ? "light" : "dark";
    stage?.setAttribute("data-flyout-theme", state.flyoutTheme);
    if (flyoutThemeLabel) {
      flyoutThemeLabel.textContent = state.flyoutTheme === "dark" ? "Win11 Dark" : "Win11 Light";
    }
  });

  const traySunBtn = document.getElementById("tray-sun-btn");
  if (traySunBtn) {
    traySunBtn.addEventListener("click", () => {
      setPreviewTab(state.activeTab === "flyout" ? "menu" : "flyout");
    });

    traySunBtn.addEventListener("contextmenu", (event) => {
      event.preventDefault();
      setPreviewTab("menu");
    });

    traySunBtn.addEventListener(
      "wheel",
      (event) => {
        event.preventDefault();
        const notches = event.deltaY < 0 ? 1 : -1;
        stepAllMonitors(notches);
      },
      { passive: false }
    );
  }
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

function initTaskbarClock() {
  const timeEl = document.getElementById("taskbar-time");
  const dateEl = document.getElementById("taskbar-date");
  if (!timeEl || !dateEl) return;

  const update = () => {
    const now = new Date();
    timeEl.textContent = now.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
    dateEl.textContent = now.toLocaleDateString([], {
      month: "numeric",
      day: "numeric",
      year: "numeric",
    });
  };

  update();
  setInterval(update, 30_000);
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
      assets.find((a) => /\.(exe|msi|msix)$/i.test(a.name)) ||
      assets.find((a) => /\.zip$/i.test(a.name));

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

initSlidersAndTray();
initCopyButtons();
initTaskbarClock();
syncDemoUI();
hydrateLatestRelease();
