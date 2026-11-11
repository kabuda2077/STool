// Uses an installed Playwright module; set STOOL_PLAYWRIGHT_MODULE to its package directory.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require(process.env.STOOL_PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../Modules/LanTransfer/Web');

test('phone transfer: keyboard, modal focus, polling and narrow layouts', async () => {
  const browser = await chromium.launch({ channel: 'chrome', headless: true });
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, reducedMotion: 'reduce' });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const transfer = { id: 'demo', name: '项目资料与设计说明.pdf', state: 'transferring', direction: 'toPhone', transferredBytes: 1024, totalBytes: 4096, bytesPerSecond: 1024 };
  await context.route('http://stool.test/**', route => {
    const url = new URL(route.request().url());
    if (url.pathname.startsWith('/api/')) {
      return route.fulfill({ json: url.pathname === '/api/transfers' ? { transfers: [transfer] } : {} });
    }
    const file = url.pathname === '/' ? 'index.html' : path.basename(url.pathname);
    return route.fulfill({ body: fs.readFileSync(path.join(root, file)), contentType: file.endsWith('.css') ? 'text/css' : file.endsWith('.js') ? 'text/javascript' : 'text/html' });
  });
  try {
    await page.goto('http://stool.test/');
    await page.locator('#workspace').waitFor({ state: 'visible' });
    await page.locator('#receiveTab').focus();
    await page.keyboard.press('ArrowRight');
    assert.equal(await page.locator('#sendTab').getAttribute('aria-selected'), 'true');
    await page.keyboard.press('Tab');
    // Activity actions precede the upload composer; the file input must still be keyboard reachable.
    await page.locator('#filePicker').focus();
    assert.equal(await page.evaluate(() => document.activeElement.id), 'filePicker');
    const chooser = page.waitForEvent('filechooser');
    await page.keyboard.press('Enter');
    await (await chooser).setFiles({ name: 'example.txt', mimeType: 'text/plain', buffer: Buffer.from('Example') });
    assert.match(await page.locator('#selectionSummary').innerText(), /1 个文件/);
    await page.locator('.task-action').first().focus();
    const focused = await page.locator('.task-action').first().elementHandle();
    await page.evaluate(async () => { await loadTransfers(); await loadTransfers(); });
    assert.equal(await focused.evaluate(node => node === document.activeElement && node.isConnected), true);
    assert.match(await page.locator('.transfer-task-state').innerText(), /从电脑接收/);
    await page.evaluate(() => renderActiveTransfers([{ id: 'demo', name: '项目资料.pdf', state: 'paused', direction: 'toPhone', transferredBytes: 1024, totalBytes: 4096 }]));
    assert.equal(await page.evaluate(() => document.activeElement.textContent), '继续');
    await page.evaluate(() => renderActiveTransfers([]));
    assert.equal(await page.evaluate(() => document.activeElement.id), 'sendTab');
    await page.evaluate(() => loadTransfers());
    await page.locator('#sendTab').focus();
    await page.evaluate(() => renderOffer({ id: 'offer', name: '示例项目资料.zip', fileCount: 3, totalBytes: 4096 }));
    assert.equal(await page.evaluate(() => document.activeElement.id), 'offerAccept');
    for (let i = 0; i < 5; i++) {
      await page.keyboard.press('Tab');
      assert.equal(await page.evaluate(() => document.querySelector('#offerDialog').contains(document.activeElement)), true);
    }
    await page.keyboard.press('Escape');
    await page.waitForFunction(() => !document.querySelector('#offerDialog').open);
    assert.equal(await page.evaluate(() => document.activeElement.id), 'sendTab');
    for (const width of [320, 390, 768, 1280]) {
      await page.setViewportSize({ width, height: 844 });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `overflow at ${width}`);
    }
    assert.equal(await page.locator('.loading-spinner').evaluate(node => getComputedStyle(node).animationName), 'none');
    await page.evaluate(() => showAuth('验证码无效，请重试'));
    assert.equal(await page.evaluate(() => document.activeElement.id), 'codeInput');
    assert.match(await page.locator('#authError').innerText(), /验证码无效/);
    await page.evaluate(() => { showWorkspace(); document.querySelector('#sendTab').click(); });
    if (process.env.STOOL_UI_ARTIFACTS) {
      fs.mkdirSync(process.env.STOOL_UI_ARTIFACTS, { recursive: true });
      await page.setViewportSize({ width: 390, height: 844 });
      await page.screenshot({ path: path.join(process.env.STOOL_UI_ARTIFACTS, 'phone-transfer.png'), fullPage: true });
      await page.evaluate(() => renderOffer({ id: 'capture', name: '项目资料.zip', fileCount: 3, totalBytes: 4096 }));
      await page.screenshot({ path: path.join(process.env.STOOL_UI_ARTIFACTS, 'phone-offer.png'), fullPage: true });
    }
    assert.deepEqual(errors, []);
  } finally { await browser.close(); }
});
