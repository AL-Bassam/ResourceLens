'use strict';
/* Process control page (Process Lasso-style): per-app rules, ProBalance and power plans. Backend: src/ModRules*.cs */
(() => {
  Object.assign(I18N.ar, {
    'nav.rules': 'التحكم بالعمليات',
    'title.rules': 'قواعد العمليات والتوازن',
    'rl.sub': 'قواعد دائمة لكل برنامج، وتوازن تلقائي يُبقي جهازك سريع الاستجابة',
    'rl.adminNote': 'يعمل ResourceLens الآن بصلاحيات مستخدم عادي: تُطبَّق القواعد وProBalance على برامجك أنت، أما البرامج التي تعمل كمسؤول وخدمات النظام فتحتاج تشغيله كمسؤول.',
    'rl.restartAdmin': 'إعادة التشغيل كمسؤول',
    'rl.search': 'ابحث في القواعد',
    'rl.add': 'إضافة قاعدة',
    'rl.count': '{n} قاعدة · {e} مفعّلة',
    'rl.loading': 'جارٍ التحميل…',
    'rl.empty': 'لا توجد قواعد بعد. أضف قاعدة، أو انقر بالزر الأيمن على أي برنامج في صفحة العمليات.',
    'rl.noMatch': 'لا توجد قاعدة تطابق البحث.',
    'rl.col.app': 'البرنامج', 'rl.col.settings': 'ما تفعله القاعدة', 'rl.col.state': 'الحالة', 'rl.runningN': 'يعمل الآن ({n})',
    'rl.enabled': 'مفعّلة', 'rl.disabled': 'متوقفة',
    'rl.edit': 'تعديل', 'rl.delete': 'حذف', 'rl.applyNow': 'طبّق الآن',
    'rl.chip.prio': 'الأولوية', 'rl.chip.cores': 'الأنوية', 'rl.chip.eco': 'وضع الكفاءة', 'rl.chip.io': 'أولوية القرص',
    'rl.chip.limit': 'أقصى عدد نسخ', 'rl.chip.plan': 'خطة الطاقة', 'rl.chip.block': 'ممنوع من العمل', 'rl.chip.none': 'لا تغيّر شيئاً',
    'rl.on': 'تشغيل', 'rl.off': 'إيقاف',
    'rl.deniedN': 'تعذّر تطبيقها على {n} عملية: تحتاج صلاحيات المسؤول',
    'rl.prio.idle': 'منخفضة', 'rl.prio.below': 'أقل من العادية', 'rl.prio.normal': 'عادية', 'rl.prio.above': 'أعلى من العادية',
    'rl.prio.high': 'مرتفعة', 'rl.prio.realtime': 'الوقت الحقيقي',
    'rl.io.low': 'منخفضة', 'rl.io.normal': 'عادية',
    'rl.deleteTitle': 'حذف قاعدة {n}؟',
    'rl.deleteBody': 'البرامج التي تعمل الآن تبقى على إعداداتها الحالية؛ القاعدة فقط لن تُطبَّق بعد الآن.',
    'rl.deleted': 'حُذفت القاعدة.',
    'rl.hasRule': 'له قاعدة',
    // dialog
    'rl.dlg.add': 'قاعدة جديدة', 'rl.dlg.edit': 'تعديل القاعدة', 'rl.close': 'إغلاق',
    'rl.f.app': 'البرنامج',
    'rl.f.appHint': 'اختر من البرامج العاملة الآن، أو اكتب اسم ملفه (مثل chrome.exe).',
    'rl.f.notRunning': 'لا يعمل الآن، ويمكنك حفظ القاعدة له مع ذلك.',
    'rl.f.noApps': 'لا يوجد برنامج يعمل بهذا الاسم الآن.',
    'rl.f.replaces': 'لهذا البرنامج قاعدة من قبل، والحفظ يستبدلها.',
    'rl.f.isCritical': 'هذه عملية أساسية في ويندوز ولا توضع لها قواعد.',
    'rl.f.block': 'منع البرنامج من العمل',
    'rl.f.blockHint': 'يُغلق البرنامج فور تشغيله في كل مرة، ولا تُستخدم بقية الإعدادات عندها.',
    'rl.f.prio': 'أولوية المعالج',
    'rl.f.prioHint': 'من يحصل على المعالج أولاً حين يزدحم الجهاز: «مرتفعة» للألعاب والبث، و«منخفضة» للمهام الخلفية.',
    'rl.f.cores': 'أنوية المعالج (Affinity)',
    'rl.f.coresHint': 'حصر البرنامج في أنوية بعينها، فيبقى الباقي لغيره.',
    'rl.f.coresPick': 'أنوية محددة',
    'rl.f.coresN': '{n} من {t}',
    'rl.f.coreTip': 'المعالج المنطقي {i} · النواة {c}',
    'rl.f.all': 'الكل', 'rl.f.none': 'لا شيء', 'rl.f.even': 'الزوجية', 'rl.f.odd': 'الفردية',
    'rl.f.pcores': 'أنوية الأداء', 'rl.f.ecores': 'أنوية الكفاءة',
    'rl.f.eco': 'وضع الكفاءة (EcoQoS)', 'rl.f.ecoHint': 'كما في مدير المهام: أقل طاقة، وأولوية منخفضة إن لم تحدد أولوية.',
    'rl.f.io': 'أولوية القرص', 'rl.f.ioHint': '«منخفضة» تجعله يفسح الطريق لغيره عند القراءة والكتابة.',
    'rl.f.limit': 'أقصى عدد من النسخ', 'rl.f.limitHint': 'تُغلق النسخ الزائدة، الأحدث أولاً. المتصفحات تشغّل عمليات كثيرة بالاسم نفسه.',
    'rl.f.plan': 'خطة الطاقة أثناء تشغيله', 'rl.f.planHint': 'تنتقل إليها عند تشغيله، وتعود للخطة السابقة عند إغلاقه.',
    'rl.unchanged': 'بلا تغيير',
    'rl.f.enabled': 'القاعدة مفعّلة',
    'rl.cancel': 'إلغاء', 'rl.save': 'حفظ القاعدة',
    'rl.blockTitle': 'منع {n} من العمل؟',
    'rl.blockBody': 'سيُغلق ResourceLens هذا البرنامج فوراً كلما بدأ، بما في ذلك النسخ التي تعمل الآن، وقد تضيع أي أعمال غير محفوظة فيه.',
    'rl.blockOk': 'منع البرنامج',
    'rl.limitTitle': 'إغلاق النسخ الزائدة من {n}؟',
    'rl.limitBody': 'يعمل منه الآن {r} نسخة، وستُغلق النسخ التي تتجاوز {k} فوراً (الأحدث أولاً)، وقد تضيع أعمال غير محفوظة فيها.',
    'rl.limitOk': 'حفظ وإغلاق الزائد',
    'rl.err.name': 'اكتب اسم ملف البرنامج، مثل chrome.exe.',
    'rl.err.critical': 'هذه عملية أساسية في ويندوز، ولا يضع ResourceLens لها قواعد حفاظاً على استقرار النظام.',
    'rl.err.nokill': 'لا يمكن منع هذا البرنامج أو تحديد عدد نسخه لأن ويندوز يحتاجه، ويمكنك ضبط أولويته وأنويته فقط.',
    'rl.err.cores': 'اختر نواة واحدة على الأقل، أو اجعل الأنوية «بلا تغيير».',
    'rl.err.not-found': 'لم يعد هذا العنصر موجوداً.',
    'rl.err.failed': 'تعذّر تنفيذ العملية.',
    'rl.err.access-denied': 'رفض ويندوز الطلب: يحتاج صلاحيات المسؤول.',
    'rl.saved': 'حُفظت القاعدة، وستُطبَّق على البرنامج كلما بدأ.',
    'rl.savedApplied': 'تم، وطُبّقت الآن على {n} عملية.',
    'rl.savedKilled': 'تم، وأُغلقت {n} عملية.',
    'rl.needAdmin': '{n} عملية تحتاج صلاحيات المسؤول.',
    'rl.quickSaved': 'سيعمل {n} دائماً بأولوية «{p}».',
    'rl.menu.create': 'إنشاء قاعدة لهذا البرنامج…',
    'rl.menu.edit': 'تعديل قاعدة هذا البرنامج…',
    'rl.menu.always': 'دائماً بأولوية: {p}',
    // ProBalance
    'rl.pb.title': 'ProBalance: التوازن التلقائي', 'rl.pb.short': 'ProBalance',
    'rl.pb.desc': 'حين ينشغل المعالج، يخفض مؤقتاً أولوية البرامج الخلفية التي تستهلكه كي يبقى البرنامج الذي أمامك سريع الاستجابة، ثم يعيد لكل برنامج أولويته كما كانت تماماً.',
    'rl.pb.on': 'يعمل', 'rl.pb.offState': 'متوقف',
    'rl.pb.now': 'مقيّدة الآن', 'rl.pb.today': 'تقييدات اليوم', 'rl.pb.session': 'منذ تشغيل البرنامج',
    'rl.pb.sys': 'يبدأ حين يتجاوز استخدام المعالج', 'rl.pb.sysHint': 'للجهاز كله',
    'rl.pb.proc': 'ويقيّد كل برنامج يستهلك أكثر من', 'rl.pb.procHint': 'من قدرة المعالج كلها',
    'rl.pb.to': 'خفض الأولوية إلى',
    'rl.pb.excl': 'استثناءات',
    'rl.pb.exclHint': 'برامج لا يلمسها ProBalance أبداً. البرنامج الذي أمامك والبرامج التي لها قاعدة أولوية مستثناة تلقائياً.',
    'rl.pb.exclNone': 'لا توجد استثناءات.',
    'rl.pb.addExcl': 'إضافة', 'rl.pb.removeExcl': 'إزالة {n}',
    'rl.pb.restoreNote': 'تعود الأولوية حين ينخفض استهلاك البرنامج تحت {h} لعينتين متتاليتين، أو ينخفض استخدام المعالج تحت {v}، أو تنتقل إلى البرنامج.',
    'rl.pb.active': 'المقيّدة الآن',
    'rl.pb.activeRow': 'من {from} إلى {to} · يستهلك {cpu} · منذ {d}',
    'rl.pb.denied': 'تخطّى برامج تحتاج صلاحيات المسؤول: {n}',
    'rl.pb.turnedOn': 'ProBalance يعمل الآن.', 'rl.pb.turnedOff': 'أُوقف ProBalance وعادت الأولويات كما كانت.',
    // power plans
    'rl.pw.title': 'خطة الطاقة',
    'rl.pw.desc': 'الخطة النشطة الآن، ويمكنك التبديل بينها فوراً. ويمكن لأي قاعدة أن تنقلك إلى خطة ما دام برنامجها يعمل.',
    'rl.pw.active': 'نشطة', 'rl.pw.switch': 'تفعيل',
    'rl.pw.none': 'تعذّرت قراءة خطط الطاقة.',
    'rl.pw.byRule': 'انتقلت الخطة لأن {n} يعمل، وستعود إلى «{p}» عند إغلاقه.',
    'rl.pw.switched': 'الخطة النشطة الآن: {p}',
    'rl.pw.rules': 'قواعد تغيّر الخطة', 'rl.pw.ruleRow': 'ينتقل إلى «{p}» ما دام يعمل',
    'rl.pw.noRules': 'لا توجد قاعدة تغيّر خطة الطاقة. اختر «خطة الطاقة أثناء تشغيله» في أي قاعدة، مثلاً «أداء عالٍ» للعبة.',
    'rl.pw.k.balanced': 'يوازن بين الأداء واستهلاك الطاقة', 'rl.pw.k.high': 'أداء أعلى مقابل طاقة أكثر',
    'rl.pw.k.saver': 'يوفّر الطاقة ويطيل عمر البطارية', 'rl.pw.k.ultimate': 'أقصى أداء بلا أي توفير', 'rl.pw.k.custom': 'خطة مخصّصة',
    'rl.pw.n.balanced': 'متوازن', 'rl.pw.n.high': 'أداء عالٍ', 'rl.pw.n.saver': 'موفّر الطاقة', 'rl.pw.n.ultimate': 'الأداء الأقصى',
    // activity
    'rl.act.title': 'سجل النشاط',
    'rl.act.all': 'الكل', 'rl.act.balance': 'ProBalance', 'rl.act.rules': 'القواعد', 'rl.act.power': 'الطاقة',
    'rl.act.clear': 'مسح السجل', 'rl.act.clearTitle': 'مسح سجل النشاط؟',
    'rl.act.clearBody': 'تُحذف كل أسطر السجل. القواعد والإعدادات لا تتأثر.',
    'rl.act.empty': 'لا يوجد نشاط بعد.',
    'rl.ev.restrain': 'خُفضت أولوية {n} من {from} إلى {to}، إذ كان يستهلك {cpu} والمعالج مشغول {sys}.',
    'rl.ev.restore': 'عادت أولوية {n} إلى {to} بعد {d}: {why}.',
    'rl.why.calm': 'هدأ استهلاكه', 'rl.why.idle': 'هدأ المعالج', 'rl.why.fg': 'انتقلت إليه', 'rl.why.off': 'أُوقف ProBalance',
    'rl.why.exit': 'أُغلق ResourceLens', 'rl.why.excluded': 'أُضيف إلى الاستثناءات', 'rl.why.changed': 'غيّرها برنامج آخر فتُركت كما هي',
    'rl.ev.rule': 'طُبّقت القاعدة على {n} ({k} عملية).',
    'rl.ev.denied': 'تعذّر تطبيق القاعدة على {n}: {k} عملية تحتاج صلاحيات المسؤول.',
    'rl.ev.failed': 'تعذّر تطبيق القاعدة على {n} ({k} عملية).',
    'rl.ev.kill': 'أُغلق {n} لأنه ممنوع من العمل ({k} عملية).',
    'rl.ev.limit': 'أُغلقت {k} نسخة زائدة من {n} (الحد {l}).',
    'rl.ev.plan': 'انتقلت خطة الطاقة إلى «{p}» لأن {n} يعمل.',
    'rl.ev.planFail': 'تعذّر نقل خطة الطاقة إلى «{p}» لأجل {n}.',
    'rl.ev.planRestore': 'عادت خطة الطاقة إلى «{p}» بعد إغلاق {n}.',
    'rl.ev.planRestoreAny': 'عادت خطة الطاقة إلى «{p}».',
    'rl.ev.pbOn': 'شُغّل ProBalance.', 'rl.ev.pbOff': 'أُوقف ProBalance.',
  });

  Object.assign(I18N.en, {
    'nav.rules': 'Process control',
    'title.rules': 'Process rules & balancing',
    'rl.sub': 'Lasting rules per app, and automatic balancing that keeps your PC responsive',
    'rl.adminNote': 'ResourceLens is running as a standard user: rules and ProBalance apply to your own apps. Apps running as administrator and system services need ResourceLens to run as administrator.',
    'rl.restartAdmin': 'Restart as administrator',
    'rl.search': 'Search rules',
    'rl.add': 'Add rule',
    'rl.count': '{n} rules · {e} on',
    'rl.loading': 'Loading…',
    'rl.empty': 'No rules yet. Add one, or right-click any app on the Processes page.',
    'rl.noMatch': 'No rule matches the search.',
    'rl.col.app': 'App', 'rl.col.settings': 'What the rule does', 'rl.col.state': 'Status', 'rl.runningN': 'running now ({n})',
    'rl.enabled': 'On', 'rl.disabled': 'Off',
    'rl.edit': 'Edit', 'rl.delete': 'Delete', 'rl.applyNow': 'Apply now',
    'rl.chip.prio': 'Priority', 'rl.chip.cores': 'Cores', 'rl.chip.eco': 'Efficiency mode', 'rl.chip.io': 'Disk priority',
    'rl.chip.limit': 'Max instances', 'rl.chip.plan': 'Power plan', 'rl.chip.block': 'Not allowed to run', 'rl.chip.none': 'Changes nothing',
    'rl.on': 'On', 'rl.off': 'Off',
    'rl.deniedN': "Couldn't apply to {n} processes: they need administrator rights",
    'rl.prio.idle': 'Low', 'rl.prio.below': 'Below normal', 'rl.prio.normal': 'Normal', 'rl.prio.above': 'Above normal',
    'rl.prio.high': 'High', 'rl.prio.realtime': 'Realtime',
    'rl.io.low': 'Low', 'rl.io.normal': 'Normal',
    'rl.deleteTitle': 'Delete the rule for {n}?',
    'rl.deleteBody': 'Processes running now keep their current settings; the rule just stops being applied.',
    'rl.deleted': 'Rule deleted.',
    'rl.hasRule': 'Has a rule',
    'rl.dlg.add': 'New rule', 'rl.dlg.edit': 'Edit rule', 'rl.close': 'Close',
    'rl.f.app': 'App',
    'rl.f.appHint': 'Pick one of the apps running now, or type its file name (like chrome.exe).',
    'rl.f.notRunning': "Not running right now — you can still save the rule for it.",
    'rl.f.noApps': 'No running app matches that name.',
    'rl.f.replaces': 'This app already has a rule; saving replaces it.',
    'rl.f.isCritical': "This is a core Windows process; it can't have rules.",
    'rl.f.block': "Don't allow this app to run",
    'rl.f.blockHint': 'Closes the app every time it starts. The other settings are not used then.',
    'rl.f.prio': 'CPU priority',
    'rl.f.prioHint': 'Who gets the processor first when the PC is busy: "High" for games and streaming, "Low" for background work.',
    'rl.f.cores': 'CPU cores (affinity)',
    'rl.f.coresHint': 'Keep the app on chosen cores and leave the rest for everything else.',
    'rl.f.coresPick': 'Chosen cores',
    'rl.f.coresN': '{n} of {t}',
    'rl.f.coreTip': 'Logical processor {i} · core {c}',
    'rl.f.all': 'All', 'rl.f.none': 'None', 'rl.f.even': 'Even', 'rl.f.odd': 'Odd',
    'rl.f.pcores': 'Performance cores', 'rl.f.ecores': 'Efficiency cores',
    'rl.f.eco': 'Efficiency mode (EcoQoS)', 'rl.f.ecoHint': 'Like Task Manager: least power, and low priority unless you pick one.',
    'rl.f.io': 'Disk priority', 'rl.f.ioHint': '"Low" makes it give way to other apps when reading and writing.',
    'rl.f.limit': 'Maximum instances', 'rl.f.limitHint': 'Extra instances are closed, newest first. Browsers run many processes with the same name.',
    'rl.f.plan': 'Power plan while it runs', 'rl.f.planHint': 'Switches to it when the app starts and back to the previous plan when it closes.',
    'rl.unchanged': 'Unchanged',
    'rl.f.enabled': 'Rule is on',
    'rl.cancel': 'Cancel', 'rl.save': 'Save rule',
    'rl.blockTitle': 'Stop {n} from running?',
    'rl.blockBody': 'ResourceLens will close this app as soon as it starts, including the copies running now. Unsaved work in it may be lost.',
    'rl.blockOk': 'Block the app',
    'rl.limitTitle': 'Close the extra instances of {n}?',
    'rl.limitBody': '{r} instances are running now; the ones beyond {k} will be closed right away (newest first). Unsaved work in them may be lost.',
    'rl.limitOk': 'Save and close extras',
    'rl.err.name': 'Type the app’s file name, like chrome.exe.',
    'rl.err.critical': 'This is a core Windows process; ResourceLens never puts rules on those, to keep Windows stable.',
    'rl.err.nokill': "Windows needs this app, so it can't be blocked or limited — you can still set its priority and cores.",
    'rl.err.cores': 'Pick at least one core, or set cores to "Unchanged".',
    'rl.err.not-found': 'This item no longer exists.',
    'rl.err.failed': 'The operation failed.',
    'rl.err.access-denied': 'Windows refused: this needs administrator rights.',
    'rl.saved': 'Rule saved; it applies every time the app starts.',
    'rl.savedApplied': 'Done — applied to {n} running processes now.',
    'rl.savedKilled': 'Done — {n} processes were closed.',
    'rl.needAdmin': '{n} processes need administrator rights.',
    'rl.quickSaved': '{n} will always run at "{p}" priority.',
    'rl.menu.create': 'Create rule for this app…',
    'rl.menu.edit': 'Edit the rule for this app…',
    'rl.menu.always': 'Always run at: {p}',
    'rl.pb.title': 'ProBalance: automatic balancing', 'rl.pb.short': 'ProBalance',
    'rl.pb.desc': 'When the CPU is busy, background apps that eat it get a lower priority for a while, so the app in front of you stays responsive. Each one gets its exact original priority back afterwards.',
    'rl.pb.on': 'On', 'rl.pb.offState': 'Off',
    'rl.pb.now': 'Restrained now', 'rl.pb.today': 'Restraints today', 'rl.pb.session': 'Since ResourceLens started',
    'rl.pb.sys': 'Starts when CPU use is above', 'rl.pb.sysHint': 'For the whole PC',
    'rl.pb.proc': 'And restrains every app using more than', 'rl.pb.procHint': 'Of the whole CPU',
    'rl.pb.to': 'Lower the priority to',
    'rl.pb.excl': 'Exclusions',
    'rl.pb.exclHint': 'Apps ProBalance never touches. The app in front of you and apps with a priority rule are always left alone.',
    'rl.pb.exclNone': 'No exclusions.',
    'rl.pb.addExcl': 'Add', 'rl.pb.removeExcl': 'Remove {n}',
    'rl.pb.restoreNote': 'The priority comes back when the app drops below {h} for two samples in a row, when CPU use falls below {v}, or when you switch to the app.',
    'rl.pb.active': 'Restrained now',
    'rl.pb.activeRow': '{from} → {to} · using {cpu} · for {d}',
    'rl.pb.denied': 'Skipped apps that need administrator rights: {n}',
    'rl.pb.turnedOn': 'ProBalance is on.', 'rl.pb.turnedOff': 'ProBalance is off; priorities are back to what they were.',
    'rl.pw.title': 'Power plan',
    'rl.pw.desc': 'The plan in use now — switch with one click. Any rule can also switch the plan while its app runs.',
    'rl.pw.active': 'Active', 'rl.pw.switch': 'Use',
    'rl.pw.none': "Couldn't read the power plans.",
    'rl.pw.byRule': 'Switched because {n} is running; goes back to "{p}" when it closes.',
    'rl.pw.switched': 'Power plan in use: {p}',
    'rl.pw.rules': 'Rules that change the plan', 'rl.pw.ruleRow': 'Switches to "{p}" while it runs',
    'rl.pw.noRules': 'No rule changes the power plan. Pick "Power plan while it runs" in any rule — for example "High performance" for a game.',
    'rl.pw.k.balanced': 'Balances performance and energy use', 'rl.pw.k.high': 'More performance for more power',
    'rl.pw.k.saver': 'Saves energy, longer battery life', 'rl.pw.k.ultimate': 'Maximum performance, no savings', 'rl.pw.k.custom': 'Custom plan',
    'rl.pw.n.balanced': 'Balanced', 'rl.pw.n.high': 'High performance', 'rl.pw.n.saver': 'Power saver', 'rl.pw.n.ultimate': 'Ultimate performance',
    'rl.act.title': 'Activity',
    'rl.act.all': 'All', 'rl.act.balance': 'ProBalance', 'rl.act.rules': 'Rules', 'rl.act.power': 'Power',
    'rl.act.clear': 'Clear log', 'rl.act.clearTitle': 'Clear the activity log?',
    'rl.act.clearBody': 'Every line of the log is deleted. Rules and settings are not affected.',
    'rl.act.empty': 'No activity yet.',
    'rl.ev.restrain': 'Lowered {n} from {from} to {to} — it was using {cpu} while the CPU was at {sys}.',
    'rl.ev.restore': 'Gave {n} its {to} priority back after {d}: {why}.',
    'rl.why.calm': 'it calmed down', 'rl.why.idle': 'the CPU calmed down', 'rl.why.fg': 'you switched to it', 'rl.why.off': 'ProBalance was turned off',
    'rl.why.exit': 'ResourceLens closed', 'rl.why.excluded': 'it was excluded', 'rl.why.changed': 'another app had changed it, so it was left alone',
    'rl.ev.rule': 'Rule applied to {n} ({k} processes).',
    'rl.ev.denied': "Couldn't apply the rule to {n}: {k} processes need administrator rights.",
    'rl.ev.failed': "Couldn't apply the rule to {n} ({k} processes).",
    'rl.ev.kill': 'Closed {n} — it is not allowed to run ({k} processes).',
    'rl.ev.limit': 'Closed {k} extra instances of {n} (limit {l}).',
    'rl.ev.plan': 'Power plan switched to "{p}" because {n} is running.',
    'rl.ev.planFail': 'Couldn’t switch the power plan to "{p}" for {n}.',
    'rl.ev.planRestore': 'Power plan back to "{p}" after {n} closed.',
    'rl.ev.planRestoreAny': 'Power plan back to "{p}".',
    'rl.ev.pbOn': 'ProBalance turned on.', 'rl.ev.pbOff': 'ProBalance turned off.',
  });

  const PRIOS = ['idle', 'below', 'normal', 'above', 'high'];
  const KNOWN_PLANS = {
    '381b4222-f694-41f0-9685-ff5bb260df2e': 'balanced',
    '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c': 'high',
    'a1841308-3541-4fab-bc81-f71556f20b4a': 'saver',
    'e9a42b02-d5df-448d-aa00-03f14749eb61': 'ultimate',
  };
  const GROUP = { restrain: 'balance', restore: 'balance', pbOn: 'balance', pbOff: 'balance', rule: 'rules', denied: 'warn', kill: 'bad', limit: 'bad', plan: 'power', planRestore: 'power' };
  const SVG = {
    search: '<svg viewBox="0 0 24 24"><circle cx="11" cy="11" r="7" fill="none" stroke="currentColor" stroke-width="2"/><path d="m20 20-3.5-3.5" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>',
    plus: '<svg viewBox="0 0 24 24"><path d="M12 5v14M5 12h14" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"/></svg>',
    close: '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6 6 18" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>',
    bolt: '<svg viewBox="0 0 24 24"><path d="M13 2 4 14h7l-1 8 9-12h-7z" fill="none" stroke="currentColor" stroke-width="2" stroke-linejoin="round"/></svg>',
    balance: '<svg viewBox="0 0 24 24"><path d="M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"/><circle cx="16" cy="6" r="2" fill="none" stroke="currentColor" stroke-width="2"/><circle cx="10" cy="12" r="2" fill="none" stroke="currentColor" stroke-width="2"/><circle cx="18" cy="18" r="2" fill="none" stroke="currentColor" stroke-width="2"/></svg>',
  };

  const st = {
    rules: null, sig: '', excluded: [], noKill: [], pb: null, pbSig: '', plans: null, events: [], evSig: '', cores: null,
    apps: [], group: 'all', q: '', timer: 0, lastPoll: 0, tick: 0, busy: false, pending: null, dlg: null, menuFetch: false,
  };
  const lc = (s) => String(s ?? '').toLowerCase();
  const bare = (n) => String(n || '').replace(/\.exe$/i, '');
  const isAdmin = () => !!state.init?.isAdmin;
  const errText = (res) => t(`rl.err.${res?.error || 'failed'}`);
  const appLabel = (x) => x.descr || bare(x.name);
  const appIcon = (x) => icon({ nid: x.nid, name: x.name || '?', hasIcon: x.hasIcon });
  const listSep = () => (lang() === 'ar' ? '، ' : ', ');

  /** "Chrome", "chrome.exe" or a full path -> the exe name the backend matches (same as NormName in ModRules.cs). */
  function normName(raw) {
    const n = String(raw || '').trim().replace(/^"+|"+$/g, '').split(/[\\/]/).pop().trim();
    if (!n) return '';
    return n.includes('.') ? n : `${n}.exe`;
  }
  const ruleFor = (name) => (st.rules || []).find((r) => lc(r.name) === lc(normName(name)));
  const isCritical = (name) => { const b = lc(bare(normName(name))); return st.excluded.some((x) => lc(x) === b); };
  const runningCount = (name) => st.apps.find((a) => lc(a.name) === lc(name))?.count
    ?? (st.rules || []).find((r) => lc(r.name) === lc(name))?.running ?? 0;

  function planName(guid) {
    const p = st.plans?.plans?.find((x) => lc(x.guid) === lc(guid));
    if (p) return p.name;
    const k = KNOWN_PLANS[lc(guid)];
    return k ? t(`rl.pw.n.${k}`) : String(guid || '').slice(0, 8);
  }
  function coreText(cores) {
    const total = st.cores?.count || 0;
    if (total && cores.length === total) return t('rl.f.all');
    const out = [];
    let a = null, b = null;
    for (const i of [...cores].sort((x, y) => x - y)) {
      if (a !== null && i === b + 1) { b = i; continue; }
      if (a !== null) out.push(a === b ? `${a}` : `${a}–${b}`);
      a = b = i;
    }
    if (a !== null) out.push(a === b ? `${a}` : `${a}–${b}`);
    return out.join(', ');
  }
  function resultText(res) {
    const parts = [res.killed ? t('rl.savedKilled', { n: res.killed }) : res.applied ? t('rl.savedApplied', { n: res.applied }) : t('rl.saved')];
    if (res.denied) parts.push(t('rl.needAdmin', { n: res.denied }));
    return parts.join(' ');
  }

  /* ================= data ================= */
  function setRules(res) {
    st.excluded = res.excluded || [];
    st.noKill = res.noKill || [];
    const sig = JSON.stringify(res.rules);
    if (st.rules && sig === st.sig) return false;
    st.sig = sig;
    st.rules = res.rules;
    return true;
  }
  const evParams = () => ({ limit: 200, group: st.group === 'all' ? null : st.group });
  const evSigOf = (list) => `${st.group}:${list.length}:${list[0]?.id ?? 0}:${lang()}`;
  function setEvents(list) {
    const sig = evSigOf(list);
    if (sig === st.evSig) return;
    st.evSig = sig;
    st.events = list;
    renderEventList();
  }
  async function refresh() {
    const [rules, events] = await Promise.all([call('rules.list'), call('rules.events', evParams())]);
    if (setRules(rules)) { renderRules(); renderPlans(); }
    setEvents(events);
  }

  async function load() {
    renderShell();
    clearInterval(st.timer);
    st.timer = setInterval(poll, 10000); // fallback; onLive polls right after each sample
    st.lastPoll = Date.now();
    const [rules, pb, plans, events, cores] = await Promise.all([
      call('rules.list'), call('rules.probalance.get'), call('rules.powerPlans'), call('rules.events', evParams()),
      st.cores || call('rules.cores'),
    ]);
    if (state.page !== 'rules') return;
    st.cores = cores;
    st.plans = plans;
    st.pb = pb;
    st.pbSig = JSON.stringify(pb);
    st.sig = '';
    setRules(rules);
    st.events = events;
    st.evSig = evSigOf(events);
    renderRules(); renderPb(); renderPlans(); renderEvents();
    if (st.dlg) renderDialog();
    if (st.pending) { const n = st.pending; st.pending = null; openDialog(n); }
  }
  function stop() {
    clearInterval(st.timer);
    st.timer = 0;
    closeDialog();
  }
  function onLive() {
    if (Date.now() - st.lastPoll > 2500) poll();
  }
  async function poll() {
    if (state.page !== 'rules' || st.busy) return;
    st.busy = true;
    st.lastPoll = Date.now();
    st.tick++;
    try {
      const [rules, pb, events, plans] = await Promise.all([
        call('rules.list'), call('rules.probalance.get'), call('rules.events', evParams()), st.tick % 3 ? null : call('rules.powerPlans'),
      ]);
      if (state.page !== 'rules') return;
      if (setRules(rules)) { renderRules(); renderPlans(); }
      const pbSig = JSON.stringify(pb);
      if (pbSig !== st.pbSig) {
        const cfgChanged = JSON.stringify(pb.config) !== JSON.stringify(st.pb?.config);
        st.pb = pb;
        st.pbSig = pbSig;
        if (cfgChanged) renderPb(); else renderPbStatus();
      }
      setEvents(events);
      if (plans && JSON.stringify(plans) !== JSON.stringify(st.plans)) { st.plans = plans; renderPlans(); }
    } catch (e) { console.error(e); } finally { st.busy = false; }
  }

  /* ================= page ================= */
  function renderShell() {
    const pb = shown('rules', 'balance'), pw = shown('rules', 'power');
    $('#page-rules').innerHTML = `
      ${isAdmin() ? '' : `<div class="note warn rl-admin"><span>${t('rl.adminNote')}</span><button class="btn" id="rlAdminBtn">${t('rl.restartAdmin')}</button></div>`}
      <div class="toolbar">
        <div class="rl-tools">
          <div class="search">${SVG.search}<input id="rlSearch" placeholder="${esc(t('rl.search'))}" value="${esc(st.q)}"></div>
          <span class="toolbar-note" id="rlCount"></span>
        </div>
        <button class="btn primary" id="rlAdd">${SVG.plus}<span>${t('rl.add')}</span></button>
      </div>
      <div class="card flush"><div class="table-wrap rl-table" id="rlTable"><div class="empty">${t('rl.loading')}</div></div></div>
      ${pb || pw ? `<div class="rl-grid">${pb ? '<div class="card rl-card" id="rlPb"></div>' : ''}${pw ? '<div class="card rl-card" id="rlPower"></div>' : ''}</div>` : ''}
      ${shown('rules', 'activity') ? '<div class="card flush" id="rlAct"></div>' : ''}`;
    $('#rlAdminBtn')?.addEventListener('click', () => call('restartAsAdmin'));
    $('#rlSearch').addEventListener('input', (e) => { st.q = e.target.value; renderRules(); });
    $('#rlAdd').addEventListener('click', () => openDialog(null));
  }

  function chips(r) {
    if (r.disallowed) return `<span class="rl-chip bad">${t('rl.chip.block')}</span>`;
    const chip = (k, v) => `<span class="rl-chip"><i>${k}</i><b>${v}</b></span>`;
    const c = [];
    if (r.priority) c.push(chip(t('rl.chip.prio'), t(`rl.prio.${r.priority}`)));
    if (r.cores) c.push(chip(t('rl.chip.cores'), `<span class="num">${esc(coreText(r.cores))}</span>`));
    if (r.efficiency != null) c.push(chip(t('rl.chip.eco'), r.efficiency ? t('rl.on') : t('rl.off')));
    if (r.io) c.push(chip(t('rl.chip.io'), t(`rl.io.${r.io}`)));
    if (r.limit) c.push(chip(t('rl.chip.limit'), N(r.limit)));
    if (r.powerPlan) c.push(chip(t('rl.chip.plan'), esc(planName(r.powerPlan))));
    return c.join('') || `<span class="muted">${t('rl.chip.none')}</span>`;
  }

  function renderRules() {
    const host = $('#rlTable');
    if (!host || !st.rules) return;
    const rules = st.rules;
    $('#rlCount').innerHTML = t('rl.count', { n: N(rules.length), e: N(rules.filter((r) => r.enabled).length) });
    const q = lc(st.q.trim());
    const rows = rules.filter((r) => !q || lc(r.name).includes(q) || lc(r.descr).includes(q));
    if (!rows.length) { host.innerHTML = `<div class="empty">${rules.length ? t('rl.noMatch') : t('rl.empty')}</div>`; return; }
    const scroll = host.scrollTop;
    host.innerHTML = `<table><thead><tr><th>${t('rl.col.app')}</th><th>${t('rl.col.settings')}</th><th>${t('rl.col.state')}</th><th></th></tr></thead><tbody>
      ${rows.map((r) => `<tr data-id="${esc(r.id)}" class="${r.enabled ? '' : 'rl-off'}">
        <td class="rl-app-cell"><div class="icon-cell">${appIcon(r)}<div><b dir="auto">${esc(appLabel(r))}</b><span><span class="num">${esc(r.name)}</span>${r.running ? ` · <span class="rl-run">${t('rl.runningN', { n: N(r.running) })}</span>` : ''}</span></div></div></td>
        <td class="rl-sum"><div class="rl-chips">${chips(r)}</div>${r.stats?.denied ? `<div class="rl-warn">${t('rl.deniedN', { n: N(r.stats.denied) })}</div>` : ''}</td>
        <td><div class="rl-state"><label class="switch" title="${esc(r.enabled ? t('rl.enabled') : t('rl.disabled'))}"><input type="checkbox" data-en ${r.enabled ? 'checked' : ''}><i></i></label><span>${r.enabled ? t('rl.enabled') : t('rl.disabled')}</span></div></td>
        <td><div class="row-btns">
          <button class="mini-btn" data-act="apply" ${r.enabled && r.running ? '' : 'disabled'}>${t('rl.applyNow')}</button>
          <button class="mini-btn" data-act="edit">${t('rl.edit')}</button>
          <button class="mini-btn rl-danger" data-act="del">${t('rl.delete')}</button>
        </div></td></tr>`).join('')}
    </tbody></table>`;
    host.scrollTop = scroll;
    $$('tbody tr', host).forEach((tr) => {
      const r = rules.find((x) => x.id === tr.dataset.id);
      $('[data-en]', tr).addEventListener('change', (e) => setEnabled(r, e.target));
      $('[data-act="edit"]', tr).addEventListener('click', () => openDialog(r));
      $('[data-act="del"]', tr).addEventListener('click', () => removeRule(r));
      $('[data-act="apply"]', tr).addEventListener('click', (e) => applyNow(r, e.currentTarget));
      tr.addEventListener('dblclick', (e) => { if (!e.target.closest('button, label, input')) openDialog(r); });
    });
  }

  /** Closing apps (blocked / over the limit) always asks first. */
  async function confirmKills(r, running) {
    if (!r.enabled) return true;
    if (r.disallowed) return confirmDialog(t('rl.blockTitle', { n: appLabel(r) }), t('rl.blockBody'), t('rl.blockOk'));
    if (r.limit && running > r.limit) return confirmDialog(t('rl.limitTitle', { n: appLabel(r) }), t('rl.limitBody', { r: running, k: r.limit }), t('rl.limitOk'));
    return true;
  }
  async function setEnabled(r, el) {
    const on = el.checked;
    if (on && !await confirmKills({ ...r, enabled: true }, r.running)) { el.checked = false; return; }
    el.disabled = true;
    const res = await call('rules.setEnabled', { id: r.id, enabled: on });
    if (!res.ok) { toast(errText(res), 4000); el.checked = !on; el.disabled = false; return; }
    if (on && res.matched) toast(resultText(res), 4000);
    await refresh();
  }
  async function applyNow(r, btn) {
    if (!await confirmKills(r, r.running)) return;
    btn.disabled = true;
    const res = await call('rules.apply', { id: r.id });
    toast(res.ok ? resultText(res) : errText(res), 4000);
    await refresh();
  }
  async function removeRule(r) {
    if (!await confirmDialog(t('rl.deleteTitle', { n: appLabel(r) }), t('rl.deleteBody'), t('rl.delete'))) return;
    const res = await call('rules.delete', { id: r.id });
    toast(res.ok ? t('rl.deleted') : errText(res), 3500);
    await refresh();
  }

  /* ---- ProBalance ---- */
  const range = (key, v, min, max) => `<div class="rl-range"><input type="range" min="${min}" max="${max}" step="1" value="${v}" data-pb="${key}"><b class="num">${fmtPct(v)}</b></div>`;
  function renderPb() {
    const host = $('#rlPb');
    if (!host || !st.pb) return;
    const c = st.pb.config;
    const names = [...new Set((state.live?.processes || []).map((p) => p.name))].sort((a, b) => a.localeCompare(b));
    host.innerHTML = `
      <div class="rl-head">
        <div class="rl-head-tx"><h3>${t('rl.pb.title')}</h3><div class="desc">${t('rl.pb.desc')}</div></div>
        <div class="rl-toggle"><span class="${c.enabled ? 'on' : ''}">${c.enabled ? t('rl.pb.on') : t('rl.pb.offState')}</span><label class="switch"><input type="checkbox" id="rlPbOn" ${c.enabled ? 'checked' : ''}><i></i></label></div>
      </div>
      <div class="mini-kpis rl-kpis" id="rlPbKpis"></div>
      <div class="set-row"><div class="lbl"><b>${t('rl.pb.sys')}</b><span>${t('rl.pb.sysHint')}</span></div>${range('systemThreshold', c.systemThreshold, 50, 100)}</div>
      <div class="set-row"><div class="lbl"><b>${t('rl.pb.proc')}</b><span>${t('rl.pb.procHint')}</span></div>${range('processThreshold', c.processThreshold, 2, 50)}</div>
      <div class="set-row"><div class="lbl"><b>${t('rl.pb.to')}</b></div><div class="seg" id="rlPbTo">${['below', 'idle'].map((v) => `<button data-v="${v}" class="${c.restrain === v ? 'active' : ''}">${t(`rl.prio.${v}`)}</button>`).join('')}</div></div>
      <div class="set-row rl-stack">
        <div class="lbl"><b>${t('rl.pb.excl')}</b><span>${t('rl.pb.exclHint')}</span></div>
        <div class="rl-excl">${c.exclusions.length ? c.exclusions.map((n) => `<span class="rl-tag"><span class="num">${esc(n)}</span><button data-rm="${esc(n)}" title="${esc(t('rl.pb.removeExcl', { n }))}" aria-label="${esc(t('rl.pb.removeExcl', { n }))}">${SVG.close}</button></span>`).join('') : `<span class="muted">${t('rl.pb.exclNone')}</span>`}</div>
        <div class="rl-add-row"><input class="input" id="rlExclIn" list="rlExclList" placeholder="game.exe" dir="ltr" spellcheck="false" autocomplete="off"><button class="btn" id="rlExclAdd">${t('rl.pb.addExcl')}</button></div>
        <datalist id="rlExclList">${names.map((n) => `<option value="${esc(n)}"></option>`).join('')}</datalist>
      </div>
      <div class="note">${t('rl.pb.restoreNote', { h: N(fmtPct(c.processThreshold / 2)), v: N(fmtPct(c.systemThreshold - 15)) })}</div>
      <div id="rlPbActive"></div>`;
    renderPbStatus();
    $('#rlPbOn').addEventListener('change', (e) => savePb({ enabled: e.target.checked }));
    $$('[data-pb]', host).forEach((el) => {
      el.addEventListener('input', () => { el.nextElementSibling.textContent = fmtPct(+el.value); });
      el.addEventListener('change', () => savePb({ [el.dataset.pb]: +el.value }));
    });
    $$('#rlPbTo button', host).forEach((b) => b.addEventListener('click', () => { if (b.dataset.v !== c.restrain) savePb({ restrain: b.dataset.v }); }));
    $$('[data-rm]', host).forEach((b) => b.addEventListener('click', () => savePb({ exclusions: c.exclusions.filter((x) => x !== b.dataset.rm) })));
    const add = () => {
      const n = normName($('#rlExclIn').value);
      if (!n) { $('#rlExclIn').focus(); return; }
      if (c.exclusions.some((x) => lc(x) === lc(n))) { $('#rlExclIn').value = ''; return; }
      savePb({ exclusions: [...c.exclusions, n] }, true);
    };
    $('#rlExclAdd').addEventListener('click', add);
    $('#rlExclIn').addEventListener('keydown', (e) => { if (e.key === 'Enter') add(); });
  }
  function renderPbStatus() {
    const pb = st.pb;
    const k = $('#rlPbKpis');
    if (!pb || !k) return;
    k.innerHTML = [['rl.pb.now', pb.active.length], ['rl.pb.today', pb.today], ['rl.pb.session', pb.session]]
      .map(([l, v]) => `<div class="mini"><span>${t(l)}</span><b class="tnum">${fmtNum(v, 0)}</b></div>`).join('');
    $('#rlPbActive').innerHTML = (pb.active.length ? `<div class="rl-sub">${t('rl.pb.active')}</div><div class="rl-list">${pb.active.map((e) => `<div class="rl-li">${appIcon(e)}<div class="rl-li-nm"><b dir="auto">${esc(appLabel(e))} <span class="muted num">PID ${e.pid}</span></b><span>${t('rl.pb.activeRow', { from: t(`rl.prio.${e.from}`), to: t(`rl.prio.${e.to}`), cpu: N(fmtPct(e.cpu)), d: fmtDur(Date.now() - e.since) })}</span></div></div>`).join('')}</div>` : '')
      + (pb.denied.length ? `<div class="note warn">${t('rl.pb.denied', { n: `<span class="num">${esc(pb.denied.join(listSep()))}</span>` })}</div>` : '');
  }
  async function savePb(patch, refocus) {
    const was = st.pb.config.enabled;
    const res = await call('rules.probalance.set', { config: { ...st.pb.config, ...patch } });
    st.pb = res;
    st.pbSig = JSON.stringify(res);
    renderPb();
    if (refocus) $('#rlExclIn')?.focus();
    if (was !== res.config.enabled) toast(res.config.enabled ? t('rl.pb.turnedOn') : t('rl.pb.turnedOff'), 3500);
    setEvents(await call('rules.events', evParams()));
  }

  /* ---- power plans ---- */
  function renderPlans() {
    const host = $('#rlPower');
    if (!host) return;
    const p = st.plans;
    const list = p?.plans || [];
    const planRules = (st.rules || []).filter((r) => r.powerPlan && !r.disallowed);
    host.innerHTML = `
      <div class="rl-head"><div class="rl-head-tx"><h3>${t('rl.pw.title')}</h3><div class="desc">${t('rl.pw.desc')}</div></div></div>
      ${list.length ? `<div class="rl-plans">${list.map((x) => `<div class="rl-plan ${x.active ? 'active' : ''}">
          <span class="rl-plan-ic">${SVG.bolt}</span>
          <div class="rl-plan-nm"><b>${esc(x.name)}</b><span>${t(`rl.pw.k.${KNOWN_PLANS[lc(x.guid)] || 'custom'}`)}</span></div>
          ${x.active ? `<span class="badge ok">${t('rl.pw.active')}</span>` : `<button class="mini-btn" data-plan="${esc(x.guid)}">${t('rl.pw.switch')}</button>`}
        </div>`).join('')}</div>` : `<div class="empty">${t('rl.pw.none')}</div>`}
      ${p?.byRule ? `<div class="note">${t('rl.pw.byRule', { n: `<b>${esc(bare(p.byRule))}</b>`, p: esc(p.restoreName || '—') })}</div>` : ''}
      <div class="rl-sub">${t('rl.pw.rules')}</div>
      ${planRules.length ? `<div class="rl-list">${planRules.map((r) => `<div class="rl-li ${r.enabled ? '' : 'rl-dim'}">${appIcon(r)}<div class="rl-li-nm"><b dir="auto">${esc(appLabel(r))}</b><span>${t('rl.pw.ruleRow', { p: esc(planName(r.powerPlan)) })}${r.enabled ? '' : ` · ${t('rl.disabled')}`}</span></div><button class="mini-btn" data-edit="${esc(r.id)}">${t('rl.edit')}</button></div>`).join('')}</div>`
        : `<div class="rl-empty-note">${t('rl.pw.noRules')}</div>`}`;
    $$('[data-edit]', host).forEach((b) => b.addEventListener('click', () => openDialog(planRules.find((r) => r.id === b.dataset.edit))));
    $$('[data-plan]', host).forEach((b) => b.addEventListener('click', async () => {
      b.disabled = true;
      const res = await call('rules.setPowerPlan', { guid: b.dataset.plan });
      st.plans = await call('rules.powerPlans');
      renderPlans();
      toast(res.ok ? t('rl.pw.switched', { p: st.plans.plans.find((x) => x.active)?.name || '' }) : errText(res), 4000);
    }));
  }

  /* ---- activity ---- */
  function renderEvents() {
    const host = $('#rlAct');
    if (!host) return;
    host.innerHTML = `
      <div class="rl-act-head">
        <h3>${t('rl.act.title')}</h3>
        <div class="rl-tools">
          <div class="seg" id="rlGroup">${['all', 'balance', 'rules', 'power'].map((g) => `<button data-v="${g}" class="${st.group === g ? 'active' : ''}">${t(`rl.act.${g}`)}</button>`).join('')}</div>
          <button class="mini-btn" id="rlClear">${t('rl.act.clear')}</button>
        </div>
      </div>
      <div class="rl-events" id="rlEvList"></div>`;
    $$('#rlGroup button', host).forEach((b) => b.addEventListener('click', async () => {
      st.group = b.dataset.v;
      $$('#rlGroup button', host).forEach((x) => x.classList.toggle('active', x === b));
      st.evSig = '';
      setEvents(await call('rules.events', evParams()));
    }));
    $('#rlClear').addEventListener('click', async () => {
      if (!await confirmDialog(t('rl.act.clearTitle'), t('rl.act.clearBody'), t('rl.act.clear'))) return;
      await call('rules.clearEvents');
      st.evSig = '';
      setEvents(await call('rules.events', evParams()));
    });
    renderEventList();
  }
  function evText(e) {
    const d = e.detail || {};
    const n = `<b dir="auto">${esc(appLabel(e))}</b>`;
    const pct = (v) => N(fmtPct(v));
    switch (e.kind) {
      case 'restrain': return t('rl.ev.restrain', { n, from: t(`rl.prio.${d.from}`), to: t(`rl.prio.${d.to}`), cpu: pct(d.cpu), sys: pct(d.sys) });
      case 'restore': return t('rl.ev.restore', { n, to: t(`rl.prio.${d.to}`), d: fmtDur(d.ms), why: t(`rl.why.${d.why}`) });
      case 'rule': return t('rl.ev.rule', { n, k: N(d.n) });
      case 'denied': return d.n ? t('rl.ev.denied', { n, k: N(d.n) }) : t('rl.ev.failed', { n, k: N(d.failed) });
      case 'kill': return t('rl.ev.kill', { n, k: N(d.n) });
      case 'limit': return t('rl.ev.limit', { n, k: N(d.n), l: N(d.limit) });
      case 'plan': return t(d.ok ? 'rl.ev.plan' : 'rl.ev.planFail', { n, p: esc(d.toName || planName(d.to)) });
      case 'planRestore': return e.name ? t('rl.ev.planRestore', { n, p: esc(d.toName || planName(d.to)) }) : t('rl.ev.planRestoreAny', { p: esc(d.toName || planName(d.to)) });
      case 'pbOn': return t('rl.ev.pbOn');
      case 'pbOff': return t('rl.ev.pbOff');
      default: return esc(e.kind);
    }
  }
  function renderEventList() {
    const host = $('#rlEvList');
    if (!host) return;
    const clear = $('#rlClear');
    if (clear) clear.disabled = !st.events.length;
    host.innerHTML = st.events.length ? st.events.map((e) => {
      const g = GROUP[e.kind] || 'rules';
      const ic = e.name ? appIcon(e) : `<span class="ph rl-sys">${g === 'power' ? SVG.bolt : SVG.balance}</span>`;
      return `<div class="rl-ev k-${g}"><span class="rl-ev-time tnum" title="${esc(fmtFull(e.ts, true))}">${esc(fmtTime(e.ts, { seconds: true }))}</span><span class="rl-ev-ic">${ic}</span><span class="rl-ev-tx">${evText(e)}</span></div>`;
    }).join('') : `<div class="empty">${t('rl.act.empty')}</div>`;
  }

  /* ================= add / edit dialog ================= */
  function openDialog(arg) {
    const r = arg == null ? {} : typeof arg === 'string' ? (ruleFor(arg) || { name: normName(arg) }) : arg;
    st.dlg = {
      id: r.id || null, name: r.name || '', enabled: r.enabled ?? true, priority: r.priority || null,
      cores: r.cores ? [...r.cores] : null, efficiency: r.efficiency ?? null, io: r.io || null,
      disallowed: !!r.disallowed, limit: r.limit || null, powerPlan: r.powerPlan || null, orig: r.id ? r : null,
    };
    let el = $('#rlDlg');
    if (!el) {
      el = document.createElement('div');
      el.id = 'rlDlg';
      el.className = 'rl-scrim hidden';
      document.body.appendChild(el);
      el.addEventListener('mousedown', (e) => { if (e.target === el) closeDialog(); });
    }
    el.classList.remove('hidden');
    renderDialog();
    loadApps();
    if (!st.dlg.name) setTimeout(() => $('#rlName')?.focus(), 30);
  }
  function closeDialog() {
    st.dlg = null;
    $('#rlDlg')?.classList.add('hidden');
  }
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && st.dlg && $('#modal').classList.contains('hidden')) closeDialog();
  });
  async function loadApps() {
    try { st.apps = await call('rules.apps'); } catch { st.apps = []; }
    renderApps();
  }

  function renderDialog() {
    const d = st.dlg;
    const el = $('#rlDlg');
    if (!d || !el) return;
    const scroll = $('.rl-dlg-body', el)?.scrollTop || 0;
    const cores = st.cores?.cores || [];
    const plans = st.plans?.plans || [];
    const seg = (field, opts) => `<div class="seg" data-seg="${field}">${opts.map(([v, label]) => `<button type="button" data-v="${v}" class="${String(d[field]) === v ? 'active' : ''}">${label}</button>`).join('')}</div>`;
    const unch = ['null', t('rl.unchanged')];
    const effs = [...new Set(cores.map((c) => c.eff))];
    const coreNo = new Map([...new Set(cores.map((c) => c.core))].map((c, i) => [c, i]));
    const field = (label, hint, control, cls = '') => `<section class="rl-field ${cls}"><div class="rl-lbl"><b>${label}</b><span>${hint}</span></div>${control}</section>`;
    el.innerHTML = `<div class="rl-dialog" role="dialog" aria-modal="true" aria-labelledby="rlDlgTitle">
      <div class="rl-dlg-head"><h3 id="rlDlgTitle">${d.orig ? t('rl.dlg.edit') : t('rl.dlg.add')}</h3><button class="icon-btn" data-close aria-label="${esc(t('rl.close'))}">${SVG.close}</button></div>
      <div class="rl-dlg-body">
        ${field(t('rl.f.app'), t('rl.f.appHint'), `<div class="search rl-name">${SVG.search}<input id="rlName" value="${esc(d.name)}" placeholder="chrome.exe" dir="ltr" spellcheck="false" autocomplete="off"></div>
          <div class="rl-apps" id="rlApps"></div><div class="rl-name-hint" id="rlNameHint"></div>`)}
        <section class="rl-field rl-block ${d.disallowed ? 'on' : ''}"><div class="rl-line"><div class="rl-lbl"><b>${t('rl.f.block')}</b><span>${t('rl.f.blockHint')}</span></div><label class="switch"><input type="checkbox" id="rlBlock" ${d.disallowed ? 'checked' : ''}><i></i></label></div></section>
        <div class="rl-tune ${d.disallowed ? 'off' : ''}">
          ${field(t('rl.f.prio'), t('rl.f.prioHint'), seg('priority', [unch, ...PRIOS.map((p) => [p, t(`rl.prio.${p}`)])]))}
          ${field(t('rl.f.cores'), t('rl.f.coresHint'), `<div class="rl-line">
              <div class="seg" id="rlCoreMode"><button type="button" data-v="null" class="${d.cores ? '' : 'active'}">${t('rl.unchanged')}</button><button type="button" data-v="pick" class="${d.cores ? 'active' : ''}">${t('rl.f.coresPick')}</button></div>
              ${d.cores ? `<span class="muted" id="rlCoreCount">${t('rl.f.coresN', { n: N(d.cores.length), t: N(cores.length) })}</span>` : ''}
            </div>
            ${d.cores ? `<div class="rl-cores-tools">${[['all', 'rl.f.all'], ['none', 'rl.f.none'], ['even', 'rl.f.even'], ['odd', 'rl.f.odd'], ...(effs.length > 1 ? [['p', 'rl.f.pcores'], ['e', 'rl.f.ecores']] : [])]
              .map(([k, l]) => `<button type="button" class="mini-btn" data-cq="${k}">${t(l)}</button>`).join('')}</div>
              <div class="rl-cores">${cores.map((c) => `<button type="button" class="rl-core ${d.cores.includes(c.index) ? 'on' : ''}" data-core="${c.index}" title="${esc(t('rl.f.coreTip', { i: c.index, c: coreNo.get(c.core) }))}"><span class="num">${c.index}</span>${effs.length > 1 ? `<i>${c.eff === Math.max(...effs) ? 'P' : 'E'}</i>` : ''}</button>`).join('')}</div>` : ''}`)}
          <div class="rl-pair">
            ${field(t('rl.f.eco'), t('rl.f.ecoHint'), seg('efficiency', [unch, ['true', t('rl.on')], ['false', t('rl.off')]]))}
            ${field(t('rl.f.io'), t('rl.f.ioHint'), seg('io', [unch, ['low', t('rl.io.low')], ['normal', t('rl.io.normal')]]))}
          </div>
          <div class="rl-pair">
            ${field(t('rl.f.limit'), t('rl.f.limitHint'), `<div class="rl-line rl-limit"><label class="switch"><input type="checkbox" id="rlLimitOn" ${d.limit ? 'checked' : ''}><i></i></label><input class="num-input num" type="text" inputmode="numeric" dir="ltr" id="rlLimit" value="${d.limit || 1}" ${d.limit ? '' : 'disabled'}></div>`)}
            ${field(t('rl.f.plan'), t('rl.f.planHint'), `<select id="rlPlan"><option value="">${t('rl.unchanged')}</option>${plans.map((p) => `<option value="${esc(p.guid)}" ${lc(d.powerPlan) === lc(p.guid) ? 'selected' : ''}>${esc(p.name)}</option>`).join('')}${d.powerPlan && !plans.some((p) => lc(p.guid) === lc(d.powerPlan)) ? `<option value="${esc(d.powerPlan)}" selected>${esc(planName(d.powerPlan))}</option>` : ''}</select>`)}
          </div>
        </div>
      </div>
      <div class="rl-dlg-foot">
        <label class="check"><input type="checkbox" id="rlEnabled" ${d.enabled ? 'checked' : ''}>${t('rl.f.enabled')}</label>
        <div class="btn-row"><button class="btn" data-close>${t('rl.cancel')}</button><button class="btn primary" id="rlSave">${t('rl.save')}</button></div>
      </div>
    </div>`;
    $('.rl-dlg-body', el).scrollTop = scroll;
    renderApps();

    $$('[data-close]', el).forEach((b) => b.addEventListener('click', closeDialog));
    $('#rlName').addEventListener('input', (e) => { d.name = e.target.value; renderApps(); });
    $('#rlBlock').addEventListener('change', (e) => { d.disallowed = e.target.checked; renderDialog(); });
    $$('[data-seg] button', el).forEach((b) => b.addEventListener('click', () => {
      const v = b.dataset.v;
      d[b.parentElement.dataset.seg] = v === 'null' ? null : v === 'true' ? true : v === 'false' ? false : v;
      renderDialog();
    }));
    $$('#rlCoreMode button', el).forEach((b) => b.addEventListener('click', () => {
      d.cores = b.dataset.v === 'pick' ? (d.cores || cores.map((c) => c.index)) : null;
      renderDialog();
    }));
    $$('[data-cq]', el).forEach((b) => b.addEventListener('click', () => {
      const q = b.dataset.cq, hi = Math.max(...effs), lo = Math.min(...effs);
      d.cores = cores.filter((c) => q === 'all' || (q === 'even' && c.index % 2 === 0) || (q === 'odd' && c.index % 2 === 1)
        || (q === 'p' && c.eff === hi) || (q === 'e' && c.eff === lo)).map((c) => c.index);
      renderDialog();
    }));
    $$('[data-core]', el).forEach((b) => b.addEventListener('click', () => {
      const i = +b.dataset.core;
      d.cores = d.cores.includes(i) ? d.cores.filter((x) => x !== i) : [...d.cores, i].sort((x, y) => x - y);
      b.classList.toggle('on', d.cores.includes(i));
      $('#rlCoreCount').innerHTML = t('rl.f.coresN', { n: N(d.cores.length), t: N(cores.length) });
    }));
    $('#rlLimitOn').addEventListener('change', (e) => { d.limit = e.target.checked ? Math.max(1, numVal($('#rlLimit').value) || 1) : null; renderDialog(); });
    $('#rlLimit').addEventListener('input', (e) => { d.limit = Math.min(99, Math.max(1, Math.round(numVal(e.target.value)) || 1)); });
    $('#rlLimit').addEventListener('change', (e) => { e.target.value = d.limit; });
    $('#rlPlan').addEventListener('change', (e) => { d.powerPlan = e.target.value || null; });
    $('#rlEnabled').addEventListener('change', (e) => { d.enabled = e.target.checked; });
    $('#rlSave').addEventListener('click', saveDialog);
  }

  function renderApps() {
    const box = $('#rlApps');
    const d = st.dlg;
    if (!box || !d) return;
    const q = lc(d.name.trim());
    const qn = lc(normName(d.name));
    const list = (q ? st.apps.filter((a) => lc(a.name).includes(q) || lc(a.descr).includes(q) || lc(a.name) === qn) : st.apps).slice(0, 60);
    box.innerHTML = list.length ? list.map((a) => `<button type="button" class="rl-app ${lc(a.name) === qn ? 'sel' : ''}" data-name="${esc(a.name)}">
        ${appIcon(a)}<span class="rl-app-nm"><b dir="auto">${esc(appLabel(a))}</b><span class="num">${esc(a.name)}${a.count > 1 ? ` ×${a.count}` : ''}</span></span>
        ${ruleFor(a.name) ? `<span class="badge info">${t('rl.hasRule')}</span>` : ''}
        <span class="rl-app-cpu num">${fmtPct(a.cpu)}</span>
      </button>`).join('') : `<div class="rl-apps-empty">${st.apps.length ? t('rl.f.noApps') : t('rl.loading')}</div>`;
    $$('[data-name]', box).forEach((b) => b.addEventListener('click', () => {
      const ex = ruleFor(b.dataset.name);
      // Picking an app that already has a rule opens that rule instead of making a second one.
      if (ex && ex.id !== d.id) { openDialog(ex); return; }
      d.name = b.dataset.name;
      $('#rlName').value = d.name;
      renderApps();
    }));
    const hint = $('#rlNameHint');
    const n = normName(d.name);
    const other = n && ruleFor(n);
    hint.className = `rl-name-hint ${n && isCritical(n) ? 'warn' : ''}`;
    hint.textContent = !n ? '' : isCritical(n) ? t('rl.f.isCritical') : other && other.id !== d.id ? t('rl.f.replaces')
      : st.apps.length && !st.apps.some((a) => lc(a.name) === lc(n)) ? t('rl.f.notRunning') : '';
  }

  async function saveDialog() {
    const d = st.dlg;
    if (!d) return;
    const name = normName(d.name);
    if (!name) { toast(t('rl.err.name'), 3500); $('#rlName')?.focus(); return; }
    if (d.cores && !d.cores.length && !d.disallowed) { toast(t('rl.err.cores'), 4000); return; }
    const rule = {
      id: d.id, name, enabled: d.enabled, priority: d.priority, cores: d.cores, efficiency: d.efficiency, io: d.io,
      disallowed: d.disallowed, limit: d.limit, powerPlan: d.powerPlan,
    };
    // Ask before closing apps, unless the rule already did exactly that.
    const prev = d.orig;
    const same = prev && prev.enabled && prev.disallowed === d.disallowed && (d.disallowed || prev.limit === d.limit);
    if (!same && !await confirmKills({ ...rule, descr: prev?.descr }, runningCount(name))) return;
    $('#rlSave').disabled = true;
    const res = await call('rules.save', { rule });
    if (!res.ok) {
      toast(errText(res), 5000);
      const b = $('#rlSave');
      if (b) b.disabled = false;
      return;
    }
    closeDialog();
    toast(resultText(res), 4500);
    await refresh();
  }

  /* ================= Processes page: right-click menu ================= */
  async function quick(name, prio) {
    const res = await call('rules.save', { rule: { name, priority: prio, enabled: true }, merge: true });
    if (!res.ok) { toast(errText(res), 5000); return; }
    toast(`${t('rl.quickSaved', { n: bare(name), p: t(`rl.prio.${prio}`) })}${res.denied ? ` ${t('rl.needAdmin', { n: res.denied })}` : ''}`, 4500);
    if (setRules(await call('rules.list')) && state.page === 'rules') { renderRules(); renderPlans(); }
  }
  PROC_MENU_EXT.push((sel, app) => {
    const name = app?.name || sel.name;
    if (!name || (state.settings?.hidden || []).includes('page:rules') || isCritical(name)) return [];
    if (!st.rules && !st.menuFetch) { // first right-click: learn the rules for next time
      st.menuFetch = true;
      call('rules.list').then(setRules).catch(() => { st.menuFetch = false; });
    }
    const r = ruleFor(name);
    return [
      { key: 'rl-rule', label: r ? t('rl.menu.edit') : t('rl.menu.create'), run: () => { st.pending = name; showPage('rules'); } },
      ...['high', 'above', 'below', 'idle'].map((p) => ({
        key: `rl-${p}`, label: t('rl.menu.always', { p: t(`rl.prio.${p}`) }), cls: r?.enabled && r.priority === p && !r.disallowed ? 'rl-active' : '', run: () => quick(name, p),
      })),
    ];
  });

  registerPage('rules', { load, stop, onLive, subtitle: () => t('rl.sub') }, { balance: 'rl.pb.short', power: 'rl.pw.title', activity: 'rl.act.title' });
})();
