/* The page's behaviour. Everything runs in the browser; nothing is sent anywhere. */
(function () {
  'use strict';

  const S = window.SITE || {};
  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
  const pad = (n) => String(n).padStart(2, '0');
  const clock = (s) => `${pad(Math.floor(s / 3600))}:${pad(Math.floor(s / 60) % 60)}:${pad(s % 60)}`;

  /* ---------- Reveal on scroll (first, so nothing stays hidden) ---------- */
  const revealer = 'IntersectionObserver' in window
    ? new IntersectionObserver((entries) => {
        entries.forEach((e) => { if (e.isIntersecting) { e.target.classList.add('in'); revealer.unobserve(e.target); } });
      }, { rootMargin: '0px 0px -8% 0px', threshold: 0.08 })
    : null;
  $$('.reveal').forEach((el) => (revealer ? revealer.observe(el) : el.classList.add('in')));

  /* ---------- Name, version and links from config.js ---------- */
  $$('.nm').forEach((el) => { el.textContent = S.name || ''; });
  $$('[data-version]').forEach((el) => { el.textContent = S.version || ''; });
  $$('[data-video-title]').forEach((el) => { el.textContent = S.videoTitle || el.textContent; });
  $$('[data-file]').forEach((el) => {
    const url = (S.downloads || {})[el.dataset.file];
    if (url) el.textContent = decodeURIComponent(url.split('/').pop());
  });

  const linkFor = (key) => S[key] || (S.downloads || {})[key] || (S.socials || {})[key] || '';
  const isExternal = (url) => /^https?:/.test(url);

  function setLink(el, url) {
    if (url) {
      el.href = url;
      el.classList.remove('is-off');
      el.removeAttribute('aria-disabled');
    } else {
      el.href = '#';
      el.classList.add('is-off');
      el.setAttribute('aria-disabled', 'true');
      el.title = 'Coming soon';
    }
  }
  $$('[data-link]').forEach((el) => {
    const url = linkFor(el.dataset.link);
    setLink(el, url);
    if (el.dataset.link === 'testflight' && !url) {
      const label = el.querySelector('span') || el;
      label.lastChild.textContent = 'Coming to TestFlight';
    }
    if (url && isExternal(url)) el.rel = 'noopener';
  });
  $$('[data-dl]').forEach((el) => {
    const url = (S.downloads || {})[el.dataset.dl];
    if (url) el.href = url;
  });

  /* ---------- Theme ---------- */
  const themeBtn = $('#themeBtn');
  const currentTheme = () => document.documentElement.getAttribute('data-theme') ||
    (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark');
  themeBtn.addEventListener('click', () => {
    const next = currentTheme() === 'dark' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', next);
    try { localStorage.setItem('theme', next); } catch (e) {}
  });

  /* ---------- Nav ---------- */
  const nav = $('#nav');
  const menuBtn = $('#menuBtn');
  const navLinks = $('#navLinks');
  menuBtn.addEventListener('click', () => {
    const open = !navLinks.classList.contains('open');
    navLinks.classList.toggle('open', open);
    menuBtn.setAttribute('aria-expanded', String(open));
  });
  navLinks.addEventListener('click', (e) => {
    if (e.target.closest('a')) { navLinks.classList.remove('open'); menuBtn.setAttribute('aria-expanded', 'false'); }
  });

  /* ---------- Hero: tilt flattens as you scroll ---------- */
  const stage = $('#stage');
  let ticking = false;
  function onScroll() {
    nav.classList.toggle('scrolled', scrollY > 10);
    if (!reduced && stage) {
      const p = Math.min(1, Math.max(0, scrollY / 520));
      stage.style.setProperty('--tilt', `${(14 * (1 - p)).toFixed(2)}deg`);
      stage.style.setProperty('--sc', (0.94 + 0.06 * p).toFixed(3));
    }
    ticking = false;
  }
  addEventListener('scroll', () => { if (!ticking) { ticking = true; requestAnimationFrame(onScroll); } }, { passive: true });
  onScroll();

  const heroRec = $('#heroRec');
  let heroSecs = 152;
  if (!reduced) setInterval(() => { heroSecs += 1; heroRec.textContent = clock(heroSecs); }, 1000);

  /* ---------- Which download fits this visitor (worked out here, never sent) ---------- */
  (async function pickDownload() {
    const ua = navigator.userAgent;
    const plat = (navigator.userAgentData && navigator.userAgentData.platform) || '';
    const touchMac = /Macintosh/.test(ua) && navigator.maxTouchPoints > 1;
    let os = 'other';
    if (/iPhone|iPad|iPod/.test(ua) || touchMac) os = 'ios';
    else if (/Android/.test(ua)) os = 'android';
    else if (/Windows/.test(plat) || /Windows NT/.test(ua)) os = 'windows';
    else if (/macOS/.test(plat) || /Macintosh/.test(ua)) os = 'mac';
    else if (/Linux|CrOS/.test(plat + ua)) os = 'linux';

    let arm = false;
    if (os === 'windows' && navigator.userAgentData && navigator.userAgentData.getHighEntropyValues) {
      try { arm = (await navigator.userAgentData.getHighEntropyValues(['architecture'])).architecture === 'arm'; } catch (e) {}
    }

    const primary = $('[data-dl="primary"]');
    const secondary = $('[data-dl="secondary"]');
    const fine = $('#heroFine');
    const label = (el, text) => { el.querySelector('span').textContent = text; };
    const d = S.downloads || {};

    if (os === 'windows' && !arm) {
      primary.href = d.windows; label(primary, 'Download for Windows');
      label(secondary, 'Mac, iPhone and iPad');
    } else if (os === 'mac') {
      primary.href = d.macApple; label(primary, 'Download for Mac');
      label(secondary, 'Windows, iPhone and iPad');
      fine.innerHTML = 'Free and open source. No account, no subscription, no tracking. <a href="' + d.macIntel + '">Older Intel Mac? Use this one.</a>';
    } else if (os === 'ios') {
      primary.href = d.testflight || '#iphone'; label(primary, d.testflight ? 'Get the iPhone app on TestFlight' : 'iPhone: coming to TestFlight');
      label(secondary, 'Windows and Mac');
    } else {
      label(primary, 'Download free');
      label(secondary, 'See what it runs on');
      secondary.href = '#download';
      if (arm) fine.textContent = 'Windows on ARM isn\'t supported. It needs a Windows 11 x64 PC or a Mac.';
      else if (os === 'android' || os === 'linux') fine.textContent = 'Free for Windows 11, Mac, iPhone and iPad. Android and Linux aren\'t supported.';
    }
    const soon = $('.qd-soon');
    if (soon && d.testflight) { soon.href = d.testflight; soon.lastChild.textContent = 'iPhone on TestFlight'; }
    const mine = { windows: 'windows', mac: 'mac', ios: 'ios' }[arm ? '' : os];
    if (mine) { const card = $(`.plat[data-os="${mine}"]`); if (card) card.classList.add('mine'); }
  })();

  /* ---------- Video: loads from YouTube only after a click ---------- */
  const video = $('#video');
  if (S.youtubeId) { $('#watch').hidden = false; $('#heroWatch').hidden = false; }
  $('#playBtn').addEventListener('click', () => {
    if (!S.youtubeId) {
      const note = $('#videoNote');
      note.textContent = 'The video is coming soon';
      note.classList.add('show');
      setTimeout(() => note.classList.remove('show'), 2600);
      return;
    }
    const f = document.createElement('iframe');
    f.src = `https://www.youtube-nocookie.com/embed/${encodeURIComponent(S.youtubeId)}?autoplay=1&rel=0&modestbranding=1&playsinline=1`;
    f.title = S.videoTitle || 'Video';
    f.allow = 'autoplay; encrypted-media; picture-in-picture; fullscreen';
    f.allowFullscreen = true;
    f.referrerPolicy = 'strict-origin-when-cross-origin';
    video.replaceChildren(f);
  });

  /* ======================================================================
     Live-view lab: a model of the window's picture tools on a real frame
     ====================================================================== */
  const lab = $('#lab');
  const canvas = $('#labCanvas');
  const ctx = canvas.getContext('2d');
  const overlay = $('#labOverlay');
  const octx = overlay.getContext('2d');
  const OW = overlay.width, OH = overlay.height;
  const histCanvas = $('#histCanvas');
  const hctx = histCanvas.getContext('2d');
  const pic = $('#monPic');
  const zoomEl = $('#monZoom');
  const focusBox = $('#focusBox');
  const hint = $('#labHint');
  const toastEl = $('#labToast');
  const histo = $('#histo');

  const ISO = [100, 125, 160, 200, 250, 320, 400, 500, 640, 800, 1000, 1250, 1600, 2000, 2500, 3200, 4000, 5000, 6400, 8000, 10000, 12800];
  const SHUT = ['1/25', '1/30', '1/40', '1/50', '1/60', '1/80', '1/100', '1/125', '1/160', '1/200', '1/250', '1/320', '1/400', '1/500', '1/640', '1/800', '1/1000'];
  const IRIS = [1.4, 1.6, 1.8, 2, 2.2, 2.5, 2.8, 3.2, 3.5, 4, 4.5, 5, 5.6, 6.3, 7.1, 8, 9, 10, 11];
  const exp = { iso: ISO.indexOf(640), shutter: SHUT.indexOf('1/50'), iris: IRIS.indexOf(2.8) };
  const BASE_METER = -2.0; // what the camera's meter said for this frame

  const state = { grid: false, zebras: false, peaking: false, histogram: true, zebra: 70, peakColor: '#30e0ff', peakStrength: 1, tap: 'focus', mag: 1, focus: null, rec: false, recSecs: 0 };

  let base = null;          // the original pixels, 1600×900
  let tainted = false;      // true when the page is opened as a file, where canvases can't be read
  let lastPx = null;        // the picture as shown, at the camera's Wi-Fi size
  const img = new Image();
  img.src = 'assets/focus-jay.jpg';

  const toLin = new Float32Array(256);
  for (let i = 0; i < 256; i++) { const c = i / 255; toLin[i] = c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); }
  const toSrgb = (l) => (l <= 0.0031308 ? 12.92 * l : 1.055 * Math.pow(l, 1 / 2.4) - 0.055);

  const evOffset = () => Math.log2(ISO[exp.iso] / 640) + Math.log2(50 / Number(SHUT[exp.shutter].split('/')[1])) + 2 * Math.log2(2.8 / IRIS[exp.iris]);

  // Worked out at 1024×576, the size the A7S III sends over Wi-Fi
  const ana = document.createElement('canvas'); ana.width = OW; ana.height = OH;
  const actx = ana.getContext('2d', { willReadFrequently: true });

  /* Real focus: a depth map of the frame (near = white) picks, for each pixel, how
     far it sits from the focused distance, and so how soft it is. The soft versions
     are blurred once up front and blended per pixel. A wider iris blurs more. */
  const depthImg = new Image();
  depthImg.src = 'assets/focus-jay-depth.png';
  const BLUR_R = [0, 2, 4, 7, 11, 16];   // radius of each soft version, at 1600 px wide
  const DEFOCUS = 22;                    // blur radius per unit of depth at F2.8
  const START_DEPTH = 0.72;              // starts on the frame in front of the jay, so the jay is soft
  let depth = null, softs = null, focused = null;
  let focusDepth = START_DEPTH, focusAnim = 0;

  function padded(r) {
    // Edges repeat outward so the blur doesn't darken the borders
    const W = canvas.width, H = canvas.height, P = Math.ceil(r * 2.5) + 2;
    const c = document.createElement('canvas'); c.width = W + 2 * P; c.height = H + 2 * P;
    const g = c.getContext('2d');
    g.drawImage(img, P, P, W, H);
    const sw = img.naturalWidth / W, sh = img.naturalHeight / H;
    g.drawImage(img, 0, 0, sw, img.naturalHeight, 0, P, P, H);
    g.drawImage(img, img.naturalWidth - sw, 0, sw, img.naturalHeight, W + P, P, P, H);
    g.drawImage(c, 0, P, c.width, 1, 0, 0, c.width, P);
    g.drawImage(c, 0, P + H - 1, c.width, 1, 0, P + H, c.width, P);
    return { c, P };
  }
  function makeSofts() {
    const W = canvas.width, H = canvas.height;
    const probe = document.createElement('canvas').getContext('2d');
    probe.filter = 'blur(2px)';
    const canFilter = probe.filter === 'blur(2px)';
    softs = BLUR_R.map((r) => {
      if (!r) return base.data;
      const { c, P } = padded(r);
      const t = document.createElement('canvas'); t.width = c.width; t.height = c.height;
      const g = t.getContext('2d', { willReadFrequently: true });
      if (canFilter) {
        g.filter = `blur(${r}px)`; g.drawImage(c, 0, 0);
      } else {
        // Older Safari: shrink and grow back, which softens about as much
        const s = document.createElement('canvas'); s.width = Math.max(1, Math.round(c.width / r)); s.height = Math.max(1, Math.round(c.height / r));
        s.getContext('2d').drawImage(c, 0, 0, s.width, s.height);
        g.imageSmoothingQuality = 'high'; g.drawImage(s, 0, 0, t.width, t.height);
      }
      return g.getImageData(P, P, W, H).data;
    });
  }
  function composeFocus() {
    const n = canvas.width * canvas.height;
    if (!focused) focused = new Uint8ClampedArray(n * 4);
    // For each of the 256 depth values: which two soft versions, and how much of each
    const lo = new Uint8Array(256), wt = new Float32Array(256);
    const dof = 2.8 / IRIS[exp.iris];
    for (let v = 0; v < 256; v++) {
      const r = Math.min(BLUR_R[BLUR_R.length - 1], DEFOCUS * dof * Math.abs(v / 255 - focusDepth));
      let k = 0; while (k < BLUR_R.length - 2 && r > BLUR_R[k + 1]) k++;
      lo[v] = k; wt[v] = (r - BLUR_R[k]) / (BLUR_R[k + 1] - BLUR_R[k]);
    }
    for (let p = 0, i = 0; p < n; p++, i += 4) {
      const v = depth[p], k = lo[v], w = wt[v], a = softs[k], b = softs[k + 1];
      focused[i] = a[i] + (b[i] - a[i]) * w;
      focused[i + 1] = a[i + 1] + (b[i + 1] - a[i + 1]) * w;
      focused[i + 2] = a[i + 2] + (b[i + 2] - a[i + 2]) * w;
    }
  }
  // Glides the focus to a new distance, like a lens racking
  function pullFocus(to) {
    cancelAnimationFrame(focusAnim);
    if (!depth || reduced) { focusDepth = to; renderExposure(); return; }
    const from = focusDepth, t0 = performance.now(), dur = 420;
    const step = (now) => {
      const t = Math.min(1, (now - t0) / dur), e = t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
      focusDepth = from + (to - from) * e;
      renderExposure(t < 1);
      if (t < 1) focusAnim = requestAnimationFrame(step);
    };
    focusAnim = requestAnimationFrame(step);
  }
  function depthAt(p) {
    // The middle value of a small patch, so a click just beside the neck still finds the bird
    if (!depth) return focusDepth;
    const W = canvas.width, H = canvas.height, cx = Math.round(p.x * W), cy = Math.round(p.y * H), vals = [];
    for (let dy = -6; dy <= 6; dy += 3) for (let dx = -6; dx <= 6; dx += 3) {
      const x = Math.min(W - 1, Math.max(0, cx + dx)), y = Math.min(H - 1, Math.max(0, cy + dy));
      vals.push(depth[y * W + x]);
    }
    vals.sort((a, b) => a - b);
    return vals[vals.length >> 1] / 255;
  }

  let loaded = 0;
  const onPictures = () => {
    if (++loaded < 2) return;
    ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
    try {
      base = ctx.getImageData(0, 0, canvas.width, canvas.height);
      if (depthImg.naturalWidth) {
        const t = document.createElement('canvas'); t.width = canvas.width; t.height = canvas.height;
        const g = t.getContext('2d', { willReadFrequently: true });
        g.drawImage(depthImg, 0, 0, t.width, t.height);
        const d = g.getImageData(0, 0, t.width, t.height).data;
        depth = new Uint8Array(t.width * t.height);
        for (let p = 0; p < depth.length; p++) depth[p] = d[p * 4];
        makeSofts();
      }
    } catch (e) {
      tainted = true;
    }
    renderExposure();
  };
  img.onload = onPictures;
  depthImg.onload = onPictures;
  depthImg.onerror = onPictures;

  function renderExposure(moving) {
    const ev = evOffset();
    const gain = Math.pow(2, ev);
    updateMeter(ev);
    if (tainted || !base) {
      ctx.filter = `brightness(${Math.pow(gain, 1 / 2.2).toFixed(3)})`;
      ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
      ctx.filter = 'none';
      drawHistogram(null);
      return;
    }
    const lut = new Uint8ClampedArray(256);
    for (let i = 0; i < 256; i++) lut[i] = Math.round(255 * toSrgb(Math.min(1, toLin[i] * gain)));
    let src = base.data;
    if (depth && softs) { composeFocus(); src = focused; }
    const out = ctx.createImageData(canvas.width, canvas.height);
    const o = out.data;
    for (let i = 0; i < src.length; i += 4) { o[i] = lut[src[i]]; o[i + 1] = lut[src[i + 1]]; o[i + 2] = lut[src[i + 2]]; o[i + 3] = 255; }
    ctx.putImageData(out, 0, 0);
    if (moving) return; // zebras, peaking and the histogram catch up when the focus settles
    actx.drawImage(canvas, 0, 0, OW, OH);
    lastPx = actx.getImageData(0, 0, OW, OH).data;
    analyse();
  }

  /* The same maths as the app's PictureAssist.cs: Rec.709 luma, "under" at about 3%,
     black zebra stripes, and peaking that marks crisp edges in connected runs. */
  const PEAK_CONTRAST = [48, 30, 18], PEAK_CRISP = 0.4, PEAK_REACH = 3, PEAK_GAP = 1, PEAK_MIN_RUN = 24, UNDER = 8;
  let bins = null;
  function analyse() {
    if (!lastPx) return;
    const px = lastPx, n = OW * OH;
    const luma = new Uint8Array(n);
    bins = new Uint32Array(256);
    for (let p = 0, i = 0; p < n; p++, i += 4) {
      const y = (px[i] * 54 + px[i + 1] * 183 + px[i + 2] * 19) >> 8;
      luma[p] = y; bins[y]++;
    }
    const level = (state.zebra * 255 / 100) | 0;
    let over = 0, under = 0;
    for (let i = level; i < 256; i++) over += bins[i];
    for (let i = 0; i <= UNDER; i++) under += bins[i];
    $('#overTxt').textContent = `Over ${state.zebra}%: ${(100 * over / n).toFixed(1)}%`;
    $('#underTxt').textContent = `Under 3%: ${(100 * under / n).toFixed(1)}%`;
    drawHistogram(bins);

    const ov = octx.createImageData(OW, OH);
    const d = ov.data;
    if (state.zebras) {
      const period = Math.max(10, (OW / 110) | 0);
      for (let y = 0; y < OH; y++) for (let x = 0; x < OW; x++) {
        if (luma[y * OW + x] >= level && (x + y) % period < period / 2) d[(y * OW + x) * 4 + 3] = 200;
      }
    }
    if (state.peaking) drawPeaking(luma, d);
    octx.putImageData(ov, 0, 0);
  }

  function drawPeaking(luma, d) {
    const w = OW, h = OH;
    const minContrast = PEAK_CONTRAST[state.peakStrength], minStep = (minContrast * PEAK_CRISP) | 0;
    const crisp = new Uint8Array(w * h);
    for (let y = PEAK_REACH; y < h - PEAK_REACH - 1; y++) {
      for (let x = PEAK_REACH; x < w - PEAK_REACH - 1; x++) {
        const i = y * w + x;
        const across = Math.abs(luma[i + 1] - luma[i]), down = Math.abs(luma[i + w] - luma[i]);
        const step = Math.max(across, down);
        if (step < minStep) continue;
        const stride = across >= down ? 1 : w;
        let low = 255, high = 0;
        for (let k = -PEAK_REACH; k <= PEAK_REACH + 1; k++) { const v = luma[i + k * stride]; if (v < low) low = v; if (v > high) high = v; }
        const contrast = high - low;
        if (contrast >= minContrast && step >= contrast * PEAK_CRISP) crisp[i] = 1;
      }
    }
    const hex = state.peakColor;
    const r = parseInt(hex.slice(1, 3), 16), g = parseInt(hex.slice(3, 5), 16), b = parseInt(hex.slice(5, 7), 16);
    const mark = (i) => { const j = i * 4; d[j] = r; d[j + 1] = g; d[j + 2] = b; d[j + 3] = 255; };
    const seen = new Uint8Array(w * h);
    const run = [], todo = [];
    for (let start = 0; start < crisp.length; start++) {
      if (!crisp[start] || seen[start]) continue;
      run.length = 0; seen[start] = 1; todo.push(start);
      let left = w, right = 0, top = h, bottom = 0;
      while (todo.length) {
        const i = todo.pop(), x = i % w, y = (i / w) | 0;
        run.push(i);
        if (x < left) left = x; if (x > right) right = x; if (y < top) top = y; if (y > bottom) bottom = y;
        for (let dy = -PEAK_GAP; dy <= PEAK_GAP; dy++) for (let dx = -PEAK_GAP; dx <= PEAK_GAP; dx++) {
          const nx = x + dx, ny = y + dy;
          if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
          const m = ny * w + nx;
          if (crisp[m] && !seen[m]) { seen[m] = 1; todo.push(m); }
        }
      }
      if (Math.max(right - left, bottom - top) < PEAK_MIN_RUN) continue;
      for (const i of run) { mark(i); mark(i + 1); mark(i + w); mark(i + w + 1); }
    }
  }

  function drawHistogram(b) {
    const W = histCanvas.width, H = histCanvas.height;
    hctx.clearRect(0, 0, W, H);
    hctx.fillStyle = 'rgba(0,0,0,.35)'; hctx.fillRect(0, 0, W, H);
    if (!b) {
      hctx.fillStyle = '#8d9399'; hctx.font = '22px system-ui'; hctx.textAlign = 'center';
      hctx.fillText('Open the page from a web server', W / 2, H / 2 + 8);
      return;
    }
    let peak = 1; for (let i = 2; i < 254; i++) peak = Math.max(peak, b[i]); // a clipped end shouldn't flatten the rest
    const lvlX = (state.zebra * 255 / 100) / 255 * W;
    hctx.beginPath(); hctx.moveTo(0, H);
    for (let i = 0; i < 256; i++) hctx.lineTo(i / 255 * W, H - Math.min(1, Math.sqrt(b[i] / peak)) * (H - 6));
    hctx.lineTo(W, H); hctx.closePath();
    hctx.save(); hctx.clip();
    hctx.fillStyle = 'rgba(232,236,240,.85)'; hctx.fillRect(0, 0, lvlX, H);
    hctx.fillStyle = '#e8b339'; hctx.fillRect(lvlX, 0, W - lvlX, H);
    hctx.restore();
    hctx.fillStyle = '#e8b339'; hctx.fillRect(Math.min(W - 2, lvlX - 1), 0, 2, H);
  }

  function updateMeter(ev) {
    const m = BASE_METER + ev;
    const t = $('#meterTxt'), mark = $('#meterMark');
    const r = Math.round(m * 10) / 10;
    const zero = Math.abs(r) < 0.2;
    t.textContent = zero ? 'On the mark' : `${Math.abs(r).toFixed(1)} stops ${r < 0 ? 'dark' : 'bright'}`;
    t.classList.toggle('zero', zero); mark.classList.toggle('zero', zero);
    const pct = (Math.max(-3.2, Math.min(3.2, m)) + 3) / 6;
    mark.style.left = `calc(7px + ${pct} * (100% - 14px))`;
  }

  let labVisible = false;
  if ('IntersectionObserver' in window) {
    new IntersectionObserver(([e]) => { labVisible = e.isIntersecting; }, { threshold: 0.15 }).observe(lab);
  }

  let toastTimer = 0;
  function toast(msg) {
    toastEl.textContent = msg; toastEl.classList.add('show');
    clearTimeout(toastTimer); toastTimer = setTimeout(() => toastEl.classList.remove('show'), 1600);
  }

  function setTool(name, on) {
    state[name] = on;
    const btn = $(`.tool[data-tool="${name}"]`);
    if (btn) btn.setAttribute('aria-pressed', String(on));
    if (name === 'grid') $('#labGrid').classList.toggle('on', on);
    if (name === 'histogram') histo.classList.toggle('off', !on);
    if (name === 'zebras' || name === 'peaking') analyse();
    if ((name === 'zebras' || name === 'peaking') && on && tainted) toast('Open the page from a web server to see this');
  }
  $$('.tool[data-tool]').forEach((b) => b.addEventListener('click', () => setTool(b.dataset.tool, !state[b.dataset.tool])));

  const levelsBtn = $('#levelsBtn'), levels = $('#levels');
  function toggleLevels(open) {
    levels.hidden = !open; levelsBtn.setAttribute('aria-expanded', String(open));
  }
  levelsBtn.addEventListener('click', (e) => { e.stopPropagation(); toggleLevels(levels.hidden); });
  document.addEventListener('click', (e) => { if (!levels.hidden && !e.target.closest('.levels-wrap') && !e.target.closest('#histo')) toggleLevels(false); });

  // Generic segmented controls: data-group on the container, data-v on each button
  const groupHandlers = {};
  function onGroup(name, fn) { groupHandlers[name] = fn; }
  $$('.seg[data-group]').forEach((seg) => {
    seg.addEventListener('click', (e) => {
      const b = e.target.closest('button[data-v]');
      if (!b) return;
      $$('button', seg).forEach((x) => x.classList.toggle('on', x === b));
      const fn = groupHandlers[seg.dataset.group];
      if (fn) fn(b.dataset.v, b);
    });
  });
  const pickInGroup = (group, v) => $$(`.seg[data-group="${group}"] button`).forEach((x) => x.classList.toggle('on', x.dataset.v === String(v)));

  onGroup('zebra', (v) => { state.zebra = +v; if (!state.zebras) setTool('zebras', true); else analyse(); });
  onGroup('peakStrength', (v) => { state.peakStrength = +v; if (!state.peaking) setTool('peaking', true); else analyse(); });
  onGroup('peakColor', (v) => { state.peakColor = v; if (!state.peaking) setTool('peaking', true); else analyse(); });
  onGroup('tap', (v) => {
    state.tap = v;
    if (state.focus) { focusBox.classList.toggle('track', v === 'track'); }
    toast(v === 'track' ? 'Tap does: Tracking' : 'Tap does: Focus');
  });
  onGroup('mag', (v) => {
    state.mag = +v;
    applyZoom();
    if (state.mag > 1) toast(`Magnifier ×${state.mag}. Click to move around.`);
  });

  function applyZoom() {
    const f = state.focus || { x: 0.6, y: 0.3 };
    zoomEl.style.transformOrigin = `${f.x * 100}% ${f.y * 100}%`;
    zoomEl.style.transform = state.mag > 1 ? `scale(${state.mag})` : 'none';
  }

  // Exposure steppers
  const LISTS = { iso: ISO, shutter: SHUT, iris: IRIS };
  const NAMES = { iso: 'ISO', shutter: 'shutter speed', iris: 'iris' };
  const fmt = { iso: (v) => `ISO ${v}`, shutter: (v) => v, iris: (v) => `F${v}` };
  function showExposure() {
    $$('.stepper').forEach((s) => { const k = s.dataset.exp; $('b', s).textContent = fmt[k](LISTS[k][exp[k]]); });
    $$('[data-base]').forEach((b) => b.classList.toggle('on', ISO[exp.iso] === +b.dataset.base));
  }
  $$('.stepper').forEach((s) => {
    const k = s.dataset.exp;
    const [prev, next] = $$('button', s);
    prev.setAttribute('aria-label', `Previous ${NAMES[k]}`);
    next.setAttribute('aria-label', `Next ${NAMES[k]}`);
    prev.addEventListener('click', () => { if (exp[k] > 0) { exp[k]--; showExposure(); renderExposure(); } });
    next.addEventListener('click', () => { if (exp[k] < LISTS[k].length - 1) { exp[k]++; showExposure(); renderExposure(); } });
  });
  $$('[data-base]').forEach((b) => b.addEventListener('click', () => {
    exp.iso = ISO.indexOf(+b.dataset.base); showExposure(); renderExposure();
    toast(+b.dataset.base === 12800 ? 'Base ISO 12800: the clean one for low light' : 'Base ISO 640');
  }));
  showExposure();

  // Click to focus; right-click cancels; with the magnifier on, a click moves around
  function picPoint(e) {
    const r = pic.getBoundingClientRect();
    let x = (e.clientX - r.left) / r.width, y = (e.clientY - r.top) / r.height;
    if (state.mag > 1) {
      const f = state.focus || { x: 0.6, y: 0.3 };
      x = f.x + (x - f.x) / state.mag; y = f.y + (y - f.y) / state.mag;
    }
    return { x: Math.min(0.98, Math.max(0.02, x)), y: Math.min(0.97, Math.max(0.03, y)) };
  }
  let focusTimer = 0;
  pic.addEventListener('click', (e) => {
    if (e.target.closest('#histo')) return;
    const p = picPoint(e);
    if (state.mag > 1) { state.focus = p; focusBox.style.left = `${p.x * 100}%`; focusBox.style.top = `${p.y * 100}%`; applyZoom(); return; }
    state.focus = p;
    hint.classList.add('gone');
    focusBox.style.left = `${p.x * 100}%`; focusBox.style.top = `${p.y * 100}%`;
    focusBox.classList.remove('show', 'ok');
    focusBox.classList.toggle('track', state.tap === 'track');
    void focusBox.offsetWidth;
    focusBox.classList.add('show');
    pullFocus(depthAt(p));
    clearTimeout(focusTimer);
    focusTimer = setTimeout(() => { focusBox.classList.add('ok'); toast(state.tap === 'track' ? 'Tracking' : 'Focused'); }, 420);
  });
  pic.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    if (!state.focus) return;
    focusBox.classList.remove('show', 'ok');
    pullFocus(START_DEPTH); // the camera's own autofocus goes back to the nearest thing
    toast(state.tap === 'track' ? 'Tracking cancelled' : 'Touch focus cancelled');
  });

  // The histogram box can be dragged anywhere over the picture, and a click opens Levels
  (function dragHisto() {
    let start = null;
    histo.addEventListener('pointerdown', (e) => {
      const r = histo.getBoundingClientRect(), pr = pic.getBoundingClientRect();
      start = { x: e.clientX, y: e.clientY, l: r.left - pr.left, t: r.top - pr.top, moved: false };
      histo.setPointerCapture(e.pointerId);
    });
    histo.addEventListener('pointermove', (e) => {
      if (!start) return;
      const dx = e.clientX - start.x, dy = e.clientY - start.y;
      if (Math.abs(dx) + Math.abs(dy) > 4) start.moved = true;
      if (!start.moved) return;
      const pr = pic.getBoundingClientRect();
      const l = Math.min(pr.width - histo.offsetWidth - 4, Math.max(4, start.l + dx));
      const t = Math.min(pr.height - histo.offsetHeight - 4, Math.max(4, start.t + dy));
      Object.assign(histo.style, { left: `${l / pr.width * 100}%`, top: `${t / pr.height * 100}%`, right: 'auto', bottom: 'auto' });
    });
    const end = (e) => {
      if (start && !start.moved) { e.stopPropagation(); toggleLevels(levels.hidden); }
      start = null;
    };
    histo.addEventListener('pointerup', end);
    histo.addEventListener('pointercancel', () => { start = null; });
    histo.addEventListener('click', (e) => e.stopPropagation());
  })();

  // Record
  let recTimer = 0;
  function setRec(on) {
    state.rec = on;
    lab.classList.toggle('recording', on);
    $('#recLabel').textContent = on ? 'Stop' : 'Record';
    clearInterval(recTimer);
    if (on) {
      state.recSecs = 0; $('#labRecTime').textContent = clock(0);
      recTimer = setInterval(() => { state.recSecs++; $('#labRecTime').textContent = clock(state.recSecs); }, 1000);
    }
    toast(on ? 'Recording' : 'Stopped. The take is on the card.');
  }
  $('#recBtn').addEventListener('click', () => setRec(!state.rec));

  // The app's keys: H Z P G and F9
  addEventListener('keydown', (e) => {
    if ((e.target instanceof Element && e.target.closest('input, textarea, select, [contenteditable]')) || e.ctrlKey || e.metaKey || e.altKey) return;
    if (!labVisible) return;
    const k = e.key.toLowerCase();
    const map = { h: 'histogram', z: 'zebras', p: 'peaking', g: 'grid' };
    if (map[k]) { setTool(map[k], !state[map[k]]); toast(`${map[k][0].toUpperCase() + map[k].slice(1)} ${state[map[k]] ? 'on' : 'off'}`); }
    else if (e.key === 'F9') { e.preventDefault(); setRec(!state.rec); }
  });

  /* ---------- Presets ---------- */
  const PRESETS = [
    '24p · XAVC HS 4K · 1/50 · F2.8 · ISO 640',
    '60p · XAVC HS 4K · 1/125 · F2.8 · ISO 640',
    '120p · XAVC HS 4K · 1/1000 · F5.6 · ISO 12800',
    'S&Q 240fps · XAVC S HD · 1/1000 · F4.5 · ISO 12800',
  ];
  const presetShot = $('.preset-shot'), presetStatus = $('#presetStatus');
  $$('.hot').forEach((h) => h.addEventListener('click', () => {
    const i = +h.dataset.preset;
    $$('.hot').forEach((x) => { x.classList.toggle('on', x === h); x.setAttribute('aria-pressed', String(x === h)); });
    presetShot.classList.toggle('moved', i !== 0);
    presetStatus.textContent = PRESETS[i];
    presetStatus.classList.remove('flash'); void presetStatus.offsetWidth; presetStatus.classList.add('flash');
    setTimeout(() => presetStatus.classList.remove('flash'), 900);
  }));
  $('.hot[data-preset="0"]').classList.add('on');

  /* ---------- S&Q ---------- */
  const sq = { shoot: 240, play: 24 };
  function showSq() {
    const f = sq.shoot / sq.play;
    const txt = Number.isInteger(f) ? String(f) : f.toFixed(1);
    $('#sqFactor').textContent = txt;
    $('#sqSecs').textContent = `${txt} s`;
    $('#sqText').textContent = f <= 1 ? 'Plays at normal speed. Pick a slower playback rate to slow it down.' : `One second of a take-off plays for ${txt} seconds.`;
    $('#birdFast').style.setProperty('--d', '1.4s');
    $('#birdSlow').style.setProperty('--d', `${(1.4 * Math.max(1, f)).toFixed(2)}s`);
  }
  onGroup('sqShoot', (v) => { sq.shoot = +v; showSq(); });
  onGroup('sqPlay', (v) => { sq.play = +v; showSq(); });
  showSq();

  /* ---------- Photo modes ---------- */
  function showMode(i) {
    $$('#photoShow [role="tab"]').forEach((b) => b.setAttribute('aria-selected', String(b.dataset.mode === String(i))));
    $$('.pane, .photo-real [data-mode]').forEach((p) => { p.hidden = p.dataset.mode !== String(i); });
    if (+i === 2) runExposureRing();
  }
  $$('#photoShow [role="tab"]').forEach((b) => b.addEventListener('click', () => showMode(b.dataset.mode)));

  const DRIVE = {
    single: 'One photo each time you press <b>Take photo</b>, or <kbd>F9</kbd>.',
    burst: 'Pick a speed, then hold <b>Take photo</b> down. The camera keeps shooting until you let go.',
    timer: 'Pick the delay, press <b>Take photo</b>, and step back.',
    bracket: 'Pick the step and how many photos. <b>One press</b> takes the whole set; <b>Press each</b> takes one per press. That\'s how you\'d shoot a set for HDR.',
  };
  onGroup('drive', (v) => { $('#driveOut').innerHTML = DRIVE[v]; });
  $('#driveOut').innerHTML = DRIVE.bracket;

  // Time-lapse, worded like the app: "Shoots for 1h 40m. Plays for 25 s at 24 fps."
  const tlEvery = $('#tlEvery'), tlCount = $('#tlCount');
  const countFrom = (t) => {
    const raw = 10 * Math.pow(999.9, t / 100);
    const step = raw < 100 ? 5 : raw < 1000 ? 25 : 100;
    return Math.min(9999, Math.max(10, Math.round(raw / step) * step));
  };
  tlCount.value = String(Math.round(100 * Math.log(60) / Math.log(999.9)));
  const dur = (s) => {
    s = Math.round(s);
    if (s < 60) return `${s} s`;
    const h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, sec = s % 60;
    if (h) return `${h}h ${m}m`;
    return sec ? `${m}m ${pad(sec)}s` : `${m}m`;
  };
  function showTl() {
    const every = +tlEvery.value, count = countFrom(+tlCount.value);
    $('#tlEveryTxt').textContent = `${every} s`;
    $('#tlCountTxt').textContent = count.toLocaleString('en');
    $('#tlShoots').textContent = dur(every * count);
    $('#tlPlays').textContent = dur(count / 24);
  }
  tlEvery.addEventListener('input', showTl); tlCount.addEventListener('input', showTl); showTl();

  // Long exposure
  let lexp = 30;
  const lexpArc = $('#lexpArc');
  function runExposureRing() {
    lexpArc.classList.remove('run'); void lexpArc.getBoundingClientRect();
    lexpArc.style.setProperty('--d', `${(0.8 + Math.log2(lexp + 1) * 0.45).toFixed(2)}s`);
    lexpArc.classList.add('run');
  }
  function showLexp() {
    $('#lexpLen').textContent = lexp < 60 ? `${lexp} s` : `${lexp / 60} min`;
    let t = lexp <= 30
      ? 'The camera times it itself.'
      : 'Longer than 30 seconds, the app holds the shutter open on Bulb, so keep the window open and the computer awake until it\'s done.';
    if (lexp <= 4) t += ' Silky water: ½ to 4 s.';
    else if (lexp >= 30) t += ' Moving clouds, empty streets and light trails: 30 s or more.';
    t += ' With Long Exposure NR on, the camera then takes as long again to finish.';
    $('#lexpText').textContent = t;
    runExposureRing();
  }
  onGroup('lexp', (v) => { lexp = +v; showLexp(); });
  showLexp();

  /* ---------- What it caught by itself ---------- */
  const TAKES = {
    animal: { tag: 'Animal', alt: 'A Douglas squirrel on the edge of a bird bath leaps up to a branch, slowed down, with the low sun behind the trees.',
      cap: 'A Douglas squirrel leaps from the bath to the branch, 4× slower. This one was filmed by hand: squirrels are hit and miss for the detector so far.' },
    bird: { tag: 'Bird · 0.91', alt: 'A Steller\'s jay perched beside a suet cage, sharp against a soft background.',
      cap: 'A Steller\'s jay by the suet cage, in focus. Bird was picked, so it waited for a bird (0.91 sure).' },
    person: { tag: 'Person · 0.96', alt: 'Remington walks in front of the camera at the feeder.',
      cap: 'Walk in front of it and it rolls. Handy for filming yourself, or for checking it works before you leave.' },
  };
  const takeVid = $('#takeVid');
  if (takeVid) {
    $$('#takes [data-take]').forEach((b) => b.addEventListener('click', () => {
      const k = b.dataset.take, t = TAKES[k];
      $$('#takes [data-take]').forEach((x) => x.setAttribute('aria-selected', String(x === b)));
      takeVid.poster = `assets/auto-${k}.jpg`;
      takeVid.src = `assets/auto-${k}.mp4`;
      takeVid.setAttribute('aria-label', t.alt);
      $('#takeTag').textContent = t.tag;
      $('#takeCap').textContent = t.cap;
      const r = takeVid.getBoundingClientRect();
      if (!reduced && r.bottom > 0 && r.top < innerHeight) takeVid.play().catch(() => {});
    }));
  }

  /* ---------- How far away ---------- */
  const N = {
    you: (ic, b, small) => ({ ic, b, small, cls: 'you' }),
    dev: (ic, b, small, tag) => ({ ic, b, small, tag }),
    cam: { ic: 'i-camera', b: 'Camera', small: 'at the feeder', cls: 'cam-node' },
  };
  const L = {
    hot: { cls: 'l-hot', t: 'iPhone hotspot' },
    ts: { cls: 'l-ts', t: 'Tailscale' },
    wifi: { cls: 'l-wifi', t: 'home Wi-Fi' },
    tsm: { cls: 'l-ts', t: 'Tailscale · mobile data' },
  };
  const JOURNEY = [
    { title: 'Next to it: just your phone',
      text: 'The iPhone app runs the camera on its own, over the phone\'s hotspot. No Wi-Fi out there, and no computer.',
      chain: [N.you('i-phone', 'iPhone', 'the app'), L.hot, N.cam] },
    { title: 'In the house: the full app',
      text: 'Put the camera on your home Wi-Fi and open the app on your computer: every setting, presets, peaking and time-lapse. The picture arrives in 0.20 s.',
      chain: [N.you('i-desktop', 'Computer', 'the full app'), L.wifi, N.cam] },
    { title: 'Across town: your phone, through your computer',
      text: 'With Tailscale on both, your phone\'s browser reaches the app on your computer at home. Tap to focus, record, change settings.',
      chain: [N.you('i-phone', 'Phone', 'browser'), L.tsm, N.dev('i-desktop', 'Computer', 'at home'), L.wifi, N.cam] },
    { title: 'Not even there: it films by itself',
      text: 'Turn on Automatic recording before you go. It sees the animal, focuses and records by itself, and stops once it\'s gone.',
      chain: [N.dev('i-desktop', 'Computer', 'automatic recording', 'AUTO'), L.wifi, N.cam] },
  ];
  const chain = $('#chain');
  function showStep(i) {
    const s = JOURNEY[i];
    $$('#journey [data-step]').forEach((b) => b.setAttribute('aria-selected', String(+b.dataset.step === i)));
    $$('#journey .ring-arc, #journey .ring-txt').forEach((el) => {
      const k = +el.dataset.step;
      el.classList.toggle('on', k === i); el.classList.toggle('in', k < i);
    });
    $('#jTitle').textContent = s.title;
    $('#jText').textContent = s.text;
    chain.innerHTML = s.chain.map((n, k) => {
      const delay = `style="animation-delay:${k * 70}ms"`;
      if (n.t) return `<div class="link ${n.cls}" ${delay}><div class="link-line"></div><span>${n.t}</span></div>`;
      return `<div class="node ${n.cls || ''}" ${delay}><div class="node-ic"><svg class="ic"><use href="#${n.ic}"/></svg>${n.tag ? `<span class="tag">${n.tag}</span>` : ''}</div><b>${n.b}</b><small>${n.small}</small></div>`;
    }).join('');
  }
  $$('#journey [data-step]').forEach((b) => b.addEventListener('click', () => { showStep(+b.dataset.step); stopTour(); }));
  // The rings themselves can be clicked too (the buttons below are the keyboard way in)
  showStep(0);
  // Walks through the steps by itself until someone clicks one
  let tour = 0, tourIdx = 0;
  function stopTour() { clearInterval(tour); tour = -1; }
  if (!reduced && 'IntersectionObserver' in window) {
    new IntersectionObserver(([e]) => {
      if (tour === -1) return;
      clearInterval(tour);
      if (e.isIntersecting) tour = setInterval(() => { tourIdx = (tourIdx + 1) % JOURNEY.length; showStep(tourIdx); }, 4200);
    }, { threshold: 0.4 }).observe($('#journey'));
  }

  /* ---------- Real clips loop only while on screen ---------- */
  $$('video.loop').forEach((v) => {
    if (reduced) { v.pause(); v.controls = true; return; }
    if (!('IntersectionObserver' in window)) { v.play().catch(() => {}); return; }
    new IntersectionObserver(([e]) => {
      if (e.isIntersecting) v.play().catch(() => {}); else v.pause();
    }, { threshold: 0.25 }).observe(v);
  });

  /* ---------- Bento glow follows the pointer ---------- */
  $$('.tile').forEach((t) => t.addEventListener('pointermove', (e) => {
    const r = t.getBoundingClientRect();
    t.style.setProperty('--mx', `${e.clientX - r.left}px`); t.style.setProperty('--my', `${e.clientY - r.top}px`);
  }));

  /* ---------- Cameras ---------- */
  const CAMS = [
    ['Alpha full frame', [['A7S III', 'tested'], ['A9 II', 'user'], ['A1 II'], ['A1'], ['A9 III'], ['A7R VI'], ['A7R V'], ['A7CR'], ['A7 V'], ['A7 IV'], ['A7C II']]],
    ['Alpha APS-C', [['a6700']]],
    ['Cinema Line', [['FX6', '', 'firmware 3.00+'], ['FX3', '', 'firmware 2.00+'], ['FX3A'], ['FX2'], ['FX30'], ['BURANO']]],
    ['Vlogging', [['ZV-E1'], ['ZV-E10 II']]],
    ['Camcorders', [['PXW-Z300'], ['PXW-Z200'], ['HXR-NX800']]],
    ['Other', [['RX1R III'], ['ILX-LR1']]],
    ['Won\'t work over Wi-Fi', [['A7R IV', 'no'], ['A7R IVA', 'no'], ['A7C', 'no'], ['RX0 II', 'no'], ['BRC-AM7', 'no'], ['FR7', 'no']]],
  ];
  const ROMAN = { II: '2', III: '3', IV: '4', V: '5', VI: '6' };
  const norm = (s) => s.toLowerCase().replace(/α/g, 'a').replace(/[^a-z0-9]/g, '');
  function aliases(name) {
    const out = new Set([norm(name)]);
    const parts = name.split(' ');
    if (parts.length > 1 && ROMAN[parts[parts.length - 1]]) {
      const head = norm(parts.slice(0, -1).join(''));
      const r = parts[parts.length - 1];
      [ROMAN[r], 'm' + ROMAN[r], 'm' + r.toLowerCase(), 'mark' + r.toLowerCase(), 'mark' + ROMAN[r]].forEach((t) => out.add(head + t));
    }
    return [...out];
  }
  function cleanQuery(q) {
    let n = norm(q).replace(/^sony/, '');
    n = n.replace(/^alpha/, 'a').replace(/^ilce/, 'a');
    return n;
  }
  const camsEl = $('#cams');
  const all = [];
  camsEl.innerHTML = CAMS.map(([g, list]) => `<div class="cam-group"><h4>${g}</h4><div class="cam-list">${list.map(([n, s, note]) => {
    const st = s || 'should';
    all.push({ n, st, note, a: aliases(n) });
    return `<span class="cam ${st === 'should' ? '' : st}" data-cam="${n}"><i class="st st-${st === 'tested' ? 'tested' : st === 'user' ? 'user' : st === 'no' ? 'no' : 'should'}"></i>${n}${note ? ` <small>${note}</small>` : ''}${st === 'tested' ? ' <small>tested</small>' : ''}${st === 'user' ? ' <small>user confirmed</small>' : ''}</span>`;
  }).join('')}</div></div>`).join('');

  const VERDICT = {
    tested: (c) => [`${c.n}: tested`, 'This is the camera everything is tested on, with firmware 5.01.', 'tested'],
    user: (c) => [`${c.n}: confirmed by a user`, 'Sony doesn\'t list it for Wi-Fi, but it connects with PC Remote and Pairing. To click to focus, set Focus area to Spot S, M or L (Flexible Spot). Time-lapse and card formatting don\'t work on it.', 'user'],
    should: (c) => [`${c.n}: should work`, `Sony lists it for Wi-Fi remote control, but nobody has tried it with the app yet.${c.note ? ` Needs ${c.note.replace('+', ' or later')}.` : ''} Update its firmware first, and tell us how it goes.`, 'should'],
    no: (c) => [`${c.n}: won't work over Wi-Fi`, 'It\'s in Sony\'s software but can\'t be reached over Wi-Fi. A wired connection could bring it in; that hasn\'t been built yet.', 'no'],
  };
  const verdictEl = $('#camVerdict');
  function setVerdict(title, body, st) {
    verdictEl.innerHTML = `${st ? `<i class="st st-${st}"></i>` : ''}<div><b>${title}</b>${body}</div>`;
  }
  const defaultVerdict = () => setVerdict('Type a model to check it', 'Like A7 IV, FX3, ZV-E1 or ILCE-7SM3.', '');
  defaultVerdict();
  $('#camSearch').addEventListener('input', (e) => {
    const q = cleanQuery(e.target.value);
    const chips = $$('.cam', camsEl);
    if (q.length < 2) { camsEl.classList.remove('filtering'); chips.forEach((c) => c.classList.remove('match')); defaultVerdict(); return; }
    const exact = all.find((c) => c.a.includes(q));
    const partial = all.filter((c) => c.a.some((a) => a.startsWith(q)));
    const hits = exact ? [exact] : partial;
    camsEl.classList.add('filtering');
    chips.forEach((c) => c.classList.toggle('match', hits.some((h) => h.n === c.dataset.cam)));
    if (exact) { const [t, b, st] = VERDICT[exact.st](exact); setVerdict(t, b, st); }
    else if (partial.length) setVerdict(`Did you mean ${partial.slice(0, 3).map((c) => c.n).join(', ')}${partial.length > 3 ? '…' : ''}?`, 'Keep typing. If your exact model isn\'t on this list, it isn\'t supported.', '');
    else setVerdict('Not on Sony\'s list', 'Cameras that aren\'t listed, like the A7 III or a6400, aren\'t supported by Sony\'s software, so the app can\'t run them.', 'no');
  });

  /* ---------- AI prompt ---------- */
  const prompt = `Help me set up ${S.name} for my Sony camera, one step at a time.
Read the guide at ${S.agentsGuide}
Ask which camera I have and whether I'm on Windows or Mac, then walk me through it in plain language.
Don't ask me for my camera password.`;
  $('#aiPrompt').textContent = prompt;
  $('#copyPrompt').addEventListener('click', async (e) => {
    const btn = e.currentTarget, label = $('span', btn);
    try { await navigator.clipboard.writeText(prompt); label.textContent = 'Copied'; }
    catch (err) {
      const r = document.createRange(); r.selectNodeContents($('#aiPrompt'));
      const sel = getSelection(); sel.removeAllRanges(); sel.addRange(r);
      label.textContent = 'Selected: press Ctrl+C';
    }
    setTimeout(() => { label.textContent = 'Copy'; }, 2200);
  });

  /* ---------- Socials, the maker and the tip jar ---------- */
  const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const SOCIAL = [['instagram', 'Instagram'], ['youtube', 'YouTube'], ['tiktok', 'TikTok'], ['x', 'X'], ['discord', 'Discord'], ['github', 'GitHub']];
  const socialUrl = (k) => (k === 'github' ? S.github : (S.socials || {})[k]) || '';
  const icon = (k) => `<svg class="ic"><use href="#s-${k}"/></svg>`;
  $('#socials').innerHTML = SOCIAL.filter(([k]) => socialUrl(k)).map(([k, label]) =>
    `<li><a href="${esc(socialUrl(k))}" rel="noopener me" aria-label="${label}" title="${label}">${icon(k)}</a></li>`).join('');
  $$('[data-maker]').forEach((el) => { el.textContent = S.maker || ''; });

  // Where to follow along: every profile that's set, but not GitHub or Discord
  const follow = SOCIAL.filter(([k]) => !['github', 'discord'].includes(k) && socialUrl(k));
  const tip = S.support ? `<a class="tip" href="${esc(S.support)}" rel="noopener">Buy me a coffee</a>` : '';
  const maker = $('#maker');
  if (maker && S.maker) {
    maker.innerHTML = `<span>Made by <b>${esc(S.maker)}</b></span>` +
      (follow.length ? `<span class="follow">${follow.map(([k, label]) =>
        `<a href="${esc(socialUrl(k))}" rel="noopener me" aria-label="${label}" title="${label}">${icon(k)}</a>`).join('')}</span>` : '') +
      (tip ? `<span>${tip}</span>` : '');
  }

  // A quiet thank-you once someone has actually downloaded it
  const thanks = $('#thanks');
  if (thanks) {
    const first = follow[0];
    let msg = 'Thanks for trying it.';
    if (first) msg += ` New features and footage go up on <a href="${esc(socialUrl(first[0]))}" rel="noopener me">${first[1]}</a> first.`;
    if (S.support) msg += ` And if it saves you a walk to the camera, <a href="${esc(S.support)}" rel="noopener">there's a tip jar</a>.`;
    $$('[data-dl]').forEach((el) => el.addEventListener('click', () => {
      if (!/^https?:/.test(el.getAttribute('href') || '')) return;
      thanks.innerHTML = msg; thanks.hidden = false;
    }));
  }
})();
