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
  // Goal: when the server restarts (redeploy / App Service restart / worker
  // recycle) the Blazor circuit is lost and the client shows the "Rejoining
  // the server…" modal. Left alone Blazor retries for minutes; a tab stuck
  // reconnecting that long can exhaust and crash its renderer ("Aw snap"),
  // after which neither a manual refresh nor any script can recover it.
  //
  // So we don't wait it out. On disconnect we give Blazor a few seconds to
  // self-heal a transient blip, then poll the app until the server answers
  // again and reload the page ONCE — promptly, while the tab is still healthy.
  // That's exactly what opening a fresh tab does, and it's the only thing that
  // reliably recovers. Polling until the server responds (rather than reloading
  // blindly) avoids reloading into a still-restarting server.
  //
  //   rejected      -> server reached but our circuit is gone -> reload now.
  //   show / failed -> poll the app root until it serves, then reload once.
  //
  // A reload budget stops an endless reload loop against a server that keeps
  // dropping the circuit; it is cleared after any healthy load.
  const RELOAD_KEY = "sessionGuard.reloads";
  const RELOAD_WINDOW_MS = 60000;
  const RELOAD_MAX = 5;

  function recentReloads() {
    try {
      const raw = sessionStorage.getItem(RELOAD_KEY);
      if (!raw) return [];
      const now = Date.now();
      return JSON.parse(raw).filter(function (t) { return now - t < RELOAD_WINDOW_MS; });
    } catch (e) { return []; }
  }

  function initReconnectRecovery() {
    const modal = document.getElementById("components-reconnect-modal");
    if (!modal) return;

    let reloading = false, graceTimer = null, pollTimer = null;

    function clearTimers() {
      if (graceTimer !== null) { clearTimeout(graceTimer);  graceTimer = null; }
      if (pollTimer  !== null) { clearInterval(pollTimer);  pollTimer  = null; }
    }

    function reloadNow() {
      if (reloading) return;
      const reloads = recentReloads();
      if (reloads.length >= RELOAD_MAX) return; // storm guard; leave the manual "Reload" link
      reloading = true;
      reloads.push(Date.now());
      try { sessionStorage.setItem(RELOAD_KEY, JSON.stringify(reloads)); } catch (e) {}
      clearTimers();
      modal.classList.add("session-guard-reloading");
      location.reload();
    }

    function stillDisconnected() {
      const cl = modal.classList;
      return cl.contains("components-reconnect-show") ||
             cl.contains("components-reconnect-failed") ||
             cl.contains("components-reconnect-rejected");
    }

    // Disconnected: wait a short grace for Blazor to self-heal a transient blip
    // (a silent reconnect preserves page state; a reload does not), then poll the
    // app root until it serves again and reload ONCE — while the tab is healthy.
    function beginRecovery() {
      if (reloading || graceTimer !== null || pollTimer !== null) return;
      graceTimer = setTimeout(function () {
        graceTimer = null;
        if (!stillDisconnected()) return;              // recovered on its own
        pollTimer = setInterval(function () {
          if (!stillDisconnected()) { clearTimers(); return; }
          fetch(window.location.pathname, { method: "GET", cache: "no-store", credentials: "same-origin" })
            .then(function (r) { if (r && r.status < 500) reloadNow(); })  // server answering -> reload
            .catch(function () { /* still down — keep polling */ });
        }, 3000);
      }, 5000);
    }

    function evaluate() {
      const cl = modal.classList;
      if (cl.contains("components-reconnect-rejected")) {
        reloadNow();                                   // circuit gone, server up -> reload immediately
      } else if (cl.contains("components-reconnect-show") ||
                 cl.contains("components-reconnect-failed")) {
        beginRecovery();                               // reconnecting -> reload once the server is back
      } else {
        clearTimers();                                 // reconnected/hidden -> cancel pending recovery
      }
    }

    // .NET 9/10 dispatches this event; the MutationObserver covers class-only runtimes.
    modal.addEventListener("components-reconnect-state-changed", evaluate);
    const observer = new MutationObserver(evaluate);
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
