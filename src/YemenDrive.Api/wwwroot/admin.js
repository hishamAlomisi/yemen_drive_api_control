const state = {
  view: 'overview',
  databaseConfigured: false,
  users: [],
  drivers: [],
  rides: [],
  catalog: [],
  kinds: [],
  pricing: [], places: [], history: [], settlements: [], ledgerAccounts: [], paymentMethods: []
};

const ADMIN_TOKEN_KEY = 'yemendrive_admin_token';

const titles = {
  overview: ['نظرة عامة', 'متابعة حالة النظام والعمليات الأخيرة'],
  users: ['المستخدمون', 'إدارة حسابات العملاء والسائقين'],
  drivers: ['السائقون', 'المركبات والخدمات وحالة التوفر'],
  rides: ['الرحلات', 'متابعة الطلبات وحالات التنفيذ'],
  catalog: ['الخدمات', 'كتالوج الخدمات والتصنيفات'],
  pricing: ['التسعير', 'قواعد احتساب أسعار الرحلات'],
  wallet: ['المحفظة', 'الأرصدة والحركات المالية'],
  'payment-methods': ['طرق الدفع الخارجية', 'إدارة وسائل الشحن والدفع الظاهرة للعميل'],
  settlements: ['مديونيات السائقين', 'تحصيل عمولة المنصة من الرحلات النقدية'],
  ledger: ['كشف الحساب', 'استعلام القيود المحاسبية المنشورة والأرصدة'],
  places: ['الأماكن المفضلة', 'الأماكن المحفوظة للمستخدمين'],
  history: ['سجل الرحلات', 'الرحلات السابقة والقادمة'],
  console: ['محطة API', 'تنفيذ الطلبات المتقدمة']
};

const rideStatuses = ['مسودة', 'جاري البحث', 'تفاوض', 'تم تعيين سائق', 'السائق في الطريق', 'قيد التنفيذ', 'مكتملة', 'ملغاة'];
const roles = ['عميل', 'سائق', 'مدير'];
const transactionTypes = ['إيداع', 'خصم', 'حجز', 'تحرير', 'استرداد', 'عمولة'];
const journalEntryTypes = ['رصيد افتتاحي', 'تحصيل نقدي لرحلة', 'دفع رحلة من المحفظة', 'تغذية محفظة', 'إلغاء رحلة', 'تحصيل تسوية سائق', 'تسوية يدوية', 'قيد عكسي'];

const formConfigs = {
  user: {
    title: 'إضافة مستخدم', subtitle: 'إنشاء حساب جديد ومحفظة تلقائية', model: 'UserModel', operation: 'add', refresh: 'users',
    fields: [
      ['displayName', 'الاسم', 'text', true], ['phoneNumber', 'رقم الهاتف', 'tel', true],
      ['email', 'البريد الإلكتروني', 'email'], ['password', 'كلمة المرور', 'password', true],
      ['role', 'الدور', 'select', true, [['0','عميل'],['1','سائق'],['2','مدير']]],
      ['city', 'المدينة', 'text'], ['street', 'الشارع', 'text'], ['district', 'الحي', 'text']
    ]
  },
  driver: {
    title: 'إضافة ملف سائق', subtitle: 'ربط مستخدم ببيانات المركبة والخدمة', model: 'DriverModel', operation: 'add', refresh: 'drivers',
    fields: [
      ['userId','حساب المستخدم','select',true,'driverUsers'],
      ['serviceKindId','نوع الخدمة','select',true,'serviceKinds'], ['serviceCatalogItemId','الخدمة','select',true,'serviceCatalogItems'],
      ['vehicleModel','موديل المركبة','text',true], ['plateNumber','رقم اللوحة','text',true],
      ['commissionRate','نسبة العمولة','number',false,'0.20','0.01'], ['rating','التقييم','number',false,'5','0.1'],
      ['isAvailable','متاح للعمل','checkbox',false,true]
    ]
  },
  driverLocation: {
    title: 'تحديث موقع السائق', subtitle: 'حفظ آخر إحداثيات وحالة الاتصال', model: 'DriverLocationModel', operation: 'update', refresh: 'drivers',
    fields: [
      ['driverId','معرف مستخدم السائق','text',true], ['latitude','خط العرض','number',true,'15.3694','0.000001'],
      ['longitude','خط الطول','number',true,'44.1910','0.000001'], ['bearing','الاتجاه','number'],
      ['speed','السرعة','number'], ['isOnline','متصل الآن','checkbox',false,true]
    ]
  },
  ride: {
    title: 'إنشاء رحلة', subtitle: 'تسجيل طلب رحلة جديد للعميل', model: 'RideModel', operation: 'add', refresh: 'rides',
    fields: [
      ['customerId','معرف العميل','text',true], ['serviceKindId','نوع الخدمة','select',true,'serviceKinds'],
      ['serviceCatalogItemId','الخدمة','select',true,'serviceCatalogItems'],
      ['pickupLabel','اسم نقطة الانطلاق','text'], ['pickupAddress','عنوان الانطلاق','text',true],
      ['pickupLatitude','خط عرض الانطلاق','number',true,'15.3694','0.000001'], ['pickupLongitude','خط طول الانطلاق','number',true,'44.1910','0.000001'],
      ['destinationLabel','اسم الوجهة','text'], ['destinationAddress','عنوان الوجهة','text',true],
      ['destinationLatitude','خط عرض الوجهة','number',true,'15.3547','0.000001'], ['destinationLongitude','خط طول الوجهة','number',true,'44.2067','0.000001'],
      ['customerPrice','سعر العميل','number',false,'0','0.01']
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
    title: 'إضافة قاعدة تسعير', subtitle: 'تحديد السعر ورسوم العميل وعمولة السائق', model: 'PricingRuleModel', operation: 'add', refresh: 'pricing',
    fields: [['serviceKindId','نوع الخدمة','select',true,'serviceKinds'],['serviceCatalogItemId','الخدمة','select',true,'serviceCatalogItems'],['baseFare','السعر الأساسي','number',true,'500','0.01'],['perKilometer','لكل كيلومتر','number',true,'100','0.01'],['perMinute','لكل دقيقة','number',true,'20','0.01'],['serviceFee','رسوم الخدمة الثابتة على العميل','number',true,'0','0.01'],['driverCommissionRate','نسبة عمولة السائق (0 إلى 1)','number',true,'0','0.01'],['driverCommissionFixed','عمولة السائق الثابتة','number',true,'0','0.01'],['cancellationFee','رسم الإلغاء','number',true,'0','0.01'],['driverShareRate','نسبة السائق القديمة','number',false,'0','0.01'],['isActive','نشطة','checkbox',false,true]]
  },
  quote: {
    title: 'تجربة التسعير', subtitle: 'احتساب سعر رحلة دون حفظها', model: 'PricingModel', operation: 'report',
    fields: [['serviceKindId','نوع الخدمة','select',true,'serviceKinds'],['serviceCatalogItemId','الخدمة','select',true,'serviceCatalogItems'],['distanceKm','المسافة بالكيلومتر','number',true,'8.5','0.1'],['durationMinutes','المدة بالدقائق','number',true,'20','0.1']]
  },
  settlementPayment: {
    title: 'تحصيل مديونية سائق', subtitle: 'ينشئ قيد تحصيل جديد؛ لا يعدّل الدفعة أو المديونية الأصلية.', model: 'DriverSettlementPaymentModel', operation: 'add', refresh: 'settlements',
    fields: [['driverSettlementId','معرف سجل المديونية','number',true],['amount','المبلغ المحصل','number',true,'0','0.01'],['currency','العملة','text',true,'YER'],['method','طريقة التحصيل','select',true,[['CashToPlatform','نقد إلى الإدارة'],['BankTransfer','تحويل بنكي'],['WalletTransfer','تحويل محفظة']]],['reference','مرجع التحصيل الفريد','text',true],['note','ملاحظة','textarea']]
  }
  ,paymentMethod: {
    title: 'إضافة طريقة دفع خارجية', subtitle: 'بيانات تعريف وتشغيل فقط؛ لا تضع مفاتيح API أو بيانات سرية هنا.', model: 'PaymentMethodModel', operation: 'add', refresh: 'payment-methods',
    fields: [['code','رمز فريد','text',true],['nameAr','الاسم الظاهر للعميل','text',true],['descriptionAr','الوصف المختصر','textarea',true],['imageFile','الشعار أو الصورة','file'],['imageUrl','رابط الصورة (اختياري)','url'],['kind','النوع','select',true,[['0','محفظة خارجية'],['1','بطاقة'],['2','حساب/تحويل بنكي']]],['providerCode','رمز المزود غير السري','text',true],['publicInstructionsAr','تعليمات ظاهرة للعميل','textarea'],['sortOrder','الترتيب','number',false,'0'],['isAvailableForRidePayment','تظهر أثناء دفع الرحلة','checkbox',false,true],['isAvailableForWalletTopUp','تظهر أثناء شحن المحفظة','checkbox',false,true],['isActive','نشطة','checkbox',false,true]]
  }
};

const $ = selector => document.querySelector(selector);
const $$ = selector => [...document.querySelectorAll(selector)];
const escapeHtml = value => String(value ?? '').replace(/[&<>'"]/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[char]));
const number = value => new Intl.NumberFormat('ar-YE-u-nu-latn', { maximumFractionDigits: 2 }).format(Number(value || 0));
const date = value => value ? new Intl.DateTimeFormat('ar-YE-u-nu-latn', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)) : '—';
const badge = (text, tone = '') => `<span class="badge ${tone}">${escapeHtml(text)}</span>`;

async function execute(model, operation, data = {}) {
  const token = localStorage.getItem(ADMIN_TOKEN_KEY);
  const response = await fetch('/api/admin/execute', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {})
    },
    body: JSON.stringify({ model, operation, data })
  });
  const result = await response.json().catch(() => ({ success: false, message: 'استجابة غير صالحة من الخادم.' }));
  if (result.code === 'admin_authentication_required' || result.errorCode === 'admin_authentication_required') {
    showAdminLogin('انتهت جلسة الإدارة. سجل الدخول من جديد.');
  }
  if (!result.success) throw new Error(result.message || 'تعذر تنفيذ العملية.');
  return result;
}

async function seedDevelopmentData() {
  if (!window.confirm('سيتم إضافة بيانات تجريبية فقط دون حذف البيانات الحالية. هل تريد المتابعة؟')) return;
  const button = $('#development-seed-button');
  button.disabled = true;
  const token = localStorage.getItem(ADMIN_TOKEN_KEY);
  try {
    const response = await fetch('/api/admin/development/seed', {
      method: 'POST',
      headers: token ? { Authorization: `Bearer ${token}` } : {}
    });
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
  localStorage.removeItem(ADMIN_TOKEN_KEY);
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
  if (!result.success || !result.data?.accessToken) {
    throw new Error(result.message || 'بيانات حساب الإدارة غير صحيحة.');
  }
  localStorage.setItem(ADMIN_TOKEN_KEY, result.data.accessToken);
  return result;
}

function toast(message, error = false) {
  const item = document.createElement('div');
  item.className = `toast${error ? ' error' : ''}`;
  item.textContent = message;
  $('#toast-region').append(item);
  setTimeout(() => item.remove(), 4200);
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
  const rows = result.data.map(item => `<tr><td><span class="cell-main">${escapeHtml(item.displayName || 'بدون اسم')}</span><span class="cell-sub">${escapeHtml(item.id)}</span></td><td>${escapeHtml(item.phoneNumber)}</td><td>${escapeHtml(item.email || '—')}</td><td>${badge(roles[item.role] ?? item.role, item.role === 2 ? 'warning' : item.role === 1 ? 'info' : '')}</td><td>${escapeHtml(item.city || '—')}</td><td>${badge(item.isActive ? 'نشط' : 'موقوف', item.isActive ? 'success' : 'danger')}</td><td>${date(item.createdAtUtc)}</td></tr>`);
  toggleEmpty('#users-body', '#users-empty', rows);
}

async function loadDrivers() {
  const result = await execute('DriverModel', 'list', {});
  state.drivers = result.data;
  const rows = result.data.map(item => `<tr><td><span class="cell-main">${escapeHtml(item.displayName || 'بدون اسم')}</span><span class="cell-sub">${escapeHtml(item.userId)}</span></td><td>${escapeHtml(item.phoneNumber)}</td><td><span class="cell-main">${escapeHtml(item.vehicleModel)}</span></td><td>${escapeHtml(item.serviceKindNameAr || item.serviceKindId)} / ${escapeHtml(item.serviceNameAr || item.serviceCatalogItemId)}</td><td>${escapeHtml(item.plateNumber)}</td><td>${number(item.rating)}</td><td>${badge(item.isAvailable ? 'متاح' : 'غير متاح', item.isAvailable ? 'success' : '')}</td></tr>`);
  toggleEmpty('#drivers-body', '#drivers-empty', rows);
}

async function loadRides() {
  const value = $('#ride-filter').value;
  const result = await execute('RideModel', 'list', value ? { status: Number(value) } : {});
  state.rides = result.data;
  const rows = result.data.map(item => `<tr><td><span class="cell-main">${escapeHtml(item.customerName || item.customerId)}</span></td><td>${escapeHtml(item.driverName || 'لم يحدد')}</td><td><span class="cell-main">${escapeHtml(item.pickupDisplayName || item.pickupLabel || 'نقطة الانطلاق')}</span><span class="cell-sub">${escapeHtml(item.destinationDisplayName || item.destinationLabel || 'الوجهة')}</span></td><td>${escapeHtml(item.serviceKindNameAr || item.serviceKindId)} / ${escapeHtml(item.serviceNameAr || item.serviceCatalogItemId)}</td><td>${rideBadge(item.status)}</td><td>${number(item.customerPrice)} YER</td><td>${date(item.createdAtUtc)}</td></tr>`);
  toggleEmpty('#rides-body', '#rides-empty', rows);
}

async function loadCatalog() {
  const [catalog, kinds] = await Promise.all([execute('ServiceCatalogModel','list',{}), execute('ServiceKindModel','list',{})]);
  state.catalog = catalog.data; state.kinds = kinds.data;
  toggleEmpty('#catalog-body','#catalog-empty',catalog.data.map(item => `<tr><td>${item.imageUrl ? `<img class="table-icon" src="${escapeHtml(item.imageUrl)}" alt="">` : '—'}</td><td>${escapeHtml(item.code)}</td><td>${escapeHtml(item.nameAr)}</td><td>${escapeHtml(item.serviceKindNameAr || item.serviceKindId)}</td><td>${number(item.basePrice)}</td><td>${number(item.arrivalMinutes)} د</td><td>${badge(item.isActive ? 'نشطة' : 'متوقفة', item.isActive ? 'success' : 'danger')}</td></tr>`));
  toggleEmpty('#kinds-body','#kinds-empty',kinds.data.map(item => `<tr><td>${item.imageUrl ? `<img class="table-icon" src="${escapeHtml(item.imageUrl)}" alt="">` : '—'}</td><td>${escapeHtml(item.code)}</td><td>${escapeHtml(item.nameAr)}</td><td>${number(item.sortOrder)}</td><td>${item.isDefault ? badge('افتراضي','success') : (item.isActive ? `<button class="button small" onclick="setDefaultServiceKind(${Number(item.id)})">تعيين افتراضي</button>` : badge('متوقف','danger'))}</td></tr>`));
}

window.setDefaultServiceKind = async id => {
  const item = state.kinds.find(kind => Number(kind.id) === Number(id));
  if (!item || !item.isActive) return toast('لا يمكن تعيين نوع خدمة متوقف كافتراضي.', true);
  await execute('ServiceKindModel', 'update', {
    id: item.id, code: item.code, nameAr: item.nameAr, imageUrl: item.imageUrl,
    isActive: true, isDefault: true, sortOrder: item.sortOrder || 0
  });
  toast('تم تعيين النوع الافتراضي للعميل.');
  await loadCatalog();
};

async function loadPricing() {
  const result = await execute('PricingRuleModel','list',{}); state.pricing = result.data;
  toggleEmpty('#pricing-body','#pricing-empty',result.data.map(item => `<tr><td>${escapeHtml(item.serviceKindNameAr || item.serviceKindId)}</td><td>${escapeHtml(item.serviceNameAr || item.serviceCatalogItemId)}</td><td>${number(item.baseFare)}</td><td>${number(item.perKilometer)}</td><td>${number(item.perMinute)}</td><td>${number(item.serviceFee)}</td><td>${number((item.driverCommissionRate || 0) * 100)}%</td><td>${number(item.driverCommissionFixed || 0)}</td><td>${number(item.cancellationFee || 0)}</td><td>${badge(item.isActive ? 'نشطة' : 'متوقفة', item.isActive ? 'success' : 'danger')}</td></tr>`));
}

async function loadWallet() {
  const userId = $('#wallet-user-id').value.trim();
  if (!userId) return toast('أدخل معرف المستخدم أولاً.', true);
  const numericUserId = Number(userId);
  if (!Number.isInteger(numericUserId) || numericUserId <= 0) return toast('معرف المستخدم يجب أن يكون رقماً صحيحاً.', true);
  const result = await execute('WalletModel','get',{ userId: numericUserId });
  const wallet = result.data;
  $('#wallet-summary').classList.remove('hidden');
  $('#wallet-balance').textContent = number(wallet.balance);
  $('#wallet-currency').textContent = wallet.currency;
  $('#wallet-transactions-count').textContent = number(wallet.transactions.length);
  toggleEmpty('#wallet-body','#wallet-empty',wallet.transactions.map(item => `<tr><td>${badge(transactionTypes[item.type] ?? item.type, item.type === 0 ? 'success' : item.type === 1 ? 'danger' : 'info')}</td><td>${number(item.amount)}</td><td>${number(item.balanceAfter)}</td><td>${escapeHtml(item.description)}</td><td>${escapeHtml(item.externalReference || '—')}</td><td>${date(item.createdAtUtc)}</td></tr>`));
}

async function loadSettlements() {
  const result = await execute('DriverSettlementPaymentModel', 'list', {});
  state.settlements = result.data || [];
  const rows = state.settlements.map(item => `<tr><td><span class="cell-main">${escapeHtml(item.driverName || 'بدون اسم')}</span><span class="cell-sub">${escapeHtml(item.driverPhone || item.driverId)}</span></td><td>${escapeHtml(item.id)}</td><td>${number(item.amountDue)} YER</td><td>${number(item.amountPaid)} YER</td><td>${number(item.amountOutstanding)} YER</td><td>${escapeHtml(item.paymentReference || '—')}</td><td>${date(item.createdAtUtc)}</td><td>${item.isSettled ? badge('تم التحصيل','success') : `<button class="button small" onclick="openSettlementForm(${Number(item.id)}, ${Number(item.amountOutstanding)})">تسجيل تحصيل</button>`}</td></tr>`);
  toggleEmpty('#settlements-body', '#settlements-empty', rows);
}

async function loadPaymentMethods() {
  const result = await execute('PaymentMethodModel', 'list', {});
  state.paymentMethods = result.data || [];
  const kinds = ['محفظة خارجية','بطاقة','حساب/تحويل بنكي'];
  const rows = state.paymentMethods.map(item => {
    const image = item.imageUrl ? `<img class="payment-method-logo" src="${escapeHtml(item.imageUrl)}" alt="شعار ${escapeHtml(item.nameAr)}">` : '—';
    const visibility = [item.isAvailableForRidePayment ? 'دفع الرحلة' : '', item.isAvailableForWalletTopUp ? 'شحن المحفظة' : ''].filter(Boolean).join(' + ') || '—';
    return `<tr><td>${image}</td><td><span class="cell-main">${escapeHtml(item.nameAr)}</span><span class="cell-sub">${escapeHtml(item.descriptionAr)}</span></td><td>${escapeHtml(kinds[item.kind] || item.kind)}</td><td>${escapeHtml(item.providerCode)}</td><td>${escapeHtml(visibility)}</td><td>${number(item.sortOrder)}</td><td>${badge(item.isActive ? 'نشطة' : 'موقوفه', item.isActive ? 'success' : 'danger')}</td></tr>`;
  });
  toggleEmpty('#payment-methods-body', '#payment-methods-empty', rows);
}

async function loadLedgerAccounts() {
  const result = await execute('LedgerAccountModel', 'list', {});
  state.ledgerAccounts = result.data || [];
  const select = $('#statement-account');
  const current = select.value;
  select.innerHTML = `<option value="">كافة الحسابات</option>${state.ledgerAccounts.map(item => `<option value="${Number(item.id)}">${escapeHtml(item.code)} — ${escapeHtml(item.name)} (${escapeHtml(item.currency)})</option>`).join('')}`;
  select.value = [...select.options].some(option => option.value === current) ? current : '';
}

function syncStatementPeriodFields() {
  const mode = $('#statement-period').value;
  $$('.statement-period-field').forEach(field => field.classList.toggle('hidden', field.dataset.periodField !== mode));
}

function statementRequest() {
  const data = {};
  const accountId = $('#statement-account').value;
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
  const summaries = (data.accounts || []).map(item => `<tr><td><span class="cell-main">${escapeHtml(item.accountCode)} — ${escapeHtml(item.accountName)}</span><span class="cell-sub">${escapeHtml(item.accountType)}</span></td><td>${escapeHtml(item.currency)}</td><td>${number(item.openingBalance)}</td><td>${number(item.debit)}</td><td>${number(item.credit)}</td><td>${number(item.closingBalance)}</td></tr>`);
  toggleEmpty('#statement-summary-body', '#statement-empty', summaries);
  const rows = (data.lines || []).map(item => `<tr><td>${date(item.postedAtUtc)}</td><td><span class="cell-main">${escapeHtml(item.accountCode)} — ${escapeHtml(item.accountName)}</span><span class="cell-sub">${escapeHtml(item.currency)}</span></td><td>${escapeHtml(item.entryNumber)}</td><td>${escapeHtml(journalEntryTypes[item.entryType] || item.entryType)}</td><td>${escapeHtml(item.description)}</td><td>${escapeHtml(item.entryReference)}</td><td>${number(item.debit)}</td><td>${number(item.credit)}</td><td>${number(item.runningBalance)}</td></tr>`);
  toggleEmpty('#statement-lines-body', '#statement-empty', rows);
}

async function initializeAccounting() {
  if (!window.confirm('سيتم إنشاء الحسابات الأساسية المفقودة وربط العملاء والسائقين وأنواع الخدمة الموجودة بحساباتها المالية، دون إنشاء قيود أو تغيير أرصدة. هل تريد المتابعة؟')) return;
  const button = $('#accounting-setup-button');
  button.disabled = true;
  try {
    const result = await execute('AccountingSetupModel', 'add', {});
    toast(result.message || 'تمت تهيئة وربط الحسابات المالية.');
    await loadLedgerAccounts();
    await loadAccountStatement();
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
    if (view === 'drivers') await loadDrivers();
    if (view === 'rides') await loadRides();
    if (view === 'catalog') await loadCatalog();
    if (view === 'pricing') await loadPricing();
    if (view === 'payment-methods') await loadPaymentMethods();
    if (view === 'settlements') await loadSettlements();
    if (view === 'ledger') { await loadLedgerAccounts(); await loadAccountStatement(); }
    if (view === 'places') await loadPlaces();
    if (view === 'history') await loadHistory();
  } catch (error) { if (!quiet) toast(error.message, true); }
}

async function loadPlaces() {
  const result = await execute('SavedPlaceAdminModel', 'list', {}); state.places = result.data;
  toggleEmpty('#places-body', '#places-empty', (result.data || []).map(item => `<tr><td><span class="cell-main">${escapeHtml(item.userName || 'بدون اسم')}</span><span class="cell-sub">${escapeHtml(item.userId)}</span></td><td>${escapeHtml(item.label)}</td><td>${escapeHtml(item.kind || 'place')}</td><td>${escapeHtml(item.address)}</td><td>${number(item.latitude)}, ${number(item.longitude)}</td><td>${date(item.updatedAtUtc || item.createdAtUtc)}</td></tr>`));
}

async function loadHistory() {
  const result = await execute('RideModel', 'list', {}); state.history = result.data;
  toggleEmpty('#history-body', '#history-empty', (result.data || []).map(item => `<tr><td>${escapeHtml(item.customerName || item.customerId)}</td><td>${escapeHtml(item.pickupDisplayName || item.pickupLabel || 'نقطة الانطلاق')} ← ${escapeHtml(item.destinationDisplayName || item.destinationLabel || 'الوجهة')}</td><td>${rideBadge(item.status)}</td><td>${number(item.customerPrice)} YER</td><td>${date(item.createdAtUtc)}</td></tr>`));
}

function setView(view) {
  state.view = view;
  $$('.view').forEach(item => item.classList.toggle('active', item.id === `view-${view}`));
  $$('.nav-item').forEach(item => item.classList.toggle('active', item.dataset.view === view));
  $('#page-title').textContent = titles[view][0];
  $('#page-subtitle').textContent = titles[view][1];
  closeSidebar();
  loadView(view);
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
async function openForm(name) {
  const config = formConfigs[name]; activeForm = config;
  if (name === 'driver' && !state.users.length) {
    try { await loadUsers(); } catch { }
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
    config.fields = config.fields.map(field => {
      if (field[0] === 'userId' && field[4] === 'driverUsers') return [field[0], field[1], 'select', field[3], driverUsers];
      if (field[0] === 'serviceKindId') return [field[0], field[1], 'select', field[3], serviceKinds];
      if (field[0] === 'serviceCatalogItemId') return [field[0], field[1], 'select', field[3], serviceCatalogItems];
      return field;
    });
  }
  $('#modal-title').textContent = config.title; $('#modal-subtitle').textContent = config.subtitle;
  $('#modal-fields').innerHTML = config.fields.map(buildField).join('');
  $$('#modal-fields input[type="file"]').forEach(input => input.addEventListener('change', () => {
    const preview = $(`#modal-fields [data-preview-for="${input.name}"]`);
    if (!preview) return;
    const file = input.files?.[0];
    preview.innerHTML = file ? `<small>${escapeHtml(file.name)}</small><img class="form-image-preview" src="${URL.createObjectURL(file)}" alt="">` : '';
  }));
  const kindSelect = $('#modal-fields [name="serviceKindId"]');
  const catalogSelect = $('#modal-fields [name="serviceCatalogItemId"]');
  if (kindSelect && catalogSelect) {
    const allItems = (state.catalog || []).map(item => ({ id: String(item.id), name: item.nameAr || item.code, kindId: String(item.serviceKindId) }));
    const refreshCatalogOptions = () => {
      const selected = String(kindSelect.value || '');
      catalogSelect.replaceChildren(...allItems.filter(item => !selected || item.kindId === selected).map(item => {
        const option = document.createElement('option'); option.value = item.id; option.textContent = item.name; return option;
      }));
      catalogSelect.disabled = catalogSelect.options.length === 0;
    };
    kindSelect.addEventListener('change', refreshCatalogOptions);
    refreshCatalogOptions();
  }
  $('#modal-result').className = 'inline-result hidden';
  if (name === 'wallet' && $('#wallet-user-id').value.trim()) $('#modal-fields [name="userId"]').value = $('#wallet-user-id').value.trim();
  $('#form-modal').showModal();
}

function readForm(form, fields) {
  const data = {};
  for (const [name,,type] of fields) {
    const input = form.elements[name];
    if (type === 'file') continue;
    if (type === 'checkbox') data[name] = input.checked;
    else if (type === 'number') { if (input.value !== '') data[name] = Number(input.value); }
    else if (type === 'select' && /^\d+$/.test(input.value)) data[name] = Number(input.value);
    else if (input.value.trim() !== '') data[name] = input.value.trim();
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
    const image = form.elements.imageFile?.files?.[0];
    if (image) {
      const upload = new FormData(); upload.append('file', image);
      const uploaded = await fetch('/api/media/upload', { method: 'POST', body: upload }).then(response => response.json());
      if (!uploaded.success) throw new Error(uploaded.message || 'تعذر رفع الصورة.');
      data.imageUrl = uploaded.url;
    }
    const result = await execute(activeForm.model, activeForm.operation, data);
    resultBox.className = 'inline-result success';
    resultBox.innerHTML = `${escapeHtml(result.message)}${result.data?.imageUrl ? `<br><img class="form-image-preview" src="${escapeHtml(result.data.imageUrl)}" alt=""><br><small>${escapeHtml(result.data.imageUrl)}</small>` : ''}${activeForm === formConfigs.quote ? `<pre>${escapeHtml(JSON.stringify(result.data, null, 2))}</pre>` : ''}`;
    toast(result.message);
    if (activeForm.refresh) await loadView(activeForm.refresh === 'wallet' ? state.view : activeForm.refresh, true);
    if (activeForm !== formConfigs.quote) setTimeout(() => $('#form-modal').close(), 700);
  } catch (error) { resultBox.className = 'inline-result error'; resultBox.textContent = error.message; }
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

function bindEvents() {
  $$('.nav-item').forEach(button => button.addEventListener('click', () => setView(button.dataset.view)));
  $$('[data-go]').forEach(button => button.addEventListener('click', () => setView(button.dataset.go)));
  $$('[data-form]').forEach(button => button.addEventListener('click', () => openForm(button.dataset.form)));
  $$('[data-close]').forEach(button => button.addEventListener('click', () => document.getElementById(button.dataset.close).close()));
  $('#refresh-button').addEventListener('click', async () => { await loadDatabaseStatus(); await loadView(state.view); });
  $('#database-button').addEventListener('click', () => $('#database-modal').showModal());
  $('#menu-button').addEventListener('click', () => { $('#sidebar').classList.add('open'); $('#sidebar-backdrop').classList.add('open'); });
  $('#sidebar-backdrop').addEventListener('click', closeSidebar);
  $('#entity-form').addEventListener('submit', submitEntityForm);
  $('#ride-filter').addEventListener('change', loadRides);
  let searchTimer; $('#user-search').addEventListener('input', event => { clearTimeout(searchTimer); searchTimer = setTimeout(() => loadUsers(event.target.value.trim()).catch(error => toast(error.message, true)), 300); });
  $('#wallet-load').addEventListener('click', () => loadWallet().catch(error => toast(error.message, true)));
  $('#statement-load').addEventListener('click', () => loadAccountStatement().catch(error => toast(error.message, true)));
  $('#statement-period').addEventListener('change', syncStatementPeriodFields);
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
      await loadView('overview', true);
    } catch (error) {
      result.className = 'inline-result error';
      result.textContent = error.message;
    } finally { submit.disabled = false; }
  });
  $('#admin-logout-button').addEventListener('click', () => showAdminLogin('تم تسجيل الخروج.'));
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

async function start() {
  bindEvents();
  const settings = await loadDatabaseStatus();
  if (!settings?.configured) {
    showAdminApp();
    $('#database-modal').showModal();
    return;
  }

  const token = localStorage.getItem(ADMIN_TOKEN_KEY);
  if (!token) {
    showAdminLogin();
    return;
  }

  showAdminApp();
  try {
    await loadView('overview', true);
  } catch {
    showAdminLogin('تعذر التحقق من جلسة الإدارة.');
  }
}

start();
