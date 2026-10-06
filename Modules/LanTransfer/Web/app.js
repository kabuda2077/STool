const state = {
  selected: [],
  selectedRows: [],
  uploads: new Map(),
  folder: null,
  folderRootName: "",
  downloadSelection: new Map(),
  transferPollTimer: 0,
  uploadBatchId: null,
  uploadRequests: new Map(),
  currentOfferId: null,
  currentOffer: null
};
const $ = id => document.getElementById(id);
const headers = { "X-STool-Request": "1" };
let sessionRefresh = null;

// 会话失效（电脑端重启服务、换了网络）时，用“记住此设备”的令牌换一个新会话。
// 设备令牌的 Cookie 只发往 /api/session，其余请求只带会话 Cookie。
function refreshSession() {
  if (!sessionRefresh) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 8000);
    sessionRefresh = fetch("/api/session", { credentials: "same-origin", signal: controller.signal })
      .then(response => response.ok, () => false)
      .finally(() => {
        clearTimeout(timeout);
        sessionRefresh = null;
      });
  }
  return sessionRefresh;
}

function canRefreshSession(path) {
  return !path.startsWith("/api/session") && !path.startsWith("/api/auth/");
}

function formatBytes(value) {
  if (!Number.isFinite(value) || value <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
  return `${(value / Math.pow(1024, index)).toFixed(index ? 1 : 0)} ${units[index]}`;
}

function formatSpeed(value) {
  return `${formatBytes(value)}/s`;
}

async function api(path, options = {}, allowSessionRefresh = true) {
  const { timeoutMs = 8000, signal: externalSignal, ...requestOptions } = options;
  const controller = new AbortController();
  const abort = () => controller.abort();
  if (externalSignal) {
    if (externalSignal.aborted) controller.abort();
    else externalSignal.addEventListener("abort", abort, { once: true });
  }
  const timeout = setTimeout(abort, timeoutMs);
  try {
    const response = await fetch(path, {
      credentials: "same-origin",
      ...requestOptions,
      signal: controller.signal,
      headers: { ...(requestOptions.headers || {}), ...(requestOptions.method && requestOptions.method !== "GET" ? headers : {}) }
    });
    if (response.status === 401 && allowSessionRefresh && canRefreshSession(path) && await refreshSession())
      return api(path, options, false);
    if (!response.ok) {
      let message = `请求失败 (${response.status})`;
      try { message = (await response.json()).error || message; } catch { }
      const error = new Error(message);
      error.status = response.status;
      throw error;
    }
    if (response.status === 204 || requestOptions.method === "HEAD") return null;
    return await response.json();
  } catch (error) {
    if (error.name === "AbortError")
      throw new Error("连接电脑超时，请确认手机和电脑处于同一 Wi-Fi。 ");
    throw error;
  } finally {
    clearTimeout(timeout);
    externalSignal?.removeEventListener("abort", abort);
  }
}

function showWorkspace() {
  $("loadingView").hidden = true;
  $("authView").hidden = true;
  $("workspace").hidden = false;
  $("connectionLabel").textContent = "已连接电脑";
  $("statusDot").classList.add("online");
  sendPresence();
  scheduleTransferPoll(0);
}

async function sendPresence() {
  if ($("workspace").hidden) return;
  try {
    await api("/api/presence", { timeoutMs: 5000 });
    $("connectionLabel").textContent = "已连接电脑";
    $("statusDot").classList.add("online");
  } catch (error) {
    if (error.status === 401) return showAuth();
    $("connectionLabel").textContent = "正在重新连接";
    $("statusDot").classList.remove("online");
  }
}

function showAuth(message = "") {
  $("loadingView").hidden = true;
  $("workspace").hidden = true;
  $("authView").hidden = false;
  $("connectionLabel").textContent = "等待验证";
  $("statusDot").classList.remove("online");
  $("authError").textContent = message;
  clearTimeout(state.transferPollTimer);
}

async function exchange(payload) {
  await api("/api/auth/exchange", {
    method: "POST",
    body: JSON.stringify(payload),
    headers: { "Content-Type": "application/json" }
  });
  showWorkspace();
}

async function initialize() {
  const params = new URLSearchParams(location.hash.startsWith("#") ? location.hash.slice(1) : "");
  const token = params.get("token");
  if (token) history.replaceState(null, "", "/");

  try {
    await api("/api/session");
    showWorkspace();
  } catch {
    if (!token) return showAuth();
    try { await exchange({ token, remember: true }); }
    catch (error) { showAuth(error.message); }
  }
}

$("authForm").addEventListener("submit", async event => {
  event.preventDefault();
  $("authError").textContent = "";
  try {
    await exchange({ code: $("codeInput").value.trim(), remember: $("rememberInput").checked });
  } catch (error) {
    $("authError").textContent = error.message;
  }
});

document.querySelectorAll(".tab").forEach(button => button.addEventListener("click", () => {
  document.querySelectorAll(".tab").forEach(tab => {
    const selected = tab === button;
    tab.classList.toggle("active", selected);
    tab.setAttribute("aria-selected", String(selected));
  });
  const send = button.dataset.tab === "send";
  $("sendView").hidden = !send;
  $("receiveView").hidden = send;
}));

const transferStateText = {
  preparing: "准备中",
  awaitingConfirmation: "等待确认",
  transferring: "传输中",
  paused: "已暂停",
  waitingForResume: "等待继续",
  failed: "失败"
};

function scheduleTransferPoll(delay) {
  clearTimeout(state.transferPollTimer);
  if ($("workspace").hidden) return;
  state.transferPollTimer = setTimeout(loadTransfers, delay);
}

async function loadTransfers() {
  try {
    const result = await api("/api/transfers", { timeoutMs: 5000 });
    const transfers = result.transfers || [];
    if (state.uploadBatchId) {
      const uploadTransfer = transfers.find(item => item.id === state.uploadBatchId);
      if (uploadTransfer?.state === "paused")
        abortUploadRequests();
    }
    const offer = result.offer || null;
    renderActiveTransfers(offer ? transfers.filter(item => item.id !== offer.id) : transfers);
    renderOffer(offer);
  } catch (error) {
    if (error.status === 401) showAuth();
  } finally {
    scheduleTransferPoll(document.visibilityState === "visible" ? 1000 : 4000);
  }
}

function renderActiveTransfers(transfers) {
  const container = $("activeTransferList");
  container.replaceChildren();
  $("activeTransfers").hidden = transfers.length === 0;
  transfers.forEach(transfer => container.append(createTransferTask(transfer)));
}

function createTransferTask(transfer) {
  const row = document.createElement("div");
  row.className = "transfer-task";
  const head = document.createElement("div");
  head.className = "transfer-task-head";
  const name = document.createElement("div");
  name.className = "transfer-task-name";
  name.textContent = transfer.name;
  const status = document.createElement("span");
  status.className = "transfer-task-state";
  status.textContent = transfer.state === "transferring"
    ? transfer.direction === "toPhone" ? "发送中" : "接收中"
    : transferStateText[transfer.state] || transfer.state;
  const actions = document.createElement("div");
  actions.className = "transfer-task-actions";

  if (transfer.state === "preparing" || transfer.state === "transferring")
    actions.append(createTaskAction("暂停", () => controlTransfer(transfer.id, "pause")));
  if (transfer.state === "paused")
    actions.append(createTaskAction("继续", () => controlTransfer(transfer.id, "resume")));
  if (transfer.state === "waitingForResume" && transfer.direction === "toPhone") {
    const remaining = incompleteDownloads(transfer);
    if (remaining.length) {
      const label = remaining.length > 1 ? `继续剩余 ${remaining.length} 项` : "继续";
      actions.append(createTaskAction(label, () => triggerDownloads(remaining)));
    }
  } else if (transfer.state === "waitingForResume") {
    actions.append(createTaskAction("继续", () => controlTransfer(transfer.id, "resume")));
  }
  actions.append(createTaskAction("取消", () => controlTransfer(transfer.id, "cancel"), true));
  head.append(name, status, actions);

  const progressTrack = document.createElement("div");
  progressTrack.className = "transfer-task-progress";
  const progress = document.createElement("span");
  const percent = transfer.totalBytes > 0
    ? Math.min(100, transfer.transferredBytes / transfer.totalBytes * 100)
    : 0;
  progress.style.width = `${percent}%`;
  progressTrack.append(progress);
  const detail = document.createElement("div");
  detail.className = "transfer-task-detail";
  const downloadItems = Array.isArray(transfer.downloads) ? transfer.downloads : [];
  const completedItems = downloadItems.filter(item => item.completed).length;
  const itemProgress = downloadItems.length > 1 ? ` · ${completedItems}/${downloadItems.length} 项` : "";
  detail.textContent = transfer.state === "failed"
    ? transfer.error || "传输失败"
    : `${formatBytes(transfer.transferredBytes)} / ${formatBytes(transfer.totalBytes)}` +
      (transfer.state === "transferring" && transfer.bytesPerSecond > 0
        ? ` · ${formatSpeed(transfer.bytesPerSecond)}`
        : "") + itemProgress;
  row.append(head, progressTrack, detail);
  return row;
}

function createTaskAction(label, handler, danger = false) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = `task-action${danger ? " danger" : ""}`;
  button.textContent = label;
  button.addEventListener("click", handler);
  return button;
}

async function controlTransfer(id, action) {
  try {
    await api(`/api/transfers/${id}/${action}`, { method: "POST" });
    if ((action === "pause" || action === "cancel") && state.uploadBatchId === id)
      abortUploadRequests();
    if (action === "cancel" && state.currentOfferId === id)
      hideOffer();
    await loadTransfers();
  } catch (error) {
    showArchiveError(error.message);
  }
}

function renderOffer(offer) {
  if (!offer) {
    if (state.currentOfferId) hideOffer();
    return;
  }
  if (state.currentOfferId === offer.id && !$("offerDialog").hidden) return;
  state.currentOfferId = offer.id;
  state.currentOffer = offer;
  $("offerDialog").querySelector(".offer-dialog").classList.toggle("separate", offer.separate);
  $("offerTitle").textContent = offer.separate ? `${offer.itemCount} 个项目` : offer.name;
  $("offerDetail").textContent = offer.separate
    ? `共 ${formatBytes(offer.totalBytes)}`
    : `${offer.fileCount} 个文件 · ${formatBytes(offer.totalBytes)}`;
  $("offerHint").hidden = !offer.separate;
  $("offerHint").textContent = offer.separate
    ? "文件将分别下载，文件夹已单独打包。浏览器可能请求多文件下载权限。"
    : "";
  $("offerBrowse").hidden = !offer.canBrowse;
  $("offerActions").classList.toggle("single", !offer.canBrowse);
  $("offerAccept").textContent = offer.separate
    ? `接收 ${offer.itemCount} 个项目`
    : offer.canBrowse ? "接收全部" : "接收";
  $("offerDialog").hidden = false;
}

function hideOffer() {
  $("offerDialog").hidden = true;
  state.currentOfferId = null;
  state.currentOffer = null;
}

$("offerAccept").addEventListener("click", () => {
  const offer = state.currentOffer;
  if (!offer) return;
  if (offer.separate)
    triggerDownloads(offer.downloads || []);
  else if (offer.downloadUrl)
    triggerDownload(offer.downloadUrl, offer.name);
  hideOffer();
  scheduleTransferPoll(500);
});

$("offerBrowse").addEventListener("click", async () => {
  const id = state.currentOfferId;
  if (!id) return;
  try {
    const folder = await api(`/api/transfers/${id}/browse`, { method: "POST" });
    hideOffer();
    $("receiveTab").click();
    state.folderRootName = folder.name;
    state.downloadSelection.clear();
    await loadFolder(folder.folderId, "");
    scheduleTransferPoll(0);
  } catch (error) {
    showArchiveError(error.message);
  }
});

$("offerReject").addEventListener("click", async () => {
  if (state.currentOfferId)
    await controlTransfer(state.currentOfferId, "reject");
});

document.addEventListener("visibilitychange", () => scheduleTransferPoll(0));

function selectFiles(fileList) {
  state.selected = Array.from(fileList || []);
  $("uploadList").replaceChildren();
  state.selectedRows = state.selected.map(file => createUploadRow(file));
  const total = state.selected.reduce((sum, file) => sum + file.size, 0);
  $("selectionSummary").textContent = state.selected.length
    ? `${state.selected.length} 个文件，共 ${formatBytes(total)}`
    : "尚未选择文件";
  $("uploadButton").disabled = state.selected.length === 0;
}

function updateUploadComposerVisibility() {
  $("uploadComposer").hidden = Boolean(state.uploadBatchId);
}

$("filePicker").addEventListener("change", event => selectFiles(event.target.files));
$("folderPicker").addEventListener("change", event => selectFiles(event.target.files));

function fingerprint(file) {
  return [file.webkitRelativePath || file.name, file.size, file.lastModified].join("|");
}

function createUploadRow(file) {
  const row = document.createElement("div");
  row.className = "item";
  const main = document.createElement("div");
  main.className = "item-main";
  const name = document.createElement("div");
  name.className = "item-name";
  name.textContent = file.webkitRelativePath || file.name;
  const meta = document.createElement("div");
  meta.className = "item-meta";
  meta.textContent = formatBytes(file.size);
  const status = document.createElement("div");
  status.className = "item-action";
  status.textContent = "等待";
  const progress = document.createElement("div");
  progress.className = "progress";
  main.append(name, meta);
  row.append(main, status, progress);
  $("uploadList").append(row);
  return { status, progress, meta };
}

async function getResumeOffset(uploadId, allowSessionRefresh = true) {
  const response = await fetch(`/api/uploads/${uploadId}`, { method: "HEAD", credentials: "same-origin" });
  if (response.status === 401 && allowSessionRefresh && await refreshSession())
    return getResumeOffset(uploadId, false);
  if (!response.ok) return null;
  return Number(response.headers.get("Upload-Offset") || 0);
}

async function beginUpload(file, batchId) {
  const key = `stool-upload:${batchId}:${fingerprint(file)}`;
  const stored = localStorage.getItem(key);
  if (stored) {
    const offset = await getResumeOffset(stored);
    if (offset !== null) return { id: stored, offset, chunkSize: 4 * 1024 * 1024, key };
    localStorage.removeItem(key);
  }

  const result = await api("/api/uploads", {
    method: "POST",
    body: JSON.stringify({
      name: file.name,
      relativePath: file.webkitRelativePath || file.name,
      size: file.size,
      lastModified: file.lastModified,
      batchId
    }),
    headers: { "Content-Type": "application/json" }
  });
  localStorage.setItem(key, result.id);
  return { id: result.id, offset: result.offset, chunkSize: result.chunkSize, key, complete: result.complete };
}

function patchChunk(id, offset, blob, onProgress) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    state.uploadRequests.set(id, xhr);
    xhr.open("PATCH", `/api/uploads/${id}`);
    xhr.withCredentials = true;
    xhr.setRequestHeader("X-STool-Request", "1");
    xhr.setRequestHeader("Upload-Offset", String(offset));
    xhr.setRequestHeader("Content-Type", "application/octet-stream");
    xhr.upload.onprogress = event => onProgress(event.loaded);
    xhr.onerror = () => reject(new Error("网络连接已中断"));
    xhr.onabort = () => reject(new Error("传输已暂停"));
    xhr.onload = () => {
      state.uploadRequests.delete(id);
      if (xhr.status >= 200 && xhr.status < 300) {
        resolve(JSON.parse(xhr.responseText || "{}"));
      } else if (xhr.status === 409) {
        resolve({ offset: Number(xhr.getResponseHeader("Upload-Offset") || offset), retry: true });
      } else {
        try { reject(new Error(JSON.parse(xhr.responseText).error)); }
        catch { reject(new Error(`上传失败 (${xhr.status})`)); }
      }
    };
    xhr.onloadend = () => state.uploadRequests.delete(id);
    xhr.send(blob);
  });
}

async function uploadFile(file, row, batchId) {
  const upload = await beginUpload(file, batchId);
  if (upload.complete) {
    localStorage.removeItem(upload.key);
    row.status.textContent = "完成";
    row.progress.style.width = "100%";
    return;
  }

  let offset = upload.offset;
  while (offset < file.size) {
    const end = Math.min(file.size, offset + upload.chunkSize);
    const chunk = file.slice(offset, end);
    row.status.textContent = `${Math.floor(offset / file.size * 100)}%`;
    try {
      const result = await patchChunk(upload.id, offset, chunk, loaded => {
        const value = file.size ? Math.min(100, (offset + loaded) / file.size * 100) : 100;
        row.progress.style.width = `${value}%`;
        row.status.textContent = `${Math.floor(value)}%`;
      });
      offset = result.offset;
      if (result.complete) break;
    } catch (error) {
      const transfer = await waitUntilUploadCanContinue(batchId, row);
      if (transfer.state === "canceled" || transfer.state === "rejected")
        throw new Error("传输已取消");
      row.status.textContent = "重连中";
      const resumed = await getResumeOffset(upload.id);
      if (resumed === null) throw error;
      offset = resumed;
    }
  }
  localStorage.removeItem(upload.key);
  row.status.textContent = "完成";
  row.progress.style.width = "100%";
}

$("uploadButton").addEventListener("click", async () => {
  const files = state.selected.slice();
  if (!files.length) return;
  $("uploadButton").disabled = true;
  const batch = describeUploadBatch(files);
  let batchId;
  try {
    const created = await api("/api/upload-batches", {
      method: "POST",
      body: JSON.stringify(batch),
      headers: { "Content-Type": "application/json" }
    });
    batchId = created.id;
    state.uploadBatchId = batchId;
    updateUploadComposerVisibility();
    scheduleTransferPoll(0);
  } catch (error) {
    $("uploadButton").disabled = false;
    showArchiveError(error.message);
    return;
  }
  const queue = files.map((file, index) => ({
    file,
    row: state.selectedRows[index] || createUploadRow(file)
  }));
  let cursor = 0;
  let uploadFailed = false;
  const workers = Array.from({ length: Math.min(2, queue.length) }, async () => {
    while (cursor < queue.length) {
      const item = queue[cursor++];
      try { await uploadFile(item.file, item.row, batchId); }
      catch (error) {
        uploadFailed = true;
        item.row.status.textContent = "失败";
        item.row.meta.textContent = error.message;
      }
    }
  });
  await Promise.all(workers);
  state.uploadBatchId = null;
  state.uploadRequests.clear();
  if (!uploadFailed) {
    $("filePicker").value = "";
    $("folderPicker").value = "";
    selectFiles([]);
  }
  updateUploadComposerVisibility();
  $("uploadButton").disabled = state.selected.length === 0;
});

function describeUploadBatch(files) {
  const relativePaths = files.map(file => file.webkitRelativePath).filter(Boolean);
  const roots = new Set(relativePaths.map(path => path.split("/")[0]));
  const isFolder = relativePaths.length === files.length && roots.size === 1;
  return {
    name: isFolder ? Array.from(roots)[0] : files.length === 1 ? files[0].name : `${files.length} 个文件`,
    fileCount: files.length,
    totalBytes: files.reduce((sum, file) => sum + file.size, 0),
    isFolder
  };
}

function abortUploadRequests() {
  state.uploadRequests.forEach(xhr => xhr.abort());
  state.uploadRequests.clear();
}

async function waitUntilUploadCanContinue(batchId, row) {
  while (true) {
    let transfer;
    try {
      transfer = await api(`/api/transfers/${batchId}`);
    } catch {
      return { state: "canceled" };
    }
    if (transfer.state === "paused") {
      row.status.textContent = "已暂停";
      await new Promise(resolve => setTimeout(resolve, 500));
      continue;
    }
    if (transfer.state === "waitingForResume") {
      await api(`/api/transfers/${batchId}/resume`, { method: "POST" });
      await new Promise(resolve => setTimeout(resolve, 300));
      continue;
    }
    return transfer;
  }
}

async function loadFolder(folderId, path) {
  try {
    const query = path ? `?path=${encodeURIComponent(path)}` : "";
    state.folder = await api(`/api/folders/${folderId}${query}`);
    renderFolder();
  } catch (error) {
    showArchiveError(error.message);
  }
}

function renderFolder() {
  const folder = state.folder;
  if (!folder) return closeFolderBrowser();

  const container = $("downloadList");
  container.replaceChildren();
  container.hidden = false;
  $("folderToolbar").hidden = false;
  $("folderTitle").textContent = folder.name;
  const location = folder.path ? `${state.folderRootName}/${folder.path}` : "根目录";
  $("folderPath").textContent = `${location} · ${folder.items.length} 个项目`;

  if (!folder.items.length) {
    const empty = document.createElement("div");
    empty.className = "empty";
    empty.textContent = "此文件夹为空";
    container.append(empty);
    updateSelectionBar();
    return;
  }

  folder.items.forEach(item => {
    const row = document.createElement("div");
    row.className = "item";
    if (item.isFolder) {
      const main = createItemMain(item.name, `文件夹 · ${item.fileCount} 个文件 · ${formatBytes(item.size)}`, true);
      main.addEventListener("click", () => loadFolder(folder.folderId, item.relativePath));
      const action = document.createElement("button");
      action.type = "button";
      action.className = "item-action";
      action.textContent = "打开";
      action.addEventListener("click", () => loadFolder(folder.folderId, item.relativePath));
      row.append(main, action);
    } else {
      const label = document.createElement("label");
      label.className = "file-select";
      const checkbox = document.createElement("input");
      checkbox.type = "checkbox";
      checkbox.checked = state.downloadSelection.has(downloadKey(folder.folderId, item.relativePath));
      checkbox.addEventListener("change", () => toggleDownloadSelection(folder.folderId, item, checkbox.checked));
      const main = createItemMain(item.name, formatBytes(item.size), false);
      label.append(checkbox, main);
      const action = document.createElement("a");
      action.className = "item-action";
      action.href = folderFileUrl(folder.folderId, item.relativePath);
      action.textContent = "下载";
      action.setAttribute("aria-label", `下载文件 ${item.name}`);
      row.append(label, action);
    }
    container.append(row);
  });
  updateSelectionBar();
}

function closeFolderBrowser() {
  state.folder = null;
  state.folderRootName = "";
  state.downloadSelection.clear();
  $("folderToolbar").hidden = true;
  $("selectionBar").hidden = true;
  $("archiveStatus").hidden = true;
  $("downloadList").replaceChildren();
  $("downloadList").hidden = true;
}

function createItemMain(nameText, metaText, interactive) {
  const main = document.createElement(interactive ? "button" : "div");
  main.className = "item-main";
  if (interactive) {
    main.type = "button";
    main.classList.add("item-open");
  }
  const name = document.createElement("div");
  name.className = "item-name";
  name.textContent = nameText;
  const meta = document.createElement("div");
  meta.className = "item-meta";
  meta.textContent = metaText;
  main.append(name, meta);
  return main;
}

function downloadKey(folderId, relativePath) {
  return `${folderId}|${relativePath}`;
}

function folderFileUrl(folderId, relativePath) {
  return `/api/folders/${folderId}/file?path=${encodeURIComponent(relativePath)}`;
}

function toggleDownloadSelection(folderId, item, selected) {
  const key = downloadKey(folderId, item.relativePath);
  if (selected) {
    state.downloadSelection.set(key, {
      name: item.name,
      url: folderFileUrl(folderId, item.relativePath)
    });
  } else {
    state.downloadSelection.delete(key);
  }
  updateSelectionBar();
}

function updateSelectionBar() {
  const count = state.downloadSelection.size;
  $("selectionBar").hidden = count === 0;
  $("selectedCount").textContent = `已选择 ${count} 个文件`;
  $("downloadSelected").textContent = `下载所选 (${count})`;
  if (!state.folder) $("selectionBar").hidden = true;

  if (state.folder) {
    const files = state.folder.items.filter(item => !item.isFolder);
    const allSelected = files.length > 0 && files.every(item =>
      state.downloadSelection.has(downloadKey(state.folder.folderId, item.relativePath)));
    $("selectAll").textContent = allSelected ? "取消全选" : "全选";
    $("selectAll").disabled = files.length === 0;
  }
}

$("folderBack").addEventListener("click", () => {
  if (!state.folder) return;
  if (!state.folder.path) {
    closeFolderBrowser();
    return;
  }
  const parts = state.folder.path.split("/");
  parts.pop();
  loadFolder(state.folder.folderId, parts.join("/"));
});

$("selectAll").addEventListener("click", () => {
  if (!state.folder) return;
  const files = state.folder.items.filter(item => !item.isFolder);
  const allSelected = files.length > 0 && files.every(item =>
    state.downloadSelection.has(downloadKey(state.folder.folderId, item.relativePath)));
  files.forEach(item => toggleDownloadSelection(state.folder.folderId, item, !allSelected));
  renderFolder();
});

$("downloadSelected").addEventListener("click", () => {
  const downloads = Array.from(state.downloadSelection.values());
  downloads.forEach(download => triggerDownload(download.url, download.name));
  $("selectedCount").textContent = `已提交 ${downloads.length} 个下载`;
});

function incompleteDownloads(transfer) {
  if (Array.isArray(transfer.downloads) && transfer.downloads.length)
    return transfer.downloads.filter(item => !item.completed);
  return transfer.downloadUrl
    ? [{ name: transfer.name, downloadUrl: transfer.downloadUrl, completed: false }]
    : [];
}

function triggerDownloads(downloads) {
  downloads
    .filter(download => !download.completed && download.downloadUrl)
    .forEach(download => triggerDownload(download.downloadUrl, download.name));
  scheduleTransferPoll(500);
}

function triggerDownload(url, name) {
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = name || "";
  anchor.hidden = true;
  document.body.append(anchor);
  anchor.click();
  anchor.remove();
}

function showArchiveError(message) {
  $("archiveStatus").hidden = false;
  $("archiveName").textContent = "操作失败";
  $("archiveState").textContent = "";
  $("archiveProgress").style.width = "0";
  $("archiveDetail").textContent = message;
}

setInterval(() => {
  if ($("receiveView").hidden) return;
  if (state.folder) loadFolder(state.folder.folderId, state.folder.path);
}, 4000);
setInterval(sendPresence, 4000);
initialize();
