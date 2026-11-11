using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using Serilog;

namespace STool.Modules.LanTransfer;

/// <summary>
/// 局域网传输 HTTP 服务：生命周期、请求分发与访问控制。
/// 各接口的处理、发送任务模型与 HTTP 辅助方法分别见同名 partial 文件。
/// </summary>
internal sealed partial class LanTransferServer : IAsyncDisposable
{
    private const int FileTransferBufferSize = 512 * 1024;
    private const int ProgressReportBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(100);
    // 任务完成后保留下载地址的宽限期:手机下载管理器会在首个请求取完小文件后
    // 再发起并行/重试请求,立即失效会让它们收到 401 并把整个下载判为失败。
    private static readonly TimeSpan CompletedDownloadGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TerminalSessionGrace = TimeSpan.FromSeconds(2);
    private const int MaxParallelRangeRequests = 12;

    /// <summary>请求被取消（非服务关闭）时返回的状态码，沿用常见的“客户端关闭请求”499。</summary>
    internal const int ClientCanceledStatusCode = 499;

    /// <summary>读写在该时间内没有任何字节进展就断开请求并释放传输名额。</summary>
    internal TimeSpan TransferIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>等待传输名额的最长时间，超时返回 503 + Retry-After。</summary>
    internal TimeSpan TransferSlotWaitTimeout { get; set; } = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    // 监听前缀是通配地址，网络切换后只需更新对外显示的地址与来源网段校验，无需重新绑定。
    private volatile NetworkEndpoint _endpoint;
    private readonly int _port;
    private readonly SharedFileCatalog _sharedFiles;
    private readonly LanTransferAuthService _auth;
    private readonly UploadCoordinator _uploads;
    private readonly PreparedArchiveManager _archives;
    private readonly TransferSessionManager _sessions = new();
    private readonly DownloadProgressTracker _downloadProgress = new();
    private readonly ConcurrentDictionary<string, OutgoingTransfer> _outgoing = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ArchivePayloadReference> _archivePayloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _transferGate;
    private readonly SemaphoreSlim _rangeTransferGate = new(MaxParallelRangeRequests);
    private readonly ConcurrentDictionary<int, Task> _requests = new();
    private readonly string _listenerHost;
    private HttpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptLoop;
    private int _requestId;

    /// <param name="listenerHost">监听前缀的主机部分。正式运行用 "+" 接收局域网访问；测试用 "localhost"，无需 URL ACL。</param>
    public LanTransferServer(
        NetworkEndpoint endpoint,
        int port,
        string receiveDirectory,
        int maxConcurrentTransfers,
        SharedFileCatalog sharedFiles,
        DeviceTokenStore deviceTokenStore,
        string listenerHost = "+")
    {
        _endpoint = endpoint;
        _port = port;
        _listenerHost = listenerHost;
        _sharedFiles = sharedFiles;
        _auth = new LanTransferAuthService(deviceTokenStore, autoRotate: true);
        _uploads = new UploadCoordinator(receiveDirectory, _sessions);
        _archives = new PreparedArchiveManager(receiveDirectory);
        _transferGate = new SemaphoreSlim(Math.Clamp(maxConcurrentTransfers, 1, 4));
        _uploads.BatchReceived += info => BatchReceived?.Invoke(info);
        _archives.ProgressChanged += Archives_ProgressChanged;
        _sessions.Changed += Sessions_Changed;
        _auth.AccessCodesRotated += () => AccessCodesRotated?.Invoke();
    }

    public event Action<ReceivedBatchInfo>? BatchReceived;
    public event Action<TransferSessionSnapshot>? TransferChanged;
    public event Action<TransferHistoryEntry>? OutgoingItemCompleted;
    public event Action<LanTransferServer>? ClientActivity;

    /// <summary>二维码与验证码已更换（配对成功或触发限流后自动更换）。可能在后台线程触发。</summary>
    public event Action? AccessCodesRotated;

    public string Address => $"http://{_endpoint.Address}:{_port}/";
    public NetworkEndpoint Endpoint => _endpoint;
    public string QrAddress => $"{Address}#token={Uri.EscapeDataString(_auth.QrToken)}";
    public string ManualCode => _auth.ManualCode;
    public int ActiveTransferCount => _sessions.ActiveCount;
    public bool IsRunning => _listener?.IsListening == true;

    public Task StartAsync()
    {
        if (IsRunning)
            return Task.CompletedTask;

        _lifetime = new CancellationTokenSource();
        _listener = new HttpListener { IgnoreWriteExceptions = false };
        _listener.Prefixes.Add($"http://{_listenerHost}:{_port}/");
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_lifetime.Token);
        Log.Information("LAN transfer listening at {Address} via {Interface}", Address, _endpoint.InterfaceName);
        return Task.CompletedTask;
    }

    /// <summary>网络地址变化后更新对外地址与允许的来源网段；不影响进行中的传输。</summary>
    public void UpdateEndpoint(NetworkEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoint = endpoint;
        Log.Information("LAN transfer endpoint updated to {Address} via {Interface}", Address, endpoint.InterfaceName);
    }

    public void RotateAccessCodes() => _auth.RotateAccessCodes();

    /// <summary>移除所有已记住的设备，并让现有在线会话立即失效。</summary>
    public void RevokeRememberedDevices() => _auth.RevokeRememberedDevices();

    /// <summary>当前验证码的到期时间（UTC）。</summary>
    public DateTimeOffset AccessCodesExpireUtc => _auth.CodesExpireUtc;

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener?.IsListening == true)
        {
            HttpListenerContext context;
            try
            {
                // DisposeAsync stops the listener to release this await. Do not abandon
                // the underlying accept task with WaitAsync when cancellation wins.
                context = await _listener.GetContextAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            var id = Interlocked.Increment(ref _requestId);
            var task = HandleContextSafeAsync(context, cancellationToken);
            _requests[id] = task;
            _ = task.ContinueWith(completedTask => _requests.TryRemove(id, out var _), TaskScheduler.Default);
        }
    }

    private async Task HandleContextSafeAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath ?? "/";
        var remote = request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
        var diagnosticRequest = path == "/" || path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase) ||
            path is "/api/session" or "/api/auth/exchange";
        var stopwatch = Stopwatch.StartNew();
        if (diagnosticRequest)
            Log.Information("LAN transfer request method={Method} path={Path} remote={Remote}", request.HttpMethod, path, remote);

        try
        {
            if (!IsRemoteAllowed(request))
            {
                Log.Warning("LAN transfer request rejected remote={Remote} path={Path}", remote, path);
                await WriteErrorAsync(context.Response, 403, "当前设备不在允许的局域网中。 ", cancellationToken);
                return;
            }

            // 只接受以本机地址访问的请求，防止 DNS 重绑定让外部网页借浏览器访问本服务。
            if (!IsAllowedHost(request.Url?.Host))
            {
                Log.Warning("LAN transfer request rejected host={Host} remote={Remote}", request.Url?.Host, remote);
                await WriteErrorAsync(context.Response, 421, "请求地址无效，请使用电脑上显示的地址访问。 ", cancellationToken);
                return;
            }

            await RouteAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 服务关闭导致的取消：服务不可用，客户端可稍后重连。
            context.Response.StatusCode = 503;
        }
        catch (OperationCanceledException)
        {
            // 用户在电脑上取消/暂停任务，或客户端断开：不能返回 2xx，否则网页会把未完成的分块当作成功。
            Log.Information("LAN transfer request canceled method={Method} path={Path} remote={Remote}",
                request.HttpMethod, path, remote);
            await TryWriteErrorAsync(context.Response, ClientCanceledStatusCode, "传输已取消或暂停。 ");
        }
        catch (TimeoutException ex)
        {
            // 读写长时间没有任何字节进展（手机息屏等半开连接）：直接断开连接，释放传输名额。
            Log.Warning(ex, "LAN transfer request timed out without progress method={Method} path={Path} remote={Remote}",
                request.HttpMethod, path, remote);
            try { context.Response.Abort(); }
            catch (Exception abortEx) { Log.Debug(abortEx, "Failed to abort LAN response path={Path}", path); }
        }
        catch (TransferBusyException ex)
        {
            context.Response.Headers["Retry-After"] = ex.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await WriteErrorAsync(context.Response, 503, ex.Message, cancellationToken);
        }
        catch (HttpListenerException ex) when (ex.ErrorCode is 64 or 995 or 1229)
        {
            Log.Information("LAN transfer client disconnected method={Method} path={Path} remote={Remote} code={Code}",
                request.HttpMethod, path, remote, ex.ErrorCode);
        }
        catch (UploadOffsetMismatchException ex)
        {
            context.Response.StatusCode = 409;
            context.Response.Headers["Upload-Offset"] = ex.ExpectedOffset.ToString();
            await WriteErrorAsync(context.Response, 409, "上传位置不一致，请从服务端断点继续。 ", cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            await WriteErrorAsync(context.Response, 404, ex.Message, cancellationToken);
        }
        catch (LanAuthException ex)
        {
            await WriteErrorAsync(context.Response, 401, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is LanRequestForbiddenException or TransferOwnershipException)
        {
            await WriteErrorAsync(context.Response, 403, ex.Message, cancellationToken);
        }
        catch (UnauthorizedAccessException ex)
        {
            // 本机文件系统权限不足，不是认证问题：返回 500，避免网页误以为登录失效。
            Log.Warning(ex, "LAN transfer file access denied method={Method} path={Path}",
                request.HttpMethod, path);
            await WriteErrorAsync(context.Response, 500, "电脑上的文件或接收目录没有访问权限。 ", cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            await WriteErrorAsync(context.Response, 400, ex.Message, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LAN transfer request failed method={Method} path={Path}",
                context.Request.HttpMethod, context.Request.Url?.AbsolutePath);
            await WriteErrorAsync(context.Response, 500, "请求处理失败。 ", cancellationToken);
        }
        finally
        {
            if (diagnosticRequest || context.Response.StatusCode >= 400)
                Log.Information("LAN transfer response method={Method} path={Path} remote={Remote} status={StatusCode} elapsedMs={ElapsedMs}",
                    request.HttpMethod, path, remote, context.Response.StatusCode, stopwatch.ElapsedMilliseconds);
            try { context.Response.Close(); }
            catch (Exception ex) { Log.Debug(ex, "Failed to close LAN response path={Path}", path); }
        }
    }

    private async Task RouteAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath ?? "/";

        if (request.HttpMethod == "GET" && path == "/")
        {
            await WriteAssetAsync(context.Response, "index.html", "text/html; charset=utf-8", cancellationToken);
            return;
        }
        if (request.HttpMethod == "GET" && path == "/assets/style.css")
        {
            await WriteAssetAsync(context.Response, "style.css", "text/css; charset=utf-8", cancellationToken);
            return;
        }
        if (request.HttpMethod == "GET" && path == "/assets/app.js")
        {
            await WriteAssetAsync(context.Response, "app.js", "text/javascript; charset=utf-8", cancellationToken);
            return;
        }
        if (path == "/favicon.ico")
        {
            context.Response.StatusCode = 204;
            return;
        }

        if (path == "/api/session" && request.HttpMethod == "GET")
        {
            await HandleSessionAsync(context, cancellationToken);
            return;
        }
        if (path == "/api/auth/exchange" && request.HttpMethod == "POST")
        {
            EnsureStateChangingRequest(request);
            await HandleAuthExchangeAsync(context, cancellationToken);
            return;
        }
        if (path == "/api/auth/logout" && request.HttpMethod == "POST")
        {
            EnsureStateChangingRequest(request);
            _auth.RemoveSession(GetCookie(request, LanTransferAuthService.SessionCookieName));
            ExpireCookie(context.Response, LanTransferAuthService.SessionCookieName, SessionCookiePath);
            ExpireCookie(context.Response, LanTransferAuthService.DeviceCookieName, DeviceCookiePath);
            ExpireCookie(context.Response, LanTransferAuthService.DeviceCookieName, SessionCookiePath);
            await WriteJsonAsync(context.Response, new { ok = true }, cancellationToken);
            return;
        }

        EnsureAuthenticated(context);

        if (path == "/api/presence" && request.HttpMethod == "GET")
        {
            await WriteJsonAsync(context.Response, new { connected = true }, cancellationToken);
            return;
        }

        if (path == "/api/transfers" && request.HttpMethod == "GET")
        {
            await HandleTransferListAsync(context, cancellationToken);
            return;
        }

        if (path.StartsWith("/api/transfers/", StringComparison.OrdinalIgnoreCase))
        {
            await HandleTransferAsync(context, path, cancellationToken);
            return;
        }

        if (path == "/api/files" && request.HttpMethod == "GET")
        {
            var activeSourceIds = _outgoing.Values
                .SelectMany(transfer => transfer.SourceIds)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = _sharedFiles.Snapshot()
                .Where(item => item.IsAvailable && !activeSourceIds.Contains(item.Id))
                .Select(item => item.ToDto());
            await WriteJsonAsync(context.Response, new { files = items }, cancellationToken);
            return;
        }

        if (path.StartsWith("/api/folders/", StringComparison.OrdinalIgnoreCase) &&
            request.HttpMethod is "GET" or "HEAD")
        {
            await HandleFolderAsync(context, path, cancellationToken);
            return;
        }

        if (path == "/api/archives" && request.HttpMethod == "POST")
        {
            EnsureStateChangingRequest(request);
            var archiveRequest = await ReadJsonAsync<ArchiveCreateRequest>(request, cancellationToken)
                ?? new ArchiveCreateRequest(null);
            var result = BeginArchive(archiveRequest.FileId, GetRequestOwner(request));
            context.Response.StatusCode = result.Status == ArchiveStatusNames.Ready ? 200 : 202;
            await WriteJsonAsync(context.Response, result, cancellationToken);
            return;
        }

        if (path.StartsWith("/api/archives/", StringComparison.OrdinalIgnoreCase) &&
            request.HttpMethod is "GET" or "HEAD")
        {
            await HandleArchiveAsync(context, path, cancellationToken);
            return;
        }

        if (path == "/api/uploads" && request.HttpMethod == "POST")
        {
            EnsureStateChangingRequest(request);
            var uploadRequest = await ReadJsonAsync<UploadInitRequest>(request, cancellationToken)
                ?? throw new InvalidDataException("上传信息无效。 ");
            if (string.IsNullOrWhiteSpace(uploadRequest.BatchId))
                throw new InvalidDataException("请先创建上传批次。 ");
            EnsureUploadOwner(request, uploadRequest.BatchId);
            var result = await _uploads.BeginAsync(uploadRequest, cancellationToken);
            context.Response.StatusCode = 201;
            await WriteJsonAsync(context.Response,
                new UploadInitResponse(result.Id, UploadCoordinator.ChunkSize, result.Offset, result.Name, result.Complete),
                cancellationToken);
            return;
        }

        if (path == "/api/upload-batches" && request.HttpMethod == "POST")
        {
            EnsureStateChangingRequest(request);
            await HandleUploadBatchCreateAsync(context, cancellationToken);
            return;
        }

        if (path.StartsWith("/api/uploads/", StringComparison.OrdinalIgnoreCase))
        {
            var id = path["/api/uploads/".Length..];
            await HandleUploadAsync(context, id, cancellationToken);
            return;
        }

        if (path.StartsWith("/api/download/", StringComparison.OrdinalIgnoreCase) &&
            request.HttpMethod is "GET" or "HEAD")
        {
            var id = path["/api/download/".Length..];
            if (!_sharedFiles.TryGet(id, out var entry) || entry == null || !entry.IsAvailable)
                throw new FileNotFoundException("共享文件不存在或已被移除。 ");
            if (entry.IsFolder)
                throw new InvalidDataException("请打开文件夹选择文件下载，或使用顶部的下载全部。 ");
            var transfer = QueueOutgoing(
                [entry],
                offerRequired: false,
                ownerToken: GetRequestOwner(request));
            RedirectToTransferDownload(context.Response, transfer.Id);
            return;
        }

        await WriteErrorAsync(context.Response, 404, "请求地址不存在。 ", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime?.Cancel();
        if (_listener != null)
        {
            try { _listener.Stop(); }
            catch (Exception ex) { Log.Debug(ex, "LAN listener stop failed during disposal"); }
            _listener.Close();
            _listener = null;
        }

        if (_acceptLoop != null)
        {
            try { await _acceptLoop; }
            catch (Exception ex) { Log.Debug(ex, "LAN accept loop ended with an error during disposal"); }
            _acceptLoop = null;
        }

        var requests = _requests.Values.ToArray();
        if (requests.Length > 0)
        {
            try { await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException ex) { Log.Warning(ex, "LAN requests did not stop within the disposal timeout"); }
            catch (Exception ex) { Log.Debug(ex, "LAN requests ended with an error during disposal"); }
        }

        _auth.Dispose();
        _auth.Clear();
        _downloadProgress.Clear();
        _archives.ProgressChanged -= Archives_ProgressChanged;
        _sessions.Changed -= Sessions_Changed;
        await _archives.DisposeAsync();
        await _uploads.DisposeAsync();
        await _sessions.DisposeAsync();
        _transferGate.Dispose();
        _rangeTransferGate.Dispose();
        _lifetime?.Dispose();
        _lifetime = null;
        Log.Information("LAN transfer stopped");
    }
}
