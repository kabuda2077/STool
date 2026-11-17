# STool 设置浏览器原型

独立的浏览器设置原型，用于查看组件、通用、剪贴板、OCR、翻译与服务账号的交互。产品边界见 [PRODUCT.md](PRODUCT.md)，界面规范见 [DESIGN.md](DESIGN.md)。

所有数据均为演示数据，修改只保存在当前浏览器中。账号测试仅展示模拟结果，请勿输入真实密钥或凭据。原型不连接真实服务、应用配置、注册表或系统剪贴板，也不启动 STool 桌面应用。

## 启动

使用 Node.js 20 或更新版本。服务器只使用 Node.js 标准库，无依赖，无需执行 `npm install`。

在仓库根目录打开 PowerShell：

```powershell
cd .\prototypes\settings
npm start
```

手动在浏览器打开 **http://127.0.0.1:5173/**。服务器只监听本机 IPv4 回环地址；按 `Ctrl+C` 停止。也可在本目录执行 `node server.mjs`，从其他目录启动时可执行 `node .\prototypes\settings\server.mjs`。

需要修改端口时，在同一个 PowerShell 会话中设置：

```powershell
$env:STOOL_SETTINGS_PORT = '5174'
npm start
```

此时访问 http://127.0.0.1:5174/。端口必须为 1–65535 的整数；端口占用或启动失败会报错退出，不自动切换端口。移除覆盖值可执行 `Remove-Item Env:STOOL_SETTINGS_PORT`。

## 自动刷新

保持页面和服务器开启。服务器监听本目录中 `index.html`、`styles.css` 和 `app.js` 的新增、修改、替换及删除，并通过 `/__events` 推送变化；同一文件连续变化合并约 80 毫秒。浏览器端负责订阅并应用刷新：CSS 变化可替换样式链接，HTML 或 JavaScript 变化时重新加载页面。实际刷新行为由 `app.js` 的订阅代码实现。

文档、测试和其他目录的修改不发送刷新事件。服务器不会注入脚本，也不会自动打开浏览器。刷新后浏览器内演示状态的保留方式由页面实现决定。

## SSE 约定

浏览器使用 `new EventSource('/__events')`。响应类型为 `text/event-stream; charset=utf-8`，使用 UTF-8 JSON 数据。事件名称通过 `addEventListener` 订阅。

每次连接或重新连接，服务器先发送重试间隔及 **`ready`** 事件；此事件仅用于确认连接，不触发页面刷新：

```text
retry: 1000
event: ready
data: {"version":1710000000000}

```

文件变化发送 **`change`** 事件：

```text
event: change
data: {"filename":"styles.css","version":1710000000001}

```

- `filename`：`index.html`、`styles.css` 或 `app.js`，不包含路径。
- `version`：整数，整个服务器进程共用且随每个变化事件严格递增；初始值为启动时的毫秒时间戳。可用于资源 URL 的查询参数，例如 `/styles.css?v=1710000000001`。
- 每条事件以一个空行结束；服务器每 25 秒发送 `: heartbeat` 注释保活，该注释不触发事件监听器。
- 连接关闭后移除该客户端；服务器停止时关闭连接、文件监听器及计时器。
- 不提供事件重放或 SSE `id`；连接中断期间的变更不会补发。`ready` 会携带当前版本，客户端如需处理重连期间的变更，可与上一次保存的版本比较。

浏览器订阅示意：

```js
const events = new EventSource('/__events');
events.addEventListener('ready', (event) => {
  const { version } = JSON.parse(event.data);
  // 首次连接仅记录版本，不刷新页面。
});
events.addEventListener('change', (event) => {
  const { filename, version } = JSON.parse(event.data);
  // styles.css：替换样式链接并附带 ?v=version。
  // index.html / app.js：location.reload()。
});
```

## 服务范围与检查

仅提供 `/`（对应 `index.html`）、`/index.html`、`/styles.css`、`/app.js`、四个明确列入白名单的 `/assets/` 服务标识（DeepSeek、OpenAI、Gemini、腾讯云），以及存在时的 `/favicon.svg`。标识的来源与许可证见 [assets/README.md](assets/README.md)。静态资源支持 GET、HEAD；SSE 仅支持 GET。查询参数可用于刷新资源缓存；文档、测试、服务器源文件及仓库其他文件均不可访问。

语法检查无需启动服务：

```powershell
node --check server.mjs
```

浏览器验证复用环境中已有的 Playwright，服务器本身不依赖它。保持预览服务运行，然后执行：

```powershell
$env:STOOL_PLAYWRIGHT_MODULE = '本机已有的 playwright 包目录'
node .\prototypes\settings\verify.cjs  # 在仓库根目录执行
```

检查五页导航、输入与保存、快捷键冲突、账号测试模拟、异常恢复、紧凑布局、150% 放大和 CSS 实时刷新。截图写入 `artifacts/settings-prototype/`。浏览器预览修改仅保存在当前标签页的 sessionStorage，点击“重置”恢复演示数据。
