const { chromium } = require(process.env.STOOL_PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../../artifacts/settings-prototype');
(async () => {
  fs.mkdirSync(root, { recursive:true });
  const browser = await chromium.launch({ channel:'chrome', headless:true });
  const page = await browser.newPage({ viewport:{width:1280,height:900}, deviceScaleFactor:1 });
  const errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  try {
    await page.goto('http://127.0.0.1:5173/');
    await page.locator('[data-page="general"]').waitFor();
    const heights=await page.locator('.settings-group:first-child .switch-row').evaluateAll(rows=>rows.map(row=>row.getBoundingClientRect().height));
    assert.equal(heights[0],heights[1],'single and double-line switch rows must share height');
    await page.screenshot({path:path.join(root,'01-general.png')});
    for(const id of ['clipboard','ocr','translation','accounts']) {
      await page.locator(`[data-page="${id}"]`).click();
      if(id==='accounts') {
        await page.locator('.brand-logo').evaluate(image=>image.decode());
        assert.equal(await page.locator('.brand-logo').evaluate(image=>image.naturalWidth>0),true);
      }
      await page.screenshot({path:path.join(root,`page-${id}.png`)});
    }
    for(const platform of ['openai','google','deepseek']) {
      await page.locator('#account-platform').selectOption(platform);
      await page.locator('.brand-logo').evaluate(image=>image.decode());
      assert.equal(await page.locator('.brand-logo').evaluate(image=>image.naturalWidth>0),true);
    }
    await page.locator('#account-tab-tencent').click();
    await page.locator('.brand-logo').evaluate(image=>image.decode());
    await page.screenshot({path:path.join(root,'accounts-tencent.png')});
    await page.locator('#account-tab-text').click();
    await page.locator('#app-frame .advanced summary').hover();
    await page.screenshot({path:path.join(root,'accounts-advanced-hover.png')});
    await page.locator('[data-view="components"]').click();
    await page.screenshot({path:path.join(root,'components.png'),fullPage:true});
    await page.locator('[data-view="app"]').click();
    await page.locator('[data-page="clipboard"]').click();
    await page.locator('#app-name').fill('Private');
    await page.waitForTimeout(650);
    assert.equal(await page.locator('#app-name').inputValue(),'Private');
    await page.locator('#app-name').fill('PrivateApp.exe, privateapp.EXE; Example.exe');
    await page.locator('#app-name').press('Enter');
    assert.equal(await page.locator('.app-name',{hasText:'PrivateApp.exe'}).count(),1);
    await page.getByRole('button',{name:'移除 PrivateApp.exe',exact:true}).click();
    assert.equal(await page.locator('.app-name',{hasText:'PrivateApp.exe'}).count(),0);
    await page.locator('[data-page="ocr"]').click();
    await page.locator('#ocr-provider').selectOption('vision');
    await page.locator('[data-account-link="vision"]').click();
    assert.equal(await page.locator('#account-tab-vision').getAttribute('aria-selected'),'true');
    await page.locator('[data-action="return"]').click();
    assert.equal(await page.locator('#ocr-provider').inputValue(),'vision');
    await page.locator('#ocr-fallback').selectOption('false');
    assert.equal(await page.locator('#ocr-language').count(),0);
    await page.locator('[data-page="general"]').click();
    await page.locator('[data-hotkey="screenshot"]').click();
    await page.keyboard.press('Alt+2');
    assert.match(await page.locator('#screenshot-error').innerText(),/已用于/);
    await page.locator('[data-hotkey="screenshot"]').click();
    await page.keyboard.press('Control+Shift+K');
    assert.match(await page.locator('[data-hotkey="screenshot"]').innerText(),/Ctrl/);
    await page.locator('[data-page="accounts"]').click();
    await page.locator('#account-tab-text').click();
    await page.locator('[data-input="accounts.text.key"]').fill('demo-key-not-a-real-secret');
    await page.locator('[data-test="text-model"]').click();
    await page.locator('[data-results]').getByText('模型测试通过（模拟）',{exact:true}).waitFor();
    await page.locator('[data-test="text-model"]').click();
    await page.locator('[data-input="accounts.text.key"]').fill('changed-demo-key');
    await page.waitForTimeout(1050);
    assert.equal(await page.locator('[data-results]').getByText('模型测试通过（模拟）',{exact:true}).count(),0);
    await page.locator('[data-fetch="text"]').click();
    await page.locator('[data-fetch-result]').getByText(/已获取 .* 个演示模型/).waitFor();
    assert.ok(await page.locator('#model-options option').count()>0);
    await page.locator('#account-platform').selectOption('openai');
    assert.equal(await page.locator('#model-options option').count(),0);
    await page.locator('#account-tab-text').focus();
    await page.keyboard.press('ArrowRight');
    assert.equal(await page.locator('#account-tab-vision').getAttribute('aria-selected'),'true');
    await page.locator('#demo-scenario').selectOption('save-error');
    await page.locator('[data-page="general"]').click();
    const before = await page.locator('[data-toggle="general.startup"]').getAttribute('aria-checked');
    await page.locator('[data-toggle="general.startup"]').click();
    await page.locator('[data-page="translation"]').click();
    assert.equal(await page.locator('#app-frame h1').innerText(),'通用');
    await page.locator('[data-action="discard-save"]').click();
    assert.equal(await page.locator('#app-frame h1').innerText(),'翻译');
    await page.locator('#demo-scenario').selectOption('normal');
    await page.locator('[data-page="general"]').click();
    assert.equal(await page.locator('[data-toggle="general.startup"]').getAttribute('aria-checked'),before);
    await page.locator('[data-page="accounts"]').click();
    await page.locator('#account-tab-text').click();
    await page.locator('#app-frame .advanced summary').click();
    await page.locator('#demo-scenario').selectOption('save-error');
    const credential=page.locator('[data-input="accounts.text.key"]');
    await credential.fill('unsaved-demo-draft');
    await page.waitForTimeout(650);
    assert.equal(await credential.evaluate(input=>input===document.activeElement),true);
    assert.equal(await page.locator('#app-frame .advanced').getAttribute('open'),'');
    const jsPath=path.join(__dirname,'app.js');
    const js=fs.readFileSync(jsPath,'utf8');
    const reloaded=page.waitForEvent('load');
    fs.writeFileSync(jsPath,js);
    await reloaded;
    await page.locator('.save-error').waitFor();
    assert.equal(await page.locator('[data-input="accounts.text.key"]').inputValue(),'unsaved-demo-draft');
    await page.locator('#demo-scenario').selectOption('normal');
    await page.locator('[data-action="retry-save"]').click();
    assert.equal(await page.locator('.save-error').count(),0);
    await page.locator('#reset-demo').click();
    await page.locator('#viewport-size').selectOption('compact');
    for(const id of ['general','clipboard','ocr','translation','accounts']) {
      await page.locator(`[data-page="${id}"]`).click();
      const overflow=await page.locator('.page-content').evaluate(el=>el.scrollWidth>el.clientWidth+1);
      assert.equal(overflow,false,`${id} compact overflow`);
      await page.screenshot({path:path.join(root,`compact-${id}.png`)});
    }
    await page.locator('#viewport-size').selectOption('standard');
    await page.locator('#preview-scale').selectOption('1.5');
    await page.setViewportSize({width:1540,height:1240});
    await page.locator('[data-page="general"]').click();
    await page.screenshot({path:path.join(root,'general-150.png')});
    await page.locator('#preview-scale').selectOption('1');
    await page.setViewportSize({width:520,height:900});
    for(const id of ['general','clipboard','ocr','translation','accounts']) {
      await page.locator(`[data-page="${id}"]`).click();
      assert.equal(await page.locator('.page-content').evaluate(el=>el.scrollWidth>el.clientWidth+1),false,`${id} narrow overflow`);
    }
    await page.screenshot({path:path.join(root,'narrow.png')});
    // Verify the running server sends change events and the page replaces its stylesheet.
    const prior=await page.locator('#app-styles').getAttribute('href');
    const cssPath=path.join(__dirname,'styles.css');
    const css=fs.readFileSync(cssPath,'utf8');
    fs.writeFileSync(cssPath,css);
    await page.waitForFunction(old=>document.querySelector('#app-styles').getAttribute('href')!==old,prior);
    assert.equal((await page.request.get('http://127.0.0.1:5173/PRODUCT.md')).status(),404);
    assert.deepEqual(errors,[]);
    console.log('PASS: five pages, component sheet, clipboard draft/dedup/removal, account routing, conditional OCR, hotkeys, simulated requests/stale responses, keyboard tabs, failed-save recovery, compact and 150% layouts, live CSS refresh.');
  } finally { await browser.close(); }
})().catch(error=>{console.error(error);process.exitCode=1;});
