'use strict';
/* ResourceLens dashboard */

// Smooth wheel scrolling: chart libraries register non-passive wheel listeners, which make the browser wait for
// JavaScript before every scroll step (visible lag when the cursor is over a chart). Charts here never use the wheel,
// so force those listeners passive and let scrolling run on the compositor thread.
(() => {
  const orig = EventTarget.prototype.addEventListener;
  EventTarget.prototype.addEventListener = function (type, fn, opts) {
    if (type === 'wheel' || type === 'mousewheel' || type === 'touchstart' || type === 'touchmove') {
      opts = typeof opts === 'object' && opts !== null ? { ...opts, passive: true } : { capture: !!opts, passive: true };
    }
    return orig.call(this, type, fn, opts);
  };
})();

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const state = {
  settings: null,
  init: null,
  boot: 0,
  page: 'overview',
  range: { kind: 'boot', from: 0, to: 0 },
  rangeStack: [],
  live: null,
  liveHist: [],
  procRows: null,
  procIndex: new Map(),
  procSort: { key: 'memArea', dir: -1 },
  liveSort: { key: 'mem', dir: -1 },
  liveExpanded: new Set(),
  moments: { metric: 'cpu', order: 'top', min: null, rows: [], total: 0 },
  sysBucket: 1000,
  draft: null,
  charts: {},
  drawer: null, // { kind: 'app'|'inst'|'moment', ... }
  exportTitles: true,
  exportRange: { kind: 'all', from: 0, to: 0 }, // independent of the page range bar (hidden on Settings)
};

/* ================= Show / hide (display only — recording never changes) ================= */
// page -> hideable parts, with the label key used in Settings
const UI_ITEMS = {
  perf: { cpu: 'pf.cpu', mem: 'pf.mem', disk: 'col.disk', net: 'col.net', gpu: 'pf.gpu', sensors: 'pf.sensors', cores: 'pf.coresTitle' },
  moments: { fg: 'col.fg', causes: 'col.causes', cpu: 'col.cpu', mem: 'col.mem', disk: 'col.disk', net: 'col.net', temp: 'metric.temp' },
  live: { cpu: 'col.cpu', mem: 'col.mem', disk: 'col.disk', net: 'col.net', gpu: 'col.gpu', details: 'view.details', endTask: 'pa.endTask' },
  processes: { memMax: 'col.memPeak', growth: 'col.growth', cpuAvg: 'col.cpuAvg', cpuMax: 'col.cpuPeak', ioR: 'col.diskR', ioW: 'col.diskW', net: 'col.net', gpuAvg: 'col.gpu', presence: 'col.presence' },
  insights: { peakMem: 'ins.peakMem', peakCpu: 'ins.peakCpu', peakTemp: 'ins.peakTemp', leaks: 'ins.leaks', episodes: 'ins.episodesShort', alerts: 'ins.alerts', topCpu: 'ins.topCpu', topDisk: 'ins.topDisk', topNet: 'ins.topNet' },
  startup: { publisher: 'col.publisher', source: 'col.source' },
  services: { descr: 'col.display', pid: 'col.pid', startType: 'col.startType' },
};
/* ================= Add-on pages (network.js, rules.js, counters.js) ================= */
// id -> { load(): Promise|void, stop?(), onLive?(live), subtitle?(): string }. The page's <section id="page-ID"> and nav
// button live in index.html; its strings are merged into I18N by the module ("nav.ID", "title.ID", …).
const PAGES = {};
function registerPage(id, mod, uiItems) {
  PAGES[id] = mod;
  if (uiItems) UI_ITEMS[id] = uiItems; // parts the user can hide in Settings
}
// Extra entries for the process context menu: (sel, app, inst) => [{ key, label, cls?, run() }]
const PROC_MENU_EXT = [];

const isHidden = (key) => (state.settings?.hidden || []).includes(key);
const pageShown = (page) => !isHidden(`page:${page}`);
const shown = (page, item) => !isHidden(`${page}:${item}`);

/** Hide navigation entries for hidden pages (Overview and Settings are always there). */
function applyVisibility() {
  $$('.nav-item').forEach((b) => {
    const p = b.dataset.page;
    b.classList.toggle('hidden', p !== 'overview' && p !== 'settings' && !pageShown(p));
  });
  if (state.page !== 'overview' && state.page !== 'settings' && !pageShown(state.page)) showPage('overview');
}

/* ================= Bridge ================= */
const hasHost = !!(window.chrome && window.chrome.webview);
let seq = 0;
const pending = new Map();
function call(method, params = {}) {
  if (hasHost) {
    return new Promise((res, rej) => {
      const id = ++seq;
      pending.set(id, { res, rej });
      window.chrome.webview.postMessage({ id, method, params });
    });
  }
  return fetch('/api', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ method, params }) })
    .then((r) => r.json())
    .then((m) => { if (m.error) throw new Error(m.error); return m.result; });
}
if (hasHost) {
  window.chrome.webview.addEventListener('message', (e) => {
    const m = e.data;
    if (m.event === 'live') { onLive(m.data); return; }
    if (m.event === 'settings') { state.settings = m.data; state.draft = null; if (state.page === 'settings') renderSettings(); return; }
    const p = pending.get(m.id);
    if (!p) return;
    pending.delete(m.id);
    if (m.error) p.rej(new Error(m.error)); else p.res(m.result);
  });
} else {
  setInterval(async () => {
    try { const l = await call('live'); if (l && l.ts !== state.live?.ts) onLive(l); } catch { /* dev only */ }
  }, 2000);
}

/* ================= i18n + formatting ================= */
const lang = () => state.settings?.language || 'ar';
const locale = () => (lang() === 'ar' ? 'ar-u-nu-latn' : 'en-US');
function t(key, vars) {
  let s = (I18N[lang()] && I18N[lang()][key]) ?? I18N.en[key] ?? key;
  if (vars) for (const k in vars) s = s.replace(`{${k}}`, vars[k]);
  return s;
}
function applyI18n() {
  document.documentElement.lang = lang();
  document.documentElement.dir = lang() === 'ar' ? 'rtl' : 'ltr';
  $$('[data-i18n]').forEach((el) => { el.textContent = t(el.dataset.i18n); });
  $$('[data-i18n-ph]').forEach((el) => { el.placeholder = t(el.dataset.i18nPh); });
  $('#langLabel').textContent = lang() === 'ar' ? 'EN' : 'ع';
}

const nf = (d = 1) => new Intl.NumberFormat(locale(), { maximumFractionDigits: d, minimumFractionDigits: 0 });
function fmtNum(v, d = 1) { return v == null || isNaN(v) ? '—' : nf(d).format(v); }
function fmtBytes(b, d) {
  if (b == null || isNaN(b)) return '—';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0; let v = Math.abs(b);
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  const digits = d ?? (v >= 100 || i === 0 ? 0 : v >= 10 ? 1 : 2);
  return `${nf(digits).format(Math.sign(b) * v)} ${u[i]}`;
}
const LRM = (s) => `‎${s}‎`; // keeps "11.1 GB" readable inside RTL canvas text
// "9.6% · 2.11 GHz · 41°C" as plain text: each part isolated (FSI…PDI), so numbers and units keep their order in Arabic
const ISO = (s) => `\u2068${s}\u2069`;
const isoParts = (...xs) => xs.filter((x) => x != null && x !== '').map(ISO).join(' · ');
function fmtRate(b) { return b == null || isNaN(b) ? '—' : `${fmtBytes(b)}/s`; }
function fmtPct(v) { return v == null || isNaN(v) ? '—' : `${nf(v < 10 ? 1 : 0).format(v)}%`; }
function fmtDur(ms) {
  if (ms == null || ms < 0) return '—';
  const s = Math.round(ms / 1000);
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
  const sp = lang() === 'ar' ? ' ' : '';
  const parts = [];
  if (d) parts.push(`${d}${sp}${t('u.d')}`);
  if (h) parts.push(`${h}${sp}${t('u.h')}`);
  if (m && d === 0) parts.push(`${m}${sp}${t('u.min')}`);
  if (!parts.length) parts.push(`${s}${sp}${t('u.s')}`);
  return parts.join(' ');
}
/** Time; with opts.date (or another day) the full date incl. year. opts.axis keeps chart labels compact. */
function fmtTime(ts, opts = {}) {
  if (!ts) return '—';
  const d = new Date(ts);
  const sameDay = d.toDateString() === new Date().toDateString();
  const withDate = opts.date || !sameDay;
  return new Intl.DateTimeFormat(locale(), {
    hour: 'numeric', minute: '2-digit',
    ...(opts.seconds ? { second: '2-digit' } : {}),
    ...(withDate ? { day: 'numeric', month: opts.axis ? 'short' : 'long', ...(opts.axis && d.getFullYear() === new Date().getFullYear() ? {} : { year: 'numeric' }) } : {}),
  }).format(d);
}
/** Weekday, day month year, hour:minute — used wherever a precise moment is named. */
function fmtFull(ts, seconds) {
  if (!ts) return '—';
  return new Intl.DateTimeFormat(locale(), {
    weekday: 'long', day: 'numeric', month: 'long', year: 'numeric', hour: 'numeric', minute: '2-digit', ...(seconds ? { second: '2-digit' } : {}),
  }).format(new Date(ts));
}
/** Clock time only (no date) — for rows that show the day on a separate line. */
function fmtClock(ts, seconds = true) { return ts ? new Intl.DateTimeFormat(locale(), { hour: 'numeric', minute: '2-digit', ...(seconds ? { second: '2-digit' } : {}) }).format(new Date(ts)) : '—'; }
function fmtDay(ts) { return new Intl.DateTimeFormat(locale(), { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }).format(new Date(ts)); }
function fmtTemp(v) { return v == null || isNaN(v) ? '—' : `${nf(0).format(v)}°C`; }
function tempClass(v) { return v == null ? '' : v >= 85 ? 'temp-hot' : v >= 70 ? 'temp-warm' : 'temp-ok'; }
function fmtMhz(v) { return v == null || isNaN(v) ? '—' : v >= 1000 ? `${nf(2).format(v / 1000)} GHz` : `${nf(0).format(v)} MHz`; }
function fmtWatt(v) { return v == null || isNaN(v) ? '—' : `${nf(1).format(v)} W`; }
function fmtInterval(sec) { return sec >= 60 ? `${sec / 60}${t('u.min')}` : `${sec}${t('u.s')}`; }
function cssVar(n) { return getComputedStyle(document.documentElement).getPropertyValue(n).trim(); }
function alpha(color, a) {
  const c = color.replace('#', '');
  const n = parseInt(c.length === 3 ? c.split('').map((x) => x + x).join('') : c, 16);
  return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${a})`;
}
function toast(msg, ms = 2600) {
  const el = $('#toast');
  el.textContent = msg;
  el.classList.remove('hidden');
  clearTimeout(toast._t);
  toast._t = setTimeout(() => el.classList.add('hidden'), ms);
}
const N = (s) => `<span class="num">${s}</span>`;
/* Number fields are plain text inputs (a number input would show Arabic-Indic digits on Arabic Windows):
   read them through this, which also accepts digits typed on an Arabic keyboard. NaN when not a number. */
function numVal(v) {
  const s = String(v).replace(/[٠-٩]/g, (d) => d.charCodeAt(0) - 0x660).replace(/[۰-۹]/g, (d) => d.charCodeAt(0) - 0x6f0)
    .replace(/[٫,]/g, '.').trim();
  return s === '' ? NaN : Number(s);
}

/* metric helpers (moments, moment drawer) */
const METRICS = ['cpu', 'mem', 'disk', 'net', 'gpu'];
const MOMENT_METRICS = ['cpu', 'mem', 'disk', 'net', 'gpu', 'temp'];
function metricColor(m) { return cssVar({ cpu: '--c-cpu', mem: '--c-mem', disk: '--c-disk-r', net: '--c-net-rx', gpu: '--c-gpu', temp: '--c-disk-w' }[m]); }
function fmtMetric(m, v, memTotal) {
  if (m === 'mem') return memTotal ? `${fmtBytes(v, 1)} · ${fmtPct((v * 100) / memTotal)}` : fmtBytes(v, 1);
  if (m === 'disk' || m === 'net') return fmtRate(v);
  if (m === 'temp') return fmtTemp(v);
  return fmtPct(v);
}
/** value of a process row for a metric (moment/top contributor rows); "temp" = how hard it drives CPU + GPU */
function procVal(m, p) {
  if (m === 'mem') return p.mem;
  if (m === 'disk') return p.io ?? ((p.ioR ?? 0) + (p.ioW ?? 0));
  if (m === 'net') return p.net ?? ((p.netRx ?? 0) + (p.netTx ?? 0));
  if (m === 'temp') return (p.cpu ?? 0) + (p.gpu ?? 0);
  return p[m];
}
function fmtProcVal(m, v) {
  if (m === 'mem') return fmtBytes(v, 1);
  if (m === 'disk' || m === 'net') return fmtRate(v);
  return fmtPct(v); // cpu, gpu and temp (cpu+gpu load)
}

/* ================= Range ================= */
const RANGE_MS = { '1h': 3600e3, '6h': 6 * 3600e3, '24h': 86400e3, '7d': 7 * 86400e3, '30d': 30 * 86400e3 };
function currentRange() {
  const now = Date.now();
  const k = state.range.kind;
  if (k === 'custom') return { from: state.range.from, to: state.range.to };
  if (k === 'boot') return { from: state.boot || now - 3600e3, to: now };
  return { from: now - RANGE_MS[k], to: now };
}
function rangeLabel(r = currentRange(), toNow = state.range.kind !== 'custom') {
  const secs = r.to - r.from < 2 * 3600e3;
  const end = toNow ? `${t('now')} (${fmtFull(r.to)})` : fmtFull(r.to, secs);
  return t('range.fromTo', { a: fmtFull(r.from, secs), b: end });
}
const EXPORT_KINDS = ['all', 'boot', '24h', '7d', 'custom'];
function exportRange() {
  const now = Date.now();
  const r = state.exportRange;
  if (r.kind === 'custom') return { from: r.from, to: r.to };
  if (r.kind === 'boot') return { from: state.boot || now - 3600e3, to: now };
  if (r.kind === 'all') return { from: state.init.ranges?.first || now - RANGE_MS['30d'], to: now };
  return { from: now - RANGE_MS[r.kind], to: now };
}
function toLocalInput(ts) {
  const d = new Date(ts - new Date(ts).getTimezoneOffset() * 60000);
  return d.toISOString().slice(0, 19);
}

/* ================= Charts ================= */
function chartTheme() {
  return { text: cssVar('--text'), muted: cssVar('--muted'), grid: cssVar('--grid'), surface: cssVar('--surface-solid'), border: cssVar('--border-strong'), font: cssVar('--font') };
}
function getChart(id) {
  let c = state.charts[id];
  const el = document.getElementById(id);
  if (c && !c.isDisposed() && c.getDom() === el) return c;
  if (c && !c.isDisposed()) c.dispose();
  c = echarts.init(el, null, { renderer: 'canvas' });
  state.charts[id] = c;
  new ResizeObserver(() => { if (!c.isDisposed()) c.resize(); }).observe(el);
  return c;
}
function disposeCharts(prefix = '') {
  for (const k of Object.keys(state.charts)) {
    if (!k.startsWith(prefix)) continue;
    if (!state.charts[k].isDisposed()) state.charts[k].dispose();
    delete state.charts[k];
  }
}
function series(name, data, color, o = {}) {
  return {
    type: 'line', name, data, showSymbol: false, symbol: 'circle', symbolSize: 5,
    sampling: data.length > 1500 ? 'lttb' : undefined, connectNulls: false, z: o.z ?? 2,
    stack: o.stack, lineStyle: { width: o.width ?? 1.8, color, type: o.dashed ? 'dashed' : 'solid', opacity: o.opacity ?? 1 },
    itemStyle: { color },
    areaStyle: o.area === false ? undefined : {
      color: o.stack ? alpha(color, 0.55) : new echarts.graphic.LinearGradient(0, 0, 0, 1, [{ offset: 0, color: alpha(color, o.areaA ?? 0.32) }, { offset: 1, color: alpha(color, 0) }]),
    },
    emphasis: { disabled: true },
  };
}
function byteAxis(...arrays) {
  let max = 0;
  for (const a of arrays) for (const v of a) if (v != null && v > max) max = v;
  if (max <= 0) return {};
  const raw = max / 4;
  let unit = 1;
  while (raw / unit >= 1024) unit *= 1024;
  const steps = [1, 2, 2.5, 3, 4, 5, 6, 8, 10, 16, 20, 25, 32, 50, 64, 100, 128, 200, 250, 256, 400, 500, 512, 1024];
  const step = (steps.find((s) => s * unit >= raw) ?? 1024) * unit;
  return { interval: step, max: Math.ceil(max / step) * step };
}
function zip(ts, vals) { const out = new Array(ts.length); for (let i = 0; i < ts.length; i++) out[i] = [ts[i], vals[i]]; return out; }
function baseOption({ yFmt, yMax, valFmt, bytes, legend, win }) {
  const th = chartTheme();
  return {
    animationDuration: 300,
    textStyle: { fontFamily: th.font },
    grid: { left: 10, right: 14, top: 14, bottom: 6, containLabel: true },
    // A visible window inside the period (moved by the navigator); the wheel stays free for page scrolling.
    dataZoom: win ? [{ type: 'inside', xAxisIndex: 0, filterMode: 'none', startValue: win[0], endValue: win[1],
      zoomOnMouseWheel: false, moveOnMouseWheel: false, moveOnMouseMove: false, preventDefaultMouseMove: false }] : [],
    tooltip: {
      // shown by hover-intent (see bindHoverTip): only after the pointer rests, hidden as soon as it moves
      trigger: 'axis', confine: true, triggerOn: 'none', transitionDuration: 0, hideDelay: 0,
      backgroundColor: th.surface, borderColor: th.border, borderWidth: 1, padding: [8, 12],
      textStyle: { color: th.text, fontFamily: th.font, fontSize: 12 },
      axisPointer: { type: 'line', lineStyle: { color: th.muted, width: 1, type: 'dashed' } },
      formatter: (ps) => {
        if (!ps.length) return '';
        let h = `<div style="color:${th.muted};margin-bottom:4px">${fmtFull(ps[0].value[0], true)}</div>`;
        for (const p of ps) {
          if (p.value[1] == null) continue;
          h += `<div style="display:flex;gap:14px;justify-content:space-between;align-items:center"><span><span style="display:inline-block;width:8px;height:8px;border-radius:50%;background:${p.color};margin-inline-end:6px"></span>${esc(p.seriesName)}</span><b style="direction:ltr">${valFmt(p.value[1])}</b></div>`;
        }
        return h;
      },
    },
    legend: legend ? { show: true, type: 'scroll', bottom: 0, textStyle: { color: th.muted, fontSize: 11 }, icon: 'roundRect', itemWidth: 10, itemHeight: 6 } : { show: false },
    xAxis: {
      type: 'time', boundaryGap: false,
      axisLine: { lineStyle: { color: th.grid } }, axisTick: { show: false },
      axisLabel: { color: th.muted, hideOverlap: true, fontSize: 11, formatter: (v) => fmtTime(v, { axis: true }) },
      splitLine: { show: false },
    },
    yAxis: {
      type: 'value', min: 0, max: yMax, splitNumber: 4, ...(bytes || {}),
      splitLine: { lineStyle: { color: th.grid } },
      axisLabel: { color: th.muted, fontSize: 11, formatter: (v) => LRM(yFmt(v)) },
    },
  };
}
function legend(id, items) {
  $(id).innerHTML = items.map(([label, color]) => `<span style="--lc:${color}">${esc(label)}</span>`).join('');
}
function noDataGraphic(empty) {
  return empty ? { type: 'text', left: 'center', top: 'middle', style: { text: t('noData'), fill: cssVar('--muted'), font: `12px ${cssVar('--font')}`, width: 320, overflow: 'break', align: 'center' } } : null;
}

const activeCharts = new Set();
function hideAllTips() { for (const c of activeCharts) if (!c.isDisposed()) c.__hideTip?.(); else activeCharts.delete(c); }

/**
 * Chart interactions: drag horizontally to drill into that period (re-queried at full resolution),
 * click to open the exact moment under the cursor.
 */
function bindChartInteractions(chart, metric, opts = {}) {
  const zr = chart.getZr();
  zr.off('mousedown'); zr.off('mousemove'); zr.off('mouseup'); zr.off('globalout');
  const dom = chart.getDom();
  let drag = null;
  let box = null;
  const inGrid = (x, y) => chart.containPixel({ gridIndex: 0 }, [x, y]);

  // Hover intent: the detail tooltip appears only after the pointer rests ~0.5 s on a point,
  // and disappears the moment it moves or leaves the chart.
  let tipTimer = null;
  const hideTip = () => { clearTimeout(tipTimer); tipTimer = null; chart.dispatchAction({ type: 'hideTip' }); };
  chart.__hideTip = hideTip;
  activeCharts.add(chart);

  zr.on('mousedown', (e) => { hideTip(); if (inGrid(e.offsetX, e.offsetY)) drag = { x0: e.offsetX }; });
  zr.on('mousemove', (e) => {
    hideTip();
    if (!drag && inGrid(e.offsetX, e.offsetY)) {
      const x = e.offsetX, y = e.offsetY;
      tipTimer = setTimeout(() => {
        for (const c of activeCharts) if (c !== chart && !c.isDisposed()) c.__hideTip?.();
        chart.dispatchAction({ type: 'showTip', x, y });
      }, 500);
    }
    if (!drag || opts.noDrag) return;
    const dx = e.offsetX - drag.x0;
    if (Math.abs(dx) < 6) return;
    if (!box) { box = document.createElement('div'); box.className = 'sel-box'; dom.appendChild(box); }
    box.style.left = `${Math.min(drag.x0, e.offsetX)}px`;
    box.style.width = `${Math.abs(dx)}px`;
  });
  const finish = (e) => {
    if (e.type === 'globalout') hideTip();
    if (!drag) return;
    const x0 = drag.x0, x1 = e.offsetX;
    drag = null;
    if (box) { box.remove(); box = null; }
    if (e.type === 'globalout') return;
    if (!opts.noDrag && Math.abs(x1 - x0) >= 6) {
      const a = chart.convertFromPixel({ gridIndex: 0 }, [Math.min(x0, x1), e.offsetY])[0];
      const b = chart.convertFromPixel({ gridIndex: 0 }, [Math.max(x0, x1), e.offsetY])[0];
      if (b - a > 2000) drillTo(Math.floor(a), Math.ceil(b));
      return;
    }
    if (!inGrid(x1, e.offsetY)) return;
    const tval = chart.convertFromPixel({ gridIndex: 0 }, [x1, e.offsetY])[0];
    const bucket = opts.bucket ? opts.bucket() : state.sysBucket;
    // nearest bucket; inside it, the moment with the highest value of the clicked metric
    openMoment(Math.round(tval / bucket) * bucket, bucket, metric);
  };
  zr.on('mouseup', finish);
  zr.on('globalout', finish);
}

function drillTo(from, to) {
  state.rangeStack.push({ ...state.range });
  state.range = { kind: 'custom', from, to };
  syncRangeUi();
  updateSubtitle();
  loadPage();
}
function goBack() {
  const prev = state.rangeStack.pop();
  if (!prev) return;
  state.range = prev;
  syncRangeUi();
  updateSubtitle();
  loadPage();
}

/* ================= Navigation ================= */
const RANGE_PAGES = ['overview', 'moments', 'processes', 'insights'];
function showPage(page) {
  if (page !== 'perf') stopPerf();
  if (state.page !== page) try { PAGES[state.page]?.stop?.(); } catch (e) { console.error(e); }
  hideAllTips();
  $('#ctx').classList.add('hidden');
  state.page = page;
  $$('.nav-item').forEach((b) => b.classList.toggle('active', b.dataset.page === page));
  $$('.page').forEach((p) => p.classList.toggle('hidden', p.id !== `page-${page}`));
  $('#pageTitle').textContent = t(`title.${page}`);
  syncRangeUi();
  updateSubtitle();
  loadPage();
}
function syncRangeUi() {
  const showRange = RANGE_PAGES.includes(state.page);
  $('#rangeBar').classList.toggle('hidden', !showRange);
  $$('#rangeBar button').forEach((b) => b.classList.toggle('active', b.dataset.range === state.range.kind));
  $('#customRange').classList.toggle('hidden', !(showRange && state.range.kind === 'custom'));
  $('#btnBack').classList.toggle('hidden', !(showRange && state.rangeStack.length));
  if (state.range.kind === 'custom') {
    $('#cFrom').value = toLocalInput(state.range.from);
    $('#cTo').value = toLocalInput(state.range.to);
  }
}
function updateSubtitle() {
  const p = state.page;
  if (p === 'live') $('#pageSub').textContent = t('live.note', { s: fmtInterval(state.settings.intervalSeconds) });
  else if (p === 'perf') $('#pageSub').textContent = t('pf.last60');
  else if (['settings', 'startup', 'services'].includes(p)) $('#pageSub').textContent = '';
  else if (PAGES[p]) $('#pageSub').textContent = PAGES[p].subtitle?.() || '';
  else $('#pageSub').innerHTML = `<span class="num">${esc(rangeLabel())}</span>`;
}
async function loadPage() {
  try {
    if (state.page === 'overview') await loadOverview();
    else if (state.page === 'moments') await loadMoments(false);
    else if (state.page === 'live') renderLive();
    else if (state.page === 'processes') await loadProcesses();
    else if (state.page === 'insights') await loadInsights();
    else if (state.page === 'settings') renderSettings();
    else if (state.page === 'perf') startPerf();
    else if (state.page === 'startup') await loadStartup();
    else if (state.page === 'services') await loadServices();
    else if (PAGES[state.page]) await PAGES[state.page].load();
  } catch (e) { console.error(e); toast(e.message); }
}

/* ================= Process labels ================= */
function roleName(code) { return code ? t(`role.${code}`) : ''; }
function appName(p) { return (p.descr || (p.name || '').replace(/\.exe$/i, '')); }
function icon(p, cls = '') {
  const letter = esc((p.name || '?')[0].toUpperCase());
  return p.hasIcon !== false && p.nid
    ? `<img class="${cls}" src="icon/${p.nid}" onerror="this.outerHTML='<span class=&quot;ph ${cls}&quot;>${letter}</span>'">`
    : `<span class="ph ${cls}">${letter}</span>`;
}
function roleTag(role) { return role && role !== 'app' ? `<span class="role r-${esc(role)}">${esc(roleName(role))}</span>` : ''; }
/** App name on line 1; role + detail (or exe name) on line 2. */
function pname(p, extra = '') {
  const sub = p.role || p.detail
    ? `<span class="sub">${roleTag(p.role)}<i>${esc(p.detail || p.name || '')}</i></span>`
    : `<span>${esc(p.name)}</span>`;
  return `<div class="pname">${icon(p)}<div class="nm"><b>${esc(appName(p))} ${extra}</b>${sub}</div></div>`;
}
function causeLine(m, p) {
  const label = [roleName(p.role !== 'app' ? p.role : null), p.detail || p.title].filter(Boolean).join(' · ');
  return `<div class="cause">${icon(p)}<b>${esc(appName(p))}</b>${label ? `<span class="d">${esc(label)}</span>` : ''}<span class="v num">${fmtProcVal(m, procVal(m, p))}</span></div>`;
}
function fgCell(fg) {
  if (!fg) return '<span class="muted">—</span>';
  return `<div class="fg-cell">${icon(fg)}<div><b>${esc(appName(fg))}</b><span>${esc(fg.title || roleName(fg.role) || fg.name)}</span></div></div>`;
}
function barCell(text, frac, color) {
  const w = Math.max(0, Math.min(1, frac || 0)) * 100;
  return `<div class="bar-cell"><span class="num">${text}</span><span class="bar"><i style="width:${w}%;--bc:${color}"></i></span></div>`;
}
function isLeak(p) { return p.memSlopeMBh >= 20 && p.memR2 >= 0.6 && p.spanHours >= 0.5 && p.presence >= 0.5; }

/* ================= Overview ================= */
const ICONS = {
  cpu: '<svg viewBox="0 0 24 24"><rect x="6" y="6" width="12" height="12" rx="2" fill="none" stroke="currentColor" stroke-width="2"/><path d="M9 2v3M15 2v3M9 19v3M15 19v3M2 9h3M2 15h3M19 9h3M19 15h3" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>',
  mem: '<svg viewBox="0 0 24 24"><rect x="2" y="7" width="20" height="10" rx="2" fill="none" stroke="currentColor" stroke-width="2"/><path d="M6 17v3M10 17v3M14 17v3M18 17v3M7 10v4M12 10v4M17 10v4" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>',
  disk: '<svg viewBox="0 0 24 24"><ellipse cx="12" cy="6" rx="8" ry="3" fill="none" stroke="currentColor" stroke-width="2"/><path d="M4 6v12c0 1.7 3.6 3 8 3s8-1.3 8-3V6M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3" fill="none" stroke="currentColor" stroke-width="2"/></svg>',
  net: '<svg viewBox="0 0 24 24"><path d="M7 4v14m0 0-3-3m3 3 3-3M17 20V6m0 0-3 3m3-3 3 3" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>',
  gpu: '<svg viewBox="0 0 24 24"><rect x="2" y="6" width="20" height="12" rx="2" fill="none" stroke="currentColor" stroke-width="2"/><circle cx="15" cy="12" r="3" fill="none" stroke="currentColor" stroke-width="2"/><path d="M6 10h4M6 14h4" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>',
  bolt: '<svg viewBox="0 0 24 24"><path d="M13 2 4 14h7l-1 8 9-12h-7z" fill="none" stroke="currentColor" stroke-width="2" stroke-linejoin="round"/></svg>',
  trend: '<svg viewBox="0 0 24 24"><path d="m3 17 6-6 4 4 8-8M15 7h6v6" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>',
  clock: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9" fill="none" stroke="currentColor" stroke-width="2"/><path d="M12 7v5l3 2" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>',
  bell: '<svg viewBox="0 0 24 24"><path d="M6 8a6 6 0 1 1 12 0c0 7 3 9 3 9H3s3-2 3-9M10.3 21a1.9 1.9 0 0 0 3.4 0" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>',
  prev: '<svg viewBox="0 0 24 24"><path d="M15 6l-6 6 6 6" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>',
  next: '<svg viewBox="0 0 24 24"><path d="M9 6l6 6-6 6" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>',
};

const KPI_DEFS = [
  { id: 'cpu', color: '--c-cpu', on: (s) => s.trackCpu },
  { id: 'mem', color: '--c-mem', on: (s) => s.trackMemory },
  { id: 'disk', color: '--c-disk-r', on: (s) => s.trackDisk },
  { id: 'net', color: '--c-net-rx', on: (s) => s.trackNetwork },
  { id: 'gpu', color: '--c-gpu', on: (s) => s.trackGpu },
];

function buildKpis() {
  $('#kpis').innerHTML = KPI_DEFS.map((k) => `
    <div class="card kpi" id="kpi-${k.id}" style="--kc:var(${k.color})">
      <div class="kpi-top"><div class="kpi-label"><i>${ICONS[k.id]}</i>${t(`kpi.${k.id}`)}</div><span class="kpi-badge" id="kb-${k.id}"></span></div>
      <div class="kpi-value" id="kv-${k.id}">—</div>
      <div class="kpi-sub kpi-live" id="kl-${k.id}"></div>
      <div class="kpi-sub" id="ks-${k.id}"></div>
      <div class="spark" id="sp-${k.id}"></div>
    </div>`).join('');
}

function liveKpiValues(l) {
  if (!l) return {};
  const memPct = l.memTotal ? (l.memUsed * 100) / l.memTotal : null;
  return {
    cpu: {
      v: N(fmtPct(l.cpu)), spark: l.cpu,
      live: l.sensors ? `<span><b class="num ${tempClass(l.sensors.cpuTemp)}">${fmtTemp(l.sensors.cpuTemp)}</b></span><span><b class="num">${fmtMhz(l.sensors.cpuMhz)}</b></span>` : '',
    },
    mem: { v: `${N(fmtBytes(l.memUsed, 1))}<small>${t('kpi.of')} ${N(fmtBytes(l.memTotal, 0))}</small>`, badge: fmtPct(memPct), spark: memPct },
    disk: { v: N(fmtRate((l.diskR ?? 0) + (l.diskW ?? 0))), live: `<span>${t('kpi.read')} <b class="num">${fmtRate(l.diskR)}</b></span><span>${t('kpi.write')} <b class="num">${fmtRate(l.diskW)}</b></span>`, spark: (l.diskR ?? 0) + (l.diskW ?? 0) },
    net: { v: N(fmtRate((l.netRx ?? 0) + (l.netTx ?? 0))), live: `<span>↓ <b class="num">${fmtRate(l.netRx)}</b></span><span>↑ <b class="num">${fmtRate(l.netTx)}</b></span>`, spark: (l.netRx ?? 0) + (l.netTx ?? 0) },
    gpu: {
      v: N(fmtPct(l.gpu)), spark: l.gpu,
      live: l.sensors ? `<span><b class="num ${tempClass(l.sensors.gpuTemp)}">${fmtTemp(l.sensors.gpuTemp)}</b></span><span><b class="num">${fmtMhz(l.sensors.gpuMhz)}</b></span>` : '',
    },
  };
}

function updateKpisLive() {
  if (state.page !== 'overview' || !$('#kv-cpu')) return;
  const vals = liveKpiValues(state.live);
  for (const k of KPI_DEFS) {
    const on = k.on(state.settings);
    $(`#kpi-${k.id}`).classList.toggle('off', !on);
    const v = vals[k.id];
    $(`#kv-${k.id}`).innerHTML = !on ? t('kpi.off') : v ? v.v : '—';
    $(`#kb-${k.id}`).textContent = on && v?.badge ? v.badge : '';
    $(`#kl-${k.id}`).innerHTML = on && v?.live ? v.live : '';
    drawSpark(k, on);
  }
}

function drawSpark(k, on) {
  const pts = state.liveHist.map((l) => [l.ts, liveKpiValues(l)[k.id]?.spark ?? null]);
  const c = getChart(`sp-${k.id}`);
  const isPct = k.id === 'cpu' || k.id === 'mem' || k.id === 'gpu';
  c.setOption({
    animation: false,
    grid: { left: 0, right: 0, top: 4, bottom: 0 },
    xAxis: { type: 'time', show: false },
    yAxis: { type: 'value', show: false, min: 0, max: isPct ? 100 : (v) => Math.max(v.max * 1.15, 1024) },
    series: [series('', on ? pts : [], cssVar(k.color), { width: 1.5, areaA: 0.28 })],
  }, true);
}

function fillRangeKpis(sum) {
  const s = state.settings;
  const set = (id, html) => { const el = $(`#ks-${id}`); if (el) el.innerHTML = html; };
  const avgPeak = (a, p, f) => `<span>${t('kpi.avg')} <b class="num">${f(a)}</b></span><span>${t('kpi.peak')} <b class="num">${f(p)}</b></span>`;
  set('cpu', s.trackCpu ? avgPeak(sum.cpuAvg, sum.cpuMax, fmtPct) : '');
  set('mem', s.trackMemory ? avgPeak(sum.memAvg, sum.memMax, (v) => fmtBytes(v, 1)) : '');
  set('disk', s.trackDisk ? `<span>${t('kpi.read')} <b class="num">${fmtBytes(sum.diskR)}</b></span><span>${t('kpi.write')} <b class="num">${fmtBytes(sum.diskW)}</b></span>` : '');
  set('net', s.trackNetwork ? `<span>↓ <b class="num">${fmtBytes(sum.netRx)}</b></span><span>↑ <b class="num">${fmtBytes(sum.netTx)}</b></span>` : '');
  set('gpu', s.trackGpu ? avgPeak(sum.gpuAvg, sum.gpuMax, fmtPct) : '');
}

function renderStrip(sum) {
  const l = state.live;
  $('#strip').innerHTML = [
    `<span>${ICONS.clock}</span>`,
    `<span>${t('strip.boot')}: <b class="num">${fmtTime(state.boot, { date: true })}</b></span>`,
    '<span class="sep"></span>',
    `<span>${t('strip.uptime')}: <b class="tnum">${fmtDur(Date.now() - state.boot)}</b></span>`,
    '<span class="sep"></span>',
    `<span>${t('strip.covered')}: <b class="tnum">${fmtDur(sum.coveredMs)}</b></span>`,
    l ? `<span class="sep"></span><span>${t('strip.procs')}: <b class="num">${fmtNum(l.processCount, 0)}</b></span>` : '',
    `<span class="sep"></span><span>${t('strip.db')}: <b class="num">${fmtBytes(state.init.dbSize)}</b></span>`,
  ].join('');
}

async function loadOverview() {
  if (!$('#kv-cpu')) buildKpis();
  const r = currentRange();
  // With a view span narrower than the period, fetch finer points so the visible window keeps full detail.
  const span = chartSpan(r);
  const basePts = Math.min(1400, Math.max(300, Math.round(window.innerWidth / 1.1)));
  const points = span ? Math.min(20000, Math.round(((r.to - r.from) / span) * basePts)) : basePts;
  const [sys, sum, procs] = await Promise.all([
    call('system', { from: r.from, to: r.to, points }),
    call('summary', r),
    call('processes', { ...r, limit: 400 }),
  ]);
  state.sysBucket = sys.bucket;
  cacheProcs(procs);
  updateKpisLive();
  fillRangeKpis(sum);
  renderStrip(sum);
  drawSystemCharts(sys);
  renderTopTable('#ovTop', procs.items.slice(0, 8));
}

/* ---- view span (whole period / 6 h / 1 h / 30 min / 10 min) with a navigator ---- */
const SPANS = [0, 21600000, 3600000, 1800000, 600000];
state.span = (() => { try { return +(localStorage.getItem('rl.span') || 0); } catch { return 0; } })();
state.win = null; // [start, end] of the visible window
const OV_CHARTS = ['ch-cpu', 'ch-gpu', 'ch-mem', 'ch-temp', 'ch-disk', 'ch-net', 'ch-clock'];

function chartSpan(r) { return state.span && state.span < (r.to - r.from) * 0.95 ? state.span : 0; }
function buildSpanSeg() {
  $('#spanSeg').innerHTML = SPANS.map((s) => `<button data-v="${s}" class="${state.span === s ? 'active' : ''}">${t(`span.${s}`)}</button>`).join('');
  $$('#spanSeg button').forEach((b) => b.addEventListener('click', () => {
    state.span = +b.dataset.v;
    state.win = null;
    try { localStorage.setItem('rl.span', String(state.span)); } catch { /* private storage */ }
    buildSpanSeg();
    loadOverview();
  }));
}
function setWindow(a, b) {
  state.win = [a, b];
  for (const id of OV_CHARTS) {
    const c = state.charts[id];
    if (c && !c.isDisposed()) c.dispatchAction({ type: 'dataZoom', startValue: a, endValue: b });
  }
  $('#navLabel').textContent = `${fmtFull(a)} — ${fmtTime(b, { date: new Date(a).toDateString() !== new Date(b).toDateString() })}`;
}
function drawNavigator(sys, span) {
  const card = $('#navCard');
  card.classList.toggle('hidden', !span);
  if (!span) { state.win = null; return null; }
  const t0 = sys.t[0] ?? currentRange().from, t1 = sys.t[sys.t.length - 1] ?? currentRange().to;
  // keep the previous window if it still fits, otherwise show the latest part of the period
  let win = state.win && state.win[1] - state.win[0] === span && state.win[0] >= t0 && state.win[1] <= t1 ? state.win : [Math.max(t0, t1 - span), t1];
  const th = chartTheme();
  const nav = getChart('ch-nav');
  const memTotal = Math.max(1, ...sys.memTotal.filter((v) => v != null));
  void memTotal;
  const acc = cssVar('--accent');
  // The slider's own data shadow draws the whole period (CPU); the grid itself is hidden.
  nav.setOption({
    animation: false,
    grid: { left: 10, right: 10, top: 0, height: 1 },
    xAxis: { type: 'time', min: t0, max: t1, show: false },
    yAxis: { type: 'value', min: 0, max: 100, show: false },
    tooltip: { show: false },
    dataZoom: [{
      type: 'slider', xAxisIndex: 0, filterMode: 'none', startValue: win[0], endValue: win[1], left: 10, right: 10, top: 4, height: 50,
      borderColor: th.grid, backgroundColor: 'transparent', fillerColor: alpha(acc, 0.16), handleStyle: { color: acc, borderColor: acc },
      moveHandleStyle: { color: alpha(acc, 0.6) }, emphasis: { moveHandleStyle: { color: acc } },
      textStyle: { color: th.muted, fontSize: 10 }, labelFormatter: (v) => fmtTime(v, { axis: true }), showDetail: true, brushSelect: false,
      showDataShadow: true,
      dataBackground: { lineStyle: { color: cssVar('--c-cpu'), opacity: 0.6 }, areaStyle: { color: cssVar('--c-cpu'), opacity: 0.15 } },
      selectedDataBackground: { lineStyle: { color: cssVar('--c-cpu') }, areaStyle: { color: cssVar('--c-cpu'), opacity: 0.35 } },
    }],
    series: [{ type: 'line', data: zip(sys.t, sys.cpu), showSymbol: false, lineStyle: { opacity: 0 } }],
  }, true);
  nav.off('datazoom');
  nav.on('datazoom', () => {
    const dz = nav.getOption().dataZoom[0];
    setWindow(dz.startValue, dz.endValue);
  });
  return win;
}

function drawSystemCharts(sys) {
  const empty = sys.t.length === 0;
  const win = drawNavigator(sys, chartSpan(currentRange()));
  const C = {
    cpu: cssVar('--c-cpu'), gpu: cssVar('--c-gpu'), mem: cssVar('--c-mem'), commit: cssVar('--c-commit'),
    dr: cssVar('--c-disk-r'), dw: cssVar('--c-disk-w'), rx: cssVar('--c-net-rx'), tx: cssVar('--c-net-tx'),
  };
  const memTotal = Math.max(0, ...sys.memTotal.filter((v) => v != null));

  const pctAxis = { yFmt: (v) => `${v}%`, yMax: 100, valFmt: fmtPct, bytes: { interval: 25 }, win };
  const cpu = getChart('ch-cpu');
  cpu.group = '';
  cpu.setOption({
    ...baseOption(pctAxis),
    graphic: noDataGraphic(empty),
    series: [
      series(t('s.cpu'), zip(sys.t, sys.cpu), C.cpu),
      series(`${t('s.cpu')} · ${t('kpi.peak')}`, zip(sys.t, sys.cpuMax), C.cpu, { area: false, width: 1, opacity: 0.45, dashed: true }),
    ],
  }, true);
  legend('#lg-cpu', [[t('s.cpu'), C.cpu], [t('kpi.peak'), alpha(C.cpu, 0.5)]]);
  bindChartInteractions(cpu, 'cpu');

  const gpu = getChart('ch-gpu');
  gpu.group = '';
  gpu.setOption({
    ...baseOption(pctAxis),
    graphic: noDataGraphic(empty),
    series: [
      series(t('s.gpu'), zip(sys.t, sys.gpu), C.gpu),
      series(`${t('s.gpu')} · ${t('kpi.peak')}`, zip(sys.t, sys.gpuMax), C.gpu, { area: false, width: 1, opacity: 0.45, dashed: true }),
    ],
  }, true);
  legend('#lg-gpu', [[t('s.gpu'), C.gpu], [t('kpi.peak'), alpha(C.gpu, 0.5)]]);
  bindChartInteractions(gpu, 'gpu');

  // temperatures (avg line + dashed peak per sample)
  const temps = [...sys.cpuTemp, ...sys.gpuTemp, ...sys.cpuTempMax, ...sys.gpuTempMax].filter((v) => v != null);
  const tMax = temps.length ? Math.max(60, Math.ceil((Math.max(...temps) + 5) / 10) * 10) : 100;
  const tmp = getChart('ch-temp');
  tmp.group = '';
  tmp.setOption({
    ...baseOption({ yFmt: (v) => `${v}°`, valFmt: fmtTemp, bytes: { min: 20, max: tMax, interval: 10 }, win }),
    graphic: noDataGraphic(empty || !temps.length),
    series: [
      series(t('s.cpuTemp'), zip(sys.t, sys.cpuTemp), C.cpu, { areaA: 0.18 }),
      series(t('s.gpuTemp'), zip(sys.t, sys.gpuTemp), C.gpu, { area: false, width: 1.6 }),
      series(`${t('s.cpuTemp')} · ${t('kpi.peak')}`, zip(sys.t, sys.cpuTempMax), C.cpu, { area: false, width: 1, opacity: 0.4, dashed: true }),
    ],
  }, true);
  legend('#lg-temp', [[t('s.cpuTemp'), C.cpu], [t('s.gpuTemp'), C.gpu]]);
  bindChartInteractions(tmp, 'temp');

  // clocks (MHz, left axis) + package power (W, right axis)
  const clk = getChart('ch-clock');
  clk.group = '';
  const clkOpt = baseOption({ yFmt: (v) => fmtMhz(v), valFmt: fmtMhz, win });
  const pw = cssVar('--c-disk-r');
  clk.setOption({
    ...clkOpt,
    yAxis: [
      { ...clkOpt.yAxis, max: undefined },
      { type: 'value', min: 0, splitLine: { show: false }, axisLabel: { color: chartTheme().muted, fontSize: 11, formatter: (v) => LRM(`${v} W`) } },
    ],
    graphic: noDataGraphic(empty || !sys.cpuMhz.some((v) => v != null)),
    series: [
      series(t('s.cpuMhz'), zip(sys.t, sys.cpuMhz), C.cpu, { area: false, width: 1.6 }),
      series(t('s.gpuMhz'), zip(sys.t, sys.gpuMhz), C.gpu, { area: false, width: 1.6 }),
      { ...series(t('s.power'), zip(sys.t, sys.pkgPower), pw, { areaA: 0.15, width: 1.2 }), yAxisIndex: 1 },
    ],
  }, true);
  // power values are watts, not MHz: format that series separately in the tooltip
  clk.setOption({
    tooltip: {
      formatter: (ps) => {
        const th = chartTheme();
        let h = `<div style="color:${th.muted};margin-bottom:4px">${fmtFull(ps[0].value[0], true)}</div>`;
        for (const p of ps) {
          if (p.value[1] == null) continue;
          const v = p.seriesIndex === 2 ? fmtWatt(p.value[1]) : fmtMhz(p.value[1]);
          h += `<div style="display:flex;gap:14px;justify-content:space-between"><span><span style="display:inline-block;width:8px;height:8px;border-radius:50%;background:${p.color};margin-inline-end:6px"></span>${esc(p.seriesName)}</span><b style="direction:ltr">${v}</b></div>`;
        }
        return h;
      },
    },
  });
  legend('#lg-clock', [[t('s.cpuMhz'), C.cpu], [t('s.gpuMhz'), C.gpu], [t('s.power'), pw]]);
  bindChartInteractions(clk, 'cpu');

  const mem = getChart('ch-mem');
  mem.group = '';
  mem.setOption({
    ...baseOption({ yFmt: (v) => fmtBytes(v), valFmt: (v) => fmtBytes(v, 2), bytes: byteAxis([memTotal], sys.commit), win }),
    graphic: noDataGraphic(empty),
    series: [
      series(t('s.memUsed'), zip(sys.t, sys.mem), C.mem),
      series(t('s.memPeak'), zip(sys.t, sys.memMax), C.mem, { area: false, width: 1, opacity: 0.55, dashed: true }),
      series(t('s.commit'), zip(sys.t, sys.commit), C.commit, { area: false, width: 1.3 }),
    ],
  }, true);
  legend('#lg-mem', [[t('s.memUsed'), C.mem], [t('s.commit'), C.commit]]);
  bindChartInteractions(mem, 'mem');

  const disk = getChart('ch-disk');
  disk.group = '';
  disk.setOption({
    ...baseOption({ yFmt: (v) => fmtBytes(v), valFmt: fmtRate, bytes: byteAxis(sys.diskR, sys.diskW), win }),
    graphic: noDataGraphic(empty),
    series: [series(t('s.read'), zip(sys.t, sys.diskR), C.dr), series(t('s.write'), zip(sys.t, sys.diskW), C.dw)],
  }, true);
  legend('#lg-disk', [[t('s.read'), C.dr], [t('s.write'), C.dw]]);
  bindChartInteractions(disk, 'disk');

  const net = getChart('ch-net');
  net.group = '';
  net.setOption({
    ...baseOption({ yFmt: (v) => fmtBytes(v), valFmt: fmtRate, bytes: byteAxis(sys.netRx, sys.netTx), win }),
    graphic: noDataGraphic(empty),
    series: [series(t('s.rx'), zip(sys.t, sys.netRx), C.rx), series(t('s.tx'), zip(sys.t, sys.netTx), C.tx)],
  }, true);
  legend('#lg-net', [[t('s.rx'), C.rx], [t('s.tx'), C.tx]]);
  bindChartInteractions(net, 'net');

  if (win) setWindow(win[0], win[1]);
}

/* ================= Process tables ================= */
function cacheProcs(res) {
  state.procRows = res.items;
  state.procIndex = new Map(res.items.map((p) => [p.nid, p]));
}

function renderTopTable(sel, items) {
  const host = $(sel);
  if (!items.length) { host.innerHTML = `<div class="empty">${t('noData')}</div>`; return; }
  const maxMem = Math.max(...items.map((p) => p.memAvg || 0), 1);
  const maxCpu = Math.max(...items.map((p) => p.cpuAvg || 0), 0.01);
  host.innerHTML = `<table><thead><tr>
      <th>${t('col.process')}</th><th class="r">${t('col.memAvg')}</th><th class="r">${t('col.memPeak')}</th>
      <th class="r">${t('col.cpuAvg')}</th><th class="r">${t('col.disk')}</th><th class="r">${t('col.net')}</th>
    </tr></thead><tbody>${items.map((p) => `<tr data-nid="${p.nid}">
      <td>${pname(p, isLeak(p) ? `<span class="badge bad">${t('ins.leaks').split(' ')[0]}</span>` : '')}</td>
      <td class="r">${barCell(fmtBytes(p.memAvg), p.memAvg / maxMem, cssVar('--c-mem'))}</td>
      <td class="r num">${fmtBytes(p.memMax)}</td>
      <td class="r">${barCell(fmtPct(p.cpuAvg), p.cpuAvg / maxCpu, cssVar('--c-cpu'))}</td>
      <td class="r num">${fmtBytes((p.ioR || 0) + (p.ioW || 0))}</td>
      <td class="r num">${p.netRx == null ? '—' : fmtBytes((p.netRx || 0) + (p.netTx || 0))}</td>
    </tr>`).join('')}</tbody></table>`;
  bindRows(host);
}
function bindRows(host) {
  $$('tbody tr[data-nid]', host).forEach((tr) => tr.addEventListener('click', () => openApp(+tr.dataset.nid)));
}

const PROC_COLS = [
  { key: 'name', label: 'col.process', sort: (p) => appName(p).toLowerCase(), render: (p) => pname(p, p.instances > 1 ? `<span class="cnt num">${p.instances}</span>` : '') },
  { key: 'memArea', label: 'col.memAvg', r: true, sort: (p) => p.memArea, render: (p, m) => barCell(fmtBytes(p.memAvg), p.memAvg / m.mem, cssVar('--c-mem')) },
  { key: 'memMax', label: 'col.memPeak', r: true, sort: (p) => p.memMax, render: (p) => `<span class="num">${fmtBytes(p.memMax)}</span>` },
  { key: 'memSlopeMBh', label: 'col.growth', r: true, sort: (p) => p.memSlopeMBh, render: (p) => growthCell(p) },
  { key: 'cpuAvg', label: 'col.cpuAvg', r: true, sort: (p) => p.cpuAvg, render: (p, m) => barCell(fmtPct(p.cpuAvg), p.cpuAvg / m.cpu, cssVar('--c-cpu')) },
  { key: 'cpuMax', label: 'col.cpuPeak', r: true, sort: (p) => p.cpuMax, render: (p) => `<span class="num ${p.cpuMax >= 80 ? 'heat-hi' : p.cpuMax >= 40 ? 'heat-md' : ''}">${fmtPct(p.cpuMax)}</span>` },
  { key: 'ioR', label: 'col.diskR', r: true, sort: (p) => p.ioR, render: (p) => `<span class="num">${fmtBytes(p.ioR)}</span>` },
  { key: 'ioW', label: 'col.diskW', r: true, sort: (p) => p.ioW, render: (p) => `<span class="num">${fmtBytes(p.ioW)}</span>` },
  { key: 'net', label: 'col.net', r: true, sort: (p) => (p.netRx || 0) + (p.netTx || 0), render: (p) => `<span class="num">${p.netRx == null ? '—' : fmtBytes((p.netRx || 0) + (p.netTx || 0))}</span>` },
  { key: 'gpuAvg', label: 'col.gpu', r: true, sort: (p) => p.gpuAvg, render: (p) => `<span class="num">${p.gpuAvg ? fmtPct(p.gpuAvg) : '—'}</span>` },
  { key: 'presence', label: 'col.presence', r: true, sort: (p) => p.presence, render: (p) => `<span class="num muted">${fmtPct(p.presence * 100)}</span>` },
];
function growthCell(p) {
  if (p.spanHours < 0.25 || Math.abs(p.memSlopeMBh) < 1) return '<span class="muted">—</span>';
  const v = `${p.memSlopeMBh > 0 ? '+' : ''}${fmtNum(p.memSlopeMBh, 0)} MB/${t('u.h')}`;
  if (isLeak(p)) return `<span class="badge bad num">${v}</span>`;
  if (p.memSlopeMBh >= 10 && p.memR2 >= 0.4) return `<span class="badge warn num">${v}</span>`;
  return `<span class="num muted">${v}</span>`;
}

function sortRows(rows, cols, sortState) {
  const col = cols.find((c) => c.key === sortState.key) || cols[1];
  return [...rows].sort((a, b) => {
    const va = col.sort(a) ?? -Infinity, vb = col.sort(b) ?? -Infinity;
    return (va > vb ? 1 : va < vb ? -1 : 0) * sortState.dir;
  });
}
function headerHtml(cols, sortState) {
  return `<thead><tr>${cols.map((c) => `<th class="${c.r ? 'r' : ''} ${c.key === sortState.key ? 'sorted' + (sortState.dir > 0 ? ' asc' : '') : ''}" data-key="${c.key}">${t(c.label)}</th>`).join('')}</tr></thead>`;
}
function bindSort(host, sortState, onSort) {
  $$('th[data-key]', host).forEach((th) => th.addEventListener('click', () => {
    const k = th.dataset.key;
    if (sortState.key === k) sortState.dir *= -1; else { sortState.key = k; sortState.dir = k === 'name' ? 1 : -1; }
    onSort();
  }));
}
function renderSortableTable(host, cols, rows, sortState, onSort) {
  rows = sortRows(rows, cols, sortState);
  const m = {
    mem: Math.max(1, ...rows.map((p) => p.memAvg ?? p.mem ?? 0)),
    cpu: Math.max(0.01, ...rows.map((p) => p.cpuAvg ?? p.cpu ?? 0)),
  };
  const scrollTop = host.scrollTop;
  host.innerHTML = rows.length ? `<table>${headerHtml(cols, sortState)}
    <tbody>${rows.slice(0, 400).map((p) => `<tr data-nid="${p.nid}">${cols.map((c) => `<td class="${c.r ? 'r' : ''}">${c.render(p, m)}</td>`).join('')}</tr>`).join('')}</tbody></table>`
    : `<div class="empty">${t('noData')}</div>`;
  host.scrollTop = scrollTop;
  bindSort(host, sortState, onSort);
  bindRows(host);
}

async function loadProcesses() {
  const r = currentRange();
  const res = await call('processes', { ...r, limit: 1000 });
  cacheProcs(res);
  renderProcTable();
}
function renderProcTable() {
  const q = $('#procSearch').value.trim().toLowerCase();
  const rows = (state.procRows || []).filter((p) => !q || p.name.toLowerCase().includes(q) || (p.descr || '').toLowerCase().includes(q));
  const map = { memMax: 'memMax', memSlopeMBh: 'growth', cpuAvg: 'cpuAvg', cpuMax: 'cpuMax', ioR: 'ioR', ioW: 'ioW', net: 'net', gpuAvg: 'gpuAvg', presence: 'presence' };
  const cols = PROC_COLS.filter((c) => !map[c.key] || shown('processes', map[c.key]));
  renderSortableTable($('#procTable'), cols, rows, state.procSort, renderProcTable);
}

/* ================= Moments ================= */
function momentsUnit(m) { return m === 'temp' ? '°C' : m === 'disk' || m === 'net' ? 'MB/s' : '%'; }
function momentsMinValue() {
  const raw = numVal($('#mMin').value);
  if (isNaN(raw) || raw <= 0) return null;
  const m = state.moments.metric;
  if (m === 'mem') return ((state.live?.memTotal || state.init.live?.memTotal || 0) * raw) / 100;
  if (m === 'disk' || m === 'net') return raw * 1048576;
  return raw;
}
function buildMomentsToolbar() {
  const mm = state.moments;
  $('#mMetric').innerHTML = MOMENT_METRICS.map((m) => `<button data-v="${m}" class="${mm.metric === m ? 'active' : ''}">${t(`metric.${m}`)}</button>`).join('');
  $('#mOrder').innerHTML = ['top', 'new', 'old'].map((o) => `<button data-v="${o}" class="${mm.order === o ? 'active' : ''}">${t(`m.${o}`)}</button>`).join('');
  $('#mMinUnit').textContent = momentsUnit(mm.metric);
  $$('#mMetric button').forEach((b) => b.onclick = () => { mm.metric = b.dataset.v; $('#mMin').value = ''; buildMomentsToolbar(); loadMoments(false); });
  $$('#mOrder button').forEach((b) => b.onclick = () => { mm.order = b.dataset.v; buildMomentsToolbar(); loadMoments(false); });
}
async function loadMoments(append) {
  const mm = state.moments;
  buildMomentsToolbar();
  const r = currentRange();
  const res = await call('moments', { metric: mm.metric, ...r, order: mm.order, offset: append ? mm.rows.length : 0, limit: 100, min: momentsMinValue() });
  mm.total = res.total;
  mm.rows = append ? mm.rows.concat(res.rows) : res.rows;
  renderMoments();
}
function renderMoments() {
  const mm = state.moments;
  const host = $('#mTable');
  $('#mCount').innerHTML = t('m.count', { n: N(fmtNum(mm.total, 0)) });
  if (!mm.rows.length) { host.innerHTML = `<div class="empty">${t('noData')}</div>`; return; }
  const m = mm.metric;
  const max = Math.max(...mm.rows.map((r) => r.value || 0), m === 'cpu' || m === 'gpu' ? 1 : 1024);
  const color = metricColor(m);
  const scroll = host.scrollTop;
  const temp = (r) => Math.max(r.cpuTemp ?? 0, r.gpuTemp ?? 0) || null;
  // optional columns (Settings → Customize): [key, header, cell]
  const cols = [
    ['fg', `<th>${t('col.fg')}</th>`, (r) => `<td>${fgCell(r.fg)}</td>`],
    ['causes', `<th>${t('col.causes')}</th>`, (r) => `<td><div class="causes">${(r.top || []).map((p) => causeLine(m, p)).join('') || '<span class="muted">—</span>'}</div></td>`],
    ['cpu', `<th class="r">${t('col.cpu')}</th>`, (r) => `<td class="r num">${fmtPct(r.cpu)}</td>`],
    ['mem', `<th class="r">${t('col.mem')}</th>`, (r) => `<td class="r num">${fmtBytes(r.mem, 1)}</td>`],
    ['disk', `<th class="r">${t('col.disk')}</th>`, (r) => `<td class="r num">${fmtRate((r.diskR ?? 0) + (r.diskW ?? 0))}</td>`],
    ['net', `<th class="r">${t('col.net')}</th>`, (r) => `<td class="r num">${fmtRate((r.netRx ?? 0) + (r.netTx ?? 0))}</td>`],
    ['temp', `<th class="r">${t('metric.temp')}</th>`, (r) => `<td class="r num ${tempClass(temp(r))}">${fmtTemp(temp(r))}</td>`],
  ].filter(([k]) => shown('moments', k));
  host.innerHTML = `<table><thead><tr>
      <th>${t('col.time')}</th><th class="r">${t(`metric.${m}`)}</th>${cols.map((c) => c[1]).join('')}
    </tr></thead><tbody>${mm.rows.map((r) => `<tr data-ts="${r.ts}">
      <td><div class="time-cell"><b class="num">${fmtClock(r.ts)}</b><span>${fmtDay(r.ts)}${r.dt > 30000 ? ` · ${t('kpi.avg')} ${fmtDur(r.dt)}` : ''}</span></div></td>
      <td class="r">${barCell(fmtMetric(m, r.value, r.memTotal), (r.value || 0) / max, color)}</td>
      ${cols.map((c) => c[2](r)).join('')}
    </tr>`).join('')}</tbody></table>
    ${mm.rows.length < mm.total ? `<div class="more-row"><button class="btn" id="mMore">${t('m.more')} (${N(fmtNum(mm.total - mm.rows.length, 0))})</button></div>` : ''}`;
  host.scrollTop = scroll;
  $$('tbody tr', host).forEach((tr) => tr.addEventListener('click', () => openMoment(+tr.dataset.ts, 1, m)));
  $('#mMore')?.addEventListener('click', () => loadMoments(true));
}

/* ================= Live ================= */
const LIVE_COLS_ALL = [
  { key: 'name', label: 'col.process', sort: (p) => appName(p).toLowerCase() },
  { key: 'cpu', label: 'col.cpu', r: true, sort: (p) => p.cpu },
  { key: 'mem', label: 'col.mem', r: true, sort: (p) => p.mem },
  { key: 'disk', label: 'col.disk', r: true, sort: (p) => p.ioR + p.ioW },
  { key: 'net', label: 'col.net', r: true, sort: (p) => (p.netRx || 0) + (p.netTx || 0) },
  { key: 'gpu', label: 'col.gpu', r: true, sort: (p) => p.gpu },
];
const LIVE_METRICS = ['cpu', 'mem', 'disk', 'net', 'gpu'];
const liveMetricKeys = () => LIVE_METRICS.filter((k) => shown('live', k));
const liveCols = () => LIVE_COLS_ALL.filter((c) => c.key === 'name' || shown('live', c.key));
const descrCache = new Map();
function liveCells(p, m) {
  const cell = {
    cpu: () => barCell(fmtPct(p.cpu), p.cpu / Math.max(m.cpu, 5), cssVar('--c-cpu')),
    mem: () => barCell(fmtBytes(p.mem), p.mem / m.mem, cssVar('--c-mem')),
    disk: () => `<span class="num">${p.ioR + p.ioW > 0 ? fmtRate(p.ioR + p.ioW) : '—'}</span>`,
    net: () => `<span class="num">${p.netRx == null ? '—' : (p.netRx + p.netTx > 0 ? fmtRate(p.netRx + p.netTx) : '—')}</span>`,
    gpu: () => `<span class="num">${p.gpu ? fmtPct(p.gpu) : '—'}</span>`,
  };
  return liveMetricKeys().map((k) => `<td class="r">${cell[k]()}</td>`).join('');
}
const PRIO = { 4: 'idle', 6: 'below', 8: 'normal', 10: 'above', 13: 'high', 24: 'realtime' };
function prioName(base) { const k = PRIO[base] ?? (base < 6 ? 'idle' : base < 8 ? 'below' : base < 10 ? 'normal' : base < 13 ? 'above' : base < 24 ? 'high' : 'realtime'); return k; }
const DETAIL_COLS = [
  { key: 'name', label: 'col.name', sort: (p) => p.name.toLowerCase() },
  { key: 'pid', label: 'col.pid', r: true, sort: (p) => p.pid },
  { key: 'role', label: 'col.role', sort: (p) => p.role || '' },
  { key: 'cpu', label: 'col.cpu', r: true, sort: (p) => p.cpu },
  { key: 'mem', label: 'col.mem', r: true, sort: (p) => p.mem },
  { key: 'disk', label: 'col.disk', r: true, sort: (p) => p.ioR + p.ioW },
  { key: 'net', label: 'col.net', r: true, sort: (p) => (p.netRx || 0) + (p.netTx || 0) },
  { key: 'gpu', label: 'col.gpu', r: true, sort: (p) => p.gpu },
  { key: 'threads', label: 'col.threads', r: true, sort: (p) => p.threads },
  { key: 'handles', label: 'col.handles', r: true, sort: (p) => p.handles },
  { key: 'priority', label: 'col.priority', r: true, sort: (p) => p.priority },
];
state.detailSort = { key: 'cpu', dir: -1 };
state.liveView = 'apps';
state.liveSel = null; // { kind: 'app'|'proc', nid, pid, name }

function liveRows() {
  const l = state.live;
  const q = $('#liveSearch').value.trim().toLowerCase();
  return l.processes
    .map((p) => {
      const known = state.procIndex.get(p.nid);
      return { ...p, descr: known?.descr ?? descrCache.get(p.nid) ?? null, hasIcon: known ? known.hasIcon : true, path: known?.path };
    })
    .filter((p) => !q || p.name.toLowerCase().includes(q) || (p.descr || '').toLowerCase().includes(q));
}
function isSel(kind, nid, pid) {
  const s = state.liveSel;
  return s && s.kind === kind && s.nid === nid && (kind === 'app' || s.pid === pid);
}
function renderLive() {
  const l = state.live;
  $('#liveNote').textContent = l && !l.perProcessNetwork && state.settings.trackNetwork ? t('live.netAdmin') : '';
  if (!shown('live', 'details')) state.liveView = 'apps';
  $('#liveView').classList.toggle('hidden', !shown('live', 'details'));
  $('#btnEndTask').classList.toggle('hidden', !shown('live', 'endTask'));
  $$('#liveView button').forEach((b) => b.classList.toggle('active', b.dataset.v === state.liveView));
  $('#btnEndTask').disabled = !state.liveSel;
  const host = $('#liveTable');
  if (!l) { host.innerHTML = `<div class="empty">${t('loading')}</div>`; return; }
  if (!state.procRows && !renderLive.fetching) {
    renderLive.fetching = true;
    call('processes', { from: state.boot, to: Date.now(), limit: 1000 }).then((r) => { cacheProcs(r); renderLive(); }).catch(() => {});
  }
  const scroll = host.scrollTop;
  if (state.liveView === 'details') renderDetails(host); else renderApps(host);
  host.scrollTop = scroll;
}

function renderApps(host) {
  const rows = sortRows(liveRows(), LIVE_COLS_ALL, state.liveSort);
  const m = { mem: Math.max(1, ...rows.map((p) => p.mem)), cpu: Math.max(0.01, ...rows.map((p) => p.cpu)) };
  const body = rows.slice(0, 300).map((p) => {
    const open = state.liveExpanded.has(p.nid);
    const many = (p.instances?.length || 0) > 0;
    let html = `<tr data-kind="app" data-nid="${p.nid}" class="${open ? 'expanded' : ''} ${isSel('app', p.nid) ? 'selected' : ''}"><td><div style="display:flex;align-items:center;gap:6px">${many ? '<span class="caret" data-caret="1">›</span>' : '<span class="caret"></span>'}${pname(p, p.count > 1 ? `<span class="cnt num">${p.count}</span>` : '')}<button class="mini-btn" data-open="${p.nid}">${t('live.details')}</button></div></td>${liveCells(p, m)}</tr>`;
    if (open && many) {
      html += p.instances.map((i) => `<tr class="sub-row ${isSel('proc', p.nid, i.pid) ? 'selected' : ''}" data-kind="proc" data-nid="${p.nid}" data-pid="${i.pid}"><td><div class="pname"><div class="nm"><b>${roleTag(i.role) || esc(roleName('app'))} <span class="muted num" style="font-weight:400">PID ${i.pid}</span></b><span>${esc(i.detail || i.title || '')}</span></div></div></td>${liveCells(i, m)}</tr>`).join('');
    }
    return html;
  }).join('');
  host.innerHTML = `<table>${headerHtml(liveCols(), state.liveSort)}<tbody>${body}</tbody></table>`;
  bindSort(host, state.liveSort, renderLive);
  bindLiveRows(host);
}

function renderDetails(host) {
  const flat = [];
  for (const p of liveRows()) for (const i of p.instances || []) flat.push({ ...i, nid: p.nid, name: p.name, descr: p.descr, hasIcon: p.hasIcon, path: p.path });
  const rows = sortRows(flat, DETAIL_COLS, state.detailSort);
  const dcols = DETAIL_COLS.filter((c) => !LIVE_METRICS.includes(c.key) || shown('live', c.key));
  const m = { mem: Math.max(1, ...rows.map((p) => p.mem)), cpu: Math.max(0.01, ...rows.map((p) => p.cpu)) };
  const body = rows.slice(0, 600).map((i) => `<tr data-kind="proc" data-nid="${i.nid}" data-pid="${i.pid}" class="${isSel('proc', i.nid, i.pid) ? 'selected' : ''}">
      <td><div class="icon-cell">${icon(i)}<div><b>${esc(i.name)}</b><span>${esc(i.descr || '')}</span></div></div></td>
      <td class="r num">${i.pid}</td>
      <td><div class="pname" style="min-width:160px"><div class="nm"><b>${roleTag(i.role) || esc(roleName('app'))}</b><span>${esc(i.detail || i.title || '')}</span></div></div></td>
      ${liveCells(i, m)}
      <td class="r num">${fmtNum(i.threads, 0)}</td><td class="r num">${fmtNum(i.handles, 0)}</td>
      <td class="r">${esc(t(`prio.${prioName(i.priority)}`))}</td>
    </tr>`).join('');
  host.innerHTML = `<table>${headerHtml(dcols, state.detailSort)}<tbody>${body}</tbody></table>`;
  bindSort(host, state.detailSort, renderLive);
  bindLiveRows(host);
}

function bindLiveRows(host) {
  $$('tbody tr[data-kind]', host).forEach((tr) => {
    const sel = () => ({ kind: tr.dataset.kind, nid: +tr.dataset.nid, pid: tr.dataset.pid ? +tr.dataset.pid : null, name: liveRows().find((p) => p.nid === +tr.dataset.nid)?.name });
    tr.addEventListener('click', (e) => {
      const nid = +tr.dataset.nid;
      if (e.target.closest('[data-open]')) { openApp(nid); return; }
      state.liveSel = sel();
      if (e.target.closest('[data-caret]') || (tr.dataset.kind === 'app' && e.detail === 2)) {
        if (state.liveExpanded.has(nid)) state.liveExpanded.delete(nid); else state.liveExpanded.add(nid);
      }
      renderLive();
    });
    tr.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      state.liveSel = sel();
      renderLive();
      showProcMenu(e.clientX, e.clientY, state.liveSel);
    });
  });
}

/* ---- process actions (Task Manager) ---- */
function liveApp(nid) { return state.live?.processes.find((p) => p.nid === nid); }
function liveInst(nid, pid) { return liveApp(nid)?.instances?.find((i) => i.pid === pid); }

function confirmDialog(title, body, okLabel, danger = true) {
  return new Promise((resolve) => {
    $('#mdTitle').textContent = title;
    $('#mdBody').textContent = body;
    $('#mdBtns').innerHTML = `<button class="btn" data-r="0">${esc(t('pa.cancel'))}</button><button class="btn ${danger ? 'danger-solid' : 'primary'}" data-r="1">${esc(okLabel)}</button>`;
    const m = $('#modal');
    m.classList.remove('hidden');
    const done = (v) => { m.classList.add('hidden'); resolve(v); };
    $$('#mdBtns button').forEach((b) => b.addEventListener('click', () => done(b.dataset.r === '1')));
    m.onclick = (e) => { if (e.target === m) done(false); };
    $('#mdBtns [data-r="1"]').focus();
  });
}
function actionResult(res, okMsg) {
  if (res?.ok) { toast(okMsg || t('pa.done')); setTimeout(() => call('live').then((l) => l && onLive(l)).catch(() => {}), 600); return true; }
  toast(t(`pa.err.${res?.error || 'failed'}`), 4500);
  return false;
}
async function endTask(sel, tree) {
  if (!sel) return;
  const app = liveApp(sel.nid);
  const label = sel.kind === 'app' ? appName(app || { name: sel.name }) : `${appName(app || { name: sel.name })} (PID ${sel.pid})`;
  const ok = await confirmDialog(t('pa.confirmTitle', { n: label }), tree || sel.kind === 'app' ? t('pa.confirmTree') : t('pa.confirmBody'), sel.kind === 'app' ? t('pa.endTask') : tree ? t('pa.endTree') : t('pa.endProcess'));
  if (!ok) return;
  const res = sel.kind === 'app' ? await call('killApp', { name: sel.name || app?.name }) : await call('kill', { pid: sel.pid, tree: !!tree });
  if (actionResult(res, t('pa.ended', { n: label }))) state.liveSel = null;
}

function showProcMenu(x, y, sel) {
  const app = liveApp(sel.nid);
  const inst = sel.kind === 'proc' ? liveInst(sel.nid, sel.pid) : null;
  const path = state.procIndex.get(sel.nid)?.path;
  const cur = inst ? prioName(inst.priority) : null;
  const items = [];
  if (sel.kind === 'app') items.push(['endApp', t('pa.endTask'), 'danger']);
  else { items.push(['end', t('pa.endProcess'), 'danger']); items.push(['tree', t('pa.endTree'), 'danger']); }
  items.push(['-']);
  items.push(['eff', inst && inst.priority <= 4 ? t('pa.effOff') : t('pa.efficiency')]);
  if (inst) {
    items.push(['label', t('pa.priority')]);
    for (const p of ['high', 'above', 'normal', 'below', 'idle']) items.push([`prio:${p}`, t(`prio.${p}`), `sub ${cur === p ? 'active' : ''}`]);
  }
  items.push(['-']);
  items.push(['loc', t('pa.openLoc'), path ? '' : 'disabled']);
  items.push(['search', t('pa.search')]);
  items.push(['details', t('pa.details')]);
  const ext = new Map();
  for (const f of PROC_MENU_EXT) {
    let more = [];
    try { more = f(sel, app, inst) || []; } catch (e) { console.error(e); }
    if (more.length) items.push(['-']);
    for (const m of more) { const k = `ext:${ext.size}`; ext.set(k, m); items.push([k, m.label, m.cls]); }
  }
  const menu = $('#ctx');
  menu.innerHTML = items.map(([k, label, cls]) => k === '-' ? '<hr>' : k === 'label'
    ? `<div class="muted" style="font-size:11.5px;padding:6px 10px 2px">${esc(label)}</div>`
    : `<button data-k="${k}" class="${cls || ''}" ${cls === 'disabled' ? 'disabled' : ''}>${esc(label)}</button>`).join('');
  menu.classList.remove('hidden');
  const r = menu.getBoundingClientRect();
  menu.style.left = `${Math.min(x, window.innerWidth - r.width - 8)}px`;
  menu.style.top = `${Math.min(y, window.innerHeight - r.height - 8)}px`;
  $$('button', menu).forEach((b) => b.addEventListener('click', async () => {
    menu.classList.add('hidden');
    const k = b.dataset.k;
    if (k === 'endApp') endTask(sel, true);
    else if (k === 'end') endTask(sel, false);
    else if (k === 'tree') endTask(sel, true);
    else if (k === 'eff') {
      const on = !(inst && inst.priority <= 4);
      const pids = inst ? [inst.pid] : (app?.instances || []).map((i) => i.pid);
      let last;
      for (const pid of pids) last = await call('efficiency', { pid, on });
      actionResult(last);
    } else if (k.startsWith('prio:')) actionResult(await call('priority', { pid: sel.pid, level: k.slice(5) }));
    else if (k === 'loc') call('openFileLocation', { path });
    else if (k === 'search') call('searchOnline', { q: `${app?.name || sel.name} ${state.procIndex.get(sel.nid)?.descr || ''}`.trim() });
    else if (k === 'details') openApp(sel.nid);
    else if (ext.has(k)) { try { await ext.get(k).run(); } catch (e) { toast(e.message, 4000); } }
  }));
}
document.addEventListener('click', (e) => { if (!e.target.closest('#ctx')) $('#ctx').classList.add('hidden'); });
document.addEventListener('scroll', () => $('#ctx').classList.add('hidden'), true);

function onLive(l) {
  state.live = l;
  state.liveHist.push(l);
  if (state.liveHist.length > 180) state.liveHist.shift();
  $('#recText').textContent = t('rec', { s: fmtInterval(state.settings?.intervalSeconds ?? 5) });
  renderSelfMeter();
  if (state.page === 'overview') updateKpisLive();
  if (state.page === 'live') renderLive();
  if (state.page === 'settings') { const el = $('#selfNow'); if (el) el.innerHTML = selfText(); }
  if (PAGES[state.page]?.onLive) try { PAGES[state.page].onLive(l); } catch (e) { console.error(e); }
}
function selfText() {
  const l = state.live;
  if (!l) return '—';
  return `${t('col.cpu')} <b class="num">${fmtNum(l.selfCpu, 2)}%</b> · RAM <b class="num">${fmtBytes(l.selfMem, 1)}</b>`;
}
function renderSelfMeter() { $('#selfMeter').innerHTML = `${t('self')}<br>${selfText()}`; }

/* ================= Insights ================= */
function rankList(items, valFn, fmt, color) {
  if (!items.length) return `<div class="muted">${t('noData')}</div>`;
  const max = Math.max(...items.map(valFn), 1e-9);
  return `<div class="rank">${items.map((p) => `<div class="rank-row" data-nid="${p.nid}">
      ${pname(p)}<span class="val num">${fmt(valFn(p))}</span>
      <span class="bar"><i style="width:${(valFn(p) / max) * 100}%;--bc:${color}"></i></span></div>`).join('')}</div>`;
}
function chipList(items, valFn = (p) => fmtBytes(p.mem, 1)) {
  return `<div class="chips">${items.map((p) => `<span class="chip" data-nid="${p.nid}">${icon(p)}${esc(appName(p))}${p.role && p.role !== 'app' ? ` · ${esc(roleName(p.role))}` : ''} <b class="num">${valFn(p)}</b></span>`).join('')}</div>`;
}
const insightHead = (iconSvg, color, title) => `<h3 class="card-head" style="justify-content:flex-start"><i style="--ic:${color}">${iconSvg}</i>${title}</h3>`;

async function loadInsights() {
  const r = currentRange();
  const [ins, procs, events, hot] = await Promise.all([
    call('insights', r), call('processes', { ...r, limit: 1000 }), call('events', r),
    call('moments', { metric: 'temp', ...r, order: 'top', offset: 0, limit: 1 }),
  ]);
  cacheProcs(procs);
  const memAlert = state.settings.alertMemoryPercent;
  const leaks = procs.items.filter(isLeak).sort((a, b) => b.memSlopeMBh - a.memSlopeMBh).slice(0, 8);
  const cards = [];
  const push = (k, html) => { if (shown('insights', k)) cards.push(html); };

  if (ins.peakMem) {
    const pct = ins.peakMem.memTotal ? (ins.peakMem.value * 100) / ins.peakMem.memTotal : null;
    push('peakMem', `<div class="card insight">${insightHead(ICONS.mem, 'var(--c-mem)', t('ins.peakMem'))}
      <div class="big num">${fmtBytes(ins.peakMem.value, 2)} <span class="muted" style="font-size:14px">(${fmtPct(pct)})</span></div>
      <div class="when"><button class="link num" data-moment="${ins.peakMem.ts}" data-metric="mem">${fmtFull(ins.peakMem.ts, true)}</button> — ${t('ins.who')}</div>
      ${rankList(ins.peakMem.top, (p) => p.mem || 0, (v) => fmtBytes(v), cssVar('--c-mem'))}</div>`);
  }
  if (ins.peakCpu) {
    push('peakCpu', `<div class="card insight">${insightHead(ICONS.cpu, 'var(--c-cpu)', t('ins.peakCpu'))}
      <div class="big num">${fmtPct(ins.peakCpu.value)}</div>
      <div class="when"><button class="link num" data-moment="${ins.peakCpu.ts}" data-metric="cpu">${fmtFull(ins.peakCpu.ts, true)}</button> — ${t('ins.who')}</div>
      ${rankList(ins.peakCpu.top, (p) => p.cpu || 0, fmtPct, cssVar('--c-cpu'))}</div>`);
  }
  const h = hot.rows[0];
  if (h && h.value > 0) {
    push('peakTemp', `<div class="card insight">${insightHead(ICONS.bolt, 'var(--c-disk-w)', t('ins.peakTemp'))}
      <div class="big num ${tempClass(h.value)}">${fmtTemp(h.value)} <span class="muted" style="font-size:13px">${t('metric.cpu')} ${fmtTemp(h.cpuTemp)} · GPU ${fmtTemp(h.gpuTemp)}</span></div>
      <div class="when"><button class="link num" data-moment="${h.ts}" data-metric="temp">${fmtFull(h.ts, true)}</button>${h.fg ? ` · ${esc(appName(h.fg))}` : ''} — ${t('ins.tempWho')}</div>
      ${rankList(h.top, (p) => (p.cpu || 0) + (p.gpu || 0), fmtPct, cssVar('--c-disk-w'))}</div>`);
  }
  push('leaks', `<div class="card insight">${insightHead(ICONS.trend, 'var(--bad)', t('ins.leaks'))}
    <div class="when">${t('ins.leaksDesc')}</div>
    ${leaks.length ? leaks.map((p) => `<div class="leak-row" data-nid="${p.nid}">${pname(p)}
      <div style="text-align:end"><span class="badge bad num">${t('ins.growth', { v: fmtNum(p.memSlopeMBh, 0) })}</span>
      <div class="muted num" style="font-size:11.5px">${fmtBytes(p.memAvg)} → ${fmtBytes(p.memMax)} · ${t('ins.fit', { v: fmtNum(p.memR2 * 100, 0) })}</div></div></div>`).join('')
      : `<div class="muted">${t('ins.noLeaks')}</div>`}</div>`);

  push('episodes', `<div class="card insight">${insightHead(ICONS.bolt, 'var(--warn)', t('ins.episodes', { p: memAlert }))}
    ${ins.episodes.length ? ins.episodes.map((e) => `<div class="episode">
      <div class="episode-head"><span class="num">${fmtTime(e.start, { date: true })} – ${fmtTime(e.end)}</span>
      <span class="muted">${t('ins.duration')} <b class="tnum">${fmtDur(e.end - e.start)}</b> · ${t('ins.peak')} <b class="num" style="color:var(--bad)">${fmtPct(e.peak)}</b></span></div>
      ${chipList(e.top)}</div>`).join('') : `<div class="muted">${t('ins.noEpisodes', { p: memAlert })}</div>`}</div>`);

  push('alerts', `<div class="card insight">${insightHead(ICONS.bell, 'var(--warn)', t('ins.alerts'))}
    ${events.length ? events.slice(0, 12).map((e) => `<div class="episode">
      <div class="episode-head"><button class="link num" data-moment="${e.ts}" data-metric="${e.kind === 'mem' ? 'mem' : e.kind === 'temp' ? 'temp' : 'cpu'}">${fmtFull(e.ts, true)}</button>
      <span><span class="badge ${e.kind === 'mem' ? 'info' : 'warn'}">${t(e.kind === 'mem' ? 'ins.alertMem' : e.kind === 'temp' ? 'ins.alertTemp' : 'ins.alertCpu')}</span> <b class="num" style="color:var(--bad)">${e.kind === 'temp' ? fmtTemp(e.value) : fmtPct(e.value)}</b></span></div>
      ${chipList((e.top || []).map((p) => ({ ...p, name: p.app, descr: state.procIndex.get(p.nid)?.descr })), (p) => e.kind === 'mem' ? fmtBytes(p.mem, 1) : fmtPct(p.cpu))}</div>`).join('')
      : `<div class="muted">${t('ins.noAlerts')}</div>`}</div>`);

  push('topCpu', `<div class="card insight">${insightHead(ICONS.cpu, 'var(--c-cpu)', t('ins.topCpu'))}
    ${rankList(ins.topCpu, (p) => p.cpu || 0, fmtPct, cssVar('--c-cpu'))}</div>`);
  push('topDisk', `<div class="card insight">${insightHead(ICONS.disk, 'var(--c-disk-r)', t('ins.topDisk'))}
    ${rankList(ins.topDisk, (p) => p.io || 0, (v) => fmtBytes(v), cssVar('--c-disk-r'))}</div>`);
  const netHasData = ins.topNet.some((p) => p.net > 0);
  push('topNet', `<div class="card insight">${insightHead(ICONS.net, 'var(--c-net-rx)', t('ins.topNet'))}
    ${netHasData ? rankList(ins.topNet.filter((p) => p.net > 0), (p) => p.net || 0, (v) => fmtBytes(v), cssVar('--c-net-rx'))
      : `<div class="muted">${state.init.isAdmin ? t('noData') : t('ins.netNeedAdmin')}</div>`}</div>`);

  const host = $('#insights');
  host.innerHTML = cards.join('');
  $$('[data-nid]', host).forEach((el) => el.addEventListener('click', () => openApp(+el.dataset.nid)));
  $$('[data-moment]', host).forEach((el) => el.addEventListener('click', (ev) => { ev.stopPropagation(); openMoment(+el.dataset.moment, 1, el.dataset.metric); }));
}

/* ================= Drawer ================= */
function openDrawerShell() {
  $('#scrim').classList.remove('hidden');
  $('#drawer').classList.add('open');
  $('#drawer').setAttribute('aria-hidden', 'false');
}
function closeDrawer() {
  state.drawer = null;
  $('#scrim').classList.add('hidden');
  $('#drawer').classList.remove('open');
  $('#drawer').setAttribute('aria-hidden', 'true');
  disposeCharts('dr-');
}
function setDrawer(title, actions, body) {
  disposeCharts('dr-');
  $('#drTitle').innerHTML = title;
  $('#drActions').innerHTML = actions || '';
  $('#drBody').innerHTML = body;
  $('#drBody').scrollTop = 0;
}
const mini = (label, val) => `<div class="mini"><span>${label}</span><b class="num">${val}</b></div>`;
const chartCard = (id, title, hidden) => `<div class="card chart-card${hidden ? ' hidden' : ''}" id="${id}-card"><div class="card-head"><h3>${title}</h3></div><div class="chart sm" id="${id}"></div></div>`;
const skeleton = '<div class="skeleton"></div>';

function drawSeriesCharts(prefix, ser) {
  const C = { mem: cssVar('--c-mem'), cpu: cssVar('--c-cpu'), gpu: cssVar('--c-gpu'), dr: cssVar('--c-disk-r'), dw: cssVar('--c-disk-w'), rx: cssVar('--c-net-rx'), tx: cssVar('--c-net-tx') };
  const empty = ser.t.length === 0;
  const bucket = () => ser.bucket;
  const memC = getChart(`${prefix}-mem`);
  memC.setOption({
    ...baseOption({ yFmt: (v) => fmtBytes(v), valFmt: (v) => fmtBytes(v, 1), bytes: byteAxis(ser.mem, ser.memMax) }),
    graphic: noDataGraphic(empty),
    series: [series(t('s.memUsed'), zip(ser.t, ser.mem), C.mem), series(t('s.memPeak'), zip(ser.t, ser.memMax), C.mem, { area: false, dashed: true, width: 1, opacity: 0.6 })],
  }, true);
  bindChartInteractions(memC, 'mem', { noDrag: true, bucket });
  const cpuC = getChart(`${prefix}-cpu`);
  cpuC.setOption({
    ...baseOption({ yFmt: (v) => `${v}%`, valFmt: fmtPct }),
    graphic: noDataGraphic(empty),
    series: [series(t('s.cpu'), zip(ser.t, ser.cpu), C.cpu), series(t('s.gpu'), zip(ser.t, ser.gpu), C.gpu, { area: false })],
  }, true);
  bindChartInteractions(cpuC, 'cpu', { noDrag: true, bucket });
  const diskC = getChart(`${prefix}-disk`);
  diskC.setOption({
    ...baseOption({ yFmt: (v) => fmtBytes(v), valFmt: fmtRate, bytes: byteAxis(ser.ioR, ser.ioW) }),
    graphic: noDataGraphic(empty),
    series: [series(t('s.read'), zip(ser.t, ser.ioR), C.dr), series(t('s.write'), zip(ser.t, ser.ioW), C.dw)],
  }, true);
  bindChartInteractions(diskC, 'disk', { noDrag: true, bucket });
  const hasNet = ser.netRx.some((v) => v != null);
  $(`#${prefix}-net-card`).classList.toggle('hidden', !hasNet);
  if (hasNet) {
    const netC = getChart(`${prefix}-net`);
    netC.setOption({
      ...baseOption({ yFmt: (v) => fmtBytes(v), valFmt: fmtRate, bytes: byteAxis(ser.netRx, ser.netTx) }),
      series: [series(t('s.rx'), zip(ser.t, ser.netRx), C.rx), series(t('s.tx'), zip(ser.t, ser.netTx), C.tx)],
    }, true);
    bindChartInteractions(netC, 'net', { noDrag: true, bucket });
  }
}

/* ---- application ---- */
async function openApp(nid) {
  state.drawer = { kind: 'app', nid };
  openDrawerShell();
  setDrawer('', '', skeleton);
  const r = currentRange();
  const needRow = !state.procIndex.has(nid);
  const [ser, inst, stack, procs] = await Promise.all([
    call('process', { nid, ...r, points: 500 }),
    call('appInstances', { nid, ...r }),
    call('appStack', { nid, ...r, points: 300, metric: 'mem' }),
    needRow ? call('processes', { ...r, limit: 1000 }) : Promise.resolve(null),
  ]);
  if (procs) cacheProcs(procs);
  if (state.drawer?.kind !== 'app' || state.drawer.nid !== nid) return;
  const p = state.procIndex.get(nid) || { nid, name: ser.name, descr: ser.descr, path: ser.path };
  descrCache.set(nid, ser.descr);
  state.drawer.name = appName({ ...p, descr: ser.descr || p.descr });

  const title = `${icon({ ...p, nid })}<div style="min-width:0"><h2>${esc(appName({ ...p, descr: ser.descr || p.descr }))}</h2>
    <div class="sub">${esc(ser.name)}${ser.path ? ` · <button class="link" id="drLoc">${t('dr.openLoc')}</button>` : ''}</div></div>`;

  const kpis = p.memAvg != null ? [
    mini(t('dr.memAvg'), fmtBytes(p.memAvg)), mini(t('dr.memPeak'), fmtBytes(p.memMax)), mini(t('dr.growth'), growthCell(p)),
    mini(t('dr.cpuAvg'), fmtPct(p.cpuAvg)), mini(t('dr.cpuPeak'), fmtPct(p.cpuMax)),
    mini(t('dr.disk'), `${fmtBytes(p.ioR)} / ${fmtBytes(p.ioW)}`),
    p.netRx != null ? mini(t('dr.net'), `${fmtBytes(p.netRx)} / ${fmtBytes(p.netTx)}`) : '',
    mini(t('dr.instances'), fmtNum(p.instances, 0)),
  ].join('') : `<div class="muted">${t('noData')}</div>`;

  const items = inst.items;
  const maxMem = Math.max(1, ...items.map((i) => i.memAvg || 0));
  const instTable = items.length ? `<div class="card flush"><div class="table-wrap"><table><thead><tr>
      <th>${t('col.process')}</th><th class="r">${t('col.memAvg')}</th><th class="r">${t('col.memPeak')}</th>
      <th class="r">${t('col.cpuAvg')}</th><th class="r">${t('col.cpuPeak')}</th><th class="r">${t('col.disk')}</th><th class="r">${t('col.presence')}</th>
    </tr></thead><tbody>${items.map((i) => `<tr data-iid="${i.iid}">
      <td><div class="pname"><div class="nm"><b>${roleTag(i.role) || esc(roleName('app'))} <span class="muted num" style="font-weight:400">PID ${i.pid}</span></b><span>${esc(i.detail || i.title || '')}</span></div></div></td>
      <td class="r">${barCell(fmtBytes(i.memAvg), i.memAvg / maxMem, cssVar('--c-mem'))}</td>
      <td class="r num">${fmtBytes(i.memMax)}</td>
      <td class="r num">${fmtPct(i.cpuAvg)}</td>
      <td class="r num">${fmtPct(i.cpuMax)}</td>
      <td class="r num">${fmtBytes((i.ioR || 0) + (i.ioW || 0))}</td>
      <td class="r num muted">${fmtPct(i.presence * 100)}</td>
    </tr>`).join('')}</tbody></table></div></div>` : `<div class="muted">${t('dr.noInst')}</div>`;

  setDrawer(title, '', `
    <div class="mini-kpis">${kpis}</div>
    ${chartCard('dr-mem', t('chart.mem'))}
    ${stack.series.length > 1 ? chartCard('dr-stack', t('dr.stack')) : ''}
    <div class="dr-section-title"><h3>${t('dr.inside')}</h3><span>${t('dr.insideHint')}</span></div>
    ${instTable}
    ${chartCard('dr-cpu', t('chart.cpuOnly'))}
    ${chartCard('dr-disk', t('chart.disk'))}
    ${chartCard('dr-net', t('chart.net'), true)}`);
  $('#drLoc')?.addEventListener('click', () => call('openFileLocation', { path: ser.path }));
  $$('#drBody tr[data-iid]').forEach((tr) => tr.addEventListener('click', () => openInstance(+tr.dataset.iid)));
  drawSeriesCharts('dr', ser);

  if (stack.series.length > 1) {
    const palette = ['--c-mem', '--c-cpu', '--c-gpu', '--c-disk-r', '--c-net-rx', '--c-commit', '--c-net-tx'].map(cssVar);
    const sc = getChart('dr-stack');
    const totals = stack.t.map((_, i) => stack.series.reduce((a, s) => a + (s.data[i] || 0), 0));
    sc.setOption({
      ...baseOption({ yFmt: (v) => fmtBytes(v), valFmt: (v) => fmtBytes(v, 1), legend: true, bytes: byteAxis(totals) }),
      grid: { left: 10, right: 14, top: 14, bottom: 30, containLabel: true },
      series: stack.series.map((s, i) => series(`${roleName(s.role) || roleName('app')} · ${s.pid}`, zip(stack.t, s.data), palette[i % palette.length], { stack: 'm', width: 1 })),
    }, true);
    bindChartInteractions(sc, 'mem', { noDrag: true, bucket: () => stack.bucket });
  }
}

/* ---- one process (PID) ---- */
async function openInstance(iid) {
  const back = state.drawer?.kind === 'app' ? { nid: state.drawer.nid, name: state.drawer.name } : null;
  state.drawer = { kind: 'inst', iid, back };
  setDrawer('', '', skeleton);
  const ser = await call('instance', { iid, ...currentRange(), points: 500 });
  if (state.drawer?.kind !== 'inst' || state.drawer.iid !== iid) return;
  const info = ser.info || {};
  const head = `${icon({ nid: info.nid, name: info.name })}<div style="min-width:0"><h2>${roleTag(info.role)} ${esc(appName(info))}</h2>
    <div class="sub num">PID ${info.pid ?? '—'}${info.created ? ` · ${t('col.started')} ${fmtTime(info.created, { date: true, seconds: true })}` : ''}</div></div>`;
  const actions = back ? `<button class="btn back-btn" id="drBack">${ICONS.next}${esc(t('dr.back', { a: back.name || '' }))}</button>` : '';
  setDrawer(head, actions, `
    ${info.detail ? `<div class="note">${esc(info.detail)}</div>` : ''}
    ${info.title ? `<div class="fg-box"><div><span>${t('dr.lastTitle')}</span><b>${esc(info.title)}</b></div></div>` : ''}
    ${info.cmd ? `<div><div class="muted" style="font-size:12px;margin-bottom:4px">${t('dr.cmd')}</div><div class="cmdline">${esc(info.cmd)}</div></div>` : ''}
    ${chartCard('dr-mem', t('chart.mem'))}
    ${chartCard('dr-cpu', t('chart.cpuOnly'))}
    ${chartCard('dr-disk', t('chart.disk'))}
    ${chartCard('dr-net', t('chart.net'), true)}`);
  $('#drBack')?.addEventListener('click', () => openApp(back.nid));
  drawSeriesCharts('dr', ser);
}

/* ---- one moment ---- */
async function openMoment(t0, span, metric) {
  state.drawer = { kind: 'moment', metric: metric || 'cpu' };
  openDrawerShell();
  setDrawer('', '', skeleton);
  const m = await call('moment', { t: t0, span: Math.max(1, Math.round(span)), metric: state.drawer.metric });
  if (state.drawer?.kind !== 'moment') return;
  if (!m) { setDrawer('', '', `<div class="empty">${t('noData')}</div>`); return; }
  state.drawer.data = m;
  renderMoment();
}
function renderMoment() {
  const d = state.drawer;
  const m = d.data;
  const s = m.sys;
  const metric = d.metric;
  const head = `<div style="min-width:0"><h2 class="num">${esc(t('mo.title', { t: fmtClock(s.ts) }))}</h2><div class="sub">${fmtDay(s.ts)}${s.dt > 30000 ? ` · ${t('kpi.avg')} ${fmtDur(s.dt)}` : ''}</div></div>`;
  const actions = `<button class="icon-btn" id="moPrev" title="${t('mo.prev')}" ${m.prev ? '' : 'disabled'}>${lang() === 'ar' ? ICONS.next : ICONS.prev}</button>
    <button class="icon-btn" id="moNext" title="${t('mo.next')}" ${m.next ? '' : 'disabled'}>${lang() === 'ar' ? ICONS.prev : ICONS.next}</button>`;
  const memPct = s.memTotal ? (s.mem * 100) / s.memTotal : null;
  const sys = `<div class="moment-sys">
    ${mini(t('metric.cpu'), fmtPct(s.cpu))}
    ${mini(t('metric.mem'), `${fmtBytes(s.mem, 1)} · ${fmtPct(memPct)}`)}
    ${mini(t('metric.disk'), `${fmtRate(s.diskR)} / ${fmtRate(s.diskW)}`)}
    ${mini(t('metric.net'), `↓ ${fmtRate(s.netRx)} · ↑ ${fmtRate(s.netTx)}`)}
    ${mini(t('metric.gpu'), fmtPct(s.gpu))}
    ${s.cpuTemp != null || s.gpuTemp != null ? mini(`${t('metric.temp')} (CPU / GPU)`, `<span class="${tempClass(s.cpuTemp)}">${fmtTemp(s.cpuTemp)}</span> / <span class="${tempClass(s.gpuTemp)}">${fmtTemp(s.gpuTemp)}</span>`) : ''}
    ${s.cpuMhz != null ? mini(t('chart.clock'), `${fmtMhz(s.cpuMhz)}${s.pkgPower != null ? ` · ${fmtWatt(s.pkgPower)}` : ''}`) : ''}
  </div>`;
  const fg = s.fg ? `<div class="fg-box">${icon(s.fg)}<div><span>${t('mo.fg')}</span><b>${esc(appName(s.fg))}</b><span>${esc(s.fg.title || roleName(s.fg.role))}</span></div></div>` : '';
  const sortSeg = `<div class="tool-group"><span class="muted">${t('mo.sortBy')}</span><div class="seg" id="moSort">${MOMENT_METRICS.map((x) => `<button data-v="${x}" class="${x === metric ? 'active' : ''}">${t(`metric.${x}`)}</button>`).join('')}</div></div>`;

  const hasInst = m.processes.length > 0;
  const rows = (hasInst ? m.processes : m.apps).map((p) => ({ ...p, io: (p.ioR || 0) + (p.ioW || 0), net: p.netRx == null ? null : (p.netRx || 0) + (p.netTx || 0) }));
  rows.sort((a, b) => (procVal(metric, b) || 0) - (procVal(metric, a) || 0));
  const max = Math.max(...rows.map((p) => procVal(metric, p) || 0), 1e-9);
  const col = metricColor(metric);
  const cell = (x, v) => (x === metric ? barCell(fmtProcVal(x, v), (v || 0) / max, col) : `<span class="num">${v == null ? '—' : fmtProcVal(x, v)}</span>`);
  const table = `<div class="card flush"><div class="table-wrap"><table><thead><tr>
      <th>${t('col.process')}</th><th class="r">${t('col.cpu')}</th><th class="r">${t('col.mem')}</th><th class="r">${t('col.disk')}</th><th class="r">${t('col.net')}</th><th class="r">${t('col.gpu')}</th>
    </tr></thead><tbody>${rows.map((p) => `<tr data-nid="${p.nid}" ${p.iid ? `data-iid="${p.iid}"` : ''}>
      <td>${pname(p, p.pid ? `<span class="muted num" style="font-weight:400;font-size:11px">PID ${p.pid}</span>` : '')}</td>
      <td class="r">${cell('cpu', p.cpu)}</td><td class="r">${cell('mem', p.mem)}</td><td class="r">${cell('disk', p.io)}</td>
      <td class="r">${cell('net', p.net)}</td><td class="r">${cell('gpu', p.gpu)}</td>
    </tr>`).join('')}</tbody></table></div></div>`;

  setDrawer(head, actions, `${sys}${fg}
    <div class="dr-section-title"><h3>${t('mo.procs')}</h3>${sortSeg}</div>
    ${hasInst ? '' : `<div class="note">${t('mo.noInst')}</div>`}
    ${table}`);
  $('#moPrev')?.addEventListener('click', () => openMoment(m.prev, 1, metric));
  $('#moNext')?.addEventListener('click', () => openMoment(m.next, 1, metric));
  $$('#moSort button').forEach((b) => b.addEventListener('click', () => { d.metric = b.dataset.v; renderMoment(); }));
  $$('#drBody tr[data-nid]').forEach((tr) => tr.addEventListener('click', () => {
    if (tr.dataset.iid) openInstance(+tr.dataset.iid); else openApp(+tr.dataset.nid);
  }));
}

/* ================= Performance (Task Manager style, 1 s) ================= */
state.perfDev = 'cpu';
state.perfData = null;
let perfTimer = null;
let perfBuiltFor = null;

function startPerf() {
  stopPerf();
  perfBuiltFor = null;
  const tick = async () => {
    if (document.hidden || state.page !== 'perf') return;
    try { state.perfData = await call('perf'); renderPerf(); } catch (e) { console.error(e); }
  };
  tick();
  perfTimer = setInterval(tick, 1000);
}
function stopPerf() { if (perfTimer) { clearInterval(perfTimer); perfTimer = null; } }

function perfDevices(p) {
  const s = p.sensors || {};
  const list = [
    { id: 'cpu', color: '--c-cpu', title: t('pf.cpu'), sub: isoParts(fmtPct(p.cpu.util), fmtMhz(s.cpuMhz), s.cpuTemp != null ? fmtTemp(s.cpuTemp) : null), hist: ['cpu'] },
    { id: 'mem', color: '--c-mem', title: t('pf.mem'), sub: isoParts(`${fmtBytes(p.mem.used, 1)} / ${fmtBytes(p.mem.total, 1)} (${fmtPct((p.mem.used * 100) / p.mem.total)})`), hist: ['mem'] },
  ];
  for (const d of p.disks) {
    list.push({ id: d.key, color: '--c-disk-r', title: t('pf.disk', { n: ISO(`${d.number}${d.volumes.length ? ` (${d.volumes.join(' ')})` : ''}`) }), sub: isoParts(d.ssd ? `SSD${d.bus ? ` (${d.bus})` : ''}` : d.bus, fmtPct(d.active)), hist: [d.key] });
  }
  for (const n of p.nets) {
    list.push({ id: n.key, color: '--c-net-rx', title: n.type, sub: isoParts(n.name.replace(/^‏+/, ''), `↓ ${fmtRate(n.rx)} ↑ ${fmtRate(n.tx)}`), hist: [`${n.key}r`, `${n.key}t`] });
  }
  for (const g of p.gpu.adapters) {
    list.push({ id: g.key, color: '--c-gpu', title: `${t('pf.gpu')} ${g.key.slice(3)}`, sub: isoParts(g.name, fmtPct(g.util), g.temp != null ? fmtTemp(g.temp) : null), hist: [g.key] });
  }
  list.push({ id: 'sensors', color: '--c-disk-w', title: t('pf.sensors'), sub: isoParts(fmtTemp(s.cpuTemp), fmtTemp(s.gpuTemp), s.packagePower != null ? fmtWatt(s.packagePower) : null), hist: ['cpuTemp', 'gpuTemp'] });
  return list;
}

function histSeries(p, key) { return zip(p.times, p.history[key] || []); }

function perfChart(id, p, keys, colors, opts = {}) {
  const c = getChart(id);
  const th = chartTheme();
  c.setOption({
    animation: false,
    grid: { left: opts.mini ? 0 : 6, right: opts.mini ? 0 : 6, top: opts.mini ? 2 : 8, bottom: opts.mini ? 0 : 6, containLabel: !opts.mini },
    tooltip: opts.mini ? { show: false } : {
      trigger: 'axis', backgroundColor: th.surface, borderColor: th.border, textStyle: { color: th.text, fontFamily: th.font, fontSize: 12 },
      formatter: (ps) => `<div style="color:${th.muted}">${fmtTime(ps[0].value[0], { seconds: true })}</div>` + ps.map((x) => `<div><span style="color:${x.color}">●</span> ${esc(x.seriesName)} <b style="direction:ltr">${(opts.fmt || fmtPct)(x.value[1])}</b></div>`).join(''),
    },
    xAxis: { type: 'time', show: false, min: p.times[0], max: p.times[p.times.length - 1] },
    yAxis: {
      type: 'value', min: 0, max: opts.max ?? undefined, show: !opts.mini, splitNumber: 4,
      splitLine: { lineStyle: { color: th.grid } }, axisLabel: { color: th.muted, fontSize: 10, formatter: (v) => LRM((opts.fmt || ((x) => `${x}%`))(v)) },
    },
    series: keys.map((k, i) => ({ ...series(opts.names?.[i] || k, histSeries(p, k), colors[i], { width: opts.mini ? 1.2 : 1.6, areaA: i === 0 ? 0.3 : 0.12, area: opts.noArea ? false : undefined }), sampling: undefined })),
  }, true);
}

function stat(label, value, cls = '') { return `<div class="stat"><span>${esc(label)}</span><b class="num ${cls}">${value}</b></div>`; }
function kv(pairs) { return `<div class="kv">${pairs.filter((x) => x[1] != null && x[1] !== '').map(([k, v]) => `<span>${esc(k)}</span><b>${v}</b>`).join('')}</div>`; }

function renderPerf() {
  const p = state.perfData;
  if (!p) return;
  const devs = perfDevices(p).filter((d) => shown('perf', d.id.startsWith('disk') ? 'disk' : d.id.startsWith('net') ? 'net' : d.id.startsWith('gpu') ? 'gpu' : d.id));
  if (!devs.length) { $('#perfList').innerHTML = ''; $('#perfList').dataset.sig = ''; $('#perfMain').innerHTML = `<div class="empty">${t('pf.allHidden')}</div>`; perfBuiltFor = null; return; }
  if (!devs.find((d) => d.id === state.perfDev)) state.perfDev = 'cpu';

  // device list (rebuilt only when the set of devices changes)
  const sig = devs.map((d) => d.id).join('|') + lang();
  if ($('#perfList').dataset.sig !== sig) {
    disposeCharts('pfm-');
    $('#perfList').dataset.sig = sig;
    $('#perfList').innerHTML = devs.map((d) => `<button class="dev" data-dev="${d.id}" style="--dc:var(${d.color})"><div class="mini-chart" id="pfm-${d.id}"></div><div><b>${esc(d.title)}</b><span class="v" id="pfs-${d.id}"></span></div></button>`).join('');
    $$('#perfList .dev').forEach((b) => b.addEventListener('click', () => { state.perfDev = b.dataset.dev; perfBuiltFor = null; renderPerf(); }));
  }
  for (const d of devs) {
    $(`#pfs-${d.id}`).textContent = d.sub;
    $(`.dev[data-dev="${d.id}"]`).classList.toggle('active', d.id === state.perfDev);
    const max = ['cpu', 'mem'].includes(d.id) || d.id.startsWith('disk') || d.id.startsWith('gpu') ? 100 : undefined;
    perfChart(`pfm-${d.id}`, p, d.hist, d.hist.map(() => cssVar(d.color)), { mini: true, max: d.id === 'sensors' ? undefined : max });
  }

  const dev = devs.find((d) => d.id === state.perfDev);
  const main = $('#perfMain');
  const s = p.sensors || {};
  const C = (v) => cssVar(v);
  if (perfBuiltFor !== state.perfDev + lang()) {
    disposeCharts('pfc-');
    perfBuiltFor = state.perfDev + lang();
    let charts = '';
    if (dev.id === 'disk' || dev.id.startsWith('disk')) charts = `<div class="perf-sub"><span>${t('pf.active')}</span><span></span></div><div class="perf-chart" id="pfc-a"></div><div class="perf-sub" style="margin-top:10px"><span>${t('pf.transfer')}</span><span>${t('pf.last60')}</span></div><div class="perf-chart sm" id="pfc-b"></div>`;
    else if (dev.id.startsWith('gpu')) charts = `<div class="perf-sub"><span>${t('pf.util')}</span><span></span></div><div class="perf-chart" id="pfc-a"></div><div class="perf-sub" style="margin-top:10px"><span>${t('pf.engines')}</span><span>${t('pf.last60')}</span></div><div class="perf-charts2" id="pfc-eng"></div>`;
    else if (dev.id === 'sensors') charts = `<div class="perf-sub"><span>${t('chart.temp')}</span><span>°C</span></div><div class="perf-chart" id="pfc-a"></div><div class="perf-sub" style="margin-top:10px"><span>${t('chart.clock')}</span><span>${t('pf.last60')}</span></div><div class="perf-chart sm" id="pfc-b"></div>`;
    else if (dev.id.startsWith('net')) charts = `<div class="perf-sub"><span>${t('pf.throughput')}</span><span>${t('pf.last60')}</span></div><div class="perf-chart" id="pfc-a"></div>`;
    else charts = `<div class="perf-sub"><span>${dev.id === 'mem' ? t('pf.inUse') : t('pf.util')}</span><span></span></div><div class="perf-chart" id="pfc-a"></div>`;
    main.innerHTML = `<div class="card"><div class="perf-head"><h2 id="pfTitle"></h2><span class="model" id="pfModel"></span></div>${charts}<div id="pfExtra"></div><div id="pfStats"></div></div>`;
  }

  let model = '', extra = '', stats = '';
  if (dev.id === 'cpu') {
    const i = p.cpu.info;
    model = i.name || '';
    perfChart('pfc-a', p, ['cpu'], [C('--c-cpu')], { max: 100, names: [t('pf.util')] });
    extra = !shown('perf', 'cores') ? '' : `<div class="perf-sub" style="margin-top:12px"><span>${t('pf.coresTitle')}</span></div><div class="cores">${p.cpu.cores.map((v, n) => `<div class="core"><i style="height:${v}%"></i><span class="num">${n}</span><b class="num">${fmtNum(v, 0)}%</b></div>`).join('')}</div>`;
    stats = `<div class="stats">${stat(t('pf.util'), fmtPct(p.cpu.util))}${stat(t('pf.speed'), fmtMhz(s.cpuMhz))}${stat(t('pf.temp'), fmtTemp(s.cpuTemp), tempClass(s.cpuTemp))}${stat(t('pf.procs'), fmtNum(p.cpu.processes, 0))}${stat(t('pf.threads'), fmtNum(p.cpu.threads, 0))}${stat(t('pf.handles'), fmtNum(p.cpu.handles, 0))}${stat(t('pf.uptime'), fmtUptime(p.cpu.uptimeMs))}${s.packagePower != null ? stat(t('pf.pkgPower'), fmtWatt(s.packagePower)) : ''}</div>`
      + kv([[t('pf.base'), fmtMhz(i.baseMhz)], [t('pf.sockets'), i.sockets], [t('pf.cores'), i.cores], [t('pf.logical'), i.logical], [t('pf.virt'), i.virtualization == null ? null : t(i.virtualization ? 'pf.enabled' : 'pf.disabled')], ['L1', fmtBytes(i.l1)], ['L2', fmtBytes(i.l2)], ['L3', fmtBytes(i.l3)]]);
  } else if (dev.id === 'mem') {
    const m = p.mem, info = m.info || {};
    model = `${fmtBytes(info.installedBytes || m.total, 0)} ${info.type || ''}`;
    perfChart('pfc-a', p, ['mem'], [C('--c-mem')], { max: 100, names: [t('pf.inUse')] });
    const parts = [[t('pf.inUse'), m.used - m.modified, C('--c-mem')], [t('pf.modified'), m.modified, C('--c-disk-w')], [t('pf.standby'), m.standby, C('--c-commit')], [t('pf.free'), m.free, C('--surface-3')]];
    const tot = parts.reduce((a, x) => a + Math.max(0, x[1]), 0) || 1;
    extra = `<div class="perf-sub" style="margin-top:12px"><span>${t('pf.composition')}</span></div><div class="comp">${parts.map(([, v, c]) => `<i style="width:${(Math.max(0, v) * 100) / tot}%;background:${c}"></i>`).join('')}</div>
      <div class="comp-legend">${parts.map(([l, v, c]) => `<span style="--lc:${c}">${esc(l)} <b class="num">${fmtBytes(v, 1)}</b></span>`).join('')}</div>`;
    stats = `<div class="stats">${stat(t('pf.inUse'), fmtBytes(m.used, 1))}${stat(t('pf.avail'), fmtBytes(m.avail, 1))}${stat(t('pf.committed'), `${fmtBytes(m.commit, 1)} / ${fmtBytes(m.commitLimit, 1)}`)}${stat(t('pf.cached'), fmtBytes(m.cached, 1))}${stat(t('pf.paged'), fmtBytes(m.paged, 0))}${stat(t('pf.nonpaged'), fmtBytes(m.nonPaged, 0))}</div>`
      + kv([[t('pf.memSpeed'), info.speedMts ? `${fmtNum(info.speedMts, 0)} MT/s` : null], [t('pf.type'), info.type], [t('pf.slots'), info.slotsTotal ? `${info.slotsUsed} / ${info.slotsTotal}` : null], [t('pf.form'), info.formFactor], [t('pf.reserved'), m.hardwareReserved ? fmtBytes(m.hardwareReserved, 1) : null]]);
  } else if (dev.id.startsWith('disk')) {
    const d = p.disks.find((x) => x.key === dev.id);
    model = d.model || '';
    perfChart('pfc-a', p, [d.key], [C('--c-disk-r')], { max: 100, names: [t('pf.active')] });
    perfChart('pfc-b', p, [`${d.key}r`, `${d.key}w`], [C('--c-disk-r'), C('--c-disk-w')], { fmt: (v) => fmtRate(v), names: [t('pf.read'), t('pf.write')], noArea: true });
    stats = `<div class="stats">${stat(t('pf.active'), fmtPct(d.active))}${stat(t('pf.resp'), `${fmtNum(d.respMs, 2)} ms`)}${stat(t('pf.read'), fmtRate(d.read))}${stat(t('pf.write'), fmtRate(d.write))}</div>`
      + kv([[t('pf.capacity'), fmtBytes(d.size, 0)], [t('pf.volumes'), d.volumes.join(' ')], [t('pf.type'), `${d.ssd ? 'SSD' : d.ssd === false ? 'HDD' : ''}${d.bus ? ` (${d.bus})` : ''}`], [t('pf.model'), esc(d.model || '')]]);
  } else if (dev.id.startsWith('net')) {
    const n = p.nets.find((x) => x.key === dev.id);
    model = n.description;
    perfChart('pfc-a', p, [`${n.key}t`, `${n.key}r`], [C('--c-net-tx'), C('--c-net-rx')], { fmt: (v) => fmtRate(v), names: [t('pf.send'), t('pf.recv')], noArea: false });
    stats = `<div class="stats">${stat(t('pf.send'), fmtRate(n.tx))}${stat(t('pf.recv'), fmtRate(n.rx))}</div>`
      + kv([[t('pf.adapter'), esc(n.name.replace(/^‏+/, ''))], [t('pf.conn'), esc(n.type)], [t('pf.link'), n.speedBps > 0 ? (n.speedBps >= 1e9 ? `${fmtNum(n.speedBps / 1e9, 1)} Gbps` : `${fmtNum(n.speedBps / 1e6, 1)} Mbps`) : null], [t('pf.ipv4'), n.iPv4 ?? n.ipv4], [t('pf.ipv6'), n.iPv6 ?? n.ipv6]]);
  } else if (dev.id.startsWith('gpu')) {
    const g = p.gpu.adapters.find((x) => x.key === dev.id) || { engines: {} };
    model = g.name || '';
    perfChart('pfc-a', p, [dev.id], [C('--c-gpu')], { max: 100, names: [t('pf.util')] });
    // Task Manager's four engines first, in a fixed order (charts are rebuilt only when the set changes)
    const rank = (e) => { const i = ['3D', 'Copy', 'VideoDecode', 'VideoEncode'].indexOf(e); return i < 0 ? 9 : i; };
    const engs = Object.keys(g.engines).sort((a, b) => rank(a) - rank(b) || a.localeCompare(b)).slice(0, 4);
    const host = $('#pfc-eng');
    const engSig = engs.join('|');
    if (host && host.dataset.sig !== engSig) {
      disposeCharts('pfc-e');
      host.dataset.sig = engSig;
      host.innerHTML = engs.map((e, i) => `<div><div class="perf-sub"><span>${esc(e)}</span><span class="num" id="pfc-ev${i}"></span></div><div class="perf-chart sm" id="pfc-e${i}"></div></div>`).join('');
    }
    engs.forEach((e, i) => { perfChart(`pfc-e${i}`, p, [`${dev.id}E:${e}`], [C('--c-gpu')], { max: 100, names: [e] }); const el = $(`#pfc-ev${i}`); if (el) el.textContent = fmtPct(g.engines[e]); });
    const ded = g.dedicatedUsed, dedT = g.dedicatedTotal, sh = g.sharedUsed, shT = g.sharedTotal;
    extra = `<div class="stats">
      <div class="stat"><span>${t('pf.dedicated')}</span><b class="num sm">${fmtBytes(ded, 1)} / ${fmtBytes(dedT, 1)}</b><div class="membar"><i style="width:${dedT ? (ded * 100) / dedT : 0}%;--bc:var(--c-gpu)"></i></div></div>
      <div class="stat"><span>${t('pf.shared')}</span><b class="num sm">${fmtBytes(sh, 1)} / ${fmtBytes(shT, 1)}</b><div class="membar"><i style="width:${shT ? (sh * 100) / shT : 0}%;--bc:var(--c-commit)"></i></div></div></div>`;
    stats = `<div class="stats">${stat(t('pf.util'), fmtPct(g.util))}${stat(t('pf.temp'), fmtTemp(g.temp), tempClass(g.temp))}${stat(t('pf.gpuClock'), fmtMhz(g.coreMhz))}${stat(t('pf.memClock'), fmtMhz(g.memMhz))}${g.power != null ? stat(t('pf.gpuPower'), fmtWatt(g.power)) : ''}</div>`
      + kv([[t('pf.driver'), esc(g.driver || '')], [t('pf.driverDate'), esc(g.driverDate || '')]]);
  } else {
    model = p.sensorSources || '';
    perfChart('pfc-a', p, ['cpuTemp', 'gpuTemp'], [C('--c-cpu'), C('--c-gpu')], { fmt: (v) => `${fmtNum(v, 0)}°`, names: [t('s.cpuTemp'), t('s.gpuTemp')], noArea: true });
    perfChart('pfc-b', p, ['cpuMhz', 'gpuMhz'], [C('--c-cpu'), C('--c-gpu')], { fmt: (v) => fmtMhz(v), names: [t('s.cpuMhz'), t('s.gpuMhz')], noArea: true });
    const tiles = [];
    const tile = (label, v, sub, cls = '') => tiles.push(`<div class="sensor"><span>${esc(label)}</span><b class="num ${cls}">${v}</b>${sub ? `<small> ${esc(sub)}</small>` : ''}</div>`);
    if (s.cpuTemp != null) tile(t('pf.cpuTemp'), fmtTemp(s.cpuTemp), s.cpuTempSource, tempClass(s.cpuTemp));
    if (s.gpuTemp != null) tile(t('pf.gpuTemp'), fmtTemp(s.gpuTemp), s.gpuTempSource, tempClass(s.gpuTemp));
    if (s.socTemp != null) tile(t('pf.socTemp'), fmtTemp(s.socTemp), '', tempClass(s.socTemp));
    if (s.gpuHotspot != null) tile(t('pf.hotspot'), fmtTemp(s.gpuHotspot), '', tempClass(s.gpuHotspot));
    for (const [k, v] of Object.entries(s.thermalZones || {})) tile(t('pf.zone', { n: k }), fmtTemp(v), 'ACPI', tempClass(v));
    if (s.cpuMhz != null) tile(t('s.cpuMhz'), fmtMhz(s.cpuMhz), s.cpuBaseMhz ? `${t('pf.base')} ${fmtMhz(s.cpuBaseMhz)}` : '');
    if (s.gpuMhz != null) tile(t('s.gpuMhz'), fmtMhz(s.gpuMhz));
    if (s.gpuMemMhz != null) tile(t('pf.memClock'), fmtMhz(s.gpuMemMhz));
    if (s.packagePower != null) tile(t('pf.pkgPower'), fmtWatt(s.packagePower));
    if (s.gpuPower != null) tile(t('pf.gpuPower'), fmtWatt(s.gpuPower));
    if (s.fan1Rpm != null) tile(t('pf.fan', { n: 1 }), `${fmtNum(s.fan1Rpm, 0)} RPM`);
    if (s.fan2Rpm != null) tile(t('pf.fan', { n: 2 }), `${fmtNum(s.fan2Rpm, 0)} RPM`);
    extra = tiles.length ? `<div class="sensor-grid" style="margin-top:14px">${tiles.join('')}</div>` : `<div class="note">${t('pf.noSensors')}</div>`;
    if (s.fan1Rpm == null && !state.init.isAdmin && p.isLenovo) extra += `<div class="note">${t('pf.fanAdmin')}</div>`;
  }
  $('#pfTitle').textContent = dev.title;
  $('#pfModel').textContent = model;
  $('#pfExtra').innerHTML = extra;
  $('#pfStats').innerHTML = stats;
}
function fmtUptime(ms) {
  const s = Math.floor(ms / 1000);
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  const p2 = (x) => String(x).padStart(2, '0');
  return `${d}:${p2(h)}:${p2(m)}:${p2(sec)}`;
}

/* ================= Startup apps ================= */
async function loadStartup() {
  const rows = await call('startupList');
  state.suRows = rows;
  renderStartup();
}
function renderStartup() {
  const rows = state.suRows || [];
  $('#suCount').innerHTML = t('su.count', { n: N(rows.length), e: N(rows.filter((r) => r.enabled).length) });
  const needAdmin = rows.some((r) => r.needsAdmin) && !state.init.isAdmin;
  $('#suTable').innerHTML = `<table><thead><tr><th>${t('col.name')}</th>${shown('startup', 'publisher') ? `<th>${t('col.publisher')}</th>` : ''}<th>${t('col.status')}</th>${shown('startup', 'source') ? `<th>${t('col.source')}</th>` : ''}<th></th></tr></thead><tbody>
    ${rows.map((r) => `<tr data-id="${esc(r.id)}">
      <td><div class="icon-cell">${r.path ? `<img src="fileicon?p=${encodeURIComponent(r.path)}" onerror="this.outerHTML='<span class=&quot;ph&quot;>${esc((r.name || '?')[0].toUpperCase())}</span>'">` : `<span class="ph">${esc((r.name || '?')[0].toUpperCase())}</span>`}<div><b>${esc(r.descr || r.name)}</b><span title="${esc(r.command)}">${esc(r.path || r.command)}</span></div></div></td>
      ${shown('startup', 'publisher') ? `<td>${esc(r.publisher || '—')}</td>` : ''}
      <td><div class="su-status"><label class="switch" title="${r.enabled ? t('su.enabled') : t('su.disabled')}"><input type="checkbox" data-su="${esc(r.id)}" ${r.enabled ? 'checked' : ''} ${r.needsAdmin && !state.init.isAdmin ? 'disabled' : ''}><i></i></label><span class="muted">${r.enabled ? t('su.enabled') : t('su.disabled')}</span></div></td>
      ${shown('startup', 'source') ? `<td class="muted">${esc(t(`su.src.${r.source}`))}</td>` : ''}
      <td><div class="row-btns">${r.path ? `<button class="mini-btn" data-loc="${esc(r.path)}">${t('pa.openLoc')}</button>` : ''}</div></td>
    </tr>`).join('')}</tbody></table>${needAdmin ? `<div class="note" style="margin:10px">${t('su.adminNote')}</div>` : ''}`;
  $$('#suTable input[data-su]').forEach((el) => el.addEventListener('change', async () => {
    const res = await call('startupSet', { id: el.dataset.su, enabled: el.checked });
    if (!res.ok) { toast(t(`pa.err.${res.error}`), 4000); el.checked = !el.checked; return; }
    await loadStartup();
  }));
  $$('#suTable [data-loc]').forEach((b) => b.addEventListener('click', () => call('openFileLocation', { path: b.dataset.loc })));
}

/* ================= Services ================= */
state.svFilter = 'all';
async function loadServices() {
  state.svRows = await call('services');
  renderServices();
}
function renderServices() {
  const rows = state.svRows || [];
  $$('#svFilter button').forEach((b) => b.classList.toggle('active', b.dataset.v === state.svFilter));
  $('#svCount').innerHTML = t('sv.count', { n: N(rows.length), r: N(rows.filter((r) => r.status === 'running').length) });
  $('#svAdmin').innerHTML = state.init.isAdmin ? '' : `<div class="note warn" style="display:flex;justify-content:space-between;align-items:center;gap:10px">${t('sv.adminTip')}<button class="btn" id="svAdminBtn">${t('set.restartAdmin')}</button></div>`;
  $('#svAdminBtn')?.addEventListener('click', () => call('restartAsAdmin'));
  const q = $('#svSearch').value.trim().toLowerCase();
  const list = rows.filter((r) => (state.svFilter === 'all' || (state.svFilter === 'running' ? r.status === 'running' : r.status !== 'running'))
    && (!q || r.name.toLowerCase().includes(q) || r.display.toLowerCase().includes(q) || (r.descr || '').toLowerCase().includes(q)));
  const stColor = { running: 'var(--good)', stopped: 'var(--muted)', starting: 'var(--warn)', stopping: 'var(--warn)', paused: 'var(--warn)' };
  const scroll = $('#svTable').scrollTop;
  $('#svTable').innerHTML = `<table><thead><tr><th>${t('col.name')}</th>${shown('services', 'descr') ? `<th>${t('col.display')}</th>` : ''}${shown('services', 'pid') ? `<th class="r">${t('col.pid')}</th>` : ''}<th>${t('col.status')}</th>${shown('services', 'startType') ? `<th>${t('col.startType')}</th>` : ''}<th></th></tr></thead><tbody>
    ${list.map((r) => `<tr>
      <td><b>${esc(r.display)}</b><div class="muted num" style="font-size:11.5px">${esc(r.name)}</div></td>
      ${shown('services', 'descr') ? `<td class="wrap">${esc(r.descr || '')}</td>` : ''}
      ${shown('services', 'pid') ? `<td class="r num">${r.pid ?? '—'}</td>` : ''}
      <td><span class="st-badge" style="--sc:${stColor[r.status] || 'var(--muted)'}">${esc(t(`sv.st.${r.status}`))}</span></td>
      ${shown('services', 'startType') ? `<td class="muted">${esc(t(`sv.t.${r.startType}`))}</td>` : ''}
      <td><div class="row-btns">
        ${r.status === 'running' ? `<button class="mini-btn" data-sv="stop" data-n="${esc(r.name)}" data-d="${esc(r.display)}">${t('sv.stop')}</button><button class="mini-btn" data-sv="restart" data-n="${esc(r.name)}" data-d="${esc(r.display)}">${t('sv.restart')}</button>`
          : r.startType !== 'disabled' ? `<button class="mini-btn" data-sv="start" data-n="${esc(r.name)}" data-d="${esc(r.display)}">${t('sv.start')}</button>` : ''}
      </div></td>
    </tr>`).join('')}</tbody></table>`;
  $('#svTable').scrollTop = scroll;
  $$('#svTable [data-sv]').forEach((b) => b.addEventListener('click', async () => {
    const a = b.dataset.sv;
    const ok = await confirmDialog(t('sv.confirm', { a: t(`sv.${a}`), n: b.dataset.d }), b.dataset.n, t(`sv.${a}`), a !== 'start');
    if (!ok) return;
    b.disabled = true;
    const res = await call('serviceControl', { name: b.dataset.n, action: a });
    if (res.ok) toast(t('pa.done')); else toast(res.detail ? `${t(`pa.err.${res.error}`)} (${res.detail})` : t(`pa.err.${res.error}`), 5000);
    await loadServices();
  }));
}

/* ================= Settings ================= */
const sw = (key, disabled) => `<label class="switch"><input type="checkbox" data-k="${key}" ${state.draft[key] ? 'checked' : ''} ${disabled ? 'disabled' : ''}><i></i></label>`;
const seg = (key, opts, fmt) => `<div class="seg" data-seg="${key}">${opts.map((o) => `<button data-v="${o}" class="${state.draft[key] === o ? 'active' : ''}">${fmt ? fmt(o) : o}</button>`).join('')}</div>`;
const row = (label, hint, control) => `<div class="set-row"><div class="lbl"><b>${label}</b>${hint ? `<span>${hint}</span>` : ''}</div>${control}</div>`;
const slider = (key, min, max, step, suffix = '', disabled = false) => `<div style="display:flex;gap:10px;align-items:center"><input type="range" min="${min}" max="${max}" step="${step}" data-num="${key}" data-suffix="${suffix}" value="${state.draft[key]}" ${disabled ? 'disabled' : ''}><b class="num" style="min-width:36px">${state.draft[key]}${suffix}</b></div>`;

function renderSettings() {
  if (!state.draft) state.draft = structuredClone(state.settings);
  const d = state.draft;
  const init = state.init;
  const dirty = JSON.stringify(d) !== JSON.stringify(state.settings);
  const sec = (v) => (v >= 60 ? `${v / 60} ${t('u.min')}` : `${v} ${t('u.s')}`);
  const days = (v) => `${v} ${v === 1 ? t('set.day') : t('set.days')}`;

  $('#settings').innerHTML = `
  <div class="card set-card">
    <h3>${t('set.sampling')}</h3><div class="desc">${t('set.samplingDesc')}</div>
    ${row(t('set.interval'), t('set.intervalHint'), `<div style="display:flex;gap:8px;align-items:center"><input class="num-input num" type="text" inputmode="numeric" dir="ltr" data-num="intervalSeconds" value="${d.intervalSeconds}"><span class="muted">${t('set.seconds')}</span></div>`)}
    <div style="padding-bottom:6px">${seg('intervalSeconds', [1, 2, 5, 10, 15, 30, 60, 300], sec)}</div>
    ${row(t('set.fullRes'), t('set.fullResHint'), '')}
    <div style="padding-bottom:6px">${seg('fullResolutionDays', [1, 3, 7, 14, 30, 90], days)}</div>
    ${row(t('set.retention'), '', '')}
    ${seg('retentionDays', [7, 14, 30, 60, 90, 180, 365], days)}
  </div>

  <div class="card set-card">
    <h3>${t('set.metrics')}</h3><div class="desc">${t('set.metricsDesc')}</div>
    ${row(t('set.cpu'), t('set.cpuHint'), sw('trackCpu'))}
    ${row(t('set.mem'), t('set.memHint'), sw('trackMemory'))}
    ${row(t('set.disk'), t('set.diskHint'), sw('trackDisk'))}
    ${row(t('set.net'), t('set.netHint'), sw('trackNetwork'))}
    ${row(t('set.gpu'), t('set.gpuHint'), sw('trackGpu'))}
    ${row(t('set.sensors'), t('set.sensorsHint'), sw('trackSensors'))}
    ${row(t('set.procs'), t('set.procsHint'), sw('trackProcesses'))}
    ${row(t('set.procNet'), t('set.procNetHint'), sw('perProcessNetwork', !d.trackNetwork || !d.trackProcesses))}
    ${row(t('set.titles'), t('set.titlesHint'), sw('recordWindowTitles'))}
    ${row(t('set.topN'), t('set.topNHint'), slider('topProcesses', 5, 100, 5))}
    ${row(t('set.topInst'), t('set.topInstHint'), slider('topInstances', 5, 100, 5))}
    ${!init.isAdmin && d.perProcessNetwork && d.trackNetwork ? `<div class="note warn">${t('set.netAdminNote')}</div>` : ''}
  </div>

  <div class="card set-card">
    <h3>${t('set.export')}</h3><div class="desc">${t('set.exportDesc')}</div>
    ${row(t('set.expRange'), t('set.expRangeHint'), `<div class="seg" id="expRange">${EXPORT_KINDS.map((k) => `<button data-v="${k}" class="${state.exportRange.kind === k ? 'active' : ''}">${t(k === 'all' ? 'set.expAll' : `range.${k}`)}</button>`).join('')}</div>`)}
    ${state.exportRange.kind === 'custom' ? `<div class="custom-range" style="margin:0 0 10px">
      <label><span>${t('range.from')}</span><input type="datetime-local" step="1" id="expFrom" value="${toLocalInput(state.exportRange.from)}"></label>
      <label><span>${t('range.to')}</span><input type="datetime-local" step="1" id="expTo" value="${toLocalInput(state.exportRange.to)}"></label>
    </div>` : ''}
    <div class="btn-row">
      <button class="btn primary" id="bExpAi">${t('set.exportAi')}<span class="exp-size num" data-est="summary"></span></button>
      <button class="btn" id="bExpFull">${t('set.exportFull')}<span class="exp-size num" data-est="full"></span></button>
    </div>
    <div class="note">${t('set.exportAiHint')}</div>
    <div class="btn-row" style="margin-top:10px;align-items:center">
      <label class="check"><input type="checkbox" id="expTitles" ${state.exportTitles ? 'checked' : ''}>${t('set.exportTitles')}</label>
      <button class="btn" id="bPrompt">${t('set.copyPrompt')}</button>
    </div>
    <div class="btn-row" style="margin-top:10px">
      <button class="btn" id="bExpSys">${t('set.exportSys')}</button>
      <button class="btn" id="bExpProc">${t('set.exportProc')}</button>
    </div>
    <div class="note">${t('set.exportHint')} <span class="num">${esc(rangeLabel(exportRange(), state.exportRange.kind !== 'custom'))}</span></div>
  </div>

  <div class="card set-card">
    <h3>${t('set.perf')}</h3><div class="desc">${t('set.perfDesc')}</div>
    ${row(t('set.selfNow'), '', `<span id="selfNow">${selfText()}</span>`)}
    ${row(t('set.eco'), t('set.ecoHint'), sw('efficiencyMode'))}
    <div class="note">${t('set.perfTip')}</div>
  </div>

  <div class="card set-card">
    <h3>${t('set.startup')}</h3><div class="desc">${t('set.startupDesc')}</div>
    ${row(t('set.autostart'), t('set.autostartHint'), sw('autostart'))}
    ${row(t('set.elevated'), t('set.elevatedHint'), sw('autostartElevated', !d.autostart))}
    <div class="note">${t('set.moveHint')}<div class="path" style="margin-top:4px">${esc(init.exePath)}</div></div>
  </div>

  <div class="card set-card">
    <h3>${t('set.alerts')}</h3><div class="desc">${t('set.alertsDesc')}</div>
    ${row(t('set.alertsOn'), '', sw('alertsEnabled'))}
    ${row(t('set.alertMem'), '', slider('alertMemoryPercent', 50, 99, 1, '%', !d.alertsEnabled))}
    ${row(t('set.alertCpu'), '', slider('alertCpuPercent', 50, 100, 1, '%', !d.alertsEnabled))}
    ${row(t('set.alertTemp'), '', slider('alertTempC', 60, 110, 1, '°C', !d.alertsEnabled))}
    ${row(t('set.alertSustain'), '', seg('alertSustainSeconds', [0, 30, 60, 120, 300], sec))}
  </div>

  <div class="card set-card">
    <h3>${t('ov.title')}</h3><div class="desc">${t('ov.desc')}</div>
    ${row(t('ov.enable'), 'Ctrl + Shift + O', sw('overlayEnabled'))}
    ${row(t('ov.corner'), '', seg('overlayCorner', ['tl', 'tr', 'bl', 'br'], (v) => t(`ov.c.${v}`)))}
    <div class="set-row" style="flex-direction:column;align-items:stretch"><div class="lbl"><b>${t('ov.items')}</b></div>
      <div class="vis-items" style="margin-top:6px">${['cpu', 'cpuTemp', 'cpuClock', 'gpu', 'gpuTemp', 'gpuClock', 'ram', 'vram', 'power', 'net', 'fan', 'time']
        .map((k) => `<label class="check"><input type="checkbox" data-ovi="${k}" ${(d.overlayItems || []).includes(k) ? 'checked' : ''}>${esc(t(`ov.i.${k}`))}</label>`).join('')}</div></div>
    ${row(t('ov.opacity'), '', slider('overlayOpacity', 0, 100, 5, '%'))}
    ${row(t('ov.size'), '', slider('overlayScale', 60, 200, 10, '%'))}
    ${row(t('ov.hotkey'), 'Ctrl + Shift + O', sw('overlayHotkey'))}
    <div class="note">${t('ov.note')}</div>
  </div>

  <div class="card set-card vis-card">
    <h3>${t('set.ui')}</h3><div class="desc">${t('set.uiDesc')}</div>
    <div class="vis-grid">
      ${Object.entries(UI_ITEMS).map(([page, items]) => {
        const pageOn = !(d.hidden || []).includes(`page:${page}`);
        return `<div class="vis-page ${pageOn ? '' : 'off'}">
          <div class="vis-head"><b>${t(`nav.${page}`)}</b><label class="switch"><input type="checkbox" data-vis="page:${page}" ${pageOn ? 'checked' : ''}><i></i></label></div>
          <div class="vis-items">${Object.entries(items).map(([item, label]) => `<label class="check"><input type="checkbox" data-vis="${page}:${item}" ${(d.hidden || []).includes(`${page}:${item}`) ? '' : 'checked'} ${pageOn ? '' : 'disabled'}>${esc(t(label))}</label>`).join('')}</div>
        </div>`;
      }).join('')}
    </div>
    <div class="note">${t('set.uiNote')}</div>
  </div>

  <div class="card set-card">
    <h3>${t('set.appearance')}</h3>
    ${row(t('set.lang'), '', seg('language', ['ar', 'en'], (v) => (v === 'ar' ? 'العربية' : 'English')))}
    ${row(t('set.theme'), '', seg('theme', ['dark', 'light'], (v) => t(`set.${v}`)))}
    ${row(t('set.smooth'), t('set.smoothHint'), sw('smoothScrolling'))}
  </div>

  <div class="card set-card">
    <h3>${t('set.data')}</h3><div class="desc">${t('set.dataDesc')}</div>
    ${row(t('set.dbSize'), '', `<b class="num" id="dbSize">${fmtBytes(init.dbSize)}</b>`)}
    <div class="path">${esc(init.dataDir)}</div>
    <div class="btn-row" style="margin-top:12px">
      <button class="btn" id="bOpen">${t('set.openFolder')}</button>
      <button class="btn danger" id="bClear">${t('set.clear')}</button>
    </div>
  </div>

  <div class="card set-card">
    <h3>${t('set.about')}</h3>
    ${row(t('set.version'), '', `<b class="num">${esc(init.version)}</b>`)}
    ${row(t('set.admin'), '', `<span class="badge ${init.isAdmin ? 'ok' : 'info'}">${init.isAdmin ? t('set.isAdmin') : t('set.notAdmin')}</span>`)}
    ${row(t('set.sensorSrc'), '', `<span class="num muted">${esc(init.sensorSources || '—')}</span>`)}
    <div class="btn-row" style="margin-top:12px">
      ${init.isAdmin ? '' : `<button class="btn" id="bAdmin">${t('set.restartAdmin')}</button>`}
      <button class="btn danger" id="bExit">${t('set.exit')}</button>
    </div>
  </div>

  ${dirty ? `<div class="save-bar"><b>${t('set.unsaved')}</b><div class="btn-row"><button class="btn" id="bDiscard">${t('set.discard')}</button><button class="btn primary" id="bSave">${t('set.save')}</button></div></div>` : ''}
  `;

  const host = $('#settings');
  $$('input[type=checkbox][data-k]', host).forEach((el) => el.addEventListener('change', () => { d[el.dataset.k] = el.checked; renderSettings(); }));
  $$('input[type=checkbox][data-ovi]', host).forEach((el) => el.addEventListener('change', () => {
    const set = new Set(d.overlayItems || []);
    if (el.checked) set.add(el.dataset.ovi); else set.delete(el.dataset.ovi);
    const order = ['cpu', 'cpuTemp', 'cpuClock', 'gpu', 'gpuTemp', 'gpuClock', 'ram', 'vram', 'power', 'net', 'fan', 'time'];
    d.overlayItems = order.filter((k) => set.has(k));
    renderSettings();
  }));
  $$('input[type=checkbox][data-vis]', host).forEach((el) => el.addEventListener('change', () => {
    const key = el.dataset.vis;
    const set = new Set(d.hidden || []);
    if (el.checked) set.delete(key); else set.add(key);
    d.hidden = [...set];
    renderSettings();
  }));
  $$('[data-seg] button', host).forEach((b) => b.addEventListener('click', () => {
    const key = b.parentElement.dataset.seg;
    const v = b.dataset.v;
    d[key] = isNaN(+v) ? v : +v;
    renderSettings();
  }));
  $$('[data-num]', host).forEach((el) => {
    el.addEventListener(el.type === 'range' ? 'input' : 'change', () => {
      const v = Math.round(el.type === 'range' ? +el.value : numVal(el.value));
      if (Number.isFinite(v)) d[el.dataset.num] = el.dataset.num === 'intervalSeconds' ? Math.min(600, Math.max(1, v)) : v;
      if (el.type === 'range') el.nextElementSibling.textContent = `${el.value}${el.dataset.suffix || ''}`;
      else renderSettings();
    });
    if (el.type === 'range') el.addEventListener('change', () => renderSettings());
  });
  $('#bSave')?.addEventListener('click', () => saveSettings());
  $('#bDiscard')?.addEventListener('click', () => { state.draft = null; renderSettings(); });
  $('#bOpen').addEventListener('click', () => call('openDataFolder'));
  $('#expTitles').addEventListener('change', (e) => { state.exportTitles = e.target.checked; updateExportEstimate(); });
  updateExportEstimate();
  $$('#expRange button').forEach((b) => b.addEventListener('click', () => {
    const r = state.exportRange;
    if (b.dataset.v === 'custom' && r.kind !== 'custom') Object.assign(r, exportRange());
    r.kind = b.dataset.v;
    renderSettings();
  }));
  ['expFrom', 'expTo'].forEach((id) => $(`#${id}`)?.addEventListener('change', (e) => {
    const v = new Date(e.target.value).getTime();
    if (Number.isFinite(v)) state.exportRange[id === 'expFrom' ? 'from' : 'to'] = v;
    renderSettings();
  }));
  $('#bExpAi').addEventListener('click', (e) => exportJson('summary', e.currentTarget));
  $('#bExpFull').addEventListener('click', (e) => exportJson('full', e.currentTarget));
  $('#bPrompt').addEventListener('click', async () => {
    try { await navigator.clipboard.writeText(t('aiPrompt')); toast(t('set.copied'), 3500); } catch (err) { toast(err.message); }
  });
  $('#bExpSys').addEventListener('click', () => exportCsv('system'));
  $('#bExpProc').addEventListener('click', () => exportCsv('processes'));
  $('#bClear').addEventListener('click', async () => {
    if (!confirm(t('set.clearConfirm'))) return;
    await call('clearData');
    const info = await call('dbInfo');
    state.init.dbSize = info.dbSize;
    toast(t('cleared'));
    renderSettings();
  });
  $('#bAdmin')?.addEventListener('click', () => call('restartAsAdmin'));
  $('#bExit').addEventListener('click', () => call('exitApp'));
}
// Resolves the export period at click time; "all" re-reads the oldest stored sample, which retention may have moved.
async function exportRangeNow() {
  if (state.exportRange.kind === 'all') state.init.ranges = await call('ranges');
  const r = exportRange();
  if (!(r.from < r.to)) throw new Error(t('set.expBadRange'));
  return r;
}
async function exportCsv(kind) {
  try {
    const path = await call('exportCsv', { ...(await exportRangeNow()), kind });
    if (path) toast(t('exported', { p: path }), 4000);
  } catch (e) { toast(e.message, 5000); }
}
// Above this a full export no longer fits in an AI chat (about 1 MB of JSON is already ~300k tokens).
const BIG_EXPORT = 5 * 1048576;
let estimateSeq = 0;
async function updateExportEstimate() {
  const seq = ++estimateSeq;
  let est = null;
  try { est = await call('exportEstimate', { ...exportRange(), titles: state.exportTitles }); } catch { /* buttons stay without a size */ }
  if (seq !== estimateSeq) return;
  $$('[data-est]').forEach((el) => { el.textContent = est?.samples ? `≈ ${fmtBytes(est[el.dataset.est])}` : ''; });
}
async function exportJson(level, btn) {
  let range;
  try { range = await exportRangeNow(); } catch (e) { toast(e.message, 5000); return; }
  if (level === 'full') {
    const est = await call('exportEstimate', { ...range, titles: state.exportTitles }).catch(() => null);
    if (est?.full > BIG_EXPORT && !confirm(t('set.expBigConfirm', { s: fmtBytes(est.full) }))) return;
  }
  btn.disabled = true;
  const old = btn.innerHTML;
  btn.textContent = t('exporting');
  try {
    const res = await call('exportJson', { ...range, level, titles: state.exportTitles });
    if (res) toast(t('exported', { p: `${res.path} (${fmtBytes(res.size)})` }), 5000);
  } catch (e) { toast(e.message, 5000); } finally { btn.disabled = false; btn.innerHTML = old; }
}
async function saveSettings(next) {
  const payload = next || state.draft;
  const res = await call('saveSettings', { settings: payload });
  const langChanged = res.settings.language !== state.settings.language;
  const themeChanged = res.settings.theme !== state.settings.theme;
  state.settings = res.settings;
  state.draft = null;
  if (themeChanged) applyTheme();
  if (langChanged || themeChanged) { applyI18n(); disposeCharts(); buildKpis(); renderSideMeta(); fillBootPicker(); buildSpanSeg(); }
  $('#recText').textContent = t('rec', { s: fmtInterval(state.settings.intervalSeconds) });
  applyVisibility();
  if (res.autostartError === 'uac-cancelled') toast(t('set.uacCancelled'), 4000);
  else if (res.autostartError) toast(t('set.taskFailed'), 4000);
  else toast(t('set.saved'));
  showPage(state.page);
}

/* ================= Theme / language ================= */
function applyTheme() {
  document.documentElement.dataset.theme = state.settings.theme === 'light' ? 'light' : 'dark';
}

/* ================= Boot ================= */
function setRange(kind) {
  if (kind === 'custom') {
    if (state.range.kind !== 'custom') {
      const r = currentRange();
      state.range = { kind: 'custom', from: r.from, to: r.to };
    }
    syncRangeUi();
    return;
  }
  state.rangeStack = [];
  state.range = { kind, from: 0, to: 0 };
  syncRangeUi();
  updateSubtitle();
  loadPage();
}

function fillBootPicker() {
  const sel = $('#bootPick');
  const boots = state.init.ranges?.boots || [];
  sel.innerHTML = `<option value="">${t('range.pickBoot')}</option>` + boots.map((b) =>
    `<option value="${b.first}|${b.last}">${esc(t('range.session', { d: fmtTime(b.boot, { date: true }) }))} · ${fmtDur(b.last - b.first)}</option>`).join('');
}
function renderSideMeta() {
  const init = state.init;
  $('#sideMeta').innerHTML = `<span class="num">v${esc(init.version)}</span><span>${init.isAdmin ? t('set.isAdmin') : t('set.notAdmin')}</span>`;
}

async function main() {
  const init = await call('init');
  state.init = init;
  state.settings = init.settings;
  state.boot = init.boot || Date.now();
  applyTheme();
  applyI18n();
  buildKpis();
  fillBootPicker();
  renderSideMeta();
  $('#recText').textContent = t('rec', { s: fmtInterval(state.settings.intervalSeconds) });

  // Seed sparklines with the last 15 minutes of history.
  try {
    const now = Date.now();
    const hist = await call('system', { from: now - 15 * 60e3, to: now, points: 180 });
    state.liveHist = hist.t.map((ts, i) => ({
      ts, cpu: hist.cpu[i], gpu: hist.gpu[i], memUsed: hist.mem[i], memTotal: hist.memTotal[i],
      diskR: hist.diskR[i], diskW: hist.diskW[i], netRx: hist.netRx[i], netTx: hist.netTx[i],
    })).filter((x) => x.memTotal != null);
  } catch { /* first run */ }
  if (init.live) onLive(init.live);

  $$('.nav-item').forEach((b) => b.addEventListener('click', () => showPage(b.dataset.page)));
  $$('[data-goto]').forEach((b) => b.addEventListener('click', () => showPage(b.dataset.goto)));
  $$('#rangeBar button').forEach((b) => b.addEventListener('click', () => setRange(b.dataset.range)));
  $('#btnBack').addEventListener('click', goBack);
  $('#cApply').addEventListener('click', () => {
    const f = new Date($('#cFrom').value).getTime(), to = new Date($('#cTo').value).getTime();
    if (!(f < to)) return;
    state.range = { kind: 'custom', from: f, to };
    updateSubtitle(); loadPage();
  });
  $('#bootPick').addEventListener('change', (e) => {
    if (!e.target.value) return;
    const [f, to] = e.target.value.split('|').map(Number);
    $('#cFrom').value = toLocalInput(f); $('#cTo').value = toLocalInput(to + 1000);
  });
  $('#procSearch').addEventListener('input', renderProcTable);
  $('#liveSearch').addEventListener('input', renderLive);
  buildSpanSeg();
  applyVisibility();
  $('.content').addEventListener('scroll', hideAllTips, { passive: true });
  $$('#liveView button').forEach((b) => b.addEventListener('click', () => { state.liveView = b.dataset.v; state.liveSel = null; renderLive(); }));
  $('#btnEndTask').addEventListener('click', () => endTask(state.liveSel, state.liveSel?.kind === 'app'));
  $$('#svFilter button').forEach((b) => b.addEventListener('click', () => { state.svFilter = b.dataset.v; renderServices(); }));
  $('#svSearch').addEventListener('input', () => renderServices());
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Delete' && state.page === 'live' && state.liveSel && !e.target.closest('input')) endTask(state.liveSel, state.liveSel.kind === 'app');
  });
  let minTimer;
  $('#mMin').addEventListener('input', () => { clearTimeout(minTimer); minTimer = setTimeout(() => loadMoments(false), 400); });
  $('#drClose').addEventListener('click', closeDrawer);
  $('#scrim').addEventListener('click', closeDrawer);
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') closeDrawer();
    if (state.drawer?.kind === 'moment' && (e.key === 'ArrowLeft' || e.key === 'ArrowRight')) {
      const forward = (e.key === 'ArrowRight') !== (lang() === 'ar');
      const target = forward ? state.drawer.data?.next : state.drawer.data?.prev;
      if (target) openMoment(target, 1, state.drawer.metric);
    }
  });
  $('#btnLang').addEventListener('click', () => saveSettings({ ...state.settings, language: lang() === 'ar' ? 'en' : 'ar' }));
  $('#btnTheme').addEventListener('click', () => saveSettings({ ...state.settings, theme: state.settings.theme === 'light' ? 'dark' : 'light' }));

  setRange('boot');
  showPage('overview');

  // Periodic refresh of history views while the range ends at "now" (paused while hidden).
  setInterval(() => {
    if (document.hidden || state.range.kind === 'custom' || state.drawer) return;
    if (['overview', 'processes', 'insights'].includes(state.page)) { updateSubtitle(); loadPage(); }
  }, 30_000);
}

main().catch((e) => { console.error(e); document.body.insertAdjacentHTML('beforeend', `<div class="toast">${esc(e.message)}</div>`); });
