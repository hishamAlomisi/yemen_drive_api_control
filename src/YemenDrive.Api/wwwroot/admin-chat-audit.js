// Administration is read-only: reviewing a transcript never acknowledges it.
const chatAudit = { generation: 0, initialized: false, loading: false, pair: null,
  rides: new Map(), nextBeforeRideId: null, messageLoads: new Set() };

function chatAuditDate(value) {
  const parsed = value ? new Date(value) : null;
  return parsed && Number.isFinite(parsed.getTime()) ? new Intl.DateTimeFormat('ar-YE-u-nu-latn', {
    dateStyle: 'medium', timeStyle: 'medium', timeZone: 'Asia/Aden'
  }).format(parsed) : '—';
}

function chatAuditFeedback(message, error = false) {
  const node = $('#chat-audit-feedback');
  node.textContent = message;
  node.classList.toggle('chat-audit-error', error);
}

function resetChatAudit(clearLists = false) {
  chatAudit.generation++;
  chatAudit.loading = false;
  chatAudit.pair = null;
  chatAudit.rides.clear();
  chatAudit.messageLoads.clear();
  chatAudit.nextBeforeRideId = null;
  $('#chat-audit-results').innerHTML = '';
  $('#chat-audit-more').classList.add('hidden');
  $('#chat-audit-more').disabled = false;
  $('#chat-audit-show').disabled = !$('#chat-audit-driver').value || !$('#chat-audit-customer').value;
  if (clearLists === true) {
    chatAudit.initialized = false;
    $('#chat-audit-driver').innerHTML = chatAuditOptions([], 'اختر السائق');
    $('#chat-audit-driver').disabled = false;
    $('#chat-audit-customer').innerHTML = chatAuditOptions([], 'اختر السائق أولًا');
    $('#chat-audit-customer').disabled = true;
    $('#chat-audit-show').disabled = true;
  }
  chatAuditFeedback('اختر الطرفين ثم اضغط عرض المحادثات.');
}

function chatAuditOptions(rows, placeholder) {
  return `<option value="">${escapeHtml(placeholder)}</option>` + rows.map(row =>
    `<option value="${Number(row.id)}">${escapeHtml(row.displayName || 'بدون اسم')}${row.phoneNumber ? ' · ' + escapeHtml(row.phoneNumber) : ''} · ${Number(row.conversationCount) || 0} رحلة</option>`).join('');
}

async function loadChatAudit(refresh = false) {
  if (!canAdmin('operations.read')) {
    resetChatAudit();
    chatAudit.initialized = false;
    $('#chat-audit-driver').innerHTML = chatAuditOptions([], 'غير مفوض');
    $('#chat-audit-customer').innerHTML = chatAuditOptions([], 'غير مفوض');
    $('#chat-audit-show').disabled = true;
    chatAuditFeedback('عرض سجلات المحادثات يتطلب صلاحية متابعة العمليات.', true);
    return;
  }
  if (chatAudit.initialized && !refresh) return;
  resetChatAudit();
  chatAudit.initialized = false;
  const generation = chatAudit.generation;
  $('#chat-audit-driver').disabled = true;
  $('#chat-audit-driver').innerHTML = chatAuditOptions([], 'جارٍ تحميل السائقين…');
  $('#chat-audit-customer').disabled = true;
  $('#chat-audit-customer').innerHTML = chatAuditOptions([], 'اختر السائق أولًا');
  $('#chat-audit-show').disabled = true;
  chatAuditFeedback('جارٍ تحميل السائقين الذين لديهم محادثات…');
  try {
    const result = await execute('RideChatAuditModel', 'list', {});
    if (generation !== chatAudit.generation) return;
    const rows = result.data || [];
    $('#chat-audit-driver').innerHTML = chatAuditOptions(rows, rows.length ? 'اختر السائق' : 'لا توجد محادثات');
    chatAudit.initialized = true;
    chatAuditFeedback(rows.length ? 'اختر السائق لعرض الركاب الذين تحدثوا معه.' : 'لا توجد محادثات مسجلة حتى الآن.');
  } catch (error) {
    if (generation !== chatAudit.generation) return;
    chatAudit.initialized = false;
    $('#chat-audit-driver').innerHTML = chatAuditOptions([], 'تعذر تحميل السائقين');
    chatAuditFeedback(error.message, true);
  } finally {
    if (generation === chatAudit.generation) $('#chat-audit-driver').disabled = false;
  }
}

async function loadChatAuditCustomers() {
  resetChatAudit();
  const generation = chatAudit.generation;
  const driverId = Number($('#chat-audit-driver').value);
  $('#chat-audit-customer').disabled = true;
  $('#chat-audit-customer').innerHTML = chatAuditOptions([], driverId ? 'جارٍ تحميل الركاب…' : 'اختر السائق أولًا');
  $('#chat-audit-show').disabled = true;
  if (!driverId) return;
  chatAuditFeedback('جارٍ تحميل الركاب الذين تحدثوا مع هذا السائق…');
  try {
    const result = await execute('RideChatAuditModel', 'search', { driverId });
    if (generation !== chatAudit.generation) return;
    const rows = result.data || [];
    $('#chat-audit-customer').innerHTML = chatAuditOptions(rows, rows.length ? 'اختر الراكب' : 'لا توجد محادثات');
    $('#chat-audit-customer').disabled = rows.length === 0;
    chatAuditFeedback(rows.length ? 'اختر الراكب ثم اضغط عرض المحادثات.' : 'لا توجد محادثات لهذا السائق.');
  } catch (error) {
    if (generation !== chatAudit.generation) return;
    $('#chat-audit-customer').innerHTML = chatAuditOptions([], 'تعذر تحميل الركاب');
    chatAuditFeedback(error.message, true);
  }
}

function chatAuditMessage(message) {
  const role = message.senderRole === 'driver' ? 'driver' : 'customer';
  const roleName = role === 'driver' ? 'السائق' : 'الراكب';
  const status = message.readAtUtc ? 'مقروءة' : message.deliveredAtUtc ? 'تم الوصول' : 'مرسلة';
  const receipt = message.readAtUtc ? `القراءة: ${chatAuditDate(message.readAtUtc)}` :
    message.deliveredAtUtc ? `الوصول: ${chatAuditDate(message.deliveredAtUtc)}` : 'بانتظار تأكيد وصولها إلى الطرف الآخر';
  return `<li class="chat-audit-message ${role}" data-message-id="${Number(message.id)}">
    <div class="chat-audit-message-heading"><strong>${roleName}</strong><span>الترتيب ${Number(message.sequence) || Number(message.id)}</span><time>${escapeHtml(chatAuditDate(message.createdAtUtc))}</time></div>
    <p dir="auto">${escapeHtml(message.content || '')}</p>
    <div class="chat-audit-receipt${message.readAtUtc ? ' read' : ''}" title="${escapeHtml(receipt)}"><span aria-hidden="true">${message.deliveredAtUtc || message.readAtUtc ? '✓✓' : '✓'}</span> ${status}<span>${escapeHtml(receipt)}</span></div>
  </li>`;
}

function renderChatAuditRide(ride) {
  const id = Number(ride.rideId);
  let section = document.getElementById(`chat-audit-ride-${id}`);
  if (!section) {
    section = document.createElement('section');
    section.id = `chat-audit-ride-${id}`;
    section.className = 'chat-audit-ride';
    $('#chat-audit-results').appendChild(section);
  }
  const messages = [...ride.messages].sort((a, b) => Number(a.sequence) - Number(b.sequence) ||
    new Date(a.createdAtUtc) - new Date(b.createdAtUtc) || Number(a.id) - Number(b.id));
  section.innerHTML = `<header><div><h3>رحلة #${id}</h3><p>${escapeHtml(ride.pickupLabel || 'نقطة الانطلاق')} ← ${escapeHtml(ride.destinationLabel || 'الوجهة')}</p><p>السائق: ${escapeHtml(ride.driverName || '—')} · الراكب: ${escapeHtml(ride.customerName || '—')}</p></div><div class="chat-audit-ride-meta">${rideBadge(ride.status)}<span>${escapeHtml(chatAuditDate(ride.createdAtUtc))}</span><span>${messages.length} من ${Number(ride.messageCount) || messages.length} رسالة</span></div></header>
    ${ride.hasMoreMessages ? `<button class="button chat-audit-older" type="button" data-chat-older="${id}" ${chatAudit.messageLoads.has(id) ? 'disabled' : ''}>${chatAudit.messageLoads.has(id) ? 'جارٍ التحميل…' : 'عرض رسائل أقدم لهذه الرحلة'}</button>` : ''}
    <ol class="chat-audit-messages" aria-label="رسائل الرحلة ${id}">${messages.map(chatAuditMessage).join('')}</ol>`;
}

async function showChatAudit(append = false) {
  if (chatAudit.loading) return;
  if (!append) {
    resetChatAudit();
    const driverId = Number($('#chat-audit-driver').value), customerId = Number($('#chat-audit-customer').value);
    if (!driverId || !customerId) { chatAuditFeedback('اختر السائق والراكب أولًا.', true); return; }
    chatAudit.pair = { driverId, customerId };
  }
  if (!chatAudit.pair || append && !chatAudit.nextBeforeRideId) return;
  const generation = chatAudit.generation;
  chatAudit.loading = true;
  $('#chat-audit-show').disabled = true;
  $('#chat-audit-more').disabled = true;
  chatAuditFeedback('جارٍ تحميل المحادثات…');
  try {
    const result = await execute('RideChatAuditModel', 'report', { ...chatAudit.pair, pageSize: 20,
      ...(append ? { beforeRideId: chatAudit.nextBeforeRideId } : {}) });
    if (generation !== chatAudit.generation) return;
    const data = result.data;
    for (const ride of data.rides || []) {
      chatAudit.rides.set(Number(ride.rideId), ride);
      renderChatAuditRide(ride);
    }
    chatAudit.nextBeforeRideId = data.hasMore ? data.nextBeforeRideId : null;
    $('#chat-audit-more').classList.toggle('hidden', !chatAudit.nextBeforeRideId);
    chatAuditFeedback(chatAudit.rides.size ? `المعروض ${chatAudit.rides.size} رحلة. أحدث الرحلات أولًا، والرسائل مرتبة زمنيًا داخل الرحلة.` : 'لا توجد محادثات بين الطرفين.');
  } catch (error) {
    if (generation === chatAudit.generation) chatAuditFeedback(error.message, true);
  } finally {
    if (generation === chatAudit.generation) {
      chatAudit.loading = false;
      $('#chat-audit-show').disabled = false;
      $('#chat-audit-more').disabled = false;
    }
  }
}

async function loadOlderChatAuditMessages(rideId) {
  const ride = chatAudit.rides.get(rideId);
  if (!ride?.hasMoreMessages || !chatAudit.pair || chatAudit.messageLoads.has(rideId)) return;
  const generation = chatAudit.generation;
  chatAudit.messageLoads.add(rideId);
  renderChatAuditRide(ride);
  try {
    const result = await execute('RideChatAuditModel', 'get', { ...chatAudit.pair, rideId, pageSize: 50,
      beforeId: ride.nextBeforeId, beforeCreatedAtUtc: ride.nextBeforeCreatedAtUtc });
    if (generation !== chatAudit.generation) return;
    const byId = new Map(ride.messages.map(message => [message.id, message]));
    for (const message of result.data.messages || []) byId.set(message.id, message);
    Object.assign(ride, result.data, { messages: [...byId.values()] });
  } catch (error) {
    if (generation === chatAudit.generation) chatAuditFeedback(error.message, true);
  } finally {
    if (generation === chatAudit.generation) {
      chatAudit.messageLoads.delete(rideId);
      renderChatAuditRide(ride);
    }
  }
}

$('#chat-audit-refresh').addEventListener('click', () => void loadChatAudit(true));
$('#chat-audit-driver').addEventListener('change', () => void loadChatAuditCustomers());
$('#chat-audit-customer').addEventListener('change', resetChatAudit);
$('#chat-audit-form').addEventListener('submit', event => { event.preventDefault(); void showChatAudit(); });
$('#chat-audit-more').addEventListener('click', () => void showChatAudit(true));
$('#chat-audit-results').addEventListener('click', event => {
  const button = event.target.closest('[data-chat-older]');
  if (button) void loadOlderChatAuditMessages(Number(button.dataset.chatOlder));
});
