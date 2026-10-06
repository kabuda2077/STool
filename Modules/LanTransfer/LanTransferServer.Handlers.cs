using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using Serilog;

namespace STool.Modules.LanTransfer;

/// <summary>LanTransferServer 各接口的处理与访问控制。</summary>
internal sealed partial class LanTransferServer
{
    private async Task HandleSessionAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var deviceToken = GetCookie(request, LanTransferAuthService.DeviceCookieName);
        var valid = _auth.TryAuthenticate(
            GetCookie(request, LanTransferAuthService.SessionCookieName),
            deviceToken,
            GetRemoteAddress(request),
            out var issuedSession);
        if (!valid)
        {
            await WriteErrorAsync(context.Response, 401, "需要连接认证。 ", cancellationToken);
            return;
        }

        if (issuedSession != null)
        {
            SetCookie(context.Response, LanTransferAuthService.SessionCookieName, issuedSession, SessionCookiePath, SessionCookieLifetime);
            if (!string.IsNullOrWhiteSpace(deviceToken))
            {
                // 设备令牌只随 /api/session 发送；旧版本写在根路径的同名 Cookie 迁移后删除，避免每个请求都带着它。
                SetCookie(context.Response, LanTransferAuthService.DeviceCookieName, deviceToken, DeviceCookiePath, DeviceCookieLifetime);
                ExpireCookie(context.Response, LanTransferAuthService.DeviceCookieName, SessionCookiePath);
            }
        }

        ClientActivity?.Invoke(this);
        await WriteJsonAsync(context.Response, new { authenticated = true }, cancellationToken);
    }

    private async Task HandleAuthExchangeAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = await ReadJsonAsync<AuthExchangeRequest>(context.Request, cancellationToken)
            ?? throw new InvalidDataException("认证信息无效。 ");
        if (!_auth.TryExchange(request, GetRemoteAddress(context.Request), out var sessionToken, out var deviceToken))
        {
            await WriteErrorAsync(context.Response, 401, "二维码令牌或验证码无效，或尝试次数过多，请稍后再试。 ", cancellationToken);
            return;
        }

        SetCookie(context.Response, LanTransferAuthService.SessionCookieName, sessionToken, SessionCookiePath, SessionCookieLifetime);
        if (deviceToken != null)
            SetCookie(context.Response, LanTransferAuthService.DeviceCookieName, deviceToken, DeviceCookiePath, DeviceCookieLifetime);
        ClientActivity?.Invoke(this);
        await WriteJsonAsync(context.Response, new { authenticated = true }, cancellationToken);
    }

    private async Task HandleTransferListAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var owner = GetRequestOwner(context.Request);
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

            await SendPayloadAsync(context, snapshot.Id, outgoing.Payloads[0], downloadPath, cancellationToken);
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

            await SendPayloadAsync(context, snapshot.Id, payload, downloadPath, cancellationToken);
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

    private Task SendPayloadAsync(
        HttpListenerContext context,
        string transferId,
        OutgoingPayload payload,
        string downloadPath,
        CancellationToken cancellationToken)
    {
        // 分段并行下载走单独的并发上限，避免占满整任务名额。
        var gate = string.IsNullOrWhiteSpace(context.Request.Headers["Range"])
            ? _transferGate
            : _rangeTransferGate;
        return WithTransferSlotAsync(
            () => SendFileAsync(context, transferId, payload.Id, payload.Name, downloadPath, cancellationToken),
            cancellationToken,
            gate);
    }

    private async Task HandleUploadBatchCreateAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
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
    }

    private void EnsureUploadOwner(HttpListenerRequest request, string batchId)
    {
        if (!_sessions.TryGet(batchId, out var batch) || batch?.Direction != TransferDirection.ToComputer)
            throw new FileNotFoundException("上传批次不存在。 ");
        if (!_sessions.CanControl(batchId, GetRequestOwner(request)))
            throw new UnauthorizedAccessException("该上传任务属于其他设备。 ");
    }

    private async Task HandleUploadAsync(HttpListenerContext context, string id, CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (!_uploads.TryGetBatchId(id, out var batchId) || batchId == null)
            throw new FileNotFoundException("上传任务不存在。 ");
        EnsureUploadOwner(request, batchId);

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
            await CancelTransferAsync(batchId);
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

        await WithTransferSlotAsync(async () =>
        {
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
            if (!_sessions.CanControl(snapshot.Id, GetRequestOwner(context.Request)))
                throw new UnauthorizedAccessException("该下载任务属于其他设备。 ");
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

    private void EnsureAuthenticated(HttpListenerContext context)
    {
        var request = context.Request;
        var valid = _auth.TryAuthenticate(
            GetCookie(request, LanTransferAuthService.SessionCookieName),
            // 长期凭据只能在 /api/session 换取短期会话，不能直接授权业务请求。
            null,
            GetRemoteAddress(request),
            out var issuedSession);
        if (!valid)
            throw new UnauthorizedAccessException("需要连接认证。 ");
        if (issuedSession != null)
            SetCookie(context.Response, LanTransferAuthService.SessionCookieName, issuedSession, SessionCookiePath, SessionCookieLifetime);
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
            !string.Equals(originUri.Scheme, request.Url?.Scheme, StringComparison.OrdinalIgnoreCase) ||
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

    /// <summary>Host 必须是本服务监听的局域网地址或本机回环地址。</summary>
    internal bool IsAllowedHost(string? host) => IsAllowedHost(host, _endpoint.Address);

    internal static bool IsAllowedHost(string? host, IPAddress endpointAddress)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address))
            return false;

        address = LanTransferAuthService.Normalize(address);
        return IPAddress.IsLoopback(address) || address.Equals(endpointAddress);
    }

    private static IPAddress GetRemoteAddress(HttpListenerRequest request) =>
        request.RemoteEndPoint?.Address ?? IPAddress.None;

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
}
