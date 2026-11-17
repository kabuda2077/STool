const icons = {
  settings:'<path d="M9.5 3h5l.6 2.2 2 .9 2-.7 2.5 4.2-1.5 1.6v2.4l1.5 1.6-2.5 4.2-2-.7-2 .9-.6 2.4h-5l-.6-2.4-2-.9-2 .7L2 15.2l1.5-1.6v-2.4L2 9.6l2.5-4.2 2 .7 2-.9z"/><circle cx="12" cy="12" r="3"/>',
  chevron:'<path d="m9 5 7 7-7 7"/>',
  arrow:'<path d="m10 6-6 6 6 6M4 12h16"/>',
  check:'<path d="m5 12 4 4L19 6"/>',
  close:'<path d="m6 6 12 12M6 18 18 6"/>',
  minus:'<path d="M5 12h14"/>',
  square:'<rect x="5" y="5" width="14" height="14" rx="1"/>',
  plus:'<path d="M12 5v14M5 12h14"/>',
  eye:'<path d="M2 12s3-7 10-7 10 7 10 7-3 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/>',
  eyeOff:'<path d="m3 3 18 18M10 5a11 11 0 0 1 12 7 17 17 0 0 1-3 4M6 6a17 17 0 0 0-4 6s3 7 10 7c2 0 4-.7 5-1.5"/>',
  info:'<circle cx="12" cy="12" r="9"/><path d="M12 11v6m0-10v.2"/>',
  error:'<circle cx="12" cy="12" r="9"/><path d="M12 7v6m0 4v.2"/>',
  spinner:'<path d="M20 12a8 8 0 1 1-8-8"/>'
};
const icon = (name, cls='') => `<svg class="${cls}" viewBox="0 0 24 24" aria-hidden="true">${icons[name] || icons.settings}</svg>`;
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const pages = [ ['general','通用'], ['clipboard','剪贴板'], ['ocr','OCR'], ['translation','翻译'], ['accounts','服务账号'] ];
const presets = {
  openai:{ name:'OpenAI', url:'https://api.openai.com/v1/chat/completions', model:'gpt-6-luna', models:['gpt-6-luna','gpt-4.1-mini'] },
  google:{ name:'Google AI Studio', url:'https://generativelanguage.googleapis.com/v1beta/openai/chat/completions', model:'gemini-flash-latest', models:['gemini-flash-latest','gemini-pro-latest'] },
  deepseek:{ name:'DeepSeek', url:'https://api.deepseek.com/chat/completions', model:'deepseek-flash', models:['deepseek-flash'] },
  custom:{ name:'自定义', url:'', model:'', models:['example-text-model','example-vision-model'] }
};
const defaultState = () => ({
  general:{ startup:false, hideTray:false, diagnostics:false, hotkeys:{ screenshot:'Alt+1', translation:'Alt+2', clipboard:'Alt+3', transfer:'Alt+4', settings:'Alt+5' } },
  clipboard:{ enabled:true, retention:'30', maximum:'1000', image:'5', apps:['KeePass.exe','KeePassXC.exe','1Password.exe','Bitwarden.exe'] },
  ocr:{ provider:'local', fallback:true, language:'auto' },
  translation:{ provider:'google', language:'zh-en', ocr:'local', mode:'fast' },
  accounts:{ tencent:{ id:'', key:'' }, text:{ platform:'deepseek', url:presets.deepseek.url, key:'', model:presets.deepseek.model }, vision:{ shared:true, platform:'openai', url:presets.openai.url, key:'', model:presets.openai.model } }
});
const STORE='stool-settings-prototype-v1';
const stored = sessionStorage.getItem(STORE);
let state = stored ? JSON.parse(stored) : defaultState();
let saved = structuredClone(state);
let page = sessionStorage.getItem('stool-prototype-page') || 'general';
if (!pages.some(([id])=>id===page)) page='general';
let account = sessionStorage.getItem('stool-prototype-account') || 'text';
if (!['tencent','text','vision'].includes(account)) account='text';
let view='app', scenario='normal', sourcePage=null, appDraft='', saveTimer, saveFeedbackTimer, pendingNavigation=null;
let dirty=false, saveFailed=false, recording=null;
const pendingPreview=sessionStorage.getItem('stool-prototype-draft');
if(pendingPreview) {
  const draft=JSON.parse(pendingPreview);
  state=draft.state; saved=draft.saved; dirty=draft.dirty; saveFailed=draft.saveFailed;
  scenario=draft.scenario; sourcePage=draft.sourcePage; appDraft=draft.appDraft;
  document.querySelector('#demo-scenario').value=scenario;
  sessionStorage.removeItem('stool-prototype-draft');
}
const disclosureStates={};
const hotkeyErrors={};
const requests={};
const results={};
const modelLists={};
const scrollPositions={};
const frame=document.querySelector('#app-frame');
const specimen=document.querySelector('#specimen');
function get(path) { return path.split('.').reduce((object,key)=>object[key],state); }
function set(path,value) { const parts=path.split('.'); const key=parts.pop(); const target=parts.reduce((object,key)=>object[key],state); target[key]=value; }
function announce(text) { document.querySelector('#announcement').textContent=text; }
function group(title,body,action='') { return `<section class="settings-group"><div class="group-heading"><h2>${title}</h2>${action}</div><div class="group-surface">${body}</div></section>`; }
function brand(platform) {
  const files={openai:'openai',google:'gemini-color',deepseek:'deepseek-color',tencent:'tencentcloud-color'};
  return files[platform]?`<img class="brand-logo" src="/assets/${files[platform]}.svg" alt="">`:icon('settings');
}
function accountIdentity(platform,title,description) {
  return `<div class="account-identity"><span class="brand-emblem">${brand(platform)}</span><div><h2>${esc(title)}</h2><p class="account-description">${description}</p></div></div>`;
}
function row(title,control,{description='',cls='',id=''}={}) { return `<div class="setting-row ${cls}"><div class="row-label"><label class="row-title" ${id?`for="${id}"`:''}>${title}</label>${description?`<p class="row-description">${description}</p>`:''}</div>${control}</div>`; }
function toggle(path,title,description='') { const id=path.replaceAll('.','-'); return row(title,`<button class="toggle" role="switch" id="${id}" aria-label="${esc(title)}" aria-checked="${get(path)}" data-toggle="${path}"></button>`,{id,description,cls:'switch-row'}); }
function select(path,title,options,description='') { const id=path.replaceAll('.','-'); return row(title,`<div class="field-value"><select class="control" id="${id}" data-setting="${path}">${options.map(([value,label])=>`<option value="${esc(value)}" ${String(get(path))===String(value)?'selected':''}>${esc(label)}</option>`).join('')}</select></div>`,{id,description}); }
function missing(text,target,label='配置账号') { return `<div class="account-link"><span>${text}</span><button class="text-button" data-account-link="${target}">${label}${icon('chevron')}</button></div>`; }
function isConfigured(target) { const data=state.accounts[target]; if(target==='tencent') return Boolean(data.id.trim() && data.key.trim()); const credentials=target==='vision'&&data.shared?state.accounts.text:data; return Boolean(credentials.key.trim() && credentials.url.trim() && data.model.trim()); }
function keyMarkup(value) { return value.split('+').map(part=>`<kbd>${esc(part)}</kbd>`).join('<span class="key-plus" aria-hidden="true">+</span>'); }
function renderGeneral() {
  const names={screenshot:'截图',translation:'翻译',clipboard:'剪贴板',transfer:'局域网传输',settings:'设置'};
  const hotkeys=Object.entries(names).map(([id,name])=>`<div class="setting-row hotkey-row"><span class="row-title">${name}</span><button class="hotkey" data-hotkey="${id}" aria-label="${name}快捷键，当前 ${esc(state.general.hotkeys[id])}，点击录制" aria-describedby="hotkey-help ${id}-error">${keyMarkup(state.general.hotkeys[id])}</button><p class="field-error" id="${id}-error" ${hotkeyErrors[id]?'':'hidden'}>${esc(hotkeyErrors[id]||'')}</p></div>`).join('');
  return group('启动与显示',toggle('general.startup','开机自动启动')+toggle('general.hideTray','隐藏托盘图标','仍可通过快捷键打开工具'))
    +group('快捷键',hotkeys+'<p class="row-note" id="hotkey-help">点击键位录制，按 Esc 取消。</p>',`<button class="text-button muted" data-action="reset-hotkeys">恢复默认</button>`)
    +group('诊断',toggle('general.diagnostics','记录诊断日志','排查问题时开启'));
}
function renderClipboard() {
  const list=state.clipboard.apps.map((app,i)=>`<div class="app-list-item"><span class="app-name" title="${esc(app)}">${esc(app)}</span><button class="icon-button remove" data-remove-app="${i}" aria-label="移除 ${esc(app)}">${icon('close')}</button></div>`).join('');
  return group('记录',toggle('clipboard.enabled','记录剪贴板历史',state.clipboard.enabled?'':'已有记录会保留'))
    +group('保留策略',select('clipboard.retention','保留时间',[['7','7 天'],['30','30 天'],['90','90 天'],['0','不限时间']])+select('clipboard.maximum','最多保留',[['500','500 条'],['1000','1,000 条'],['3000','3,000 条'],['5000','5,000 条']])+select('clipboard.image','单张图片上限',[['2','2 MB'],['5','5 MB'],['10','10 MB'],['20','20 MB']])+'<p class="group-note">达到时间或数量上限时清理旧记录，收藏不受影响。超限图片不记录。</p>')
    +group('不记录的应用',`<div class="app-list">${list||'<p class="list-empty">尚未排除应用</p>'}</div><form class="add-app" id="add-app-form"><label class="sr-only" for="app-name">添加应用进程名</label><input class="control" id="app-name" placeholder="进程名，例如 KeePass.exe" value="${esc(appDraft)}" autocomplete="off"><button class="btn" type="submit">${icon('plus')}添加</button></form><p class="group-note">密码管理器标记为隐私的内容始终不会记录。</p>`);
}
function renderOcr() {
  let engine=select('ocr.provider','识别引擎',[['local','Windows 本地 OCR'],['tencent','腾讯云 OCR'],['vision','AI 视觉模型']]);
  const target=state.ocr.provider==='tencent'?'tencent':'vision';
  if(state.ocr.provider!=='local'&&!isConfigured(target)) engine+=missing(`${target==='tencent'?'腾讯云账号':'AI 视觉模型'}尚未配置。`,target);
  if(state.ocr.provider!=='local') engine+=select('ocr.fallback','识别失败时',[['true','改用 Windows 本地 OCR'],['false','停止并提示错误']]);
  const languages=[['auto','自动'],['zh-Hans','简体中文'],['en-US','English'],['ja-JP',scenario==='missing-language'?'日本語（未安装）':'日本語']];
  const unavailable=scenario==='missing-language'&&state.ocr.language==='ja-JP';
  return group('截图文字识别',engine)+'<p class="group-note">截图翻译的识别引擎在翻译页单独设置。</p>'
    +((state.ocr.provider==='local'||state.ocr.fallback)?group('本地识别',select('ocr.language','识别语言',languages)+(unavailable?'<p class="field-error">此语言未安装。请选择其他语言，或在 Windows 设置中安装语言包。</p>':'<p class="group-note">可选语言以 Windows 已安装的 OCR 语言包为准。</p>')):'');
}
function renderTranslation() {
  const t=state.translation;
  let text=select('translation.provider','翻译引擎',[['google','谷歌翻译'],['tencent','腾讯云翻译'],['text','AI 翻译']]);
  if(t.provider!=='google'&&!isConfigured(t.provider)) text+=missing(`${t.provider==='tencent'?'腾讯云账号':'AI 文本模型'}尚未配置。`,t.provider);
  text+=select('translation.language','语言',[['zh-en','中文 ⇄ 英文'],['auto-zh','自动 → 中文'],['auto-en','自动 → 英文'],['auto-ja','自动 → 日文'],['auto-ko','自动 → 韩文']]);
  let screenshot=select('translation.ocr','识别引擎',[['local','Windows 本地 OCR'],['tencent','腾讯云 OCR']]);
  if(t.ocr==='tencent') { if(!isConfigured('tencent')) screenshot+=missing('腾讯云账号尚未配置。','tencent'); screenshot+='<p class="group-note">识别失败时自动改用 Windows 本地 OCR。</p>'; }
  screenshot+=row('翻译模式',`<div class="field-value segmented" role="group" aria-label="截图翻译模式">${[['fast','快速'],['smart','智能']].map(([value,label])=>`<button data-mode="${value}" aria-pressed="${t.mode===value}">${label}</button>`).join('')}</div>`);
  if(t.mode==='smart'&&!isConfigured('text')) screenshot+=missing('智能模式需要 AI 文本模型。','text','配置 AI 文本模型');
  const providerName={google:'谷歌翻译',tencent:'腾讯云翻译',text:'AI 翻译'}[t.provider];
  screenshot+=`<p class="group-note">${t.mode==='smart'?'由 AI 选段并翻译，失败时改用快速模式。':`自动选段，使用${providerName}。`}</p>`;
  return group('文字翻译',text)+group('截图翻译',screenshot);
}
function formInput(path,label,{password=false,placeholder='',list=''}={}) {
  const id=path.replaceAll('.','-');
  return `<div class="form-field"><label for="${id}">${label}</label><div class="${password?'password-wrap':''}"><input class="control" id="${id}" data-input="${path}" value="${esc(get(path))}" ${password?'type="password"':'type="text"'} placeholder="${esc(placeholder)}" autocomplete="off" spellcheck="false" ${list?`list="${list}"`:''}>${password?`<button class="icon-button" data-reveal="${id}" aria-label="显示 ${label}" aria-pressed="false">${icon('eye')}</button>`:''}</div></div>`;
}
function resultMarkup(key) { const result=results[key]; if(!result) return ''; return `<p class="test-result ${result.status}" role="status">${icon(result.status==='pending'?'spinner':result.status==='error'?'error':'check',result.status==='pending'?'spinner':'')}<span>${esc(result.message)}</span></p>`; }
function renderAccounts() {
  const tabs=`<div class="account-tabs" role="tablist" aria-label="服务账号">${[['tencent','腾讯云'],['text','AI 文本'],['vision','AI 视觉']].map(([id,label])=>`<button class="account-tab" role="tab" id="account-tab-${id}" aria-controls="account-panel" aria-selected="${account===id}" tabindex="${account===id?0:-1}" data-account-tab="${id}">${label}</button>`).join('')}</div>`;
  const back=sourcePage?`<button class="text-button return-link" data-action="return">${icon('arrow')}返回${pages.find(([id])=>id===sourcePage)[1]}设置</button>`:'';
  let content='';
  if(account==='tencent') {
    content=accountIdentity('tencent','腾讯云','文字识别与翻译，共用一组凭据。')+'<div class="account-form">'+formInput('accounts.tencent.id','Secret ID',{placeholder:'填写演示 Secret ID'})+formInput('accounts.tencent.key','Secret Key',{password:true,placeholder:'仅使用演示密钥'})+`<div class="test-area"><div class="test-actions"><button class="btn" data-test="tencent-translation">测试翻译</button><button class="btn" data-test="tencent-ocr">测试 OCR</button></div><div data-results="tencent">${resultMarkup('tencent-translation')}${resultMarkup('tencent-ocr')}</div></div></div>`;
  } else {
    const a=state.accounts[account];
    const shared=account==='vision'&&a.shared;
    const platform=shared?state.accounts.text.platform:a.platform;
    content=accountIdentity(platform,presets[platform].name,account==='text'?'AI 翻译与智能截图翻译。':'AI 文字识别，支持图片输入。')+'<div class="account-form">';
    if(account==='vision') content+=toggle('accounts.vision.shared','共用文本模型账号');
    if(!shared) {
      content+=`<div class="form-field"><label for="account-platform">平台</label><select class="control" id="account-platform" data-setting="accounts.${account}.platform">${Object.entries(presets).map(([id,p])=>`<option value="${id}" ${a.platform===id?'selected':''}>${p.name}</option>`).join('')}</select></div>`;
      content+=formInput(`accounts.${account}.key`,'API Key',{password:true,placeholder:'仅使用演示 API Key'});
    } else {
      content+=`<p class="group-note">使用 ${esc(presets[state.accounts.text.platform].name)} 的接口和密钥。${!isConfigured('text')?'<button class="text-button" data-account-tab="text">配置文本模型</button>':''}</p>`;
    }
    content+=`<div class="form-field"><label for="account-model">模型</label><div class="form-field-row"><input class="control" id="account-model" data-input="accounts.${account}.model" value="${esc(a.model)}" placeholder="输入或选择模型名" list="model-options" autocomplete="off" spellcheck="false"><button class="btn" data-fetch="${account}" ${results[`${account}-fetch`]?.status==='pending'?'disabled':''}>${results[`${account}-fetch`]?.status==='pending'?icon('spinner','spinner'):'获取模型'}</button></div><datalist id="model-options">${(modelLists[account]||[]).map(model=>`<option value="${esc(model)}"></option>`).join('')}</datalist><div data-fetch-result>${resultMarkup(`${account}-fetch`)}</div></div>`;
    content+=`<div class="test-area"><div class="test-actions"><button class="btn primary" data-test="${account}-model">测试模型</button><span class="status-tag">${isConfigured(account)?'配置完整':'待填写凭据'}</span></div><div data-results="${account}">${resultMarkup(`${account}-model`)}</div></div>`;
    if(!shared) content+=`<details class="advanced" ${a.platform==='custom'||disclosureStates[account]?'open':''}><summary>${icon('chevron')}接口地址${a.platform==='custom'?'':'与高级设置'}</summary>${formInput(`accounts.${account}.url`,'API URL',{placeholder:'https://api.example.com/v1'})}<p class="url-preview">${a.platform==='custom'?'兼容 OpenAI Chat Completions 接口。':'已使用平台默认地址，可按需修改。'}</p></details>`;
    content+='</div>';
  }
  return back+tabs+`<section id="account-panel" class="account-surface" role="tabpanel" aria-labelledby="account-tab-${account}">${content}</section>`;
}
function renderApp({keepScroll=false}={}) {
  const active=document.activeElement;
  let focusSelector=null, selection=null;
  if(frame.contains(active)) {
    if(active.id) focusSelector=`#${CSS.escape(active.id)}`;
    else {
      const attribute=[...active.attributes].find(attr=>attr.name.startsWith('data-'));
      if(attribute) focusSelector=`[${attribute.name}="${CSS.escape(attribute.value)}"]`;
    }
    if(active instanceof HTMLInputElement) selection=[active.selectionStart,active.selectionEnd];
  }
  const oldScroll=frame.querySelector('.page-content')?.scrollTop||0;
  const title=pages.find(([id])=>id===page)[1];
  const pageContent={general:renderGeneral,clipboard:renderClipboard,ocr:renderOcr,translation:renderTranslation,accounts:renderAccounts}[page]();
  frame.innerHTML=`<header class="titlebar"><span class="titlebar-title">STool <span>设置</span></span><div class="window-actions" aria-hidden="true"><span>${icon('minus')}</span><span>${icon('square')}</span><span>${icon('close')}</span></div></header><nav class="sidebar" aria-label="设置分类">${pages.map(([id,label],index)=>`${index===4?'<div class="nav-divider"></div>':''}<button class="nav-item" data-page="${id}" ${page===id?'aria-current="page"':''}>${label}</button>`).join('')}</nav><section class="workspace" aria-label="${title}设置"><div class="page-heading"><h1>${title}</h1><span class="save-state" role="status"></span></div>${saveFailed?'<div class="save-error" role="alert">修改尚未保存，请重试或放弃本次修改。<div class="save-error-actions"><button class="text-button" data-action="retry-save">重试保存</button><button class="text-button" data-action="discard-save">放弃修改</button></div></div>':''}<div class="page-content">${pageContent}</div></section>`;
  frame.querySelector('.page-content').scrollTop=keepScroll?oldScroll:(scrollPositions[page]||0);
  updateSaveStatus();
  updateBusyButtons();
  const replacement=focusSelector?frame.querySelector(focusSelector):null;
  if(replacement&&!replacement.disabled) {
    replacement.focus({preventScroll:true});
    if(selection&&replacement instanceof HTMLInputElement) replacement.setSelectionRange(...selection);
  }
}
function updateSaveStatus(message) {
  const element=frame.querySelector('.save-state');
  if(element) element.innerHTML=saveFailed?'保存失败':dirty?'等待保存…':message===undefined?'':`${icon('check')}${esc(message)}`;
}
function scheduleSave(immediate=false) {
  pendingNavigation=null;
  dirty=true;
  clearTimeout(saveTimer); clearTimeout(saveFeedbackTimer);
  updateSaveStatus();
  if(immediate) flush(); else saveTimer=setTimeout(flush,500);
}
function flush() {
  clearTimeout(saveTimer);
  if(!dirty) return true;
  if(scenario==='save-error') {
    if(!saveFailed) { saveFailed=true; renderApp({keepScroll:true}); }
    announce('修改尚未保存。可以重试或放弃修改。');
    return false;
  }
  const wasFailed=saveFailed;
  saved=structuredClone(state);
  sessionStorage.setItem(STORE,JSON.stringify(saved));
  dirty=false; saveFailed=false;
  if(wasFailed) renderApp({keepScroll:true});
  updateSaveStatus('已保存');
  saveFeedbackTimer=setTimeout(()=>updateSaveStatus(),1800);
  return true;
}
function navigate(next,target=null) {
  cancelRecording();
  if(!flush()) { pendingNavigation=()=>navigate(next,target); return; }
  scrollPositions[page]=frame.querySelector('.page-content')?.scrollTop||0;
  if(target) { sourcePage=page==='accounts'?sourcePage:page; account=target; }
  else if(next!=='accounts') sourcePage=null;
  else sourcePage=null;
  page=next;
  sessionStorage.setItem('stool-prototype-page',page);
  sessionStorage.setItem('stool-prototype-account',account);
  renderApp();
  frame.querySelector(`[data-page="${next}"]`)?.focus({preventScroll:true});
}
function switchAccount(target) {
  if(!flush()) { pendingNavigation=()=>switchAccount(target); return; }
  account=target;
  sessionStorage.setItem('stool-prototype-account',account);
  renderApp();
  frame.querySelector(`[data-account-tab="${account}"]`)?.focus({preventScroll:true});
}
function invalidateAccount(target,clearModels=true) {
  if(clearModels) delete modelLists[target];
  for(const key of Object.keys(results)) if(key.startsWith(target+'-')) { delete results[key]; requests[key]=(requests[key]||0)+1; }
  if(target==='text'&&state.accounts.vision.shared) invalidateAccount('vision',clearModels);
  if(page==='accounts') {
    frame.querySelectorAll('[data-results],[data-fetch-result]').forEach(element=>element.innerHTML='');
    const status=frame.querySelector('.test-actions .status-tag');
    if(status) status.textContent=isConfigured(account)?'配置完整':'待填写凭据';
    if(clearModels&&(account===target||(target==='text'&&account==='vision'&&state.accounts.vision.shared))) {
      const list=frame.querySelector('#model-options'); if(list)list.innerHTML='';
    }
    updateBusyButtons();
  }
}
function renderAccountFeedback() {
  const resultHost=frame.querySelector('[data-results]');
  if(!resultHost)return;
  resultHost.innerHTML=account==='tencent'?resultMarkup('tencent-translation')+resultMarkup('tencent-ocr'):resultMarkup(`${account}-model`);
  const fetchHost=frame.querySelector('[data-fetch-result]');
  if(fetchHost)fetchHost.innerHTML=resultMarkup(`${account}-fetch`);
  const list=frame.querySelector('#model-options');
  if(list)list.innerHTML=(modelLists[account]||[]).map(model=>`<option value="${esc(model)}"></option>`).join('');
  updateBusyButtons();
}
function updateBusyButtons() {
  frame.querySelectorAll('[data-test]').forEach(button=>button.disabled=results[button.dataset.test]?.status==='pending');
  frame.querySelectorAll('[data-fetch]').forEach(button=>{ const busy=results[`${button.dataset.fetch}-fetch`]?.status==='pending'; button.disabled=busy; button.innerHTML=busy?icon('spinner','spinner'):'获取模型'; });
}
function addApps() {
  const names=appDraft.split(/[,;，；\n]/).map(name=>name.trim().replace(/\.exe$/i,'')).filter(Boolean).map(name=>name+'.exe');
  if(!names.length) { document.querySelector('#app-name')?.focus(); return; }
  const known=new Set(state.clipboard.apps.map(app=>app.toLowerCase()));
  for(const name of names) if(!known.has(name.toLowerCase())) { state.clipboard.apps.push(name); known.add(name.toLowerCase()); }
  appDraft=''; scheduleSave(true); renderApp({keepScroll:true}); document.querySelector('#app-name')?.focus();
}
function startRecording(id) {
  cancelRecording(); recording=id;
  const button=frame.querySelector(`[data-hotkey="${id}"]`);
  button.classList.add('recording'); button.textContent='请按快捷键…'; button.focus();
  announce('请按新的快捷键组合，Esc 取消。');
}
function cancelRecording() {
  if(!recording) return;
  const button=frame.querySelector(`[data-hotkey="${recording}"]`);
  if(button) { button.classList.remove('recording'); button.innerHTML=keyMarkup(state.general.hotkeys[recording]); }
  recording=null;
}
function showHotkeyError(id,message) { hotkeyErrors[id]=message; const error=frame.querySelector(`#${id}-error`); if(error) { error.textContent=message; error.hidden=!message; } announce(message); }
function onHotkey(event) {
  if(!recording) return;
  if(event.key==='Escape'||event.key==='Tab') { if(event.key==='Escape')event.preventDefault(); cancelRecording(); return; }
  event.preventDefault();
  if(['Control','Alt','Shift','Meta'].includes(event.key)) return;
  const modifiers=[event.ctrlKey?'Ctrl':null,event.altKey?'Alt':null,event.shiftKey?'Shift':null,event.metaKey?'Win':null].filter(Boolean);
  const key=event.code.startsWith('Key')?event.code.slice(3):event.code.startsWith('Digit')?event.code.slice(5):event.key===' '?'Space':event.key;
  const id=recording;
  if(!modifiers.length&&!/^F\d+$/.test(key)) { showHotkeyError(id,'请使用 Ctrl、Alt、Shift 或 Win 组合键。'); return; }
  const value=[...modifiers,key].join('+');
  const conflict=Object.keys(state.general.hotkeys).find(other=>other!==id&&state.general.hotkeys[other]===value);
  cancelRecording();
  if(conflict) { showHotkeyError(id,`${value} 已用于其他功能，原快捷键已保留。`); return; }
  for(const errorId of Object.keys(hotkeyErrors)) delete hotkeyErrors[errorId];
  state.general.hotkeys[id]=value; scheduleSave(true); renderApp({keepScroll:true});
  frame.querySelector(`[data-hotkey="${id}"]`)?.focus();
}
function runTest(key) {
  const target=key.split('-')[0];
  const isFetch=key.endsWith('-fetch');
  const data=state.accounts[target];
  const credentials=target==='vision'&&data.shared?state.accounts.text:data;
  const complete=target==='tencent'?data.id.trim()&&data.key.trim():credentials.key.trim()&&credentials.url.trim()&&(isFetch||data.model.trim());
  if(!complete) {
    results[key]={status:'error',message:target==='tencent'?'请先填写演示 Secret ID 和 Secret Key。':'请先填写演示凭据'+(isFetch?'。':'与模型配置。')};
    renderAccountFeedback(); return;
  }
  if(target!=='tencent') {
    try { const url=new URL(credentials.url); if(!['https:','http:'].includes(url.protocol))throw new Error(); }
    catch { results[key]={status:'error',message:'接口地址需以 https:// 或 http:// 开头。'}; renderAccountFeedback(); return; }
  }
  const token=(requests[key]||0)+1; requests[key]=token;
  const requestPage=page; const requestAccount=account;
  const failed=scenario==='network-error';
  results[key]={status:'pending',message:isFetch?'正在获取演示模型…':'正在测试…'};
  renderAccountFeedback();
  setTimeout(()=>{
    if(requests[key]!==token) return;
    if(failed) results[key]={status:'error',message:'连接超时，请检查接口地址后重试。（模拟）'};
    else if(isFetch) {
      modelLists[target]=[...presets[credentials.platform].models];
      results[key]={status:'success',message:`已获取 ${modelLists[target].length} 个演示模型，可在输入框中选择。`};
    } else results[key]={status:'success',message:`${key==='tencent-ocr'?'OCR':key==='tencent-translation'?'翻译':'模型'}测试通过（模拟）`};
    if(page===requestPage&&account===requestAccount)renderAccountFeedback();
    announce(results[key].message);
  },900);
}
function specimenMarkup() {
  return `<div class="specimen-intro"><h1>组件样板</h1><p>在同一处检查尺寸、对齐和状态。这里的操作不会修改演示设置。</p></div><div class="specimen-grid">
  <section class="specimen-block"><h2>设置行 · 52 px</h2>${row('开机自动启动','<button class="toggle" role="switch" aria-label="样板开机启动" aria-checked="false" data-sample-toggle></button>',{cls:'switch-row'})}${row('隐藏托盘图标','<button class="toggle" role="switch" aria-label="样板隐藏托盘" aria-checked="true" data-sample-toggle></button>',{cls:'switch-row',description:'仍可通过快捷键打开工具'})}<p class="specimen-note">单行和双行共用相同高度，开关共用右边线。</p></section>
  <section class="specimen-block"><h2>字段 · 32 px</h2><label class="sr-only" for="sample-input">样板输入框</label><input class="control" id="sample-input" placeholder="输入内容"><div style="height:12px"></div><label class="sr-only" for="sample-select">样板选择器</label><select class="control" id="sample-select"><option>Windows 本地 OCR</option><option>腾讯云 OCR</option></select><p class="specimen-note">相同圆角、内边距和焦点外圈。</p></section>
  <section class="specimen-block"><h2>快捷键</h2><div class="specimen-actions"><button class="hotkey" aria-label="示例快捷键">${keyMarkup('Alt+1')}</button><button class="hotkey recording">请按快捷键…</button></div><p class="field-error">Alt+2 已用于翻译，原快捷键已保留。</p></section>
  <section class="specimen-block"><h2>操作</h2><div class="specimen-actions"><button class="btn primary">测试模型</button><button class="btn">获取模型</button><button class="btn" disabled>测试中…</button><button class="text-button">配置账号${icon('chevron')}</button></div><p class="specimen-note">每个区域只有一个主要操作。</p></section>
  <section class="specimen-block"><h2>状态反馈</h2><div class="test-result">${icon('check')}模型测试通过</div><div class="test-result error" style="margin-top:12px">${icon('error')}连接超时，请检查接口地址后重试。</div><div class="test-result pending" style="margin-top:12px">${icon('spinner','spinner')}正在测试…</div></section>
  <section class="specimen-block"><h2>披露与提示</h2><details class="advanced"><summary>${icon('chevron')}接口地址与高级设置</summary><p class="group-note">仅标题响应悬浮，展开内容沿用表单边线。</p></details><div class="tooltip-wrap" style="margin-top:14px"><button class="icon-button" aria-label="查看提示">${icon('info')}</button><span class="tooltip" role="tooltip">这是一条简短的补充说明。</span></div></section>
  <section class="specimen-block"><h2>排除列表</h2><div class="app-list-item"><span class="app-name">KeePass.exe</span><button class="icon-button remove" aria-label="样板移除应用">${icon('close')}</button></div><p class="specimen-note">紧凑列表项，操作贴近对应对象。</p></section>
  <section class="specimen-block"><h2>工具面板的色彩</h2><div class="token-swatches"><div class="swatch" style="background:var(--accent)" title="#2554C7"></div><div class="swatch" style="background:var(--sidebar)" title="#F7F7F8"></div><div class="swatch" style="background:var(--text)" title="#18181B"></div><div class="swatch" style="background:var(--muted)" title="#626773"></div></div><p class="specimen-note">蓝色用于主要操作与焦点，导航和内容以中性色建立层级。</p></section>
  </div>`;
}
function standardsMarkup() {
  const rows=[['窗口','800 × 680 / 680 × 580','独立桌面工具的体量'],['侧栏','144 / 行高 34','服务账号最后，功能入口保持稳定'],['内容区域','最大 552 / 水平内边距 28','收敛阅读宽度，标题与字段对齐'],['间距阶梯','4 · 8 · 12 · 14 · 16 · 20 · 24 · 28 · 32','同类关系只用一种距离'],['设置行','最小 52','同组开关行等高，长内容自然增高'],['输入与按钮','32 / 圆角 7','输入、选择和操作在同一条基线上'],['快捷键','116 × 32','纯文字标签与轻键帽，列表行高 44'],['文字','22 标题 / 14 正文 / 12 辅助','系统字体，清楚的字重层次'],['分组','连续内容面板 + 间距','弱化外框，组内细分隔'],['悬浮与焦点','轻背景 / 蓝色外圈','悬浮仅响应可操作区域'],['错误与测试','就地显示，修改后失效','保存成功与服务可用分开表达'],['动效','120–160 ms','只解释状态变化，尊重减少动态效果']];
  return `<div class="specimen-intro"><h1>设计标准</h1><p>一套规则，五个页面。此为可随预览校准的初稿。</p></div><div class="standard-copy"><p>参考 Linear 的整体空间与视觉权重，沿用 STool 的浅色和蓝色。侧栏与窗口底色连续，主体收敛为单一内容面板；用文字、间距和弱分隔建立层级，减少重复卡片与装饰图标。</p><p>账号采用同页切换，一次完成一个服务。快捷键、排除列表和连接测试各有对应结构，共享同一套尺寸与状态规则。</p></div><table class="standard-table"><thead><tr><th>对象</th><th>标准（px / DIP）</th><th>用途</th></tr></thead><tbody>${rows.map(row=>`<tr>${row.map(cell=>`<td>${cell}</td>`).join('')}</tr>`).join('')}</tbody></table><p class="specimen-note">150% 是浏览器预览放大。WPF 落地时仍需核验系统 DPI、字体度量与原生控件。</p>`;
}
function changeView(next) {
  view=next; cancelRecording();
  frame.hidden=view!=='app'; specimen.hidden=view==='app';
  document.querySelectorAll('[data-view]').forEach(button=>{ if(button.dataset.view===view)button.setAttribute('aria-current','page'); else button.removeAttribute('aria-current'); });
  if(view!=='app') specimen.innerHTML=view==='components'?specimenMarkup():standardsMarkup();
}
frame.addEventListener('click',event=>{
  const button=event.target.closest('button'); if(!button)return;
  if(button.dataset.page) { navigate(button.dataset.page); return; }
  if(button.dataset.accountLink) { navigate('accounts',button.dataset.accountLink); return; }
  if(button.dataset.accountTab) { switchAccount(button.dataset.accountTab); return; }
  if(button.dataset.toggle) {
    const path=button.dataset.toggle; set(path,!get(path));
    if(path.startsWith('accounts.'))invalidateAccount('vision');
    scheduleSave(true); renderApp({keepScroll:true}); frame.querySelector(`[data-toggle="${path}"]`)?.focus({preventScroll:true}); return;
  }
  if(button.dataset.mode) { state.translation.mode=button.dataset.mode; scheduleSave(true); renderApp({keepScroll:true}); frame.querySelector(`[data-mode="${button.dataset.mode}"]`)?.focus({preventScroll:true}); return; }
  if(button.dataset.hotkey) { startRecording(button.dataset.hotkey); return; }
  if(button.dataset.removeApp!==undefined) { const i=Number(button.dataset.removeApp); state.clipboard.apps.splice(i,1); scheduleSave(true); renderApp({keepScroll:true}); (frame.querySelector(`[data-remove-app="${Math.min(i,state.clipboard.apps.length-1)}"]`)||frame.querySelector('#app-name'))?.focus(); return; }
  if(button.dataset.reveal) { const input=document.getElementById(button.dataset.reveal); const visible=input.type==='password'; input.type=visible?'text':'password'; button.innerHTML=icon(visible?'eyeOff':'eye'); button.setAttribute('aria-pressed',String(visible)); button.setAttribute('aria-label',`${visible?'隐藏':'显示'} ${input.id.endsWith('-key')?(account==='tencent'?'Secret Key':'API Key'):'密钥'}`); return; }
  if(button.dataset.test) { runTest(button.dataset.test); return; }
  if(button.dataset.fetch) { runTest(`${button.dataset.fetch}-fetch`); return; }
  const action=button.dataset.action;
  if(action==='reset-hotkeys') { state.general.hotkeys=defaultState().general.hotkeys; Object.keys(hotkeyErrors).forEach(key=>delete hotkeyErrors[key]); scheduleSave(true); renderApp({keepScroll:true}); }
  if(action==='return') { const target=sourcePage; if(target)navigate(target); }
  if(action==='retry-save') { const pending=pendingNavigation; if(flush()) {pendingNavigation=null; pending?.();} }
  if(action==='discard-save') { clearTimeout(saveTimer); const pending=pendingNavigation; pendingNavigation=null; state=structuredClone(saved); dirty=false; saveFailed=false; Object.keys(modelLists).forEach(key=>delete modelLists[key]); Object.keys(results).forEach(key=>{delete results[key];requests[key]=(requests[key]||0)+1;}); renderApp({keepScroll:true}); pending?.(); }
});
frame.addEventListener('change',event=>{
  const path=event.target.dataset.setting; if(!path)return;
  const old=get(path); const value=path==='ocr.fallback'?event.target.value==='true':event.target.value;
  set(path,value);
  if(path.startsWith('accounts.')) {
    const target=path.split('.')[1]; const a=state.accounts[target];
    if(path.endsWith('.platform')&&value!=='custom') { const prior=presets[old]; a.url=presets[value].url; if(!a.model||Object.values(presets).some(p=>p.model===a.model)||a.model===prior?.model)a.model=presets[value].model; }
    invalidateAccount(target);
  }
  scheduleSave(true); renderApp({keepScroll:true}); frame.querySelector(`[data-setting="${path}"]`)?.focus({preventScroll:true});
});
frame.addEventListener('input',event=>{
  if(event.target.id==='app-name') { appDraft=event.target.value; return; }
  const path=event.target.dataset.input; if(!path)return;
  set(path,event.target.value); invalidateAccount(path.split('.')[1],!path.endsWith('.model')); scheduleSave();
});
frame.addEventListener('toggle',event=>{ if(event.target.matches('details.advanced'))disclosureStates[account]=event.target.open; },true);
frame.addEventListener('focusout',event=>{ if(event.target.dataset.input)flush(); if(event.target.dataset.hotkey&&recording)cancelRecording(); });
frame.addEventListener('submit',event=>{ if(event.target.id==='add-app-form'){ event.preventDefault(); addApps(); } });
frame.addEventListener('keydown',event=>{
  if(event.target.matches('[role=tab]')&&['ArrowLeft','ArrowRight','Home','End'].includes(event.key)) {
    event.preventDefault(); const list=['tencent','text','vision']; const i=list.indexOf(account); const next=event.key==='Home'?0:event.key==='End'?2:(i+(event.key==='ArrowRight'?1:2))%3; switchAccount(list[next]);
  }
});
document.addEventListener('keydown',onHotkey,true);
document.querySelectorAll('[data-view]').forEach(button=>button.addEventListener('click',()=>changeView(button.dataset.view)));
specimen.addEventListener('click',event=>{ const button=event.target.closest('[data-sample-toggle]'); if(button)button.setAttribute('aria-checked',String(button.getAttribute('aria-checked')!=='true')); });
document.querySelector('#viewport-size').addEventListener('change',event=>frame.classList.toggle('compact',event.target.value==='compact'));
document.querySelector('#preview-scale').addEventListener('change',event=>frame.classList.toggle('enlarged',event.target.value==='1.5'));
document.querySelector('#demo-scenario').addEventListener('change',event=>{ scenario=event.target.value; if(scenario==='missing-language'){state.ocr.language='ja-JP'; navigate('ocr');} else renderApp({keepScroll:true}); });
document.querySelector('#reset-demo').addEventListener('click',()=>{
  clearTimeout(saveTimer); clearTimeout(saveFeedbackTimer); cancelRecording(); state=defaultState(); saved=structuredClone(state); dirty=false;saveFailed=false;sourcePage=null;pendingNavigation=null;appDraft='';scenario='normal';page='general';account='text';
  Object.keys(modelLists).forEach(key=>delete modelLists[key]);Object.keys(disclosureStates).forEach(key=>delete disclosureStates[key]);Object.keys(results).forEach(key=>{delete results[key];requests[key]=(requests[key]||0)+1;});Object.keys(hotkeyErrors).forEach(key=>delete hotkeyErrors[key]);Object.keys(scrollPositions).forEach(key=>delete scrollPositions[key]);
  sessionStorage.removeItem(STORE);sessionStorage.removeItem('stool-prototype-page');sessionStorage.removeItem('stool-prototype-account');document.querySelector('#demo-scenario').value='normal';changeView('app');renderApp();announce('演示设置已重置。');
});
renderApp();
const events=new EventSource('/__events');
let liveVersion=null;
function reloadPreview() {
  sessionStorage.setItem('stool-prototype-draft',JSON.stringify({state,saved,dirty,saveFailed,scenario,sourcePage,appDraft}));
  location.reload();
}
events.addEventListener('ready',event=>{ const {version}=JSON.parse(event.data); if(liveVersion!==null&&version!==liveVersion)reloadPreview(); liveVersion=version; document.querySelector('#live-status').textContent='实时预览'; });
events.addEventListener('change',event=>{ const {filename,version}=JSON.parse(event.data); liveVersion=version; if(filename==='styles.css')document.querySelector('#app-styles').href=`/styles.css?v=${version}`; else reloadPreview(); });
events.onerror=()=>document.querySelector('#live-status').textContent='正在重新连接…';
