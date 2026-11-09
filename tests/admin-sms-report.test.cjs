const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const { chromium } = require('C:/Users/ThinkPad/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root = path.resolve(__dirname, '../src/YemenDrive.Api/wwwroot');
const out = path.resolve(__dirname, '../../output/sms-manual-send-20261108/verification');
fs.mkdirSync(out, { recursive: true });
let data = { signInCount: 0, passwordResetCount: 0, registrationCount: 0, totalCount: 0, lastSentAtUtc: null };
let fail = false, delay = 0, checks = 0;
function check(condition, message) { assert.ok(condition, message); checks++; }
const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  function json(value, status = 200) { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); }
  if (url.pathname === '/api/setup/database') return json({ configured: true, server: '(localdb)', database: 'قاعدة اختبار', providers: [] });
  if (url.pathname === '/api/admin/sms-statistics') {
    if (delay) await new Promise(resolve => setTimeout(resolve, delay));
    return fail ? json({ success: false, message: 'تعذر تحميل تقرير التجربة.' }, 503) : json({ success: true, data });
  }
  if (url.pathname.startsWith('/api/')) return json({ success: true, data: [] });
  const target = path.resolve(root, '.' + (url.pathname === '/admin' ? '/admin.html' : url.pathname));
  if (!target.startsWith(root + path.sep) || !fs.existsSync(target)) { res.writeHead(404); return res.end(); }
  res.writeHead(200, { 'Content-Type': { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'application/javascript', '.png': 'image/png' }[path.extname(target)] || 'application/octet-stream' });
  fs.createReadStream(target).pipe(res);
});
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${server.address().port}/admin`); await page.waitForLoadState('networkidle');
    await page.evaluate(async () => { administratorPermissions = new Set(['*']); currentAdministratorId = 1; state.databaseConfigured = true; showAdminApp(); await setView('sms-report'); });
    check(await page.locator('#view-sms-report .stat').count() === 7, 'OTP categories, wallet, manual and total are visible');
    check(await page.locator('#sms-report-total').textContent() === await page.evaluate(() => number(0)), 'zero is displayed for an empty report');
    check((await page.locator('#sms-report-result').textContent()).includes('لا توجد'), 'empty report is explained');
    data = { signInCount: 3, passwordResetCount: 2, registrationCount: 4, walletMovementCount: 5, otherOtpCount: 1, manualCount: 6, totalCount: 21, lastSentAtUtc: '2026-11-08T10:00:00Z' };
    await page.locator('#sms-report-refresh').click();
    await page.waitForFunction(() => document.querySelector('#sms-report-total').textContent === number(21));
    for (const [field, id] of [['signInCount', 'sign-in'], ['passwordResetCount', 'password-reset'], ['registrationCount', 'registration'], ['walletMovementCount', 'wallet'], ['otherOtpCount', 'other-otp'], ['manualCount', 'manual'], ['totalCount', 'total']])
      check(await page.locator('#sms-report-' + id).textContent() === await page.evaluate(value => number(value), data[field]), field + ' is mapped correctly');
    await page.screenshot({ path: path.join(out, 'sms-report-desktop.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForFunction(() => document.querySelector('#sidebar').getBoundingClientRect().left >= window.innerWidth);
    await page.screenshot({ path: path.join(out, 'sms-report-mobile.png'), fullPage: true });
    check(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'mobile report has no horizontal overflow');
    fail = true; await page.locator('#sms-report-refresh').click();
    await page.waitForFunction(() => document.querySelector('#sms-report-result').classList.contains('error'));
    check(await page.locator('#sms-report-total').textContent() === '—' && await page.locator('#sms-report-refresh').isEnabled(), 'failed request clears stale counts and permits retry');
    fail = false;
    await page.evaluate(async () => { administratorPermissions = new Set(['policy.read']); await loadSmsReport(); });
    check((await page.locator('#sms-report-result').textContent()).includes('صلاحية') && await page.locator('#sms-report-total').textContent() === '—', 'permissions block report data');
    delay = 300;
    await page.evaluate(() => { administratorPermissions = new Set(['*']); void loadSmsReport(); clearAdminSession(); });
    await page.waitForFunction(() => document.querySelector('#sms-report-refresh').disabled === false);
    await page.waitForLoadState('networkidle');
    check(await page.locator('#sms-report-total').textContent() === '—', 'late response cannot restore report after logout');
    check(errors.length === 0, 'no JavaScript errors: ' + errors.join('; '));
    fs.writeFileSync(path.join(out, 'browser-checks.json'), JSON.stringify({ checks, errors }, null, 2));
    console.log(`PASS: ${checks} SMS report browser checks; mocked API and desktop/mobile layouts.`);
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; server.close(); });
