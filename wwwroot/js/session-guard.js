// session-guard.js — shared across all Advanced suite apps.
// Two independent responsibilities, initialized from _Host.cshtml:
//   1. Reconnect auto-recovery: when the Blazor SignalR circuit cannot be
//      restored, reload automatically instead of stranding the user on the
//      "disconnected" modal. Loop-guarded so a truly-down server does not
//      reload forever.
//   2. Idle timeout: after N minutes without interaction, warn with a
//      countdown, then sign the user out via /logout?reason=inactivity.
(function () {
  "use strict";

  // ---- 1. Reconnect auto-recovery -----------------------------------------
  const RELOAD_KEY = "sessionGuard.reloads";
  const RELOAD_WINDOW_MS = 60000;
  const RELOAD_MAX = 3;

  function recentReloads() {
    try {
      const raw = sessionStorage.getItem(RELOAD_KEY);
      if (!raw) return [];
      const now = Date.now();
      return JSON.parse(raw).filter(function (t) { return now - t < RELOAD_WINDOW_MS; });
    } catch (e) { return []; }
  }

  function autoReload() {
    const reloads = recentReloads();
    if (reloads.length >= RELOAD_MAX) return false; // give up; keep manual link
    reloads.push(Date.now());
    try { sessionStorage.setItem(RELOAD_KEY, JSON.stringify(reloads)); } catch (e) {}
    location.reload();
    return true;
  }

  function initReconnectRecovery() {
    const modal = document.getElementById("components-reconnect-modal");
    if (!modal) return;
    const observer = new MutationObserver(function () {
      const cl = modal.classList;
      if (cl.contains("components-reconnect-failed") ||
          cl.contains("components-reconnect-rejected")) {
        modal.classList.add("session-guard-reloading");
        setTimeout(function () {
          if (!autoReload()) modal.classList.remove("session-guard-reloading");
        }, 1500);
      }
    });
    observer.observe(modal, { attributes: true, attributeFilter: ["class"] });
  }

  // Clear the reload budget shortly after a healthy load.
  window.addEventListener("load", function () {
    setTimeout(function () { try { sessionStorage.removeItem(RELOAD_KEY); } catch (e) {} }, 5000);
  });

  // ---- 2. Idle timeout ----------------------------------------------------
  let cfg = null, idleTimer = null, warnTimer = null, countdownTimer = null, lastPing = 0;

  function resetIdle() {
    if (!cfg) return;
    hideWarning();
    clearTimeout(idleTimer); clearTimeout(warnTimer);
    const idleMs = cfg.idleMinutes * 60000;
    const warnMs = cfg.warningSeconds * 1000;
    warnTimer = setTimeout(showWarning, Math.max(0, idleMs - warnMs));
    idleTimer = setTimeout(signOut, idleMs);
  }

  function onActivity() {
    resetIdle();
    const now = Date.now();
    if (now - lastPing > 300000) { // slide the server cookie at most every 5 min
      lastPing = now;
      fetch("/keepalive", { method: "GET", credentials: "same-origin" }).catch(function () {});
    }
  }

  function signOut() { window.location.href = "/logout?reason=inactivity"; }

  function ensureModal() {
    let m = document.getElementById("idle-warning-modal");
    if (m) return m;
    m = document.createElement("div");
    m.id = "idle-warning-modal";
    m.className = "idle-warning-modal";
    m.innerHTML =
      '<div class="idle-warning-card" role="alertdialog" aria-labelledby="idle-warning-title">' +
        '<h2 id="idle-warning-title" class="idle-warning-title">Still there?</h2>' +
        '<p class="idle-warning-text">You\'ll be signed out in ' +
          '<span class="idle-warning-count">0:00</span> due to inactivity.</p>' +
        '<div class="idle-warning-actions">' +
          '<button type="button" class="idle-warning-stay">Stay signed in</button>' +
          '<button type="button" class="idle-warning-out">Sign out now</button>' +
        '</div>' +
      '</div>';
    document.body.appendChild(m);
    m.querySelector(".idle-warning-stay").addEventListener("click", onActivity);
    m.querySelector(".idle-warning-out").addEventListener("click", signOut);
    return m;
  }

  function showWarning() {
    const m = ensureModal();
    m.classList.add("idle-warning-show");
    let remaining = cfg.warningSeconds;
    const countEl = m.querySelector(".idle-warning-count");
    function render() {
      const mm = Math.floor(remaining / 60);
      const ss = String(remaining % 60).padStart(2, "0");
      countEl.textContent = mm + ":" + ss;
    }
    render();
    clearInterval(countdownTimer);
    countdownTimer = setInterval(function () {
      remaining -= 1;
      if (remaining <= 0) { clearInterval(countdownTimer); return; }
      render();
    }, 1000);
  }

  function hideWarning() {
    const m = document.getElementById("idle-warning-modal");
    if (m) m.classList.remove("idle-warning-show");
    clearInterval(countdownTimer);
  }

  function initIdleTimeout(config) {
    cfg = config;
    let throttled = false;
    function handler() {
      if (throttled) return;
      throttled = true;
      setTimeout(function () { throttled = false; }, 1000);
      onActivity();
    }
    ["mousemove", "keydown", "click", "scroll", "touchstart"].forEach(function (e) {
      document.addEventListener(e, handler, { passive: true });
    });
    resetIdle();
  }

  window.SessionGuard = { initReconnectRecovery: initReconnectRecovery, initIdleTimeout: initIdleTimeout };
})();
