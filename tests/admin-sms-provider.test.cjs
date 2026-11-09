const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const { chromium } = require('C:/Users/ThinkPad/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root = path.resolve(__dirname, '../src/YemenDrive.Api/wwwroot');
const out = path.resolve(__dirname, '../../output/sms-provider-delivery-20261108/verification');
fs.mkdirSync(out, { recursive: true });
let saved = { url: '', username: '', deviceNumber: '', hasPassword: false, updatedAtUtc: null };
let posts = 0;
let failSave = false;
let checks = 0;
let connectionState = null;
let activationPosts = 0;
function check(condition, message) { assert.ok(condition, message); checks++; }
const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  function json(data, status = 200) { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(data)); }
  if (url.pathname === '/api/setup/database') return json({ configured: true, provider: 'SqlServer', server: '(localdb)', database: 'قاعدة اختبار', providers: [] });
  if (url.pathname === '/api/admin/sms-provider/connection' || url.pathname === '/api/admin/sms-provider/connect') return json({ success: true, data: connectionState });
  if (url.pathname === '/api/admin/sms-provider/activate') {
    let body = ''; for await (const chunk of req) body += chunk;
    activationPosts++;
    const code = JSON.parse(body).code;
    await new Promise(resolve => setTimeout(resolve, 150));
    connectionState = code === '724245' ? { state: 'connected', message: 'تم تسجيل الدخول إلى مزود الرسائل بنجاح.', connected: true }
      : { state: 'activation_required', message: 'رمز التفعيل غير صحيح.', requiresActivation: true };
    return json({ success: true, data: connectionState });
  }
  if (url.pathname === '/api/admin/sms-provider') {
    if (req.method === 'POST') {
      let body = ''; for await (const chunk of req) body += chunk;
      const data = JSON.parse(body); posts++;
      await new Promise(resolve => setTimeout(resolve, 150));
      if (failSave) return json({ success: false, message: 'تعذر حفظ بيانات التجربة.' }, 503);
      saved = { url: data.url.trim(), username: data.username.trim(), deviceNumber: data.deviceNumber.trim(), hasPassword: Boolean(data.password || saved.hasPassword), updatedAtUtc: new Date().toISOString() };
      return json({ success: true, data: saved, message: 'تم حفظ بيانات مزود الرسائل النصية.' });
    }
    return json({ success: true, data: { ...saved, connection: connectionState } });
  }
  if (url.pathname.startsWith('/api/')) return json({ success: true, data: [] });
  const target = path.resolve(root, '.' + (url.pathname === '/admin' ? '/admin.html' : url.pathname));
  if (!target.startsWith(root + path.sep) || !fs.existsSync(target)) { res.writeHead(404); return res.end(); }
  const type = { '.html': 'text/html; charset=utf-8', '.js': 'application/javascript', '.css': 'text/css', '.png': 'image/png' }[path.extname(target)] || 'application/octet-stream';
  res.writeHead(200, { 'Content-Type': type }); fs.createReadStream(target).pipe(res);
});
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${server.address().port}/admin`);
    await page.waitForLoadState('networkidle');
    await page.evaluate(async () => {
      administratorPermissions = new Set(['*']); currentAdministratorId = 1; state.databaseConfigured = true;
      showAdminApp(); await setView('sms-provider');
    });
    const form = page.locator('#sms-provider-form');
    await page.waitForFunction(() => !document.querySelector('#sms-provider-fields').disabled);
    check(await form.locator('input').count() === 4, 'four fields are present');
    check(await form.locator('[name=deviceNumber]').getAttribute('type') === 'text', 'device is a text field');
    check(await form.locator('[name=password]').getAttribute('type') === 'password', 'password is masked');
    await form.locator('[name=url]').fill('https://example.test/sms');
    await form.locator('[name=username]').fill('مزود تجريبي');
    await form.locator('[name=password]').fill('synthetic-secret');
    await form.locator('[name=deviceNumber]').fill('00012');
    await form.locator('[type=submit]').click();
    await page.evaluate(() => document.querySelector('#sms-provider-form').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })));
    await page.waitForFunction(() => document.querySelector('#sms-provider-result').textContent.includes('تم حفظ'));
    check(posts === 1, 'duplicate submissions are blocked');
    check(await form.locator('[name=password]').inputValue() === '', 'password is cleared after saving');
    check(await form.locator('[name=deviceNumber]').inputValue() === '00012', 'device leading zeros survive');
    check(!await form.locator('[name=password]').evaluate(node => node.required), 'saved password may be retained');
    await page.evaluate(() => loadSmsProviderSettings());
    check(await form.locator('[name=username]').inputValue() === 'مزود تجريبي', 'reload restores saved fields');
    await page.screenshot({ path: path.join(out, 'sms-provider-desktop.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForFunction(() => document.querySelector('#sidebar').getBoundingClientRect().left >= window.innerWidth);
    await page.screenshot({ path: path.join(out, 'sms-provider-mobile.png'), fullPage: true });
    check(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'mobile screen has no horizontal overflow');
    failSave = true;
    await form.locator('[name=password]').fill('synthetic-replacement');
    await form.locator('[type=submit]').click();
    await page.waitForFunction(() => document.querySelector('#sms-provider-result').classList.contains('error'));
    check(await form.locator('[name=password]').inputValue() === '' && await form.locator('[type=submit]').isEnabled(), 'failed submission clears password and permits retry');
    connectionState = { state: 'activation_required', message: 'أدخل رمز التفعيل.', requiresActivation: true };
    await page.evaluate(() => loadSmsProviderSettings());
    const activation = page.locator('#sms-activation-modal');
    check(await activation.isVisible(), 'activation popup opens automatically for Type 4');
    await activation.locator('[name=code]').fill('111111');
    await activation.locator('[type=submit]').click();
    await page.waitForFunction(() => document.querySelector('#sms-activation-result').textContent.includes('غير صحيح'));
    check(await activation.isVisible() && await activation.locator('[name=code]').inputValue() === '', 'incorrect code remains retryable and clears input');
    await page.screenshot({ path: path.join(out, 'sms-activation-mobile.png'), fullPage: true });
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.screenshot({ path: path.join(out, 'sms-activation-desktop.png'), fullPage: true });
    await activation.locator('[name=code]').fill('724245');
    await page.evaluate(() => { const form = document.querySelector('#sms-activation-form'); form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); });
    await page.waitForFunction(() => !document.querySelector('#sms-activation-modal').open);
    check(activationPosts === 2 && (await page.locator('#sms-provider-connection').textContent()).includes('بنجاح'), 'activation blocks duplicate submissions and shows connected state');
    connectionState = { state: 'activation_required', message: 'أدخل رمز التفعيل.', requiresActivation: true };
    await page.locator('#sms-provider-connect').click();
    await page.waitForFunction(() => document.querySelector('#sms-activation-modal').open);
    check(await activation.isVisible(), 'reconnect can reopen dismissed activation');
    await activation.locator('[data-close]').first().click();
    await page.evaluate(async () => { administratorPermissions = new Set(['policy.read']); await loadSmsProviderSettings(); });
    check(await form.locator('[type=submit]').isDisabled(), 'read permission disables editing');
    check(!await activation.isVisible() && await page.locator('#sms-provider-connect').isDisabled(), 'read-only administrator cannot activate or reconnect');
    await page.evaluate(() => clearAdminSession());
    check(await form.locator('[name=username]').inputValue() === '' && await form.locator('[name=deviceNumber]').inputValue() === '', 'logout clears provider form');
    check(errors.length === 0, 'no browser JavaScript errors: ' + errors.join('; '));
    fs.writeFileSync(path.join(out, 'browser-checks.json'), JSON.stringify({ checks, posts, errors }, null, 2));
    console.log(`PASS: ${checks} SMS provider browser checks; mocked API, desktop and mobile screenshots.`);
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; server.close(); });
