'use strict';
/* Performance counters page (Performance Monitor style): live chart + legend table, counter browser, one-click presets,
   recording and the recorded history. Polls the backend only while the page is visible. */
(() => {
  const ID = 'counters';

  Object.assign(I18N.ar, {
    'nav.counters': 'عدادات الأداء',
    'title.counters': 'مراقب الأداء (عدادات Windows)',
    'cn.sub': 'عدادات أداء Windows كما في Performance Monitor — تُقرأ كل ثانية ما دامت هذه الصفحة مفتوحة فقط',
    'cn.subHist': 'العدادات التي اخترت تسجيلها، محفوظة مع سجل البرنامج',
    'cn.tab.live': 'مباشر', 'cn.tab.history': 'السجل',
    'cn.add': 'إضافة عدادات', 'cn.pause': 'إيقاف مؤقت', 'cn.resume': 'استئناف', 'cn.paused': 'متوقف مؤقتاً',
    'cn.clearAll': 'إزالة الكل', 'cn.clearAllQ': 'إزالة كل العدادات من الرسم المباشر؟', 'cn.clearAllBody': 'العدادات المسجّلة تستمر في التسجيل، ويمكنك إيقافها من «السجل».',
    'cn.count': '{n} من {max} عداداً',
    'cn.chartTitle': 'آخر دقيقتين', 'cn.chartNote': 'القيم على الرسم مضروبة في «المقياس»؛ الجدول يعرض القيم الحقيقية',
    'cn.empty': 'لم تُضف أي عدادات بعد — اختر من «إضافة سريعة» بالأسفل أو اضغط «إضافة عدادات»',
    'cn.col.show': 'إظهار', 'cn.col.counter': 'العدّاد', 'cn.col.instance': 'المثيل', 'cn.col.last': 'الأخيرة', 'cn.col.avg': 'المتوسط',
    'cn.col.min': 'الأدنى', 'cn.col.max': 'الأقصى', 'cn.col.scale': 'المقياس', 'cn.col.record': 'تسجيل', 'cn.col.status': 'الحالة',
    'cn.auto': 'تلقائي', 'cn.remove': 'إزالة', 'cn.allInst': 'كل المثيلات',
    'cn.instCount': '{n} مثيل', 'cn.more': 'يُعرض أنشط {n} فقط',
    'cn.noRecWild': 'التسجيل يحتاج مثيلاً واحداً محدداً، لا «*»',
    'cn.presets': 'إضافة سريعة', 'cn.presetsHint': 'عدادات شائعة بأسمائها الإنجليزية، فتعمل مع أي لغة لـ Windows',
    'cn.g.cpu': 'المعالج', 'cn.g.mem': 'الذاكرة', 'cn.g.disk': 'القرص', 'cn.g.net': 'الشبكة', 'cn.g.sys': 'النظام',
    'cn.p.cpu': 'انشغال المعالج % (الإجمالي)', 'cn.p.queue': 'طابور انتظار المعالج', 'cn.p.ctx': 'تبديلات السياق في الثانية',
    'cn.p.avail': 'الذاكرة المتاحة (MB)', 'cn.p.pages': 'صفحات الذاكرة في الثانية', 'cn.p.committed': 'الذاكرة الملتزَم بها (بايت)',
    'cn.p.commitPct': 'نسبة الذاكرة الملتزَم بها %', 'cn.p.pagefile': 'استخدام ملف الصفحات %',
    'cn.p.diskTime': 'انشغال القرص %', 'cn.p.diskQueue': 'متوسط طابور القرص', 'cn.p.diskBytes': 'بايتات القرص في الثانية',
    'cn.p.netBytes': 'بايتات الشبكة في الثانية (كل محوّل)', 'cn.p.handles': 'عدد المقابض (Handles) لكل العمليات', 'cn.p.threads': 'عدد الخيوط (Threads) في النظام',
    'cn.br.title': 'إضافة عدادات', 'cn.br.search': 'ابحث في الكائنات والعدادات…', 'cn.br.refresh': 'تحديث القائمة', 'cn.br.close': 'إغلاق',
    'cn.br.loading': 'جارٍ قراءة قائمة العدادات من Windows…', 'cn.br.indexing': 'جارٍ تجهيز البحث في أسماء العدادات…',
    'cn.br.objects': 'الكائنات', 'cn.br.counters': 'العدادات', 'cn.br.instances': 'المثيلات',
    'cn.br.pickObject': 'اختر كائناً من القائمة', 'cn.br.single': 'هذا الكائن ليس له مثيلات', 'cn.br.noInst': 'لا توجد مثيلات الآن',
    'cn.br.filter': 'تصفية…', 'cn.br.unavailable': 'غير متاح على هذا الجهاز (مزوّده غير محمَّل)', 'cn.br.selectAll': 'تحديد الكل', 'cn.br.none': 'لا نتائج',
    'cn.br.sel': '{c} عداد × {i} مثيل', 'cn.br.addSel': 'إضافة المحدد ({n})', 'cn.br.byPath': 'إضافة بالمسار الكامل',
    'cn.br.check': 'تحقّق وأضف', 'cn.br.cap': 'يُعرض أول {n} — اكتب في «تصفية» للبقية', 'cn.br.noExplain': 'لا يوجد وصف لهذا العدّاد',
    'cn.br.pathHint': 'مثال: {p} — يُقبل الاسم المحلي أو الإنجليزي، و«*» للمثيلات',
    'cn.added': 'أُضيف {n} عداد', 'cn.exists': 'العدّاد موجود مسبقاً',
    'cn.err.limitLive': 'يتسع الرسم المباشر لـ {n} عداداً على الأكثر', 'cn.err.limit': 'يمكن تسجيل {n} عدادات على الأكثر',
    'cn.err.wildcard': 'التسجيل يحتاج مثيلاً محدداً (مثل _Total) بدل «*»',
    'cn.err.no-object': 'الكائن غير موجود على هذا الجهاز', 'cn.err.no-counter': 'العدّاد غير موجود في هذا الكائن',
    'cn.err.no-instance': 'المثيل غير موجود الآن (هل البرنامج المقصود يعمل؟)', 'cn.err.bad-path': 'صيغة المسار غير صحيحة',
    'cn.err.wild-counter': '«*» مسموحة في اسم المثيل فقط', 'cn.err.remote': 'العدادات المحلية فقط (بدون اسم جهاز آخر)',
    'cn.err.access': 'هذا العدّاد يحتاج صلاحيات المسؤول', 'cn.err.no-data': 'لا توجد بيانات لهذا العدّاد الآن',
    'cn.err.recording': 'أوقف تسجيل هذا العدّاد أولاً', 'cn.err.failed': 'تعذّرت قراءة العدّاد', 'cn.err.bad-range': 'فترة غير صحيحة',
    'cn.adminNote': 'بعض العدادات تحتاج تشغيل البرنامج كمسؤول لقراءتها.',
    'cn.recOn': 'يُسجَّل', 'cn.recOff': 'متوقف', 'cn.recStarted': 'بدأ تسجيل العدّاد', 'cn.recStopped': 'توقف تسجيل العدّاد',
    'cn.recorded': '{n} من {max} مسجّلة',
    'cn.recNote': 'العدادات المسجّلة تُقرأ مرة واحدة مع كل عينة يحفظها البرنامج (كل {s}) وتبقى {d} يوماً؛ وبعد {f} أيام تُختصر إلى متوسط كل دقيقة. يمكن تسجيل {n} عدادات على الأكثر، ولا تكلفة إطلاقاً إن لم تسجّل شيئاً.',
    'cn.hist.title': 'العدادات المسجّلة', 'cn.hist.empty': 'لا توجد عدادات مسجّلة بعد — فعّل «تسجيل» لعدّاد في العرض المباشر',
    'cn.hist.forget': 'حذف السجل', 'cn.hist.forgetQ': 'حذف كل القيم المحفوظة لهذا العدّاد؟', 'cn.hist.deleted': 'حُذف سجل العدّاد',
    'cn.hist.point': 'كل نقطة = متوسط {b}', 'cn.hist.size': 'حجم features.db: {s}', 'cn.refresh': 'تحديث',
    'cn.watch': 'مراقبة في عدادات الأداء',
  });

  Object.assign(I18N.en, {
    'nav.counters': 'Counters',
    'title.counters': 'Performance Monitor (Windows counters)',
    'cn.sub': 'Windows performance counters, as in Performance Monitor — read every second, only while this page is open',
    'cn.subHist': 'The counters you chose to record, kept with the app’s history',
    'cn.tab.live': 'Live', 'cn.tab.history': 'History',
    'cn.add': 'Add counters', 'cn.pause': 'Pause', 'cn.resume': 'Resume', 'cn.paused': 'Paused',
    'cn.clearAll': 'Remove all', 'cn.clearAllQ': 'Remove every counter from the live chart?', 'cn.clearAllBody': 'Recorded counters keep recording; you can stop them under History.',
    'cn.count': '{n} of {max} counters',
    'cn.chartTitle': 'Last 2 minutes', 'cn.chartNote': 'Chart values are multiplied by Scale; the table shows the real values',
    'cn.empty': 'No counters yet — pick one from Quick add below, or press Add counters',
    'cn.col.show': 'Show', 'cn.col.counter': 'Counter', 'cn.col.instance': 'Instance', 'cn.col.last': 'Last', 'cn.col.avg': 'Average',
    'cn.col.min': 'Minimum', 'cn.col.max': 'Maximum', 'cn.col.scale': 'Scale', 'cn.col.record': 'Record', 'cn.col.status': 'Status',
    'cn.auto': 'Auto', 'cn.remove': 'Remove', 'cn.allInst': 'All instances',
    'cn.instCount': '{n} instances', 'cn.more': 'showing the {n} busiest',
    'cn.noRecWild': 'Recording needs one specific instance, not “*”',
    'cn.presets': 'Quick add', 'cn.presetsHint': 'Common counters by their English names, so they work on any Windows language',
    'cn.g.cpu': 'Processor', 'cn.g.mem': 'Memory', 'cn.g.disk': 'Disk', 'cn.g.net': 'Network', 'cn.g.sys': 'System',
    'cn.p.cpu': '% Processor Time (_Total)', 'cn.p.queue': 'Processor Queue Length', 'cn.p.ctx': 'Context Switches/sec',
    'cn.p.avail': 'Available MBytes', 'cn.p.pages': 'Pages/sec', 'cn.p.committed': 'Committed Bytes',
    'cn.p.commitPct': '% Committed Bytes In Use', 'cn.p.pagefile': 'Paging File % Usage',
    'cn.p.diskTime': '% Disk Time', 'cn.p.diskQueue': 'Avg. Disk Queue Length', 'cn.p.diskBytes': 'Disk Bytes/sec',
    'cn.p.netBytes': 'Network Bytes Total/sec (each adapter)', 'cn.p.handles': 'Handle Count (all processes)', 'cn.p.threads': 'System Threads',
    'cn.br.title': 'Add counters', 'cn.br.search': 'Search objects and counters…', 'cn.br.refresh': 'Refresh list', 'cn.br.close': 'Close',
    'cn.br.loading': 'Reading the counter list from Windows…', 'cn.br.indexing': 'Preparing search across counter names…',
    'cn.br.objects': 'Objects', 'cn.br.counters': 'Counters', 'cn.br.instances': 'Instances',
    'cn.br.pickObject': 'Pick an object from the list', 'cn.br.single': 'This object has no instances', 'cn.br.noInst': 'No instances right now',
    'cn.br.filter': 'Filter…', 'cn.br.unavailable': 'not available on this PC (its provider is not loaded)', 'cn.br.selectAll': 'Select all', 'cn.br.none': 'No matches',
    'cn.br.sel': '{c} counter(s) × {i} instance(s)', 'cn.br.addSel': 'Add selected ({n})', 'cn.br.byPath': 'Add by full path',
    'cn.br.check': 'Check and add', 'cn.br.cap': 'Showing the first {n} — type in Filter for the rest', 'cn.br.noExplain': 'No description for this counter',
    'cn.br.pathHint': 'Example: {p} — localized or English names, “*” for instances',
    'cn.added': 'Added {n} counter(s)', 'cn.exists': 'Already on the chart',
    'cn.err.limitLive': 'The live chart holds up to {n} counters', 'cn.err.limit': 'Up to {n} counters can be recorded',
    'cn.err.wildcard': 'Recording needs a specific instance (such as _Total), not “*”',
    'cn.err.no-object': 'This object does not exist on this PC', 'cn.err.no-counter': 'This object has no such counter',
    'cn.err.no-instance': 'No such instance right now (is that program running?)', 'cn.err.bad-path': 'The path is not valid',
    'cn.err.wild-counter': '“*” is allowed only in the instance name', 'cn.err.remote': 'Local counters only (no other computer name)',
    'cn.err.access': 'This counter needs administrator rights', 'cn.err.no-data': 'This counter has no data right now',
    'cn.err.recording': 'Stop recording this counter first', 'cn.err.failed': 'Could not read the counter', 'cn.err.bad-range': 'Invalid period',
    'cn.adminNote': 'Some counters can only be read when the app runs as administrator.',
    'cn.recOn': 'Recording', 'cn.recOff': 'Stopped', 'cn.recStarted': 'Recording started', 'cn.recStopped': 'Recording stopped',
    'cn.recorded': '{n} of {max} recorded',
    'cn.recNote': 'Recorded counters are read once with every sample the app stores (every {s}) and kept {d} days; after {f} days they are reduced to 1-minute averages. Up to {n} counters, and no cost at all when nothing is recorded.',
    'cn.hist.title': 'Recorded counters', 'cn.hist.empty': 'Nothing recorded yet — turn on Record for a counter in the Live view',
    'cn.hist.forget': 'Delete history', 'cn.hist.forgetQ': 'Delete every stored value of this counter?', 'cn.hist.deleted': 'Counter history deleted',
    'cn.hist.point': 'Each point = average of {b}', 'cn.hist.size': 'features.db size: {s}', 'cn.refresh': 'Refresh',
    'cn.watch': 'Watch in Performance counters',
  });

  // 16 mid-tone hues that read on both themes; further wildcard series reuse them in order.
  const PALETTE = ['#6366f1', '#06b6d4', '#f43f5e', '#f59e0b', '#10b981', '#a855f7', '#3b82f6', '#f97316',
    '#84cc16', '#ec4899', '#14b8a6', '#eab308', '#8b5cf6', '#ef4444', '#0ea5e9', '#78716c'];
  const SCALES = [0, 1e-6, 1e-5, 1e-4, 1e-3, 0.01, 0.1, 1, 10, 100, 1000];
  const GROUPS = ['cpu', 'mem', 'disk', 'net', 'sys'];
  const PROC_COUNTERS = ['% Processor Time', 'Working Set - Private', 'IO Data Bytes/sec'];
  const X_SVG = '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6 6 18" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"/></svg>';
  const SEARCH_SVG = '<svg viewBox="0 0 24 24"><circle cx="11" cy="11" r="7" fill="none" stroke="currentColor" stroke-width="2"/><path d="m20 20-3.5-3.5" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>';

  const S = {
    tab: 'live', data: null, paused: false, timer: null, histTimer: null, busy: false, sig: '', loadSeq: 0,
    presets: null, browserOpen: false, histRange: '1h', hist: null, histSig: '', histHidden: new Set(), histScale: {},
    explains: new Map(),
    br: { objects: null, index: null, unavailable: new Set(), indexing: false, loading: false, error: null, obj: null, items: null, selC: new Set(), selI: new Set(), q: '', iq: '', focus: null },
  };

  /* ---------------- helpers ---------------- */
  /** counter / instance names are mostly Latin with leading symbols ("% Processor Time", "_Total"): isolate them in RTL */
  const bdi = (s) => `<bdi>${esc(s)}</bdi>`;
  const errText = (code, n) => t(`cn.err.${code || 'failed'}`, { n });
  const keyOf = (path) => path.toLowerCase();
  const makePath = (o, i, c) => (i == null ? `\\${o}\\${c}` : `\\${o}(${i})\\${c}`);
  const toItem = (i) => ({ path: i.path, english: i.english, scale: i.scale || 0, hidden: !!i.hidden, off: i.off || [] });
  const historyShown = () => shown(ID, 'history');

  function fmtV(v) {
    if (v == null || isNaN(v)) return '—';
    const a = Math.abs(v);
    if (a !== 0 && a < 1) return new Intl.NumberFormat(locale(), { maximumSignificantDigits: 3 }).format(v);
    return fmtNum(v, a >= 1000 ? 0 : a >= 100 ? 1 : 2);
  }
  function fmtScale(s) { return s >= 1 ? String(s) : s.toFixed(Math.round(-Math.log10(s))); }
  /** perfmon-style automatic scale: the largest power of ten that keeps the series under 100 */
  function autoScale(max) {
    if (!(max > 0)) return 1;
    return 10 ** Math.max(-12, Math.min(6, Math.floor(Math.log10(100 / max) + 1e-9)));
  }
  /** Scale picker; the Auto entry names the factor it currently applies ("Auto ×0.01"). */
  function scaleSelect(attr, value, autoId) {
    return `<select class="cn-scale" ${attr} aria-label="${esc(t('cn.col.scale'))}">${SCALES.map((s) => `<option value="${s}" ${s ? '' : `id="${autoId}"`} ${s === value ? 'selected' : ''}>${s ? fmtScale(s) : t('cn.auto')}</option>`).join('')}</select>`;
  }
  function autoLabel(id, eff) {
    const el = document.getElementById(id);
    const text = `${t('cn.auto')} \u2066×${fmtScale(eff)}\u2069`; // isolate keeps "×0.01" in order inside Arabic text
    if (el && el.textContent !== text) el.textContent = text;
  }
  function statsOf(values) {
    let n = 0, sum = 0, min = null, max = null;
    for (const v of values) {
      if (v == null) continue;
      n++; sum += v;
      if (min == null || v < min) min = v;
      if (max == null || v > max) max = v;
    }
    return { avg: n ? sum / n : null, min, max };
  }
  /** Single counters take the first colors so a busy wildcard never shifts them. */
  function flatten(d) {
    const rows = [];
    let ci = 0;
    const push = (it, ii) => {
      for (const s of it.series) rows.push({ it, ii, s, color: PALETTE[ci++ % PALETTE.length], on: !it.hidden && !(it.off || []).includes(s.name) });
    };
    d.items.forEach((it, ii) => { if (!it.wildcard) push(it, ii); });
    d.items.forEach((it, ii) => { if (it.wildcard) push(it, ii); });
    rows.forEach((r, i) => { r.idx = i; });
    return rows;
  }
  function seriesName(it, name) {
    const inst = name || it.inst;
    return inst ? `${it.counter} (${inst})` : it.counter || it.path;
  }
  function adminNote(show) {
    const el = $('#cnAdminNote');
    if (!el) return;
    const need = show && !state.init.isAdmin;
    el.classList.toggle('hidden', !need);
    if (need && !el.innerHTML) {
      el.innerHTML = `<span>${t('cn.adminNote')}</span><button class="btn" data-admin>${t('set.restartAdmin')}</button>`;
    }
  }
  document.addEventListener('click', (e) => { if (e.target.closest(`#page-${ID} [data-admin]`)) call('restartAsAdmin'); });

  function lineOption({ times, from, to, series, fmtX }) {
    const th = chartTheme();
    return {
      animation: false,
      textStyle: { fontFamily: th.font },
      grid: { left: 8, right: 16, top: 12, bottom: 6, containLabel: true },
      tooltip: {
        trigger: 'axis', confine: true, transitionDuration: 0,
        backgroundColor: th.surface, borderColor: th.border, borderWidth: 1, padding: [8, 12],
        textStyle: { color: th.text, fontFamily: th.font, fontSize: 12 },
        axisPointer: { type: 'line', lineStyle: { color: th.muted, width: 1, type: 'dashed' } },
        formatter: (ps) => {
          if (!ps.length) return '';
          let h = `<div style="color:${th.muted};margin-bottom:4px">${esc(fmtFull(ps[0].value[0], true))}</div>`;
          for (const p of ps) {
            if (p.value[2] == null) continue;
            h += `<div style="display:flex;gap:14px;justify-content:space-between;align-items:center"><span><span style="display:inline-block;width:8px;height:8px;border-radius:50%;background:${p.color};margin-inline-end:6px"></span><bdi>${esc(p.seriesName)}</bdi></span><b style="direction:ltr">${fmtV(p.value[2])}</b></div>`;
          }
          return h;
        },
      },
      xAxis: {
        type: 'time', min: from, max: to, boundaryGap: false,
        axisLine: { lineStyle: { color: th.grid } }, axisTick: { show: false }, splitLine: { show: false },
        axisLabel: { color: th.muted, fontSize: 11, hideOverlap: true, formatter: fmtX },
      },
      yAxis: {
        type: 'value', min: 0, max: (v) => Math.max(100, Math.ceil(v.max / 20) * 20), splitNumber: 5,
        splitLine: { lineStyle: { color: th.grid } }, axisLabel: { color: th.muted, fontSize: 11 },
      },
      series: series.map((s) => ({
        type: 'line', name: s.name, showSymbol: false, symbol: 'circle', symbolSize: 5, connectNulls: false,
        data: times(s), lineStyle: { width: 1.7, color: s.color }, itemStyle: { color: s.color }, emphasis: { disabled: true },
      })),
      graphic: [],
    };
  }

  /* ---------------- shell ---------------- */
  function buildShell() {
    disposeCharts('cn-');
    S.sig = ''; S.histSig = '';
    const page = $(`#page-${ID}`);
    if (!historyShown()) S.tab = 'live';
    const tabs = `<div class="seg" id="cnTabs"><button data-v="live" class="${S.tab === 'live' ? 'active' : ''}">${t('cn.tab.live')}</button>${historyShown() ? `<button data-v="history" class="${S.tab === 'history' ? 'active' : ''}">${t('cn.tab.history')}</button>` : ''}</div>`;
    if (S.tab === 'live') {
      page.innerHTML = `
        <div class="toolbar">
          ${tabs}
          <div class="cn-actions">
            <span class="toolbar-note" id="cnCount"></span>
            <span class="badge warn hidden" id="cnPausedBadge">${t('cn.paused')}</span>
            <button class="btn danger" id="cnClear">${t('cn.clearAll')}</button>
            <button class="btn" id="cnPause">${S.paused ? t('cn.resume') : t('cn.pause')}</button>
            <button class="btn primary" id="cnAddBtn"><svg viewBox="0 0 24 24"><path d="M12 5v14M5 12h14" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"/></svg>${t('cn.add')}</button>
          </div>
        </div>
        <div class="card cn-browser hidden" id="cnBrowser"></div>
        <div class="note warn cn-msg hidden" id="cnAdminNote"></div>
        <div class="card">
          <div class="card-head"><h3>${t('cn.chartTitle')}</h3><span class="cn-head-note">${t('cn.chartNote')}</span></div>
          <div class="cn-chart" id="cn-live"></div>
        </div>
        <div class="card flush"><div class="table-wrap cn-table-wrap" id="cnTable"></div></div>
        ${shown(ID, 'presets') ? `<div class="card">
          <div class="card-head"><h3>${t('cn.presets')}</h3><span class="cn-head-note">${t('cn.presetsHint')}</span></div>
          <div class="cn-presets" id="cnPresets"></div>
        </div>` : ''}`;
      $('#cnPause').addEventListener('click', togglePause);
      $('#cnAddBtn').addEventListener('click', () => (S.browserOpen ? closeBrowser() : openBrowser()));
      $('#cnClear').addEventListener('click', clearAll);
      bindLiveTable();
      $('#cnPresets')?.addEventListener('click', (e) => { const b = e.target.closest('[data-preset]'); if (b && !b.disabled) addPreset(b.dataset.preset, b); });
      if (S.browserOpen) openBrowser();
    } else {
      page.innerHTML = `
        <div class="toolbar">
          ${tabs}
          <div class="cn-actions">
            <span class="toolbar-note" id="cnRecCount"></span>
            <div class="seg" id="cnRange">${['1h', '24h', '7d'].map((r) => `<button data-r="${r}" class="${S.histRange === r ? 'active' : ''}">${t(`range.${r}`)}</button>`).join('')}</div>
            <button class="btn" id="cnHistRefresh">${t('cn.refresh')}</button>
          </div>
        </div>
        <div class="card">
          <div class="card-head"><h3>${t('cn.hist.title')}</h3><span class="cn-head-note" id="cnHistPoint"></span></div>
          <div class="cn-chart" id="cn-hist"></div>
        </div>
        <div class="card flush"><div class="table-wrap cn-table-wrap" id="cnHistTable"></div></div>
        <div class="note" id="cnHistNote"></div>`;
      $$('#cnRange button').forEach((b) => b.addEventListener('click', () => { S.histRange = b.dataset.r; $$('#cnRange button').forEach((x) => x.classList.toggle('active', x === b)); loadHistory(); }));
      $('#cnHistRefresh').addEventListener('click', loadHistory);
      bindHistTable();
    }
    $$('#cnTabs button').forEach((b) => b.addEventListener('click', () => { if (S.tab !== b.dataset.v) { S.tab = b.dataset.v; load(); } }));
    $('#pageSub').textContent = subtitle();
  }

  /* ---------------- live ---------------- */
  async function refreshLive() {
    S.data = await call('counters.live');
    renderLive();
  }

  async function tickLive() {
    if (S.paused || S.busy || document.hidden || state.page !== ID || S.tab !== 'live') return;
    S.busy = true;
    try { await refreshLive(); } catch (e) { console.error(e); } finally { S.busy = false; }
  }

  function togglePause() {
    S.paused = !S.paused;
    $('#cnPause').textContent = S.paused ? t('cn.resume') : t('cn.pause');
    $('#cnPausedBadge').classList.toggle('hidden', !S.paused);
    if (!S.paused) tickLive();
  }

  function renderLive() {
    const d = S.data;
    if (!d || !$('#cnTable')) return;
    const rows = flatten(d);
    $('#cnCount').innerHTML = t('cn.count', { n: N(d.items.length), max: N(d.maxLive) });
    $('#cnPausedBadge').classList.toggle('hidden', !S.paused);
    $('#cnClear').disabled = !d.items.length;
    adminNote(d.items.some((i) => i.error === 'access'));
    // rebuild the table only when its rows change, so open dropdowns and clicks survive the 1-second refresh
    const sig = lang() + JSON.stringify(d.items.map((i) => [i.key, i.hidden, i.scale, i.record, i.error, i.off, i.series.map((s) => s.name)]));
    if (sig !== S.sig) { S.sig = sig; $('#cnTable').innerHTML = liveTable(d, rows); }
    for (const r of rows) for (const k of ['last', 'avg', 'min', 'max']) {
      const el = document.getElementById(`cn-v-${r.idx}-${k}`);
      if (el) el.textContent = fmtV(r.s[k]);
    }
    d.items.forEach((it, ii) => {
      const el = document.getElementById(`cn-n-${ii}`);
      if (el) el.innerHTML = t('cn.instCount', { n: N(it.series.length + it.more) }) + (it.more ? ` · ${t('cn.more', { n: N(it.series.length) })}` : '');
    });
    const eff = d.items.map((it, ii) => it.scale || autoScale(Math.max(0, ...rows.filter((r) => r.ii === ii && r.on).map((r) => r.s.max ?? 0))));
    d.items.forEach((it, ii) => autoLabel(`cn-auto-${ii}`, eff[ii]));
    drawLive(d, rows, eff);
    renderPresets();
  }

  function drawLive(d, rows, eff) {
    if (!$('#cn-live')) return;
    const times = d.times;
    const to = times.length ? times[times.length - 1] : Date.now();
    const vis = rows.filter((r) => r.on);
    const opt = lineOption({
      from: to - 120e3, to,
      series: vis.map((r) => ({ name: seriesName(r.it, r.s.name), color: r.color, r })),
      times: (s) => times.map((ts, i) => { const v = s.r.s.values[i]; return [ts, v == null ? null : v * eff[s.r.ii], v]; }),
      fmtX: (v) => fmtClock(v),
    });
    if (!d.items.length) opt.graphic = [{ type: 'text', left: 'center', top: 'middle', style: { text: t('cn.empty'), fill: cssVar('--muted'), font: `13px ${cssVar('--font')}`, width: 420, overflow: 'break', align: 'center' } }];
    getChart('cn-live').setOption(opt, true);
  }

  function liveTable(d, rows) {
    if (!d.items.length) return `<div class="empty">${t('cn.empty')}</div>`;
    const statCells = (r) => ['last', 'avg', 'min', 'max'].map((k) => `<td class="r num" id="cn-v-${r.idx}-${k}">—</td>`).join('');
    const dot = (c) => `<span class="cn-dot" style="--dc:${c}"></span>`;
    let h = `<table class="cn-table"><thead><tr>
      <th>${t('cn.col.show')}</th><th>${t('cn.col.counter')}</th><th>${t('cn.col.instance')}</th>
      <th class="r">${t('cn.col.last')}</th><th class="r">${t('cn.col.avg')}</th><th class="r">${t('cn.col.min')}</th><th class="r">${t('cn.col.max')}</th>
      <th>${t('cn.col.scale')}</th><th class="c">${t('cn.col.record')}</th><th></th></tr></thead><tbody>`;
    d.items.forEach((it, ii) => {
      const mine = rows.filter((r) => r.ii === ii);
      const name = `<div class="cn-name"><b title="${esc(it.path)}">${bdi(it.counter || it.path)}</b><span title="${esc(it.path)}">${bdi(it.obj || '')}</span></div>`;
      const scale = scaleSelect(`data-scale="${ii}"`, it.scale || 0, `cn-auto-${ii}`);
      const rec = it.wildcard
        ? `<label class="switch" title="${esc(t('cn.noRecWild'))}"><input type="checkbox" disabled><i></i></label>`
        : `<label class="switch" title="${esc(t('cn.col.record'))}"><input type="checkbox" data-rec="${ii}" ${it.record ? 'checked' : ''}><i></i></label>`;
      const del = `<button class="mini-btn cn-icon" data-del="${ii}" title="${esc(t('cn.remove'))}" aria-label="${esc(t('cn.remove'))}">${X_SVG}</button>`;
      const show = (attrs, checked, colors, disabled) => `<label class="cn-show"><input type="checkbox" ${attrs} ${checked ? 'checked' : ''} ${disabled ? 'disabled' : ''}>${colors}</label>`;
      if (it.error) {
        h += `<tr class="cn-bad"><td>${show('disabled', false, dot('var(--muted)'), true)}</td><td>${name}</td><td class="cn-inst">${bdi(it.inst ?? '—')}</td>
          <td colspan="5"><span class="cn-err" title="${esc(it.detail || '')}">${esc(errText(it.error))}</span></td><td class="c">${rec}</td><td>${del}</td></tr>`;
      } else if (!it.wildcard) {
        const r = mine[0];
        h += `<tr><td>${show(`data-hide="${ii}"`, !it.hidden, dot(r ? r.color : 'var(--muted)'))}</td><td>${name}</td>
          <td class="cn-inst" title="${esc(it.inst || '')}">${bdi(it.inst ?? '—')}</td>${r ? statCells(r) : '<td colspan="4"></td>'}
          <td>${scale}</td><td class="c">${rec}</td><td>${del}</td></tr>`;
      } else {
        const dots = mine.slice(0, 3).map((r) => dot(r.color)).join('') || dot('var(--muted)');
        h += `<tr class="cn-group"><td>${show(`data-hide="${ii}"`, !it.hidden, `<span class="cn-dots">${dots}</span>`)}</td><td>${name}</td>
          <td class="cn-inst"><b>${bdi(it.inst)}</b><span class="muted cn-inst-n" id="cn-n-${ii}"></span></td>
          <td colspan="4"></td><td>${scale}</td><td class="c">${rec}</td><td>${del}</td></tr>`;
        for (const r of mine) {
          h += `<tr class="cn-sub"><td>${show(`data-off="${ii}" data-name="${esc(r.s.name)}"`, !(it.off || []).includes(r.s.name), dot(r.color), it.hidden)}</td><td></td>
            <td class="cn-inst" title="${esc(r.s.name)}">${bdi(r.s.name)}</td>${statCells(r)}<td></td><td></td><td></td></tr>`;
        }
      }
    });
    return `${h}</tbody></table>`;
  }

  function bindLiveTable() {
    const host = $('#cnTable');
    host.addEventListener('change', async (e) => {
      const el = e.target;
      const d = S.data;
      if (!d) return;
      const items = d.items.map(toItem);
      if (el.dataset.hide != null) {
        items[+el.dataset.hide].hidden = !el.checked;
        await saveLive(items);
      } else if (el.dataset.off != null) {
        const it = items[+el.dataset.off];
        const off = new Set(it.off);
        if (el.checked) off.delete(el.dataset.name); else off.add(el.dataset.name);
        it.off = [...off];
        await saveLive(items);
      } else if (el.dataset.scale != null) {
        items[+el.dataset.scale].scale = +el.value;
        await saveLive(items);
      } else if (el.dataset.rec != null) {
        el.disabled = true;
        const ok = await setRecord(d.items[+el.dataset.rec], el.checked);
        if (!ok) el.checked = !el.checked;
        el.disabled = false;
        await refreshLive();
      }
    });
    host.addEventListener('click', async (e) => {
      const b = e.target.closest('[data-del]');
      if (!b || !S.data) return;
      const items = S.data.items.map(toItem);
      items.splice(+b.dataset.del, 1);
      await saveLive(items);
    });
  }

  /** Sends the whole live list; the server keeps history for counters that stay. */
  async function saveLive(items) {
    const res = await call('counters.setLive', { items });
    if (!res.ok) { toast(res.error === 'limit' ? errText('limitLive', S.data?.maxLive ?? 16) : errText(res.error), 4000); return false; }
    await refreshLive();
    return true;
  }

  async function addCounters(list) {
    const d = S.data || await call('counters.live');
    const items = d.items.map(toItem);
    const have = new Set(items.map((i) => keyOf(i.path)));
    const fresh = [];
    for (const x of list) if (!have.has(keyOf(x.path))) { have.add(keyOf(x.path)); fresh.push(x); }
    if (!fresh.length) { toast(t('cn.exists')); return false; }
    if (items.length + fresh.length > d.maxLive) { toast(errText('limitLive', d.maxLive), 4000); return false; }
    const ok = await saveLive([...items, ...fresh.map((x) => ({ path: x.path, english: !!x.english, scale: 0, hidden: false, off: [] }))]);
    if (ok) toast(t('cn.added', { n: fresh.length }));
    return ok;
  }

  async function addPreset(id, btn) {
    const p = (S.presets || []).find((x) => x.id === id);
    if (!p) return;
    btn.disabled = true;
    try {
      const v = await call('counters.validate', { path: p.path, english: true });
      if (!v.ok) { toast(`${t(`cn.p.${id}`)}: ${errText(v.error)}`, 4500); adminNote(v.error === 'access'); return; }
      await addCounters([{ path: p.path, english: true }]);
    } finally { btn.disabled = false; renderPresets(); }
  }

  async function setRecord(it, on) {
    const d = S.data;
    let rec = d.recorded.map((r) => ({ path: r.path, english: r.english }));
    if (on) {
      if (rec.length >= d.maxRecorded) { toast(errText('limit', d.maxRecorded), 4500); return false; }
      rec.push({ path: it.path, english: it.english });
    } else rec = rec.filter((r) => keyOf(r.path) !== keyOf(it.path));
    const res = await call('counters.setRecorded', { items: rec });
    if (!res.ok) { toast(res.error === 'limit' ? errText('limit', d.maxRecorded) : errText(res.error), 4500); adminNote(res.error === 'access'); return false; }
    toast(t(on ? 'cn.recStarted' : 'cn.recStopped'));
    return true;
  }

  async function clearAll() {
    if (!S.data?.items.length) return;
    const ok = await confirmDialog(t('cn.clearAllQ'), t('cn.clearAllBody'), t('cn.clearAll'));
    if (ok) await saveLive([]);
  }

  function renderPresets() {
    const host = $('#cnPresets');
    if (!host || !S.presets) return;
    const have = new Set((S.data?.items || []).map((i) => i.key));
    const html = GROUPS.map((g) => `<div class="cn-pgroup"><h4>${t(`cn.g.${g}`)}</h4>${S.presets.filter((p) => p.group === g).map((p) => {
      const on = have.has(keyOf(p.path));
      return `<button class="cn-chip ${on ? 'on' : ''}" data-preset="${p.id}" ${on ? 'disabled' : ''} title="${esc(p.path)}"><span>${esc(t(`cn.p.${p.id}`))}</span><i>${on ? '✓' : '+'}</i></button>`;
    }).join('')}</div>`).join('');
    if (host.dataset.sig !== html) { host.innerHTML = html; host.dataset.sig = html; }
  }

  /* ---------------- counter browser ---------------- */
  async function openBrowser() {
    S.browserOpen = true;
    const box = $('#cnBrowser');
    if (!box) return;
    box.classList.remove('hidden');
    box.innerHTML = `
      <div class="card-head"><h3>${t('cn.br.title')}</h3><button class="icon-btn" id="cnBrClose" title="${esc(t('cn.br.close'))}" aria-label="${esc(t('cn.br.close'))}">${X_SVG}</button></div>
      <div class="cn-br-top">
        <div class="search">${SEARCH_SVG}<input id="cnBrSearch" placeholder="${esc(t('cn.br.search'))}" value="${esc(S.br.q)}"></div>
        <button class="btn" id="cnBrRefresh">${t('cn.br.refresh')}</button>
        <span class="toolbar-note" id="cnBrStatus"></span>
      </div>
      <div class="cn-br-cols">
        <div class="cn-col"><div class="cn-col-head"><span>${t('cn.br.objects')} <span class="num muted" id="cnObjN"></span></span></div><div class="cn-list" id="cnObjList"></div></div>
        <div class="cn-col"><div class="cn-col-head"><span>${t('cn.br.counters')}</span><button class="link" id="cnSelAll">${t('cn.br.selectAll')}</button></div><div class="cn-list" id="cnCntList"></div></div>
        <div class="cn-col"><div class="cn-col-head"><span>${t('cn.br.instances')}</span><input class="input cn-filter" id="cnInstFilter" placeholder="${esc(t('cn.br.filter'))}" value="${esc(S.br.iq)}"></div><div class="cn-list" id="cnInstList"></div></div>
      </div>
      <div class="cn-explain" id="cnExplain"></div>
      <div class="cn-br-foot"><span class="muted" id="cnSelInfo"></span><button class="btn primary" id="cnAddSel" disabled></button></div>
      <div class="cn-path-row">
        <label for="cnPath">${t('cn.br.byPath')}</label>
        <input class="input" id="cnPath" dir="ltr" spellcheck="false" placeholder="\\Processor(_Total)\\% Processor Time">
        <button class="btn" id="cnPathAdd">${t('cn.br.check')}</button>
      </div>
      <div class="cn-path-hint muted">${esc(t('cn.br.pathHint')).replace('{p}', '<bdi dir="ltr" class="cn-code">\\Processor(_Total)\\% Processor Time</bdi>')}</div>
      <div id="cnPathMsg"></div>`;
    $('#cnBrClose').addEventListener('click', closeBrowser);
    $('#cnBrRefresh').addEventListener('click', () => loadObjects(true));
    let qTimer;
    $('#cnBrSearch').addEventListener('input', (e) => {
      S.br.q = e.target.value;
      clearTimeout(qTimer);
      qTimer = setTimeout(() => { if (S.br.q.trim().length >= 2) ensureIndex(); renderBr(); }, 150);
    });
    $('#cnInstFilter').addEventListener('input', (e) => { S.br.iq = e.target.value; renderInstances(); });
    $('#cnObjList').addEventListener('click', (e) => { const b = e.target.closest('[data-obj]'); if (b) selectObject(b.dataset.obj); });
    $('#cnCntList').addEventListener('change', (e) => {
      const c = e.target.dataset.c;
      if (c == null) return;
      if (e.target.checked) S.br.selC.add(c); else S.br.selC.delete(c);
      renderSelInfo();
    });
    $('#cnCntList').addEventListener('click', (e) => { const l = e.target.closest('[data-focus]'); if (l) focusCounter(l.dataset.focus); });
    $('#cnInstList').addEventListener('change', (e) => {
      const i = e.target.dataset.i;
      if (i == null) return;
      if (e.target.checked) {
        // "*" means every instance: it excludes picking single ones
        if (i === '*') S.br.selI.clear(); else S.br.selI.delete('*');
        S.br.selI.add(i);
      } else S.br.selI.delete(i);
      renderInstances();
      renderSelInfo();
    });
    $('#cnSelAll').addEventListener('click', () => {
      const list = visibleCounters();
      const all = list.every((c) => S.br.selC.has(c));
      for (const c of list) if (all) S.br.selC.delete(c); else S.br.selC.add(c);
      renderCounters();
      renderSelInfo();
    });
    $('#cnAddSel').addEventListener('click', addSelection);
    $('#cnPathAdd').addEventListener('click', addByPath);
    $('#cnPath').addEventListener('keydown', (e) => { if (e.key === 'Enter') addByPath(); });
    renderBr();
    if (!S.br.objects && !S.br.loading) await loadObjects(false);
    else if (S.br.obj && !S.br.items) selectObject(S.br.obj);
  }

  function closeBrowser() {
    S.browserOpen = false;
    const box = $('#cnBrowser');
    if (box) { box.classList.add('hidden'); box.innerHTML = ''; }
  }

  async function askObjects(params) {
    // the server waits up to ~12 s per call; enumeration normally finishes well inside the first one
    for (let i = 0; i < 6; i++) {
      const r = await call('counters.objects', i ? { ...params, refresh: false } : params);
      if (r.ready && (!params.deep || r.indexReady !== false)) return r;
    }
    return null;
  }

  async function loadObjects(refresh) {
    S.br.loading = true; S.br.error = null;
    if (refresh) { S.br.index = null; S.br.unavailable = new Set(); S.br.items = null; S.explains.clear(); }
    renderBr();
    try {
      const r = await askObjects({ refresh });
      if (!r) S.br.error = errText('failed');
      else {
        S.br.objects = r.objects;
        if (!r.ok) S.br.error = `${errText(r.error)}${r.detail ? ` (${r.detail})` : ''}`;
      }
    } catch (e) { S.br.error = e.message; } finally { S.br.loading = false; }
    renderBr();
    ensureIndex(); // also tells which listed objects cannot be read here
    if (S.br.obj) selectObject(S.br.obj, true);
  }

  async function ensureIndex() {
    if (S.br.index || S.br.indexing || !S.br.objects) return;
    S.br.indexing = true;
    renderStatus();
    try { const r = await askObjects({ deep: true }); if (r?.index) { S.br.index = r.index; S.br.unavailable = new Set(r.unavailable || []); } } catch (e) { console.error(e); }
    S.br.indexing = false;
    renderBr();
  }

  async function selectObject(name, keep) {
    if (!keep) { S.br.selC.clear(); S.br.selI.clear(); S.br.focus = null; S.br.iq = ''; const f = $('#cnInstFilter'); if (f) f.value = ''; }
    S.br.obj = name; S.br.items = null;
    renderBr();
    let r;
    try { r = await call('counters.items', { object: name }); } catch (e) { r = { ok: false, error: 'failed', detail: e.message }; }
    if (S.br.obj !== name) return;
    S.br.items = r;
    if (r.ok && !keep && !r.single && r.instances?.includes('_Total')) S.br.selI.add('_Total');
    renderBr();
  }

  function visibleObjects() {
    const q = S.br.q.trim().toLowerCase();
    const objs = S.br.objects || [];
    if (!q) return objs.map((o) => ({ o, hits: 0 }));
    const out = [];
    for (const o of objs) {
      const hits = S.br.index?.[o]?.reduce((n, c) => n + (c.toLowerCase().includes(q) ? 1 : 0), 0) || 0;
      if (hits || o.toLowerCase().includes(q)) out.push({ o, hits });
    }
    return out;
  }
  function visibleCounters() {
    const it = S.br.items;
    if (!it?.ok) return [];
    const q = S.br.q.trim().toLowerCase();
    if (!q || S.br.obj.toLowerCase().includes(q)) return it.counters;
    const m = it.counters.filter((c) => c.toLowerCase().includes(q));
    return m.length ? m : it.counters;
  }

  function renderStatus() {
    const el = $('#cnBrStatus');
    if (!el) return;
    el.classList.toggle('cn-warn', !!S.br.error);
    el.textContent = S.br.loading ? t('cn.br.loading') : S.br.error ? S.br.error : S.br.indexing ? t('cn.br.indexing') : '';
  }

  function renderBr() {
    if (!$('#cnObjList')) return;
    renderStatus();
    const objs = visibleObjects();
    $('#cnObjN').textContent = S.br.objects ? `(${objs.length})` : '';
    $('#cnObjList').innerHTML = S.br.loading && !S.br.objects ? '<div class="skeleton cn-skel"></div>'
      : objs.length ? objs.map(({ o, hits }) => `<button class="cn-li ${o === S.br.obj ? 'active' : ''} ${S.br.unavailable.has(o) ? 'off' : ''}" data-obj="${esc(o)}" title="${esc(S.br.unavailable.has(o) ? `${o} — ${t('cn.br.unavailable')}` : o)}"><span>${bdi(o)}</span>${hits ? `<small class="num">${hits}</small>` : ''}</button>`).join('')
        : `<div class="cn-empty">${t('cn.br.none')}</div>`;
    renderCounters();
    renderInstances();
    renderExplain();
    renderSelInfo();
  }

  function renderCounters() {
    const host = $('#cnCntList');
    const it = S.br.items;
    if (!S.br.obj) { host.innerHTML = `<div class="cn-empty">${t('cn.br.pickObject')}</div>`; return; }
    if (!it) { host.innerHTML = '<div class="skeleton cn-skel"></div>'; return; }
    if (!it.ok) { host.innerHTML = `<div class="cn-empty cn-warn">${esc(errText(it.error))}</div>`; return; }
    const list = visibleCounters();
    host.innerHTML = list.length ? list.map((c) => `<label class="cn-li ${S.br.focus === c ? 'active' : ''}" data-focus="${esc(c)}" title="${esc(c)}"><input type="checkbox" data-c="${esc(c)}" ${S.br.selC.has(c) ? 'checked' : ''}><span>${bdi(c)}</span></label>`).join('')
      : `<div class="cn-empty">${t('cn.br.none')}</div>`;
  }

  function renderInstances() {
    const host = $('#cnInstList');
    const filter = $('#cnInstFilter');
    const it = S.br.items;
    const multi = it?.ok && !it.single;
    filter.classList.toggle('hidden', !(multi && (it.instances.length > 12 || S.br.iq)));
    if (!S.br.obj || !it) { host.innerHTML = it === null && S.br.obj ? '<div class="skeleton cn-skel"></div>' : ''; return; }
    if (!it.ok) { host.innerHTML = ''; return; }
    if (it.single) { host.innerHTML = `<div class="cn-empty">${t('cn.br.single')}</div>`; return; }
    const q = S.br.iq.trim().toLowerCase();
    const all = q ? it.instances.filter((i) => i.toLowerCase().includes(q)) : it.instances;
    const CAP = 1500;
    const opt = (v, label) => `<label class="cn-li" title="${esc(v)}"><input type="checkbox" data-i="${esc(v)}" ${S.br.selI.has(v) ? 'checked' : ''}><span>${label}</span></label>`;
    host.innerHTML = opt('*', `${esc(t('cn.allInst'))} <span class="num muted">(*)</span>`)
      + (it.instances.length ? all.slice(0, CAP).map((i) => opt(i, bdi(i))).join('') : `<div class="cn-empty">${t('cn.br.noInst')}</div>`)
      + (all.length > CAP || it.truncated ? `<div class="cn-empty">${t('cn.br.cap', { n: N(Math.min(CAP, all.length)) })}</div>` : '');
  }

  function selection() {
    const it = S.br.items;
    if (!it?.ok) return [];
    const insts = it.single ? [null] : [...S.br.selI];
    const out = [];
    for (const c of it.counters) if (S.br.selC.has(c)) for (const i of insts) out.push({ path: makePath(S.br.obj, i, c), english: false });
    return out;
  }

  function renderSelInfo() {
    const it = S.br.items;
    const sel = selection();
    const info = $('#cnSelInfo');
    info.innerHTML = it?.ok ? t('cn.br.sel', { c: N(S.br.selC.size), i: N(it.single ? 1 : S.br.selI.size) }) : '';
    const b = $('#cnAddSel');
    b.textContent = t('cn.br.addSel', { n: sel.length });
    b.disabled = !sel.length;
  }

  async function focusCounter(c) {
    S.br.focus = c;
    $$('#cnCntList .cn-li').forEach((l) => l.classList.toggle('active', l.dataset.focus === c));
    const it = S.br.items;
    if (!it?.ok) return;
    const key = `${S.br.obj}\u0000${c}`;
    if (!S.explains.has(key)) {
      S.explains.set(key, undefined); // in flight: the label's click and its checkbox's click both land here
      renderExplain();
      const inst = it.single ? null : it.instances[0] ?? '*';
      try { const r = await call('counters.explain', { path: makePath(S.br.obj, inst, c), english: false }); S.explains.set(key, r.text || ''); } catch { S.explains.set(key, ''); }
    }
    if (S.br.focus === c) renderExplain();
  }

  function renderExplain() {
    const el = $('#cnExplain');
    if (!el) return;
    const c = S.br.focus;
    if (!c || !S.br.items?.ok) { el.classList.add('hidden'); return; }
    const text = S.explains.get(`${S.br.obj}\u0000${c}`);
    el.classList.remove('hidden');
    el.innerHTML = `<b>${bdi(c)}</b><p dir="auto">${text === undefined ? '…' : esc(text || t('cn.br.noExplain'))}</p>`;
  }

  async function addSelection() {
    const sel = selection();
    if (!sel.length) return;
    const b = $('#cnAddSel');
    b.disabled = true;
    try {
      if (await addCounters(sel)) { S.br.selC.clear(); renderCounters(); }
    } finally { renderSelInfo(); }
  }

  async function addByPath() {
    const input = $('#cnPath');
    const path = input.value.trim();
    if (!path) return;
    const b = $('#cnPathAdd');
    b.disabled = true;
    $('#cnPathMsg').innerHTML = '';
    try {
      const r = await call('counters.validate', { path });
      if (!r.ok) {
        const admin = r.error === 'access' && !state.init.isAdmin;
        $('#cnPathMsg').innerHTML = `<div class="note warn cn-msg"><div><b>${esc(errText(r.error))}</b>${r.detail ? `<div class="path cn-detail">${esc(r.detail)}</div>` : ''}</div>${admin ? `<button class="btn" data-admin>${t('set.restartAdmin')}</button>` : ''}</div>`;
        return;
      }
      if (await addCounters([{ path: r.path, english: r.english }])) input.value = '';
    } catch (e) { toast(e.message, 4000); } finally { b.disabled = false; }
  }

  /* ---------------- history ---------------- */
  async function loadHistory() {
    try { S.hist = await call('counters.history', { range: S.histRange }); } catch (e) { toast(e.message, 4000); return; }
    renderHistory();
  }

  function histRows(h) {
    return h.series.map((s, i) => {
      const st = statsOf(s.avg);
      const lo = statsOf(s.lo).min, hi = statsOf(s.hi).max;
      const key = keyOf(s.path);
      const rec = h.recorded.find((r) => r.key === key);
      return { s, i, key, rec, color: PALETTE[i % PALETTE.length], avg: st.avg, min: lo, max: hi, on: !S.histHidden.has(key), scale: S.histScale[key] || 0 };
    });
  }

  function renderHistory() {
    const h = S.hist;
    if (!h || !$('#cnHistTable')) return;
    const rows = histRows(h);
    $('#cnRecCount').innerHTML = t('cn.recorded', { n: N(h.recorded.length), max: N(h.maxRecorded) });
    $('#cnHistPoint').textContent = rows.length ? t('cn.hist.point', { b: fmtDur(h.bucket) }) : '';
    $('#cnHistNote').innerHTML = `${esc(t('cn.recNote', { s: fmtInterval(h.interval), d: h.retentionDays, f: h.fullResolutionDays, n: h.maxRecorded }))}${h.dbSize ? ` <span class="muted">· ${t('cn.hist.size', { s: N(fmtBytes(h.dbSize)) })}</span>` : ''}`;
    const sig = lang() + JSON.stringify(rows.map((r) => [r.key, r.s.id, r.s.recording, r.rec?.error, r.on, r.scale]));
    if (sig !== S.histSig) { S.histSig = sig; $('#cnHistTable').innerHTML = histTable(rows); }
    const eff = rows.map((r) => r.scale || autoScale(r.max ?? 0));
    for (const r of rows) {
      for (const k of ['avg', 'min', 'max']) { const el = document.getElementById(`cn-h-${r.i}-${k}`); if (el) el.textContent = fmtV(r[k]); }
      autoLabel(`cn-hauto-${r.i}`, eff[r.i]);
    }
    if (!$('#cn-hist')) return;
    const vis = rows.filter((r) => r.on);
    const opt = lineOption({
      from: h.from, to: h.to,
      series: vis.map((r) => ({ name: seriesName(r.s, null), color: r.color, r })),
      times: (s) => s.r.s.t.map((ts, i) => { const v = s.r.s.avg[i]; return [ts, v == null ? null : v * eff[s.r.i], v]; }),
      fmtX: (v) => fmtTime(v, { axis: true }),
    });
    if (!rows.length || !rows.some((r) => r.s.t.length)) {
      opt.graphic = [{ type: 'text', left: 'center', top: 'middle', style: { text: rows.length ? t('noData') : t('cn.hist.empty'), fill: cssVar('--muted'), font: `13px ${cssVar('--font')}`, width: 420, overflow: 'break', align: 'center' } }];
    }
    getChart('cn-hist').setOption(opt, true);
  }

  function histTable(rows) {
    if (!rows.length) return `<div class="empty">${t('cn.hist.empty')}</div>`;
    let h = `<table class="cn-table"><thead><tr>
      <th>${t('cn.col.show')}</th><th>${t('cn.col.counter')}</th><th>${t('cn.col.instance')}</th><th>${t('cn.col.status')}</th>
      <th class="r">${t('cn.col.avg')}</th><th class="r">${t('cn.col.min')}</th><th class="r">${t('cn.col.max')}</th>
      <th>${t('cn.col.scale')}</th><th class="c">${t('cn.col.record')}</th><th></th></tr></thead><tbody>`;
    for (const r of rows) {
      const s = r.s;
      const status = r.rec?.error ? `<span class="badge warn" title="${esc(errText(r.rec.error))}">${esc(errText(r.rec.error))}</span>`
        : s.recording ? `<span class="badge ok">${t('cn.recOn')}</span>` : `<span class="badge info">${t('cn.recOff')}</span>`;
      h += `<tr>
        <td><label class="cn-show"><input type="checkbox" data-hshow="${esc(r.key)}" ${r.on ? 'checked' : ''}><span class="cn-dot" style="--dc:${r.color}"></span></label></td>
        <td><div class="cn-name"><b title="${esc(s.path)}">${bdi(s.counter || s.path)}</b><span>${bdi(s.obj || '')}</span></div></td>
        <td class="cn-inst" title="${esc(s.inst || '')}">${bdi(s.inst ?? '—')}</td>
        <td>${status}</td>
        ${['avg', 'min', 'max'].map((k) => `<td class="r num" id="cn-h-${r.i}-${k}">—</td>`).join('')}
        <td>${scaleSelect(`data-hscale="${esc(r.key)}"`, r.scale, `cn-hauto-${r.i}`)}</td>
        <td class="c"><label class="switch" title="${esc(t('cn.col.record'))}"><input type="checkbox" data-hrec="${r.i}" ${s.recording ? 'checked' : ''}><i></i></label></td>
        <td><button class="mini-btn cn-icon" data-forget="${s.id}" title="${esc(t('cn.hist.forget'))}" aria-label="${esc(t('cn.hist.forget'))}" ${s.recording ? 'disabled' : ''}><svg viewBox="0 0 24 24"><path d="M4 7h16M10 11v6M14 11v6M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12M9 7V4h6v3" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg></button></td>
      </tr>`;
    }
    return `${h}</tbody></table>`;
  }

  function bindHistTable() {
    const host = $('#cnHistTable');
    host.addEventListener('change', async (e) => {
      const el = e.target;
      const h = S.hist;
      if (!h) return;
      if (el.dataset.hshow != null) {
        if (el.checked) S.histHidden.delete(el.dataset.hshow); else S.histHidden.add(el.dataset.hshow);
        renderHistory();
      } else if (el.dataset.hscale != null) {
        S.histScale[el.dataset.hscale] = +el.value;
        renderHistory();
      } else if (el.dataset.hrec != null) {
        const s = h.series[+el.dataset.hrec];
        el.disabled = true;
        let rec = h.recorded.map((r) => ({ path: r.path, english: r.english }));
        if (el.checked) {
          if (rec.length >= h.maxRecorded) { toast(errText('limit', h.maxRecorded), 4500); el.checked = false; el.disabled = false; return; }
          rec.push({ path: s.path, english: s.english });
        } else rec = rec.filter((r) => keyOf(r.path) !== keyOf(s.path));
        const res = await call('counters.setRecorded', { items: rec });
        if (!res.ok) toast(res.error === 'limit' ? errText('limit', h.maxRecorded) : errText(res.error), 4500);
        else toast(t(el.checked ? 'cn.recStarted' : 'cn.recStopped'));
        await loadHistory();
      }
    });
    host.addEventListener('click', async (e) => {
      const b = e.target.closest('[data-forget]');
      if (!b || b.disabled) return;
      const s = S.hist?.series.find((x) => String(x.id) === b.dataset.forget);
      if (!s) return;
      if (!await confirmDialog(t('cn.hist.forgetQ'), s.path, t('cn.hist.forget'))) return;
      const res = await call('counters.forget', { id: s.id });
      if (res.ok) toast(t('cn.hist.deleted')); else toast(errText(res.error), 4000);
      await loadHistory();
    });
  }

  /* ---------------- page ---------------- */
  function subtitle() { return t(S.tab === 'history' ? 'cn.subHist' : 'cn.sub'); }

  async function load() {
    stop();
    const seq = ++S.loadSeq; // a newer load (language switch, tab change) supersedes this one
    buildShell();
    if (!S.presets) S.presets = await call('counters.presets').catch(() => []);
    if (S.tab === 'live') {
      if (S.paused && S.data) renderLive(); else await refreshLive();
      if (seq === S.loadSeq && state.page === ID) S.timer = setInterval(tickLive, 1000);
    } else {
      await loadHistory();
      if (seq === S.loadSeq && state.page === ID) {
        S.histTimer = setInterval(() => { if (!document.hidden && state.page === ID && S.tab === 'history') loadHistory(); }, 15000);
      }
    }
  }

  function stop() {
    clearInterval(S.timer); S.timer = null;
    clearInterval(S.histTimer); S.histTimer = null;
  }

  registerPage(ID, { load, stop, subtitle }, { presets: 'cn.presets', history: 'cn.tab.history' });

  // Processes page: right-click → watch this process (or every process of the app) with Process V2 counters,
  // whose instance names carry the PID ("chrome:1234"), so the chart follows exactly that process.
  PROC_MENU_EXT.push((sel, app) => {
    const base = (sel.name || app?.name || '').replace(/\.exe$/i, '');
    if (!base || !pageShown(ID)) return [];
    return [{ key: 'cn-watch', label: t('cn.watch'), run: () => watchProcess(base, sel.kind === 'proc' ? sel.pid : null) }];
  });
  async function watchProcess(base, pid) {
    const inst = pid ? `${base}:${pid}` : `${base}:*`;
    const list = PROC_COUNTERS.map((c) => ({ path: `\\Process V2(${inst})\\${c}`, english: true }));
    const v = await call('counters.validate', { path: list[0].path, english: true });
    if (!v.ok) { toast(errText(v.error), 4500); return; }
    S.tab = 'live';
    if (await addCounters(list)) showPage(ID);
  }
})();
