import {
  API, D, PANEL_HEADERS, api, bridge, cfg, emit, h, haptic, loadConfig, online,
  remoteStats, runShortcut, sendRemoteDelta, setGyro, setGyroListener, setMediaState,
  setRemoteActive, store, toast, touchActivity,
} from '../main.js';
import { W } from '../registry.js';
import { applyToggles } from './basic.js';
import { invalidateDeckState } from './controls.js';

// Laptop remote widget: touchpad + gyroscope motion mode, mouse buttons, soft keyboard, quick keys,
// media/volume controls, shortcut launcher, and Disable button.
const QUICK_KEYS = [
  ['Esc', 'esc'],
  ['Tab', 'tab'],
  ['Win', 'win'],
  ['Alt+Tab', 'alt+tab'],
  ['←', 'left'],
  ['↑', 'up'],
  ['↓', 'down'],
  ['→', 'right'],
  ['⌫', 'backspace'],
  ['↵', 'enter'],
];

const NAV_KEYS = {
  ArrowLeft: 'left',
  ArrowRight: 'right',
  ArrowUp: 'up',
  ArrowDown: 'down',
  Backspace: 'backspace',
  Enter: 'enter',
  Escape: 'esc',
  Tab: 'tab',
};

const MOVE_INTERVAL_MS = 16; // ~60 Hz cap
const SCROLL_SCALE = 4.5;
const AXES = ['yaw', 'pitch', 'roll'];
const MOTION_STORAGE_KEY = 'remoteMotion';

function readMotionCfg(optionSens) {
  const defSens = Math.max(25, Math.min(300, Number(optionSens) || 100));
  const saved = store.get(MOTION_STORAGE_KEY, null) || {};
  return {
    mode: saved.mode === 'motion' ? 'motion' : 'touchpad',
    sensitivity: Math.max(25, Math.min(300, Number(saved.sensitivity) || defSens)),
    smoothing: Math.max(0, Math.min(90, saved.smoothing ?? 35)),
    deadZone: Math.max(0, Math.min(20, saved.deadZone ?? 2)),
    xAxis: AXES.includes(saved.xAxis) ? saved.xAxis : 'yaw',
    yAxis: AXES.includes(saved.yAxis) ? saved.yAxis : 'pitch',
    invertX: !!saved.invertX,
    invertY: !!saved.invertY,
    tuningOpen: saved.tuningOpen !== false,
  };
}

function axisSelect(cls, label, selected) {
  const s = h('select', cls);
  s.setAttribute('aria-label', label);
  for (const a of AXES) {
    const opt = h('option', null, a[0].toUpperCase() + a.slice(1));
    opt.value = a;
    if (a === selected) opt.selected = true;
    s.append(opt);
  }
  return s;
}

W.remote = {
  needs: ['state'],
  create(box, { o }) {
    const sens = Math.max(25, Math.min(300, Number(o.sensitivity) || 100)) / 100;
    const motionCfg = readMotionCfg(o.sensitivity);
    const saveMotionCfg = () => store.set(MOTION_STORAGE_KEY, motionCfg);

    const notice = h('div', 'rm-notice',
      'Allow remote input is off on the PC — turn it on in PhoneBox Settings › General or from the tray icon.');

    const modeTouchBtn = h('button', 'rm-mode-btn', 'Touchpad');
    modeTouchBtn.dataset.mode = 'touchpad';
    const modeMotionBtn = h('button', 'rm-mode-btn', 'Motion');
    modeMotionBtn.dataset.mode = 'motion';
    const tuneBtn = h('button', 'rm-tune-btn', '⚙ Tuning');
    tuneBtn.setAttribute('aria-label', 'Motion settings');
    const debugOn = o.debug === true;
    const debugEl = h('span', 'rm-debug', '');
    debugEl.hidden = !debugOn;
    const modeBar = h('div', 'rm-mode-bar', modeTouchBtn, modeMotionBtn, tuneBtn, debugEl);

    const dragBadge = h('span', 'rm-drag-badge', 'DRAG');
    dragBadge.hidden = true;
    const padHint = h('div', 'rm-pad-hint',
      'Touchpad · tap = click · 2-finger tap = right-click · 2-finger drag = scroll · double-tap & drag');
    const pad = h('div', 'remote-pad', padHint, dragBadge);
    pad.setAttribute('aria-label', 'Touchpad');

    const noGyroMsg = h('div', 'rm-no-gyro', 'No gyroscope sensor detected on this phone.');
    noGyroMsg.hidden = true;
    const moveHint = h('small', 'rm-move-sub', 'Hold & aim phone to steer cursor');
    const moveBtn = h('button', 'rm-move-btn', h('span', null, '✋ Hold to move'), moveHint);
    moveBtn.setAttribute('aria-label', 'Hold to move');
    const scrollUpBtn = h('button', 'rm-ctl rm-scroll-up', '▲');
    scrollUpBtn.setAttribute('aria-label', 'Scroll up');
    const scrollDownBtn = h('button', 'rm-ctl rm-scroll-down', '▼');
    scrollDownBtn.setAttribute('aria-label', 'Scroll down');
    const scrollCol = h('div', 'rm-scroll-col', scrollUpBtn, h('span', 'rm-scroll-lbl', 'Scroll'), scrollDownBtn);
    const motionTop = h('div', 'rm-motion-top', moveBtn, scrollCol);

    const xAxisSel = axisSelect('rm-axis-x', 'Horizontal axis', motionCfg.xAxis);
    const yAxisSel = axisSelect('rm-axis-y', 'Vertical axis', motionCfg.yAxis);
    const invXChk = h('input', 'rm-inv-x');
    invXChk.type = 'checkbox';
    invXChk.checked = motionCfg.invertX;
    const invYChk = h('input', 'rm-inv-y');
    invYChk.type = 'checkbox';
    invYChk.checked = motionCfg.invertY;
    const cfgAxesRow = h('div', 'rm-cfg-row',
      h('label', 'rm-cfg-field', h('span', null, 'X'), xAxisSel),
      h('label', 'rm-cfg-field', h('span', null, 'Y'), yAxisSel),
      h('label', 'rm-chk', invXChk, ' Invert X'),
      h('label', 'rm-chk', invYChk, ' Invert Y'),
    );

    const sensInput = h('input', 'rm-sens');
    sensInput.type = 'range'; sensInput.min = '25'; sensInput.max = '300'; sensInput.step = '5';
    sensInput.value = String(motionCfg.sensitivity);
    sensInput.setAttribute('aria-label', 'Motion sensitivity');
    const sensVal = h('span', 'rm-cfg-val rm-sens-val', `${motionCfg.sensitivity}%`);

    const smoothInput = h('input', 'rm-smooth');
    smoothInput.type = 'range'; smoothInput.min = '0'; smoothInput.max = '90'; smoothInput.step = '5';
    smoothInput.value = String(motionCfg.smoothing);
    smoothInput.setAttribute('aria-label', 'Motion smoothing');
    const smoothVal = h('span', 'rm-cfg-val rm-smooth-val', `${motionCfg.smoothing}%`);

    const deadInput = h('input', 'rm-dead');
    deadInput.type = 'range'; deadInput.min = '0'; deadInput.max = '20'; deadInput.step = '1';
    deadInput.value = String(motionCfg.deadZone);
    deadInput.setAttribute('aria-label', 'Motion dead zone');
    const deadVal = h('span', 'rm-cfg-val rm-dead-val', `${(motionCfg.deadZone * 0.01).toFixed(2)}`);

    const cfgTuneRow = h('div', 'rm-cfg-sliders',
      h('label', 'rm-slider', h('span', null, 'Sens'), sensInput, sensVal),
      h('label', 'rm-slider', h('span', null, 'Smooth'), smoothInput, smoothVal),
      h('label', 'rm-slider', h('span', null, 'Dead'), deadInput, deadVal),
    );
    const motionCfgPane = h('div', 'rm-motion-cfg', cfgAxesRow, cfgTuneRow);
    const motionPane = h('div', 'rm-motion-pane', noGyroMsg, motionTop, motionCfgPane);

    const lmb = h('button', 'rm-btn rm-lmb', 'Left click');
    const rmb = h('button', 'rm-btn rm-rmb', 'Right click');
    const kbdBtn = h('button', 'rm-btn rm-kbd-btn', '⌨ Keyboard');
    const kbdIn = h('input', 'rm-kbd-input');
    kbdIn.type = 'text';
    kbdIn.autocomplete = 'off';
    kbdIn.autocapitalize = 'off';
    kbdIn.spellcheck = false;
    kbdIn.setAttribute('autocorrect', 'off');
    kbdIn.setAttribute('aria-label', 'Remote keyboard input');

    const clickRow = h('div', 'rm-click-row', lmb, rmb, kbdBtn, kbdIn);
    const keyBtns = QUICK_KEYS.map(([label, spec]) => {
      const b = h('button', 'rm-key', label);
      b.dataset.key = spec;
      return b;
    });
    const keysRow = h('div', 'rm-keys', ...keyBtns);
    const leftCol = h('div', 'rm-left-col', modeBar, pad, motionPane, clickRow, keysRow);

    const mPrev = h('button', 'rm-ctl', '⏮');
    mPrev.setAttribute('aria-label', 'Previous track');
    const mPlay = h('button', 'rm-ctl rm-play', '⏯');
    mPlay.setAttribute('aria-label', 'Play/Pause');
    const mNext = h('button', 'rm-ctl', '⏭');
    mNext.setAttribute('aria-label', 'Next track');
    const mediaRow = h('div', 'rm-media', mPrev, mPlay, mNext);

    const vMute = h('button', 'rm-ctl rm-mute', '🔇');
    vMute.setAttribute('aria-label', 'Mute');
    const vDown = h('button', 'rm-ctl', '🔉 −');
    vDown.setAttribute('aria-label', 'Volume down');
    const vUp = h('button', 'rm-ctl', '🔊 +');
    vUp.setAttribute('aria-label', 'Volume up');
    const vPct = h('span', 'rm-vol-pct', '--');
    const volRow = h('div', 'rm-vol', vMute, vDown, vUp, vPct);

    const shortcuts = cfg.shortcuts || [];
    const appRow = h('div', 'rm-apps',
      ...(shortcuts.length
        ? shortcuts.map((s) => {
            const b = h('button', 'rm-app' + (s.toggle ? ' key toggle' : ''),
              h('span', 'rm-app-ico', s.icon || '•'),
              h('span', 'rm-app-lbl', s.label));
            b.style.setProperty('--k', s.color || '#6b7280');
            b.dataset.id = s.id;
            b.addEventListener('click', (e) => {
              e.stopPropagation();
              touchActivity();
              runShortcut(b, s.id);
            });
            return b;
          })
        : [h('span', 'rm-apps-empty', 'No shortcuts configured')]));

    const disableBtn = h('button', 'rm-disable', '🛑 Disable remote');
    const sideCol = h('div', 'rm-side', mediaRow, volRow, appRow, disableBtn);
    const main = h('div', 'rm-main', leftCol, sideCol);

    box.append(notice, main);

    // ---- state & coalesced transport
    let destroyed = false;
    let moveInFlight = false;
    let pendingDx = 0;
    let pendingDy = 0;
    let remX = 0;
    let remY = 0;
    let lastMoveAt = 0;
    let moveTimer = 0;

    let scrollInFlight = false;
    let pendingScrollX = 0;
    let pendingScrollY = 0;
    let scrollRemX = 0;
    let scrollRemY = 0;
    let lastScrollAt = 0;
    let scrollTimer = 0;

    let dragActive = false;
    let onCurrentPage = false;
    let gyroActive = false;
    let moveHeld = false;
    let holdTimer = 0;
    let emaX = 0;
    let emaY = 0;
    let emaInit = false;
    let remoteActiveSent = false;
    const jsSamples = [];

    const remoteEnabled = () => cfg.allowRemoteInput === true;
    const sensorsInfo = (() => {
      try { return bridge?.sensors ? JSON.parse(bridge.sensors()) : null; } catch (_) { return null; }
    })();
    noGyroMsg.hidden = !(sensorsInfo && sensorsInfo.gyro === false);

    const recordJsSample = (ms) => {
      if (!debugOn) return;
      const now = performance.now();
      jsSamples.push({ at: now, ms });
      while (jsSamples.length && now - jsSamples[0].at > 5000) jsSamples.shift();
      renderDebug();
    };

    const renderDebug = () => {
      if (!debugOn || destroyed) return;
      const st = remoteStats();
      if (st && st.count > 0) {
        debugEl.textContent = `${st.route} · ${st.minMs.toFixed(0)}/${st.avgMs.toFixed(0)}/${st.p95Ms.toFixed(0)}/${st.maxMs.toFixed(0)}ms · reuse ${st.reused}/${st.count}`;
        return;
      }
      const now = performance.now();
      while (jsSamples.length && now - jsSamples[0].at > 5000) jsSamples.shift();
      if (!jsSamples.length) {
        debugEl.textContent = st?.route ? `${st.route} · idle` : '';
        return;
      }
      const s = jsSamples.map((x) => x.ms).sort((a, b) => a - b);
      const avg = s.reduce((a, b) => a + b, 0) / s.length;
      const p95 = s[Math.min(s.length - 1, Math.floor(s.length * 0.95))];
      debugEl.textContent = `${st?.route || 'web'} · ${s[0].toFixed(0)}/${avg.toFixed(0)}/${p95.toFixed(0)}/${s[s.length - 1].toFixed(0)}ms`;
    };

    const stopMoveHold = () => {
      moveHeld = false;
      emaInit = false;
      emaX = 0;
      emaY = 0;
      remX = 0;
      remY = 0;
      moveBtn.classList.remove('holding');
      moveHint.textContent = 'Hold & aim phone to steer cursor';
      clearInterval(holdTimer);
      holdTimer = 0;
    };

    const syncRemoteActive = () => {
      const wantLock = !destroyed && onCurrentPage && !document.hidden && remoteEnabled() && online !== false;
      if (wantLock !== remoteActiveSent) {
        remoteActiveSent = wantLock;
        setRemoteActive(wantLock);
      }
    };

    const syncGyroSubscription = () => {
      syncRemoteActive();
      const want = !destroyed && onCurrentPage && !document.hidden && motionCfg.mode === 'motion' && remoteEnabled() && online !== false && !(sensorsInfo && sensorsInfo.gyro === false);
      if (want === gyroActive) return;
      gyroActive = want;
      setGyro(want);
      setGyroListener(want ? handleGyro : null);
      if (!want) stopMoveHold();
    };

    const renderMode = () => {
      const isMotion = motionCfg.mode === 'motion';
      pad.hidden = isMotion;
      motionPane.hidden = !isMotion;
      modeTouchBtn.classList.toggle('on', !isMotion);
      modeMotionBtn.classList.toggle('on', isMotion);
      tuneBtn.hidden = !isMotion;
      tuneBtn.classList.toggle('on', !!motionCfg.tuningOpen);
      tuneBtn.setAttribute('aria-expanded', String(!!motionCfg.tuningOpen));
      motionCfgPane.hidden = !motionCfg.tuningOpen;
      if (!isMotion) stopMoveHold();
      syncGyroSubscription();
    };

    const renderEnabled = () => {
      const on = remoteEnabled();
      notice.hidden = on;
      box.classList.toggle('rm-disabled', !on);
      lmb.disabled = rmb.disabled = kbdBtn.disabled = disableBtn.disabled = moveBtn.disabled = scrollUpBtn.disabled = scrollDownBtn.disabled = !on;
      for (const b of keyBtns) b.disabled = !on;
      if (!on) {
        stopMoveHold();
        if (document.activeElement === kbdIn) kbdIn.blur();
      }
      syncGyroSubscription();
    };

    async function postRemote(query) {
      if (destroyed) return false;
      const t0 = performance.now();
      try {
        const r = await fetch(`${API}/remote?${query}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        recordJsSample(performance.now() - t0);
        if (r.status === 403 && !destroyed) {
          cfg.allowRemoteInput = false;
          renderEnabled();
          loadConfig();
        }
        return r.ok;
      } catch (_) {
        recordJsSample(performance.now() - t0);
        return false;
      }
    }

    function sendRemote(query) {
      if (!remoteEnabled()) {
        renderEnabled();
        return Promise.resolve(false);
      }
      touchActivity();
      return postRemote(query);
    }

    async function flushMove() {
      moveTimer = 0;
      if (destroyed || moveInFlight || !remoteEnabled()) return;
      const dx = Math.trunc(pendingDx);
      const dy = Math.trunc(pendingDy);
      if (!dx && !dy) return;
      if (sendRemoteDelta('move', dx, dy)) {
        pendingDx -= dx;
        pendingDy -= dy;
        lastMoveAt = performance.now();
        return;
      }
      const wait = MOVE_INTERVAL_MS - (performance.now() - lastMoveAt);
      if (wait > 0) {
        moveTimer = setTimeout(flushMove, Math.ceil(wait));
        return;
      }
      pendingDx -= dx;
      pendingDy -= dy;
      moveInFlight = true;
      lastMoveAt = performance.now();
      try {
        await postRemote(`op=move&dx=${dx}&dy=${dy}`);
      } finally {
        moveInFlight = false;
        if (!destroyed && (Math.trunc(pendingDx) || Math.trunc(pendingDy)) && !moveTimer) flushMove();
      }
    }

    function accumulateMove(dx, dy) {
      if (!remoteEnabled()) return;
      remX += dx;
      remY += dy;
      const ix = Math.trunc(remX);
      const iy = Math.trunc(remY);
      if (!ix && !iy) return;
      remX -= ix;
      remY -= iy;
      pendingDx += ix;
      pendingDy += iy;
      flushMove();
    }

    function queueMove(rawDx, rawDy) {
      const speed = Math.hypot(rawDx, rawDy);
      if (!speed) return;
      // Mild velocity curve so tiny finger nudges stay accurate while quick swipes span the screen
      const mult = sens * Math.min(2.2, 1 + 0.045 * Math.pow(speed, 0.85));
      accumulateMove(rawDx * mult, rawDy * mult);
    }

    async function flushScroll() {
      scrollTimer = 0;
      if (destroyed || scrollInFlight || !remoteEnabled()) return;
      const dx = Math.trunc(pendingScrollX);
      const dy = Math.trunc(pendingScrollY);
      if (!dx && !dy) return;
      if (sendRemoteDelta('scroll', dx, dy)) {
        pendingScrollX -= dx;
        pendingScrollY -= dy;
        lastScrollAt = performance.now();
        return;
      }
      const wait = MOVE_INTERVAL_MS - (performance.now() - lastScrollAt);
      if (wait > 0) {
        scrollTimer = setTimeout(flushScroll, Math.ceil(wait));
        return;
      }
      pendingScrollX -= dx;
      pendingScrollY -= dy;
      scrollInFlight = true;
      lastScrollAt = performance.now();
      try {
        await postRemote(`op=scroll&dx=${dx}&dy=${dy}`);
      } finally {
        scrollInFlight = false;
        if (!destroyed && (Math.trunc(pendingScrollX) || Math.trunc(pendingScrollY)) && !scrollTimer) flushScroll();
      }
    }

    function queueScroll(rawDx, rawDy, scale = SCROLL_SCALE) {
      if (!remoteEnabled()) return;
      scrollRemX += rawDx * scale;
      scrollRemY += rawDy * scale;
      const ix = Math.trunc(scrollRemX);
      const iy = Math.trunc(scrollRemY);
      if (!ix && !iy) return;
      scrollRemX -= ix;
      scrollRemY -= iy;
      pendingScrollX += ix;
      pendingScrollY += iy;
      flushScroll();
    }

    const axisVal = (s, axis) => Number(axis === 'yaw' ? s.z : axis === 'pitch' ? s.x : s.y) || 0;
    function handleGyro(sample) {
      if (destroyed || !moveHeld || motionCfg.mode !== 'motion' || !remoteEnabled()) return;
      touchActivity();
      const dt = Math.max(0.001, Math.min(0.2, Number(sample?.dt) || 0.016));
      let wx = axisVal(sample, motionCfg.xAxis) * (motionCfg.invertX ? 1 : -1);
      let wy = axisVal(sample, motionCfg.yAxis) * (motionCfg.invertY ? 1 : -1);
      const dz = (Number(motionCfg.deadZone) || 0) * 0.01;
      wx = Math.abs(wx) <= dz ? 0 : Math.sign(wx) * (Math.abs(wx) - dz);
      wy = Math.abs(wy) <= dz ? 0 : Math.sign(wy) * (Math.abs(wy) - dz);
      const smooth = Math.max(0, Math.min(90, Number(motionCfg.smoothing) ?? 35)) / 100;
      const alpha = 1 - smooth * 0.9;
      if (!emaInit) { emaX = wx; emaY = wy; emaInit = true; }
      else { emaX += alpha * (wx - emaX); emaY += alpha * (wy - emaY); }
      if (wx === 0 && Math.abs(emaX) < 1e-4) emaX = 0;
      if (wy === 0 && Math.abs(emaY) < 1e-4) emaY = 0;
      const scale = (Math.max(25, Math.min(300, Number(motionCfg.sensitivity) || 100)) / 100) * 950;
      const dx = emaX * dt * scale;
      const dy = emaY * dt * scale;
      if (dx || dy) accumulateMove(dx, dy);
    }

    function setDrag(on) {
      dragActive = on;
      dragBadge.hidden = !on;
      pad.classList.toggle('dragging', on);
    }

    // ---- Touchpad gestures (multi-touch)
    let mode = 'idle'; // 'idle' | 'one' | 'two'
    let startAt = 0;
    let startX = 0;
    let startY = 0;
    let lastX = 0;
    let lastY = 0;
    let movedDist = 0;
    let maxFingers = 0;
    let twoStartX = 0;
    let twoStartY = 0;
    let twoLastX = 0;
    let twoLastY = 0;
    let twoMoved = false;
    let lastTapAt = 0;
    let lastTapX = 0;
    let lastTapY = 0;
    let doubleTapArmed = false;

    const centroid = (touches) => ({
      x: (touches[0].clientX + touches[1].clientX) / 2,
      y: (touches[0].clientY + touches[1].clientY) / 2,
    });

    const startTwo = (touches) => {
      const c = centroid(touches);
      mode = 'two';
      maxFingers = 2;
      doubleTapArmed = false;
      twoStartX = twoLastX = c.x;
      twoStartY = twoLastY = c.y;
      twoMoved = false;
    };

    const endDragIfNeeded = () => {
      if (!dragActive) return;
      setDrag(false);
      sendRemote('op=up&btn=left');
    };

    pad.addEventListener('touchstart', (e) => {
      e.preventDefault();
      e.stopPropagation();
      touchActivity();
      if (!remoteEnabled()) { renderEnabled(); return; }
      const n = e.touches.length;
      if (n === 1) {
        const t = e.touches[0];
        const now = Date.now();
        mode = 'one';
        maxFingers = 1;
        startAt = now;
        startX = lastX = t.clientX;
        startY = lastY = t.clientY;
        movedDist = 0;
        doubleTapArmed = (now - lastTapAt) < 320 && Math.hypot(t.clientX - lastTapX, t.clientY - lastTapY) < 28;
      } else if (n >= 2) {
        endDragIfNeeded();
        startTwo(e.touches);
      }
    }, { passive: false });

    pad.addEventListener('touchmove', (e) => {
      e.preventDefault();
      e.stopPropagation();
      touchActivity();
      if (!remoteEnabled()) return;
      const n = e.touches.length;
      if (n >= 2) {
        if (mode !== 'two') { endDragIfNeeded(); startTwo(e.touches); return; }
        const c = centroid(e.touches);
        const dx = c.x - twoLastX;
        const dy = c.y - twoLastY;
        twoLastX = c.x;
        twoLastY = c.y;
        if (Math.hypot(c.x - twoStartX, c.y - twoStartY) > 8) twoMoved = true;
        if (dx || dy) queueScroll(-dx, dy);
        return;
      }
      if (mode === 'one' && n === 1) {
        const t = e.touches[0];
        const dx = t.clientX - lastX;
        const dy = t.clientY - lastY;
        lastX = t.clientX;
        lastY = t.clientY;
        movedDist = Math.max(movedDist, Math.hypot(t.clientX - startX, t.clientY - startY));
        if (doubleTapArmed && !dragActive && movedDist > 6) {
          doubleTapArmed = false;
          setDrag(true);
          haptic();
          sendRemote('op=down&btn=left');
        }
        if (dx || dy) queueMove(dx, dy);
      }
    }, { passive: false });

    const finishTouch = (e) => {
      e.preventDefault();
      e.stopPropagation();
      if (e.touches.length === 1 && mode === 'two') {
        // Ignore lingering single finger lift after two-finger gesture until all fingers lift
        return;
      }
      if (e.touches.length > 0) return;

      const dt = Date.now() - startAt;
      if (dragActive) {
        endDragIfNeeded();
        lastTapAt = 0;
      } else if (mode === 'two' && maxFingers === 2 && !twoMoved && dt < 300) {
        haptic();
        lastTapAt = 0;
        sendRemote('op=click&btn=right');
      } else if (mode === 'one' && maxFingers === 1 && movedDist < 8 && dt < 260) {
        haptic();
        lastTapAt = Date.now();
        lastTapX = startX;
        lastTapY = startY;
        sendRemote('op=click&btn=left');
      }
      mode = 'idle';
      maxFingers = 0;
      doubleTapArmed = false;
    };
    pad.addEventListener('touchend', finishTouch, { passive: false });
    pad.addEventListener('touchcancel', (e) => {
      e.preventDefault();
      e.stopPropagation();
      endDragIfNeeded();
      mode = 'idle';
      maxFingers = 0;
      doubleTapArmed = false;
    }, { passive: false });

    // Desktop mouse fallback (browser demo / Playwright tests; touch is handled above)
    let mDown = false;
    let mStartX = 0;
    let mStartY = 0;
    let mLastX = 0;
    let mLastY = 0;
    let mMoved = false;
    pad.addEventListener('pointerdown', (e) => {
      if (e.pointerType === 'touch') return;
      e.stopPropagation();
      touchActivity();
      if (!remoteEnabled()) { renderEnabled(); return; }
      mDown = true;
      mMoved = false;
      mStartX = mLastX = e.clientX;
      mStartY = mLastY = e.clientY;
      try { pad.setPointerCapture(e.pointerId); } catch (_) {}
    });
    pad.addEventListener('pointermove', (e) => {
      if (e.pointerType === 'touch' || !mDown) return;
      e.stopPropagation();
      const dx = e.clientX - mLastX;
      const dy = e.clientY - mLastY;
      mLastX = e.clientX;
      mLastY = e.clientY;
      if (Math.hypot(e.clientX - mStartX, e.clientY - mStartY) > 4) mMoved = true;
      if (dx || dy) queueMove(dx, dy);
    });
    const stopMouse = (e) => {
      if (e.pointerType === 'touch' || !mDown) return;
      e.stopPropagation();
      mDown = false;
      if (!mMoved && e.type === 'pointerup') {
        haptic();
        sendRemote(`op=click&btn=${e.button === 2 ? 'right' : 'left'}`);
      }
    };
    pad.addEventListener('pointerup', stopMouse);
    pad.addEventListener('pointercancel', stopMouse);

    // ---- Mouse buttons & quick keys
    lmb.addEventListener('click', (e) => { e.stopPropagation(); haptic(); sendRemote('op=click&btn=left'); });
    rmb.addEventListener('click', (e) => { e.stopPropagation(); haptic(); sendRemote('op=click&btn=right'); });
    for (const b of keyBtns) {
      b.addEventListener('click', (e) => {
        e.stopPropagation();
        haptic();
        sendRemote('op=key&key=' + encodeURIComponent(b.dataset.key));
      });
    }

    // ---- Hidden input for Android soft keyboard
    const resetSentinel = () => {
      kbdIn.value = ' ';
      try { kbdIn.setSelectionRange(1, 1); } catch (_) {}
    };
    const sendTextChunks = (str) => {
      if (!str) return;
      for (let i = 0; i < str.length; i += 500) {
        sendRemote('op=text&text=' + encodeURIComponent(str.slice(i, i + 500)));
      }
    };

    kbdBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      haptic();
      touchActivity();
      if (!remoteEnabled()) { renderEnabled(); return; }
      if (document.activeElement === kbdIn) kbdIn.blur();
      else {
        resetSentinel();
        kbdIn.focus({ preventScroll: true });
      }
    });
    kbdIn.addEventListener('focus', () => {
      kbdBtn.classList.add('on');
      resetSentinel();
    });
    kbdIn.addEventListener('blur', () => {
      kbdBtn.classList.remove('on');
      kbdIn.value = '';
    });
    kbdIn.addEventListener('keydown', (e) => {
      e.stopPropagation();
      touchActivity();
      if (e.isComposing) return;
      const mapped = NAV_KEYS[e.key];
      if (mapped) {
        e.preventDefault();
        sendRemote('op=key&key=' + encodeURIComponent(mapped));
      }
    });
    kbdIn.addEventListener('input', (e) => {
      touchActivity();
      if (e.isComposing) return;
      const val = kbdIn.value;
      if (val === '' || e.inputType === 'deleteContentBackward') {
        resetSentinel();
        sendRemote('op=key&key=backspace');
        return;
      }
      const typed = val.startsWith(' ') ? val.slice(1) : val;
      resetSentinel();
      sendTextChunks(typed);
    });
    kbdIn.addEventListener('compositionend', (e) => {
      touchActivity();
      const val = kbdIn.value;
      const typed = (val.startsWith(' ') ? val.slice(1) : val) || e.data || '';
      setTimeout(resetSentinel, 0);
      sendTextChunks(typed);
    });

    // ---- Media & volume controls (reuse existing endpoints)
    const mediaAct = async (action) => {
      haptic();
      touchActivity();
      try {
        const res = await api('/media/' + action, 'POST');
        if (res && typeof res === 'object' && 'media' in res) setMediaState(res.media);
        invalidateDeckState();
        emit('tick');
      } catch (_) {}
    };
    mPrev.addEventListener('click', (e) => { e.stopPropagation(); mediaAct('prev'); });
    mPlay.addEventListener('click', (e) => { e.stopPropagation(); mediaAct('playpause'); });
    mNext.addEventListener('click', (e) => { e.stopPropagation(); mediaAct('next'); });

    const volAct = async (op) => {
      haptic();
      touchActivity();
      if (!D.state?.audio || D.state.audio.canControl === false) return;
      try {
        const a = await api('/volume?op=' + op, 'POST');
        if (D.state) D.state.audio = a;
        emit('state');
      } catch (_) {}
    };
    vMute.addEventListener('click', (e) => { e.stopPropagation(); volAct('mute'); });
    vDown.addEventListener('click', (e) => { e.stopPropagation(); volAct('down'); });
    vUp.addEventListener('click', (e) => { e.stopPropagation(); volAct('up'); });

    // ---- Disable remote button
    disableBtn.addEventListener('click', async (e) => {
      e.stopPropagation();
      haptic();
      touchActivity();
      endDragIfNeeded();
      stopMoveHold();
      disableBtn.disabled = true;
      try {
        await postRemote('op=disable');
        cfg.allowRemoteInput = false;
        renderEnabled();
        toast('Remote input disabled', 'ok');
        loadConfig();
      } catch (_) {
        toast('Could not disable remote input', 'err');
      } finally {
        renderEnabled();
      }
    });

    // ---- Motion mode controls & settings
    const setMode = (mode) => {
      motionCfg.mode = mode === 'motion' ? 'motion' : 'touchpad';
      saveMotionCfg();
      renderMode();
    };
    modeTouchBtn.addEventListener('click', (e) => { e.stopPropagation(); haptic(); touchActivity(); setMode('touchpad'); });
    modeMotionBtn.addEventListener('click', (e) => { e.stopPropagation(); haptic(); touchActivity(); setMode('motion'); });
    tuneBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      haptic();
      touchActivity();
      motionCfg.tuningOpen = !motionCfg.tuningOpen;
      saveMotionCfg();
      renderMode();
    });

    const startMoveHold = (e) => {
      e.preventDefault();
      e.stopPropagation();
      touchActivity();
      if (!remoteEnabled() || motionCfg.mode !== 'motion' || destroyed) return;
      endDragIfNeeded();
      moveHeld = true;
      emaInit = false;
      emaX = 0;
      emaY = 0;
      remX = 0;
      remY = 0;
      moveBtn.classList.add('holding');
      moveHint.textContent = 'Moving cursor…';
      if (!holdTimer) holdTimer = setInterval(touchActivity, 1000);
      try { moveBtn.setPointerCapture(e.pointerId); } catch (_) {}
    };
    moveBtn.addEventListener('pointerdown', startMoveHold);
    ['pointerup', 'pointercancel', 'lostpointercapture'].forEach((ev) => {
      moveBtn.addEventListener(ev, (e) => { e.stopPropagation(); stopMoveHold(); });
    });
    moveBtn.addEventListener('touchstart', (e) => { e.preventDefault(); e.stopPropagation(); touchActivity(); }, { passive: false });
    moveBtn.addEventListener('contextmenu', (e) => e.preventDefault());

    scrollUpBtn.addEventListener('click', (e) => { e.stopPropagation(); haptic(); touchActivity(); queueScroll(0, 120, 1); });
    scrollDownBtn.addEventListener('click', (e) => { e.stopPropagation(); haptic(); touchActivity(); queueScroll(0, -120, 1); });

    xAxisSel.addEventListener('change', (e) => { e.stopPropagation(); touchActivity(); motionCfg.xAxis = xAxisSel.value; saveMotionCfg(); });
    yAxisSel.addEventListener('change', (e) => { e.stopPropagation(); touchActivity(); motionCfg.yAxis = yAxisSel.value; saveMotionCfg(); });
    invXChk.addEventListener('change', (e) => { e.stopPropagation(); touchActivity(); motionCfg.invertX = invXChk.checked; saveMotionCfg(); });
    invYChk.addEventListener('change', (e) => { e.stopPropagation(); touchActivity(); motionCfg.invertY = invYChk.checked; saveMotionCfg(); });
    sensInput.addEventListener('input', (e) => {
      e.stopPropagation();
      touchActivity();
      motionCfg.sensitivity = Number(sensInput.value);
      sensVal.textContent = `${motionCfg.sensitivity}%`;
      saveMotionCfg();
    });
    smoothInput.addEventListener('input', (e) => {
      e.stopPropagation();
      touchActivity();
      motionCfg.smoothing = Number(smoothInput.value);
      smoothVal.textContent = `${motionCfg.smoothing}%`;
      saveMotionCfg();
    });
    deadInput.addEventListener('input', (e) => {
      e.stopPropagation();
      touchActivity();
      motionCfg.deadZone = Number(deadInput.value);
      deadVal.textContent = `${(motionCfg.deadZone * 0.01).toFixed(2)}`;
      saveMotionCfg();
    });

    const onVisChange = () => {
      if (document.hidden) stopMoveHold();
      syncGyroSubscription();
    };
    const onWindowBlur = () => stopMoveHold();
    document.addEventListener('visibilitychange', onVisChange);
    window.addEventListener('blur', onWindowBlur);

    const syncState = () => {
      renderEnabled();
      applyToggles(appRow);
      const m = D.state?.media;
      mPlay.classList.toggle('on', !!m?.playing);
      const a = D.state?.audio;
      const noAudio = !a || a.canControl === false;
      vMute.disabled = vDown.disabled = vUp.disabled = noAudio;
      vMute.classList.toggle('on', !!a?.muted);
      vPct.textContent = a ? (a.muted ? 'muted' : `${a.volume}%`) : '--';
    };

    const statsTimer = (debugOn || bridge?.remoteStats) ? setInterval(() => {
      if (destroyed || !onCurrentPage) return;
      const st = remoteStats();
      if (st?.status === 403 && remoteEnabled()) {
        cfg.allowRemoteInput = false;
        renderEnabled();
        loadConfig();
      }
      if (debugOn) renderDebug();
    }, 500) : 0;

    renderMode();
    syncState();
    return {
      update: syncState,
      onPage(active) {
        if (onCurrentPage === !!active) return;
        onCurrentPage = !!active;
        if (!onCurrentPage) stopMoveHold();
        syncGyroSubscription();
      },
      destroy() {
        destroyed = true;
        clearInterval(statsTimer);
        document.removeEventListener('visibilitychange', onVisChange);
        window.removeEventListener('blur', onWindowBlur);
        stopMoveHold();
        if (remoteActiveSent) {
          remoteActiveSent = false;
          setRemoteActive(false);
        }
        if (gyroActive) {
          gyroActive = false;
          setGyro(false);
          setGyroListener(null);
        }
        clearTimeout(moveTimer);
        clearTimeout(scrollTimer);
        if (dragActive) {
          dragActive = false;
          fetch(`${API}/remote?op=up&btn=left`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS }).catch(() => {});
        }
      },
    };
  },
};
