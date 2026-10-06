using System.Net;
using System.IO;
using System.IO.Compression;
using System.Text;
using STool.Modules.LanTransfer;
using Xunit;

namespace STool.Tests;

public class LanTransferTests
{
    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 0, true)]
    [InlineData(true, 11, true)]
    [InlineData(true, 12, true)]
    [InlineData(true, 13, false)]
    public void ClientPresence_RequiresRecentAuthenticatedActivity(bool hasActivity, int elapsedSeconds, bool expected)
    {
        var timestamp = System.Diagnostics.Stopwatch.Frequency;
        var lastActivity = hasActivity ? timestamp : 0;
        var now = timestamp + elapsedSeconds * System.Diagnostics.Stopwatch.Frequency;

        Assert.Equal(expected, LanTransferWindow.IsClientPresent(lastActivity, now));
    }

    [Fact]
    public void SanitizeRelativePath_PreservesSafeFolderStructure()
    {
        var result = TransferPathGuard.SanitizeRelativePath("report.txt", "Documents/2026/report.txt");

        Assert.Equal(Path.Combine("Documents", "2026", "report.txt"), result);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("folder/../../secret.txt")]
    [InlineData("C:\\secret.txt")]
    [InlineData(".stool-transfer/file.txt")]
    public void SanitizeRelativePath_RejectsUnsafePaths(string relativePath)
    {
        Assert.Throws<InvalidDataException>(() =>
            TransferPathGuard.SanitizeRelativePath("secret.txt", relativePath));
    }

    [Fact]
    public void CreateUniquePath_UsesNumberedSuffix()
    {
        var root = CreateTempDirectory();
        try
        {
            var requested = Path.Combine(root, "file.txt");
            File.WriteAllText(requested, "existing");

            var result = TransferPathGuard.CreateUniquePath(requested);

            Assert.Equal(Path.Combine(root, "file (1).txt"), result);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NetworkPolicy_AllowsOnlySelectedSubnetAndLoopback()
    {
        var endpoint = new NetworkEndpoint(
            IPAddress.Parse("192.168.1.179"),
            IPAddress.Parse("255.255.255.0"),
            "Ethernet");

        Assert.True(NetworkEndpointSelector.IsRemoteAllowed(endpoint, IPAddress.Parse("192.168.1.20")));
        Assert.True(NetworkEndpointSelector.IsRemoteAllowed(endpoint, IPAddress.Loopback));
        Assert.False(NetworkEndpointSelector.IsRemoteAllowed(endpoint, IPAddress.Parse("192.168.2.20")));
    }

    [Theory]
    [InlineData("10.0.0.2", true)]
    [InlineData("172.16.0.2", true)]
    [InlineData("192.168.0.2", true)]
    [InlineData("198.18.0.2", false)]
    [InlineData("8.8.8.8", false)]
    public void PrivateLanAddress_IsClassifiedCorrectly(string address, bool expected)
    {
        Assert.Equal(expected, NetworkEndpointSelector.IsPrivateLanAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void AuthService_AcceptsQrTokenAndCreatesSession()
    {
        var root = CreateTempDirectory();
        try
        {
            var auth = new LanTransferAuthService(new DeviceTokenStore(Path.Combine(root, "devices.json")));
            var phone = IPAddress.Parse("192.168.1.20");

            var accepted = auth.TryExchange(
                new AuthExchangeRequest(auth.QrToken, null, false),
                phone,
                out var session,
                out var deviceToken);

            Assert.True(accepted);
            Assert.Null(deviceToken);
            Assert.True(auth.TryAuthenticate(session, null, phone, out var issuedSession));
            Assert.Null(issuedSession);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AuthService_SessionIsBoundToClientAddress()
    {
        var root = CreateTempDirectory();
        try
        {
            var auth = new LanTransferAuthService(new DeviceTokenStore(Path.Combine(root, "devices.json")));
            Assert.True(auth.TryExchange(
                new AuthExchangeRequest(null, auth.ManualCode, false),
                IPAddress.Parse("192.168.1.20"),
                out var session,
                out _));

            Assert.False(auth.TryAuthenticate(session, null, IPAddress.Parse("192.168.1.99"), out _));
            Assert.True(auth.TryAuthenticate(session, null, IPAddress.Parse("::ffff:192.168.1.20"), out _));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AuthService_RotatesCodesAfterSuccessfulPairing()
    {
        var root = CreateTempDirectory();
        try
        {
            var auth = new LanTransferAuthService(new DeviceTokenStore(Path.Combine(root, "devices.json")));
            var rotations = 0;
            auth.AccessCodesRotated += () => rotations++;
            var token = auth.QrToken;

            Assert.True(auth.TryExchange(new AuthExchangeRequest(token, null, false), IPAddress.Parse("192.168.1.20"), out _, out _));

            Assert.Equal(1, rotations);
            Assert.NotEqual(token, auth.QrToken);
            Assert.False(auth.TryExchange(new AuthExchangeRequest(token, null, false), IPAddress.Parse("192.168.1.21"), out _, out _));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AuthService_RejectsExpiredSessionEvenIfCookieIsReplayed()
    {
        var root = CreateTempDirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var auth = new LanTransferAuthService(new DeviceTokenStore(Path.Combine(root, "devices.json")), () => now);
            var phone = IPAddress.Parse("192.168.1.20");
            Assert.True(auth.TryExchange(new AuthExchangeRequest(auth.QrToken, null, false), phone, out var session, out _));
            now += LanTransferAuthService.SessionLifetime;

            Assert.False(auth.TryAuthenticate(session, null, phone, out _));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AuthService_ConcurrentExchangesConsumeCodeOnce()
    {
        var root = CreateTempDirectory();
        try
        {
            var auth = new LanTransferAuthService(new DeviceTokenStore(Path.Combine(root, "devices.json")));
            var request = new AuthExchangeRequest(auth.QrToken, null, false);
            var accepted = 0;
            Parallel.For(0, 8, index =>
            {
                if (auth.TryExchange(request, IPAddress.Parse($"192.168.1.{index + 20}"), out _, out _))
                    Interlocked.Increment(ref accepted);
            });
            Assert.Equal(1, accepted);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AuthService_GlobalLockoutStopsGuessingFromManyAddresses()
    {
        var root = CreateTempDirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var auth = new LanTransferAuthService(new DeviceTokenStore(Path.Combine(root, "devices.json")), () => now);

            // 每个地址只试 5 次，不会触发单地址锁定，但总数达到全局上限。
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var address = IPAddress.Parse($"192.168.1.{100 + attempt / 5}");
                Assert.False(auth.TryExchange(new AuthExchangeRequest(null, "wrong", false), address, out _, out _));
            }

            Assert.False(auth.TryExchange(
                new AuthExchangeRequest(null, auth.ManualCode, false),
                IPAddress.Parse("192.168.1.200"),
                out _,
                out _));

            now = now.AddMinutes(6);
            Assert.True(auth.TryExchange(
                new AuthExchangeRequest(null, auth.ManualCode, false),
                IPAddress.Parse("192.168.1.200"),
                out _,
                out _));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("192.168.1.179", true)]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("192.168.1.20", false)]
    [InlineData("attacker.example", false)]
    [InlineData("", false)]
    public void HostCheck_AllowsOnlyServiceAddressAndLoopback(string host, bool expected)
    {
        Assert.Equal(expected, LanTransferServer.IsAllowedHost(host, IPAddress.Parse("192.168.1.179")));
    }

    [Fact]
    public void SetCookie_IsHttpOnlyLaxAndScopedToPath()
    {
        var header = LanTransferServer.BuildSetCookie("stool_device", "token", "/api/session", DateTimeOffset.UtcNow.AddDays(1));

        Assert.StartsWith("stool_device=token; Path=/api/session; Expires=", header);
        Assert.Contains("HttpOnly", header);
        Assert.Contains("SameSite=Lax", header);
        Assert.DoesNotContain(",", header.Split("Expires=")[0]);
    }

    [Fact]
    public void RememberedDevice_CanAuthenticateNewSession()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new DeviceTokenStore(Path.Combine(root, "devices.json"));
            var auth = new LanTransferAuthService(store);
            Assert.True(auth.TryExchange(
                new AuthExchangeRequest(null, auth.ManualCode, true),
                IPAddress.Parse("192.168.1.20"),
                out _,
                out var deviceToken));

            var nextSession = new LanTransferAuthService(new DeviceTokenStore(Path.Combine(root, "devices.json")));
            Assert.True(nextSession.TryAuthenticate(null, deviceToken, IPAddress.Parse("192.168.1.30"), out var issuedSession));
            Assert.False(string.IsNullOrWhiteSpace(issuedSession));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UploadCoordinator_WritesChunksAndFinalizesAtomically()
    {
        var root = CreateTempDirectory();
        try
        {
            await using var coordinator = new UploadCoordinator(root);
            var content = Encoding.UTF8.GetBytes("STool LAN transfer");
            coordinator.BeginBatch("batch", new UploadBatchCreateRequest("hello.txt", 1, content.Length, false));
            var initialized = await coordinator.BeginAsync(
                new UploadInitRequest("hello.txt", "folder/hello.txt", content.Length, 0, "batch"),
                CancellationToken.None);

            await using var input = new MemoryStream(content);
            var completed = await coordinator.AppendAsync(
                initialized.Id, 0, input, content.Length, CancellationToken.None);

            Assert.True(completed.Complete);
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(root, "folder", "hello.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UploadCoordinator_ReportsExpectedOffset()
    {
        var root = CreateTempDirectory();
        try
        {
            await using var coordinator = new UploadCoordinator(root);
            coordinator.BeginBatch("batch", new UploadBatchCreateRequest("hello.txt", 1, 3, false));
            var initialized = await coordinator.BeginAsync(
                new UploadInitRequest("hello.txt", null, 3, 0, "batch"),
                CancellationToken.None);

            await using var input = new MemoryStream([1, 2]);
            var exception = await Assert.ThrowsAsync<UploadOffsetMismatchException>(() =>
                coordinator.AppendAsync(initialized.Id, 1, input, 2, CancellationToken.None));

            Assert.Equal(0, exception.ExpectedOffset);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UploadCoordinator_RejectsMismatchedSizesWithoutReservingTheName()
    {
        var root = CreateTempDirectory();
        try
        {
            await using var coordinator = new UploadCoordinator(root);
            coordinator.BeginBatch("batch", new UploadBatchCreateRequest("files", 2, 3, false));
            await coordinator.BeginAsync(new UploadInitRequest("first.txt", null, 2, 0, "batch"), CancellationToken.None);
            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.BeginAsync(
                new UploadInitRequest("last.txt", null, 2, 0, "batch"), CancellationToken.None));

            var retried = await coordinator.BeginAsync(new UploadInitRequest("last.txt", null, 1, 0, "batch"), CancellationToken.None);
            Assert.Equal(1, retried.Size);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UploadCoordinator_RejectsSizeThatWouldOverflowDiskReservation()
    {
        var root = CreateTempDirectory();
        try
        {
            await using var coordinator = new UploadCoordinator(root, getAvailableFreeSpace: _ => long.MaxValue);
            Assert.Throws<InvalidDataException>(() => coordinator.BeginBatch("huge",
                new UploadBatchCreateRequest("huge", 1, long.MaxValue, false)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UploadCoordinator_RejectsUploadWithoutBatch()
    {
        var root = CreateTempDirectory();
        try
        {
            await using var coordinator = new UploadCoordinator(root);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                coordinator.BeginAsync(new UploadInitRequest("hello.txt", null, 3, 0), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UploadCoordinator_RejectsBatchLargerThanFreeSpace()
    {
        var root = CreateTempDirectory();
        try
        {
            await using var coordinator = new UploadCoordinator(root, getAvailableFreeSpace: _ => 100L * 1024 * 1024);

            var exception = Assert.Throws<InvalidDataException>(() =>
                coordinator.BeginBatch("big", new UploadBatchCreateRequest("big.iso", 1, 90L * 1024 * 1024, false)));
            Assert.Contains("磁盘空间不足", exception.Message);

            coordinator.BeginBatch("small", new UploadBatchCreateRequest("small.bin", 1, 1024, false));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("bytes=0-99", 100, 0, 99)]
    [InlineData("bytes=50-", 100, 50, 99)]
    [InlineData("bytes=-10", 100, 90, 99)]
    public void RangeParser_HandlesSupportedRanges(string header, long length, long expectedStart, long expectedEnd)
    {
        Assert.True(LanTransferServer.TryParseRange(header, length, out var start, out var end));
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    [Fact]
    public async Task JsonReader_AcceptsKnownBoundedContent()
    {
        const string json = "{\"token\":\"abc\",\"code\":null,\"remember\":true}";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await LanTransferServer.ReadJsonAsync<AuthExchangeRequest>(stream, stream.Length);

        Assert.NotNull(result);
        Assert.Equal("abc", result.Token);
        Assert.True(result.Remember);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65537)]
    public async Task JsonReader_RejectsUnknownOrOversizedContent(long contentLength)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{}"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LanTransferServer.ReadJsonAsync<AuthExchangeRequest>(stream, contentLength));
    }

    [Fact]
    public async Task JsonReader_AllowsLimitAndReportsMalformedJsonAsInvalidData()
    {
        await using var limitStream = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        await LanTransferServer.ReadJsonAsync<AuthExchangeRequest>(limitStream, 64 * 1024);

        await using var malformed = new MemoryStream(Encoding.UTF8.GetBytes("{broken"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LanTransferServer.ReadJsonAsync<AuthExchangeRequest>(malformed, malformed.Length));
    }

    [Fact]
    public void DownloadProgress_MergesParallelAndRepeatedRanges()
    {
        var tracker = new DownloadProgressTracker();
        var first = tracker.Begin("file", "192.168.1.3", 1000, 0);
        var second = tracker.Begin("file", "192.168.1.3", 1000, 500);

        Assert.Equal(400, tracker.Record(first, 0, 400));
        Assert.Equal(700, tracker.Record(second, 500, 300));
        Assert.Equal(800, tracker.Record(first, 300, 300));
    }

    [Fact]
    public void SharedCatalog_AddsDirectoryAsSingleFolderItem()
    {
        var root = CreateTempDirectory();
        try
        {
            var folder = Path.Combine(root, "Backup");
            Directory.CreateDirectory(Path.Combine(folder, "nested"));
            File.WriteAllBytes(Path.Combine(folder, "first.bin"), new byte[3]);
            File.WriteAllBytes(Path.Combine(folder, "nested", "second.bin"), new byte[5]);
            var catalog = new SharedFileCatalog();

            var added = catalog.AddPaths([folder]);

            var item = Assert.Single(added);
            Assert.True(item.IsFolder);
            Assert.Equal("Backup", item.Name);
            Assert.Equal(2, item.FileCount);
            Assert.Equal(8, item.Size);
            Assert.Equal(2, item.FolderFiles.Count);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SharedCatalog_BrowsesFoldersAndResolvesIndividualFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var folder = Path.Combine(root, "Backup");
            Directory.CreateDirectory(Path.Combine(folder, "nested", "deeper"));
            File.WriteAllBytes(Path.Combine(folder, "root.bin"), new byte[3]);
            File.WriteAllBytes(Path.Combine(folder, "nested", "child.bin"), new byte[5]);
            File.WriteAllBytes(Path.Combine(folder, "nested", "deeper", "leaf.bin"), new byte[7]);
            var catalog = new SharedFileCatalog();
            var shared = Assert.Single(catalog.AddPaths([folder]));

            Assert.True(catalog.TryGetFolderContents(shared.Id, null, out var rootContents));
            Assert.NotNull(rootContents);
            Assert.Equal("Backup", rootContents.Name);
            Assert.Collection(
                rootContents.Items,
                item =>
                {
                    Assert.True(item.IsFolder);
                    Assert.Equal("nested", item.Name);
                    Assert.Equal(2, item.FileCount);
                    Assert.Equal(12, item.Size);
                },
                item =>
                {
                    Assert.False(item.IsFolder);
                    Assert.Equal("root.bin", item.Name);
                });

            Assert.True(catalog.TryGetFolderContents(shared.Id, "nested", out var nestedContents));
            Assert.NotNull(nestedContents);
            Assert.Contains(nestedContents.Items, item => item.IsFolder && item.Name == "deeper");
            Assert.Contains(nestedContents.Items, item => !item.IsFolder && item.Name == "child.bin");
            Assert.True(catalog.TryGetFolderFile(shared.Id, "nested/child.bin", out var child));
            Assert.Equal(5, child!.Size);
            Assert.False(catalog.TryGetFolderFile(shared.Id, "../root.bin", out _));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PreparedArchive_CreatesReusableStoreOnlyZipAndDeletesItOnDispose()
    {
        var root = CreateTempDirectory();
        var source = Path.Combine(root, "source.bin");
        File.WriteAllBytes(source, Encoding.UTF8.GetBytes("archive content"));
        string archivePath;
        try
        {
            await using (var manager = new PreparedArchiveManager(root))
            {
                var first = manager.Begin(
                    "folder:test",
                    "Backup.zip",
                    [new PreparedArchiveItem(source, "Backup/source.bin", new FileInfo(source).Length)]);
                var second = manager.Begin(
                    "folder:test",
                    "Backup.zip",
                    [new PreparedArchiveItem(source, "Backup/source.bin", new FileInfo(source).Length)]);

                Assert.Equal(first.Id, second.Id);
                var ready = await WaitForArchiveAsync(manager, first.Id);
                Assert.Equal("ready", ready.Status);
                Assert.NotNull(ready.DownloadUrl);
                Assert.True(manager.TryGetDownload(first.Id, out var download));
                archivePath = download!.FullPath;
                Assert.True(File.Exists(archivePath));

                using var archive = ZipFile.OpenRead(archivePath);
                var entry = Assert.Single(archive.Entries);
                Assert.Equal("Backup/source.bin", entry.FullName);
                Assert.Equal(CompressionMethodStored, GetZipCompressionMethod(archivePath));
            }

            Assert.False(File.Exists(archivePath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TransferSession_PauseResumeAndCompletionUseOneStateMachine()
    {
        await using var sessions = new TransferSessionManager();
        var created = sessions.CreateOutgoing(
            "Backup.zip", 100, 2, ["C:\\Source"], "C:\\Source", preparing: true);

        Assert.True(sessions.Pause(created.Id));
        Assert.True(sessions.TryGet(created.Id, out var paused));
        Assert.Equal(TransferState.Paused, paused!.State);

        Assert.True(sessions.Resume(created.Id));
        sessions.SetPreparingProgress(created.Id, 100, 100);
        sessions.SetAwaitingConfirmation(created.Id, 80);
        Assert.True(sessions.TryClaim(created.Id, "phone"));
        sessions.BeginRequest(created.Id);
        sessions.ReportProgress(created.Id, 80, 80);
        sessions.EndRequest(created.Id);

        Assert.True(sessions.TryGet(created.Id, out var completed));
        Assert.Equal(TransferState.Completed, completed!.State);
    }

    [Fact]
    public async Task TransferSession_CompletedTransfer_StaysDownloadableForSameDeviceOnly()
    {
        await using var sessions = new TransferSessionManager();
        var created = sessions.CreateOutgoing(
            "config.json", 10915, 1, ["C:\\Downloads\\config.json"], "C:\\Downloads", preparing: false);

        Assert.True(sessions.TryClaim(created.Id, "phone"));
        Assert.True(sessions.TryBeginRequest(created.Id));
        sessions.ReportProgress(created.Id, 10915, 10915);
        sessions.EndRequest(created.Id);

        Assert.True(sessions.TryGet(created.Id, out var completed));
        Assert.Equal(TransferState.Completed, completed!.State);

        // 宽限期内同一设备的并行/重试请求仍可读取
        Assert.True(sessions.TryClaim(created.Id, "phone"));
        Assert.False(sessions.TryClaim(created.Id, "other-phone"));

        // 但不再参与状态机,不会把完成任务拉回传输中
        Assert.False(sessions.TryBeginRequest(created.Id));
        Assert.True(sessions.TryGet(created.Id, out var afterRetry));
        Assert.Equal(TransferState.Completed, afterRetry!.State);
    }

    [Fact]
    public async Task TransferSession_RepeatedPausePreservesPreparingState()
    {
        await using var sessions = new TransferSessionManager();
        var created = sessions.CreateOutgoing(
            "Backup.zip", 100, 2, ["C:\\Source"], "C:\\Source", preparing: true);

        Assert.True(sessions.Pause(created.Id));
        Assert.True(sessions.Pause(created.Id));
        Assert.True(sessions.Resume(created.Id));
        Assert.True(sessions.TryGet(created.Id, out var resumed));
        Assert.Equal(TransferState.Preparing, resumed!.State);
    }

    [Fact]
    public async Task TransferSession_AggregateProgressWaitsForAllPayloads()
    {
        await using var sessions = new TransferSessionManager();
        var created = sessions.CreateOutgoing(
            "2 个项目", 100, 2, ["C:\\a.bin", "C:\\b.bin"], "C:\\", preparing: false);

        sessions.BeginRequest(created.Id);
        sessions.ReportProgress(created.Id, 50, 100, completeWhenReached: false);
        sessions.ReportProgress(created.Id, 100, 100, completeWhenReached: false);

        Assert.True(sessions.TryGet(created.Id, out var stillTransferring));
        Assert.Equal(TransferState.Transferring, stillTransferring!.State);

        sessions.ReportProgress(created.Id, 100, 100, completeWhenReached: true);
        Assert.True(sessions.TryGet(created.Id, out var completed));
        Assert.Equal(TransferState.Completed, completed!.State);
    }

    [Fact]
    public async Task TransferSession_DisconnectedRequestBecomesWaitingForResume()
    {
        await using var sessions = new TransferSessionManager(disconnectedDelay: TimeSpan.FromMilliseconds(10));
        var created = sessions.CreateOutgoing(
            "file.bin", 100, 1, ["C:\\file.bin"], "C:\\", preparing: false);
        Assert.True(sessions.TryClaim(created.Id, "phone"));

        sessions.BeginRequest(created.Id);
        await Task.Delay(25);
        sessions.ReportProgress(created.Id, 40, 100);
        Assert.True(sessions.TryGet(created.Id, out var transferring));
        Assert.True(transferring!.BytesPerSecond > 0);
        sessions.EndRequest(created.Id);
        await Task.Delay(40);

        Assert.True(sessions.TryGet(created.Id, out var waiting));
        Assert.Equal(TransferState.WaitingForResume, waiting!.State);
        Assert.Equal(40, waiting.TransferredBytes);
        Assert.Equal(0, waiting.BytesPerSecond);
    }

    [Fact]
    public async Task UploadBatch_CommitsOnlyAfterEveryFileCompletes()
    {
        var root = CreateTempDirectory();
        try
        {
            await using var sessions = new TransferSessionManager();
            await using var uploads = new UploadCoordinator(root, sessions);
            const string batchId = "batch";
            sessions.CreateIncoming(batchId, "2 个文件", 5, 2, "phone");
            uploads.BeginBatch(batchId, new UploadBatchCreateRequest("2 个文件", 2, 5, false));
            var first = await uploads.BeginAsync(
                new UploadInitRequest("a.txt", "a.txt", 2, 0, batchId), CancellationToken.None);
            var second = await uploads.BeginAsync(
                new UploadInitRequest("b.txt", "b.txt", 3, 0, batchId), CancellationToken.None);

            await using (var input = new MemoryStream([1, 2]))
                await uploads.AppendAsync(first.Id, 0, input, 2, CancellationToken.None);
            Assert.False(File.Exists(Path.Combine(root, "a.txt")));

            await using (var input = new MemoryStream([3, 4, 5]))
                await uploads.AppendAsync(second.Id, 0, input, 3, CancellationToken.None);

            Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(root, "a.txt")));
            Assert.Equal(new byte[] { 3, 4, 5 }, await File.ReadAllBytesAsync(Path.Combine(root, "b.txt")));
            Assert.True(sessions.TryGet(batchId, out var completed));
            Assert.Equal(TransferState.Completed, completed!.State);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UploadBatch_CancelDeletesStagedFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            await using var sessions = new TransferSessionManager();
            await using var uploads = new UploadCoordinator(root, sessions);
            const string batchId = "cancel-batch";
            sessions.CreateIncoming(batchId, "file.bin", 4, 1, "phone");
            uploads.BeginBatch(batchId, new UploadBatchCreateRequest("file.bin", 1, 4, false));
            var upload = await uploads.BeginAsync(
                new UploadInitRequest("file.bin", "file.bin", 4, 0, batchId), CancellationToken.None);
            await using (var input = new MemoryStream([1, 2]))
                await uploads.AppendAsync(upload.Id, 0, input, 2, CancellationToken.None);

            sessions.Cancel(batchId);
            await uploads.CancelBatchAsync(batchId);

            Assert.False(File.Exists(Path.Combine(root, "file.bin")));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(root, "*.stool-uploading", SearchOption.AllDirectories),
                _ => true);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CancelOutgoing_CanPreserveFolderForBrowsing()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "Backup");
            var receive = Path.Combine(root, "Receive");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(receive);
            File.WriteAllText(Path.Combine(source, "file.txt"), "content");
            var catalog = new SharedFileCatalog();
            var folder = Assert.Single(catalog.AddPaths([source]));
            await using var server = new LanTransferServer(
                new NetworkEndpoint(IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), "Loopback"),
                17654,
                receive,
                2,
                catalog,
                new DeviceTokenStore(Path.Combine(root, "devices.json")));
            var transfer = server.QueueOutgoing([folder]);

            Assert.True(await server.CancelTransferAsync(transfer.Id, removeSources: false));

            Assert.True(catalog.TryGet(folder.Id, out var preserved));
            Assert.NotNull(preserved);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task QueueOutgoing_SeparateFilesSkipsCombinedArchivePreparation()
    {
        var root = CreateTempDirectory();
        try
        {
            var receive = Path.Combine(root, "Receive");
            Directory.CreateDirectory(receive);
            File.WriteAllBytes(Path.Combine(root, "first.bin"), new byte[3]);
            File.WriteAllBytes(Path.Combine(root, "second.bin"), new byte[5]);
            var catalog = new SharedFileCatalog();
            var files = catalog.AddPaths([
                Path.Combine(root, "first.bin"),
                Path.Combine(root, "second.bin")]);
            await using var server = new LanTransferServer(
                new NetworkEndpoint(IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), "Loopback"),
                17654,
                receive,
                2,
                catalog,
                new DeviceTokenStore(Path.Combine(root, "devices.json")));

            var transfer = server.QueueOutgoing(files, OutgoingTransferMode.Separate);

            Assert.Equal("2 个项目", transfer.Name);
            Assert.Equal(TransferState.AwaitingConfirmation, transfer.State);
            Assert.Equal(8, transfer.TotalBytes);
            Assert.Equal(2, transfer.FileCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResolveOutgoingMode_FollowsImmediateSendBatchRules()
    {
        var file = CreateSharedEntry("file.txt", isFolder: false);
        var folder = CreateSharedEntry("folder", isFolder: true);

        Assert.Equal(OutgoingTransferMode.Automatic, LanTransferWindow.ResolveOutgoingMode([file]));
        Assert.Equal(OutgoingTransferMode.CombinedArchive, LanTransferWindow.ResolveOutgoingMode([folder]));
        Assert.Equal(OutgoingTransferMode.Separate, LanTransferWindow.ResolveOutgoingMode([file, file]));
        Assert.Equal(OutgoingTransferMode.Separate, LanTransferWindow.ResolveOutgoingMode([file, file, file, file, file]));
        Assert.Equal(OutgoingTransferMode.CombinedArchive, LanTransferWindow.ResolveOutgoingMode([file, file, file, file, file, file]));
    }

    [Fact]
    public void SharedFileCatalog_CanCreateIndependentEntriesForRepeatedSendBatches()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "repeat.txt");
            File.WriteAllText(path, "content");
            var catalog = new SharedFileCatalog();

            var first = Assert.Single(catalog.AddPaths([path], allowDuplicatePaths: true));
            var second = Assert.Single(catalog.AddPaths([path], allowDuplicatePaths: true));

            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(2, catalog.Snapshot().Count);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TransferHistory_PersistsAndKeepsNewestHundredEntries()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "history.json");
            var store = new TransferHistoryStore(path);
            for (var index = 0; index < 105; index++)
            {
                store.Add(new TransferHistoryEntry(
                    index.ToString(),
                    $"file-{index}",
                    TransferDirection.ToPhone,
                    index,
                    1,
                    root,
                    true,
                    DateTimeOffset.UtcNow.AddMinutes(index)));
            }

            var reloaded = new TransferHistoryStore(path).Snapshot();
            Assert.Equal(100, reloaded.Count);
            Assert.Equal("104", reloaded[0].Id);
            Assert.DoesNotContain(reloaded, item => item.Id == "0");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TransferHistory_CorruptFileLoadsAsEmpty()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "history.json");
            File.WriteAllText(path, "{not-json");

            Assert.Empty(new TransferHistoryStore(path).Snapshot());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void WebAssets_AreEmbeddedInApplicationAssembly()
    {
        Assert.NotEmpty(LanTransferWebAssets.Read("index.html")!);
        Assert.NotEmpty(LanTransferWebAssets.Read("style.css")!);
        var script = Encoding.UTF8.GetString(LanTransferWebAssets.Read("app.js")!);
        Assert.Contains("/api/presence", script);
        Assert.Contains("exchange({ token, remember: true })", script);
        Assert.Contains("triggerDownloads", script);
        Assert.Contains("继续剩余", script);
    }

    private static SharedFileEntry CreateSharedEntry(string name, bool isFolder) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        RelativePath = name,
        FullPath = Path.Combine(Path.GetTempPath(), name),
        Size = 1,
        ModifiedUtc = DateTimeOffset.UtcNow,
        IsFolder = isFolder,
        FolderFiles = []
    };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "STool.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<PreparedArchiveStatus> WaitForArchiveAsync(
        PreparedArchiveManager manager,
        string id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            Assert.True(manager.TryGetStatus(id, out var status));
            Assert.NotNull(status);
            if (status.Status != "preparing")
                return status;
            await Task.Delay(20);
        }

        throw new TimeoutException("Prepared archive did not complete in time.");
    }

    private static ushort GetZipCompressionMethod(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0x04034b50u, reader.ReadUInt32());
        stream.Position = 8;
        return reader.ReadUInt16();
    }

    private const ushort CompressionMethodStored = 0;
}
