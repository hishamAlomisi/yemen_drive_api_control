const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const { chromium } = require('C:/Users/ThinkPad/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root = path.resolve(__dirname, '../src/YemenDrive.Api/wwwroot');
const out = path.resolve(__dirname, '../../output/sms-manual-send-20261108/verification');
fs.mkdirSync(out, { recursive: true });
let connected = true, mode = 'success', posts = [], checks = 0;
const outcomes = new Map();
function check(value, message) { assert.ok(value, message); checks++; }
const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  function json(data, status = 200) { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(data)); }
  if (url.pathname === '/api/setup/database') return json({ configured: true, server: '(localdb)', database: 'اختبار واجهة', providers: [] });
  if (url.pathname === '/api/admin/sms-send') {
    if (req.method === 'GET') return json({ success: true, data: { connected, state: connected ? 'connected' : 'activation_required', message: connected ? 'تم تسجيل الدخول إلى مزود الرسائل بنجاح.' : 'المزود يحتاج تفعيل الحساب.' } });
    let body = ''; for await (const chunk of req) body += chunk;
    const data = JSON.parse(body); posts.push(data);
    await new Promise(resolve => setTimeout(resolve, 180));
    if (data.numbers.some(number => number.includes('bad'))) return json({ success: false, message: 'يوجد رقم غير صالح.' }, 400);
    const numbers = [...new Set(data.numbers.map(x => x.replace(/\D/g, '').replace(/^967/, '')))];
    let result = outcomes.get(data.requestKey);
    if (!result) {
      result = { messageId: 1000000000000 + outcomes.size, recipientCount: numbers.length, accepted: mode !== 'reject' && mode !== 'uncertain', uncertain: mode === 'uncertain', state: mode === 'reject' ? 'rejected' : mode === 'uncertain' ? 'unconfirmed' : 'sent', message: mode === 'reject' ? 'لم يقبل المزود الرسالة.' : mode === 'uncertain' ? 'نتيجة الإرسال غير مؤكدة.' : `قبل المزود إرسال الرسالة إلى ${numbers.length} رقمًا.` };
      outcomes.set(data.requestKey, result);
    }
    if (mode === 'network') { mode = 'success'; return json({ success: false, message: 'تعذر قراءة النتيجة.' }, 503); }
    return json({ success: true, data: result });
  }
  if (url.pathname.startsWith('/api/')) return json({ success: true, data: [] });
  const target = path.resolve(root, '.' + (url.pathname === '/admin' ? '/admin.html' : url.pathname));
  if (!target.startsWith(root + path.sep) || !fs.existsSync(target)) { res.writeHead(404); return res.end(); }
  res.writeHead(200, { 'Content-Type': { '.html': 'text/html; charset=utf-8', '.js': 'application/javascript', '.css': 'text/css', '.png': 'image/png' }[path.extname(target)] || 'application/octet-stream' });
  fs.createReadStream(target).pipe(res);
});
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    await page.addInitScript(() => Object.defineProperty(Crypto.prototype, 'randomUUID', { value: undefined, configurable: true }));
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${server.address().port}/admin`); await page.waitForLoadState('networkidle');
    await page.evaluate(async () => { administratorPermissions = new Set(['*']); currentAdministratorId = 1; state.databaseConfigured = true; showAdminApp(); setView('sms-send'); });
    const form = page.locator('#sms-send-form');
    await page.waitForFunction(() => !document.querySelector('#sms-send-fields').disabled);
    check(await page.locator('[data-view=sms-send]').isVisible() && await form.locator('[type=submit]').isEnabled(), 'screen appears with existing provider connection');
    check((await page.locator('#sms-send-connection').textContent()).includes('بنجاح'), 'provider connection displayed');
    await form.locator('[name=numbers]').fill('+967 775000001\n775000002،775000001');
    await form.locator('[name=text]').fill('رسالة تجريبية من لوحة التحكم');
    check(await page.locator('#sms-send-text-count').textContent() === `${'رسالة تجريبية من لوحة التحكم'.length} / 1600 حرف`, 'message character count displayed');
    await page.screenshot({ path: path.join(out, 'sms-send-desktop.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForFunction(() => document.querySelector('#sidebar').getBoundingClientRect().left >= window.innerWidth);
    await page.screenshot({ path: path.join(out, 'sms-send-mobile.png'), fullPage: true });
    check(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'mobile layout has no horizontal overflow');
    await page.evaluate(() => { const form = document.querySelector('#sms-send-form'); form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); });
    await page.waitForFunction(() => document.querySelector('#sms-send-result').textContent.includes('معرف الرسالة'));
    check(posts.length === 1 && posts[0].numbers.length === 3 && posts[0].numbers[1] === '775000002', 'mixed separators parsed and double click sends one request');
    check(/^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/.test(posts[0].requestKey), 'request ID fallback works on HTTP without randomUUID');
    check((await page.locator('#sms-send-result').textContent()).includes('2 رقمًا') && !(await page.locator('#sms-send-result').getAttribute('class')).includes('error'), 'accepted recipient count and ID displayed');
    check(await form.locator('[name=text]').inputValue() === '', 'accepted text cleared to prevent accidental repeat');
    await form.locator('[name=numbers]').fill('775000003'); await form.locator('[name=text]').fill('رسالة لرقم واحد');
    await form.locator('[type=submit]').click();
    await page.waitForFunction(() => document.querySelector('#sms-send-result').textContent.includes('1 رقمًا'));
    check(posts.at(-1).numbers.length === 1, 'single phone number supported');
    const beforeInvalid = posts.length;
    await form.locator('[name=numbers]').fill(Array(101).fill('775000001').join('\n')); await form.locator('[name=text]').fill('عدد زائد');
    await form.locator('[type=submit]').click();
    check(posts.length === beforeInvalid && (await page.locator('#sms-send-result').textContent()).includes('100'), 'recipient limit validated before sending');
    await form.locator('[name=numbers]').fill('bad-number'); await form.locator('[name=text]').fill('رقم غير صحيح');
    await form.locator('[type=submit]').click();
    await page.waitForFunction(() => document.querySelector('#sms-send-result').textContent.includes('غير صالح'));
    check(await form.locator('[name=text]').inputValue() === 'رقم غير صحيح', 'server validation error preserves message');
    await form.locator('[name=numbers]').fill('775000004'); await form.locator('[name=text]').fill('اختبار نتيجة الطلب');
    mode = 'network'; await form.locator('[type=submit]').click();
    await page.waitForFunction(() => document.querySelector('#sms-send-submit').textContent.includes('تحقق'));
    const uncertainRequest = posts.at(-1).requestKey;
    await form.locator('[type=submit]').click();
    await page.waitForFunction(() => !document.querySelector('#sms-send-result').classList.contains('error'));
    check(posts.at(-1).requestKey === uncertainRequest, 'result retry preserves request ID to avoid duplicate SMS');
    mode = 'reject'; await form.locator('[name=text]').fill('رسالة مرفوضة'); await form.locator('[type=submit]').click();
    await page.waitForFunction(() => document.querySelector('#sms-send-submit').textContent.includes('إعادة'));
    check((await page.locator('#sms-send-result').textContent()).includes('لم يقبل') && await form.locator('[name=text]').inputValue() === 'رسالة مرفوضة', 'provider rejection is shown and remains editable');
    connected = false; await page.locator('#sms-send-refresh').click();
    await page.waitForFunction(() => document.querySelector('#sms-send-connection').textContent.includes('تفعيل'));
    check((await page.locator('#sms-send-connection').textContent()).includes('تفعيل') && await form.locator('[type=submit]').isDisabled(), 'disconnected provider prevents sending');
    connected = true;
    await page.evaluate(async () => { administratorPermissions = new Set(['operations.read']); await loadSmsSend(); });
    check(await form.locator('[type=submit]').isDisabled() && (await page.locator('#sms-send-connection').textContent()).includes('صلاحية'), 'read-only administrator cannot send');
    await page.evaluate(async () => { administratorPermissions = new Set(['*']); await loadSmsSend(); });
    await form.locator('[name=numbers]').fill('775000005'); await form.locator('[name=text]').fill('بيانات جلسة سابقة');
    mode = 'success';
    await page.evaluate(() => { document.querySelector('#sms-send-form').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); clearAdminSession(); });
    await page.waitForFunction(() => !smsSendBusy);
    check(await form.locator('[name=numbers]').inputValue() === '' && await form.locator('[name=text]').inputValue() === '' && await page.locator('#sms-send-result').textContent() === '', 'logout clears inputs and ignores late send response');
    check(errors.length === 0, 'no browser JavaScript errors: ' + errors.join('; '));
    fs.writeFileSync(path.join(out, 'sms-send-browser-checks.json'), JSON.stringify({ checks, posts: posts.length, errors }, null, 2));
    console.log(`PASS: ${checks} manual SMS browser checks; mocked provider only, desktop/mobile screenshots.`);
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; server.close(); });
