const state = {
  view: 'overview',
  financialReportType: 'position',
  financialOperationType: 'cashReceipt',
  financialOperationEditId: null,
  databaseConfigured: false,
  users: [],
  customers: [],
  customerSort: { sortBy: 'createdAt', descending: true },
  accountStatement: { accounts: [], lines: [] },
  financialOperations: [],
  notificationCampaigns: [],
  drivers: [],
  driverRatingReport: { drivers: [], ratings: [] },
  rides: [],
  catalog: [],
  kinds: [],
  pricing: [], places: [], areas: [], history: [], settlements: [], ledgerAccounts: [], paymentMethods: [], cancellations: [], safety: [], safetyRecordings: [], notifications: [], liveRides: new Map()
};

const busySafetyActions = new Set();
const safetyAudioUrls = new Map();
let pageScrollDirection = 'down';
let lastPageScrollPosition = 0;
let pageScrollUpdatePending = false;
let safetyMapTimer = null;
let safetyMapIncidentId = null;
let safetyMapLastMarker = '';
let safetyMapPendingUrl = '';
let safetyMapPendingMarker = '';
let safetyMapLoaded = false;

const ADMIN_TOKEN_KEY = 'yemendrive_admin_token';
const ADMIN_REFRESH_TOKEN_KEY = 'yemendrive_admin_refresh_token';
const ADMIN_TOPBAR_THEME_KEY = 'yemendrive_admin_topbar_theme';
let adminRefreshInFlight = null;
let adminHubConnection = null;
let liveRidesMap = null;
const liveRideMarkers = new Map();
const liveRideAnimations = new Map();
const livePassengerAnimations = new Map();
const pendingLiveRideSnapshots = new Set();

const titles = {
  overview: ['نظرة عامة', 'متابعة حالة النظام والعمليات الأخيرة'],
  users: ['المستخدمون', 'إدارة حسابات العملاء والسائقين'],
  customers: ['العملاء', 'بيانات العملاء ومؤشرات النشاط والتقييم'],
  drivers: ['السائقون', 'المركبات والخدمات وحالة التوفر'],
  'driver-ratings': ['تقييمات السائقين', 'تقييمات العملاء وإضافة تقييم الإدارة'],
  rides: ['الرحلات', 'متابعة الطلبات وحالات التنفيذ'],
  cancellations: ['طلبات الإلغاء', 'مراجعة القرارات والإحالات قبل اعتماد الإدارة'],
  safety: ['بلاغات السلامة', 'بلاغات الرحلات التي تحتاج متابعة فورية'],
  'safety-recordings': ['تسجيلات السلامة', 'مراجعة التسجيلات المكتملة التي وافق أصحابها على تسجيلها'],
  catalog: ['الخدمات', 'كتالوج الخدمات والتصنيفات'],
  pricing: ['التسعير', 'قواعد احتساب أسعار الرحلات'],
  wallet: ['المحفظة', 'الأرصدة والحركات المالية'],
  'payment-methods': ['طرق الدفع الخارجية', 'إدارة وسائل الشحن والدفع الظاهرة للعميل'],
  settlements: ['مديونيات السائقين', 'تحصيل عمولة المنصة من الرحلات النقدية'],
  ledger: ['كشف الحساب', 'استعلام القيود المحاسبية المنشورة والأرصدة'],
  'financial-operations': ['العمليات المالية', 'تسجيل القيود النقدية والتحويلات المالية اليدوية'],
  'financial-reports': ['التقارير المالية', 'القوائم المالية المستخرجة من القيود المنشورة'],
  places: ['الأماكن المفضلة', 'الأماكن المحفوظة للمستخدمين'],
  areas: ['مناطق الخدمة', 'الدول والمدن التي تعمل فيها الخدمة'],
  history: ['سجل الرحلات', 'الرحلات السابقة والقادمة'],
  'waiting-report': ['تقرير تأخيرات الرحلات', 'مواقع التوقف ومدد الانتظار والرسوم المضافة لأجرة الراكب'],
  'route-change-report': ['سجل تغييرات المسار', 'طلبات تغيير الوجهة ومواقعها وتفاصيل احتساب الأجرة'],
  console: ['محطة API', 'تنفيذ الطلبات المتقدمة'],
  'notification-campaigns': ['إشعارات المستخدمين', 'إنشاء حملات إشعار ومراجعة سجلها']
};

const financialReportDefinitions = {
  position: ['قائمة المركز المالي', 'الأصول والالتزامات وحقوق الملكية حتى التاريخ المحدد.'],
  trialBalance: ['ميزان المراجعة', 'الأرصدة الافتتاحية والحركة والأرصدة الختامية لكل حساب وعملة.'],
  income: ['قائمة الدخل والأرباح والخسائر', 'الإيرادات والمصروفات وصافي الربح أو الخسارة خلال الفترة.'],
  cashFlow: ['قائمة التدفقات النقدية', 'حركة النقدية المسجلة في حسابات الصندوق والمحافظ الخارجية خلال الفترة.'],
  equity: ['قائمة التغيرات في حقوق الملكية', 'أرصدة حقوق الملكية والحركات وصافي نتيجة الفترة.']
};

const financialOperationDefinitions = {
  cashReceipt: ['قبض نقدي', 'تسجيل سند قبض نقدي بقيد محاسبي متوازن.', 'سيُسجل حساب من الصندوق النقدي للإدارة مديناً والحساب المحدد دائناً.'],
  cashDisbursement: ['صرف نقدي', 'تسجيل سند صرف نقدي بقيد محاسبي متوازن.', 'سيُسجل الحساب المحدد مديناً وحساب من الصندوق النقدي للإدارة دائناً.'],
  accountTransfer: ['قيد مالي', 'تسجيل قيد مالي متوازن من حساب إلى حساب.', 'أدخل الحساب المدين والدائن والمبلغ والتاريخ والملاحظة.']
};

const rideStatuses = ['مسودة', 'جاري البحث', 'تفاوض', 'تم تعيين سائق', 'السائق في الطريق', 'قيد التنفيذ', 'مكتملة', 'ملغاة', 'الإلغاء قيد المراجعة', 'وصل السائق إلى نقطة الانطلاق'];
const cancellationStatuses = ['بانتظار قرار السائق', 'رفض السائق', 'قيد مراجعة الإدارة', 'وافقت الإدارة', 'رفضت الإدارة', 'بانتظار موافقة الراكب', 'وافق الراكب', 'رفض الراكب', 'ألغى السائق قبل البدء'];
const cancellationDriverDecisions = ['—', 'وافق السائق', 'رفض السائق', 'أحال السائق للإدارة'];
const cancellationRefundMethods = { 1: 'استرداد نقدي مباشر من السائق', 2: 'إضافة الاسترداد إلى محفظة العميل' };
const safetyStatuses = ['جديد', 'تم الاستلام', 'مغلق'];
const roles = ['عميل', 'سائق', 'مدير'];
const notificationCampaignTargets = ['العملاء', 'السائقون', 'العملاء والسائقون'];
const transactionTypes = ['إيداع', 'خصم', 'حجز', 'تحرير', 'استرداد', 'عمولة'];
const journalEntryTypes = ['رصيد افتتاحي', 'تحصيل نقدي لرحلة', 'دفع رحلة من المحفظة', 'تغذية محفظة', 'إلغاء رحلة', 'تحصيل تسوية سائق', 'تسوية يدوية', 'قيد عكسي', 'قبض نقدي إداري', 'صرف نقدي إداري', 'تحويل بين حسابات'];

const formConfigs = {
  user: {
    title: 'إضافة مستخدم', subtitle: 'إنشاء حساب جديد ومحفظة تلقائية', model: 'UserModel', operation: 'add', refresh: 'users',
    fields: [
      ['displayName', 'الاسم', 'text', true], ['phoneNumber', 'رقم الهاتف', 'tel', true],
      ['email', 'البريد الإلكتروني', 'email'], ['password', 'كلمة المرور', 'password', true],
      ['role', 'الدور', 'select', true, [['0','عميل'],['1','سائق'],['2','مدير']]],
      ['gender', 'النوع (اختياري)', 'text'], ['city', 'المدينة', 'text'], ['street', 'الشارع', 'text'], ['district', 'الحي', 'text'],
      ['isActive', 'الحساب نشط', 'checkbox', false, true]
    ]
  },
  driver: {
    title: 'إضافة ملف سائق', subtitle: 'ربط مستخدم ببيانات المركبة والخدمة', model: 'DriverModel', operation: 'add', refresh: 'drivers',
    fields: [
      ['userId','حساب المستخدم','select',true,'driverUsers'],
      ['serviceKindId','نوع الخدمة','select',true,'serviceKinds'], ['serviceCatalogItemId','الخدمة','select',true,'serviceCatalogItems'],
      ['vehicleModel','موديل المركبة','text',true], ['plateNumber','رقم اللوحة','text',true],
      ['commissionRate','نسبة العمولة','number',false,'0.20','0.01'],
      ['imageFile','صورة السائق أو المركبة','file'], ['photoUrl','رابط الصورة (اختياري)','url'],
      ['isAvailable','متاح للعمل','checkbox',false,true]
    ], imageField: 'photoUrl'
  },
  ride: {
    title: 'إنشاء رحلة', subtitle: 'تسجيل طلب رحلة جديد للعميل', model: 'RideModel', operation: 'add', refresh: 'rides',
    fields: [
      ['customerId','العميل','select',true,'customerUsers'], ['serviceKindId','نوع الخدمة','select',true,'serviceKinds'],
      ['serviceCatalogItemId','الخدمة','select',true,'serviceCatalogItems'],
      ['pickupLabel','اسم نقطة الانطلاق','text'], ['pickupAddress','عنوان الانطلاق','text',true],
      ['pickupLatitude','خط عرض الانطلاق','number',true,'15.3694','0.000001'], ['pickupLongitude','خط طول الانطلاق','number',true,'44.1910','0.000001'],
      ['destinationLabel','اسم الوجهة','text'], ['destinationAddress','عنوان الوجهة','text',true],
      ['destinationLatitude','خط عرض الوجهة','number',true,'15.3547','0.000001'], ['destinationLongitude','خط طول الوجهة','number',true,'44.2067','0.000001'],
      ['customerPrice','سعر العميل','number',false,'0','0.01'],
      ['moreThanFourPassengers','أكثر من 4 ركاب','checkbox'], ['passengerNotes','ملاحظات الركوب','text'],
      ['status','حالة الإنشاء','select',true,[['1','بدء البحث عن سائق'],['0','حفظ كمسودة']]]
    ]
  },
  serviceKind: {
    title: 'إضافة نوع خدمة', subtitle: 'تصنيف رئيسي للخدمات', model: 'ServiceKindModel', operation: 'add', refresh: 'catalog',
    fields: [['code','الرمز','text',true],['nameAr','الاسم العربي','text',true],['imageFile','اختيار صورة أو أيقونة','file'],['imageUrl','رابط الصورة (اختياري)','url'],['sortOrder','الترتيب','number',false,'0'],['isDefault','النوع الافتراضي للعميل','checkbox'],['isActive','نشط','checkbox',false,true]]
  },
  serviceCatalog: {
    title: 'إضافة خدمة', subtitle: 'خدمة تظهر للعملاء عند طلب الرحلة', model: 'ServiceCatalogModel', operation: 'add', refresh: 'catalog',
    fields: [
      ['code','الرمز','text',true],['nameAr','الاسم العربي','text',true],['serviceKindId','نوع الخدمة','select',true,'serviceKinds'],
      ['arrivalMinutes','دقائق الوصول','number',false,'5'],['basePrice','السعر الأساسي','number',false,'0','0.01'],['rating','التقييم','number',false,'5','0.1'],['seats','المقاعد','number',false,'4'],
      ['descriptionAr','الوصف','textarea',true],['imageFile','اختيار صورة أو أيقونة','file'],['imageUrl','رابط الصورة (اختياري)','url'],['sortOrder','الترتيب','number',false,'0'],['isRecommended','موصى بها','checkbox'],['isActive','نشطة','checkbox',false,true]
    ]
  },
  pricingRule: {
    title: 'إضافة قاعدة تسعير', subtitle: 'اضبط الأجرة والانتظار وحدود المفاوضة لكل خدمة. نسبة 0 تمنع التغيير في ذلك الاتجاه؛ رسوم انتظار 0 تعطّل الانتظار المدفوع. الحدود من الأجرة المقترحة قبل رسم الخدمة.', model: 'PricingRuleModel', operation: 'add', refresh: 'pricing',
    fields: [['serviceKindId','نوع الخدمة','select',true,'serviceKinds'],['serviceCatalogItemId','الخدمة','select',true,'serviceCatalogItems'],['baseFare','السعر الأساسي','number',true,'500','0.01'],['perKilometer','لكل كيلومتر','number',true,'100','0.01'],['perMinute','لكل دقيقة','number',true,'20','0.01'],['serviceFee','رسوم الخدمة الثابتة على العميل','number',true,'0','0.01'],['driverCommissionRate','نسبة عمولة السائق (0 إلى 1)','number',true,'0','0.01'],['driverCommissionFixed','عمولة السائق الثابتة','number',true,'0','0.01'],['cancellationFee','رسم الإلغاء','number',true,'0','0.01'],['routeChangeFee','رسوم تغيير المسار (ر.ي)','number',true,'0','0.01'],['waitingFreeMinutes','الانتظار المجاني لكل توقف (دقيقة)','number',true,'0','1'],['waitingPricePerMinute','رسوم دقيقة الانتظار (ر.ي)','number',true,'0','0.01'],['waitingMaxFee','سقف رسوم الانتظار للرحلة (ر.ي)','number',true,'0','1'],['maxPriceDecreasePercent','أقصى خفض من الأجرة المقترحة (%)','number',true,'0','0.01'],['maxPriceIncreasePercent','أقصى رفع من الأجرة المقترحة (%)','number',true,'0','0.01'],['driverShareRate','نسبة السائق القديمة','number',false,'0','0.01'],['isActive','نشطة','checkbox',false,true]]
  },
  quote: {
    title: 'تجربة التسعير', subtitle: 'احتساب سعر رحلة دون حفظها', model: 'PricingModel', operation: 'report',
    fields: [['serviceKindId','نوع الخدمة','select',true,'serviceKinds'],['serviceCatalogItemId','الخدمة','select',true,'serviceCatalogItems'],['distanceKm','المسافة بالكيلومتر','number',true,'8.5','0.1'],['durationMinutes','المدة بالدقائق','number',true,'20','0.1']]
  },
  settlementPayment: {
    title: 'تحصيل مديونية سائق', subtitle: 'ينشئ قيد تحصيل جديد؛ لا يعدّل الدفعة أو المديونية الأصلية.', model: 'DriverSettlementPaymentModel', operation: 'add', refresh: 'settlements',
    fields: [['driverSettlementId','سجل المديونية','select',true,'openSettlements'],['amount','المبلغ المحصل','number',true,'0','0.01'],['currency','العملة','text',true,'YER'],['method','طريقة التحصيل','select',true,[['CashToPlatform','نقد إلى الإدارة'],['BankTransfer','تحويل بنكي'],['WalletTransfer','تحويل محفظة']]],['reference','مرجع الإيصال أو التحويل الخارجي','text',true],['note','ملاحظة','textarea']]
  }
  ,paymentMethod: {
    title: 'إضافة طريقة دفع خارجية', subtitle: 'بيانات تعريف وتشغيل فقط؛ لا تضع مفاتيح API أو بيانات سرية هنا.', model: 'PaymentMethodModel', operation: 'add', refresh: 'payment-methods',
    fields: [['code','رمز فريد','text',true],['nameAr','الاسم الظاهر للعميل','text',true],['descriptionAr','الوصف المختصر','textarea',true],['imageFile','الشعار أو الصورة','file'],['imageUrl','رابط الصورة (اختياري)','url'],['kind','النوع','select',true,[['0','محفظة خارجية'],['1','بطاقة'],['2','حساب/تحويل بنكي']]],['providerCode','رمز المزود غير السري','text',true],['publicInstructionsAr','تعليمات ظاهرة للعميل','textarea'],['sortOrder','الترتيب','number',false,'0'],['isAvailableForRidePayment','تظهر أثناء دفع الرحلة','checkbox',false,true],['isAvailableForWalletTopUp','تظهر أثناء شحن المحفظة','checkbox',false,true],['isActive','نشطة','checkbox',false,true]]
  }
  ,serviceArea: {
    title: 'إضافة مدينة خدمة', subtitle: 'تفعيل الخدمة في دولة ومدينة محددة', model: 'ServiceAreaModel', operation: 'add', refresh: 'areas',
    fields: [['countryCode','رمز الدولة','text',true,'YE'],['countryNameAr','اسم الدولة','text',true,'اليمن'],['cityNameAr','اسم المدينة','text',true],['isActive','الخدمة متاحة','checkbox',false,true]]
  },
  driverRating: {
    title: 'إضافة تقييم الإدارة للسائق', subtitle: 'يسجل كتقييم إدارة مستقل ويضاف إلى متوسط تقييم السائق.', model: 'DriverRatingModel', operation: 'add', refresh: 'driver-ratings',
    fields: [['driverUserId','السائق','select',true,'driverUsers'],['score','التقييم من 1 إلى 5','number',true,'5','0.5'],['comment','ملاحظة الإدارة','textarea']]
  },
  notificationCampaign: {
    title: 'إضافة إشعار وإرساله', subtitle: 'ينشئ هذا النموذج حملة جديدة؛ إعادة الإرسال لا تعدّل الحملات السابقة.', model: 'AdminNotificationCampaignModel', operation: 'add', refresh: 'notification-campaigns',
    fields: [['body','نص الإشعار','textarea',true],['target','إرسال إلى','select',true,[['0','العملاء'],['1','السائقون'],['2','العملاء والسائقون']]]]
  }
};

const $ = selector => document.querySelector(selector);
const $$ = selector => [...document.querySelectorAll(selector)];
const escapeHtml = value => String(value ?? '').replace(/[&<>'"]/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[char]));
const number = value => new Intl.NumberFormat('ar-YE-u-nu-latn', { maximumFractionDigits: 2 }).format(Number(value || 0));
const date = value => value ? new Intl.DateTimeFormat('ar-YE-u-nu-latn', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)) : '—';
const badge = (text, tone = '') => `<span class="badge ${tone}">${escapeHtml(text)}</span>`;

const actionIconPaths = {
  edit: '<path d="M12 20h9"/><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L8 18l-4 1 1-4Z"/>',
  delete: '<path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="m19 6-1 14H6L5 6"/><path d="M10 11v5M14 11v5"/>',
  suspend: '<circle cx="12" cy="12" r="9"/><path d="M9 9v6M15 9v6"/>',
  activate: '<circle cx="12" cy="12" r="9"/><path d="m10 8 6 4-6 4z"/>',
  statement: '<path d="M8 6h13M8 12h13M8 18h13"/><path d="M3 6h.01M3 12h.01M3 18h.01"/>',
  wallet: '<path d="M4 6.5A2.5 2.5 0 0 1 6.5 4H20v16H6.5A2.5 2.5 0 0 1 4 17.5z"/><path d="M4 7h16v4h-5a2 2 0 0 0 0 4h5"/><path d="M15 13h.01"/>',
  assessment: '<path d="M4 19V5M4 19h17"/><path d="m7 15 4-4 3 2 5-6"/><path d="M16 7h3v3"/>',
  resend: '<path d="M3 11a9 9 0 0 1 15.4-6.4L21 7"/><path d="M21 3v4h-4"/><path d="M21 13a9 9 0 0 1-15.4 6.4L3 17"/><path d="M3 21v-4h4"/>',
  default: '<path d="m12 3 2.8 5.7 6.2.9-4.5 4.4 1.1 6.2-5.6-3-5.6 3 1.1-6.2L3 9.6l6.2-.9z"/>',
  cash: '<rect x="3" y="5" width="18" height="14" rx="2"/><circle cx="12" cy="12" r="3"/><path d="M7 9h.01M17 15h.01"/>',
  approve: '<circle cx="12" cy="12" r="9"/><path d="m8 12 2.5 2.5L16 9"/>',
  reject: '<circle cx="12" cy="12" r="9"/><path d="m9 9 6 6m0-6-6 6"/>',
  map: '<path d="M20 10c0 5-8 11-8 11S4 15 4 10a8 8 0 1 1 16 0Z"/><circle cx="12" cy="10" r="2.5"/>',
  play: '<path d="m8 5 12 7-12 7z"/>',
  pause: '<path d="M8 5h3v14H8zM15 5h3v14h-3z"/>',
  sort: '<path d="M4 7h10M4 12h7M4 17h4"/><path d="m17 7 3 3 3-3M20 10V4M17 17l3-3 3 3M20 14v6"/>'
};

function iconActionButton(icon, label, handler, options = {}) {
  const disabledReason = options.disabledReason || '';
  const tooltip = disabledReason ? `${label} · ${disabledReason}` : label;
  const disabledClass = disabledReason ? ' is-disabled' : '';
  const handlerAttribute = handler && !disabledReason ? ` onclick="${handler}"` : '';
  return `<button class="icon-action tone-${options.tone || icon}${disabledClass}${options.className ? ` ${options.className}` : ''}" type="button" aria-label="${escapeHtml(label)}" data-tooltip="${escapeHtml(tooltip)}"${disabledReason ? ' aria-disabled="true"' : ''}${handlerAttribute}><svg viewBox="0 0 24 24" aria-hidden="true">${actionIconPaths[icon] || actionIconPaths.assessment}</svg></button>`;
}

function updateIconAction(button, icon, label) {
  if (!button) return;
  button.dataset.tooltip = label;
  button.setAttribute('aria-label', label);
  button.innerHTML = `<svg viewBox="0 0 24 24" aria-hidden="true">${actionIconPaths[icon] || actionIconPaths.assessment}</svg>`;
  const tooltip = document.getElementById(button.getAttribute('aria-describedby'));
  if (tooltip) tooltip.textContent = label;
}

function bindActionTooltips() {
  const tooltip = document.createElement('div');
  tooltip.className = 'admin-action-tooltip';
  tooltip.id = 'admin-action-tooltip';
  tooltip.setAttribute('role', 'tooltip');
  tooltip.hidden = true;
  document.body.append(tooltip);
  let activeButton = null;

  const hide = () => {
    if (activeButton) activeButton.removeAttribute('aria-describedby');
    activeButton = null;
    tooltip.hidden = true;
  };
  const show = button => {
    if (!button?.dataset.tooltip) return hide();
    activeButton = button;
    tooltip.textContent = button.dataset.tooltip;
    tooltip.hidden = false;
    button.setAttribute('aria-describedby', tooltip.id);
    requestAnimationFrame(() => {
      if (activeButton !== button) return;
      const rect = button.getBoundingClientRect();
      const tipRect = tooltip.getBoundingClientRect();
      const left = Math.max(8, Math.min(window.innerWidth - tipRect.width - 8, rect.left + rect.width / 2 - tipRect.width / 2));
      const top = rect.top > tipRect.height + 12 ? rect.top - tipRect.height - 8 : Math.min(window.innerHeight - tipRect.height - 8, rect.bottom + 8);
      tooltip.style.left = `${left}px`;
      tooltip.style.top = `${Math.max(8, top)}px`;
    });
  };

  document.addEventListener('pointerover', event => {
    const button = event.target.closest?.('.icon-action[data-tooltip]');
    if (button && button !== activeButton) show(button);
  });
  document.addEventListener('pointerout', event => {
    if (activeButton && event.target.closest?.('.icon-action') === activeButton && !activeButton.contains(event.relatedTarget)) hide();
  });
  document.addEventListener('focusin', event => {
    const button = event.target.closest?.('.icon-action[data-tooltip]');
    if (button) show(button);
  });
  document.addEventListener('focusout', event => {
    if (activeButton && event.target === activeButton) hide();
  });
  window.addEventListener('scroll', hide, true);
  window.addEventListener('resize', hide);
}

class AdminOperationCancelled extends Error {}

const readOnlyOperations = new Set(['list', 'get', 'report', 'search']);
const adminModelLabels = {
  UserModel: 'حساب المستخدم', DriverModel: 'ملف السائق', RideModel: 'الرحلة',
  ServiceKindModel: 'نوع الخدمة', ServiceCatalogModel: 'الخدمة', PricingRuleModel: 'قاعدة التسعير',
  PaymentMethodModel: 'طريقة الدفع', ServiceAreaModel: 'منطقة الخدمة',
  DriverSettlementPaymentModel: 'تحصيل مديونية السائق', DriverRatingModel: 'تقييم السائق',
  RideCancellationRequestModel: 'طلب إلغاء الرحلة', SafetyIncidentModel: 'بلاغ السلامة',
  NotificationModel: 'الإشعار', AccountingSetupModel: 'تهيئة الحسابات المالية',
  FinancialOperationModel: 'العملية المالية',
  DatabaseConfigurationModel: 'اتصال قاعدة البيانات',
  AdminNotificationCampaignModel: 'حملة إشعار'
};

function adminConfirmationMessage(model, operation, data = {}) {
  const action = String(operation || '').toLowerCase();
  const label = adminModelLabels[model] || 'البيانات';
  if (model === 'AdminNotificationCampaignModel' && action === 'add') {
    const target = notificationCampaignTargets[Number(data.target)] || 'المستخدمين المحددين';
    return `سيُنشأ سجل إرسال جديد ويُضاف الإشعار داخل التطبيق ويُجدول للأجهزة المسجلة لدى ${target}. هل تريد المتابعة؟`;
  }
  if (model === 'AdminNotificationCampaignModel' && action === 'delete')
    return 'سيُحذف سجل الحملة من لوحة التحكم فقط. الإشعارات التي وصلت إلى المستخدمين أو أُرسلت إلى أجهزتهم لن تُسحب. هل تريد حذف السجل؟';
  if (model === 'FinancialOperationModel' && action === 'add') {
    const operationName = financialOperationDefinitions[data.operationType]?.[0] || 'العملية المالية';
    const accountName = id => {
      const account = state.ledgerAccounts.find(item => Number(item.id) === Number(id));
      return account ? `${account.code} — ${account.name}` : `الحساب ${id || 'غير محدد'}`;
    };
    return `سيُرحّل قيد ${operationName} بمبلغ ${data.amount || 0} ${state.ledgerAccounts.find(item => Number(item.id) === Number(data.debitAccountId))?.currency || ''}.\nمدين: ${accountName(data.debitAccountId)}\nدائن: ${accountName(data.creditAccountId)}\nالتاريخ: ${data.operationDate || 'غير محدد'}\nالملاحظة: ${data.note || '—'}\nلا يمكن تعديل القيد بعد ترحيله. هل تريد المتابعة؟`;
  }
  if (model === 'FinancialOperationModel' && action === 'update')
    return `سيُنشأ قيد عكسي للعملية ${data.journalEntryId || ''} ثم يُرحّل القيد الجديد بالبيانات المعدلة. سيبقى القيد الأصلي محفوظاً في السجل المحاسبي. هل تريد المتابعة؟`;
  if (model === 'FinancialOperationModel' && action === 'delete')
    return `سيُلغى القيد ${data.journalEntryId || ''} بإنشاء قيد عكسي متوازن. سيبقى القيد الأصلي محفوظاً لأغراض المراجعة المحاسبية. هل تريد المتابعة؟`;
  if (model === 'ServiceKindModel' && action === 'update' && data.isDefault === true)
    return `سيتم تعيين نوع الخدمة المحدد كنوع افتراضي للعملاء وتغيير الاختيار الحالي. هل تريد المتابعة؟`;
  if (model === 'UserModel' && action === 'update' && data.isActive === false)
    return 'سيتم إيقاف الحساب ومنع صاحبه من استخدام التطبيق. هل تريد المتابعة؟';
  if (model === 'DatabaseConfigurationModel' && action === 'save')
    return 'سيتم حفظ إعدادات الاتصال الجديدة وتجهيز الجداول المطلوبة في قاعدة البيانات المحددة. هل تريد المتابعة؟';
  if (action === 'delete') return `سيتم حذف ${label} نهائيًا، وقد تُحذف معه البيانات التابعة المسموح بحذفها تلقائيًا. هل تريد المتابعة؟`;
  if (action === 'update') return `سيتم حفظ التعديلات على ${label}. هل تريد المتابعة؟`;
  if (action === 'add' || action === 'create') return `سيتم إنشاء ${label} وحفظه في قاعدة البيانات. هل تريد المتابعة؟`;
  const operationNames = {
    accept: 'استلام البلاغ', reject: 'رفض الطلب', refer: 'إحالة الطلب',
    adminapprove: 'اعتماد القرار', adminreject: 'رفض القرار', cancel: 'تنفيذ الإلغاء'
  };
  return `سيتم تنفيذ «${operationNames[action] || action}» على ${label} وتحديث بيانات النظام. هل تريد المتابعة؟`;
}

function confirmAdminOperation(model, operation, data = {}) {
  return window.confirm(adminConfirmationMessage(model, operation, data));
}

function isAdminOperationCancelled(error) {
  return error instanceof AdminOperationCancelled;
}

async function execute(model, operation, data = {}, options = {}) {
  if (!readOnlyOperations.has(String(operation || '').toLowerCase()) &&
      options.confirmed !== true && !confirmAdminOperation(model, operation, data)) {
    throw new AdminOperationCancelled();
  }
  const response = await adminFetch('/api/admin/execute', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ model, operation, data })
  });
  const result = await response.json().catch(() => ({ success: false, message: 'استجابة غير صالحة من الخادم.' }));
  if (response.status === 401 || result.code === 'session_expired' || result.code === 'admin_authentication_required' || result.errorCode === 'admin_authentication_required') {
    showAdminLogin('انتهت جلسة الإدارة. سجل الدخول من جديد.');
  }
  if (!result.success) throw new Error(result.message || 'تعذر تنفيذ العملية.');
  return result;
}

async function seedDevelopmentData() {
  if (!window.confirm('سيتم إضافة بيانات تجريبية فقط دون حذف البيانات الحالية. هل تريد المتابعة؟')) return;
  const button = $('#development-seed-button');
  button.disabled = true;
  try {
    const response = await adminFetch('/api/admin/development/seed', { method: 'POST' });
    if (response.status === 404) throw new Error('هذه العملية متاحة في بيئة التطوير فقط.');
    const result = await response.json().catch(() => ({ success: false, message: 'استجابة غير صالحة من الخادم.' }));
    if (!result.success) throw new Error(result.message || 'تعذر تعبئة بيانات التطوير.');
    toast(result.message || 'تمت إضافة بيانات التطوير التجريبية.');
    await loadView('overview', true);
  } catch (error) {
    toast(error.message, true);
  } finally {
    button.disabled = false;
  }
}

function showAdminLogin(message = '') {
  stopAdminRealtime();
  clearAdminSession();
  $('.admin-login').classList.remove('hidden');
  $('.app-shell').classList.add('hidden');
  const result = $('#admin-login-result');
  result.className = message ? 'inline-result error' : 'inline-result hidden';
  result.textContent = message;
  $('#admin-phone').focus();
}

function showAdminApp() {
  $('.admin-login').classList.add('hidden');
  $('.app-shell').classList.remove('hidden');
}

async function adminLogin(phoneNumber, password) {
  const response = await fetch('/api/admin/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ phoneNumber, password })
  });
  const result = await response.json().catch(() => ({ success: false, message: 'استجابة غير صالحة من الخادم.' }));
  if (!result.success || !result.data?.accessToken || !result.data?.refreshToken) {
    throw new Error(result.message || 'بيانات حساب الإدارة غير صحيحة.');
  }
  sessionStorage.setItem(ADMIN_TOKEN_KEY, result.data.accessToken);
  sessionStorage.setItem(ADMIN_REFRESH_TOKEN_KEY, result.data.refreshToken);
  return result;
}

function clearAdminSession() {
  sessionStorage.removeItem(ADMIN_TOKEN_KEY);
  sessionStorage.removeItem(ADMIN_REFRESH_TOKEN_KEY);
  localStorage.removeItem(ADMIN_TOKEN_KEY);
  localStorage.removeItem(ADMIN_REFRESH_TOKEN_KEY);
}

function refreshAdminSession() {
  if (adminRefreshInFlight) return adminRefreshInFlight;
  adminRefreshInFlight = performAdminSessionRefresh().finally(() => {
    adminRefreshInFlight = null;
  });
  return adminRefreshInFlight;
}

async function performAdminSessionRefresh() {
  const refreshToken = sessionStorage.getItem(ADMIN_REFRESH_TOKEN_KEY);
  if (!refreshToken) {
    clearAdminSession();
    return 'expired';
  }

  try {
    const response = await fetch('/api/auth/refresh', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken })
    });
    if (response.status === 401) {
      clearAdminSession();
      return 'expired';
    }
    if (!response.ok) return 'unavailable';
    const result = await response.json().catch(() => null);
    const session = result?.success === true ? result.data : null;
    if (!session?.accessToken || !session?.refreshToken) {
      if (result?.success === false) {
        clearAdminSession();
        return 'expired';
      }
      return 'unavailable';
    }
    sessionStorage.setItem(ADMIN_TOKEN_KEY, session.accessToken);
    sessionStorage.setItem(ADMIN_REFRESH_TOKEN_KEY, session.refreshToken);
    return 'refreshed';
  } catch {
    return 'unavailable';
  }
}

async function adminFetch(url, options = {}, retried = false) {
  const headers = new Headers(options.headers || {});
  const token = sessionStorage.getItem(ADMIN_TOKEN_KEY);
  if (token) headers.set('Authorization', `Bearer ${token}`);
  const response = await fetch(url, { ...options, headers });
  if (response.status !== 401 || retried) return response;

  const refresh = await refreshAdminSession();
  if (refresh === 'refreshed') return adminFetch(url, options, true);
  if (refresh === 'expired') showAdminLogin('انتهت جلسة الإدارة. سجل الدخول من جديد.');
  else {
    const error = new Error('تعذر الاتصال بالخادم لتجديد جلسة الإدارة.');
    error.code = 'admin_network_error';
    throw error;
  }
  return response;
}

async function adminLogout() {
  let revoked = false;
  try {
    const response = await adminFetch('/api/auth/logout', { method: 'POST' });
    revoked = response.ok;
  } catch {
    // Remove browser credentials even when the server cannot be reached.
  }
  showAdminLogin(revoked
    ? 'تم تسجيل الخروج.'
    : 'تم الخروج من هذا المتصفح، لكن تعذر الاتصال بالخادم لإلغاء الجلسة.');
}

function toast(message, error = false) {
  const item = document.createElement('div');
  item.className = `toast${error ? ' error' : ''}`;
  item.textContent = message;
  $('#toast-region').append(item);
  setTimeout(() => item.remove(), 4200);
}

function updateAdminNotificationBadge() {
  const count = state.notifications.filter(item => item.isRead !== true).length;
  const badge = $('#admin-notifications-count');
  badge.textContent = count > 99 ? '99+' : String(count);
  badge.classList.toggle('hidden', count === 0);
}

function renderAdminNotifications() {
  const list = $('#admin-notifications-list');
  if (!state.notifications.length) {
    list.innerHTML = '<div class="empty-state">لا توجد إشعارات لحساب الإدارة هذا.</div>';
    return;
  }
  list.replaceChildren(...state.notifications.map(item => {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = `admin-notification-item${item.isRead === true ? '' : ' unread'}`;
    button.innerHTML = `<strong>${escapeHtml(item.title || 'إشعار')}</strong><span>${escapeHtml(item.body || '')}</span><small>${escapeHtml(date(item.createdAtUtc))}</small>`;
    button.addEventListener('click', () => openAdminNotification(item).catch(error => {
      if (!isAdminOperationCancelled(error)) toast(error.message, true);
    }));
    return button;
  }));
}

async function loadAdminNotifications() {
  const result = await execute('NotificationModel', 'list', {});
  state.notifications = Array.isArray(result.data) ? result.data : [];
  updateAdminNotificationBadge();
  renderAdminNotifications();
}

function readNotificationRoutingData(notification) {
  try {
    const value = typeof notification.dataJson === 'string' ? JSON.parse(notification.dataJson) : {};
    return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
  } catch {
    return {};
  }
}

async function openAdminNotification(notification) {
  if (notification.isRead !== true) {
    await execute('NotificationModel', 'update', { id: notification.id, isRead: true });
    notification.isRead = true;
    updateAdminNotificationBadge();
    renderAdminNotifications();
  }
  $('#admin-notifications-modal').close();
  const routing = readNotificationRoutingData(notification);
  const action = String(routing.action || '').toLowerCase();
  if (action === 'open_safety_incident') return setView('safety');
  if (action === 'open_safety_recording') return setView('safety-recordings');
  if (action === 'open_cancellation_request') return setView('cancellations');
  if (action === 'open_driver_account') return setView('settlements');
  if (routing.rideId) return setView('rides');
}

function toggleEmpty(bodyId, emptyId, rows) {
  $(bodyId).innerHTML = rows.join('');
  $(emptyId).classList.toggle('hidden', rows.length > 0);
  $(bodyId).closest('.table-wrap').classList.toggle('hidden', rows.length === 0);
}

function rideBadge(status) {
  const tone = status === 6 ? 'success' : status === 7 ? 'danger' : status >= 3 ? 'info' : 'warning';
  return badge(rideStatuses[status] ?? status, tone);
}

async function loadDatabaseStatus() {
  try {
    const settings = await fetch('/api/setup/database').then(response => response.json());
    state.databaseConfigured = settings.configured;
    $('#database-dot').classList.toggle('online', settings.configured);
    $('#connection-pill').classList.toggle('online', settings.configured);
    $('#connection-pill b').textContent = settings.configured ? 'قاعدة البيانات متصلة' : 'الاتصال غير معد';
    $('#database-label').textContent = settings.configured ? `${settings.server} / ${settings.database}` : 'يلزم إعداد الاتصال';
    fillDatabaseForm(settings);
    return settings;
  } catch {
    $('#database-label').textContent = 'تعذر التحقق';
    return null;
  }
}

async function loadDashboard() {
  const result = await execute('DashboardModel', 'report', {});
  const data = result.data;
  $('#stat-users').textContent = number(data.users);
  $('#stat-drivers').textContent = number(data.drivers);
  $('#stat-active-rides').textContent = number(data.activeRides);
  $('#stat-wallet').textContent = number(data.walletBalance);
  const rows = data.recentRides.map(item => `<tr><td><span class="cell-main">${escapeHtml(item.id)}</span></td><td><span class="cell-main">${escapeHtml(item.pickupDisplayName || item.pickupLabel || 'نقطة الانطلاق')}</span><span class="cell-sub">${escapeHtml(item.destinationDisplayName || item.destinationLabel || 'الوجهة')}</span></td><td>${escapeHtml(item.serviceKindNameAr || item.serviceKindId)} / ${escapeHtml(item.serviceNameAr || item.serviceCatalogItemId)}</td><td>${rideBadge(item.status)}</td><td>${number(item.customerPrice)} YER</td><td>${date(item.createdAtUtc)}</td></tr>`);
  toggleEmpty('#recent-rides-body', '#overview-empty', rows);
}

async function loadUsers(search = '') {
  const result = await execute('UserModel', search ? 'search' : 'list', search ? { displayName: search } : {});
  state.users = result.data;
  const rows = result.data.map(item => `<tr><td><span class="cell-main">${escapeHtml(item.displayName || 'بدون اسم')}</span><span class="cell-sub">${escapeHtml(item.id)}</span></td><td>${escapeHtml(item.phoneNumber)}</td><td>${escapeHtml(item.email || '—')}</td><td>${badge(roles[item.role] ?? item.role, item.role === 2 ? 'warning' : item.role === 1 ? 'info' : '')}</td><td>${escapeHtml(item.city || '—')}</td><td>${badge(item.isActive ? 'نشط' : 'موقوف', item.isActive ? 'success' : 'danger')}</td><td>${date(item.createdAtUtc)}</td><td>${recordActions('user', item.id)}</td></tr>`);
  toggleEmpty('#users-body', '#users-empty', rows);
}

async function loadCustomers(search = '') {
  const result = await execute('CustomerAssessmentModel', search ? 'search' : 'list', {
    query: search || null,
    sortBy: state.customerSort.sortBy,
    descending: state.customerSort.descending
  });
  state.customers = Array.isArray(result.data) ? result.data : [];
  const rows = state.customers.map(item => {
    const name = item.displayName || 'بدون اسم';
    const status = item.isActive
      ? iconActionButton('suspend', 'إيقاف الحساب', `setCustomerStatus(${Number(item.id)}, false)`, { tone: 'warning' })
      : iconActionButton('activate', 'تفعيل الحساب', `setCustomerStatus(${Number(item.id)}, true)`, { tone: 'success' });
    const actions = `<div class="customer-actions">${iconActionButton('edit', 'تعديل بيانات العميل', `editAdminEntity('user', ${Number(item.id)})`)}${iconActionButton('delete', 'حذف حساب العميل', `deleteAdminEntity('user', ${Number(item.id)})`)}${status}${iconActionButton('statement', 'كشف حساب العميل', `openCustomerAccountStatement(${Number(item.id)})`, { tone: 'info' })}${iconActionButton('wallet', 'كشف محفظة العميل', `openCustomerWallet(${Number(item.id)})`, { tone: 'success' })}${iconActionButton('assessment', 'تقرير تقييم العميل', `openCustomerAssessment(${Number(item.id)})`, { tone: 'gold' })}</div>`;
    return `<tr><td><span class="cell-main">${escapeHtml(name)}</span><span class="cell-sub">#${Number(item.id)}</span></td><td><span class="cell-main">${escapeHtml(item.phoneNumber)}</span><span class="cell-sub">${escapeHtml(item.email || 'لا يوجد بريد')}</span></td><td><span class="cell-main">${escapeHtml(item.city || '—')}</span><span class="cell-sub">${escapeHtml([item.district, item.street].filter(Boolean).join('، ') || '')}</span></td><td>${number(item.requestedRideCount)} / ${number(item.completedRideCount)}</td><td>${number(item.cancelledRideCount)}<span class="cell-sub">${number(item.cancellationRate)}٪</span></td><td>${number(item.collectedRideRevenue)} YER</td><td>${number(item.walletBalance)} YER</td><td>${number(item.safetyReportCount)}</td><td><span class="cell-main">${escapeHtml(item.label)}</span><span class="cell-sub">${number(item.score)} / 100</span></td><td>${date(item.lastRequestAtUtc)}</td><td>${badge(item.isActive ? 'نشط' : 'موقوف', item.isActive ? 'success' : 'danger')}</td><td>${actions}</td></tr>`;
  });
  toggleEmpty('#customers-body', '#customers-empty', rows);
}

window.setCustomerStatus = async (id, isActive) => {
  try {
    const result = await execute('UserModel', 'update', { id: Number(id), role: 0, isActive });
    toast(result.message || (isActive ? 'تم تفعيل الحساب.' : 'تم إيقاف الحساب.'));
    await loadCustomers($('#customer-search').value.trim());
  } catch (error) { if (!isAdminOperationCancelled(error)) toast(error.message, true); }
};

window.openCustomerAssessment = id => {
  const item = state.customers.find(customer => Number(customer.id) === Number(id));
  if (!item) return toast('تعذر العثور على بيانات العميل. حدّث القائمة ثم حاول مرة أخرى.', true);
  $('#assessment-customer-name').textContent = item.displayName || 'بدون اسم';
  $('#assessment-customer-contact').textContent = `${item.phoneNumber || '—'} · ${item.email || 'لا يوجد بريد'}`;
  $('#assessment-label').textContent = item.label || 'لا يوجد سجل كافٍ';
  $('#assessment-score').textContent = number(item.score);
  const metrics = [
    ['الطلبات المسجلة', number(item.requestedRideCount)], ['الرحلات المكتملة', number(item.completedRideCount)],
    ['الرحلات الملغاة', `${number(item.cancelledRideCount)} (${number(item.cancellationRate)}٪)`],
    ['صافي الإيراد المحصل', `${number(item.collectedRideRevenue)} YER`],
    ['رصيد المحفظة الحالي', `${number(item.walletBalance)} YER`], ['البلاغات', number(item.safetyReportCount)],
    ['المدينة', item.city || '—'], ['تاريخ إنشاء الحساب', date(item.createdAtUtc)]
  ];
  $('#customer-assessment-details').innerHTML = metrics.map(([label, value]) => `<div class="customer-assessment-metric"><span>${escapeHtml(label)}</span><strong>${escapeHtml(value)}</strong></div>`).join('');
  $('#customer-assessment-modal').showModal();
};

window.openCustomerWallet = async id => {
  const item = state.customers.find(customer => Number(customer.id) === Number(id));
  if (!item) return toast('تعذر العثور على بيانات العميل.', true);
  try {
    $('#wallet-phone-number').value = item.phoneNumber || '';
    await setView('wallet');
    await loadWallet();
  } catch (error) { toast(error.message, true); }
};

window.openCustomerAccountStatement = async id => {
  const item = state.customers.find(customer => Number(customer.id) === Number(id));
  if (!item) return toast('تعذر العثور على بيانات العميل.', true);
  try {
    await loadLedgerAccounts();
    const account = state.ledgerAccounts.find(row => row.code === `2100-WLT-${item.walletId}`);
    if (!account) return toast('لا يوجد حساب محاسبي للمحفظة حتى الآن.', true);
    $('#statement-account').value = String(account.id);
    $('#statement-account-search').value = statementAccountLabel(account);
    await setView('ledger');
  } catch (error) { toast(error.message, true); }
};

window.setDriverAccountStatus = async (userId, isActive) => {
  try {
    const result = await execute('UserModel', 'update', { id: Number(userId), role: 1, isActive });
    toast(result.message || (isActive ? 'تم تفعيل حساب السائق.' : 'تم إيقاف حساب السائق.'));
    await loadDrivers();
  } catch (error) { if (!isAdminOperationCancelled(error)) toast(error.message, true); }
};

window.openDriverWallet = async userId => {
  const item = state.drivers.find(driver => Number(driver.userId) === Number(userId));
  if (!item) return toast('تعذر العثور على بيانات السائق.', true);
  try {
    $('#wallet-phone-number').value = item.phoneNumber || '';
    await setView('wallet');
    await loadWallet();
  } catch (error) { toast(error.message, true); }
};

window.openDriverAccountStatement = async userId => {
  try {
    await loadLedgerAccounts();
    const account = state.ledgerAccounts.find(row => row.code === `1100-DRV-${Number(userId)}`);
    if (!account) return toast('لا يوجد حساب محاسبي للسائق حتى الآن.', true);
    $('#statement-account').value = String(account.id);
    $('#statement-account-search').value = statementAccountLabel(account);
    await setView('ledger');
  } catch (error) { toast(error.message, true); }
};

async function loadDriverRatings() {
  const filter = $('#driver-rating-filter');
  const selectedDriverId = Number(filter.value) || null;
  const result = await execute('DriverRatingReportModel', 'list', selectedDriverId ? { driverUserId: selectedDriverId } : {});
  state.driverRatingReport = result.data || { drivers: [], ratings: [] };
  const drivers = state.driverRatingReport.drivers || [];
  const previousSelection = filter.value;
  filter.innerHTML = `<option value="">كل السائقين</option>${drivers.map(item => `<option value="${Number(item.driverUserId)}">${escapeHtml(item.driverName || 'بدون اسم')} · ${escapeHtml(item.driverPhone || '')}</option>`).join('')}`;
  if ([...filter.options].some(option => option.value === previousSelection)) filter.value = previousSelection;

  const selected = drivers.find(item => Number(item.driverUserId) === Number(filter.value));
  const ratings = state.driverRatingReport.ratings || [];
  const customerRatings = ratings.filter(item => Number(item.source) === 0);
  const adminRatings = ratings.filter(item => Number(item.source) === 1);
  const weighted = (customerAvg, customerCount, adminAvg, adminCount) => {
    const count = Number(customerCount) + Number(adminCount);
    return count ? (Number(customerAvg) * Number(customerCount) + Number(adminAvg) * Number(adminCount)) / count : 0;
  };
  if (selected) {
    $('#driver-customer-average').textContent = Number(selected.customerAverage || 0).toFixed(1);
    $('#driver-customer-count').textContent = `${number(selected.customerRatingCount)} تقييم من العملاء`;
    $('#driver-admin-average').textContent = Number(selected.adminAverage || 0).toFixed(1);
    $('#driver-admin-count').textContent = `${number(selected.adminRatingCount)} تقييم من الإدارة`;
    const allReviewCount = Number(selected.customerRatingCount) + Number(selected.adminRatingCount);
    $('#driver-combined-average').textContent = allReviewCount
      ? weighted(selected.customerAverage, selected.customerRatingCount, selected.adminAverage, selected.adminRatingCount).toFixed(1)
      : '—';
  } else {
    const customerCount = drivers.reduce((sum, item) => sum + Number(item.customerRatingCount || 0), 0);
    const adminCount = drivers.reduce((sum, item) => sum + Number(item.adminRatingCount || 0), 0);
    const customerTotal = drivers.reduce((sum, item) => sum + Number(item.customerAverage || 0) * Number(item.customerRatingCount || 0), 0);
    const adminTotal = drivers.reduce((sum, item) => sum + Number(item.adminAverage || 0) * Number(item.adminRatingCount || 0), 0);
    $('#driver-customer-average').textContent = customerCount ? (customerTotal / customerCount).toFixed(1) : '—';
    $('#driver-customer-count').textContent = `${number(customerCount)} تقييم من العملاء`;
    $('#driver-admin-average').textContent = adminCount ? (adminTotal / adminCount).toFixed(1) : '—';
    $('#driver-admin-count').textContent = `${number(adminCount)} تقييم من الإدارة`;
    const combinedCount = customerCount + adminCount;
    $('#driver-combined-average').textContent = combinedCount ? ((customerTotal + adminTotal) / combinedCount).toFixed(1) : '—';
  }

  const rows = ratings.map(item => {
    const fromCustomer = Number(item.source) === 0;
    const author = fromCustomer ? `${item.customerName || 'عميل'} · ${item.customerPhone || ''}` : `${item.adminName || 'الإدارة'}`;
    return `<tr><td>${date(item.createdAtUtc)}</td><td><span class="cell-main">${escapeHtml(item.driverName || '—')}</span><span class="cell-sub">${escapeHtml(item.driverPhone || '')}</span></td><td>${badge(fromCustomer ? 'العميل' : 'الإدارة', fromCustomer ? 'info' : 'warning')}</td><td>${number(item.score)} / 5</td><td>${item.rideId ? `#${Number(item.rideId)}` : '—'}</td><td>${escapeHtml(author)}</td><td>${escapeHtml(item.comment || '—')}</td></tr>`;
  });
  toggleEmpty('#driver-ratings-body', '#driver-ratings-empty', rows);
}

async function loadNotificationCampaigns() {
  const result = await execute('AdminNotificationCampaignModel', 'list', {});
  state.notificationCampaigns = Array.isArray(result.data) ? result.data : [];
  const rows = state.notificationCampaigns.map(item => {
    const target = notificationCampaignTargets[Number(item.target)] || 'غير محدد';
    const source = item.sourceCampaignId
      ? `<span class="cell-sub">إعادة إرسال من #${Number(item.sourceCampaignId)}</span>`
      : '—';
    const actions = `<div class="notification-campaign-actions">${iconActionButton('resend', 'إعادة إرسال كحملة جديدة', `resendNotificationCampaign(${Number(item.id)})`, { tone: 'info' })}${iconActionButton('delete', 'حذف سجل الحملة', `deleteNotificationCampaign(${Number(item.id)})`)}</div>`;
    return `<tr><td>${date(item.createdAtUtc)}</td><td><div class="notification-campaign-text">${escapeHtml(item.body || '')}</div></td><td>${escapeHtml(target)}</td><td>${number(item.recipientCount)}</td><td>${escapeHtml(item.createdByName || '—')}</td><td>${source}</td><td>${actions}</td></tr>`;
  });
  toggleEmpty('#notification-campaigns-body', '#notification-campaigns-empty', rows);
}

window.deleteNotificationCampaign = async id => {
  try {
    const result = await execute('AdminNotificationCampaignModel', 'delete', { id: Number(id) });
    toast(result.message || 'تم حذف سجل الحملة.');
    await loadNotificationCampaigns();
  } catch (error) {
    if (!isAdminOperationCancelled(error)) toast(error.message || 'تعذر حذف سجل الحملة.', true);
  }
};

window.resendNotificationCampaign = async id => {
  const campaign = state.notificationCampaigns.find(item => Number(item.id) === Number(id));
  if (!campaign) return toast('تعذر العثور على الإشعار. حدّث السجل ثم حاول مرة أخرى.', true);
  try {
    await openForm('notificationCampaign');
    activeForm.sourceCampaignId = Number(campaign.id);
    $('#modal-fields [name="body"]').value = campaign.body || '';
    $('#modal-fields [name="target"]').value = String(campaign.target ?? 2);
    $('#modal-subtitle').textContent = `نسخة جديدة من الحملة #${Number(campaign.id)}. يمكنك تعديل النص أو الفئة؛ لن تتغير الحملة الأصلية.`;
  } catch (error) {
    toast(error.message || 'تعذر فتح نموذج إعادة الإرسال.', true);
  }
};

async function loadDrivers() {
  const result = await execute('DriverModel', 'list', {});
  state.drivers = result.data;
  const rows = result.data.map(item => {
    const userId = Number(item.userId);
    const status = item.isActive
      ? iconActionButton('suspend', 'إيقاف حساب السائق', `setDriverAccountStatus(${userId}, false)`, { tone: 'warning' })
      : iconActionButton('activate', 'تفعيل حساب السائق', `setDriverAccountStatus(${userId}, true)`, { tone: 'success' });
    const actions = `<div class="driver-actions">${status}${iconActionButton('statement', 'كشف حساب السائق', `openDriverAccountStatement(${userId})`, { tone: 'info' })}${iconActionButton('wallet', 'كشف محفظة السائق', `openDriverWallet(${userId})`, { tone: 'success' })}${recordActions('driver', item.id)}</div>`;
    return `<tr><td><span class="cell-main">${escapeHtml(item.displayName || 'بدون اسم')}</span><span class="cell-sub">${escapeHtml(item.userId)}</span></td><td>${escapeHtml(item.phoneNumber)}</td><td><span class="cell-main">${escapeHtml(item.vehicleModel)}</span></td><td>${escapeHtml(item.serviceKindNameAr || item.serviceKindId)} / ${escapeHtml(item.serviceNameAr || item.serviceCatalogItemId)}</td><td>${escapeHtml(item.plateNumber)}</td><td>${number(item.rating)}</td><td>${badge(item.isActive ? 'الحساب نشط' : 'الحساب موقوف', item.isActive ? 'success' : 'danger')}<span class="cell-sub">${item.isAvailable ? 'متاح' : 'غير متاح'}</span></td><td>${actions}</td></tr>`;
  });
  toggleEmpty('#drivers-body', '#drivers-empty', rows);
}

async function loadRides() {
  const value = $('#ride-filter').value;
  const result = await execute('RideModel', 'list', value ? { status: Number(value) } : {});
  state.rides = result.data;
  const rows = result.data.map(item => {
    const ridingOptions = `${item.moreThanFourPassengers ? '<span class="cell-sub">أكثر من 4 ركاب</span>' : ''}${item.passengerNotes ? `<span class="cell-sub">${escapeHtml(item.passengerNotes)}</span>` : ''}`;
    const lockedReason = Number(item.status) === 0 ? '' : 'تُدار الرحلات بعد بدء البحث عبر سير العمل التشغيلي والإلغاء الرسمي.';
    return `<tr><td><span class="cell-main">${escapeHtml(item.customerName || item.customerId)}</span></td><td>${escapeHtml(item.driverName || 'لم يحدد')}</td><td><span class="cell-main">${escapeHtml(item.pickupDisplayName || item.pickupLabel || 'نقطة الانطلاق')}</span><span class="cell-sub">${escapeHtml(item.destinationDisplayName || item.destinationLabel || 'الوجهة')}</span></td><td>${escapeHtml(item.serviceKindNameAr || item.serviceKindId)} / ${escapeHtml(item.serviceNameAr || item.serviceCatalogItemId)}</td><td>${rideBadge(item.status)}</td><td>${number(item.customerPrice)} YER</td><td>${date(item.createdAtUtc)}</td><td>${ridingOptions}<button class="btn btn-sm" type="button" data-waiting-ride="${Number(item.id)}">الانتظار ${number(item.waitingFee || 0)} ر.ي</button>${recordActions('ride', item.id, lockedReason)}</td></tr>`;
  });
  toggleEmpty('#rides-body', '#rides-empty', rows);
}

const LIVE_RIDE_STATUSES = new Set([3, 4, 5, 8, 9]);
const isLiveRide = ride => LIVE_RIDE_STATUSES.has(Number(ride?.status));

function setLiveConnectionStatus(label, connected = false) {
  const status = $('#live-rides-connection');
  if (!status) return;
  status.textContent = label;
  status.classList.toggle('connected', connected);
}

function ensureLiveRidesMap() {
  if (liveRidesMap || !window.L || !$('#live-rides-map')) return;
  liveRidesMap = L.map('live-rides-map', { preferCanvas: true }).setView([15.3694, 44.1910], 12);
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '&copy; OpenStreetMap contributors'
  }).addTo(liveRidesMap);
}

function liveRideCoordinates(ride, prefix) {
  return validMapPoint(ride?.[`${prefix}Latitude`], ride?.[`${prefix}Longitude`]);
}

function getLiveRideMarkerEntry(rideId) {
  if (!liveRideMarkers.has(rideId)) {
    liveRideMarkers.set(rideId, { driver: null, passenger: null, pickup: null, destination: null });
  }
  return liveRideMarkers.get(rideId);
}

function driverMarkerIcon(rideId) {
  return L.divIcon({
    className: '',
    html: `<div class="live-ride-pin" id="live-ride-pin-${Number(rideId)}"><span>🚕</span></div>`,
    iconSize: [34, 34],
    iconAnchor: [17, 17]
  });
}

function passengerMarkerIcon(rideId) {
  return L.divIcon({
    className: '',
    html: `<div class="live-passenger-pin" id="live-passenger-pin-${Number(rideId)}"><span>👤</span></div>`,
    iconSize: [32, 32],
    iconAnchor: [16, 16]
  });
}

function ensureDriverMarker(ride) {
  ensureLiveRidesMap();
  if (!liveRidesMap) return null;
  const rideId = Number(ride.id);
  const point = liveRideCoordinates(ride, 'driver');
  if (!point) return null;
  const entry = getLiveRideMarkerEntry(rideId);
  if (!entry.driver) {
    entry.driver = L.marker([point.lat, point.lon], { icon: driverMarkerIcon(rideId) })
      .addTo(liveRidesMap)
      .bindPopup(`الرحلة #${rideId} — ${escapeHtml(ride.driverName || 'السائق')}`);
  }
  return entry.driver;
}

function ensurePassengerMarker(ride) {
  ensureLiveRidesMap();
  if (!liveRidesMap) return null;
  const rideId = Number(ride.id);
  const point = liveRideCoordinates(ride, 'passenger');
  if (!point) return null;
  const entry = getLiveRideMarkerEntry(rideId);
  if (!entry.passenger) {
    entry.passenger = L.marker([point.lat, point.lon], { icon: passengerMarkerIcon(rideId) })
      .addTo(liveRidesMap)
      .bindPopup(`الرحلة #${rideId} — ${escapeHtml(ride.customerName || 'الراكب')}`);
  }
  return entry.passenger;
}

function renderLiveRide(ride) {
  ensureLiveRidesMap();
  if (!liveRidesMap || !ride) return;
  const rideId = Number(ride.id);
  const entry = getLiveRideMarkerEntry(rideId);
  const pickup = liveRideCoordinates(ride, 'pickup');
  const destination = liveRideCoordinates(ride, 'destination');
  if (pickup && !entry.pickup) {
    entry.pickup = L.circleMarker([pickup.lat, pickup.lon], {
      radius: 6, color: '#fff', weight: 2, fillColor: '#08766a', fillOpacity: 1
    }).addTo(liveRidesMap).bindTooltip(`انطلاق #${rideId}`);
  }
  if (destination && !entry.destination) {
    entry.destination = L.circleMarker([destination.lat, destination.lon], {
      radius: 6, color: '#fff', weight: 2, fillColor: '#bd7a18', fillOpacity: 1
    }).addTo(liveRidesMap).bindTooltip(`وجهة #${rideId}`);
  }
  const marker = ensureDriverMarker(ride);
  if (marker && Number.isFinite(Number(ride.driverBearing))) {
    const pin = marker.getElement()?.querySelector('.live-ride-pin span');
    if (pin) pin.style.transform = `rotate(${Number(ride.driverBearing)}deg)`;
  }
  const passengerMarker = ensurePassengerMarker(ride);
  if (passengerMarker && Number.isFinite(Number(ride.passengerBearing))) {
    const pin = passengerMarker.getElement()?.querySelector('.live-passenger-pin span');
    if (pin) pin.style.transform = `rotate(${Number(ride.passengerBearing)}deg)`;
  }
}

function removeLiveRide(rideId) {
  const entry = liveRideMarkers.get(Number(rideId));
  if (entry && liveRidesMap) {
    for (const marker of Object.values(entry)) if (marker) liveRidesMap.removeLayer(marker);
  }
  liveRideMarkers.delete(Number(rideId));
  const animation = liveRideAnimations.get(Number(rideId));
  if (animation) cancelAnimationFrame(animation);
  liveRideAnimations.delete(Number(rideId));
  const passengerAnimation = livePassengerAnimations.get(Number(rideId));
  if (passengerAnimation) cancelAnimationFrame(passengerAnimation);
  livePassengerAnimations.delete(Number(rideId));
}

function renderLiveRideList() {
  const container = $('#live-rides-list');
  if (!container) return;
  const rides = [...state.liveRides.values()].filter(isLiveRide);
  container.innerHTML = rides.length ? rides.map(ride => {
    const id = Number(ride.id);
    const pickup = ride.pickupDisplayName || ride.pickupAddress || 'نقطة الانطلاق';
    const destination = ride.destinationDisplayName || ride.destinationAddress || 'الوجهة';
    const status = rideStatuses[Number(ride.status)] || 'نشطة';
    return `<button type="button" class="live-ride-card" data-live-ride="${id}"><strong>رحلة #${id} · ${escapeHtml(status)}</strong><span>${escapeHtml(ride.driverName || 'بانتظار السائق')} · ${escapeHtml(ride.customerName || 'عميل')}</span><span>${escapeHtml(pickup)} ← ${escapeHtml(destination)}</span><span>${ride.driverLocationObservedAtUtc ? `آخر موقع ${date(ride.driverLocationObservedAtUtc)}` : 'بانتظار أول موقع من السائق'}</span></button>`;
  }).join('') : '<div class="empty-state">لا توجد رحلات نشطة الآن.</div>';
  container.querySelectorAll('[data-live-ride]').forEach(button => button.addEventListener('click', () => {
    const ride = state.liveRides.get(Number(button.dataset.liveRide));
    if (!ride || !liveRidesMap) return;
    const point = liveRideCoordinates(ride, 'driver') || liveRideCoordinates(ride, 'passenger') || liveRideCoordinates(ride, 'pickup');
    if (point) liveRidesMap.setView([point.lat, point.lon], 15, { animate: true });
    container.querySelectorAll('.live-ride-card').forEach(card => card.classList.toggle('selected', card === button));
  }));
}

function renderAllLiveRides() {
  for (const rideId of [...liveRideMarkers.keys()]) {
    if (!state.liveRides.has(rideId)) removeLiveRide(rideId);
  }
  for (const ride of state.liveRides.values()) renderLiveRide(ride);
  renderLiveRideList();
  if (liveRidesMap) setTimeout(() => liveRidesMap.invalidateSize(), 0);
}

async function loadActiveRideSnapshot() {
  const result = await execute('RideModel', 'list', { activeOnly: true });
  const rides = (result.data || []).filter(isLiveRide);
  state.liveRides = new Map(rides.map(ride => [Number(ride.id), ride]));
  renderAllLiveRides();
}

function animateLiveDriverLocation(payload) {
  const rideId = Number(payload?.rideId);
  const latitude = Number(payload?.latitude);
  const longitude = Number(payload?.longitude);
  if (!Number.isInteger(rideId) || !Number.isFinite(latitude) || !Number.isFinite(longitude)) return;
  const ride = state.liveRides.get(rideId);
  if (!ride) {
    if (pendingLiveRideSnapshots.has(rideId)) return;
    pendingLiveRideSnapshots.add(rideId);
    loadActiveRideSnapshot()
      .then(() => {
        if (state.liveRides.has(rideId)) animateLiveDriverLocation(payload);
      })
      .catch(error => toast(error.message, true))
      .finally(() => pendingLiveRideSnapshots.delete(rideId));
    return;
  }
  const updated = {
    ...ride,
    driverLatitude: latitude,
    driverLongitude: longitude,
    driverBearing: payload.bearing,
    driverSpeed: payload.speed,
    driverLocationObservedAtUtc: payload.observedAtUtc
  };
  state.liveRides.set(rideId, updated);
  const marker = ensureDriverMarker(updated);
  if (!marker) return;
  const current = marker.getLatLng();
  const oldAnimation = liveRideAnimations.get(rideId);
  if (oldAnimation) cancelAnimationFrame(oldAnimation);
  const startedAt = performance.now();
  const duration = 1700;
  const step = now => {
    const progress = Math.min(1, (now - startedAt) / duration);
    marker.setLatLng([
      current.lat + (latitude - current.lat) * progress,
      current.lng + (longitude - current.lng) * progress
    ]);
    const pin = marker.getElement()?.querySelector('.live-ride-pin span');
    if (pin && Number.isFinite(Number(payload.bearing))) pin.style.transform = `rotate(${Number(payload.bearing)}deg)`;
    if (progress < 1) liveRideAnimations.set(rideId, requestAnimationFrame(step));
    else liveRideAnimations.delete(rideId);
  };
  liveRideAnimations.set(rideId, requestAnimationFrame(step));
  renderLiveRideList();
}

function animateLivePassengerLocation(payload) {
  const rideId = Number(payload?.rideId);
  const latitude = Number(payload?.latitude);
  const longitude = Number(payload?.longitude);
  if (!Number.isInteger(rideId) || !Number.isFinite(latitude) || !Number.isFinite(longitude)) return;
  if (payload?.rideStatus != null && !LIVE_RIDE_STATUSES.has(Number(payload.rideStatus))) return;
  const ride = state.liveRides.get(rideId);
  if (!ride) {
    if (pendingLiveRideSnapshots.has(rideId)) return;
    pendingLiveRideSnapshots.add(rideId);
    loadActiveRideSnapshot()
      .then(() => {
        if (state.liveRides.has(rideId)) animateLivePassengerLocation(payload);
      })
      .catch(error => toast(error.message, true))
      .finally(() => pendingLiveRideSnapshots.delete(rideId));
    return;
  }
  const updated = {
    ...ride,
    passengerLatitude: latitude,
    passengerLongitude: longitude,
    passengerBearing: payload.bearing,
    passengerSpeed: payload.speed,
    passengerLocationObservedAtUtc: payload.observedAtUtc
  };
  state.liveRides.set(rideId, updated);
  const marker = ensurePassengerMarker(updated);
  if (!marker) return;
  const current = marker.getLatLng();
  const oldAnimation = livePassengerAnimations.get(rideId);
  if (oldAnimation) cancelAnimationFrame(oldAnimation);
  const startedAt = performance.now();
  const duration = 1700;
  const step = now => {
    const progress = Math.min(1, (now - startedAt) / duration);
    marker.setLatLng([
      current.lat + (latitude - current.lat) * progress,
      current.lng + (longitude - current.lng) * progress
    ]);
    const pin = marker.getElement()?.querySelector('.live-passenger-pin span');
    if (pin && Number.isFinite(Number(payload.bearing))) pin.style.transform = `rotate(${Number(payload.bearing)}deg)`;
    if (progress < 1) livePassengerAnimations.set(rideId, requestAnimationFrame(step));
    else livePassengerAnimations.delete(rideId);
  };
  livePassengerAnimations.set(rideId, requestAnimationFrame(step));
  renderLiveRideList();
}

function applyRideStatusEvent(payload) {
  const rideId = Number(payload?.rideId);
  const status = Number(payload?.rideStatus);
  if (!Number.isInteger(rideId) || !Number.isFinite(status)) return;
  const ride = state.liveRides.get(rideId);
  if (!LIVE_RIDE_STATUSES.has(status)) {
    state.liveRides.delete(rideId);
    removeLiveRide(rideId);
    renderLiveRideList();
    return;
  }
  if (!ride) {
    loadActiveRideSnapshot().catch(error => toast(error.message, true));
    return;
  }
  const updated = { ...ride, status, driverId: payload.driverId, customerPaymentEnabled: payload.customerPaymentEnabled };
  state.liveRides.set(rideId, updated);
  renderLiveRide(updated);
  renderLiveRideList();
}

async function startAdminRealtime() {
  if (!sessionStorage.getItem(ADMIN_TOKEN_KEY) || !window.signalR) return;
  if (adminHubConnection?.state === signalR.HubConnectionState.Connected) return;
  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/tracking', {
      accessTokenFactory: () => sessionStorage.getItem(ADMIN_TOKEN_KEY) || ''
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();
  adminHubConnection = connection;
  connection.on('driverLocationUpdated', animateLiveDriverLocation);
  connection.on('passengerLocationUpdated', animateLivePassengerLocation);
  connection.on('rideStatusUpdated', applyRideStatusEvent);
  connection.on('notificationCreated', () => {
    loadAdminNotifications()
      .then(() => toast('ورد إشعار جديد للإدارة.'))
      .catch(error => toast(error.message, true));
  });
  const refreshSafetyIncidents = () => {
    if (state.view === 'safety') loadSafety().catch(error => toast(error.message, true));
  };
  connection.on('safetyIncidentCreated', refreshSafetyIncidents);
  connection.on('safetyIncidentUpdated', refreshSafetyIncidents);
  connection.on('safetyRecordingAvailable', () => {
    if (state.view === 'safety-recordings') {
      loadSafetyRecordings().catch(error => toast(error.message, true));
    }
  });
  connection.on('sessionRevoked', () => showAdminLogin('انتهت جلسة الإدارة. سجل الدخول من جديد.'));
  connection.onreconnecting(() => setLiveConnectionStatus('إعادة الاتصال…'));
  connection.onreconnected(async () => {
    try {
      await connection.invoke('JoinOperations');
      setLiveConnectionStatus('متصل مباشرًا', true);
      await loadActiveRideSnapshot();
    } catch (error) {
      setLiveConnectionStatus('تعذر استعادة المتابعة');
      toast(error.message, true);
    }
  });
  connection.onclose(() => setLiveConnectionStatus('غير متصل'));
  try {
    setLiveConnectionStatus('جارٍ الاتصال…');
    await connection.start();
    await connection.invoke('JoinOperations');
    setLiveConnectionStatus('متصل مباشرًا', true);
    await loadActiveRideSnapshot();
  } catch (error) {
    setLiveConnectionStatus('تعذر الاتصال المباشر');
    if (adminHubConnection === connection) adminHubConnection = null;
    setTimeout(() => startAdminRealtime().catch(() => {}), 5000);
  }
}

function stopAdminRealtime() {
  const connection = adminHubConnection;
  adminHubConnection = null;
  if (connection) connection.stop().catch(() => {});
  setLiveConnectionStatus('غير متصل');
}

async function loadCatalog() {
  const [catalog, kinds] = await Promise.all([execute('ServiceCatalogModel','list',{}), execute('ServiceKindModel','list',{})]);
  state.catalog = catalog.data; state.kinds = kinds.data;
  toggleEmpty('#catalog-body','#catalog-empty',catalog.data.map(item => `<tr><td>${item.imageUrl ? `<img class="table-icon" src="${escapeHtml(item.imageUrl)}" alt="">` : '—'}</td><td>${escapeHtml(item.code)}</td><td>${escapeHtml(item.nameAr)}</td><td>${escapeHtml(item.serviceKindNameAr || item.serviceKindId)}</td><td>${number(item.basePrice)}</td><td>${number(item.arrivalMinutes)} د</td><td>${badge(item.isActive ? 'نشطة' : 'متوقفة', item.isActive ? 'success' : 'danger')}</td><td>${recordActions('serviceCatalog', item.id)}</td></tr>`));
  toggleEmpty('#kinds-body','#kinds-empty',kinds.data.map(item => `<tr><td>${item.imageUrl ? `<img class="table-icon" src="${escapeHtml(item.imageUrl)}" alt="">` : '—'}</td><td>${escapeHtml(item.code)}</td><td>${escapeHtml(item.nameAr)}</td><td>${number(item.sortOrder)}</td><td>${item.isDefault ? badge('افتراضي','success') : (item.isActive ? iconActionButton('default', 'تعيين نوع الخدمة افتراضيًا', `setDefaultServiceKind(${Number(item.id)})`, { tone: 'gold' }) : '—')}</td><td>${badge(item.isActive ? 'نشط' : 'متوقف', item.isActive ? 'success' : 'danger')}</td><td>${recordActions('serviceKind', item.id)}</td></tr>`));
}

window.setDefaultServiceKind = async id => {
  const item = state.kinds.find(kind => Number(kind.id) === Number(id));
  if (!item || !item.isActive) return toast('لا يمكن تعيين نوع خدمة متوقف كافتراضي.', true);
  try {
    await execute('ServiceKindModel', 'update', {
      id: item.id, code: item.code, nameAr: item.nameAr, imageUrl: item.imageUrl,
      isActive: true, isDefault: true, sortOrder: item.sortOrder || 0
    });
    toast('تم تعيين النوع الافتراضي للعميل.');
    await loadCatalog();
  } catch (error) { if (!isAdminOperationCancelled(error)) toast(error.message, true); }
};

async function loadPricing() {
  const result = await execute('PricingRuleModel','list',{}); state.pricing = result.data;
  toggleEmpty('#pricing-body','#pricing-empty',result.data.map(item => `<tr><td>${escapeHtml(item.serviceKindNameAr || item.serviceKindId)}</td><td>${escapeHtml(item.serviceNameAr || item.serviceCatalogItemId)}</td><td>${number(item.baseFare)}</td><td>${number(item.perKilometer)}</td><td>${number(item.perMinute)}</td><td>${number(item.serviceFee)}</td><td>${number((item.driverCommissionRate || 0) * 100)}%</td><td>${number(item.driverCommissionFixed || 0)}</td><td>${number(item.cancellationFee || 0)}</td><td>${number(item.routeChangeFee || 0)}</td><td>${number(item.waitingFreeMinutes || 0)} د</td><td>${number(item.waitingPricePerMinute || 0)}</td><td>${number(item.waitingMaxFee || 0)}</td><td>${number(item.maxPriceDecreasePercent || 0)}%</td><td>${number(item.maxPriceIncreasePercent || 0)}%</td><td>${badge(item.isActive ? 'نشطة' : 'متوقفة', item.isActive ? 'success' : 'danger')}</td><td>${recordActions('pricingRule', item.id)}</td></tr>`));
}

async function loadWallet() {
  const phoneNumber = $('#wallet-phone-number').value.trim();
  if (!phoneNumber) return toast('أدخل رقم هاتف المستخدم أولاً.', true);
  $('#wallet-summary').classList.add('hidden');
  $('#wallet-body').innerHTML = '';
  $('#wallet-empty').classList.remove('hidden');
  $('#wallet-empty').textContent = 'جارٍ البحث عن المحفظة…';
  const result = await execute('WalletModel','get',{ phoneNumber });
  const wallet = result.data;
  $('#wallet-summary').classList.remove('hidden');
  $('#wallet-balance').textContent = number(wallet.balance);
  $('#wallet-currency').textContent = wallet.currency;
  $('#wallet-transactions-count').textContent = number(wallet.transactions.length);
  const owner = wallet.owner || {};
  const ownerRole = Number(owner.role) === 1 ? 'سائق' : Number(owner.role) === 2 ? 'مدير' : 'عميل';
  const ownerAddress = [owner.city, owner.district, owner.street].filter(value => value && String(value).trim()).join('، ');
  $('#wallet-owner-name').textContent = owner.displayName || 'بدون اسم مسجل';
  $('#wallet-owner-id').textContent = `معرف المستخدم: ${owner.id ?? wallet.userId}`;
  $('#wallet-owner-phone').textContent = owner.phoneNumber || '—';
  $('#wallet-owner-email').textContent = owner.email || '—';
  $('#wallet-owner-role').textContent = ownerRole;
  $('#wallet-owner-status').textContent = owner.isActive ? 'نشط' : 'موقوف';
  $('#wallet-owner-gender').textContent = owner.gender || '—';
  $('#wallet-owner-address').textContent = ownerAddress || '—';
  $('#wallet-owner-created').textContent = owner.createdAtUtc ? date(owner.createdAtUtc) : '—';
  $('#wallet-owner-updated').textContent = owner.updatedAtUtc ? date(owner.updatedAtUtc) : '—';
  $('#wallet-empty').textContent = 'لا توجد حركات في هذه المحفظة.';
  toggleEmpty('#wallet-body','#wallet-empty',wallet.transactions.map(item => `<tr><td>${badge(transactionTypes[item.type] ?? item.type, item.type === 0 ? 'success' : item.type === 1 ? 'danger' : 'info')}</td><td>${number(item.amount)}</td><td>${number(item.balanceAfter)}</td><td>${escapeHtml(item.description)}</td><td>${escapeHtml(item.externalReference || '—')}</td><td>${date(item.createdAtUtc)}</td></tr>`));
}

async function loadSettlements() {
  const result = await execute('DriverSettlementPaymentModel', 'list', {});
  state.settlements = result.data || [];
  const rows = state.settlements.map(item => `<tr><td><span class="cell-main">${escapeHtml(item.driverName || 'بدون اسم')}</span><span class="cell-sub">${escapeHtml(item.driverPhone || item.driverId)}</span></td><td>${escapeHtml(item.id)}</td><td>${number(item.amountDue)} YER</td><td>${number(item.amountPaid)} YER</td><td>${number(item.amountOutstanding)} YER</td><td>${escapeHtml(item.paymentReference || '—')}</td><td>${date(item.createdAtUtc)}</td><td>${item.isSettled ? badge('تم التحصيل','success') : iconActionButton('cash', 'تسجيل تحصيل من السائق', `openSettlementForm(${Number(item.id)}, ${Number(item.amountOutstanding)})`, { tone: 'success' })}</td></tr>`);
  toggleEmpty('#settlements-body', '#settlements-empty', rows);
}

async function loadPaymentMethods() {
  const result = await execute('PaymentMethodModel', 'list', {});
  state.paymentMethods = result.data || [];
  const kinds = ['محفظة خارجية','بطاقة','حساب/تحويل بنكي'];
  const rows = state.paymentMethods.map(item => {
    const image = item.imageUrl ? `<img class="payment-method-logo" src="${escapeHtml(item.imageUrl)}" alt="شعار ${escapeHtml(item.nameAr)}">` : '—';
    const visibility = [item.isAvailableForRidePayment ? 'دفع الرحلة' : '', item.isAvailableForWalletTopUp ? 'شحن المحفظة' : ''].filter(Boolean).join(' + ') || '—';
    return `<tr><td>${image}</td><td><span class="cell-main">${escapeHtml(item.nameAr)}</span><span class="cell-sub">${escapeHtml(item.descriptionAr)}</span></td><td>${escapeHtml(kinds[item.kind] || item.kind)}</td><td>${escapeHtml(item.providerCode)}</td><td>${escapeHtml(visibility)}</td><td>${number(item.sortOrder)}</td><td>${badge(item.isActive ? 'نشطة' : 'موقوفه', item.isActive ? 'success' : 'danger')}</td><td>${recordActions('paymentMethod', item.id)}</td></tr>`;
  });
  toggleEmpty('#payment-methods-body', '#payment-methods-empty', rows);
}

async function loadCancellations() {
  const selected = $('#cancellation-filter').value;
  const data = selected === '' ? {} : { status: Number(selected) };
  const result = await execute('RideCancellationReportModel', 'list', data);
  state.cancellations = result.data || [];
  const rows = state.cancellations.map(item => {
    const isAdminReview = item.canAdminReview === true || Number(item.status) === 2;
    const decision = `${cancellationStatuses[item.status] || item.status}${item.driverDecision ? ` · ${cancellationDriverDecisions[item.driverDecision] || item.driverDecision}` : ''}`;
    const payment = item.paymentProvider ? `${item.paymentProvider} · ${number(item.paymentAmount)} YER` : 'لا توجد دفعة مسجلة';
    const refundRoute = item.requestedRefundMethod ? (cancellationRefundMethods[item.requestedRefundMethod] || 'مسار استرداد غير معروف') : 'لا يوجد مسار استرداد مطلوب';
    const place = item.cancellationLatitude == null ? '—' : `${number(item.cancellationLatitude)}, ${number(item.cancellationLongitude)}`;
    const actions = isAdminReview ? `<div class="action-row">${iconActionButton('approve', 'اعتماد الإلغاء', `decideCancellation(${Number(item.id)}, true)`, { tone: 'success' })}${iconActionButton('reject', 'رفض طلب الإلغاء', `decideCancellation(${Number(item.id)}, false)`, { tone: 'danger' })}</div>` : '—';
    return `<tr><td>#${escapeHtml(item.id)}</td><td><span class="cell-main">${escapeHtml(item.customerName || item.customerPhone || item.customerId)}</span><span class="cell-sub">السائق: ${escapeHtml(item.driverName || 'غير معيّن')}</span></td><td><span class="cell-main">${escapeHtml(item.pickup)} ← ${escapeHtml(item.destination)}</span><span class="cell-sub">الإجمالي: ${number(item.totalAmount)} YER</span></td><td><span class="cell-main">${escapeHtml(item.reason)}</span>${item.driverNote ? `<span class="cell-sub">ملاحظة السائق: ${escapeHtml(item.driverNote)}</span>` : ''}</td><td>${badge(decision, isAdminReview ? 'warning' : Number(item.status) === 3 ? 'success' : Number(item.status) === 4 ? 'danger' : 'info')}</td><td><span class="cell-main">${escapeHtml(payment)}</span><span class="cell-sub">الاسترداد: ${escapeHtml(refundRoute)}</span>${item.paymentStatus != null ? `<span class="cell-sub">حالة: ${escapeHtml(item.paymentStatus)}</span>` : ''}</td><td>${escapeHtml(place)}</td><td>${date(item.createdAtUtc)}</td><td>${actions}</td></tr>`;
  });
  toggleEmpty('#cancellations-body', '#cancellations-empty', rows);
}

async function loadSafety() {
  const selected = $('#safety-filter').value;
  const result = await execute('SafetyAdminReportModel', 'list', selected === '' ? {} : { status: Number(selected) });
  state.safety = result.data || [];
  const rows = state.safety.map(item => {
    const status = Number(item.status);
    const locationSource = item.locationSource === 'reporterGps'
      ? 'GPS مقدم البلاغ'
      : item.locationSource === 'pickupFallback' ? 'نقطة الالتقاط كبديل' : 'مصدر الموقع غير متاح';
    const location = item.latitude == null
      ? 'لا يوجد موقع مسجل'
      : `${number(item.latitude)}, ${number(item.longitude)} · ${locationSource}${item.locationObservedAtUtc ? ` · رُصد ${date(item.locationObservedAtUtc)}` : ''}`;
    const busy = busySafetyActions.has(Number(item.id));
    const actions = status === 2 ? '—' : `<div class="action-row">${status === 0 ? iconActionButton('approve', 'استلام البلاغ', `decideSafety(${Number(item.id)}, false)`, { tone: 'info', disabledReason: busy ? 'العملية قيد التنفيذ' : '' }) : ''}${iconActionButton('reject', 'إغلاق البلاغ', `decideSafety(${Number(item.id)}, true)`, { tone: 'danger', disabledReason: busy ? 'العملية قيد التنفيذ' : '' })}</div>`;
    const driver = item.driverId ? `<span class="cell-main">${escapeHtml(item.driverName || 'السائق')}</span><span class="cell-sub">${escapeHtml(item.driverPhone || 'لا يوجد رقم مسجل')}</span>` : 'لم يعيّن سائق';
    const mapAction = item.rideId ? iconActionButton('map', 'متابعة الرحلة على الخريطة', `openSafetyMap(${Number(item.id)})`, { tone: 'info' }) : '—';
    return `<tr><td>#${Number(item.id)}</td><td><span class="cell-main">${escapeHtml(item.userName || item.userPhone || '—')}</span><span class="cell-sub">${escapeHtml(item.userPhone || '')}</span></td><td>${driver}</td><td><span class="cell-main">#${Number(item.rideId || 0) || '—'}</span><span class="cell-sub">${mapAction}</span></td><td>${escapeHtml(item.message)}</td><td>${escapeHtml(location)}</td><td>${badge(safetyStatuses[status] || item.status, status === 2 ? 'success' : status === 1 ? 'info' : 'danger')}</td><td>${date(item.createdAtUtc)}</td><td>${actions}</td></tr>`;
  });
  toggleEmpty('#safety-body', '#safety-empty', rows);
}

window.decideSafety = async (id, resolve) => {
  const incidentId = Number(id);
  if (busySafetyActions.has(incidentId)) return;
  busySafetyActions.add(incidentId);
  try {
    await loadSafety();
    await execute('SafetyIncidentModel', resolve ? 'adminApprove' : 'accept', { id: incidentId });
    toast(resolve ? 'تم إغلاق بلاغ السلامة.' : 'تم استلام البلاغ، وأزيل إجراء الاستلام لمنع تكراره.');
  } catch (error) { if (!isAdminOperationCancelled(error)) toast(error.message, true); }
  finally { busySafetyActions.delete(incidentId); await loadSafety().catch(error => toast(error.message, true)); }
};

async function loadSafetyRecordings() {
  for (const url of safetyAudioUrls.values()) URL.revokeObjectURL(url);
  safetyAudioUrls.clear();
  const result = await execute('SafetyRecordingAdminReportModel', 'list', {});
  state.safetyRecordings = result.data || [];
  const rows = state.safetyRecordings.map(item => {
    const role = Number(item.userRole) === 1 ? 'سائق' : Number(item.userRole) === 0 ? 'عميل' : 'مستخدم';
    const recordingPath = `${escapeHtml(item.pickupLabel || 'الانطلاق')} ← ${escapeHtml(item.destinationLabel || 'الوجهة')}`;
    const driver = item.driverId ? `<span class="cell-main">${escapeHtml(item.driverName || '—')}</span><span class="cell-sub">${escapeHtml(item.driverPhone || '')}</span>` : 'غير معيّن';
    return `<tr><td>#${Number(item.id)}</td><td>#${Number(item.rideId || 0) || '—'}</td><td>${recordingPath}</td><td><span class="cell-main">${escapeHtml(item.userName || '—')} (${role})</span><span class="cell-sub">${escapeHtml(item.userPhone || '')}</span></td><td>${driver}</td><td>${number((Number(item.durationMilliseconds) || 0) / 1000)} ث<span class="cell-sub">${number((Number(item.uploadedBytes) || 0) / 1024)} ك.ب</span></td><td>${date(item.startedAtUtc)}</td><td>${date(item.consentAtUtc)}</td><td><div class="recording-player">${iconActionButton('play', `تشغيل تسجيل السلامة رقم ${Number(item.id)}`, `playSafetyRecording(${Number(item.id)})`, { tone: 'success' })}<audio controls preload="none" class="hidden" id="safety-audio-${Number(item.id)}" aria-label="تشغيل تسجيل السلامة رقم ${Number(item.id)}"></audio></div></td></tr>`;
  });
  toggleEmpty('#safety-recordings-body', '#safety-recordings-empty', rows);
}

window.playSafetyRecording = async id => {
  const recordingId = Number(id);
  const button = document.querySelector(`#safety-recordings-body button[onclick="playSafetyRecording(${recordingId})"]`);
  const audio = document.getElementById(`safety-audio-${recordingId}`);
  if (!audio) return;
  if (audio.src) {
    if (audio.paused) {
      try { await audio.play(); }
      catch (_) { toast('تعذر تشغيل الصوت تلقائياً. استخدم مشغل الصوت الظاهر.', true); }
    } else {
      audio.pause();
    }
    return;
  }
  if (button) { button.disabled = true; updateIconAction(button, 'play', 'جارٍ تحميل التسجيل'); }
  try {
    const response = await adminFetch(`/api/admin/safety/recordings/${recordingId}/audio`, {
      cache: 'no-store'
    });
    if (response.status === 401 || response.status === 403) throw new Error('انتهت جلسة الإدارة أو لا تملك صلاحية التشغيل.');
    if (!response.ok) throw new Error(response.status === 413 ? 'التسجيل أكبر من الحد المسموح للتشغيل في المتصفح.' : 'تعذر جلب التسجيل الصوتي.');
    const blob = await response.blob();
    if (!blob.size) throw new Error('ملف التسجيل فارغ.');
    if (safetyAudioUrls.has(recordingId)) URL.revokeObjectURL(safetyAudioUrls.get(recordingId));
    const url = URL.createObjectURL(new Blob([blob], { type: 'audio/wav' }));
    safetyAudioUrls.set(recordingId, url);
    audio.src = url;
    audio.classList.remove('hidden');
    audio.load();
    audio.onplay = () => {
      updateIconAction(button, 'pause', 'إيقاف التسجيل مؤقتًا');
    };
    audio.onpause = () => {
      updateIconAction(button, 'play', 'متابعة تشغيل التسجيل');
    };
    audio.onended = () => {
      updateIconAction(button, 'play', 'إعادة تشغيل التسجيل');
    };
    try { await audio.play(); }
    catch (_) { toast('تم تحميل التسجيل؛ استخدم زر التشغيل في مشغل الصوت.', true); }
  } catch (error) {
    toast(error.message, true);
    updateIconAction(button, 'play', 'إعادة محاولة تشغيل التسجيل');
  } finally { if (button) button.disabled = false; }
};

function validMapPoint(latitude, longitude) {
  const lat = Number(latitude), lon = Number(longitude);
  return Number.isFinite(lat) && Number.isFinite(lon) && Math.abs(lat) <= 90 && Math.abs(lon) <= 180 ? { lat, lon } : null;
}

function updateSafetyMap(item) {
  const driverPoint = validMapPoint(item.driverLatitude, item.driverLongitude);
  const incidentPoint = validMapPoint(item.latitude, item.longitude);
  const pickupPoint = validMapPoint(item.pickupLatitude, item.pickupLongitude);
  const destinationPoint = validMapPoint(item.destinationLatitude, item.destinationLongitude);
  const marker = driverPoint || incidentPoint || pickupPoint;
  $('#safety-map-summary').textContent = `البلاغ #${item.id} · الرحلة #${item.rideId || '—'} · ${rideStatuses[Number(item.rideStatus)] || 'حالة غير متاحة'}`;
  const coordinates = [driverPoint && `موقع السائق الحالي: ${number(driverPoint.lat)}, ${number(driverPoint.lon)}`, incidentPoint && `موقع البلاغ: ${number(incidentPoint.lat)}, ${number(incidentPoint.lon)}`, item.driverLocationObservedAtUtc && `آخر تحديث للسائق: ${date(item.driverLocationObservedAtUtc)}`].filter(Boolean);
  $('#safety-map-details').textContent = `${item.driverName || 'لم يعيّن سائق'}${item.driverPhone ? ` · ${item.driverPhone}` : ''} | ${item.pickupAddress || item.pickupLabel || 'نقطة الانطلاق'} ← ${item.destinationAddress || item.destinationLabel || 'الوجهة'}${coordinates.length ? ` | ${coordinates.join(' | ')}` : ''}`;
  $('#safety-map-updated').textContent = item.driverLocationObservedAtUtc ? `تم تحديث الموقع ${date(item.driverLocationObservedAtUtc)}` : 'لا يتوفر موقع مباشر للسائق؛ تعرض الخريطة موقع البلاغ إن وُجد.';

  if (marker) {
    const points = [marker, incidentPoint, pickupPoint, destinationPoint].filter(Boolean);
    const minLat = Math.min(...points.map(point => point.lat)) - 0.004;
    const maxLat = Math.max(...points.map(point => point.lat)) + 0.004;
    const minLon = Math.min(...points.map(point => point.lon)) - 0.004;
    const maxLon = Math.max(...points.map(point => point.lon)) + 0.004;
    const markerKey = `${marker.lat.toFixed(5)},${marker.lon.toFixed(5)}:${minLat.toFixed(3)},${minLon.toFixed(3)},${maxLat.toFixed(3)},${maxLon.toFixed(3)}`;
    if (markerKey !== (safetyMapLoaded ? safetyMapLastMarker : safetyMapPendingMarker)) {
      const params = new URLSearchParams({ bbox: `${minLon},${minLat},${maxLon},${maxLat}`, layer: 'mapnik', marker: `${marker.lat},${marker.lon}` });
      safetyMapPendingUrl = `https://www.openstreetmap.org/export/embed.html?${params.toString()}`;
      safetyMapPendingMarker = markerKey;
      if (safetyMapLoaded) {
        $('#safety-map-frame').src = safetyMapPendingUrl;
        safetyMapLastMarker = markerKey;
      }
    }
  } else {
    safetyMapPendingUrl = '';
    safetyMapPendingMarker = '';
    $('#safety-map-frame').removeAttribute('src');
  }

  const routePoints = [driverPoint || incidentPoint, pickupPoint, destinationPoint].filter(Boolean);
  if (routePoints.length >= 2) {
    const route = routePoints.map(point => `${point.lat},${point.lon}`).join(';');
    $('#safety-map-route-link').href = `https://www.openstreetmap.org/directions?engine=fossgis_osrm_car&route=${encodeURIComponent(route)}`;
    $('#safety-map-route-link').classList.toggle('hidden', !safetyMapLoaded);
  } else {
    $('#safety-map-route-link').classList.add('hidden');
  }
}

window.openSafetyMap = async id => {
  safetyMapIncidentId = Number(id);
  safetyMapLastMarker = '';
  safetyMapPendingMarker = '';
  safetyMapPendingUrl = '';
  safetyMapLoaded = false;
  clearInterval(safetyMapTimer);
  $('#safety-map-frame').removeAttribute('src');
  $('#safety-map-frame').classList.add('hidden');
  $('#safety-map-load').classList.remove('hidden');
  $('#safety-map-route-link').removeAttribute('href');
  $('#safety-map-route-link').classList.add('hidden');
  $('#safety-map-modal').showModal();
  try {
    const result = await execute('SafetyAdminReportModel', 'list', {});
    const item = (result.data || []).find(row => Number(row.id) === safetyMapIncidentId);
    if (!item) { toast('تعذر العثور على بيانات الرحلة.', true); $('#safety-map-modal').close(); return; }
    updateSafetyMap(item);
    safetyMapTimer = setInterval(async () => {
      if (!$('#safety-map-modal').open) { clearInterval(safetyMapTimer); return; }
      try {
        const refreshed = await execute('SafetyAdminReportModel', 'list', {});
        const latest = (refreshed.data || []).find(row => Number(row.id) === safetyMapIncidentId);
        if (!latest) { clearInterval(safetyMapTimer); return; }
        updateSafetyMap(latest);
        if (![3, 4, 5, 8, 9].includes(Number(latest.rideStatus))) clearInterval(safetyMapTimer);
      } catch (error) { console.warn('تعذر تحديث موقع الرحلة:', error); }
    }, 12000);
  } catch (error) { toast(error.message, true); $('#safety-map-modal').close(); }
};

window.decideCancellation = async (id, approve) => {
  const note = window.prompt('ملاحظة الإدارة (اختيارية):') || undefined;
  try {
    const result = await execute('RideCancellationRequestModel', approve ? 'adminApprove' : 'adminReject', { id, note });
    toast(result.message || 'تم حفظ قرار الإدارة.');
    await loadCancellations();
  } catch (error) { if (!isAdminOperationCancelled(error)) toast(error.message, true); }
};

async function loadLedgerAccounts() {
  const result = await execute('LedgerAccountModel', 'list', {});
  state.ledgerAccounts = result.data || [];
  const selectedId = $('#statement-account').value;
  const search = $('#statement-account-search');
  const selected = state.ledgerAccounts.find(item => String(item.id) === selectedId);
  $('#statement-account').value = selected ? String(selected.id) : '';
  if (selected) search.value = statementAccountLabel(selected);
  hideStatementAccountOptions();
}

function isWithinAdminCashAccount(account) {
  const visited = new Set();
  let current = account;
  while (current && visited.add(Number(current.id))) {
    if (String(current.code) === '1000') return true;
    if (current.parentLedgerAccountId == null) return false;
    current = state.ledgerAccounts.find(item => Number(item.id) === Number(current.parentLedgerAccountId));
  }
  return false;
}

function fillFinancialAccountSelect(select, accounts, prompt, selectedId = '') {
  const selected = accounts.some(item => String(item.id) === String(selectedId)) ? String(selectedId) : '';
  select.innerHTML = `<option value="">${prompt}</option>${accounts.map(item =>
    `<option value="${Number(item.id)}">${escapeHtml(item.code)} — ${escapeHtml(item.name)} (${escapeHtml(item.currency)})</option>`).join('')}`;
  select.value = selected;
}

function syncFinancialOperationForm() {
  const type = $('#financial-operation-type');
  if (!type) return;
  const operationType = financialOperationDefinitions[state.financialOperationType]
    ? state.financialOperationType : 'cashReceipt';
  state.financialOperationType = operationType;
  type.value = operationType;
  const definition = financialOperationDefinitions[operationType];
  $('#financial-operation-heading').textContent = definition[0];
  $('#financial-operation-subtitle').textContent = definition[1];
  $('#financial-operation-rules').textContent = definition[2];
  if (state.view === 'financial-operations') {
    $('#page-title').textContent = definition[0];
    $('#page-subtitle').textContent = definition[1];
  }
  $$('.nav-item[data-view="financial-operations"]').forEach(item =>
    item.classList.toggle('active', item.dataset.financialOperation === operationType));

  const postingAccounts = state.ledgerAccounts.filter(item => item.isActive && item.isPosting);
  const cashAccounts = postingAccounts.filter(isWithinAdminCashAccount);
  const debitOptions = operationType === 'cashReceipt' ? cashAccounts : postingAccounts;
  const creditOptions = operationType === 'cashDisbursement' ? cashAccounts : postingAccounts;
  const debitSelect = $('#financial-operation-debit-account');
  const creditSelect = $('#financial-operation-credit-account');
  const oldDebitId = debitSelect.value;
  const oldCreditId = creditSelect.value;
  fillFinancialAccountSelect(debitSelect, debitOptions, 'اختر الحساب المدين', oldDebitId);
  const debitAccount = postingAccounts.find(item => Number(item.id) === Number(debitSelect.value));
  const matchingCreditOptions = creditOptions.filter(item =>
    (!debitAccount || String(item.currency).toUpperCase() === String(debitAccount.currency).toUpperCase()) &&
    Number(item.id) !== Number(debitSelect.value));
  fillFinancialAccountSelect(creditSelect, matchingCreditOptions, 'اختر الحساب الدائن', oldCreditId);
  const creditAccount = postingAccounts.find(item => Number(item.id) === Number(creditSelect.value));
  const currencyStatus = $('#financial-operation-currency');
  currencyStatus.classList.remove('is-error');
  if (debitAccount && creditAccount) {
    currencyStatus.textContent = `العملة: ${debitAccount.currency}`;
  } else if (operationType !== 'accountTransfer' && cashAccounts.length === 0) {
    currencyStatus.textContent = 'حسابات الصندوق النقدي (1000) غير مهيأة أو لا تقبل القيود.';
    currencyStatus.classList.add('is-error');
  } else if (debitAccount) {
    currencyStatus.textContent = `العملة: ${debitAccount.currency} · اختر حساباً دائناً بالعملة نفسها`;
  } else {
    currencyStatus.textContent = 'العملة: اختر الحساب المدين';
  }
}

function initializeFinancialOperationDate() {
  const input = $('#financial-operation-date');
  if (!input || input.value) return;
  const now = new Date();
  input.value = new Date(now.getTime() - now.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
}

function initializeFinancialOperationFilterDate() {
  const input = $('#financial-operation-filter-date');
  if (!input || input.value) return;
  const now = new Date();
  input.value = new Date(now.getTime() - now.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
}

function financialOperationRow(item) {
  const entryId = Number(item.journalEntryId);
  const statusTone = item.status === 'مرحل' ? 'success' : item.status === 'تم تعديلها' ? 'info' : 'warning';
  const actions = item.canModify
    ? '<div class="action-row">' +
      iconActionButton('edit', 'تعديل العملية', 'editFinancialOperation(' + entryId + ')') +
      iconActionButton('delete', 'إلغاء العملية بقيد عكسي', 'deleteFinancialOperation(' + entryId + ')', { tone: 'danger' }) +
      '</div>'
    : '<span class="cell-sub">لا توجد إجراءات</span>';
  return '<tr><td>' + escapeHtml(item.operationDate) + '</td>' +
    '<td><span class="cell-main">' + escapeHtml(item.operationName) + '</span><span class="cell-sub" dir="ltr">' + escapeHtml(item.entryNumber) + '</span></td>' +
    '<td>' + escapeHtml(item.debitAccount) + '</td><td>' + escapeHtml(item.creditAccount) + '</td>' +
    '<td><span class="cell-main">' + number(item.amount) + ' ' + escapeHtml(item.currency) + '</span></td>' +
    '<td><span class="cell-main">' + escapeHtml(item.note) + '</span></td>' +
    '<td>' + badge(item.status, statusTone) + '</td><td>' + actions + '</td></tr>';
}

async function loadFinancialOperations() {
  const result = await execute('FinancialOperationModel', 'list', {
    operationDate: $('#financial-operation-filter-date').value || null
  });
  state.financialOperations = result.data || [];
  toggleEmpty('#financial-operations-body', '#financial-operations-empty',
    state.financialOperations.map(financialOperationRow));
}

async function submitFinancialOperation(event) {
  event.preventDefault();
  const form = event.currentTarget;
  const editingId = state.financialOperationEditId;
  const submit = $('#financial-operation-submit');
  const resultBox = $('#financial-operation-result');
  submit.disabled = true;
  resultBox.className = 'inline-result hidden';
  try {
    const data = {
      journalEntryId: editingId || 0,
      operationType: $('#financial-operation-type').value,
      debitAccountId: Number($('#financial-operation-debit-account').value),
      creditAccountId: Number($('#financial-operation-credit-account').value),
      amount: Number($('#financial-operation-amount').value),
      operationDate: $('#financial-operation-date').value,
      note: $('#financial-operation-note').value.trim()
    };
    const result = await execute('FinancialOperationModel', editingId ? 'update' : 'add', data);
    const entryNumber = editingId ? result.data?.replacement?.entryNumber : result.data?.entryNumber;
    const message = editingId
      ? 'تم تعديل العملية بإنشاء قيد عكسي وترحيل قيد جديد.' + (entryNumber ? ' رقم القيد الجديد: ' + entryNumber : '')
      : 'تم تسجيل العملية وترحيل القيد.' + (entryNumber ? ' رقم القيد: ' + entryNumber : '');
    resultBox.className = 'inline-result success';
    resultBox.textContent = message;
    toast(message);
    const operationType = data.operationType;
    form.reset();
    state.financialOperationEditId = null;
    $('#financial-operation-type').value = operationType;
    state.financialOperationType = operationType;
    initializeFinancialOperationDate();
    $('#financial-operation-filter-date').value = data.operationDate;
    syncFinancialOperationForm();
    syncFinancialOperationEditState();
    try {
      await loadFinancialOperations();
    } catch (refreshError) {
      toast('تم حفظ العملية، لكن تعذر تحديث سجل العمليات: ' + (refreshError.message || 'خطأ غير معروف.'), true);
    }
  } catch (error) {
    if (!isAdminOperationCancelled(error)) {
      resultBox.className = 'inline-result error';
      resultBox.textContent = error.message || 'تعذر ترحيل القيد المالي.';
      toast(resultBox.textContent, true);
    }
  } finally {
    submit.disabled = false;
  }
}

function syncFinancialOperationEditState() {
  const editing = Number(state.financialOperationEditId) > 0;
  $('#financial-operation-form-heading').textContent = editing ? 'تعديل العملية المالية' : 'بيانات العملية';
  $('#financial-operation-submit').textContent = editing ? 'حفظ التعديل وترحيل القيود' : 'تسجيل العملية وترحيل القيد';
  $('#financial-operation-cancel-edit').classList.toggle('hidden', !editing);
}

window.editFinancialOperation = journalEntryId => {
  const item = state.financialOperations.find(record => Number(record.journalEntryId) === Number(journalEntryId));
  if (!item || !item.canModify) {
    toast('تعذر فتح العملية للتعديل. حدّث السجل ثم حاول مرة أخرى.', true);
    return;
  }
  state.financialOperationEditId = Number(item.journalEntryId);
  state.financialOperationType = item.operationType;
  $('#financial-operation-type').value = item.operationType;
  syncFinancialOperationForm();
  $('#financial-operation-debit-account').value = String(item.debitAccountId);
  syncFinancialOperationForm();
  $('#financial-operation-credit-account').value = String(item.creditAccountId);
  $('#financial-operation-amount').value = String(item.amount);
  $('#financial-operation-date').value = item.operationDate;
  $('#financial-operation-note').value = item.note;
  syncFinancialOperationEditState();
  $('#financial-operation-form').scrollIntoView({ behavior: 'smooth', block: 'center' });
};

window.deleteFinancialOperation = async journalEntryId => {
  try {
    const result = await execute('FinancialOperationModel', 'delete', { journalEntryId: Number(journalEntryId) });
    const reversalNumber = result.data?.reversalEntryNumber || result.data?.entryNumber;
    toast('تم إلغاء العملية بقيد عكسي.' + (reversalNumber ? ' رقم القيد العكسي: ' + reversalNumber : ''));
    await loadFinancialOperations();
  } catch (error) {
    if (!isAdminOperationCancelled(error)) toast(error.message || 'تعذر إلغاء العملية المالية.', true);
  }
};

function cancelFinancialOperationEdit() {
  const operationType = state.financialOperationType;
  state.financialOperationEditId = null;
  $('#financial-operation-form').reset();
  $('#financial-operation-type').value = operationType;
  initializeFinancialOperationDate();
  syncFinancialOperationForm();
  syncFinancialOperationEditState();
  $('#financial-operation-result').className = 'inline-result hidden';
}

function statementAccountLabel(item) {
  return `${item.code} — ${item.name} (${item.currency})`;
}

function hideStatementAccountOptions() {
  const options = $('#statement-account-options');
  options.hidden = true;
  $('#statement-account-search').setAttribute('aria-expanded', 'false');
}

function positionStatementAccountOptions() {
  const input = $('#statement-account-search');
  const options = $('#statement-account-options');
  const rect = input.getBoundingClientRect();
  const width = Math.min(rect.width, window.innerWidth - 16);
  const left = Math.max(8, Math.min(rect.left, window.innerWidth - width - 8));
  const availableBelow = Math.max(0, window.innerHeight - rect.bottom - 8);
  const availableAbove = Math.max(0, rect.top - 8);
  const placeAbove = availableBelow < 170 && availableAbove > availableBelow;
  const maxHeight = Math.max(80, Math.min(320, placeAbove ? availableAbove : availableBelow));
  options.style.maxHeight = `${maxHeight}px`;
  options.style.left = `${left}px`;
  options.style.width = `${width}px`;
  options.style.top = placeAbove
    ? `${Math.max(8, rect.top - Math.min(maxHeight, options.scrollHeight) - 5)}px`
    : `${rect.bottom + 5}px`;
}

function statementAccountSearchText(item) {
  return [
    statementAccountLabel(item), item.financialPartyName, item.financialPartyPhoneNumber,
    item.id, item.financialPartyId, item.purpose
  ].filter(value => value != null).join(' ').toLocaleLowerCase();
}

function renderStatementAccountOptions(query = '') {
  const options = $('#statement-account-options');
  const normalizedQuery = String(query || '').trim().toLocaleLowerCase();
  const matches = state.ledgerAccounts.filter(item =>
    !normalizedQuery || statementAccountSearchText(item).includes(normalizedQuery));
  if (!matches.length) {
    options.innerHTML = '<div class="statement-account-options-empty">لا توجد حسابات مطابقة.</div>';
  } else {
    const selectedId = Number($('#statement-account').value) || null;
    options.innerHTML = matches.map(item => {
      const main = item.parentLedgerAccountId == null;
      return `<div class="statement-account-option ${main ? 'is-main' : 'is-sub'}" role="option" tabindex="-1" aria-selected="${Number(item.id) === selectedId}" data-account-id="${Number(item.id)}"><span class="statement-account-option-name">${escapeHtml(statementAccountLabel(item))}</span><span class="statement-account-option-kind">${main ? 'حساب رئيسي' : 'حساب فرعي'}</span></div>`;
    }).join('');
  }
  options.hidden = false;
  $('#statement-account-search').setAttribute('aria-expanded', 'true');
  positionStatementAccountOptions();
}

function chooseStatementAccount(id) {
  const account = state.ledgerAccounts.find(item => Number(item.id) === Number(id));
  if (!account) return;
  $('#statement-account').value = String(account.id);
  $('#statement-account-search').value = statementAccountLabel(account);
  hideStatementAccountOptions();
}

function syncStatementPeriodFields() {
  const mode = $('#statement-period').value;
  $$('.statement-period-field').forEach(field => field.classList.toggle('hidden', field.dataset.periodField !== mode));
}

function syncStatementModeFields() {
  const summaryMode = $('#statement-mode').value === 'summary';
  $('#statement-zero-filter').classList.toggle('hidden', !summaryMode);
  $('#statement-hide-zero-accounts').disabled = !summaryMode;
  renderAccountStatement();
}

function statementRequest() {
  const data = {};
  const accountId = $('#statement-account').value;
  const accountSearch = $('#statement-account-search').value.trim();
  if (accountSearch && !accountId)
    throw new Error('اختر حسابًا من قائمة النتائج، أو امسح الحقل لعرض كافة الحسابات.');
  const entryType = $('#statement-type').value;
  if (accountId) data.ledgerAccountId = Number(accountId);
  if (entryType !== '') data.entryType = Number(entryType);
  const mode = $('#statement-period').value;
  if (mode === 'range') {
    if ($('#statement-from').value) data.dateFromUtc = $('#statement-from').value;
    if ($('#statement-to').value) data.dateToUtc = $('#statement-to').value;
  } else if (mode === 'day' && $('#statement-day').value) {
    data.dateFromUtc = $('#statement-day').value;
    data.dateToUtc = $('#statement-day').value;
  } else if (mode === 'month' && $('#statement-month').value) {
    const [year, month] = $('#statement-month').value.split('-').map(Number);
    data.year = year; data.month = month;
  } else if (mode === 'year' && $('#statement-year').value) {
    data.year = Number($('#statement-year').value);
  }
  return data;
}

async function loadAccountStatement() {
  const result = await execute('AccountStatementModel', 'report', statementRequest());
  const data = result.data || { accounts: [], lines: [] };
  state.accountStatement = {
    accounts: Array.isArray(data.accounts) ? data.accounts : [],
    lines: Array.isArray(data.lines) ? data.lines : []
  };
  renderAccountStatement();
}

function initializeFinancialReportDates() {
  const now = new Date();
  const today = new Date(now.getTime() - now.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
  $('#financial-report-from').value = `${today.slice(0, 4)}-01-01`;
  $('#financial-report-to').value = today;
}

async function loadFinancialReport() {
  const reportType = state.financialReportType;
  const dateToUtc = $('#financial-report-to').value;
  const dateFromUtc = $('#financial-report-from').value;
  if (!dateToUtc) throw new Error('حدد تاريخ التقرير أولاً.');
  if (reportType !== 'position' && !dateFromUtc) throw new Error('حدد بداية الفترة أولاً.');
  if (reportType !== 'position' && dateFromUtc > dateToUtc)
    throw new Error('تاريخ البداية يجب ألا يتجاوز تاريخ النهاية.');

  const button = $('#financial-report-load');
  button.disabled = true;
  button.textContent = 'جارٍ إعداد التقرير…';
  try {
    const request = { reportType, dateToUtc };
    if (reportType !== 'position') request.dateFromUtc = dateFromUtc;
    const currency = $('#financial-report-currency').value.trim();
    if (currency) request.currency = currency;
    const result = await execute('FinancialStatementsModel', 'report', request);
    renderFinancialReport(result.data || {});
  } finally {
    button.disabled = false;
    button.textContent = 'عرض التقرير';
  }
}

function financialReportTable(title, headers, rows, emptyMessage = 'لا توجد بيانات ضمن الفترة المحددة.') {
  const content = rows.length
    ? `<div class="table-wrap"><table><thead><tr>${headers.map(header => `<th>${header}</th>`).join('')}</tr></thead><tbody>${rows.join('')}</tbody></table></div>`
    : `<div class="empty-state">${escapeHtml(emptyMessage)}</div>`;
  return `<section class="data-section financial-report-card"><div class="section-header"><div><h2>${escapeHtml(title)}</h2></div></div>${content}</section>`;
}

function financialReportMetricCards(totals, fields) {
  return `<div class="financial-report-metrics">${totals.map(total => fields.map(([key, label]) => `<article class="financial-report-metric"><span>${escapeHtml(label)} · ${escapeHtml(total.currency)}</span><strong>${number(total[key])}</strong></article>`).join('')).join('')}</div>`;
}

function financialAccountRows(rows) {
  return rows.map(row => `<tr><td><span class="cell-main">${escapeHtml(row.accountCode)} — ${escapeHtml(row.accountName)}</span></td><td>${escapeHtml(row.currency)}</td><td>${number(row.amount)}</td></tr>`);
}

function renderFinancialReport(data) {
  const reportType = data.reportType || state.financialReportType;
  const root = $('#financial-report-content');
  const note = $('#financial-report-note');
  note.textContent = data.note || '';
  note.classList.toggle('hidden', !data.note);
  const sections = data.sections || [];
  let content = '';

  if (reportType === 'position') {
    const totals = data.totals || [];
    content += financialReportMetricCards(totals, [
      ['assets', 'إجمالي الأصول'], ['liabilities', 'إجمالي الالتزامات'],
      ['totalEquity', 'إجمالي حقوق الملكية'], ['difference', 'فرق التوازن']
    ]);
    for (const section of sections)
      content += financialReportTable(section.title, ['الحساب', 'العملة', 'الرصيد'], financialAccountRows(section.rows || []));
    content += financialReportTable('مطابقة المركز المالي',
      ['العملة', 'الأصول', 'الالتزامات', 'حقوق الملكية المسجلة', 'الأرباح المتراكمة', 'إجمالي الالتزامات وحقوق الملكية', 'الفرق'],
      totals.map(item => `<tr><td>${escapeHtml(item.currency)}</td><td>${number(item.assets)}</td><td>${number(item.liabilities)}</td><td>${number(item.recordedEquity)}</td><td>${number(item.retainedEarnings)}</td><td>${number(item.liabilitiesAndEquity)}</td><td>${number(item.difference)}</td></tr>`));
  } else if (reportType === 'income') {
    const totals = data.totals || [];
    content += financialReportMetricCards(totals, [
      ['revenue', 'الإيرادات'], ['expenses', 'المصروفات'], ['netIncome', 'صافي الربح أو الخسارة']
    ]);
    for (const section of sections)
      content += financialReportTable(section.title, ['الحساب', 'العملة', 'المبلغ'], financialAccountRows(section.rows || [], 'المبلغ'));
    content += financialReportTable('صافي نتيجة الفترة', ['العملة', 'الإيرادات', 'المصروفات', 'صافي الربح أو الخسارة'],
      totals.map(item => `<tr><td>${escapeHtml(item.currency)}</td><td>${number(item.revenue)}</td><td>${number(item.expenses)}</td><td>${number(item.netIncome)}</td></tr>`));
  } else if (reportType === 'trialBalance') {
    const rows = data.rows || [];
    const totals = data.totals || [];
    content += financialReportTable('ميزان المراجعة',
      ['الحساب', 'العملة', 'مدين أول المدة', 'دائن أول المدة', 'حركة مدينة', 'حركة دائنة', 'رصيد مدين', 'رصيد دائن'],
      rows.map(item => `<tr><td><span class="cell-main">${escapeHtml(item.accountCode)} — ${escapeHtml(item.accountName)}</span></td><td>${escapeHtml(item.currency)}</td><td>${number(item.openingDebit)}</td><td>${number(item.openingCredit)}</td><td>${number(item.debit)}</td><td>${number(item.credit)}</td><td>${number(item.closingDebit)}</td><td>${number(item.closingCredit)}</td></tr>`));
    content += financialReportTable('إجماليات ميزان المراجعة',
      ['العملة', 'مدين أول المدة', 'دائن أول المدة', 'حركة مدينة', 'حركة دائنة', 'رصيد مدين', 'رصيد دائن'],
      totals.map(item => `<tr><td>${escapeHtml(item.currency)}</td><td>${number(item.openingDebit)}</td><td>${number(item.openingCredit)}</td><td>${number(item.debit)}</td><td>${number(item.credit)}</td><td>${number(item.closingDebit)}</td><td>${number(item.closingCredit)}</td></tr>`));
  } else if (reportType === 'cashFlow') {
    const totals = data.totals || [];
    const categoryNames = { operating: 'تشغيلية', investing: 'استثمارية', financing: 'تمويلية', opening: 'أرصدة افتتاحية', unclassified: 'غير مصنفة' };
    content += financialReportMetricCards(totals, [
      ['openingCash', 'رصيد النقدية أول المدة'], ['netChange', 'صافي التغير'], ['closingCash', 'رصيد النقدية آخر المدة']
    ]);
    content += financialReportTable('ملخص التدفقات النقدية',
      ['العملة', 'رصيد أول المدة', 'متحصلات تشغيلية', 'مدفوعات تشغيلية', 'صافي الاستثمار', 'صافي التمويل', 'غير مصنف', 'تسويات افتتاحية', 'صافي التغير', 'رصيد آخر المدة'],
      totals.map(item => `<tr><td>${escapeHtml(item.currency)}</td><td>${number(item.openingCash)}</td><td>${number(item.operatingInflow)}</td><td>${number(item.operatingOutflow)}</td><td>${number(item.investingNet)}</td><td>${number(item.financingNet)}</td><td>${number(item.unclassifiedNet)}</td><td>${number(item.openingBalanceAdjustments)}</td><td>${number(item.netChange)}</td><td>${number(item.closingCash)}</td></tr>`));
    content += financialReportTable('حركة النقدية بحسب العملية',
      ['التصنيف', 'العملية', 'العملة', 'متحصلات', 'مدفوعات', 'صافي الحركة'],
      (data.rows || []).map(item => `<tr><td>${escapeHtml(categoryNames[item.category] || item.category)}</td><td>${escapeHtml(item.operation)}</td><td>${escapeHtml(item.currency)}</td><td>${number(item.inflow)}</td><td>${number(item.outflow)}</td><td>${number(item.net)}</td></tr>`));
  } else if (reportType === 'equity') {
    const totals = data.totals || [];
    content += financialReportMetricCards(totals, [
      ['openingEquity', 'حقوق الملكية أول المدة'], ['periodNetIncome', 'صافي نتيجة الفترة'], ['closingEquity', 'حقوق الملكية آخر المدة']
    ]);
    content += financialReportTable('التغيرات في حقوق الملكية',
      ['الحساب', 'العملة', 'رصيد أول المدة', 'زيادة', 'انخفاض', 'نتيجة الفترة', 'رصيد آخر المدة'],
      (data.rows || []).map(item => `<tr><td><span class="cell-main">${escapeHtml(item.accountCode)} — ${escapeHtml(item.accountName)}</span></td><td>${escapeHtml(item.currency)}</td><td>${number(item.openingBalance)}</td><td>${number(item.increase)}</td><td>${number(item.decrease)}</td><td>${number(item.periodNetIncome)}</td><td>${number(item.closingBalance)}</td></tr>`));
    content += financialReportTable('إجماليات حقوق الملكية',
      ['العملة', 'رصيد أول المدة', 'زيادة', 'انخفاض', 'صافي نتيجة الفترة', 'رصيد آخر المدة'],
      totals.map(item => `<tr><td>${escapeHtml(item.currency)}</td><td>${number(item.openingEquity)}</td><td>${number(item.increase)}</td><td>${number(item.decrease)}</td><td>${number(item.periodNetIncome)}</td><td>${number(item.closingEquity)}</td></tr>`));
  }

  root.innerHTML = content || '<div class="empty-state">لا توجد بيانات مالية مطابقة للتقرير.</div>';
}

function statementMoney(value, currency, caption = '') {
  const suffix = [caption, currency].filter(Boolean).map(escapeHtml).join(' · ');
  return `<span class="cell-main">${number(value)}</span><span class="cell-sub">${suffix}</span>`;
}

function statementAccountCell(item) {
  return `<span class="cell-main">${escapeHtml(item.accountCode)} — ${escapeHtml(item.accountName)}</span><span class="cell-sub">${escapeHtml(item.currency)}</span>`;
}

function statementTotalsByCurrency(accounts) {
  const totals = new Map();
  for (const item of accounts) {
    const currency = String(item.currency || '—');
    const total = totals.get(currency) || { currency, openingBalance: 0, debit: 0, credit: 0, closingBalance: 0 };
    total.openingBalance += Number(item.openingBalance) || 0;
    total.debit += Number(item.debit) || 0;
    total.credit += Number(item.credit) || 0;
    total.closingBalance += Number(item.closingBalance) || 0;
    totals.set(currency, total);
  }
  return [...totals.values()].sort((a, b) => a.currency.localeCompare(b.currency));
}

function statementHasZeroBalance(item) {
  return Math.abs(Number(item.closingBalance) || 0) < 0.000001;
}

function statementAccountBalanceSides(item) {
  const balance = Number(item.closingBalance) || 0;
  const type = typeof item.accountType === 'string' ? item.accountType.toLowerCase() : Number(item.accountType);
  const debitNormal = type === 'asset' || type === 'expense' || type === 0 || type === 4;
  const debitBalance = debitNormal ? balance : -balance;
  return { debit: Math.max(debitBalance, 0), credit: Math.max(-debitBalance, 0) };
}

function statementBalanceCell(value, currency, accountType) {
  const amount = Number(value) || 0;
  const sides = statementAccountBalanceSides({ closingBalance: amount, accountType });
  const tone = Math.abs(amount) < 0.000001 ? '' : sides.debit > 0 ? 'statement-balance-debit' : 'statement-balance-credit';
  return `<td class="${tone}">${statementMoney(amount, currency)}</td>`;
}

function statementBalanceTotalsByCurrency(accounts) {
  const totals = new Map();
  for (const item of accounts.filter(account => !account.hasChildren)) {
    const currency = String(item.currency || '—');
    const total = totals.get(currency) || { currency, debit: 0, credit: 0 };
    const sides = statementAccountBalanceSides(item);
    total.debit += sides.debit;
    total.credit += sides.credit;
    totals.set(currency, total);
  }
  return [...totals.values()].sort((a, b) => a.currency.localeCompare(b.currency));
}

function renderAccountStatement() {
  const { accounts, lines } = state.accountStatement;
  const accountId = Number($('#statement-account').value) || null;
  const selectedAccountHasChildren = accountId !== null && state.ledgerAccounts.some(
    item => Number(item.parentLedgerAccountId) === accountId);
  const showAccountColumn = accountId === null || selectedAccountHasChildren;
  const aggregateAccounts = accountId === null || selectedAccountHasChildren;
  const selectedAccountType = state.ledgerAccounts.find(item => Number(item.id) === accountId)?.type;
  const detailed = $('#statement-mode').value === 'detailed';
  const hideZeroAccounts = !detailed && $('#statement-hide-zero-accounts').checked;
  const visibleAccounts = accounts.filter(item => {
    if (hideZeroAccounts) return !statementHasZeroBalance(item);
    if (detailed)
      return !statementHasZeroBalance(item) || (Number(item.debit) || 0) !== 0 || (Number(item.credit) || 0) !== 0;
    return true;
  });
  const currencies = [...new Set(visibleAccounts.map(item => String(item.currency || '—')))].sort();
  const multipleCurrencies = currencies.length > 1;

  const summaryRows = visibleAccounts.map(item => `<tr class="${item.hasChildren ? 'statement-parent-row' : ''}"><td>${statementAccountCell(item)}</td><td>${escapeHtml(item.currency)}</td><td>${statementMoney(item.openingBalance, item.currency)}</td><td>${statementMoney(item.debit, item.currency)}</td><td>${statementMoney(item.credit, item.currency)}</td><td>${statementMoney(item.closingBalance, item.currency)}</td></tr>`);
  $('#statement-summary-body').innerHTML = summaryRows.join('');
  $('#statement-summary-card .table-wrap').classList.toggle('hidden', summaryRows.length === 0);
  $('#statement-summary-empty').classList.toggle('hidden', summaryRows.length > 0);

  const head = $('#statement-lines-head');
  const body = $('#statement-lines-body');
  const foot = $('#statement-lines-foot');
  const empty = $('#statement-lines-empty');
  if (!detailed) {
    const columns = aggregateAccounts
      ? `${showAccountColumn ? '<th>الحساب</th>' : ''}${multipleCurrencies ? '<th>العملة</th>' : ''}<th>مدين</th><th>دائن</th>`
      : `${multipleCurrencies ? '<th>العملة</th>' : ''}<th>إجمالي مدين</th><th>إجمالي دائن</th><th>الرصيد الجاري</th>`;
    head.innerHTML = `<tr>${columns}</tr>`;
    const rows = visibleAccounts.map(item => {
      if (!aggregateAccounts)
        return `<tr>${multipleCurrencies ? `<td>${escapeHtml(item.currency)}</td>` : ''}<td>${statementMoney(item.debit, item.currency)}</td><td>${statementMoney(item.credit, item.currency)}</td>${statementBalanceCell(item.closingBalance, item.currency, item.accountType)}</tr>`;
      const sides = statementAccountBalanceSides(item);
      return `<tr class="${item.hasChildren ? 'statement-parent-row' : ''}">${showAccountColumn ? `<td>${statementAccountCell(item)}</td>` : ''}${multipleCurrencies ? `<td>${escapeHtml(item.currency)}</td>` : ''}<td>${statementMoney(sides.debit, item.currency)}</td><td>${statementMoney(sides.credit, item.currency)}</td></tr>`;
    });
    body.innerHTML = rows.join('');
    const totals = aggregateAccounts
      ? statementBalanceTotalsByCurrency(accounts)
      : statementTotalsByCurrency(accounts);
    foot.innerHTML = totals.filter(total => !hideZeroAccounts || (Number(total.debit) || 0) !== 0 || (Number(total.credit) || 0) !== 0).map(total => {
      const currencyCell = multipleCurrencies ? `<th>${escapeHtml(total.currency)}</th>` : '';
      if (!aggregateAccounts) {
        return `<tr class="statement-total-row" aria-label="إجمالي ${escapeHtml(total.currency)}" title="إجمالي ${escapeHtml(total.currency)}">${currencyCell}<th>${statementMoney(total.debit, total.currency, multipleCurrencies ? '' : 'الإجمالي')}</th><th>${statementMoney(total.credit, total.currency)}</th>${statementBalanceCell(total.closingBalance, total.currency, selectedAccountType)}</tr>`;
      }
      const difference = total.debit - total.credit;
      const balanceDebit = Math.max(difference, 0);
      const balanceCredit = Math.max(-difference, 0);
      const label = selectedAccountHasChildren ? 'إجمالي السجلات التابعة' : 'إجمالي السجلات';
      return `<tr class="statement-total-row"><th>${label}</th>${currencyCell}<th>${statementMoney(total.debit, total.currency)}</th><th>${statementMoney(total.credit, total.currency)}</th></tr><tr class="statement-balance-row"><th>الرصيد</th>${currencyCell}<th>${statementMoney(balanceDebit, total.currency)}</th><th>${statementMoney(balanceCredit, total.currency)}</th></tr>`;
    }).join('');
    $('#statement-details-card .table-wrap').classList.toggle('hidden', rows.length === 0);
    empty.textContent = 'لا توجد حركات منشورة مطابقة للمرشحات.';
    empty.classList.toggle('hidden', rows.length > 0);
    return;
  }

  head.innerHTML = `<tr>${showAccountColumn ? '<th>الحساب</th>' : ''}<th>مدين</th><th>دائن</th><th>الرصيد الجاري</th><th>التاريخ</th><th>نوع العملية</th><th>البيان</th><th>رقم المرجع</th></tr>`;
  const rows = lines.map(item => `<tr>${showAccountColumn ? `<td>${statementAccountCell(item)}</td>` : ''}<td>${statementMoney(item.debit, item.currency)}</td><td>${statementMoney(item.credit, item.currency)}</td>${statementBalanceCell(item.runningBalance, item.currency, item.accountType)}<td>${date(item.occurredAtUtc || item.postedAtUtc)}</td><td>${escapeHtml(journalEntryTypes[item.entryType] || item.entryType)}</td><td><span class="cell-main">${escapeHtml(item.description || '—')}</span></td><td><span class="cell-main">${escapeHtml(item.entryReference || item.entryNumber || '—')}</span>${item.entryReference && item.entryNumber ? `<span class="cell-sub">${escapeHtml(item.entryNumber)}</span>` : ''}</td></tr>`);
  body.innerHTML = rows.join('');

  const activeSubtotalAccounts = visibleAccounts.filter(item =>
    !statementHasZeroBalance(item) || (Number(item.debit) || 0) !== 0 || (Number(item.credit) || 0) !== 0);
  const subtotalRows = aggregateAccounts ? activeSubtotalAccounts.map(item => `<tr class="statement-subtotal-row ${item.hasChildren ? 'statement-parent-row' : ''}"><th>إجمالي ${escapeHtml(item.accountCode)} — ${escapeHtml(item.accountName)} · ${escapeHtml(item.currency)}</th><td>${statementMoney(item.debit, item.currency)}</td><td>${statementMoney(item.credit, item.currency)}</td><td>${statementMoney(item.closingBalance, item.currency)}</td><td colspan="4"></td></tr>`) : [];
  const totals = statementTotalsByCurrency(accounts.filter(item => !item.hasChildren));
  const totalRows = totals.map(total => {
    const label = aggregateAccounts
      ? `${selectedAccountHasChildren ? 'إجمالي الحسابات التابعة' : 'الإجمالي العام'} · ${escapeHtml(total.currency)}`
      : `الإجمالي · ${escapeHtml(total.currency)}`;
    const balance = aggregateAccounts ? '—' : statementMoney(total.closingBalance, total.currency);
    if (!aggregateAccounts)
      return `<tr class="statement-total-row"><td>${statementMoney(total.debit, total.currency)}</td><td>${statementMoney(total.credit, total.currency)}</td><td>${balance}</td><th colspan="4">${label}</th></tr>`;
    return `<tr class="statement-total-row"><th>${label}</th><th>${statementMoney(total.debit, total.currency)}</th><th>${statementMoney(total.credit, total.currency)}</th><th>—</th><th colspan="4">إجمالي الحركات حسب العملة</th></tr>`;
  });
  foot.innerHTML = [...subtotalRows, ...totalRows].join('');
  $('#statement-details-card .table-wrap').classList.toggle('hidden', rows.length === 0);
  empty.textContent = 'لا توجد حركات منشورة مطابقة للمرشحات.';
  empty.classList.toggle('hidden', rows.length > 0);
}

async function initializeAccounting() {
  const button = $('#accounting-setup-button');
  button.disabled = true;
  try {
    const result = await execute('AccountingSetupModel', 'add', {});
    toast(result.message || 'تمت تهيئة وربط الحسابات المالية.');
    await loadLedgerAccounts();
    await loadAccountStatement();
  } catch (error) {
    if (!isAdminOperationCancelled(error)) toast(error.message, true);
  } finally { button.disabled = false; }
}

window.openSettlementForm = async (id, outstanding) => {
  await openForm('settlementPayment');
  $('#modal-fields [name="driverSettlementId"]').value = String(id);
  $('#modal-fields [name="amount"]').value = String(outstanding);
};

async function loadView(view, quiet = false) {
  if (!state.databaseConfigured && view !== 'console') {
    if (!quiet) toast('أعد اتصال قاعدة البيانات أولاً.', true);
    return;
  }
  try {
    if (view === 'overview') await loadDashboard();
    if (view === 'users') await loadUsers($('#user-search').value.trim());
    if (view === 'customers') await loadCustomers($('#customer-search').value.trim());
    if (view === 'drivers') await loadDrivers();
    if (view === 'driver-ratings') await loadDriverRatings();
    if (view === 'notification-campaigns') await loadNotificationCampaigns();
    if (view === 'rides') {
      await loadRides();
      await loadActiveRideSnapshot();
      if (liveRidesMap) setTimeout(() => liveRidesMap.invalidateSize(), 0);
    }
    if (view === 'cancellations') await loadCancellations();
    if (view === 'safety') await loadSafety();
    if (view === 'safety-recordings') await loadSafetyRecordings();
    if (view === 'catalog') await loadCatalog();
    if (view === 'pricing') await loadPricing();
    if (view === 'payment-methods') await loadPaymentMethods();
    if (view === 'settlements') await loadSettlements();
    if (view === 'ledger') { await loadLedgerAccounts(); await loadAccountStatement(); }
    if (view === 'financial-operations') {
      await loadLedgerAccounts();
      initializeFinancialOperationFilterDate();
      syncFinancialOperationForm();
      syncFinancialOperationEditState();
      await loadFinancialOperations();
    }
    if (view === 'financial-reports') await loadFinancialReport();
    if (view === 'places') await loadPlaces();
    if (view === 'areas') await loadAreas();
    if (view === 'history') await loadHistory();
    if (view === 'waiting-report') await loadWaitingReport();
    if (view === 'route-change-report') await loadRouteChangeReport();
  } catch (error) { if (!quiet) toast(error.message, true); }
}

async function loadPlaces() {
  const result = await execute('SavedPlaceAdminModel', 'list', {}); state.places = result.data;
  toggleEmpty('#places-body', '#places-empty', (result.data || []).map(item => `<tr><td><span class="cell-main">${escapeHtml(item.userName || 'بدون اسم')}</span><span class="cell-sub">${escapeHtml(item.userId)}</span></td><td>${escapeHtml(item.label)}</td><td>${escapeHtml(item.kind || 'place')}</td><td>${escapeHtml(item.address)}</td><td>${number(item.latitude)}, ${number(item.longitude)}</td><td>${date(item.updatedAtUtc || item.createdAtUtc)}</td></tr>`));
}

async function loadAreas() {
  const result = await execute('ServiceAreaModel', 'list', {}); state.areas = result.data || [];
  toggleEmpty('#areas-body', '#areas-empty', state.areas.map(item => `<tr><td>${escapeHtml(item.countryNameAr)}</td><td>${escapeHtml(item.countryCode)}</td><td>${escapeHtml(item.cityNameAr)}</td><td>${item.isActive ? badge('متاحة','success') : badge('موقوفة','warning')}</td><td>${date(item.updatedAtUtc || item.createdAtUtc)}</td><td>${recordActions('serviceArea', item.id)}</td></tr>`));
}

async function loadHistory() {
  const result = await execute('RideModel', 'list', {}); state.history = result.data;
  toggleEmpty('#history-body', '#history-empty', (result.data || []).map(item => `<tr><td>${escapeHtml(item.customerName || item.customerId)}</td><td>${escapeHtml(item.pickupDisplayName || item.pickupLabel || 'نقطة الانطلاق')} ← ${escapeHtml(item.destinationDisplayName || item.destinationLabel || 'الوجهة')}</td><td>${rideBadge(item.status)}</td><td>${number(item.customerPrice)} YER</td><td>${date(item.createdAtUtc)}</td></tr>`));
}

function setView(view, financialReportType = null, financialOperationType = null) {
  if (view === 'financial-reports' && financialReportDefinitions[financialReportType])
    state.financialReportType = financialReportType;
  if (view === 'financial-operations' && financialOperationDefinitions[financialOperationType])
    state.financialOperationType = financialOperationType;
  state.view = view;
  $$('.view').forEach(item => item.classList.toggle('active', item.id === `view-${view}`));
  const isActiveNavItem = item => item.dataset.view === view &&
    (view !== 'financial-reports' || item.dataset.financialReport === state.financialReportType) &&
    (view !== 'financial-operations' || item.dataset.financialOperation === state.financialOperationType);
  $$('.nav-item').forEach(item => item.classList.toggle('active', isActiveNavItem(item)));
  const activeItem = $$('.nav-item').find(isActiveNavItem);
  const activeGroup = activeItem?.closest('.nav-group');
  const activeSubgroup = activeItem?.closest('.nav-subgroup');
  $$('.nav-group').forEach(group => {
    group.classList.toggle('has-active', group === activeGroup);
    if (group === activeGroup) group.open = true;
  });
  $$('.nav-subgroup').forEach(group => {
    group.classList.toggle('has-active', group === activeSubgroup);
    if (group === activeSubgroup) group.open = true;
  });
  const pageTitle = view === 'financial-reports'
    ? financialReportDefinitions[state.financialReportType]
    : view === 'financial-operations' ? financialOperationDefinitions[state.financialOperationType] : titles[view];
  $('#page-title').textContent = pageTitle[0];
  $('#page-subtitle').textContent = pageTitle[1];
  if (view === 'financial-reports') {
    $('#financial-report-heading').textContent = pageTitle[0];
    $('#financial-report-subtitle').textContent = pageTitle[1];
    $('#financial-report-from-field').classList.toggle('hidden', state.financialReportType === 'position');
    $('#financial-report-to-label').textContent = state.financialReportType === 'position' ? 'تاريخ المركز المالي' : 'إلى تاريخ';
  }
  closeSidebar();
  return loadView(view);
}

function applySegmentedFieldDesign(root) {
  root.querySelectorAll('label.check-field').forEach(label => {
    const control = [...label.children].find(element => element.matches('input[type="checkbox"], input[type="radio"]'));
    if (!control) return;
    const title = [...label.childNodes]
      .filter(node => node !== control)
      .map(node => node.textContent || '')
      .join(' ')
      .trim();
    if (!title) return;
    const titleSegment = document.createElement('span');
    titleSegment.className = 'field-label-segment';
    titleSegment.textContent = title;
    const controlSegment = document.createElement('span');
    controlSegment.className = 'field-control-segment';
    controlSegment.append(control);
    label.replaceChildren(titleSegment, controlSegment);
    label.classList.remove('check-field');
    label.classList.add('segmented-check-field');
  });

  const labels = root.querySelectorAll('label:not(.check-field):not(.statement-field-label):not(.field-label-segment)');
  labels.forEach(label => {
    if (label.classList.contains('segmented-field')) return;
    const control = [...label.children].find(element => {
      if (!element.matches('input, select, textarea')) return false;
      return !['checkbox', 'radio', 'hidden'].includes((element.type || '').toLowerCase());
    });
    if (!control) return;

    const children = [...label.childNodes];
    const controlIndex = children.indexOf(control);
    if (controlIndex < 0) return;
    const titleNodes = children.slice(0, controlIndex);
    if (!titleNodes.some(node => (node.textContent || '').trim())) return;

    const titleContent = titleNodes.filter(node => node.nodeType !== Node.TEXT_NODE || node.textContent.trim());
    let titleSegment;
    if (titleContent.length === 1 && titleContent[0] instanceof HTMLElement && titleContent[0].tagName === 'SPAN') {
      titleSegment = titleContent[0];
      titleSegment.classList.add('field-label-segment');
    } else {
      titleSegment = document.createElement('span');
      titleSegment.className = 'field-label-segment';
      titleNodes.forEach(node => {
        if (node.nodeType !== Node.TEXT_NODE || node.textContent.trim()) titleSegment.append(node);
      });
    }

    const controlSegment = document.createElement('span');
    controlSegment.className = 'field-control-segment';
    controlSegment.append(control, ...children.slice(controlIndex + 1));
    label.replaceChildren(titleSegment, controlSegment);
    label.classList.add('segmented-field');
    if (control.matches('textarea')) label.classList.add('field-has-textarea');
    if (control.matches('input[type="file"]')) label.classList.add('field-has-file');
  });
}

function buildField(field) {
  const [name, label, type, required = false, initial = '', step = 'any'] = field;
  if (type === 'checkbox') return `<label class="check-field"><input name="${name}" type="checkbox" ${initial ? 'checked' : ''}> ${label}</label>`;
  if (type === 'file') return `<label>${label}<input name="${name}" type="file" accept="image/png,image/jpeg,image/webp,image/gif"><span class="file-preview" data-preview-for="${name}"></span></label>`;
  if (type === 'select') return `<label>${label}<select name="${name}" ${required ? 'required' : ''}>${(Array.isArray(initial) ? initial : []).map(option => `<option value="${escapeHtml(option[0])}">${escapeHtml(option[1])}</option>`).join('')}</select></label>`;
  if (type === 'textarea') return `<label class="full">${label}<textarea name="${name}" rows="3" ${required ? 'required' : ''}>${escapeHtml(initial)}</textarea></label>`;
  return `<label>${label}<input name="${name}" type="${type}" value="${escapeHtml(initial)}" ${type === 'number' ? `step="${step}"` : ''} ${required ? 'required' : ''}></label>`;
}

let activeForm = null;
const formEntityLabels = {
  user: 'المستخدم', driver: 'ملف السائق', ride: 'الرحلة', serviceKind: 'نوع الخدمة',
  serviceCatalog: 'الخدمة', pricingRule: 'قاعدة التسعير', paymentMethod: 'طريقة الدفع',
  serviceArea: 'منطقة الخدمة', driverRating: 'تقييم السائق'
};
const formStateKeys = {
  user: 'users', driver: 'drivers', ride: 'rides', serviceKind: 'kinds',
  serviceCatalog: 'catalog', pricingRule: 'pricing', paymentMethod: 'paymentMethods', serviceArea: 'areas'
};
const deletingRecords = new Set();

function recordActions(name, id, lockedReason = '') {
  const numericId = Number(id);
  if (!Number.isSafeInteger(numericId) || numericId <= 0) return '—';
  if (lockedReason) {
    return `<div class="action-row">${iconActionButton('edit', 'تعديل', null, { disabledReason: lockedReason })}${iconActionButton('delete', 'حذف', null, { disabledReason: lockedReason })}</div>`;
  }
  return `<div class="action-row">${iconActionButton('edit', 'تعديل', `editAdminEntity('${name}', ${numericId})`)}${iconActionButton('delete', 'حذف', `deleteAdminEntity('${name}', ${numericId})`)}</div>`;
}

function findFormEntity(name, id) {
  if (name === 'user')
    return [...(state.users || []), ...(state.customers || [])].find(item => Number(item.id) === Number(id)) || null;
  const stateKey = formStateKeys[name];
  return stateKey ? (state[stateKey] || []).find(item => Number(item.id) === Number(id)) : null;
}

window.editAdminEntity = async (name, id) => {
  const item = findFormEntity(name, id);
  if (!item) { toast('تعذر العثور على السجل. حدّث الصفحة ثم حاول مرة أخرى.', true); return; }
  try { await openForm(name, item); }
  catch (error) { toast(error.message || 'تعذر فتح نموذج التعديل.', true); }
};

window.deleteAdminEntity = async (name, id) => {
  const config = formConfigs[name];
  if (!config) return;
  const key = `${name}:${id}`;
  if (deletingRecords.has(key)) return;
  const label = formEntityLabels[name] || 'السجل';
  deletingRecords.add(key);
  try {
    const result = await execute(config.model, 'delete', { id: Number(id) });
    toast(result.message || `تم حذف ${label}.`);
    const refreshView = name === 'user' && state.view === 'customers' ? 'customers' : config.refresh;
    await loadView(refreshView, true);
  } catch (error) {
    if (!isAdminOperationCancelled(error)) toast(error.message || `تعذر حذف ${label}.`, true);
  } finally {
    deletingRecords.delete(key);
  }
};

function populateFormValues(form, entity, fields) {
  if (!entity) return;
  for (const [name,,type] of fields) {
    if (type === 'file') continue;
    const input = form.elements.namedItem(name);
    if (!input) continue;
    const value = entity[name];
    if (type === 'checkbox') input.checked = Boolean(value);
    else input.value = value == null ? '' : String(value);
  }
}

async function openForm(name, entity = null) {
  const config = formConfigs[name];
  if (!config) throw new Error('نموذج الإدارة غير متاح.');
  const editing = entity !== null;
  let fields = config.fields.map(field => [...field]);
  if (name === 'ride') {
    try {
      const result = await execute('UserModel', 'list', {});
      state.users = result.data || [];
    } catch { }
  } else if (name === 'driver' && !state.users.length) {
    try { await loadUsers(); } catch { }
  } else if (name === 'driverRating' && !state.drivers.length) {
    try { await loadDrivers(); } catch { }
  }
  if (name === 'settlementPayment' && !state.settlements.length) {
    try { await loadSettlements(); } catch { }
  }
  if (name === 'serviceCatalog' || name === 'serviceKind' || name === 'driver' || name === 'ride' || name === 'pricingRule' || name === 'quote') {
    if (!state.kinds.length || !state.catalog.length) { try { await loadCatalog(); } catch {} }
    const serviceKinds = (state.kinds || [])
      .map(x => [String(x.id), x.nameAr || x.code])
      .filter(option => option[0] && option[0] !== 'undefined');
    const serviceCatalogItems = (state.catalog || [])
      .map(x => [String(x.id), x.nameAr || x.code, String(x.serviceKindId)])
      .filter(option => option[0] && option[0] !== 'undefined');
    const driverUsers = (state.users || [])
      .filter(x => Number(x.role) !== 2)
      .map(x => [String(x.id), `${x.displayName || 'بدون اسم'} - ${x.phoneNumber || ''}`])
      .filter(option => option[0] && option[0] !== 'undefined');
    fields = fields.map(field => {
      if (field[0] === 'userId' && field[4] === 'driverUsers') return [field[0], field[1], 'select', field[3], driverUsers];
      if (field[0] === 'serviceKindId') return [field[0], field[1], 'select', field[3], serviceKinds];
      if (field[0] === 'serviceCatalogItemId') return [field[0], field[1], 'select', field[3], serviceCatalogItems];
      return field;
    });
  }
  if (name === 'ride') {
    const customerUsers = [
      ['', 'اختر العميل'],
      ...(state.users || [])
        .filter(x => Number(x.role) === 0 && x.isActive !== false)
        .map(x => [String(x.id), `${x.displayName || 'بدون اسم'} - ${x.phoneNumber || ''}`])
        .filter(option => option[0] && option[0] !== 'undefined')
    ];
    fields = fields.map(field => field[0] === 'customerId'
      ? [field[0], field[1], 'select', field[3], customerUsers]
      : field);
  }
  if (name === 'settlementPayment') {
    const openSettlements = [
      ['', 'اختر سجل المديونية'],
      ...(state.settlements || [])
        .filter(x => !x.isSettled && Number(x.amountOutstanding) > 0)
        .map(x => [String(x.id), `#${x.id} — ${x.driverName || `السائق ${x.driverId}`} — متبقٍ ${number(x.amountOutstanding)} YER`])
        .filter(option => option[0] && option[0] !== 'undefined')
    ];
    fields = fields.map(field => field[0] === 'driverSettlementId'
      ? [field[0], field[1], 'select', field[3], openSettlements]
      : field);
  }
  if (name === 'driverRating') {
    const driverUsers = [
      ['', 'اختر السائق'],
      ...(state.drivers || []).map(x => [String(x.userId), `${x.displayName || 'بدون اسم'} - ${x.phoneNumber || ''}`])
    ];
    fields = fields.map(field => field[0] === 'driverUserId'
      ? [field[0], field[1], 'select', field[3], driverUsers]
      : field);
  }
  if (editing && name === 'user') {
    fields = fields.map(field => field[0] === 'password' ? [field[0], 'كلمة المرور الجديدة (اختيارية)', field[2], false, '', field[5]] : field);
  }
  activeForm = {
    ...config,
    name,
    fields,
    operation: editing ? 'update' : config.operation,
    editId: editing ? Number(entity.id) : null,
    editing,
    sourceCampaignId: null
  };
  $('#modal-title').textContent = editing ? `تعديل ${formEntityLabels[name] || config.title}` : config.title;
  $('#modal-subtitle').textContent = editing
    ? 'عدّل البيانات ثم احفظ التغييرات. يبقى الحذف محمياً عند وجود سجلات مرتبطة.'
    : config.subtitle;
  $('#modal-submit').textContent = name === 'notificationCampaign' ? 'إرسال الإشعار' : 'حفظ';
  $('#modal-fields').innerHTML = fields.map(buildField).join('');
  if (name === 'pricingRule') {
    const limits = { routeChangeFee: [0,999999999], waitingFreeMinutes: [0,120], waitingPricePerMinute: [0,999999999], waitingMaxFee: [0,999999999], maxPriceDecreasePercent: [0,100], maxPriceIncreasePercent: [0,1000] };
    for (const [field, [min,max]] of Object.entries(limits)) {
      const input = document.querySelector('#modal-fields [name="' + field + '"]');
      if (input) { input.min = String(min); input.max = String(max); if (field === 'routeChangeFee') input.step = '1'; }
    }
    const help = document.createElement('p'); help.className = 'form-help';
    help.textContent = 'مثال: أجرة 1000 وخفض 20% ورفع 30% تسمح من 800 إلى 1300 ر.ي. تُحفظ السياسة مع الرحلة. الانتظار يحتاج موافقة الطرفين؛ تتوقف الرسوم عند الاستئناف أو الحركة المؤكدة أو انقطاع موقع السائق لدقيقتين. السقف يشمل جميع توقفات الرحلة. رسوم تغيير المسار تُضاف لكل تغيير يقبله الطرفان، وتُحفظ سياستها مع الرحلة.';
    document.querySelector('#modal-fields').append(help);
  }
  applySegmentedFieldDesign($('#modal-fields'));
  $$('#modal-fields input[type="file"]').forEach(input => input.addEventListener('change', () => {
    const preview = $(`#modal-fields [data-preview-for="${input.name}"]`);
    if (!preview) return;
    const file = input.files?.[0];
    preview.innerHTML = file ? `<small>${escapeHtml(file.name)}</small><img class="form-image-preview" src="${URL.createObjectURL(file)}" alt="">` : '';
  }));
  const form = $('#entity-form');
  populateFormValues(form, entity, fields);
  if (editing && name === 'driver') {
    const userSelect = $('#modal-fields [name="userId"]');
    if (userSelect) {
      userSelect.disabled = true;
      userSelect.closest('label')?.setAttribute('title', 'لا يمكن تغيير حساب ملف السائق بعد إنشائه.');
    }
  }
  const kindSelect = $('#modal-fields [name="serviceKindId"]');
  const catalogSelect = $('#modal-fields [name="serviceCatalogItemId"]');
  if (kindSelect && catalogSelect) {
    const allItems = (state.catalog || []).map(item => ({ id: String(item.id), name: item.nameAr || item.code, kindId: String(item.serviceKindId) }));
    const preferredCatalogId = entity?.serviceCatalogItemId == null ? '' : String(entity.serviceCatalogItemId);
    const refreshCatalogOptions = () => {
      const selected = String(kindSelect.value || '');
      catalogSelect.replaceChildren(...allItems.filter(item => !selected || item.kindId === selected).map(item => {
        const option = document.createElement('option'); option.value = item.id; option.textContent = item.name; return option;
      }));
      catalogSelect.disabled = catalogSelect.options.length === 0;
      if (preferredCatalogId && [...catalogSelect.options].some(option => option.value === preferredCatalogId)) catalogSelect.value = preferredCatalogId;
    };
    kindSelect.addEventListener('change', refreshCatalogOptions);
    refreshCatalogOptions();
  }
  $('#modal-result').className = 'inline-result hidden';
  $('#form-modal').showModal();
}

function readForm(form, fields) {
  const data = {};
  for (const [name,,type] of fields) {
    const input = form.elements.namedItem(name);
    if (!input) continue;
    if (type === 'file') continue;
    if (type === 'checkbox') data[name] = input.checked;
    else if (type === 'number') { if (input.value !== '') data[name] = Number(input.value); }
    else if (type === 'select' && /^\d+$/.test(input.value)) data[name] = Number(input.value);
    else {
      const value = input.value.trim();
      if (value !== '' || activeForm?.editing) data[name] = value;
    }
  }
  return data;
}

async function submitEntityForm(event) {
  event.preventDefault();
  const submit = $('#modal-submit'); submit.disabled = true;
  const resultBox = $('#modal-result'); resultBox.className = 'inline-result hidden';
  try {
    const form = event.currentTarget;
    const data = readForm(form, activeForm.fields);
    if (activeForm.editing) data.id = activeForm.editId;
    if (activeForm.name === 'notificationCampaign' && activeForm.sourceCampaignId)
      data.sourceCampaignId = activeForm.sourceCampaignId;
    if (!readOnlyOperations.has(String(activeForm.operation || '').toLowerCase()) &&
        !confirmAdminOperation(activeForm.model, activeForm.operation, data)) return;
    const image = form.elements.imageFile?.files?.[0];
    if (image) {
      const upload = new FormData(); upload.append('file', image);
      const uploadResponse = await adminFetch('/api/media/upload', { method: 'POST', body: upload });
      const uploaded = await uploadResponse.json().catch(() => ({ success: false }));
      if (!uploaded.success) throw new Error(uploaded.message || 'تعذر رفع الصورة.');
      data[activeForm.imageField || 'imageUrl'] = uploaded.url;
    }
    if (activeForm.name === 'ride') {
      const quote = await execute('PricingModel', 'report', { serviceKindId: data.serviceKindId, serviceCatalogItemId: data.serviceCatalogItemId, customerId: data.customerId, pickupLatitude: data.pickupLatitude, pickupLongitude: data.pickupLongitude, destinationLatitude: data.destinationLatitude, destinationLongitude: data.destinationLongitude, distanceKm: 0, durationMinutes: 0 });
      data.pricingQuoteToken = quote.data.pricingQuoteToken;
    }
    const result = await execute(activeForm.model, activeForm.operation, data, { confirmed: true });
    resultBox.className = 'inline-result success';
    const imageUrl = result.data?.imageUrl || result.data?.photoUrl;
    if (activeForm.name === 'notificationCampaign' && result.data?.recipientCount != null) {
      const target = notificationCampaignTargets[Number(result.data.target)] || 'المستهدفين';
      const message = `حُفظ سجل الحملة وأُنشئ الإشعار لـ ${number(result.data.recipientCount)} حساب من ${target}. تتم متابعة إرسال الإشعارات للأجهزة المسجلة بالخلفية.`;
      resultBox.textContent = message;
      toast(message);
    } else {
      resultBox.innerHTML = `${escapeHtml(result.message)}${imageUrl ? `<br><img class="form-image-preview" src="${escapeHtml(imageUrl)}" alt=""><br><small>${escapeHtml(imageUrl)}</small>` : ''}${activeForm.name === 'quote' ? `<pre>${escapeHtml(JSON.stringify(result.data, null, 2))}</pre>` : ''}`;
      toast(result.message);
    }
    if (activeForm.refresh) {
      const refreshView = activeForm.name === 'user' && state.view === 'customers'
        ? 'customers'
        : activeForm.refresh === 'wallet' ? state.view : activeForm.refresh;
      await loadView(refreshView, true);
    }
    if (activeForm.name !== 'quote') setTimeout(() => $('#form-modal').close(), 700);
  } catch (error) {
    if (!isAdminOperationCancelled(error)) {
      resultBox.className = 'inline-result error'; resultBox.textContent = error.message;
    }
  }
  finally { submit.disabled = false; }
}

function fillDatabaseForm(settings) {
  const provider = $('#db-provider');
  provider.innerHTML = (settings.providers || []).map(item => `<option value="${escapeHtml(item.value)}">${escapeHtml(item.label)}</option>`).join('');
  provider.value = settings.provider || 'SqlServer';
  $('#db-server').value = settings.server || ''; $('#db-name').value = settings.database || '';
  $('#db-username').value = settings.username || ''; $('#db-integrated').checked = settings.integratedSecurity;
  $('#db-trust').checked = settings.trustServerCertificate; $('#db-encrypt').checked = settings.encrypt;
  syncDatabaseAuth();
}

function readDatabaseForm() {
  return { provider: $('#db-provider').value, server: $('#db-server').value.trim(), database: $('#db-name').value.trim(), username: $('#db-username').value.trim(), password: $('#db-password').value, integratedSecurity: $('#db-integrated').checked, trustServerCertificate: $('#db-trust').checked, encrypt: $('#db-encrypt').checked };
}

async function sendDatabase(endpoint) {
  if (endpoint.endsWith('/save') && !confirmAdminOperation('DatabaseConfigurationModel', 'save')) return;
  const resultBox = $('#database-result'); resultBox.className = 'inline-result'; resultBox.textContent = 'جارٍ تنفيذ الطلب...';
  try {
    const response = await fetch(endpoint, { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(readDatabaseForm()) });
    const result = await response.json();
    resultBox.className = `inline-result ${result.success ? 'success' : 'error'}`; resultBox.textContent = result.message;
    if (result.success && endpoint.endsWith('/save')) {
      await loadDatabaseStatus();
      toast(result.message);
      setTimeout(() => $('#database-modal').close(), 500);
      showAdminLogin('تم تجهيز قاعدة البيانات. استخدم بيانات المدير لتسجيل الدخول.');
    }
  } catch { resultBox.className = 'inline-result error'; resultBox.textContent = 'تعذر الوصول إلى خدمة إعداد قاعدة البيانات.'; }
}

function syncDatabaseAuth() { const disabled = $('#db-integrated').checked; $('#db-username').disabled = disabled; $('#db-password').disabled = disabled; }
function closeSidebar() { $('#sidebar').classList.remove('open'); $('#sidebar-backdrop').classList.remove('open'); }

function applyAdminTopbarTheme(theme, persist = false) {
  const selectedTheme = theme === 'light' ? 'light' : 'dark';
  document.documentElement.dataset.adminTopbarTheme = selectedTheme;
  const button = $('#topbar-theme-toggle');
  if (button) {
    const switchingToLight = selectedTheme === 'dark';
    button.innerHTML = switchingToLight
      ? '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="4"></circle><path d="M12 2v2m0 16v2M4.93 4.93l1.42 1.42m11.3 11.3 1.42 1.42M2 12h2m16 0h2M4.93 19.07l1.42-1.42m11.3-11.3 1.42-1.42"></path></svg>'
      : '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20.2 15.1A8.6 8.6 0 0 1 8.9 3.8 8.7 8.7 0 1 0 20.2 15.1Z"></path></svg>';
    const actionLabel = switchingToLight ? 'تغيير الشريط إلى الوضع الفاتح' : 'تغيير الشريط إلى الوضع الداكن';
    button.title = actionLabel;
    button.setAttribute('aria-label', `الشريط العلوي ${selectedTheme === 'dark' ? 'داكن' : 'فاتح'}، انقر للتبديل إلى ${switchingToLight ? 'الفاتح' : 'الداكن'}`);
    button.setAttribute('aria-pressed', String(selectedTheme === 'dark'));
  }
  if (persist) {
    try { localStorage.setItem(ADMIN_TOPBAR_THEME_KEY, selectedTheme); } catch {}
  }
}

function loadAdminTopbarTheme() {
  try { return localStorage.getItem(ADMIN_TOPBAR_THEME_KEY) === 'light' ? 'light' : 'dark'; }
  catch { return 'dark'; }
}

function updatePageScrollJump() {
  const button = $('#page-scroll-jump');
  const documentElement = document.documentElement;
  const position = Math.max(window.scrollY || 0, documentElement.scrollTop || 0, document.body.scrollTop || 0);
  const bottom = Math.max(documentElement.scrollHeight, document.body.scrollHeight) - window.innerHeight;
  if (position > lastPageScrollPosition + 2) pageScrollDirection = 'down';
  else if (position < lastPageScrollPosition - 2) pageScrollDirection = 'up';
  lastPageScrollPosition = position;

  if (bottom <= 100 || position <= 56) {
    button.classList.remove('is-visible');
    button.hidden = true;
    return;
  }
  if (position >= bottom - 56) pageScrollDirection = 'up';
  button.textContent = pageScrollDirection === 'down' ? '↓' : '↑';
  const label = pageScrollDirection === 'down' ? 'الانتقال إلى نهاية الصفحة' : 'العودة إلى أعلى الصفحة';
  button.setAttribute('aria-label', label);
  button.title = label;
  const wasHidden = button.hidden;
  button.hidden = false;
  if (wasHidden) {
    button.classList.remove('is-visible');
    requestAnimationFrame(() => button.classList.add('is-visible'));
  } else {
    button.classList.add('is-visible');
  }
}

function schedulePageScrollJumpUpdate() {
  if (pageScrollUpdatePending) return;
  pageScrollUpdatePending = true;
  requestAnimationFrame(() => {
    pageScrollUpdatePending = false;
    updatePageScrollJump();
  });
}

function initializePageScrollJump() {
  const button = $('#page-scroll-jump');
  lastPageScrollPosition = window.scrollY || document.documentElement.scrollTop || 0;
  button.addEventListener('click', () => {
    const documentElement = document.documentElement;
    const bottom = Math.max(documentElement.scrollHeight, document.body.scrollHeight) - window.innerHeight;
    const target = pageScrollDirection === 'down' ? bottom : 0;
    const reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    window.scrollTo({ top: target, behavior: reduceMotion ? 'auto' : 'smooth' });
  });
  window.addEventListener('scroll', schedulePageScrollJumpUpdate, { passive: true });
  window.addEventListener('resize', schedulePageScrollJumpUpdate, { passive: true });
  schedulePageScrollJumpUpdate();
}

function bindEvents() {
  bindActionTooltips();
  initializePageScrollJump();
  initializeFinancialReportDates();
  initializeFinancialOperationDate();
  initializeFinancialOperationFilterDate();
  $('#topbar-theme-toggle').addEventListener('click', () => {
    const currentTheme = document.documentElement.dataset.adminTopbarTheme;
    applyAdminTopbarTheme(currentTheme === 'dark' ? 'light' : 'dark', true);
  });
  window.addEventListener('storage', event => {
    if (event.key === ADMIN_TOPBAR_THEME_KEY)
      applyAdminTopbarTheme(event.newValue === 'light' ? 'light' : 'dark');
  });
  $$('.nav-item').forEach(button => button.addEventListener('click', () =>
    setView(button.dataset.view, button.dataset.financialReport || null, button.dataset.financialOperation || null)));
  $$('[data-go]').forEach(button => button.addEventListener('click', () => setView(button.dataset.go)));
  $$('[data-form]').forEach(button => button.addEventListener('click', () => openForm(button.dataset.form)));
  $$('[data-close]').forEach(button => button.addEventListener('click', () => document.getElementById(button.dataset.close).close()));
  $('#refresh-button').addEventListener('click', async () => { await loadDatabaseStatus(); await loadView(state.view); });
  $('#admin-notifications-button').addEventListener('click', async () => {
    try {
      await loadAdminNotifications();
      $('#admin-notifications-modal').showModal();
    } catch (error) {
      toast(error.message, true);
    }
  });
  $('#database-button').addEventListener('click', () => $('#database-modal').showModal());
  $('#menu-button').addEventListener('click', () => { $('#sidebar').classList.add('open'); $('#sidebar-backdrop').classList.add('open'); });
  $('#sidebar-backdrop').addEventListener('click', closeSidebar);
  $('#entity-form').addEventListener('submit', submitEntityForm);
  $('#ride-filter').addEventListener('change', loadRides);
  $('#cancellation-filter').addEventListener('change', () => loadCancellations().catch(error => toast(error.message, true)));
  $('#cancellations-refresh').addEventListener('click', () => loadCancellations().catch(error => toast(error.message, true)));
  $('#safety-filter').addEventListener('change', () => loadSafety().catch(error => toast(error.message, true)));
  $('#safety-refresh').addEventListener('click', () => loadSafety().catch(error => toast(error.message, true)));
  $('#safety-recordings-refresh').addEventListener('click', () => loadSafetyRecordings().catch(error => toast(error.message, true)));
  $('#safety-map-modal').addEventListener('close', () => { clearInterval(safetyMapTimer); safetyMapTimer = null; safetyMapIncidentId = null; safetyMapLastMarker = ''; $('#safety-map-frame').removeAttribute('src'); });
  $('#safety-map-load').addEventListener('click', () => {
    if (!safetyMapPendingUrl) { toast('لا تتوفر إحداثيات لعرضها على الخريطة.', true); return; }
    safetyMapLoaded = true;
    safetyMapLastMarker = safetyMapPendingMarker;
    $('#safety-map-frame').src = safetyMapPendingUrl;
    $('#safety-map-frame').classList.remove('hidden');
    $('#safety-map-load').classList.add('hidden');
    $('#safety-map-route-link').classList.toggle('hidden', !$('#safety-map-route-link').href);
  });
  let searchTimer; $('#user-search').addEventListener('input', event => { clearTimeout(searchTimer); searchTimer = setTimeout(() => loadUsers(event.target.value.trim()).catch(error => toast(error.message, true)), 300); });
  $('#customer-search-button').addEventListener('click', () => loadCustomers($('#customer-search').value.trim()).catch(error => toast(error.message, true)));
  $('#customer-search').addEventListener('keydown', event => {
    if (event.key === 'Enter') { event.preventDefault(); loadCustomers(event.currentTarget.value.trim()).catch(error => toast(error.message, true)); }
  });
  $('#customer-sort-button').addEventListener('click', () => {
    $('#customer-sort-by').value = state.customerSort.sortBy;
    $('#customer-sort-direction').value = state.customerSort.descending ? 'desc' : 'asc';
    $('#customer-sort-modal').showModal();
  });
  $('#customer-sort-form').addEventListener('submit', event => {
    event.preventDefault();
    state.customerSort = {
      sortBy: $('#customer-sort-by').value,
      descending: $('#customer-sort-direction').value === 'desc'
    };
    $('#customer-sort-modal').close();
    loadCustomers($('#customer-search').value.trim()).catch(error => toast(error.message, true));
  });
  $('#customer-sort-reset').addEventListener('click', () => {
    state.customerSort = { sortBy: 'createdAt', descending: true };
    $('#customer-sort-modal').close();
    loadCustomers($('#customer-search').value.trim()).catch(error => toast(error.message, true));
  });
  $('#driver-rating-filter').addEventListener('change', () => loadDriverRatings().catch(error => toast(error.message, true)));
  $('#driver-ratings-refresh').addEventListener('click', () => loadDriverRatings().catch(error => toast(error.message, true)));
  $('#wallet-load').addEventListener('click', () => loadWallet().catch(error => toast(error.message, true)));
  $('#wallet-phone-number').addEventListener('keydown', event => {
    if (event.key === 'Enter') { event.preventDefault(); loadWallet().catch(error => toast(error.message, true)); }
  });
  $('#statement-load').addEventListener('click', () => loadAccountStatement().catch(error => toast(error.message, true)));
  $('#financial-report-load').addEventListener('click', () => loadFinancialReport().catch(error => toast(error.message, true)));
  $('#financial-report-to').addEventListener('keydown', event => {
    if (event.key === 'Enter') { event.preventDefault(); loadFinancialReport().catch(error => toast(error.message, true)); }
  });
  $('#financial-operation-type').addEventListener('change', () => {
    state.financialOperationType = $('#financial-operation-type').value;
    syncFinancialOperationForm();
  });
  $('#financial-operation-debit-account').addEventListener('change', syncFinancialOperationForm);
  $('#financial-operation-credit-account').addEventListener('change', syncFinancialOperationForm);
  $('#financial-operation-form').addEventListener('submit', submitFinancialOperation);
  $('#financial-operation-cancel-edit').addEventListener('click', cancelFinancialOperationEdit);
  $('#financial-operation-filter-load').addEventListener('click', () =>
    loadFinancialOperations().catch(error => toast(error.message, true)));
  $('#financial-operation-filter-date').addEventListener('keydown', event => {
    if (event.key === 'Enter') {
      event.preventDefault();
      loadFinancialOperations().catch(error => toast(error.message, true));
    }
  });
  const accountSearch = $('#statement-account-search');
  const accountOptions = $('#statement-account-options');
  document.body.appendChild(accountOptions);
  const matchStatementAccount = value => state.ledgerAccounts.find(
    item => statementAccountLabel(item) === String(value || '').trim());
  const syncStatementAccountSelection = () => {
    const account = matchStatementAccount(accountSearch.value);
    $('#statement-account').value = account ? String(account.id) : '';
    if (account) hideStatementAccountOptions();
    else renderStatementAccountOptions(accountSearch.value);
  };
  accountSearch.addEventListener('focus', () => renderStatementAccountOptions(''));
  accountSearch.addEventListener('input', syncStatementAccountSelection);
  accountSearch.addEventListener('change', syncStatementAccountSelection);
  accountSearch.addEventListener('keydown', event => {
    if (event.key === 'Escape') { hideStatementAccountOptions(); return; }
    if (event.key === 'ArrowDown' && !accountOptions.hidden) {
      event.preventDefault();
      accountOptions.querySelector('[data-account-id]')?.focus();
      return;
    }
    if (event.key === 'Enter' && !accountOptions.hidden) {
      const exact = matchStatementAccount(accountSearch.value);
      const first = accountOptions.querySelector('[data-account-id]');
      if (!exact && first) { event.preventDefault(); chooseStatementAccount(first.dataset.accountId); }
    }
  });
  accountOptions.addEventListener('pointerdown', event => {
    if (event.target.closest('[data-account-id]')) event.preventDefault();
  });
  accountOptions.addEventListener('click', event => {
    const option = event.target.closest('[data-account-id]');
    if (option) chooseStatementAccount(option.dataset.accountId);
  });
  accountOptions.addEventListener('keydown', event => {
    if (event.key === 'Escape') { hideStatementAccountOptions(); accountSearch.focus(); return; }
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
    const options = [...accountOptions.querySelectorAll('[data-account-id]')];
    const currentIndex = options.indexOf(document.activeElement);
    const nextIndex = event.key === 'ArrowDown' ? currentIndex + 1 : currentIndex - 1;
    event.preventDefault();
    if (nextIndex < 0) accountSearch.focus();
    else if (options[nextIndex]) options[nextIndex].focus();
  });
  document.addEventListener('pointerdown', event => {
    if (!event.target.closest('.statement-account-picker') && !accountOptions.contains(event.target))
      hideStatementAccountOptions();
  });
  window.addEventListener('resize', () => { if (!accountOptions.hidden) positionStatementAccountOptions(); });
  document.addEventListener('scroll', () => { if (!accountOptions.hidden) positionStatementAccountOptions(); }, true);
  document.addEventListener('scroll', event => {
    if (event.target === accountOptions || accountOptions.contains(event.target)) return;
    hideStatementAccountOptions();
  }, true);
  window.addEventListener('resize', hideStatementAccountOptions);
  $('#statement-period').addEventListener('change', syncStatementPeriodFields);
  $('#statement-mode').addEventListener('change', syncStatementModeFields);
  $('#statement-hide-zero-accounts').addEventListener('change', renderAccountStatement);
  $('#accounting-setup-button').addEventListener('click', () => initializeAccounting().catch(error => toast(error.message, true)));
  $('#db-integrated').addEventListener('change', syncDatabaseAuth);
  $('#db-test').addEventListener('click', () => sendDatabase('/api/setup/database/test'));
  $('#database-form').addEventListener('submit', event => { event.preventDefault(); sendDatabase('/api/setup/database/save'); });
  $('#development-seed-button').addEventListener('click', seedDevelopmentData);
  $('#admin-login-form').addEventListener('submit', async event => {
    event.preventDefault();
    const submit = event.currentTarget.querySelector('button[type="submit"]');
    const result = $('#admin-login-result');
    submit.disabled = true;
    result.className = 'inline-result';
    result.textContent = 'جارٍ تسجيل الدخول...';
    try {
      const session = await adminLogin($('#admin-phone').value.trim(), $('#admin-password').value);
      result.className = 'inline-result success';
      result.textContent = session.message || 'تم تسجيل الدخول.';
      showAdminApp();
      await loadDatabaseStatus();
      await startAdminRealtime();
      await loadAdminNotifications();
      await loadView('overview', true);
    } catch (error) {
      result.className = 'inline-result error';
      result.textContent = error.message;
    } finally { submit.disabled = false; }
  });
  $('#admin-logout-button').addEventListener('click', () => adminLogout());
  $('#console-form').addEventListener('submit', async event => {
    event.preventDefault();
    try {
      const data = JSON.parse($('#console-data').value || '{}');
      const result = await execute($('#console-model').value.trim(), $('#console-operation').value.trim(), data);
      $('#console-response').textContent = JSON.stringify(result, null, 2);
    } catch (error) { $('#console-response').textContent = JSON.stringify({ success:false, message:error.message }, null, 2); }
  });
  $('#copy-response').addEventListener('click', () => navigator.clipboard.writeText($('#console-response').textContent).then(() => toast('تم نسخ الاستجابة.')));
}

window.addEventListener('beforeunload', () => {
  clearInterval(safetyMapTimer);
  for (const url of safetyAudioUrls.values()) URL.revokeObjectURL(url);
});

async function start() {
  applyAdminTopbarTheme(loadAdminTopbarTheme());
  applySegmentedFieldDesign(document);
  bindEvents();
  const settings = await loadDatabaseStatus();
  if (!settings?.configured) {
    showAdminApp();
    $('#database-modal').showModal();
    return;
  }

  localStorage.removeItem(ADMIN_TOKEN_KEY);
  localStorage.removeItem(ADMIN_REFRESH_TOKEN_KEY);
  const token = sessionStorage.getItem(ADMIN_TOKEN_KEY);
  if (!token) {
    showAdminLogin();
    return;
  }

  showAdminApp();
  try {
    await startAdminRealtime();
    await loadAdminNotifications();
    await loadView('overview', true);
  } catch (error) {
    if (sessionStorage.getItem(ADMIN_TOKEN_KEY)) {
      showAdminApp();
      toast(error.message || 'تعذر الاتصال بالخادم للتحقق من الجلسة.', true);
    } else {
      showAdminLogin('انتهت جلسة الإدارة. سجل الدخول من جديد.');
    }
  }
}

start();

document.addEventListener('click', async event => {
  const button = event.target.closest('[data-waiting-ride]');
  if (!button) return;
  button.disabled = true;
  try {
    const result = await execute('RideWaitingModel', 'get', { rideId: Number(button.dataset.waitingRide) });
    const data = result.data;
    let dialog = document.getElementById('waiting-details-dialog');
    if (!dialog) { dialog = document.createElement('dialog'); dialog.id = 'waiting-details-dialog'; document.body.append(dialog); }
    const reasons = { driver_resumed: 'استأنف السائق', vehicle_moving: 'حركة السيارة', connection_lost: 'انقطاع الموقع', fee_cap: 'بلوغ السقف', rejected: 'رفض', withdrawn: 'سحب الطلب', request_expired: 'انتهاء مهلة الموافقة', ride_cancelled: 'إلغاء الرحلة', ride_closed: 'إغلاق الرحلة' };
    dialog.innerHTML = '<h3>تفاصيل الانتظار</h3><p>المجاني لكل توقف: ' + number(data.waitingFreeMinutes) + ' دقيقة — الدقيقة: ' + number(data.waitingPricePerMinute) + ' ر.ي — السقف للرحلة: ' + number(data.waitingMaxFee) + ' ر.ي</p><p>رسوم الانتظار الحالية: ' + number(data.waitingFee) + ' ر.ي — إجمالي الرحلة الحالي: ' + number(data.totalAmount) + ' ر.ي</p>' + (data.history || []).map(item => '<p>طلب #' + number(item.id) + ' — صاحب الطلب #' + number(item.requestedByUserId) + ' — الموافق: ' + (item.approvedByUserId ? '#' + number(item.approvedByUserId) : 'لم يوافق بعد') + '<br>بدأ: ' + date(item.startedAtUtc) + ' — انتهى: ' + date(item.endedAtUtc) + ' — ' + escapeHtml(reasons[item.endReason] || 'مفتوح') + ' — الرسوم المثبتة: ' + number(item.fee) + ' ر.ي</p>').join('') + '<form method="dialog"><button class="btn" type="submit">إغلاق</button></form>';
    dialog.showModal();
  } catch (error) { toast(error.message); }
  finally { button.disabled = false; }
});
