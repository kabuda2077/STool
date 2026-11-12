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

async function mockedPhone(t, handleApi, hash = '') {
  const browser = await chromium.launch({ channel: 'chrome', headless: true });
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, reducedMotion: 'reduce' });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  t.after(async () => {
    await browser.close();
    assert.deepEqual(errors, [], 'browser script errors');
  });
  await context.route('http://stool.test/**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname.startsWith('/api/')) {
      if (await handleApi(route, url)) return;
      return route.fulfill({ json: url.pathname === '/api/transfers' ? { transfers: [] } : {} });
    }
    const file = url.pathname === '/' ? 'index.html' : path.basename(url.pathname);
    return route.fulfill({ body: fs.readFileSync(path.join(root, file)), contentType: file.endsWith('.css') ? 'text/css' : file.endsWith('.js') ? 'text/javascript' : 'text/html' });
  });
  // Freeze timers so delayed requests, polling and auth retry counts are deterministic.
  await page.clock.install();
  await page.clock.pauseAt(new Date());
  await page.goto(`http://stool.test/${hash}`);
  return page;
}

function responseGate() {
  let arrive, release;
  const arrived = new Promise(resolve => { arrive = resolve; });
  const response = new Promise(resolve => { release = resolve; });
  return {
    arrived, release,
    async handle(route) {
      arrive();
      await route.fulfill(await response);
      return true;
    }
  };
}

async function screenshot(page, name) {
  if (!process.env.STOOL_UI_ARTIFACTS) return;
  fs.mkdirSync(process.env.STOOL_UI_ARTIFACTS, { recursive: true });
  await page.screenshot({ path: path.join(process.env.STOOL_UI_ARTIFACTS, name), fullPage: true });
}

for (const withToken of [false, true]) {
  test(`initial network failure offers retry${withToken ? ' and preserves the pairing token' : ''}`, { timeout: 15000 }, async t => {
    let sessions = 0;
    let exchanges = 0;
    const page = await mockedPhone(t, async (route, url) => {
      if (url.pathname === '/api/session') {
        sessions++;
        if (sessions === 1) await route.abort('connectionfailed');
        else if (withToken) await route.fulfill({ status: 401, json: { error: '需要验证' } });
        else await route.fulfill({ json: {} });
        return true;
      }
      if (url.pathname === '/api/auth/exchange') {
        exchanges++;
        assert.deepEqual(route.request().postDataJSON(), { token: 'pairing-token', remember: true });
        await route.fulfill({ json: {} });
        return true;
      }
      return false;
    }, withToken ? '#token=pairing-token' : '');
    await page.locator('#retryConnection').waitFor({ state: 'visible' });
    assert.equal(await page.locator('#authView').isVisible(), false);
    assert.match(await page.locator('#connectionError').innerText(), /无法连接电脑.*重试/);
    assert.equal(await page.locator('#connectionError').getAttribute('role'), 'alert');
    assert.equal(exchanges, 0, 'network failure must not consume the token');
    assert.equal(new URL(page.url()).hash, '');
    if (!withToken) await screenshot(page, 'phone-connection-retry.png');
    await page.locator('#retryConnection').click();
    await page.locator('#workspace').waitFor({ state: 'visible' });
    assert.equal(sessions, 2);
    assert.equal(exchanges, withToken ? 1 : 0);
  });
}

test('invalid pairing token stops at verification and allows a code to recover', { timeout: 15000 }, async t => {
  let sessions = 0;
  let exchanges = 0;
  const page = await mockedPhone(t, async (route, url) => {
    if (url.pathname === '/api/session') {
      sessions++;
      await route.fulfill({ status: 401, json: { error: '需要验证' } });
      return true;
    }
    if (url.pathname === '/api/auth/exchange') {
      exchanges++;
      const payload = route.request().postDataJSON();
      await route.fulfill(payload.token
        ? { status: 401, json: { error: '配对链接已失效，请输入验证码' } }
        : { json: {} });
      return true;
    }
    return false;
  }, '#token=expired');
  await page.locator('#authView').waitFor({ state: 'visible' });
  assert.match(await page.locator('#authError').innerText(), /配对链接已失效/);
  assert.equal(await page.evaluate(() => document.activeElement.id), 'codeInput');
  await page.clock.fastForward(16000);
  assert.equal(sessions, 1);
  assert.equal(exchanges, 1, 'bad token must not be exchanged in a retry loop');
  await page.locator('#codeInput').fill('123456');
  await page.locator('#authForm button').click();
  await page.locator('#workspace').waitFor({ state: 'visible' });
  assert.equal(exchanges, 2);
});

test('folder navigation wins over delayed polls; close, tab exit and auth reset invalidate responses', { timeout: 15000 }, async t => {
  const rootFolder = { folderId: 'folder', name: '项目资料', path: '', items: [
    { name: '子目录', relativePath: 'child', isFolder: true, size: 10, fileCount: 1 }
  ] };
  const childFolder = { folderId: 'folder', name: '子目录', path: 'child', items: [
    { name: '说明.txt', relativePath: 'child/readme.txt', isFolder: false, size: 10 }
  ] };
  let nextRoot = null;
  let nextChild = null;
  let rootRequests = 0;
  const page = await mockedPhone(t, async (route, url) => {
    if (url.pathname !== '/api/folders/folder') return false;
    const child = url.searchParams.get('path') === 'child';
    if (!child) rootRequests++;
    const gate = child ? nextChild : nextRoot;
    if (child) nextChild = null;
    else nextRoot = null;
    if (gate) return gate.handle(route);
    await route.fulfill({ json: child ? childFolder : rootFolder });
    return true;
  });
  await page.locator('#workspace').waitFor({ state: 'visible' });
  await page.evaluate(() => loadFolder('folder', ''));
  const poll = nextRoot = responseGate();
  await page.evaluate(() => { window.pendingPoll = loadFolder('folder', '', true); });
  await poll.arrived;
  const nav = nextChild = responseGate();
  await page.locator('.item-open').click();
  await nav.arrived;
  await page.clock.fastForward(4000);
  assert.equal(rootRequests, 2, 'polling must not supersede pending navigation with the old directory');
  nav.release({ json: childFolder });
  await page.waitForFunction(() => document.querySelector('#folderTitle').textContent === '子目录');
  poll.release({ json: rootFolder });
  await page.evaluate(() => window.pendingPoll);
  assert.equal(await page.locator('#folderTitle').innerText(), '子目录');
  assert.match(await page.locator('#downloadList').innerText(), /说明.txt/);

  await page.locator('#folderBack').click();
  await page.waitForFunction(() => document.querySelector('#folderTitle').textContent === '项目资料');
  const closing = nextRoot = responseGate();
  await page.evaluate(() => { window.pendingPoll = loadFolder('folder', '', true); });
  await closing.arrived;
  await page.locator('#folderBack').click();
  closing.release({ json: rootFolder });
  await page.evaluate(() => window.pendingPoll);
  assert.equal(await page.locator('#folderToolbar').isVisible(), false, 'late poll reopened the closed browser');
  assert.equal(await page.locator('#downloadList .item').count(), 0);

  await page.evaluate(() => loadFolder('folder', ''));
  const leaving = nextRoot = responseGate();
  await page.evaluate(() => { window.pendingPoll = loadFolder('folder', '', true); });
  await leaving.arrived;
  await page.locator('#sendTab').click();
  await page.locator('#receiveTab').click();
  leaving.release({ status: 500, json: { error: '过期的目录错误' } });
  await page.evaluate(() => window.pendingPoll);
  assert.equal(await page.locator('#archiveStatus').isVisible(), false, 'stale error leaked into the new view');
  assert.equal(await page.locator('#folderTitle').innerText(), '项目资料');
  await page.waitForFunction(() => state.folderRequest === null);

  const resetting = nextRoot = responseGate();
  await page.evaluate(() => { window.pendingPoll = loadFolder('folder', '', true); });
  await resetting.arrived;
  await page.evaluate(() => { showAuth(); showWorkspace(); });
  resetting.release({ json: rootFolder });
  await page.evaluate(() => window.pendingPoll);
  assert.equal(await page.locator('#folderToolbar').isVisible(), false, 'old session restored a folder after auth reset');
});

test('first accepted folder listing resumes across tabs and stays closed after a late response', { timeout: 15000 }, async t => {
  const browse = responseGate();
  const firstListing = responseGate();
  const folder = { folderId: 'accepted', name: '已接收的文件夹', path: '', items: [
    { name: '说明.txt', relativePath: 'readme.txt', isFolder: false, size: 10 }
  ] };
  let offerAvailable = true;
  let browseRequests = 0;
  let listingRequests = 0;
  const page = await mockedPhone(t, async (route, url) => {
    if (url.pathname === '/api/transfers') {
      await route.fulfill({ json: { transfers: [], offer: offerAvailable
        ? { id: 'incoming', name: folder.name, fileCount: 1, totalBytes: 10, canBrowse: true }
        : null } });
      return true;
    }
    if (url.pathname === '/api/transfers/incoming/browse') {
      browseRequests++;
      offerAvailable = false;
      return browse.handle(route);
    }
    if (url.pathname === '/api/folders/accepted') {
      listingRequests++;
      if (listingRequests === 1) return firstListing.handle(route);
      await route.fulfill({ json: folder });
      return true;
    }
    return false;
  });
  await page.locator('#workspace').waitFor({ state: 'visible' });
  // Retain the first request's promise so the stale response is fully processed before assertions.
  await page.evaluate(() => {
    const requestFolder = loadFolder;
    loadFolder = (...args) => {
      const request = requestFolder(...args);
      window.firstFolderListing ||= request;
      return request;
    };
  });
  await page.evaluate(() => loadTransfers());
  await page.locator('#offerBrowse').click();
  await browse.arrived;
  browse.release({ json: { folderId: folder.folderId, name: folder.name } });
  await firstListing.arrived;
  assert.equal(await page.locator('#folderToolbar').isVisible(), false, 'first listing is still pending');
  await page.locator('#sendTab').click();
  await page.locator('#receiveTab').click();
  await page.locator('#folderToolbar').waitFor({ state: 'visible' });
  assert.equal(await page.locator('#folderTitle').innerText(), folder.name);
  assert.equal(await page.locator('#downloadList a').getAttribute('href'), '/api/folders/accepted/file?path=readme.txt');
  assert.match(await page.locator('#downloadList').innerText(), /说明.txt/);
  await page.evaluate(() => loadTransfers());
  assert.equal(await page.locator('#offerDialog').evaluate(node => node.open), false);
  assert.equal(browseRequests, 1, 'returning must use the accepted folder, not require the consumed offer');
  assert.equal(listingRequests, 2, 'Receive must restart the interrupted first listing');

  await page.locator('#folderBack').click();
  firstListing.release({ json: folder });
  await page.evaluate(() => window.firstFolderListing);
  await page.locator('#sendTab').click();
  await page.locator('#receiveTab').click();
  await page.clock.fastForward(4000);
  assert.equal(await page.locator('#folderToolbar').isVisible(), false, 'late first listing reopened an explicitly closed folder');
  assert.equal(await page.locator('#downloadList .item').count(), 0);
  assert.equal(listingRequests, 2, 'closed folder identity must not resume on tab activation or polling');
});

test('upload preparation and upload lock the selection, announce failures and allow retry', { timeout: 15000 }, async t => {
  const preparation = responseGate();
  const uploadStart = responseGate();
  const chunk = responseGate();
  let batches = 0;
  let uploads = 0;
  const uploadBodies = [];
  const page = await mockedPhone(t, async (route, url) => {
    if (url.pathname === '/api/upload-batches') {
      batches++;
      if (batches === 1) return preparation.handle(route);
      await route.fulfill({ json: { id: `batch-${batches}` } });
      return true;
    }
    if (url.pathname === '/api/uploads') {
      uploads++;
      uploadBodies.push(route.request().postDataJSON());
      if (uploads === 1) return uploadStart.handle(route);
      await route.fulfill({ json: { id: 'upload', offset: 0, chunkSize: 1024 } });
      return true;
    }
    if (url.pathname === '/api/uploads/upload' && route.request().method() === 'PATCH') return chunk.handle(route);
    return false;
  });
  await page.locator('#workspace').waitFor({ state: 'visible' });
  await page.locator('#sendTab').click();
  await page.locator('#filePicker').setInputFiles({ name: 'original.txt', mimeType: 'text/plain', buffer: Buffer.from('Original') });
  const originalRow = await page.locator('#uploadList .item').elementHandle();
  await page.locator('#uploadButton').click();
  await preparation.arrived;
  for (const id of ['uploadButton', 'filePicker', 'folderPicker']) assert.equal(await page.locator(`#${id}`).isDisabled(), true);
  // A file chooser already open when preparation starts can still dispatch a change.
  await page.locator('#filePicker').setInputFiles({ name: 'late.txt', mimeType: 'text/plain', buffer: Buffer.from('Late') });
  await page.evaluate(() => document.querySelector('#uploadButton').dispatchEvent(new MouseEvent('click')));
  assert.equal(batches, 1);
  assert.match(await page.locator('#uploadList').innerText(), /original.txt/);
  assert.equal(await originalRow.evaluate(node => node.isConnected), true);
  preparation.release({ status: 503, json: { error: '电脑暂时无法创建发送任务' } });
  await page.locator('#workspaceError').waitFor({ state: 'visible' });
  assert.match(await page.locator('#workspaceError').innerText(), /电脑暂时无法创建发送任务.*重试发送/);
  assert.equal(await page.locator('#workspaceError').getAttribute('role'), 'alert');
  for (const id of ['uploadButton', 'filePicker', 'folderPicker']) assert.equal(await page.locator(`#${id}`).isEnabled(), true);
  await screenshot(page, 'phone-upload-retry.png');

  await page.locator('#uploadButton').click();
  await uploadStart.arrived;
  assert.equal(await page.locator('#uploadComposer').isVisible(), false);
  assert.equal(await page.locator('#filePicker').isDisabled(), true);
  assert.equal(await page.locator('#folderPicker').isDisabled(), true);
  uploadStart.release({ status: 503, json: { error: '电脑暂时无法保存文件' } });
  await page.locator('#uploadComposer').waitFor({ state: 'visible' });
  assert.match(await page.locator('#uploadList').innerText(), /original.txt.*失败/s);
  assert.match(await page.locator('#workspaceError').innerText(), /部分文件发送失败.*重试/);
  assert.equal(await page.locator('#uploadButton').isEnabled(), true);

  await page.locator('#uploadButton').click();
  await chunk.arrived;
  for (const id of ['uploadButton', 'filePicker', 'folderPicker']) assert.equal(await page.locator(`#${id}`).isDisabled(), true);
  await page.evaluate(() => selectFiles([new File(['Late'], 'another-late.txt')]));
  chunk.release({ json: { offset: 8, complete: true } });
  await page.locator('#uploadComposer').waitFor({ state: 'visible' });
  assert.equal(await page.locator('#selectionSummary').innerText(), '尚未选择文件');
  assert.equal(await page.locator('#uploadButton').isDisabled(), true);
  assert.equal(await page.locator('#filePicker').isEnabled(), true);
  assert.equal(await page.locator('#folderPicker').isEnabled(), true);
  assert.deepEqual(uploadBodies.map(body => [body.name, body.batchId]), [['original.txt', 'batch-2'], ['original.txt', 'batch-3']]);
  await page.locator('#filePicker').setInputFiles({ name: 'next.txt', mimeType: 'text/plain', buffer: Buffer.from('Next') });
  assert.match(await page.locator('#uploadList').innerText(), /next.txt/);
});

test('bulk selection performs linear work, retains controls and danger text meets contrast', { timeout: 15000 }, async t => {
  const count = 1200;
  const folder = { folderId: 'large', name: '大量文件', path: '', items: Array.from({ length: count }, (_, i) => ({
    name: `文件-${i}.txt`, relativePath: `file-${i}.txt`, isFolder: false, size: 10
  })) };
  const page = await mockedPhone(t, async (route, url) => {
    if (url.pathname !== '/api/folders/large') return false;
    await route.fulfill({ json: folder });
    return true;
  });
  await page.locator('#workspace').waitFor({ state: 'visible' });
  await page.evaluate(() => loadFolder('large', ''));
  const checkbox = await page.locator('#downloadList input').first().elementHandle();
  await page.evaluate(() => {
    window.selectionPathReads = 0;
    state.folder.items.forEach(item => {
      const path = item.relativePath;
      Object.defineProperty(item, 'relativePath', { get() { window.selectionPathReads++; return path; } });
    });
  });
  await page.locator('#selectAll').focus();
  await page.locator('#selectAll').click();
  assert.equal(await page.locator('#downloadList input:checked').count(), count);
  assert.equal(await page.locator('#selectedCount').innerText(), `已选择 ${count} 个文件`);
  assert.equal(await page.evaluate(() => document.activeElement.id), 'selectAll');
  assert.equal(await checkbox.evaluate(node => node.isConnected), true, 'bulk selection should preserve existing controls');
  assert.ok(await page.evaluate(() => window.selectionPathReads) < count * 8, 'selection repeatedly rescanned the directory');
  await page.evaluate(() => { window.selectionPathReads = 0; });
  await page.locator('#selectAll').click();
  assert.equal(await page.locator('#downloadList input:checked').count(), 0);
  assert.equal(await page.locator('#selectionBar').isVisible(), false);
  assert.ok(await page.evaluate(() => window.selectionPathReads) < count * 8, 'deselection repeatedly rescanned the directory');

  await page.evaluate(() => {
    closeFolderBrowser();
    renderActiveTransfers([{ id: 'danger', name: '项目资料.zip', state: 'transferring', direction: 'toPhone', totalBytes: 4096 }]);
    renderOffer({ id: 'offer', name: '项目资料.zip', fileCount: 3, totalBytes: 4096 });
  });
  const contrasts = await page.evaluate(() => {
    function luminance(color) {
      const channels = color.match(/[\d.]+/g).slice(0, 3).map(Number).map(value => {
        const s = value / 255;
        return s <= .04045 ? s / 12.92 : ((s + .055) / 1.055) ** 2.4;
      });
      return channels[0] * .2126 + channels[1] * .7152 + channels[2] * .0722;
    }
    return ['.task-action.danger', '#offerReject'].map(selector => {
      const node = document.querySelector(selector);
      let surface = node;
      while (getComputedStyle(surface).backgroundColor === 'rgba(0, 0, 0, 0)') surface = surface.parentElement;
      const foreground = luminance(getComputedStyle(node).color);
      const background = luminance(getComputedStyle(surface).backgroundColor);
      return { selector, ratio: (Math.max(foreground, background) + .05) / (Math.min(foreground, background) + .05) };
    });
  });
  for (const { selector, ratio } of contrasts) {
    assert.ok(ratio >= 4.5, `${selector}: ${ratio.toFixed(2)}:1`);
    t.diagnostic(`${selector}: ${ratio.toFixed(2)}:1`);
  }
});
