(() => {
  "use strict";

  const canvas = document.getElementById("display-canvas");
  const ctx = canvas.getContext("2d", { alpha: false, desynchronized: true });
  const landingOverlay = document.getElementById("landing-overlay");
  const insecureBanner = document.getElementById("insecure-banner");
  const btnSwitchHttps = document.getElementById("btn-switch-https");
  const btnStart = document.getElementById("btn-start");
  const statsHud = document.getElementById("stats-hud");
  const miniPill = document.getElementById("mini-pill");
  const pillToggleExpand = document.getElementById("pill-toggle-expand");
  const pillControls = document.getElementById("pill-controls");
  const btnMode = document.getElementById("btn-mode");
  const btnKeyboard = document.getElementById("btn-keyboard");
  const mobileKbdInput = document.getElementById("mobile-kbd-input");
  const btnFps = document.getElementById("btn-fps");
  const btnBitrate = document.getElementById("btn-bitrate");
  const btnStats = document.getElementById("btn-stats");
  const btnReconnect = document.getElementById("btn-reconnect");
  const btnExit = document.getElementById("btn-exit");
  const toastEl = document.getElementById("toast");

  const statRes = document.getElementById("stat-res");
  const statFps = document.getElementById("stat-fps");
  const statEnc = document.getElementById("stat-enc");
  const statRtt = document.getElementById("stat-rtt");
  const statEncName = document.getElementById("stat-enc-name");

  const btnWebLang = document.getElementById("btn-web-lang");
  const txtWebSubtitle = document.getElementById("txt-web-subtitle");
  const txtInsecureTitle = document.getElementById("txt-insecure-title");
  const txtInsecureDesc = document.getElementById("txt-insecure-desc");
  const txtHint1 = document.getElementById("txt-hint-1");
  const txtHint2 = document.getElementById("txt-hint-2");
  const txtHint3 = document.getElementById("txt-hint-3");
  const pinBox = document.getElementById("pin-box");
  const inputPin = document.getElementById("input-pin");
  const urlToken = new URLSearchParams(window.location.search).get("token") || "";
  if (urlToken) {
    inputPin.value = urlToken;
    pinBox.classList.add("hidden");
  }
  let webLang = navigator.language && navigator.language.toLowerCase().startsWith("vi") ? "vi" : "en";

  const WEB_STRINGS = {
    vi: {
      subtitle: "Biến điện thoại thành màn hình phụ Windows 11 giải mã phần cứng, không cần cài app.",
      start: "Bắt đầu màn hình phụ",
      pinPlaceholder: "Mã PIN (6 số)",
      insecureTitle: "WebCodecs cần HTTPS khi dùng qua Wi-Fi",
      insecureDesc: "Chuyển sang cổng HTTPS hoặc cắm cáp USB ADB (http://localhost:8080).",
      switchHttps: "Chuyển sang HTTPS (:8443)",
      hint1: "Toàn màn hình",
      hint2: "Khóa xoay ngang",
      hint3: "Giữ sáng màn",
      connecting: "Đang kết nối...",
      connected: "Đã kết nối",
      reconnecting: "Đang kết nối lại...",
      rejected: "Kết nối bị từ chối — kiểm tra mã PIN hoặc Firewall",
      kbdOn: "Bàn phím: BẬT",
      kbdOff: "Bàn phím: TẮT",
      kbdBtnOn: "⌨ Bật",
      kbdBtnOff: "⌨ Tắt",
      modeView: "Chỉ xem",
      modeTrackpad: "Trackpad",
      modeTouch: "Chạm",
      hintView: "Chỉ xem (Đã khóa cảm ứng)",
      hintTrackpad: "Trackpad (1 ngón rê, chạm=click, 2 ngón=cuộn/chuột phải)",
      hintTouch: "Chạm trực tiếp (Giữ 1 ngón + chạm ngón 2 = chuột phải)",
      fpsToast: "Tần số quét",
      bitrateToast: "Chất lượng"
    },
    en: {
      subtitle: "Turn your device into a hardware-decoded extended Windows 11 monitor with zero app installation.",
      start: "Start Extended Display",
      pinPlaceholder: "PIN (6 digits)",
      insecureTitle: "WebCodecs requires HTTPS over Wi-Fi",
      insecureDesc: "Switch to the secure port or connect via USB ADB (http://localhost:8080).",
      switchHttps: "Switch to HTTPS (:8443)",
      hint1: "Fullscreen",
      hint2: "Landscape Lock",
      hint3: "Wake Lock",
      connecting: "Connecting...",
      connected: "Connected",
      reconnecting: "Reconnecting...",
      rejected: "Connection rejected — check PIN or Firewall",
      kbdOn: "Keyboard ON",
      kbdOff: "Keyboard OFF",
      kbdBtnOn: "⌨ On",
      kbdBtnOff: "⌨ Off",
      modeView: "View Only",
      modeTrackpad: "Trackpad",
      modeTouch: "Touch",
      hintView: "View Only (Input locked)",
      hintTrackpad: "Trackpad (1-finger move, tap=click, 2-finger=scroll/right)",
      hintTouch: "Touch (Hold 1st finger + tap 2nd finger = right-click)",
      fpsToast: "Target FPS",
      bitrateToast: "Bitrate"
    }
  };

  function t(key) {
    return (WEB_STRINGS[webLang] && WEB_STRINGS[webLang][key]) || WEB_STRINGS.en[key] || key;
  }

  function applyWebLang() {
    txtWebSubtitle.textContent = t("subtitle");
    btnStart.textContent = t("start");
    inputPin.placeholder = t("pinPlaceholder");
    txtInsecureTitle.textContent = t("insecureTitle");
    txtInsecureDesc.textContent = t("insecureDesc");
    btnSwitchHttps.textContent = t("switchHttps");
    txtHint1.textContent = t("hint1");
    txtHint2.textContent = t("hint2");
    txtHint3.textContent = t("hint3");
    btnWebLang.textContent = webLang === "vi" ? "VI" : "EN";

    const modeLabels = { view: t("modeView"), trackpad: t("modeTrackpad"), touch: t("modeTouch") };
    btnMode.textContent = modeLabels[inputMode] || t("modeView");
    btnKeyboard.textContent = isMobileKeyboardOpen ? t("kbdBtnOn") : t("kbdBtnOff");
  }

  btnWebLang.addEventListener("click", () => {
    webLang = webLang === "vi" ? "en" : "vi";
    applyWebLang();
  });

  // Secure context check (WebCodecs requires HTTPS on Wi-Fi or localhost on USB)
  const hasWebCodecs = typeof window.VideoDecoder === "function";
  if (!window.isSecureContext || !hasWebCodecs) {
    insecureBanner.classList.remove("hidden");
  }

  btnSwitchHttps.addEventListener("click", () => {
    window.location.href = `https://${window.location.hostname}:8443${window.location.pathname}`;
  });

  // State
  let ws = null;
  let decoder = null;
  let wakeLock = null;
  let isRunning = false;
  let hasConnectedOnce = false;
  let waitingForKeyframe = true;
  let reconnectTimer = null;
  let pingTimer = null;
  let pillFadeTimer = null;

  let videoWidth = 1920;
  let videoHeight = 1080;
  let drawRect = { x: 0, y: 0, w: 1920, h: 1080 };

  const inputModes = ["view", "trackpad", "touch"];
  let inputModeIndex = 0;
  let inputMode = "view"; // "view" | "trackpad" | "touch"
  let isMobileKeyboardOpen = false;
  applyWebLang();
  const fpsPresets = [60, 90, 120];
  let fpsPresetIndex = 0;
  const bitratePresets = [8000, 12000, 20000, 30000];
  let bitratePresetIndex = 1;

  // Metrics
  let decodedFramesCount = 0;
  let lastFpsTick = performance.now();
  let lastEncodeLatencyMs = 0;
  let lastRttMs = 0;
  let activeEncoderName = "--";

  function showToast(msg, durationMs = 2200) {
    toastEl.textContent = msg;
    toastEl.classList.remove("hidden");
    clearTimeout(toastEl._timer);
    if (durationMs > 0) {
      toastEl._timer = setTimeout(() => toastEl.classList.add("hidden"), durationMs);
    }
  }

  function resizeCanvas() {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const targetW = Math.max(1, Math.floor(window.innerWidth * dpr));
    const targetH = Math.max(1, Math.floor(window.innerHeight * dpr));
    if (canvas.width !== targetW || canvas.height !== targetH) {
      canvas.width = targetW;
      canvas.height = targetH;
    }
    computeDrawRect();
  }

  function computeDrawRect() {
    const cw = canvas.width;
    const ch = canvas.height;
    const videoAspect = videoWidth / Math.max(1, videoHeight);
    const canvasAspect = cw / Math.max(1, ch);

    let dw, dh, dx, dy;
    if (canvasAspect > videoAspect) {
      dh = ch;
      dw = Math.round(ch * videoAspect);
      dx = Math.round((cw - dw) / 2);
      dy = 0;
    } else {
      dw = cw;
      dh = Math.round(cw / videoAspect);
      dx = 0;
      dy = Math.round((ch - dh) / 2);
    }
    drawRect = { x: dx, y: dy, w: dw, h: dh };
  }

  window.addEventListener("resize", resizeCanvas);

  async function acquireImmersiveMode() {
    try {
      if (document.documentElement.requestFullscreen && !document.fullscreenElement) {
        await document.documentElement.requestFullscreen({ navigationUI: "hide" });
      }
    } catch { /* ignore if blocked */ }

    try {
      if (screen.orientation && screen.orientation.lock) {
        await screen.orientation.lock("landscape");
      }
    } catch { /* ignore on unsupported browsers */ }

    await requestWakeLock();
  }

  async function requestWakeLock() {
    try {
      if ("wakeLock" in navigator && !wakeLock) {
        wakeLock = await navigator.wakeLock.request("screen");
        wakeLock.addEventListener("release", () => {
          wakeLock = null;
        });
      }
    } catch { /* ignore */ }
  }

  document.addEventListener("visibilitychange", () => {
    if (isRunning && document.visibilityState === "visible") {
      requestWakeLock();
      sendControl({ type: "keyframe" });
    }
  });

  async function initDecoder(codec = "avc1.42E033") {
    closeDecoder();
    if (!hasWebCodecs) {
      showToast("WebCodecs unsupported here. Use HTTPS or USB localhost.", 5000);
      return;
    }

    let hwAccel = "prefer-hardware";
    try {
      const support = await VideoDecoder.isConfigSupported({
        codec,
        hardwareAcceleration: "prefer-hardware",
        optimizeForLatency: true
      });
      if (!support.supported) {
        hwAccel = "no-preference";
      }
    } catch {
      hwAccel = "no-preference";
    }

    waitingForKeyframe = true;
    decoder = new VideoDecoder({
      output: (frame) => {
        try {
          if (frame.displayWidth !== videoWidth || frame.displayHeight !== videoHeight) {
            videoWidth = frame.displayWidth;
            videoHeight = frame.displayHeight;
            computeDrawRect();
            statRes.textContent = `${videoWidth}x${videoHeight}`;
          }

          ctx.fillStyle = "#000000";
          ctx.fillRect(0, 0, canvas.width, canvas.height);
          ctx.drawImage(frame, drawRect.x, drawRect.y, drawRect.w, drawRect.h);

          decodedFramesCount++;
          const now = performance.now();
          if (now - lastFpsTick >= 1000) {
            const fps = (decodedFramesCount * 1000) / (now - lastFpsTick);
            statFps.textContent = `${fps.toFixed(0)} FPS`;
            statEnc.textContent = `Enc ${lastEncodeLatencyMs.toFixed(1)}ms`;
            statRtt.textContent = `RTT ${lastRttMs}ms`;
            statEncName.textContent = activeEncoderName;
            decodedFramesCount = 0;
            lastFpsTick = now;
          }
        } finally {
          frame.close();
        }
      },
      error: (err) => {
        console.error("VideoDecoder error:", err);
        showToast(`Decoder error: ${err?.message || err}`, 3000);
        waitingForKeyframe = true;
        sendControl({ type: "keyframe" });
      }
    });

    decoder.configure({
      codec,
      hardwareAcceleration: hwAccel,
      optimizeForLatency: true
    });
  }

  function closeDecoder() {
    if (decoder) {
      try {
        if (decoder.state !== "closed") decoder.close();
      } catch { /* ignore */ }
      decoder = null;
    }
  }

  function connectWebSocket() {
    clearTimeout(reconnectTimer);
    if (ws) {
      try { ws.close(); } catch { }
      ws = null;
    }

    const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
    const token = encodeURIComponent((inputPin.value || urlToken || "").trim());
    const wsUrl = `${proto}//${window.location.host}/ws?token=${token}`;
    showToast(t("connecting"), 0);

    ws = new WebSocket(wsUrl);
    ws.binaryType = "arraybuffer";

    ws.onopen = async () => {
      hasConnectedOnce = true;
      showToast(t("connected"), 1200);
      await initDecoder();

      const dpr = window.devicePixelRatio || 1;
      const physW = Math.round(Math.max(window.screen.width, window.screen.height) * dpr);
      const physH = Math.round(Math.min(window.screen.width, window.screen.height) * dpr);

      sendControl({
        type: "hello",
        width: physW,
        height: physH,
        dpr,
        refreshRate: fpsPresets[fpsPresetIndex]
      });

      clearInterval(pingTimer);
      pingTimer = setInterval(() => {
        sendControl({ type: "ping", ts: Date.now() });
      }, 2000);
    };

    ws.onmessage = (ev) => {
      if (typeof ev.data === "string") {
        handleServerText(ev.data);
        return;
      }

      handleVideoPacket(ev.data);
    };

    ws.onclose = (ev) => {
      clearInterval(pingTimer);
      closeDecoder();
      if (isRunning) {
        if (ev.code === 1006 && !hasConnectedOnce) {
          showToast(t("rejected"), 3500);
        } else {
          showToast(t("reconnecting"), 0);
        }
        reconnectTimer = setTimeout(connectWebSocket, 1200);
      }
    };

    ws.onerror = () => {
      // onclose will handle reconnect
    };
  }

  function handleServerText(text) {
    let msg;
    try { msg = JSON.parse(text); } catch { return; }

    if (msg.type === "info") {
      videoWidth = msg.width || videoWidth;
      videoHeight = msg.height || videoHeight;
      activeEncoderName = msg.encoder || "--";
      statRes.textContent = `${videoWidth}x${videoHeight}`;
      statEncName.textContent = activeEncoderName;
      computeDrawRect();
      waitingForKeyframe = true;
      if (decoder && decoder.state === "closed") {
        initDecoder(msg.codec || "avc1.42E028");
      }
    } else if (msg.type === "pong") {
      lastRttMs = Math.max(0, Date.now() - (msg.ts || Date.now()));
    }
  }

  function handleVideoPacket(arrayBuffer) {
    if (!decoder || decoder.state !== "configured") return;
    if (arrayBuffer.byteLength <= 13) return;

    const view = new DataView(arrayBuffer);
    const frameType = view.getUint8(0); // 1 = keyframe, 0 = delta
    const isKeyframe = frameType === 1;
    const tsHigh = view.getUint32(1);
    const tsLow = view.getUint32(5);
    const timestampUs = tsHigh * 4294967296 + tsLow;
    const encodeUs = view.getUint32(9);
    lastEncodeLatencyMs = encodeUs / 1000.0;

    if (waitingForKeyframe && !isKeyframe) {
      return;
    }

    // Never drop a single P-frame mid-GOP (would corrupt H.264 reference frames).
    // If decoder falls far behind, reset cleanly and wait for the next IDR keyframe.
    if (decoder.decodeQueueSize > 8 && !isKeyframe) {
      waitingForKeyframe = true;
      sendControl({ type: "keyframe" });
      return;
    }

    waitingForKeyframe = false;
    const payload = new Uint8Array(arrayBuffer, 13);

    try {
      const chunk = new EncodedVideoChunk({
        type: isKeyframe ? "key" : "delta",
        timestamp: timestampUs,
        data: payload
      });
      decoder.decode(chunk);
    } catch (err) {
      console.warn("Decode chunk failed:", err);
      waitingForKeyframe = true;
      sendControl({ type: "keyframe" });
    }
  }

  function sendControl(obj) {
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify(obj));
    }
  }

  // --- Pointer / Touch & Trackpad Handling (9remote-style modes) ---
  const activePointers = new Map();
  let trackpadSubPixelX = 0;
  let trackpadSubPixelY = 0;
  let lastTapTime = 0;

  let gestureState = {
    mode: "none", // "single" | "scroll"
    startX: 0,
    startY: 0,
    lastX: 0,
    lastY: 0,
    startTime: 0,
    moved: false,
    isDraggingLeft: false,
    longPressFired: false,
    longPressTimer: null,
    rightClickX: 0,
    rightClickY: 0,
    secondFingerTapped: false,
    twoFingerLastMidX: 0,
    twoFingerLastMidY: 0,
    twoFingerMoved: false
  };

  function clientToNormalized(clientX, clientY) {
    const rect = canvas.getBoundingClientRect();
    const scaleX = canvas.width / Math.max(1, rect.width);
    const scaleY = canvas.height / Math.max(1, rect.height);
    const cx = (clientX - rect.left) * scaleX;
    const cy = (clientY - rect.top) * scaleY;

    const nx = (cx - drawRect.x) / Math.max(1, drawRect.w);
    const ny = (cy - drawRect.y) / Math.max(1, drawRect.h);
    return {
      x: Number.isFinite(nx) ? Math.min(1, Math.max(0, nx)) : 0,
      y: Number.isFinite(ny) ? Math.min(1, Math.max(0, ny)) : 0
    };
  }

  function clearLongPress() {
    if (gestureState.longPressTimer) {
      clearTimeout(gestureState.longPressTimer);
      gestureState.longPressTimer = null;
    }
  }

  function vibrateShort() {
    if (navigator.vibrate) {
      try { navigator.vibrate(25); } catch { }
    }
  }

  let lastMoveSentAt = 0;
  let isHoveringWebUi = false;

  miniPill.addEventListener("pointerenter", () => {
    isHoveringWebUi = true;
    miniPill.classList.add("expanded");
  });
  miniPill.addEventListener("pointerleave", () => {
    isHoveringWebUi = false;
    resetPillFade();
  });

  function isOverWebUi(e) {
    if (isHoveringWebUi) return true;
    if (e.target !== canvas) return true;
    const r = miniPill.getBoundingClientRect();
    return e.clientX >= r.left - 6 && e.clientX <= r.right + 6 &&
           e.clientY >= r.top - 6 && e.clientY <= r.bottom + 6;
  }

  canvas.addEventListener("contextmenu", (e) => {
    if (isRunning) e.preventDefault();
  });

  canvas.addEventListener("dragstart", (e) => e.preventDefault());
  canvas.addEventListener("selectstart", (e) => e.preventDefault());

  canvas.addEventListener("wheel", (e) => {
    if (!isRunning || inputMode === "view") return;
    e.preventDefault();
    sendControl({
      type: "input",
      action: "scroll",
      wheelX: -e.deltaX / 100,
      wheelY: -e.deltaY / 100
    });
  }, { passive: false });

  canvas.addEventListener("pointerdown", (e) => {
    if (!isRunning || inputMode === "view" || isOverWebUi(e)) return;
    e.preventDefault();

    try { canvas.setPointerCapture(e.pointerId); } catch { /* ignore */ }

    // Mouse / Laptop Touchpad on PC browsers
    if (e.pointerType === "mouse") {
      if (inputMode === "trackpad") {
        // If Pointer Lock is active, mouse clicks act directly as left/right clicks
        if (document.pointerLockElement === canvas) {
          if (e.button === 2) sendControl({ type: "input", action: "trackpad_rightclick" });
          else if (e.button === 0) sendControl({ type: "input", action: "leftdown" });
          return;
        }
        if (e.button === 2) {
          sendControl({ type: "input", action: "trackpad_rightclick" });
        } else if (e.button === 0) {
          activePointers.set(e.pointerId, { x: e.clientX, y: e.clientY, isMouse: true });
          gestureState.mode = "single";
          gestureState.startX = e.clientX;
          gestureState.startY = e.clientY;
          gestureState.lastX = e.clientX;
          gestureState.lastY = e.clientY;
          gestureState.startTime = performance.now();
          gestureState.moved = false;
        }
      } else {
        const norm = clientToNormalized(e.clientX, e.clientY);
        if (e.button === 2) {
          sendControl({ type: "input", action: "rightclick", x: norm.x, y: norm.y });
        } else if (e.button === 0) {
          activePointers.set(e.pointerId, { x: e.clientX, y: e.clientY, isMouse: true });
          sendControl({ type: "input", action: "down", x: norm.x, y: norm.y });
        }
      }
      return;
    }

    activePointers.set(e.pointerId, { x: e.clientX, y: e.clientY });

    if (activePointers.size === 1) {
      const now = performance.now();
      gestureState.mode = "single";
      gestureState.startX = e.clientX;
      gestureState.startY = e.clientY;
      gestureState.lastX = e.clientX;
      gestureState.lastY = e.clientY;
      gestureState.startTime = now;
      gestureState.moved = false;
      gestureState.longPressFired = false;
      gestureState.isDraggingLeft = false;

      if (inputMode === "touch") {
        const norm = clientToNormalized(e.clientX, e.clientY);
        sendControl({ type: "input", action: "down", x: norm.x, y: norm.y });

        clearLongPress();
        gestureState.longPressTimer = setTimeout(() => {
          if (!gestureState.moved && activePointers.size === 1) {
            const curNorm = clientToNormalized(gestureState.lastX, gestureState.lastY);
            sendControl({ type: "input", action: "rightclick", x: curNorm.x, y: curNorm.y });
            gestureState.longPressFired = true;
            gestureState.moved = true;
            vibrateShort();
          }
        }, 450);
      } else if (inputMode === "trackpad") {
        // Double-tap & hold -> Left-button drag
        if (now - lastTapTime < 280) {
          gestureState.isDraggingLeft = true;
          sendControl({ type: "input", action: "leftdown" });
          vibrateShort();
        } else {
          clearLongPress();
          gestureState.longPressTimer = setTimeout(() => {
            if (!gestureState.moved && activePointers.size === 1) {
              sendControl({ type: "input", action: "trackpad_rightclick" });
              gestureState.longPressFired = true;
              gestureState.moved = true;
              vibrateShort();
            }
          }, 450);
        }
      }
    } else if (activePointers.size === 2) {
      clearLongPress();
      if (gestureState.isDraggingLeft) {
        sendControl({ type: "input", action: "leftup" });
        gestureState.isDraggingLeft = false;
      }
      if (inputMode === "touch" && gestureState.mode === "single") {
        // Release left-button at Finger 1's coordinates and save them for 2nd-finger right-click
        const firstNorm = clientToNormalized(gestureState.lastX, gestureState.lastY);
        sendControl({ type: "input", action: "up", x: firstNorm.x, y: firstNorm.y });
        gestureState.rightClickX = gestureState.lastX;
        gestureState.rightClickY = gestureState.lastY;
        gestureState.secondFingerTapped = true;
      }
      gestureState.mode = "scroll";
      gestureState.twoFingerMoved = false;
      const pts = Array.from(activePointers.values());
      gestureState.twoFingerLastMidX = (pts[0].x + pts[1].x) / 2;
      gestureState.twoFingerLastMidY = (pts[0].y + pts[1].y) / 2;
    }
  });

  canvas.addEventListener("pointermove", (e) => {
    if (!isRunning || inputMode === "view") return;
    if (e.pointerType === "mouse" && isOverWebUi(e) && !activePointers.has(e.pointerId)) return;

    // Mouse / Laptop Touchpad on PC browsers
    if (e.pointerType === "mouse") {
      const now = performance.now();
      if (now - lastMoveSentAt < 6) return;
      lastMoveSentAt = now;

      if (inputMode === "trackpad") {
        if (document.pointerLockElement === canvas) {
          trackpadSubPixelX += (e.movementX || 0) * 1.4;
          trackpadSubPixelY += (e.movementY || 0) * 1.4;
          const sendDx = Math.trunc(trackpadSubPixelX);
          const sendDy = Math.trunc(trackpadSubPixelY);
          if (sendDx !== 0 || sendDy !== 0) {
            trackpadSubPixelX -= sendDx;
            trackpadSubPixelY -= sendDy;
            sendControl({ type: "input", action: "trackpad_move", dx: sendDx, dy: sendDy });
          }
          return;
        }
        if (!activePointers.has(e.pointerId)) return;
        const dx = e.clientX - gestureState.lastX;
        const dy = e.clientY - gestureState.lastY;
        if (Math.hypot(e.clientX - gestureState.startX, e.clientY - gestureState.startY) > 4) {
          gestureState.moved = true;
        }
        trackpadSubPixelX += dx * 1.6;
        trackpadSubPixelY += dy * 1.6;
        const sendDx = Math.trunc(trackpadSubPixelX);
        const sendDy = Math.trunc(trackpadSubPixelY);
        if (sendDx !== 0 || sendDy !== 0) {
          trackpadSubPixelX -= sendDx;
          trackpadSubPixelY -= sendDy;
          sendControl({ type: "input", action: "trackpad_move", dx: sendDx, dy: sendDy });
        }
        gestureState.lastX = e.clientX;
        gestureState.lastY = e.clientY;
      } else {
        const norm = clientToNormalized(e.clientX, e.clientY);
        sendControl({ type: "input", action: "move", x: norm.x, y: norm.y });
      }
      return;
    }

    if (!activePointers.has(e.pointerId)) return;
    e.preventDefault();
    activePointers.set(e.pointerId, { x: e.clientX, y: e.clientY });

    const now = performance.now();
    if (now - lastMoveSentAt < 6) return;
    lastMoveSentAt = now;

    if (gestureState.mode === "single" && activePointers.size === 1) {
      const dx = e.clientX - gestureState.lastX;
      const dy = e.clientY - gestureState.lastY;
      const totalDist = Math.hypot(e.clientX - gestureState.startX, e.clientY - gestureState.startY);
      if (totalDist > 7) {
        gestureState.moved = true;
        clearLongPress();
      }

      if (inputMode === "touch") {
        const norm = clientToNormalized(e.clientX, e.clientY);
        sendControl({ type: "input", action: "move", x: norm.x, y: norm.y });
      } else if (inputMode === "trackpad") {
        // Laptop-like touchpad acceleration + sub-pixel accumulation
        const speed = Math.hypot(dx, dy);
        const gain = speed > 18 ? 2.4 : speed > 7 ? 1.8 : 1.35;
        trackpadSubPixelX += dx * gain;
        trackpadSubPixelY += dy * gain;
        const sendDx = Math.trunc(trackpadSubPixelX);
        const sendDy = Math.trunc(trackpadSubPixelY);
        if (sendDx !== 0 || sendDy !== 0) {
          trackpadSubPixelX -= sendDx;
          trackpadSubPixelY -= sendDy;
          sendControl({
            type: "input",
            action: "trackpad_move",
            dx: sendDx,
            dy: sendDy
          });
        }
      }

      gestureState.lastX = e.clientX;
      gestureState.lastY = e.clientY;
    } else if (gestureState.mode === "scroll" && activePointers.size === 2) {
      const pts = Array.from(activePointers.values());
      const midX = (pts[0].x + pts[1].x) / 2;
      const midY = (pts[0].y + pts[1].y) / 2;
      const dMidX = midX - gestureState.twoFingerLastMidX;
      const dMidY = midY - gestureState.twoFingerLastMidY;

      if (Math.hypot(dMidX, dMidY) > 3) {
        gestureState.twoFingerMoved = true;
        sendControl({
          type: "input",
          action: "scroll",
          wheelX: -dMidX / 40,
          wheelY: dMidY / 40
        });
        gestureState.twoFingerLastMidX = midX;
        gestureState.twoFingerLastMidY = midY;
      }
    }
  });

  function handlePointerEnd(e) {
    if (e.pointerType === "mouse") {
      if (inputMode === "trackpad" && document.pointerLockElement === canvas) {
        if (e.button === 0) sendControl({ type: "input", action: "leftup" });
        return;
      }
      if (activePointers.has(e.pointerId)) {
        activePointers.delete(e.pointerId);
        if (inputMode === "trackpad") {
          if (!gestureState.moved && performance.now() - gestureState.startTime < 260) {
            sendControl({ type: "input", action: "leftclick" });
          }
        } else {
          const norm = clientToNormalized(e.clientX, e.clientY);
          sendControl({ type: "input", action: "up", x: norm.x, y: norm.y });
        }
      }
      return;
    }

    if (!activePointers.has(e.pointerId)) return;
    activePointers.delete(e.pointerId);
    clearLongPress();

    if (gestureState.mode === "single") {
      if (inputMode === "touch") {
        const norm = clientToNormalized(e.clientX, e.clientY);
        sendControl({ type: "input", action: "up", x: norm.x, y: norm.y });
      } else if (inputMode === "trackpad") {
        if (gestureState.isDraggingLeft) {
          sendControl({ type: "input", action: "leftup" });
          gestureState.isDraggingLeft = false;
        } else {
          const elapsed = performance.now() - gestureState.startTime;
          if (!gestureState.moved && !gestureState.longPressFired && elapsed < 260) {
            sendControl({ type: "input", action: "leftclick" });
            lastTapTime = performance.now();
          }
        }
      }
    } else if (gestureState.mode === "scroll") {
      if (inputMode === "touch" && gestureState.secondFingerTapped && !gestureState.twoFingerMoved) {
        // 2nd finger lifted without scrolling -> Right-click at Finger 1's position immediately
        const rcNorm = clientToNormalized(gestureState.rightClickX, gestureState.rightClickY);
        sendControl({ type: "input", action: "rightclick", x: rcNorm.x, y: rcNorm.y });
        gestureState.secondFingerTapped = false;
        vibrateShort();
      } else if (activePointers.size === 0 && !gestureState.twoFingerMoved && inputMode === "trackpad") {
        sendControl({ type: "input", action: "trackpad_rightclick" });
        vibrateShort();
      }
    }

    if (activePointers.size === 0) {
      gestureState.mode = "none";
    }
  }

  canvas.addEventListener("pointerup", handlePointerEnd);
  canvas.addEventListener("pointercancel", handlePointerEnd);

  // --- Floating Mini-Pill Controls ---
  function resetPillFade() {
    clearTimeout(pillFadeTimer);
    miniPill.classList.add("expanded");
    pillFadeTimer = setTimeout(() => {
      if (pillControls.classList.contains("hidden")) {
        miniPill.classList.remove("expanded");
      }
    }, 3000);
  }

  pillToggleExpand.addEventListener("click", (e) => {
    e.stopPropagation();
    const isHidden = pillControls.classList.toggle("hidden");
    if (isHidden) {
      resetPillFade();
    } else {
      clearTimeout(pillFadeTimer);
      miniPill.classList.add("expanded");
    }
  });

  btnMode.addEventListener("click", (e) => {
    e.stopPropagation();
    inputModeIndex = (inputModeIndex + 1) % inputModes.length;
    inputMode = inputModes[inputModeIndex];
    const labels = {
      trackpad: t("modeTrackpad"),
      touch: t("modeTouch"),
      view: t("modeView")
    };
    const hints = {
      trackpad: t("hintTrackpad"),
      touch: t("hintTouch"),
      view: t("hintView")
    };
    btnMode.textContent = labels[inputMode];
    btnMode.classList.toggle("active", inputMode !== "view");
    showToast(hints[inputMode], 2600);
  });

  // --- Keyboard Support (PC physical keyboard + Mobile soft keyboard button) ---
  let isComposing = false;
  const KBD_SENTINEL = " ";

  function resetMobileKbdInput() {
    mobileKbdInput.value = KBD_SENTINEL;
    try { mobileKbdInput.setSelectionRange(1, 1); } catch { /* ignore */ }
  }

  function setMobileKeyboardUi(open) {
    isMobileKeyboardOpen = open;
    btnKeyboard.textContent = open ? t("kbdBtnOn") : t("kbdBtnOff");
    btnKeyboard.classList.toggle("active", open);
  }

  function sendKeyTap(key, code) {
    sendControl({ type: "input", action: "keydown", key, code });
    sendControl({ type: "input", action: "keyup", key, code });
  }

  // Capture state on pointerdown before blur fires on mobileKbdInput
  let kbdWasOpenOnDown = false;
  btnKeyboard.addEventListener("pointerdown", (e) => {
    e.stopPropagation();
    kbdWasOpenOnDown = isMobileKeyboardOpen;
  });

  btnKeyboard.addEventListener("click", (e) => {
    e.stopPropagation();
    if (kbdWasOpenOnDown) {
      mobileKbdInput.blur();
      setMobileKeyboardUi(false);
      showToast(t("kbdOff"), 1200);
      return;
    }

    if (inputMode === "view") {
      inputModeIndex = 1;
      inputMode = "trackpad";
      btnMode.textContent = t("modeTrackpad");
      btnMode.classList.add("active");
    }

    resetMobileKbdInput();
    mobileKbdInput.focus({ preventScroll: true });
    setMobileKeyboardUi(true);
    showToast(t("kbdOn"), 1200);
  });

  mobileKbdInput.addEventListener("blur", () => {
    setMobileKeyboardUi(false);
  });

  mobileKbdInput.addEventListener("compositionstart", () => {
    isComposing = true;
  });

  mobileKbdInput.addEventListener("compositionend", () => {
    isComposing = false;
    handleMobileInputDiff();
  });

  mobileKbdInput.addEventListener("input", (e) => {
    if (e.inputType === "deleteContentBackward") {
      sendKeyTap("Backspace", "Backspace");
      resetMobileKbdInput();
      return;
    }
    if (e.inputType === "insertLineBreak") {
      sendKeyTap("Enter", "Enter");
      resetMobileKbdInput();
      return;
    }
    if (isComposing) return;
    handleMobileInputDiff();
  });

  function handleMobileInputDiff() {
    const val = mobileKbdInput.value;
    if (val.length === 0) {
      sendKeyTap("Backspace", "Backspace");
      resetMobileKbdInput();
      return;
    }
    if (val.startsWith(KBD_SENTINEL)) {
      const typed = val.slice(KBD_SENTINEL.length);
      if (typed) sendControl({ type: "input", action: "text", text: typed });
    } else {
      sendControl({ type: "input", action: "text", text: val });
    }
    resetMobileKbdInput();
  }

  mobileKbdInput.addEventListener("keydown", (e) => {
    if (!isRunning) return;
    if (["Backspace", "Enter", "Escape", "Tab", "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(e.key)) {
      e.preventDefault();
      sendControl({ type: "input", action: "keydown", key: e.key, code: e.code });
    }
  });

  mobileKbdInput.addEventListener("keyup", (e) => {
    if (!isRunning) return;
    if (["Backspace", "Enter", "Escape", "Tab", "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(e.key)) {
      e.preventDefault();
      sendControl({ type: "input", action: "keyup", key: e.key, code: e.code });
    }
  });

  window.addEventListener("keydown", (e) => {
    if (!isRunning || inputMode === "view") return;
    if (document.activeElement === mobileKbdInput || document.activeElement === inputPin) return;
    e.preventDefault();
    sendControl({ type: "input", action: "keydown", key: e.key, code: e.code });
  });

  window.addEventListener("keyup", (e) => {
    if (!isRunning || inputMode === "view") return;
    if (document.activeElement === mobileKbdInput || document.activeElement === inputPin) return;
    e.preventDefault();
    sendControl({ type: "input", action: "keyup", key: e.key, code: e.code });
  });

  btnFps.addEventListener("click", (e) => {
    e.stopPropagation();
    fpsPresetIndex = (fpsPresetIndex + 1) % fpsPresets.length;
    const fps = fpsPresets[fpsPresetIndex];
    btnFps.textContent = `${fps} FPS`;
    sendControl({ type: "config", fps });
    showToast(`${t("fpsToast")}: ${fps}`);
  });

  btnBitrate.addEventListener("click", (e) => {
    e.stopPropagation();
    bitratePresetIndex = (bitratePresetIndex + 1) % bitratePresets.length;
    const kbps = bitratePresets[bitratePresetIndex];
    btnBitrate.textContent = `${Math.round(kbps / 1000)}M`;
    sendControl({ type: "config", bitrateKbps: kbps });
    showToast(`${t("bitrateToast")}: ${Math.round(kbps / 1000)} Mbps`);
  });

  btnStats.addEventListener("click", (e) => {
    e.stopPropagation();
    const hidden = statsHud.classList.toggle("hidden");
    btnStats.classList.toggle("active", !hidden);
  });

  btnReconnect.addEventListener("click", (e) => {
    e.stopPropagation();
    connectWebSocket();
  });

  btnExit.addEventListener("click", async (e) => {
    e.stopPropagation();
    isRunning = false;
    clearTimeout(reconnectTimer);
    clearInterval(pingTimer);
    if (ws) {
      try { ws.close(); } catch { }
      ws = null;
    }
    closeDecoder();

    if (wakeLock) {
      try { await wakeLock.release(); } catch { }
      wakeLock = null;
    }
    if (document.fullscreenElement) {
      try { await document.exitFullscreen(); } catch { }
    }

    miniPill.classList.add("hidden");
    statsHud.classList.add("hidden");
    landingOverlay.classList.remove("hidden");
  });

  btnStart.addEventListener("click", async () => {
    isRunning = true;
    landingOverlay.classList.add("hidden");
    miniPill.classList.remove("hidden");
    resizeCanvas();
    await acquireImmersiveMode();
    connectWebSocket();
  });
})();
