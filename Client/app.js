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
  const btnAudio = document.getElementById("btn-audio");
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
  const audioOnlyBanner = document.getElementById("audio-only-banner");
  const txtAudioOnlyTitle = document.getElementById("txt-audio-only-title");
  const txtAudioOnlySub = document.getElementById("txt-audio-only-sub");
  const inputDeviceName = document.getElementById("input-device-name");
  const txtDeviceNameLabel = document.getElementById("txt-device-name-label");
  const pairStatusBox = document.getElementById("pair-status-box");
  const pairSpinner = document.getElementById("pair-spinner");
  const txtPairStatus = document.getElementById("txt-pair-status");

  const rawUrlToken = new URLSearchParams(window.location.search).get("token") || "";
  if (rawUrlToken) {
    try { sessionStorage.setItem("pd_token", rawUrlToken); } catch { /* ignore */ }
    try { window.history.replaceState({}, document.title, window.location.pathname); } catch { /* ignore */ }
  }
  let storedToken = "";
  try { storedToken = sessionStorage.getItem("pd_token") || ""; } catch { /* ignore */ }
  const bootstrapToken = (rawUrlToken || storedToken).trim();

  function randomHex(byteLen) {
    const bytes = new Uint8Array(byteLen);
    if (window.crypto && window.crypto.getRandomValues) {
      window.crypto.getRandomValues(bytes);
    } else {
      for (let i = 0; i < byteLen; i++) bytes[i] = Math.floor(Math.random() * 256);
    }
    return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
  }

  function detectDefaultDeviceName() {
    const ua = navigator.userAgent || "";
    let os = "Device";
    if (/Android/i.test(ua)) os = "Android";
    else if (/iPhone/i.test(ua)) os = "iPhone";
    else if (/iPad/i.test(ua)) os = "iPad";
    else if (/Windows/i.test(ua)) os = "Windows PC";
    else if (/Mac OS X|Macintosh/i.test(ua)) os = "Mac";
    else if (/Linux/i.test(ua)) os = "Linux";

    let browser = "Browser";
    if (/Edg/i.test(ua)) browser = "Edge";
    else if (/Chrome|CriOS/i.test(ua)) browser = "Chrome";
    else if (/Firefox/i.test(ua)) browser = "Firefox";
    else if (/Safari/i.test(ua)) browser = "Safari";

    return `${os} • ${browser}`;
  }

  function getOrCreateStorage(key, fallbackFn) {
    try {
      const existing = localStorage.getItem(key);
      if (existing && existing.trim()) return existing.trim();
      const val = fallbackFn();
      localStorage.setItem(key, val);
      return val;
    } catch {
      return fallbackFn();
    }
  }

  const deviceId = getOrCreateStorage("pd_device_id", () => randomHex(16));
  const deviceSecret = getOrCreateStorage("pd_device_secret", () => randomHex(32));
  inputDeviceName.value = getOrCreateStorage("pd_device_name", detectDefaultDeviceName);

  function setPairStatus(mode, text) {
    if (!mode) {
      pairStatusBox.classList.add("hidden");
      pairStatusBox.classList.remove("pending", "error");
      return;
    }
    pairStatusBox.classList.remove("hidden", "pending", "error");
    pairStatusBox.classList.add(mode);
    pairSpinner.classList.toggle("hidden", mode !== "pending");
    txtPairStatus.textContent = text;
  }

  let webLang = navigator.language && navigator.language.toLowerCase().startsWith("vi") ? "vi" : "en";

  const WEB_STRINGS = {
    vi: {
      subtitle: "Biến điện thoại thành màn hình phụ Windows 11 giải mã phần cứng, không cần cài app.",
      start: "Kết nối màn hình phụ",
      deviceNameLabel: "Tên thiết bị hiển thị trên Host",
      deviceNamePlaceholder: "Nhập tên thiết bị...",
      waitingApproval: "Đang chờ máy Host chấp nhận kết nối...",
      denied: "Host đã từ chối yêu cầu kết nối của thiết bị này",
      banned: "Thiết bị này đã bị Host chặn (Banned)",
      insecureTitle: "WebCodecs cần HTTPS khi dùng qua Wi-Fi",
      insecureDesc: "Chuyển sang cổng HTTPS hoặc cắm cáp USB ADB (http://localhost:8080).",
      switchHttps: "Chuyển sang HTTPS (:8443)",
      hint1: "Toàn màn hình",
      hint2: "Khóa xoay ngang",
      hint3: "Giữ sáng màn",
      connecting: "Đang kết nối...",
      connected: "Đã kết nối",
      reconnecting: "Đang kết nối lại...",
      rejected: "Kết nối bị từ chối bởi Host",
      rateLimited: "Thử kết nối quá nhiều lần — tạm khóa 5 phút",
      kbdOn: "Bàn phím: BẬT",
      kbdOff: "Bàn phím: TẮT",
      kbdBtnOn: "⌨ Bật",
      kbdBtnOff: "⌨ Tắt",
      audioBtnOn: "🔊 Bật",
      audioBtnOff: "🔇 Tắt",
      audioOn: "Âm thanh: BẬT",
      audioOff: "Âm thanh: TẮT",
      audioOnlyTitle: "Chỉ phát âm thanh",
      audioOnlySub: "Host đang tắt truyền hình ảnh",
      inputLockedByHost: "Host đã khóa điều khiển chuột & bàn phím",
      inputUnlockedByHost: "Host đã mở lại điều khiển chuột & bàn phím",
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
      start: "Connect Extended Display",
      deviceNameLabel: "Device Name shown on Host",
      deviceNamePlaceholder: "Enter device name...",
      waitingApproval: "Waiting for Host approval...",
      denied: "Host denied this connection request",
      banned: "This device has been banned by Host",
      insecureTitle: "WebCodecs requires HTTPS over Wi-Fi",
      insecureDesc: "Switch to the secure port or connect via USB ADB (http://localhost:8080).",
      switchHttps: "Switch to HTTPS (:8443)",
      hint1: "Fullscreen",
      hint2: "Landscape Lock",
      hint3: "Wake Lock",
      connecting: "Connecting...",
      connected: "Connected",
      reconnecting: "Reconnecting...",
      rejected: "Connection rejected by Host",
      rateLimited: "Too many failed attempts — locked for 5 min",
      kbdOn: "Keyboard ON",
      kbdOff: "Keyboard OFF",
      kbdBtnOn: "⌨ On",
      kbdBtnOff: "⌨ Off",
      audioBtnOn: "🔊 On",
      audioBtnOff: "🔇 Off",
      audioOn: "Audio ON",
      audioOff: "Audio OFF",
      audioOnlyTitle: "Audio Only",
      audioOnlySub: "Video stream is paused on host",
      inputLockedByHost: "Host locked remote mouse & keyboard",
      inputUnlockedByHost: "Host unlocked remote mouse & keyboard",
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
    if (txtDeviceNameLabel) txtDeviceNameLabel.textContent = t("deviceNameLabel");
    if (inputDeviceName) inputDeviceName.placeholder = t("deviceNamePlaceholder");
    txtInsecureTitle.textContent = t("insecureTitle");
    txtInsecureDesc.textContent = t("insecureDesc");
    btnSwitchHttps.textContent = t("switchHttps");
    txtHint1.textContent = t("hint1");
    txtHint2.textContent = t("hint2");
    txtHint3.textContent = t("hint3");
    btnWebLang.textContent = webLang === "vi" ? "VI" : "EN";
    if (txtAudioOnlyTitle) txtAudioOnlyTitle.textContent = t("audioOnlyTitle");
    if (txtAudioOnlySub) txtAudioOnlySub.textContent = t("audioOnlySub");

    const modeLabels = { view: t("modeView"), trackpad: t("modeTrackpad"), touch: t("modeTouch") };
    btnMode.textContent = modeLabels[inputMode] || t("modeView");
    btnKeyboard.textContent = isMobileKeyboardOpen ? t("kbdBtnOn") : t("kbdBtnOff");
    if (btnAudio) {
      btnAudio.textContent = audioEnabled ? t("audioBtnOn") : t("audioBtnOff");
      btnAudio.classList.toggle("active", audioEnabled);
    }
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
  let hostVideoEnabled = true;
  let hostAllowInput = true;
  let pendingVideoFrame = null;
  let rafScheduled = false;
  let audioEnabled = true;
  let audioCtx = null;
  let audioGain = null;
  let audioWorkletNode = null;
  let useAudioWorklet = false;
  let audioNextPlayTime = 0;
  const AUDIO_TARGET_BUFFER_SEC = 0.045;
  const AUDIO_MAX_BUFFER_SEC = 0.16;

  const AUDIO_WORKLET_CODE = `
    class PcmRingProcessor extends AudioWorkletProcessor {
      constructor() {
        super();
        this.capacity = 24000;
        this.left = new Float32Array(this.capacity);
        this.right = new Float32Array(this.capacity);
        this.readIdx = 0;
        this.writeIdx = 0;
        this.available = 0;
        this.started = false;
        this.targetPrefill = Math.round(sampleRate * 0.020);
        this.maxBuffered = Math.round(sampleRate * 0.100);
        this.port.onmessage = (e) => {
          const msg = e.data;
          if (!msg) return;
          if (msg.type === "reset") {
            this.readIdx = 0;
            this.writeIdx = 0;
            this.available = 0;
            this.started = false;
            return;
          }
          if (!msg.buffer || msg.buffer.byteLength <= 14) return;
          const view = new DataView(msg.buffer);
          const pktRate = msg.sampleRate || sampleRate;
          const ch = Math.min(2, Math.max(1, msg.channels || 2));
          const frameCount = Math.floor((msg.buffer.byteLength - 14) / (ch * 2));
          if (frameCount <= 0) return;
          const ratio = pktRate / sampleRate;
          const outCount = ratio === 1 ? frameCount : Math.max(1, Math.round(frameCount / ratio));
          for (let i = 0; i < outCount; i++) {
            const srcPos = ratio === 1 ? i : Math.min(frameCount - 1, Math.floor(i * ratio));
            let l, r;
            if (ch === 1) {
              l = view.getInt16(14 + srcPos * 2, true) / 32768.0;
              r = l;
            } else {
              const off = 14 + srcPos * 4;
              l = view.getInt16(off, true) / 32768.0;
              r = view.getInt16(off + 2, true) / 32768.0;
            }
            if (this.available >= this.capacity) {
              this.readIdx = (this.readIdx + 1) % this.capacity;
              this.available--;
            }
            this.left[this.writeIdx] = l;
            this.right[this.writeIdx] = r;
            this.writeIdx = (this.writeIdx + 1) % this.capacity;
            this.available++;
          }
          if (this.available > this.maxBuffered) {
            const drop = this.available - this.targetPrefill;
            this.readIdx = (this.readIdx + drop) % this.capacity;
            this.available -= drop;
          }
        };
      }
      process(inputs, outputs) {
        const output = outputs[0];
        if (!output || output.length === 0) return true;
        const outL = output[0];
        const outR = output[1] || output[0];
        if (!this.started) {
          if (this.available < this.targetPrefill) {
            outL.fill(0);
            if (outR !== outL) outR.fill(0);
            return true;
          }
          this.started = true;
        }
        const n = outL.length;
        for (let i = 0; i < n; i++) {
          if (this.available > 0) {
            outL[i] = this.left[this.readIdx];
            if (outR !== outL) outR[i] = this.right[this.readIdx];
            this.readIdx = (this.readIdx + 1) % this.capacity;
            this.available--;
          } else {
            outL[i] = 0;
            if (outR !== outL) outR[i] = 0;
            this.started = false;
          }
        }
        return true;
      }
    }
    registerProcessor("pcm-ring-processor", PcmRingProcessor);
  `;
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
        if (pendingVideoFrame) {
          try { pendingVideoFrame.close(); } catch { /* ignore */ }
        }
        pendingVideoFrame = frame;
        if (!rafScheduled) {
          rafScheduled = true;
          requestAnimationFrame(renderPendingVideoFrame);
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

  function renderPendingVideoFrame() {
    rafScheduled = false;
    const frame = pendingVideoFrame;
    pendingVideoFrame = null;
    if (!frame) return;

    try {
      if (frame.displayWidth !== videoWidth || frame.displayHeight !== videoHeight) {
        videoWidth = frame.displayWidth;
        videoHeight = frame.displayHeight;
        computeDrawRect();
        statRes.textContent = `${videoWidth}x${videoHeight}`;
      }

      if (audioOnlyBanner && !audioOnlyBanner.classList.contains("hidden")) {
        audioOnlyBanner.classList.add("hidden");
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
  }

  function closeDecoder() {
    if (pendingVideoFrame) {
      try { pendingVideoFrame.close(); } catch { /* ignore */ }
      pendingVideoFrame = null;
    }
    rafScheduled = false;
    if (decoder) {
      try {
        if (decoder.state !== "closed") decoder.close();
      } catch { /* ignore */ }
      decoder = null;
    }
  }

  function initAudioContext() {
    const AudioCtx = window.AudioContext || window.webkitAudioContext;
    if (!AudioCtx) return;

    if (!audioCtx || audioCtx.state === "closed") {
      try {
        audioCtx = new AudioCtx({ latencyHint: "interactive" });
        audioGain = audioCtx.createGain();
        audioGain.gain.value = audioEnabled ? 1.0 : 0.0;
        audioGain.connect(audioCtx.destination);
        audioNextPlayTime = 0;
        useAudioWorklet = false;

        if (audioCtx.audioWorklet) {
          const targetCtx = audioCtx;
          const targetGain = audioGain;
          const blob = new Blob([AUDIO_WORKLET_CODE], { type: "application/javascript" });
          const blobUrl = URL.createObjectURL(blob);
          targetCtx.audioWorklet.addModule(blobUrl)
            .then(() => {
              URL.revokeObjectURL(blobUrl);
              if (audioCtx !== targetCtx || targetCtx.state === "closed" || !targetGain) return;
              audioWorkletNode = new AudioWorkletNode(targetCtx, "pcm-ring-processor", {
                numberOfInputs: 0,
                numberOfOutputs: 1,
                outputChannelCount: [2]
              });
              audioWorkletNode.connect(targetGain);
              useAudioWorklet = true;
            })
            .catch(() => {
              URL.revokeObjectURL(blobUrl);
              useAudioWorklet = false;
            });
        }
      } catch {
        audioCtx = null;
        audioGain = null;
        audioWorkletNode = null;
        useAudioWorklet = false;
        return;
      }
    }

    if (audioCtx.state === "suspended") {
      audioCtx.resume().catch(() => { /* ignore autoplay restriction until next tap */ });
    }
  }

  function closeAudioContext() {
    audioNextPlayTime = 0;
    useAudioWorklet = false;
    if (audioWorkletNode) {
      try { audioWorkletNode.disconnect(); } catch { /* ignore */ }
      audioWorkletNode = null;
    }
    audioGain = null;
    if (audioCtx) {
      try {
        if (audioCtx.state !== "closed") audioCtx.close();
      } catch { /* ignore */ }
      audioCtx = null;
    }
  }

  function handleAudioPacket(arrayBuffer) {
    if (!audioEnabled || !audioCtx || !audioGain) return;
    if (arrayBuffer.byteLength <= 14) return;

    if (audioCtx.state === "suspended") {
      audioCtx.resume().catch(() => { });
      if (audioCtx.state !== "running") return;
    }

    const view = new DataView(arrayBuffer);
    const sampleRate = view.getUint32(1);
    const channels = Math.min(2, Math.max(1, view.getUint8(5)));
    if (sampleRate < 8000 || sampleRate > 192000) return;

    if (useAudioWorklet && audioWorkletNode) {
      audioWorkletNode.port.postMessage({ buffer: arrayBuffer, sampleRate, channels }, [arrayBuffer]);
      return;
    }

    handleAudioPacketFallback(arrayBuffer, view, sampleRate, channels);
  }

  function handleAudioPacketFallback(arrayBuffer, view, sampleRate, channels) {
    const pcmBytes = arrayBuffer.byteLength - 14;
    const frameCount = Math.floor(pcmBytes / (channels * 2));
    if (frameCount <= 0) return;

    const audioBuffer = audioCtx.createBuffer(channels, frameCount, sampleRate);
    if (channels === 1) {
      const ch0 = audioBuffer.getChannelData(0);
      for (let i = 0; i < frameCount; i++) {
        ch0[i] = view.getInt16(14 + i * 2, true) / 32768.0;
      }
    } else {
      const ch0 = audioBuffer.getChannelData(0);
      const ch1 = audioBuffer.getChannelData(1);
      for (let i = 0; i < frameCount; i++) {
        const off = 14 + i * 4;
        ch0[i] = view.getInt16(off, true) / 32768.0;
        ch1[i] = view.getInt16(off + 2, true) / 32768.0;
      }
    }

    const now = audioCtx.currentTime;
    if (audioNextPlayTime < now || audioNextPlayTime > now + AUDIO_MAX_BUFFER_SEC) {
      audioNextPlayTime = now + AUDIO_TARGET_BUFFER_SEC;
    }

    const src = audioCtx.createBufferSource();
    src.buffer = audioBuffer;
    src.connect(audioGain);
    src.start(audioNextPlayTime);
    audioNextPlayTime += audioBuffer.duration;
  }

  async function onAuthenticated() {
    hasConnectedOnce = true;
    setPairStatus(null);
    btnStart.disabled = false;
    landingOverlay.classList.add("hidden");
    miniPill.classList.remove("hidden");
    resizeCanvas();
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

    sendControl({ type: "config", audioEnabled });

    clearInterval(pingTimer);
    pingTimer = setInterval(() => {
      sendControl({ type: "ping", ts: Date.now() });
    }, 2000);
  }

  function connectWebSocket() {
    clearTimeout(reconnectTimer);
    if (ws) {
      try { ws.close(); } catch { }
      ws = null;
    }

    const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
    const devName = (inputDeviceName?.value || "").trim() || detectDefaultDeviceName();
    try { localStorage.setItem("pd_device_name", devName); } catch { /* ignore */ }
    const wsUrl = `${proto}//${window.location.host}/ws`;
    showToast(t("connecting"), 0);

    ws = new WebSocket(wsUrl);
    ws.binaryType = "arraybuffer";

    ws.onopen = () => {
      sendControl({
        type: "pair",
        deviceId,
        deviceSecret,
        deviceName: devName,
        token: bootstrapToken || undefined
      });
    };

    ws.onmessage = (ev) => {
      if (typeof ev.data === "string") {
        handleServerText(ev.data);
        return;
      }

      if (ev.data instanceof ArrayBuffer && ev.data.byteLength > 0) {
        const pktType = new DataView(ev.data).getUint8(0);
        if (pktType === 2) {
          handleAudioPacket(ev.data);
          return;
        }
      }

      handleVideoPacket(ev.data);
    };

    ws.onclose = (ev) => {
      clearInterval(pingTimer);
      closeDecoder();
      audioNextPlayTime = 0;
      if (useAudioWorklet && audioWorkletNode) {
        try { audioWorkletNode.port.postMessage({ type: "reset" }); } catch { }
      }
      if (!isRunning) return;

      if (ev.code === 4001 || ev.code === 4003 || ev.code === 4029) {
        isRunning = false;
        btnStart.disabled = false;
        miniPill.classList.add("hidden");
        statsHud.classList.add("hidden");
        audioOnlyBanner?.classList.add("hidden");
        landingOverlay.classList.remove("hidden");
        if (document.fullscreenElement) {
          try { document.exitFullscreen().catch(() => { }); } catch { }
        }
        const msgKey = ev.code === 4029 ? "rateLimited" : ev.code === 4003 ? "banned" : "denied";
        setPairStatus("error", t(msgKey));
        showToast(t(msgKey), 4000);
        return;
      }

      if (ev.code === 1006 && !hasConnectedOnce) {
        showToast(t("rejected"), 3500);
      } else {
        showToast(t("reconnecting"), 0);
      }
      reconnectTimer = setTimeout(connectWebSocket, 1200);
    };

    ws.onerror = () => {
      // onclose will handle reconnect
    };
  }

  function handleServerText(text) {
    let msg;
    try { msg = JSON.parse(text); } catch { return; }

    if (msg.type === "pair_status") {
      if (msg.status === "pending") {
        setPairStatus("pending", t("waitingApproval"));
      } else if (msg.status === "denied") {
        isRunning = false;
        btnStart.disabled = false;
        setPairStatus("error", t("denied"));
      } else if (msg.status === "banned") {
        isRunning = false;
        btnStart.disabled = false;
        setPairStatus("error", t("banned"));
      }
      return;
    }

    if (msg.type === "auth_ok") {
      onAuthenticated();
      return;
    }

    if (msg.type === "info") {
      videoWidth = msg.width || videoWidth;
      videoHeight = msg.height || videoHeight;
      activeEncoderName = msg.encoder || "--";
      statRes.textContent = msg.videoEnabled === false ? "Audio" : `${videoWidth}x${videoHeight}`;
      statEncName.textContent = activeEncoderName;
      computeDrawRect();

      if (msg.videoEnabled === false) {
        hostVideoEnabled = false;
        ctx.fillStyle = "#000000";
        ctx.fillRect(0, 0, canvas.width, canvas.height);
        audioOnlyBanner?.classList.remove("hidden");
      } else {
        hostVideoEnabled = true;
        audioOnlyBanner?.classList.add("hidden");
        waitingForKeyframe = true;
        if (decoder && decoder.state === "closed") {
          initDecoder(msg.codec || "avc1.42E028");
        }
      }

      if (typeof msg.allowInput === "boolean" && msg.allowInput !== hostAllowInput) {
        hostAllowInput = msg.allowInput;
        showToast(t(hostAllowInput ? "inputUnlockedByHost" : "inputLockedByHost"), 2000);
        if (!hostAllowInput) {
          mobileKbdInput.blur();
          setMobileKeyboardUi(false);
          btnMode.textContent = t("modeView");
          btnMode.classList.remove("active");
        } else {
          applyWebLang();
        }
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
    if (decoder.decodeQueueSize > 4 && !isKeyframe) {
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
    if (!isRunning || !hostAllowInput || inputMode === "view") return;
    e.preventDefault();
    sendControl({
      type: "input",
      action: "scroll",
      wheelX: -e.deltaX / 100,
      wheelY: -e.deltaY / 100
    });
  }, { passive: false });

  canvas.addEventListener("pointerdown", (e) => {
    if (audioEnabled && audioCtx && audioCtx.state === "suspended") {
      audioCtx.resume().catch(() => { });
    }
    if (!isRunning || !hostAllowInput || inputMode === "view" || isOverWebUi(e)) return;
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
    if (!isRunning || !hostAllowInput || inputMode === "view") return;
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
    if (!hostAllowInput) {
      showToast(t("inputLockedByHost"), 2000);
      return;
    }
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
    if (!hostAllowInput) {
      mobileKbdInput.blur();
      setMobileKeyboardUi(false);
      showToast(t("inputLockedByHost"), 2000);
      return;
    }
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
    if (!isRunning || !hostAllowInput || inputMode === "view") return;
    if (document.activeElement === mobileKbdInput || document.activeElement === inputDeviceName) return;
    e.preventDefault();
    sendControl({ type: "input", action: "keydown", key: e.key, code: e.code });
  });

  window.addEventListener("keyup", (e) => {
    if (!isRunning || !hostAllowInput || inputMode === "view") return;
    if (document.activeElement === mobileKbdInput || document.activeElement === inputDeviceName) return;
    e.preventDefault();
    sendControl({ type: "input", action: "keyup", key: e.key, code: e.code });
  });

  btnAudio.addEventListener("click", (e) => {
    e.stopPropagation();
    audioEnabled = !audioEnabled;
    if (audioEnabled) {
      initAudioContext();
      if (audioGain && audioCtx) {
        audioGain.gain.setValueAtTime(1.0, audioCtx.currentTime);
      }
      if (useAudioWorklet && audioWorkletNode) {
        try { audioWorkletNode.port.postMessage({ type: "reset" }); } catch { }
      }
      audioNextPlayTime = 0;
    } else {
      if (audioGain && audioCtx) {
        audioGain.gain.setValueAtTime(0.0, audioCtx.currentTime);
      }
      if (useAudioWorklet && audioWorkletNode) {
        try { audioWorkletNode.port.postMessage({ type: "reset" }); } catch { }
      }
    }

    btnAudio.textContent = audioEnabled ? t("audioBtnOn") : t("audioBtnOff");
    btnAudio.classList.toggle("active", audioEnabled);
    sendControl({ type: "config", audioEnabled });
    showToast(audioEnabled ? t("audioOn") : t("audioOff"), 1200);
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
    closeAudioContext();

    if (wakeLock) {
      try { await wakeLock.release(); } catch { }
      wakeLock = null;
    }
    if (document.fullscreenElement) {
      try { await document.exitFullscreen(); } catch { }
    }

    miniPill.classList.add("hidden");
    statsHud.classList.add("hidden");
    audioOnlyBanner?.classList.add("hidden");
    setPairStatus(null);
    btnStart.disabled = false;
    landingOverlay.classList.remove("hidden");
  });

  btnStart.addEventListener("click", async () => {
    isRunning = true;
    btnStart.disabled = true;
    setPairStatus("pending", t("connecting"));
    initAudioContext();
    await acquireImmersiveMode();
    connectWebSocket();
  });
})();
