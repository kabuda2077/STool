using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace STool.Modules.LanTransfer;

internal sealed class LanTransferServer : IAsyncDisposable
{
    private const int FileTransferBufferSize = 512 * 1024;
    private const int ProgressReportBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(100);
    // 任务完成后保留下载地址的宽限期:手机下载管理器会在首个请求取完小文件后
    // 再发起并行/重试请求,立即失效会让它们收到 401 并把整个下载判为失败。
    private static readonly TimeSpan CompletedDownloadGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TerminalSessionGrace = TimeSpan.FromSeconds(2);
    private const int MaxParallelRangeRequests = 12;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly NetworkEndpoint _endpoint;
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
    private HttpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptLoop;
    private int _requestId;

    public LanTransferServer(
        NetworkEndpoint endpoint,
        int port,
        string receiveDirectory,
        int maxConcurrentTransfers,
        SharedFileCatalog sharedFiles,
        DeviceTokenStore deviceTokenStore)
    {
        _endpoint = endpoint;
        _port = port;
        _sharedFiles = sharedFiles;
        _auth = new LanTransferAuthService(deviceTokenStore);
        _uploads = new UploadCoordinator(receiveDirectory, _sessions);
        _archives = new PreparedArchiveManager(receiveDirectory);
        _transferGate = new SemaphoreSlim(Math.Clamp(maxConcurrentTransfers, 1, 4));
        _uploads.FileReceived += info => FileReceived?.Invoke(info);
        _uploads.BatchReceived += info => BatchReceived?.Invoke(info);
        _uploads.ProgressChanged += info => ProgressChanged?.Invoke(info);
        _archives.ProgressChanged += Archives_ProgressChanged;
        _sessions.Changed += Sessions_Changed;
    }

    public event Action<ReceivedFileInfo>? FileReceived;
    public event Action<ReceivedBatchInfo>? BatchReceived;
    public event Action<TransferProgressInfo>? ProgressChanged;
    public event Action<TransferSessionSnapshot>? TransferChanged;
    public event Action<TransferHistoryEntry>? OutgoingItemCompleted;
    public event Action<LanTransferServer>? ClientActivity;

    public string Address => $"http://{_endpoint.Address}:{_port}/";
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
        _listener.Prefixes.Add($"http://+:{_port}/");
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_lifetime.Token);
        Log.Information("LAN transfer listening at {Address} via {Interface}", Address, _endpoint.InterfaceName);
        return Task.CompletedTask;
    }

    public void RotateAccessCodes() => _auth.RotateAccessCodes();

    public TransferSessionSnapshot QueueOutgoing(
        IReadOnlyList<SharedFileEntry> entries,
        OutgoingTransferMode mode = OutgoingTransferMode.Automatic,
        bool offerRequired = true,
        string? ownerToken = null)
    {
        if (entries.Count == 0)
            throw new InvalidDataException("没有可发送的项目。 ");

        var available = entries.Where(entry => entry.IsAvailable).ToArray();
        if (available.Length == 0)
            throw new FileNotFoundException("发送项目已被移动或删除。 ");

        var resolvedMode = mode == OutgoingTransferMode.Automatic
            ? available.Length == 1 && !available[0].IsFolder
                ? OutgoingTransferMode.Automatic
                : OutgoingTransferMode.CombinedArchive
            : mode;
        var separate = resolvedMode == OutgoingTransferMode.Separate;
        var needsArchive = resolvedMode == OutgoingTransferMode.CombinedArchive ||
            separate && available.Any(entry => entry.IsFolder);
        var name = resolvedMode switch
        {
            OutgoingTransferMode.Separate => $"{available.Length} 个项目",
            OutgoingTransferMode.CombinedArchive when available.Length == 1 => available[0].Name + ".zip",
            OutgoingTransferMode.CombinedArchive => $"STool发送-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            _ => available[0].Name
        };
        var sourcePaths = available.Select(entry => entry.FullPath).ToArray();
        var total = available.Aggregate(0L, (sum, entry) => AddSaturating(sum, entry.Size));
        var fileCount = available.Sum(entry => entry.FileCount);
        var location = GetCommonLocation(sourcePaths);
        var session = _sessions.CreateOutgoing(
            name,
            total,
            fileCount,
            sourcePaths,
            location,
            needsArchive);
        var payloads = CreateOutgoingPayloads(session.Id, available, resolvedMode, name);
        var outgoing = new OutgoingTransfer(
            session.Id,
            available.Select(entry => entry.Id).ToArray(),
            available,
            resolvedMode,
            payloads,
            offerRequired);
        _outgoing[session.Id] = outgoing;
        if (!string.IsNullOrWhiteSpace(ownerToken))
            _sessions.AssignOwner(session.Id, ownerToken);

        if (needsArchive)
        {
            ReportPreparationProgress(outgoing);
            foreach (var payload in payloads.Where(payload => payload.NeedsArchive))
                StartArchivePreparation(outgoing, payload);
        }
        return session;
    }

    public IReadOnlyList<TransferSessionSnapshot> GetTransfers() => _sessions.Snapshot();

    public bool PauseTransfer(string id) => _sessions.Pause(id);

    public bool ResumeTransfer(string id) => _sessions.Resume(id);

    public async Task<bool> CancelTransferAsync(
        string id,
        bool rejected = false,
        bool removeSources = true)
    {
        if (!_sessions.Cancel(id, rejected))
            return false;
        await CleanupCanceledTransferAsync(id, removeSources);
        return true;
    }

    public bool RetryTransfer(string id)
    {
        if (!_outgoing.TryGetValue(id, out var outgoing) || !_sessions.ResetForRetry(id))
            return false;
        outgoing.ResetForRetry();
        RemoveDownloadProgress(outgoing);
        if (outgoing.NeedsArchive)
        {
            ReportPreparationProgress(outgoing);
            foreach (var payload in outgoing.Payloads.Where(payload => payload.NeedsArchive))
                StartArchivePreparation(outgoing, payload);
        }
        else
            _sessions.SetAwaitingConfirmation(id, outgoing.DownloadSize);
        return true;
    }

    public async Task RemoveTransferAsync(string id)
    {
        await CleanupCanceledTransferAsync(id);
        _sessions.Remove(id);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener?.IsListening == true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
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

            await RouteAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.Response.StatusCode = 503;
        }
        catch (OperationCanceledException)
        {
            Log.Information("LAN transfer request canceled method={Method} path={Path} remote={Remote}",
                request.HttpMethod, path, remote);
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
        catch (UnauthorizedAccessException ex)
        {
            await WriteErrorAsync(context.Response, 401, ex.Message, cancellationToken);
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
            ExpireCookie(context.Response, LanTransferAuthService.SessionCookieName);
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
            var owner = GetRequestOwner(request);
            var transfers = _sessions.Snapshot()
                .Where(item => _sessions.CanControl(item.Id, owner))
                .Select(EnrichOutgoingSnapshot)
                .ToArray();
            var offer = transfers.FirstOrDefault(item =>
                item.Direction == TransferDirection.ToPhone &&
                item.State == TransferState.AwaitingConfirmation &&
                _outgoing.TryGetValue(item.Id, out var outgoing) && outgoing.OfferRequired);
            TransferOfferDto? offerDto = null;
            if (offer != null && _outgoing.TryGetValue(offer.Id, out var offeredTransfer))
            {
                var canBrowse = offeredTransfer.Entries.Count == 1 && offeredTransfer.Entries[0].IsFolder;
                offerDto = new TransferOfferDto(
                    offer.Id,
                    offer.Name,
                    offer.TotalBytes,
                    offer.FileCount,
                    offeredTransfer.Entries.Count,
                    offer.DownloadUrl,
                    canBrowse,
                    offeredTransfer.Mode == OutgoingTransferMode.Separate,
                    offeredTransfer.CreateDownloadDtos());
            }
            await WriteJsonAsync(context.Response, new { transfers, offer = offerDto }, cancellationToken);
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
            context.Response.StatusCode = result.Status == "ready" ? 200 : 202;
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
            var batchRequest = await ReadJsonAsync<UploadBatchCreateRequest>(request, cancellationToken)
                ?? throw new InvalidDataException("上传批次信息无效。 ");
            var id = Guid.NewGuid().ToString("N");
            _sessions.CreateIncoming(
                id,
                batchRequest.Name,
                batchRequest.TotalBytes,
                batchRequest.FileCount,
                GetRequestOwner(request));
            try
            {
                _uploads.BeginBatch(id, batchRequest);
            }
            catch
            {
                _sessions.Remove(id);
                throw;
            }
            context.Response.StatusCode = 201;
            await WriteJsonAsync(context.Response, new UploadBatchCreateResponse(id), cancellationToken);
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

    private async Task HandleSessionAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var valid = _auth.TryAuthenticate(
            GetCookie(request, LanTransferAuthService.SessionCookieName),
            GetCookie(request, LanTransferAuthService.DeviceCookieName),
            out var issuedSession);
        if (!valid)
        {
            await WriteErrorAsync(context.Response, 401, "需要连接认证。 ", cancellationToken);
            return;
        }

        if (issuedSession != null)
            SetCookie(context.Response, LanTransferAuthService.SessionCookieName, issuedSession, DateTimeOffset.UtcNow.AddHours(12));
        ClientActivity?.Invoke(this);
        await WriteJsonAsync(context.Response, new { authenticated = true }, cancellationToken);
    }

    private async Task HandleAuthExchangeAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = await ReadJsonAsync<AuthExchangeRequest>(context.Request, cancellationToken)
            ?? throw new InvalidDataException("认证信息无效。 ");
        var remote = context.Request.RemoteEndPoint?.Address ?? IPAddress.None;
        if (!_auth.TryExchange(request, remote, out var sessionToken, out var deviceToken))
        {
            await WriteErrorAsync(context.Response, 401, "二维码令牌或验证码无效。 ", cancellationToken);
            return;
        }

        SetCookie(context.Response, LanTransferAuthService.SessionCookieName, sessionToken, DateTimeOffset.UtcNow.AddHours(12));
        if (deviceToken != null)
            SetCookie(context.Response, LanTransferAuthService.DeviceCookieName, deviceToken, DateTimeOffset.UtcNow.AddDays(90));
        ClientActivity?.Invoke(this);
        await WriteJsonAsync(context.Response, new { authenticated = true }, cancellationToken);
    }

    private async Task HandleTransferAsync(
        HttpListenerContext context,
        string path,
        CancellationToken cancellationToken)
    {
        var remainder = path["/api/transfers/".Length..].Trim('/');
        var segments = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || !_sessions.TryGet(segments[0], out var snapshot) || snapshot == null)
            throw new FileNotFoundException("传输任务不存在或已结束。 ");

        var owner = GetRequestOwner(context.Request);
        if (segments.Length == 1 && context.Request.HttpMethod == "GET")
        {
            if (!_sessions.CanControl(snapshot.Id, owner))
                throw new UnauthorizedAccessException("该任务已由其他设备接收。 ");
            await WriteJsonAsync(context.Response, EnrichOutgoingSnapshot(snapshot), cancellationToken);
            return;
        }

        if (segments.Length == 2 && string.Equals(segments[1], "download", StringComparison.OrdinalIgnoreCase) &&
            context.Request.HttpMethod is "GET" or "HEAD")
        {
            if (!_sessions.TryClaim(snapshot.Id, owner))
                throw new UnauthorizedAccessException("该任务已由其他设备接收，或当前不可下载。 ");
            if (!_outgoing.TryGetValue(snapshot.Id, out var outgoing) ||
                outgoing.Payloads.Count != 1 || !outgoing.Payloads[0].TryGetDownload(out var downloadPath))
                throw new FileNotFoundException("下载内容尚未准备完成或已失效。 ");

            var gate = string.IsNullOrWhiteSpace(context.Request.Headers["Range"])
                ? _transferGate
                : _rangeTransferGate;
            await WithTransferSlotAsync(
                () => SendFileAsync(
                    context,
                    snapshot.Id,
                    outgoing.Payloads[0].Id,
                    outgoing.Payloads[0].Name,
                    downloadPath,
                    cancellationToken),
                cancellationToken,
                gate);
            return;
        }

        if (segments.Length == 3 && string.Equals(segments[1], "files", StringComparison.OrdinalIgnoreCase) &&
            context.Request.HttpMethod is "GET" or "HEAD")
        {
            if (!_sessions.TryClaim(snapshot.Id, owner))
                throw new UnauthorizedAccessException("该任务已由其他设备接收，或当前不可下载。 ");
            if (!_outgoing.TryGetValue(snapshot.Id, out var outgoing) ||
                outgoing.Mode != OutgoingTransferMode.Separate ||
                !outgoing.TryGetPayload(segments[2], out var payload) || payload == null ||
                !payload.TryGetDownload(out var downloadPath))
            {
                throw new FileNotFoundException("下载项目尚未准备完成或已失效。 ");
            }

            var gate = string.IsNullOrWhiteSpace(context.Request.Headers["Range"])
                ? _transferGate
                : _rangeTransferGate;
            await WithTransferSlotAsync(
                () => SendFileAsync(
                    context,
                    snapshot.Id,
                    payload.Id,
                    payload.Name,
                    downloadPath,
                    cancellationToken),
                cancellationToken,
                gate);
            return;
        }

        if (segments.Length != 2 || context.Request.HttpMethod != "POST")
        {
            await WriteErrorAsync(context.Response, 405, "不支持的传输操作。 ", cancellationToken);
            return;
        }

        EnsureStateChangingRequest(context.Request);
        if (!_sessions.CanControl(snapshot.Id, owner))
            throw new UnauthorizedAccessException("该任务已由其他设备接收。 ");

        if (string.Equals(segments[1], "browse", StringComparison.OrdinalIgnoreCase))
        {
            if (!_sessions.TryClaim(snapshot.Id, owner))
                throw new UnauthorizedAccessException("该任务已由其他设备接收，或当前不可浏览。 ");
            if (!_outgoing.TryGetValue(snapshot.Id, out var outgoing) ||
                outgoing.Entries.Count != 1 || !outgoing.Entries[0].IsFolder)
            {
                throw new InvalidDataException("当前发送任务不支持选择文件。 ");
            }

            var folder = outgoing.Entries[0];
            if (!await CancelTransferAsync(snapshot.Id, removeSources: false))
                throw new InvalidDataException("当前状态不能选择文件。 ");
            await WriteJsonAsync(
                context.Response,
                new FolderBrowseResponse(folder.Id, folder.Name),
                cancellationToken);
            return;
        }

        var changed = segments[1].ToLowerInvariant() switch
        {
            "pause" => PauseTransfer(snapshot.Id),
            "resume" => ResumeTransfer(snapshot.Id),
            "cancel" => await CancelTransferAsync(snapshot.Id),
            "reject" => await CancelTransferAsync(snapshot.Id, rejected: true),
            _ => false
        };
        if (!changed)
            throw new InvalidDataException("当前状态不能执行该操作。 ");
        await WriteJsonAsync(context.Response, new { ok = true }, cancellationToken);
    }

    private async Task HandleUploadAsync(HttpListenerContext context, string id, CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (request.HttpMethod == "HEAD")
        {
            if (!_uploads.TryGetStatus(id, out var status) || status == null)
                throw new FileNotFoundException("上传任务不存在。 ");
            context.Response.Headers["Upload-Offset"] = status.Offset.ToString();
            context.Response.Headers["Upload-Length"] = status.Size.ToString();
            context.Response.StatusCode = 204;
            return;
        }

        EnsureStateChangingRequest(request);
        if (request.HttpMethod == "DELETE")
        {
            await _uploads.CancelAsync(id);
            context.Response.StatusCode = 204;
            return;
        }
        if (request.HttpMethod != "PATCH")
        {
            await WriteErrorAsync(context.Response, 405, "不支持的上传操作。 ", cancellationToken);
            return;
        }

        if (!long.TryParse(request.Headers["Upload-Offset"], out var offset))
            throw new InvalidDataException("缺少 Upload-Offset。 ");

        _uploads.TryGetBatchId(id, out var batchId);
        await WithTransferSlotAsync(async () =>
        {
            if (batchId != null)
                _sessions.BeginRequest(batchId);
            try
            {
                var result = await _uploads.AppendAsync(
                    id, offset, request.InputStream, request.ContentLength64, cancellationToken);
                context.Response.Headers["Upload-Offset"] = result.Offset.ToString();
                await WriteJsonAsync(context.Response, result, cancellationToken);
            }
            finally
            {
                if (batchId != null)
                    _sessions.EndRequest(batchId);
            }
        }, cancellationToken);
    }

    private async Task HandleFolderAsync(
        HttpListenerContext context,
        string path,
        CancellationToken cancellationToken)
    {
        var remainder = path["/api/folders/".Length..].Trim('/');
        var segments = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new FileNotFoundException("共享文件夹不存在。 ");

        var folderId = segments[0];
        var requestedPath = context.Request.QueryString["path"];
        if (segments.Length == 1)
        {
            if (!_sharedFiles.TryGetFolderContents(folderId, requestedPath, out var contents) || contents == null)
                throw new FileNotFoundException("共享目录不存在或已被移除。 ");
            await WriteJsonAsync(context.Response, contents, cancellationToken);
            return;
        }

        if (segments.Length == 2 && string.Equals(segments[1], "file", StringComparison.OrdinalIgnoreCase))
        {
            if (!_sharedFiles.TryGetFolderFile(folderId, requestedPath, out var file) || file == null)
                throw new FileNotFoundException("共享文件不存在或已被移除。 ");
            var info = new FileInfo(file.FullPath);
            var entry = new SharedFileEntry
            {
                Id = folderId + ":" + file.RelativePath,
                Name = info.Name,
                RelativePath = file.RelativePath,
                FullPath = info.FullName,
                Size = info.Length,
                ModifiedUtc = info.LastWriteTimeUtc,
                IsFolder = false,
                FolderFiles = []
            };
            var transfer = QueueOutgoing(
                [entry],
                offerRequired: false,
                ownerToken: GetRequestOwner(context.Request));
            RedirectToTransferDownload(context.Response, transfer.Id);
            return;
        }

        throw new FileNotFoundException("共享目录地址不存在。 ");
    }

    private PreparedArchiveStatus BeginArchive(string? fileId, string ownerToken)
    {
        SharedFileEntry[] entries;
        if (string.IsNullOrWhiteSpace(fileId))
        {
            entries = _sharedFiles.Snapshot().Where(item => item.IsAvailable).ToArray();
            if (entries.Length == 0)
                throw new FileNotFoundException("当前没有可下载的共享文件。 ");
        }
        else
        {
            if (!_sharedFiles.TryGet(fileId, out var entry) || entry == null || !entry.IsFolder || !entry.IsAvailable)
                throw new FileNotFoundException("共享文件夹不存在或已被移除。 ");
            entries = [entry];
        }
        return ToArchiveStatus(QueueOutgoing(entries, offerRequired: false, ownerToken: ownerToken));
    }

    private static IReadOnlyList<OutgoingPayload> CreateOutgoingPayloads(
        string transferId,
        IReadOnlyList<SharedFileEntry> entries,
        OutgoingTransferMode mode,
        string transferName)
    {
        if (mode == OutgoingTransferMode.Separate)
        {
            return entries.Select(entry => new OutgoingPayload(
                Guid.NewGuid().ToString("N"),
                transferId,
                entry.IsFolder ? entry.Name + ".zip" : entry.Name,
                [entry],
                entry.IsFolder,
                entry.IsFolder ? null : entry.FullPath,
                entry.IsFolder ? 0 : entry.Size)).ToArray();
        }

        var archive = mode == OutgoingTransferMode.CombinedArchive;
        return
        [
            new OutgoingPayload(
                Guid.NewGuid().ToString("N"),
                transferId,
                transferName,
                entries,
                archive,
                archive ? null : entries[0].FullPath,
                archive ? 0 : entries[0].Size)
        ];
    }

    private TransferSessionSnapshot EnrichOutgoingSnapshot(TransferSessionSnapshot snapshot)
    {
        if (!_outgoing.TryGetValue(snapshot.Id, out var outgoing))
            return snapshot;
        return snapshot with
        {
            DownloadUrl = outgoing.Payloads.Count == 1
                ? $"/api/transfers/{snapshot.Id}/download"
                : null,
            Downloads = outgoing.CreateDownloadDtos()
        };
    }

    private void StartArchivePreparation(OutgoingTransfer outgoing, OutgoingPayload payload)
    {
        var sourceKey = "transfer:" + outgoing.Id + ":" + payload.Id + ":" +
            string.Join('|', payload.Entries.Select(ArchiveEntryVersion));
        var items = CreateArchiveItems(payload.Entries).ToArray();
        _archivePayloads[payload.ArchiveJobId] = new ArchivePayloadReference(outgoing.Id, payload.Id);
        _archives.Begin(
            sourceKey,
            payload.Name,
            items,
            payload.ArchiveJobId,
            _sessions.GetPauseGate(outgoing.Id),
            _sessions.GetCancellationToken(outgoing.Id));
    }

    private void Archives_ProgressChanged(TransferProgressInfo info)
    {
        try
        {
            if (!_archivePayloads.TryGetValue(info.Id, out var reference) ||
                !_outgoing.TryGetValue(reference.TransferId, out var outgoing) ||
                !outgoing.TryGetPayload(reference.PayloadId, out var payload) || payload == null)
            {
                return;
            }

            if (string.Equals(info.Status, "准备中", StringComparison.Ordinal))
            {
                payload.SetPreparationProgress(info.Transferred, info.Total);
                ReportPreparationProgress(outgoing);
                return;
            }
            if (string.Equals(info.Status, "准备完成", StringComparison.Ordinal))
            {
                if (_archives.TryGetDownload(info.Id, out var archive) && archive != null)
                {
                    payload.SetPrepared(archive.FullPath, new FileInfo(archive.FullPath).Length);
                    if (outgoing.IsReady)
                        _sessions.SetAwaitingConfirmation(outgoing.Id, outgoing.DownloadSize);
                    else
                        ReportPreparationProgress(outgoing);
                }
                return;
            }
            if (string.Equals(info.Status, "准备失败", StringComparison.Ordinal))
            {
                var message = _archives.TryGetStatus(info.Id, out var status) && status != null
                    ? status.Error ?? "下载内容准备失败。"
                    : "下载内容准备失败。";
                _sessions.Fail(outgoing.Id, message);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to apply archive transfer state id={TransferId}", info.Id);
        }
    }

    private void ReportPreparationProgress(OutgoingTransfer outgoing)
    {
        var (processed, total) = outgoing.GetPreparationProgress();
        _sessions.SetPreparingProgress(outgoing.Id, processed, total);
    }

    private void Sessions_Changed(TransferSessionSnapshot snapshot)
    {
        TransferChanged?.Invoke(snapshot);
        if (snapshot.State == TransferState.Completed)
        {
            _downloadProgress.Remove(snapshot.Id);
            // 先只把源项从待发送列表移除;下载载荷保留到会话清理为止,
            // 以便客户端下载管理器在最后一个字节之后发起的并行/重试请求仍能读到同一内容。
            if (_outgoing.TryGetValue(snapshot.Id, out var outgoing))
            {
                foreach (var sourceId in outgoing.SourceIds)
                    _sharedFiles.Remove(sourceId);
            }
            ScheduleSessionRemoval(snapshot.Id, CompletedDownloadGrace);
        }
        else if (snapshot.State is TransferState.Canceled or TransferState.Rejected)
        {
            _downloadProgress.Remove(snapshot.Id);
            ScheduleSessionRemoval(snapshot.Id, TerminalSessionGrace);
        }
    }

    private async Task CleanupCanceledTransferAsync(string id, bool removeSources = true)
    {
        await _uploads.CancelBatchAsync(id);
        if (_outgoing.TryRemove(id, out var outgoing))
        {
            RemoveDownloadProgress(outgoing);
            foreach (var payload in outgoing.Payloads.Where(payload => payload.NeedsArchive))
            {
                _archivePayloads.TryRemove(payload.ArchiveJobId, out _);
                await _archives.CancelAsync(payload.ArchiveJobId);
            }
            if (removeSources)
            {
                foreach (var sourceId in outgoing.SourceIds)
                    _sharedFiles.Remove(sourceId);
            }
        }
    }

    /// <summary>宽限期结束后释放已完成任务的下载载荷和临时归档。</summary>
    private void ReleaseCompletedOutgoing(string id)
    {
        if (!_outgoing.TryRemove(id, out var outgoing))
            return;

        RemoveDownloadProgress(outgoing);
        foreach (var sourceId in outgoing.SourceIds)
            _sharedFiles.Remove(sourceId);
        foreach (var payload in outgoing.Payloads.Where(payload => payload.NeedsArchive))
        {
            _archivePayloads.TryRemove(payload.ArchiveJobId, out _);
            _archives.MarkDownloaded(payload.ArchiveJobId);
        }
    }

    private void ScheduleSessionRemoval(string id, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _lifetime?.Token ?? CancellationToken.None);
                ReleaseCompletedOutgoing(id);
                _sessions.Remove(id);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private static PreparedArchiveStatus ToArchiveStatus(TransferSessionSnapshot snapshot)
    {
        var status = snapshot.State switch
        {
            TransferState.Preparing or TransferState.Paused when snapshot.TransferredBytes < snapshot.TotalBytes => "preparing",
            TransferState.Failed or TransferState.Canceled or TransferState.Rejected => "failed",
            _ => "ready"
        };
        return new PreparedArchiveStatus(
            snapshot.Id,
            snapshot.Name,
            status,
            snapshot.TransferredBytes,
            snapshot.TotalBytes,
            status == "ready" ? snapshot.TotalBytes : 0,
            snapshot.FileCount,
            snapshot.Error,
            status == "ready" ? $"/api/transfers/{snapshot.Id}/download" : null);
    }

    private async Task HandleArchiveAsync(
        HttpListenerContext context,
        string path,
        CancellationToken cancellationToken)
    {
        var remainder = path["/api/archives/".Length..].Trim('/');
        var segments = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1)
        {
            if (!_sessions.TryGet(segments[0], out var snapshot) || snapshot == null)
                throw new FileNotFoundException("下载全部任务不存在或已过期。 ");
            await WriteJsonAsync(context.Response, ToArchiveStatus(snapshot), cancellationToken);
            return;
        }

        if (segments.Length == 2 && string.Equals(segments[1], "download", StringComparison.OrdinalIgnoreCase))
        {
            RedirectToTransferDownload(context.Response, segments[0]);
            return;
        }

        throw new FileNotFoundException("下载地址不存在。 ");
    }

    private async Task SendFileAsync(
        HttpListenerContext context,
        string transferId,
        string payloadId,
        string fileName,
        string fullPath,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        var info = new FileInfo(fullPath);
        var length = info.Length;
        var start = 0L;
        var end = length == 0 ? -1 : length - 1;
        var partial = false;

        var range = request.Headers["Range"];
        if (!string.IsNullOrWhiteSpace(range))
        {
            if (!TryParseRange(range, length, out start, out end))
            {
                response.StatusCode = 416;
                response.Headers["Content-Range"] = $"bytes */{length}";
                return;
            }
            partial = true;
        }

        response.StatusCode = partial ? 206 : 200;
        response.ContentType = GetContentType(info.Extension);
        response.Headers["Accept-Ranges"] = "bytes";
        response.Headers["Content-Disposition"] = BuildContentDisposition(fileName);
        response.ContentLength64 = Math.Max(0, end - start + 1);
        if (partial)
            response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
        if (request.HttpMethod == "HEAD")
            return;

        // 任务已结束时不再参与状态机:宽限期内的并行/重试请求只做只读发送,
        // 避免把已完成的任务反复拉回“传输中”或重算进度。
        var trackSession = _sessions.TryBeginRequest(transferId);
        try
        {
            if (length == 0)
            {
                if (trackSession)
                    ReportPayloadProgress(transferId, payloadId, 0, 0);
                return;
            }

            var remote = request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
            var trackerId = GetPayloadTrackerId(transferId, payloadId);
            var progressKey = _downloadProgress.Begin(trackerId, remote, length, start);
            var rangeStopwatch = Stopwatch.StartNew();
            using var linkedCancellation = trackSession
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _sessions.GetCancellationToken(transferId))
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var transferToken = linkedCancellation.Token;
            try
            {
                await using var input = new FileStream(
                    fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    FileTransferBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                input.Seek(start, SeekOrigin.Begin);
                var sent = await CopyRangeAsync(
                    input,
                    response.OutputStream,
                    progressKey,
                    start,
                    end - start + 1,
                    length,
                    transferToken,
                    transferId,
                    payloadId,
                    trackSession);
                var elapsedSeconds = Math.Max(0.001, rangeStopwatch.Elapsed.TotalSeconds);
                Log.Information(
                    "LAN transfer range completed fileId={FileId} start={Start} bytes={Bytes} elapsedMs={ElapsedMs} megabytesPerSec={MegabytesPerSec:F1} tracked={Tracked}",
                    transferId,
                    start,
                    sent,
                    rangeStopwatch.ElapsedMilliseconds,
                    sent / 1024d / 1024d / elapsedSeconds,
                    trackSession);
            }
            finally
            {
                _downloadProgress.End(progressKey);
            }
        }
        finally
        {
            if (trackSession)
                _sessions.EndRequest(transferId);
        }
    }

    private async Task<long> CopyRangeAsync(
        Stream input,
        Stream output,
        string progressKey,
        long rangeStart,
        long rangeLength,
        long totalLength,
        CancellationToken cancellationToken,
        string transferId,
        string payloadId,
        bool trackSession)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(FileTransferBufferSize);
        var remaining = rangeLength;
        var transferred = 0L;
        var pendingStart = rangeStart;
        var pendingBytes = 0L;
        var lastReportTimestamp = Stopwatch.GetTimestamp();

        void ReportProgress()
        {
            if (pendingBytes <= 0)
                return;

            var coveredBytes = _downloadProgress.Record(progressKey, pendingStart, pendingBytes);
            if (trackSession)
                ReportPayloadProgress(transferId, payloadId, coveredBytes, totalLength);
            pendingStart += pendingBytes;
            pendingBytes = 0;
            lastReportTimestamp = Stopwatch.GetTimestamp();
        }

        try
        {
            while (remaining > 0)
            {
                if (trackSession)
                    await _sessions.WaitIfPausedAsync(transferId, cancellationToken);
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken);
                if (read == 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
                transferred += read;
                pendingBytes += read;

                if (pendingBytes >= ProgressReportBytes ||
                    remaining == 0 ||
                    Stopwatch.GetElapsedTime(lastReportTimestamp) >= ProgressReportInterval)
                {
                    ReportProgress();
                }
            }

            return transferred;
        }
        finally
        {
            try
            {
                ReportProgress();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private void ReportPayloadProgress(string transferId, string payloadId, long coveredBytes, long totalLength)
    {
        if (!_outgoing.TryGetValue(transferId, out var outgoing) ||
            !outgoing.TryGetPayload(payloadId, out var payload) || payload == null)
        {
            return;
        }

        var newlyCompleted = payload.SetDownloadProgress(coveredBytes, totalLength);
        if (newlyCompleted && outgoing.Mode == OutgoingTransferMode.Separate)
            OutgoingItemCompleted?.Invoke(payload.CreateHistoryEntry(transferId));

        _sessions.ReportProgress(
            transferId,
            outgoing.TransferredBytes,
            outgoing.DownloadSize,
            outgoing.IsDownloadComplete);
    }

    private static string GetPayloadTrackerId(string transferId, string payloadId) =>
        transferId + ":" + payloadId;

    private void RemoveDownloadProgress(OutgoingTransfer outgoing)
    {
        foreach (var payload in outgoing.Payloads)
            _downloadProgress.Remove(GetPayloadTrackerId(outgoing.Id, payload.Id));
    }

    private static string ArchiveEntryVersion(SharedFileEntry entry) =>
        $"{entry.Id}:{entry.ModifiedUtc.UtcTicks}:{entry.Size}:{entry.FileCount}";

    private static IEnumerable<PreparedArchiveItem> CreateArchiveItems(IReadOnlyList<SharedFileEntry> entries)
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var rootName = CreateUniqueArchiveRoot(entry.Name, usedNames);
            if (!entry.IsFolder)
            {
                yield return new PreparedArchiveItem(entry.FullPath, rootName, entry.Size);
                continue;
            }

            if (entry.FolderFiles.Count == 0)
            {
                yield return new PreparedArchiveItem(null, rootName + "/", 0);
                continue;
            }

            foreach (var file in entry.FolderFiles)
            {
                yield return new PreparedArchiveItem(
                    file.FullPath,
                    Path.Combine(rootName, file.RelativePath),
                    file.Size);
            }
        }
    }

    private static string CreateUniqueArchiveRoot(string name, HashSet<string> usedNames)
    {
        if (usedNames.Add(name))
            return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{stem} ({suffix}){extension}";
            if (usedNames.Add(candidate))
                return candidate;
        }
    }

    private void EnsureAuthenticated(HttpListenerContext context)
    {
        var request = context.Request;
        var valid = _auth.TryAuthenticate(
            GetCookie(request, LanTransferAuthService.SessionCookieName),
            GetCookie(request, LanTransferAuthService.DeviceCookieName),
            out var issuedSession);
        if (!valid)
            throw new UnauthorizedAccessException("需要连接认证。 ");
        if (issuedSession != null)
            SetCookie(context.Response, LanTransferAuthService.SessionCookieName, issuedSession, DateTimeOffset.UtcNow.AddHours(12));
        ClientActivity?.Invoke(this);
    }

    private void EnsureStateChangingRequest(HttpListenerRequest request)
    {
        if (!string.Equals(request.Headers["X-STool-Request"], "1", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("请求来源无效。 ");

        var origin = request.Headers["Origin"];
        if (string.IsNullOrWhiteSpace(origin))
            return;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
            !string.Equals(originUri.Host, request.Url?.Host, StringComparison.OrdinalIgnoreCase) ||
            originUri.Port != _port)
        {
            throw new UnauthorizedAccessException("请求来源无效。 ");
        }
    }

    private bool IsRemoteAllowed(HttpListenerRequest request)
    {
        var remote = request.RemoteEndPoint?.Address;
        return remote != null && NetworkEndpointSelector.IsRemoteAllowed(_endpoint, remote);
    }

    private async Task WithTransferSlotAsync(
        Func<Task> action,
        CancellationToken cancellationToken,
        SemaphoreSlim? transferGate = null)
    {
        transferGate ??= _transferGate;
        await transferGate.WaitAsync(cancellationToken);
        try { await action(); }
        finally { transferGate.Release(); }
    }

    private static Task<T?> ReadJsonAsync<T>(HttpListenerRequest request, CancellationToken cancellationToken) =>
        ReadJsonAsync<T>(request.InputStream, request.ContentLength64, cancellationToken);

    internal static async Task<T?> ReadJsonAsync<T>(
        Stream stream,
        long contentLength,
        CancellationToken cancellationToken = default)
    {
        if (contentLength is < 0 or > 64 * 1024)
            throw new InvalidDataException("请求内容过大。 ");

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("请求内容格式无效。 ", ex);
        }
    }

    private static async Task WriteAssetAsync(HttpListenerResponse response, string fileName, string contentType, CancellationToken cancellationToken)
    {
        var content = LanTransferWebAssets.Read(fileName);
        if (content == null)
        {
            await WriteErrorAsync(response, 404, "网页资源不存在。 ", cancellationToken);
            return;
        }
        response.ContentType = contentType;
        response.ContentLength64 = content.Length;
        response.Headers["Cache-Control"] = "no-store";
        await response.OutputStream.WriteAsync(content, cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(HttpListenerResponse response, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
    }

    private static Task WriteErrorAsync(HttpListenerResponse response, int statusCode, string message, CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        return WriteJsonAsync(response, new { error = message }, cancellationToken);
    }

    private static string? GetCookie(HttpListenerRequest request, string name) => request.Cookies[name]?.Value;

    private static void SetCookie(HttpListenerResponse response, string name, string value, DateTimeOffset expires)
    {
        response.AppendCookie(new Cookie(name, value, "/")
        {
            HttpOnly = true,
            Expires = expires.UtcDateTime
        });
    }

    private static void ExpireCookie(HttpListenerResponse response, string name)
    {
        response.AppendCookie(new Cookie(name, string.Empty, "/")
        {
            HttpOnly = true,
            Expires = DateTime.UtcNow.AddDays(-1)
        });
    }

    internal static bool TryParseRange(string header, long length, out long start, out long end)
    {
        start = 0;
        end = Math.Max(0, length - 1);
        if (length <= 0 || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(','))
            return false;

        var parts = header[6..].Split('-', 2);
        if (parts.Length != 2)
            return false;

        if (string.IsNullOrWhiteSpace(parts[0]))
        {
            if (!long.TryParse(parts[1], out var suffixLength) || suffixLength <= 0)
                return false;
            suffixLength = Math.Min(suffixLength, length);
            start = length - suffixLength;
            end = length - 1;
            return true;
        }

        if (!long.TryParse(parts[0], out start) || start < 0 || start >= length)
            return false;
        if (string.IsNullOrWhiteSpace(parts[1]))
        {
            end = length - 1;
            return true;
        }
        return long.TryParse(parts[1], out end) && end >= start && end < length;
    }

    private static string BuildContentDisposition(string fileName)
    {
        var ascii = new string(fileName.Select(ch => ch is >= ' ' and <= '~' && ch != '"' ? ch : '_').ToArray());
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    private static string GetContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain; charset=utf-8",
        ".json" => "application/json",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static void RedirectToTransferDownload(HttpListenerResponse response, string transferId)
    {
        response.StatusCode = 302;
        response.RedirectLocation = $"/api/transfers/{transferId}/download";
    }

    private static string GetRequestOwner(HttpListenerRequest request) =>
        GetCookie(request, LanTransferAuthService.SessionCookieName) ??
        GetCookie(request, LanTransferAuthService.DeviceCookieName) ??
        request.RemoteEndPoint?.Address?.ToString() ?? "unknown";

    private static string GetCommonLocation(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1)
            return Path.GetFullPath(paths[0]);

        var directories = paths.Select(path =>
            Directory.Exists(path)
                ? Path.GetFullPath(path)
                : Path.GetDirectoryName(Path.GetFullPath(path)) ?? Path.GetPathRoot(path) ?? path).ToArray();
        var common = directories[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var directory in directories.Skip(1))
        {
            while (!directory.Equals(common, StringComparison.OrdinalIgnoreCase) &&
                   !directory.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(common);
                if (string.IsNullOrWhiteSpace(parent))
                    return Path.GetPathRoot(common) ?? common;
                common = parent;
            }
        }
        return common;
    }

    private static long AddSaturating(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

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

    private sealed class OutgoingTransfer(
        string id,
        IReadOnlyList<string> sourceIds,
        IReadOnlyList<SharedFileEntry> entries,
        OutgoingTransferMode mode,
        IReadOnlyList<OutgoingPayload> payloads,
        bool offerRequired)
    {
        public string Id { get; } = id;
        public IReadOnlyList<string> SourceIds { get; } = sourceIds;
        public IReadOnlyList<SharedFileEntry> Entries { get; } = entries;
        public OutgoingTransferMode Mode { get; } = mode;
        public IReadOnlyList<OutgoingPayload> Payloads { get; } = payloads;
        public bool NeedsArchive => Payloads.Any(payload => payload.NeedsArchive);
        public bool OfferRequired { get; } = offerRequired;

        public bool IsReady => Payloads.All(payload => payload.IsReady);
        public bool IsDownloadComplete => Payloads.All(payload => payload.Completed);
        public long DownloadSize => Payloads.Aggregate(0L, (sum, payload) => AddSaturating(sum, payload.DownloadSize));
        public long TransferredBytes => Payloads.Aggregate(0L, (sum, payload) => AddSaturating(sum, payload.CoveredBytes));

        public bool TryGetPayload(string payloadId, out OutgoingPayload? payload)
        {
            payload = Payloads.FirstOrDefault(item =>
                string.Equals(item.Id, payloadId, StringComparison.OrdinalIgnoreCase));
            return payload != null;
        }

        public (long Processed, long Total) GetPreparationProgress() =>
            Payloads.Aggregate(
                (Processed: 0L, Total: 0L),
                (progress, payload) => (
                    AddSaturating(progress.Processed, payload.PreparationProcessed),
                    AddSaturating(progress.Total, payload.PreparationTotal)));

        public IReadOnlyList<TransferDownloadDto> CreateDownloadDtos() =>
            Payloads.Select(payload => payload.CreateDownloadDto(Id, Payloads.Count == 1)).ToArray();

        public void ResetForRetry()
        {
            foreach (var payload in Payloads)
                payload.ResetForRetry();
        }
    }

    private sealed class OutgoingPayload
    {
        private readonly object _gate = new();
        private string? _downloadPath;
        private long _downloadSize;
        private long _coveredBytes;
        private long _preparationProcessed;
        private long _preparationTotal;
        private bool _ready;
        private bool _started;
        private bool _completed;

        public OutgoingPayload(
            string id,
            string transferId,
            string name,
            IReadOnlyList<SharedFileEntry> entries,
            bool needsArchive,
            string? downloadPath,
            long downloadSize)
        {
            Id = id;
            ArchiveJobId = transferId + "-" + id;
            Name = name;
            Entries = entries;
            NeedsArchive = needsArchive;
            _downloadPath = downloadPath;
            _downloadSize = Math.Max(0, downloadSize);
            _preparationTotal = entries.Aggregate(0L, (sum, entry) => AddSaturating(sum, entry.Size));
            _preparationProcessed = needsArchive ? 0 : _preparationTotal;
            _ready = !needsArchive;
        }

        public string Id { get; }
        public string ArchiveJobId { get; }
        public string Name { get; }
        public IReadOnlyList<SharedFileEntry> Entries { get; }
        public bool NeedsArchive { get; }

        public long DownloadSize { get { lock (_gate) return _downloadSize; } }
        public long CoveredBytes { get { lock (_gate) return _coveredBytes; } }
        public long PreparationProcessed { get { lock (_gate) return _preparationProcessed; } }
        public long PreparationTotal { get { lock (_gate) return _preparationTotal; } }
        public bool IsReady { get { lock (_gate) return _ready; } }
        public bool Completed { get { lock (_gate) return _completed; } }

        public void SetPreparationProgress(long processed, long total)
        {
            lock (_gate)
            {
                _preparationTotal = Math.Max(0, total);
                _preparationProcessed = Math.Clamp(processed, 0, _preparationTotal);
            }
        }

        public void SetPrepared(string fullPath, long size)
        {
            lock (_gate)
            {
                _downloadPath = fullPath;
                _downloadSize = Math.Max(0, size);
                _preparationProcessed = _preparationTotal;
                _ready = true;
            }
        }

        public bool TryGetDownload(out string fullPath)
        {
            lock (_gate)
            {
                fullPath = _downloadPath ?? string.Empty;
                return _ready && !string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath);
            }
        }

        public bool SetDownloadProgress(long coveredBytes, long totalLength)
        {
            lock (_gate)
            {
                _started = true;
                _downloadSize = Math.Max(0, totalLength);
                _coveredBytes = Math.Clamp(coveredBytes, 0, _downloadSize);
                var complete = _downloadSize == 0 || _coveredBytes >= _downloadSize;
                if (!complete || _completed)
                    return false;
                _completed = true;
                return true;
            }
        }

        public TransferDownloadDto CreateDownloadDto(string transferId, bool single)
        {
            lock (_gate)
            {
                return new TransferDownloadDto(
                    Id,
                    Name,
                    _downloadSize,
                    single
                        ? $"/api/transfers/{transferId}/download"
                        : $"/api/transfers/{transferId}/files/{Id}",
                    _started,
                    _completed);
            }
        }

        public TransferHistoryEntry CreateHistoryEntry(string transferId)
        {
            var entry = Entries[0];
            return new TransferHistoryEntry(
                transferId + ":" + Id,
                entry.Name,
                TransferDirection.ToPhone,
                entry.Size,
                entry.FileCount,
                entry.FullPath,
                entry.IsFolder,
                DateTimeOffset.UtcNow);
        }

        public void ResetForRetry()
        {
            lock (_gate)
            {
                _coveredBytes = 0;
                _started = false;
                _completed = false;
                if (NeedsArchive)
                {
                    _downloadPath = null;
                    _downloadSize = 0;
                    _preparationProcessed = 0;
                    _ready = false;
                }
            }
        }
    }

    private readonly record struct ArchivePayloadReference(string TransferId, string PayloadId);
}
