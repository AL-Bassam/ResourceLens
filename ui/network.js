'use strict';
/* Network page (GlassWire-style): live meters, connections, data usage, first-time-online alerts, firewall blocks, data cap */
(() => {
  Object.assign(I18N.ar, {
    'nav.network': 'الشبكة', 'title.network': 'مراقبة الشبكة',
    'net.sub': 'حركة البيانات الآن، الاتصالات المفتوحة، واستهلاك كل تطبيق',
    'net.download': 'التنزيل', 'net.upload': 'الرفع', 'net.today': 'اليوم', 'net.month': 'هذا الشهر', 'net.now': 'الآن',
    'net.peak': 'أعلى قيمة مؤخراً', 'net.avgDay': 'متوسط اليوم الواحد: {v}', 'net.ofCap': '{p} من حد {c}', 'net.capOff': 'لا يوجد حد شهري',
    'net.meters': 'العدادات الحية', 'net.connections': 'الاتصالات الحية', 'net.usage': 'الاستهلاك حسب التطبيق',
    'net.daily': 'الاستهلاك اليومي (آخر 30 يوماً)', 'net.dailyShort': 'الاستهلاك اليومي', 'net.activity': 'تطبيقات اتصلت لأول مرة',
    'net.activityShort': 'النشاط', 'net.blocked': 'التطبيقات المحظورة', 'net.settings': 'إعدادات الشبكة',
    'net.connCount': '{n} اتصال · {e} متصل · {l} يستمع · {u} UDP',
    'net.search': 'ابحث عن تطبيق أو عنوان…', 'net.estOnly': 'المتصلة فقط', 'net.loopback': 'إظهار الاتصالات الداخلية',
    'net.col.app': 'التطبيق', 'net.col.proto': 'بروتوكول', 'net.col.local': 'العنوان المحلي', 'net.col.remote': 'العنوان البعيد',
    'net.col.host': 'اسم المضيف', 'net.col.state': 'الحالة', 'net.col.down': 'تنزيل', 'net.col.up': 'رفع', 'net.col.total': 'الإجمالي',
    'net.st.established': 'متصل', 'net.st.listen': 'يستمع', 'net.st.syn-sent': 'جارٍ الاتصال', 'net.st.syn-received': 'طلب وارد',
    'net.st.fin-wait-1': 'يُغلق', 'net.st.fin-wait-2': 'يُغلق', 'net.st.close-wait': 'بانتظار الإغلاق', 'net.st.closing': 'يُغلق',
    'net.st.last-ack': 'يُغلق', 'net.st.time-wait': 'انتهى للتو', 'net.st.closed': 'مغلق', 'net.st.delete-tcb': 'يُحذف',
    'net.noConn': 'لا توجد اتصالات مطابقة.', 'net.idle': 'عمليات منتهية',
    'net.range.1': 'اليوم', 'net.range.7': '7 أيام', 'net.range.30': '30 يوماً',
    'net.periodTotal': 'إجمالي الجهاز في الفترة: ↓ {rx} · ↑ {tx}',
    'net.noPerApp': 'لا توجد أرقام لكل تطبيق في هذه الفترة. يعطي ويندوز حركة الشبكة لكل تطبيق للمسؤول فقط، لذلك تُسجَّل هذه الأرقام في الأوقات التي يعمل فيها ResourceLens بصلاحيات المسؤول.',
    'net.notRecording': 'أرقام التطبيقات لا تُسجَّل الآن لأن البرنامج يعمل بدون صلاحيات المسؤول.',
    'net.noUsage': 'لا يوجد استهلاك مسجَّل لأي تطبيق في هذه الفترة.',
    'net.known': 'يعرف البرنامج {n} تطبيقاً يستخدم الشبكة. أي تطبيق جديد يتصل بالإنترنت يظهر هنا.',
    'net.learning': 'جارٍ التعرّف على التطبيقات التي تستخدم الشبكة حالياً…',
    'net.noActivity': 'لم يتصل أي تطبيق جديد بالإنترنت منذ بدء المراقبة.',
    'net.paused': 'الرصد متوقف: فعّل «التنبيه عند اتصال تطبيق لأول مرة» أو شغّل البرنامج كمسؤول.',
    'net.ev.first': 'اتصل بالإنترنت لأول مرة', 'net.ev.block': 'حُظر وصوله إلى الشبكة', 'net.ev.unblock': 'سُمح له بالوصول إلى الشبكة',
    'net.ev.cap90': 'بلغ الاستهلاك 90% من الحد الشهري', 'net.ev.cap100': 'بلغ الاستهلاك الحد الشهري',
    'net.lastOnline': 'آخر اتصال {t}',
    'net.noBlocked': 'لا توجد تطبيقات محظورة.',
    'net.blockedNote': 'قواعد في جدار حماية ويندوز باسم «ResourceLens block: …» تمنع الاتصال في الاتجاهين، ويمكن رؤيتها أيضاً في إعدادات جدار الحماية.',
    'net.block': 'حظر', 'net.allow': 'سماح', 'net.blockedBadge': 'محظور',
    'net.blockAccess': 'حظر الوصول إلى الشبكة', 'net.allowAccess': 'السماح بالوصول إلى الشبكة',
    'net.blockTitle': 'حظر وصول «{n}» إلى الشبكة؟',
    'net.blockBody': 'سيضيف ResourceLens قاعدتين في جدار حماية ويندوز تمنعان هذا التطبيق من الاتصال بالشبكة (الوارد والصادر). يمكنك إلغاء الحظر في أي وقت من هذه الصفحة.',
    'net.allowTitle': 'السماح لـ«{n}» بالوصول إلى الشبكة؟',
    'net.allowBody': 'ستُحذف قاعدتا الحظر اللتان أنشأهما ResourceLens لهذا التطبيق.',
    'net.blockedOk': 'تم حظر {n}', 'net.allowedOk': 'أصبح {n} قادراً على الاتصال',
    'net.adminTitle': 'تحتاج صلاحيات المسؤول',
    'net.adminBody': 'تعديل قواعد جدار حماية ويندوز يتطلب تشغيل ResourceLens كمسؤول.',
    'net.adminNote': 'البرنامج يعمل بدون صلاحيات المسؤول: لا تُسجَّل أرقام الشبكة لكل تطبيق، ولا يمكن حظر التطبيقات في جدار الحماية. باقي الصفحة يعمل كالمعتاد.',
    'net.err.access': 'تحتاج هذه العملية صلاحيات المسؤول.', 'net.err.critical': 'هذا جزء أساسي من ويندوز ولا يمكن حظره بأمان.',
    'net.err.no-path': 'تعذّر العثور على ملف التطبيق لإنشاء القاعدة.', 'net.err.failed': 'رفض ويندوز تعديل جدار الحماية.',
    'net.alertFirst': 'التنبيه عند اتصال تطبيق لأول مرة',
    'net.alertFirstHint': 'إشعار بجانب الساعة حين يتصل تطبيق بالإنترنت لأول مرة. القائمة أعلاه تُسجَّل في الحالتين.',
    'net.cap': 'حد البيانات الشهري', 'net.capHint': '0 يعني بلا حد. يصلك تنبيه عند 90% وعند 100% من الحد.',
    'net.saved': 'تم الحفظ', 'net.copyRemote': 'نسخ العنوان البعيد', 'net.copyHost': 'نسخ اسم المضيف',
    'net.searchHost': 'البحث عن هذا العنوان على الإنترنت', 'net.copied': 'تم النسخ',
  });
  Object.assign(I18N.en, {
    'nav.network': 'Network', 'title.network': 'Network monitor',
    'net.sub': 'Live traffic, open connections and data used by each app',
    'net.download': 'Download', 'net.upload': 'Upload', 'net.today': 'Today', 'net.month': 'This month', 'net.now': 'Now',
    'net.peak': 'Recent peak', 'net.avgDay': 'Average day: {v}', 'net.ofCap': '{p} of the {c} cap', 'net.capOff': 'No monthly cap',
    'net.meters': 'Live meters', 'net.connections': 'Live connections', 'net.usage': 'Data usage by app',
    'net.daily': 'Daily usage (last 30 days)', 'net.dailyShort': 'Daily usage', 'net.activity': 'Apps online for the first time',
    'net.activityShort': 'Activity', 'net.blocked': 'Blocked apps', 'net.settings': 'Network settings',
    'net.connCount': '{n} connections · {e} established · {l} listening · {u} UDP',
    'net.search': 'Search apps or addresses…', 'net.estOnly': 'Established only', 'net.loopback': 'Show loopback',
    'net.col.app': 'App', 'net.col.proto': 'Protocol', 'net.col.local': 'Local address', 'net.col.remote': 'Remote address',
    'net.col.host': 'Host name', 'net.col.state': 'State', 'net.col.down': 'Download', 'net.col.up': 'Upload', 'net.col.total': 'Total',
    'net.st.established': 'Established', 'net.st.listen': 'Listening', 'net.st.syn-sent': 'Connecting', 'net.st.syn-received': 'Incoming',
    'net.st.fin-wait-1': 'Closing', 'net.st.fin-wait-2': 'Closing', 'net.st.close-wait': 'Close wait', 'net.st.closing': 'Closing',
    'net.st.last-ack': 'Closing', 'net.st.time-wait': 'Just closed', 'net.st.closed': 'Closed', 'net.st.delete-tcb': 'Removing',
    'net.noConn': 'No matching connections.', 'net.idle': 'Ended processes',
    'net.range.1': 'Today', 'net.range.7': '7 days', 'net.range.30': '30 days',
    'net.periodTotal': 'Whole computer in this period: ↓ {rx} · ↑ {tx}',
    'net.noPerApp': 'No per-app numbers for this period. Windows only reports network traffic per app to administrators, so these numbers are recorded while ResourceLens runs as administrator.',
    'net.notRecording': 'Per-app numbers are not being recorded right now: the app is not running as administrator.',
    'net.noUsage': 'No app used the network in this period.',
    'net.known': 'ResourceLens knows {n} apps that use the network. Any new app that goes online shows up here.',
    'net.learning': 'Learning which apps already use the network…',
    'net.noActivity': 'No new app has gone online since monitoring started.',
    'net.paused': 'Detection is paused: turn on “Alert when an app connects for the first time” or run as administrator.',
    'net.ev.first': 'Went online for the first time', 'net.ev.block': 'Network access blocked', 'net.ev.unblock': 'Network access allowed',
    'net.ev.cap90': 'Used 90% of the monthly cap', 'net.ev.cap100': 'Reached the monthly cap',
    'net.lastOnline': 'last online {t}',
    'net.noBlocked': 'No apps are blocked.',
    'net.blockedNote': 'Windows Firewall rules named “ResourceLens block: …” stop traffic both ways; they are also listed in the firewall settings.',
    'net.block': 'Block', 'net.allow': 'Allow', 'net.blockedBadge': 'Blocked',
    'net.blockAccess': 'Block network access', 'net.allowAccess': 'Allow network access',
    'net.blockTitle': 'Block network access for “{n}”?',
    'net.blockBody': 'ResourceLens will add two Windows Firewall rules that stop this app from using the network (incoming and outgoing). You can unblock it at any time on this page.',
    'net.allowTitle': 'Allow “{n}” to use the network again?',
    'net.allowBody': 'The two block rules ResourceLens created for this app will be removed.',
    'net.blockedOk': '{n} is blocked', 'net.allowedOk': '{n} can use the network again',
    'net.adminTitle': 'Administrator rights needed',
    'net.adminBody': 'Changing Windows Firewall rules requires running ResourceLens as administrator.',
    'net.adminNote': 'The app is not running as administrator: per-app network numbers are not recorded and apps cannot be blocked in the firewall. The rest of this page works as usual.',
    'net.err.access': 'This needs administrator rights.', 'net.err.critical': 'This is a core part of Windows and cannot be blocked safely.',
    'net.err.no-path': 'Could not find the app’s program file to create the rule.', 'net.err.failed': 'Windows refused to change the firewall.',
    'net.alertFirst': 'Alert when an app connects for the first time',
    'net.alertFirstHint': 'A notification by the clock when an app goes online for the first time. The list above is kept either way.',
    'net.cap': 'Monthly data cap', 'net.capHint': '0 means no cap. You are alerted at 90% and at 100% of it.',
    'net.saved': 'Saved', 'net.copyRemote': 'Copy remote address', 'net.copyHost': 'Copy host name',
    'net.searchHost': 'Search the web for this address', 'net.copied': 'Copied',
  });

  // Mirrors the backend list: Windows itself and kernel pseudo-processes (no .exe) are never offered for blocking.
  const PROTECTED = new Set(['system', 'idle', 'registry', 'smss.exe', 'csrss.exe', 'wininit.exe', 'winlogon.exe', 'services.exe',
    'lsass.exe', 'lsaiso.exe', 'svchost.exe', 'dwm.exe', 'fontdrvhost.exe', 'msmpeng.exe', 'audiodg.exe', 'resourcelens.exe']);
  const isProtected = (name) => !name || PROTECTED.has(name.toLowerCase()) || !/\.exe$/i.test(name);
  const LRM = (s) => `\u200e${s}\u200e`;
  const host = () => $('#page-network');
  const loc = () => (lang() === 'ar' ? 'ar-u-nu-latn' : 'en-US');
  const fmtDay = (ts) => new Intl.DateTimeFormat(loc(), { day: 'numeric', month: 'short' }).format(new Date(ts));
  const fmtLongDay = (ts) => new Intl.DateTimeFormat(loc(), { weekday: 'long', day: 'numeric', month: 'long' }).format(new Date(ts));
  const STATE_COLOR = { established: 'var(--good)', listen: 'var(--accent)', 'time-wait': 'var(--muted)', closed: 'var(--muted)' };
  const SEARCH_SVG = '<svg viewBox="0 0 24 24"><circle cx="11" cy="11" r="7" fill="none" stroke="currentColor" stroke-width="2"/><path d="m20 20-3.5-3.5" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>';
  const DOWN_SVG = '<svg viewBox="0 0 24 24"><path d="M12 4v14m0 0-5-5m5 5 5-5" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  const UP_SVG = '<svg viewBox="0 0 24 24"><path d="M12 20V6m0 0-5 5m5-5 5 5" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  const DAY_SVG = '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9" fill="none" stroke="currentColor" stroke-width="2"/><path d="M12 7v5l3 2" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>';
  const MONTH_SVG = '<svg viewBox="0 0 24 24"><rect x="3" y="5" width="18" height="16" rx="2" fill="none" stroke="currentColor" stroke-width="2"/><path d="M3 10h18M8 3v4M16 3v4" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>';

  const S = {
    active: false, timers: [], ticks: 0,
    conn: null, connList: [], connSig: '', connBusy: false, connAgain: false, sort: { key: 'name', dir: 1 }, est: false, loopback: false, q: '',
    days: 1, usage: null, summary: null, asOf: 0, extra: { rx: 0, tx: 0 }, lastLive: 0,
    activity: null, rules: null, blocked: null, cfg: null,
  };

  const errText = (res) => t(`net.err.${res?.error || 'failed'}`) + (res?.detail && res.error === 'failed' ? ` (${res.detail})` : '');
  const isBlocked = (name) => !!(name && S.blocked?.has(name.toLowerCase()));
  const adminNote = () => `<div class="note warn net-admin"><span>${t('net.adminNote')}</span><button class="btn" data-admin>${t('set.restartAdmin')}</button></div>`;

  /* ---------------- page skeleton ---------------- */
  function build() {
    const kpi = (id, color, svg, label) => `
      <div class="card kpi" style="--kc:var(${color})">
        <div class="kpi-top"><div class="kpi-label"><i>${svg}</i>${label}</div><span class="kpi-badge" id="nkb-${id}"></span></div>
        <div class="kpi-value" id="nkv-${id}">—</div>
        <div class="kpi-sub" id="nks-${id}"></div>
        ${id === 'rx' || id === 'tx' ? `<div class="spark" id="net-sp-${id}"></div>` : '<div class="net-capbar" id="nkc-' + id + '"></div>'}
      </div>`;
    host().innerHTML = `
      ${state.init?.isAdmin ? '' : adminNote()}
      ${shown('network', 'meters') ? `<div class="kpis net-kpis">
        ${kpi('rx', '--c-net-rx', DOWN_SVG, t('net.download'))}${kpi('tx', '--c-net-tx', UP_SVG, t('net.upload'))}
        ${kpi('today', '--accent', DAY_SVG, t('net.today'))}${kpi('month', '--accent-2', MONTH_SVG, t('net.month'))}
      </div>` : ''}
      ${shown('network', 'connections') ? `<div class="card flush net-conn">
        <div class="net-conn-head">
          <h3>${t('net.connections')}</h3>
          <div class="search">${SEARCH_SVG}<input id="netSearch" placeholder="${esc(t('net.search'))}" value="${esc(S.q)}"></div>
          <label class="net-toggle"><span class="switch"><input type="checkbox" id="netEst" ${S.est ? 'checked' : ''}><i></i></span>${t('net.estOnly')}</label>
          <label class="net-toggle"><span class="switch"><input type="checkbox" id="netLoop" ${S.loopback ? 'checked' : ''}><i></i></span>${t('net.loopback')}</label>
          <span class="toolbar-note net-count" id="netConnCount"></span>
        </div>
        <div class="table-wrap" id="netConn"><div class="skeleton" style="height:220px;margin:12px"></div></div>
      </div>` : ''}
      ${shown('network', 'usage') || shown('network', 'daily') ? `<div class="net-grid">
        ${shown('network', 'usage') ? `<div class="card net-card">
          <div class="card-head"><h3>${t('net.usage')}</h3>
            <div class="seg" id="netDays">${[1, 7, 30].map((d) => `<button data-d="${d}" class="${S.days === d ? 'active' : ''}">${t(`net.range.${d}`)}</button>`).join('')}</div></div>
          <div id="netUsage"><div class="skeleton" style="height:240px"></div></div>
        </div>` : ''}
        ${shown('network', 'daily') ? `<div class="card chart-card net-card">
          <div class="card-head"><h3>${t('net.daily')}</h3><div class="legend" id="netDailyLg"></div></div>
          <div class="chart net-daily" id="net-daily"></div>
        </div>` : ''}
      </div>` : ''}
      <div class="net-grid">
        ${shown('network', 'activity') ? `<div class="card net-card"><div class="card-head"><h3>${t('net.activity')}</h3></div><div id="netActivity"><div class="skeleton" style="height:160px"></div></div></div>` : ''}
        <div class="net-col">
          ${shown('network', 'blocked') ? `<div class="card net-card"><div class="card-head"><h3>${t('net.blocked')}</h3></div><div id="netBlocked"><div class="skeleton" style="height:80px"></div></div></div>` : ''}
          <div class="card set-card net-card" id="netSettings"></div>
        </div>
      </div>`;

    const p = host();
    $$('[data-admin]', p).forEach((b) => b.addEventListener('click', () => call('restartAsAdmin').catch((e) => toast(e.message))));
    $('#netSearch', p)?.addEventListener('input', (e) => { S.q = e.target.value; renderConn(); });
    $('#netEst', p)?.addEventListener('change', (e) => { S.est = e.target.checked; loadConn(); });
    $('#netLoop', p)?.addEventListener('change', (e) => { S.loopback = e.target.checked; loadConn(); });
    $$('#netDays button', p).forEach((b) => b.addEventListener('click', () => {
      S.days = +b.dataset.d;
      $$('#netDays button', p).forEach((x) => x.classList.toggle('active', x === b));
      S.usage = null; renderUsage(); loadUsage();
    }));
    if ($('#netDailyLg')) legend('#netDailyLg', [[t('net.download'), cssVar('--c-net-rx')], [t('net.upload'), cssVar('--c-net-tx')]]);
  }
  function legend(sel, items) { $(sel).innerHTML = items.map(([label, color]) => `<span style="--lc:${color}">${esc(label)}</span>`).join(''); }

  /* ---------------- live meters ---------------- */
  function renderMeters() {
    if (!$('#nkv-rx')) return;
    const l = state.live;
    const hist = state.liveHist || [];
    const peak = (k) => hist.reduce((m, x) => Math.max(m, x[k] ?? 0), 0);
    $('#nkv-rx').innerHTML = N(fmtRate(l?.netRx));
    $('#nkv-tx').innerHTML = N(fmtRate(l?.netTx));
    $('#nks-rx').innerHTML = `<span>${t('net.peak')} <b class="num">${fmtRate(peak('netRx'))}</b></span>`;
    $('#nks-tx').innerHTML = `<span>${t('net.peak')} <b class="num">${fmtRate(peak('netTx'))}</b></span>`;
    const s = S.summary;
    if (!s) return;
    const both = (rx, tx) => `<span>↓ <b class="num">${fmtBytes(rx)}</b></span><span>↑ <b class="num">${fmtBytes(tx)}</b></span>`;
    const tRx = s.today.rx + S.extra.rx, tTx = s.today.tx + S.extra.tx;
    const mRx = s.month.rx + S.extra.rx, mTx = s.month.tx + S.extra.tx;
    $('#nkv-today').innerHTML = N(fmtBytes(tRx + tTx));
    $('#nks-today').innerHTML = both(tRx, tTx);
    $('#nkv-month').innerHTML = N(fmtBytes(mRx + mTx));
    $('#nks-month').innerHTML = both(mRx, mTx);
    const capEl = $('#nkc-month');
    if (s.cap) {
      const used = s.cap.used + S.extra.rx + S.extra.tx;
      const pct = (used * 100) / (s.cap.gb * 1024 ** 3);
      const color = pct >= 100 ? 'var(--bad)' : pct >= 90 ? 'var(--warn)' : 'var(--accent-2)';
      $('#nkb-month').innerHTML = `<span class="num">${fmtPct(pct)}</span>`;
      capEl.innerHTML = `<span class="bar"><i style="width:${Math.min(100, pct)}%;--bc:${color}"></i></span><span class="muted">${t('net.ofCap', { p: N(fmtPct(pct)), c: N(`${fmtNum(s.cap.gb, 1)} GB`) })}</span>`;
    } else {
      $('#nkb-month').textContent = '';
      capEl.innerHTML = `<span class="muted">${t('net.capOff')}</span>`;
    }
    const past = s.days.filter((d) => d.ts < s.dayStart && d.rx != null);
    const avg = past.length ? past.reduce((a, d) => a + d.rx + d.tx, 0) / past.length : null;
    $('#nkc-today').innerHTML = avg == null ? '' : `<span class="muted">${t('net.avgDay', { v: N(fmtBytes(avg)) })}</span>`;
  }
  function drawSparks() {
    const hist = state.liveHist || [];
    for (const [id, key, color] of [['rx', 'netRx', '--c-net-rx'], ['tx', 'netTx', '--c-net-tx']]) {
      if (!$(`#net-sp-${id}`)) continue;
      getChart(`net-sp-${id}`).setOption({
        animation: false,
        grid: { left: 0, right: 0, top: 4, bottom: 0 },
        xAxis: { type: 'time', show: false },
        yAxis: { type: 'value', show: false, min: 0, max: (v) => Math.max(v.max * 1.15, 1024) },
        series: [series('', hist.map((x) => [x.ts, x[key] ?? null]), cssVar(color), { width: 1.5, areaA: 0.28 })],
      }, true);
    }
  }

  /* ---------------- connections ---------------- */
  async function loadConn() {
    if (!$('#netConn')) return;
    if (S.connBusy) { S.connAgain = true; return; } // a toggle during a poll: ask again once it returns
    S.connBusy = true;
    const est = S.est, loop = S.loopback;
    try {
      const res = await call('network.connections', { established: est, loopback: loop });
      if (!S.active) return;
      if (est === S.est && loop === S.loopback) { S.conn = res; renderConn(); } else S.connAgain = true;
    } catch (e) { console.error(e); } finally {
      S.connBusy = false;
      if (S.connAgain && S.active) { S.connAgain = false; loadConn(); }
    }
  }
  const appOf = (name) => (name && S.conn?.apps?.[name]) || {};
  const labelOf = (r) => (r.name ? appName({ name: r.name, descr: appOf(r.name).descr }) : `PID ${r.pid}`);
  const sortVal = {
    name: (r) => labelOf(r).toLowerCase(), proto: (r) => r.proto, local: (r) => r.local, remote: (r) => r.remote || '',
    host: (r) => r.host || r.remoteIp || '', state: (r) => (r.state ? t(`net.st.${r.state}`) : 'UDP'),
  };
  function renderConn() {
    const el = $('#netConn');
    if (!el || !S.conn) return;
    const { rows, counts } = S.conn;
    $('#netConnCount').innerHTML = t('net.connCount', { n: N(counts.total), e: N(counts.established), l: N(counts.listening), u: N(counts.udp) });
    const q = S.q.trim().toLowerCase();
    const list = rows.filter((r) => !q || [labelOf(r), r.name, r.local, r.remote, r.host, r.proto, r.state && t(`net.st.${r.state}`), String(r.pid)]
      .some((v) => v && String(v).toLowerCase().includes(q)));
    const f = sortVal[S.sort.key];
    const cmp = (x, y) => String(x).localeCompare(String(y), undefined, { numeric: true });
    // By app: processes with live connections first, then each one's established rows before listeners.
    const live = new Set(list.filter((r) => r.state === 'established').map((r) => r.pid));
    const rank = (r) => (r.state === 'established' ? 0 : r.state === 'listen' ? 2 : r.state ? 1 : 3);
    list.sort(S.sort.key === 'name'
      ? (a, b) => (live.has(b.pid) - live.has(a.pid)) || S.sort.dir * cmp(f(a), f(b)) || a.pid - b.pid || rank(a) - rank(b) || cmp(a.remote || '', b.remote || '')
      : (a, b) => S.sort.dir * cmp(f(a), f(b)) || cmp(a.remote || '', b.remote || ''));
    // Skip identical repaints (most polls): keeps icons, hover and text selection steady.
    const sig = JSON.stringify([list, S.sort, lang(), S.blocked ? [...S.blocked] : 0]);
    if (sig === S.connSig) return;
    S.connSig = sig;
    S.connList = list;
    const cols = [['name', 'net.col.app'], ['proto', 'net.col.proto'], ['local', 'net.col.local'], ['remote', 'net.col.remote'], ['host', 'net.col.host'], ['state', 'net.col.state']];
    const grouped = S.sort.key === 'name';
    const scroll = el.scrollTop;
    el.innerHTML = list.length ? `<table class="net-table"><colgroup>${cols.map(([k]) => `<col class="nc-${k}">`).join('')}</colgroup><thead><tr>${cols.map(([k, label]) =>
      `<th data-s="${k}" class="${S.sort.key === k ? `sorted ${S.sort.dir > 0 ? 'asc' : ''}` : ''}">${t(label)}</th>`).join('')}</tr></thead><tbody>
      ${list.map((r, i) => {
        const cont = grouped && i > 0 && list[i - 1].name === r.name && list[i - 1].pid === r.pid;
        const a = appOf(r.name);
        const app = cont ? '' : `<div class="icon-cell">${icon({ nid: a.nid, name: r.name || '?', hasIcon: a.hasIcon })}<div><b>${esc(labelOf(r))}${isBlocked(r.name) ? ` <span class="badge bad">${t('net.blockedBadge')}</span>` : ''}</b><span class="num">${r.pid ? `PID ${r.pid}` : esc(t('net.idle'))}</span></div></div>`;
        const hostCell = r.host ? `<span class="net-host" title="${esc(r.remoteIp)}">${esc(r.host)}</span>`
          : r.remoteIp ? `<span class="num muted" title="${esc(r.remoteIp)}">${esc(r.remoteIp)}</span>` : '<span class="muted">—</span>';
        const st = r.state ? `<span class="st-badge" style="--sc:${STATE_COLOR[r.state] || 'var(--warn)'}">${esc(t(`net.st.${r.state}`))}</span>` : '<span class="muted">—</span>';
        return `<tr data-i="${i}" class="${grouped && !cont && i > 0 ? 'net-grp' : ''}">
          <td>${app}</td><td class="muted">${esc(r.proto)}</td><td><span class="num" title="${esc(r.local)}">${esc(r.local)}</span></td>
          <td>${r.remote ? `<span class="num" title="${esc(r.remote)}">${esc(r.remote)}</span>` : '<span class="muted">—</span>'}</td><td>${hostCell}</td><td>${st}</td></tr>`;
      }).join('')}</tbody></table>` : `<div class="empty">${t('net.noConn')}</div>`;
    el.scrollTop = scroll;
    $$('th[data-s]', el).forEach((th) => th.addEventListener('click', () => {
      const k = th.dataset.s;
      S.sort = { key: k, dir: S.sort.key === k ? -S.sort.dir : 1 };
      renderConn();
    }));
    $$('tbody tr', el).forEach((tr) => tr.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      const r = S.connList[+tr.dataset.i];
      if (!r) return;
      const items = [];
      if (!isProtected(r.name)) items.push(blockItem(r.name, labelOf(r), r.pid));
      if (r.remote) {
        if (items.length) items.push('-');
        items.push({ label: t('net.copyRemote'), run: () => copy(r.remote) });
        if (r.host) items.push({ label: t('net.copyHost'), run: () => copy(r.host) });
        items.push({ label: t('net.searchHost'), run: () => call('searchOnline', { q: r.host || r.remoteIp }) });
      }
      if (items.length) showMenu(e.clientX, e.clientY, items);
    }));
  }
  async function copy(text) {
    try { await navigator.clipboard.writeText(text); toast(t('net.copied')); } catch (e) { toast(e.message); }
  }
  function showMenu(x, y, items) {
    const menu = $('#ctx');
    menu.innerHTML = items.map((m, i) => (m === '-' ? '<hr>' : `<button data-i="${i}" class="${m.cls || ''}">${esc(m.label)}</button>`)).join('');
    menu.classList.remove('hidden');
    const r = menu.getBoundingClientRect();
    menu.style.left = `${Math.max(8, Math.min(x, window.innerWidth - r.width - 8))}px`;
    menu.style.top = `${Math.max(8, Math.min(y, window.innerHeight - r.height - 8))}px`;
    $$('button', menu).forEach((b) => b.addEventListener('click', () => {
      menu.classList.add('hidden');
      Promise.resolve(items[+b.dataset.i].run()).catch((e) => toast(e.message, 4000));
    }));
  }

  /* ---------------- data usage by app + daily chart ---------------- */
  async function loadUsage() {
    if (!$('#netUsage')) return;
    const days = S.days;
    try {
      const res = await call('network.usage', { days });
      if (!S.active || days !== S.days) return;
      S.usage = res;
      renderUsage();
    } catch (e) { console.error(e); }
  }
  function renderUsage() {
    const el = $('#netUsage');
    if (!el) return;
    const u = S.usage;
    if (!u) { el.innerHTML = '<div class="skeleton" style="height:240px"></div>'; return; }
    const notes = !u.perApp ? `<div class="note">${t('net.noPerApp')}</div>`
      : !state.live?.perProcessNetwork ? `<div class="note warn">${t('net.notRecording')}</div>` : '';
    const max = Math.max(1, ...u.apps.map((a) => a.rx + a.tx));
    el.innerHTML = `<div class="net-period">${t('net.periodTotal', { rx: N(fmtBytes(u.totalRx)), tx: N(fmtBytes(u.totalTx)) })}</div>${notes}
      ${u.apps.length ? `<div class="table-wrap net-usage"><table><colgroup><col class="nu-app"><col class="nu-n"><col class="nu-n"><col class="nu-total"></colgroup><thead><tr><th>${t('net.col.app')}</th><th class="r">${t('net.col.down')}</th><th class="r">${t('net.col.up')}</th><th class="r">${t('net.col.total')}</th></tr></thead><tbody>
        ${u.apps.map((a, i) => `<tr data-i="${i}">
          <td><div class="icon-cell">${icon(a)}<div><b>${esc(appName(a))}${isBlocked(a.name) ? ` <span class="badge bad">${t('net.blockedBadge')}</span>` : ''}</b><span>${esc(a.name)}</span></div></div></td>
          <td class="r num">${fmtBytes(a.rx)}</td><td class="r num">${fmtBytes(a.tx)}</td>
          <td class="r"><div class="bar-cell"><span class="num">${fmtBytes(a.rx + a.tx)}</span><span class="bar net-split"><i style="width:${((a.rx + a.tx) / max) * 100}%;--rx:${(a.rx / Math.max(1, a.rx + a.tx)) * 100}%"></i></span></div></td>
        </tr>`).join('')}</tbody></table></div>` : u.perApp ? `<div class="empty">${t('net.noUsage')}</div>` : ''}`;
    $$('tbody tr', el).forEach((tr) => tr.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      const a = u.apps[+tr.dataset.i];
      if (a && !isProtected(a.name)) showMenu(e.clientX, e.clientY, [blockItem(a.name, appName(a), 0)]);
    }));
  }
  function bytesAxis(max) {
    if (!(max > 0)) return {};
    const raw = max / 4;
    let unit = 1;
    while (raw / unit >= 1024) unit *= 1024;
    const step = ([1, 2, 2.5, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1024].find((s) => s * unit >= raw) ?? 1024) * unit;
    return { interval: step, max: Math.ceil(max / step) * step };
  }
  function drawDaily() {
    if (!$('#net-daily') || !S.summary) return;
    const th = chartTheme();
    const days = S.summary.days;
    const rx = days.map((d) => d.rx), tx = days.map((d) => d.tx);
    const empty = days.every((d) => d.rx == null && d.tx == null);
    const cRx = cssVar('--c-net-rx'), cTx = cssVar('--c-net-tx');
    getChart('net-daily').setOption({
      animationDuration: 300,
      textStyle: { fontFamily: th.font },
      grid: { left: 10, right: 14, top: 14, bottom: 6, containLabel: true },
      tooltip: {
        trigger: 'axis', confine: true, transitionDuration: 0,
        backgroundColor: th.surface, borderColor: th.border, borderWidth: 1, padding: [8, 12],
        textStyle: { color: th.text, fontFamily: th.font, fontSize: 12 },
        axisPointer: { type: 'shadow', shadowStyle: { color: alpha(cssVar('--accent'), 0.08) } },
        formatter: (ps) => {
          const d = days[ps[0]?.dataIndex];
          if (!d) return '';
          const line = (label, color, v) => `<div style="display:flex;gap:14px;justify-content:space-between;align-items:center"><span><span style="display:inline-block;width:8px;height:8px;border-radius:2px;background:${color};margin-inline-end:6px"></span>${esc(label)}</span><b style="direction:ltr">${fmtBytes(v)}</b></div>`;
          return `<div style="color:${th.muted};margin-bottom:4px">${esc(fmtLongDay(d.ts))}</div>`
            + line(t('net.download'), cRx, d.rx) + line(t('net.upload'), cTx, d.tx)
            + `<div style="border-top:1px solid ${th.border};margin-top:4px;padding-top:4px;display:flex;justify-content:space-between;gap:14px"><span>${t('net.col.total')}</span><b style="direction:ltr">${fmtBytes((d.rx ?? 0) + (d.tx ?? 0))}</b></div>`;
        },
      },
      xAxis: {
        type: 'category', data: days.map((d) => fmtDay(d.ts)),
        axisLine: { lineStyle: { color: th.grid } }, axisTick: { show: false },
        axisLabel: { color: th.muted, fontSize: 11, hideOverlap: true },
      },
      yAxis: {
        type: 'value', min: 0, splitNumber: 4, ...bytesAxis(Math.max(0, ...days.map((d) => (d.rx ?? 0) + (d.tx ?? 0)))),
        splitLine: { lineStyle: { color: th.grid } },
        axisLabel: { color: th.muted, fontSize: 11, formatter: (v) => LRM(fmtBytes(v)) },
      },
      series: [
        { type: 'bar', name: t('net.download'), data: rx, stack: 'net', barMaxWidth: 16, itemStyle: { color: cRx }, emphasis: { disabled: true } },
        { type: 'bar', name: t('net.upload'), data: tx, stack: 'net', barMaxWidth: 16, itemStyle: { color: cTx, borderRadius: [3, 3, 0, 0] }, emphasis: { disabled: true } },
      ],
      graphic: empty ? [noDataGraphic(true)] : [],
    }, true);
  }

  /* ---------------- summary (today / month / days) ---------------- */
  async function loadSummary() {
    try {
      const res = await call('network.summary');
      if (!S.active) return;
      S.summary = res;
      S.asOf = res.asOf || 0;
      S.extra = { rx: 0, tx: 0 };
      renderMeters();
      drawDaily();
    } catch (e) { console.error(e); }
  }

  /* ---------------- first-time-online activity ---------------- */
  async function loadActivity() {
    if (!$('#netActivity')) return;
    try {
      const res = await call('network.activity', { limit: 60 });
      if (!S.active) return;
      S.activity = res;
      renderActivity();
    } catch (e) { console.error(e); }
  }
  function renderActivity() {
    const el = $('#netActivity');
    if (!el || !S.activity) return;
    const a = S.activity;
    const seen = new Set();
    const items = a.events.map((e) => {
      const p = { nid: e.nid, name: e.name || '?', hasIcon: e.hasIcon, descr: e.descr };
      let btn = '';
      // One button per app, on its most recent row.
      if (e.name && !e.protectedApp && !seen.has(e.name.toLowerCase())) {
        seen.add(e.name.toLowerCase());
        const b = isBlocked(e.name);
        btn = `<button class="mini-btn ${b ? '' : 'net-danger'}" data-b="${esc(e.name)}" data-l="${esc(appName(p))}">${b ? t('net.allow') : t('net.block')}</button>`;
      }
      let detail = '';
      if (e.kind === 'first' || e.kind === 'block') detail = e.detail ? `<span class="net-path" title="${esc(e.detail)}">${esc(e.detail)}</span>` : '';
      else if (e.kind.startsWith('cap') && e.detail) {
        const [used, cap] = e.detail.split('/').map(Number);
        detail = `<span class="num">${fmtBytes(used)} / ${fmtBytes(cap)}</span>`;
      }
      const last = e.kind === 'first' && e.lastTs && e.lastTs - e.ts > 60_000 ? ` · ${t('net.lastOnline', { t: fmtTime(e.lastTs) })}` : '';
      return `<div class="net-item">
        <div class="icon-cell">${e.name ? icon(p) : `<span class="ph net-ev-ic">${MONTH_SVG}</span>`}<div>
          <b>${esc(e.name ? appName(p) : t(`net.ev.${e.kind}`))}</b>
          <span>${e.name ? `${esc(t(`net.ev.${e.kind}`))} · ` : ''}${esc(fmtFull(e.ts))}${esc(last)}</span>${detail}
        </div></div>${btn}
      </div>`;
    });
    el.innerHTML = `<div class="net-lead">${a.learning ? t('net.learning') : t('net.known', { n: N(a.known) })}</div>
      ${a.watching ? '' : `<div class="note warn">${t('net.paused')}</div>`}
      ${items.length ? `<div class="net-list">${items.join('')}</div>` : `<div class="empty net-empty">${t('net.noActivity')}</div>`}`;
    $$('[data-b]', el).forEach((b) => b.addEventListener('click', () => setBlocked(b.dataset.b, b.dataset.l, 0)));
  }

  /* ---------------- firewall ---------------- */
  async function loadBlocked(quick) {
    try {
      const res = await call('network.blocked', { quick: !!quick });
      if (!res.loaded) return;
      S.rules = res.rules;
      S.blocked = new Set(res.rules.map((r) => r.name.toLowerCase()));
      if (S.active) { renderBlocked(); renderActivity(); renderUsage(); renderConn(); }
    } catch (e) { console.error(e); }
  }
  function renderBlocked() {
    const el = $('#netBlocked');
    if (!el) return;
    if (!S.rules) { el.innerHTML = '<div class="skeleton" style="height:80px"></div>'; return; }
    el.innerHTML = `${S.rules.length ? `<div class="net-list">${S.rules.map((r) => `<div class="net-item">
        <div class="icon-cell">${icon(r)}<div><b>${esc(appName(r))}</b><span class="net-path" title="${esc(r.path || r.name)}">${esc(r.path || r.name)}</span></div></div>
        <button class="mini-btn" data-u="${esc(r.name)}" data-l="${esc(appName(r))}">${t('net.allow')}</button>
      </div>`).join('')}</div>` : `<div class="empty net-empty">${t('net.noBlocked')}</div>`}
      <div class="note">${t('net.blockedNote')}</div>`;
    $$('[data-u]', el).forEach((b) => b.addEventListener('click', () => setBlocked(b.dataset.u, b.dataset.l, 0)));
  }
  function adminDialog() {
    confirmDialog(t('net.adminTitle'), t('net.adminBody'), t('set.restartAdmin'), false)
      .then((ok) => { if (ok) call('restartAsAdmin').catch((e) => toast(e.message)); });
  }
  /** Block or allow, decided from a fresh rule listing (a menu label may be older than the firewall). */
  async function setBlocked(name, label, pid) {
    if (!state.init?.isAdmin) { adminDialog(); return; }
    await loadBlocked(false);
    const block = !isBlocked(name);
    const ok = await confirmDialog(t(block ? 'net.blockTitle' : 'net.allowTitle', { n: label }), t(block ? 'net.blockBody' : 'net.allowBody'), t(block ? 'net.block' : 'net.allow'), block);
    if (!ok) return;
    const res = await call(block ? 'network.block' : 'network.unblock', { name, pid: pid || 0 });
    if (res?.ok) toast(t(block ? 'net.blockedOk' : 'net.allowedOk', { n: label }));
    else toast(errText(res), 5000);
    await loadBlocked(false);
    if (S.active) loadActivity();
  }
  function blockItem(name, label, pid) {
    const b = isBlocked(name);
    return { label: b ? t('net.allowAccess') : t('net.blockAccess'), cls: b ? '' : 'danger', run: () => setBlocked(name, label, pid) };
  }

  /* ---------------- module settings ---------------- */
  async function loadConfig() {
    try {
      S.cfg = await call('network.config');
      if (S.active) renderNetSettings();
    } catch (e) { console.error(e); }
  }
  function renderNetSettings() {
    const el = $('#netSettings');
    if (!el || !S.cfg) return;
    const c = S.cfg;
    el.innerHTML = `<h3>${t('net.settings')}</h3>
      <div class="set-row"><div class="lbl"><b>${t('net.alertFirst')}</b><span>${t('net.alertFirstHint')}</span></div>
        <label class="switch"><input type="checkbox" id="netAlert" ${c.firstSeenAlerts ? 'checked' : ''}><i></i></label></div>
      <div class="set-row"><div class="lbl"><b>${t('net.cap')}</b><span>${t('net.capHint')}</span></div>
        <div class="net-cap-input"><input class="num-input num" type="text" inputmode="decimal" dir="ltr" id="netCap" value="${c.capGb}"><span class="muted">GB</span></div></div>`;
    const save = async (patch) => {
      try {
        S.cfg = await call('network.setConfig', patch);
        toast(t('net.saved'));
        if (S.active) { renderNetSettings(); loadSummary(); loadActivity(); }
      } catch (e) { toast(e.message); }
    };
    $('#netAlert', el).addEventListener('change', (e) => save({ firstSeenAlerts: e.target.checked }));
    $('#netCap', el).addEventListener('change', (e) => {
      const v = Math.max(0, numVal(e.target.value) || 0);
      if (v !== c.capGb) save({ capGb: v });
    });
  }

  /* ---------------- page lifecycle ---------------- */
  function stop() {
    S.active = false;
    S.timers.forEach(clearInterval);
    S.timers = [];
  }
  async function load() {
    stop();
    S.active = true;
    S.connSig = '';
    build();
    renderMeters();
    drawSparks();
    renderUsage();
    renderBlocked();
    renderActivity();
    renderNetSettings();
    S.timers.push(setInterval(loadConn, 2000));
    S.timers.push(setInterval(() => {
      S.ticks++;
      loadSummary();
      loadActivity();
      if (S.ticks % 4 === 0) loadUsage();
    }, 30000));
    await Promise.all([loadConn(), loadSummary(), loadUsage(), loadActivity(), loadConfig()]);
    if ($('#netBlocked') || S.blocked === null) loadBlocked(false);
  }
  function onLive(l) {
    // Between summary refreshes the totals grow with each live sample the summary does not include yet.
    if (l.ts > S.lastLive) {
      if (S.summary && l.ts > S.asOf) {
        S.extra.rx += (l.netRx ?? 0) * (l.intervalSec ?? 0);
        S.extra.tx += (l.netTx ?? 0) * (l.intervalSec ?? 0);
      }
      S.lastLive = l.ts;
    }
    renderMeters();
    drawSparks();
  }

  registerPage('network', { load, stop, onLive, subtitle: () => t('net.sub') },
    { meters: 'net.meters', connections: 'net.connections', usage: 'net.usage', daily: 'net.dailyShort', activity: 'net.activityShort', blocked: 'net.blocked' });

  // Processes page: block / allow from the right-click menu (always confirmed; decided again from the firewall on click).
  PROC_MENU_EXT.push((sel, app) => {
    const name = sel.name || app?.name;
    if (isProtected(name)) return [];
    const descr = state.procIndex?.get(sel.nid)?.descr || S.conn?.apps?.[name]?.descr;
    return [blockItem(name, appName({ name, descr }), sel.kind === 'proc' ? sel.pid : 0)];
  });
})();
