using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using STool.Modules.LanTransfer;
using Xunit;

namespace STool.Tests;

/// <summary>局域网服务的取消状态码、传输名额超时、403/401 区分与会话撤销。</summary>
public class LanTransferHardeningTests
{
    /// <summary>先发出一部分请求体，然后一直挂起，模拟手机息屏留下的半开连接。</summary>
    private sealed class StallingContent(string head, long totalLength, Task stall) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var bytes = Encoding.UTF8.GetBytes(head);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
            await stall;
            // 放行后补齐剩余字节，满足声明的 Content-Length。
            await stream.WriteAsync(new byte[totalLength - bytes.Length]);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = totalLength;
            return true;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required LanTransferServer Server { get; init; }
        public required HttpClient Client { get; init; }
        public required string UploadId { get; init; }
        public required string BatchId { get; init; }
        public required Uri BaseAddress { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    private static async Task<Fixture> StartAsync(int slots)
    {
        var root = Path.Combine(Path.GetTempPath(), "STool.Tests", Guid.NewGuid().ToString("N"));
        var receive = Path.Combine(root, "Receive");
        Directory.CreateDirectory(receive);
        var port = GetFreePort();
        var server = new LanTransferServer(
            new NetworkEndpoint(IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), "Loopback"),
            port, receive, slots, new SharedFileCatalog(),
            new DeviceTokenStore(Path.Combine(root, "devices.json")), listenerHost: "localhost");
        await server.StartAsync();

        var baseAddress = new Uri($"http://localhost:{port}/");
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        var client = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };
        using var paired = await SendAsync(client, HttpMethod.Post, "api/auth/exchange",
            Json(JsonSerializer.Serialize(new { code = server.ManualCode })));
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);

        using var batchResponse = await SendAsync(client, HttpMethod.Post, "api/upload-batches",
            Json("{\"name\":\"a.bin\",\"fileCount\":1,\"totalBytes\":10,\"isFolder\":false}"));
        var batchId = (await ReadJsonAsync(batchResponse)).GetProperty("id").GetString()!;
        using var uploadResponse = await SendAsync(client, HttpMethod.Post, "api/uploads",
            Json($"{{\"name\":\"a.bin\",\"relativePath\":\"a.bin\",\"size\":10,\"lastModified\":0,\"batchId\":\"{batchId}\"}}"));
        var uploadId = (await ReadJsonAsync(uploadResponse)).GetProperty("id").GetString()!;
        return new Fixture
        {
            Root = root, Server = server, Client = client, UploadId = uploadId, BatchId = batchId, BaseAddress = baseAddress
        };
    }

    private static Task<HttpResponseMessage> StalledPatchAsync(Fixture fixture, Task stall)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"api/uploads/{fixture.UploadId}")
        {
            Content = new StallingContent("ab", 10, stall)
        };
        request.Headers.Add("X-STool-Request", "1");
        request.Headers.Add("Upload-Offset", "0");
        return fixture.Client.SendAsync(request);
    }

    [Fact]
    public async Task CancelingTransferWhileUploading_ReturnsNon2xxInsteadOfOk()
    {
        await using var fixture = await StartAsync(2);
        var stall = new TaskCompletionSource();
        var patch = StalledPatchAsync(fixture, stall.Task);
        await Task.Delay(500);

        using var cancel = await SendAsync(fixture.Client, HttpMethod.Delete, $"api/uploads/{fixture.UploadId}", null);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);

        // 客户端通常要等请求体发完才读响应；放行后应收到 499 而不是 200。
        await Task.Delay(300);
        stall.TrySetResult();
        using var response = await patch.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LanTransferServer.ClientCanceledStatusCode, (int)response.StatusCode);
    }

    [Fact]
    public async Task StalledUpload_TimesOutAndReleasesSlot()
    {
        await using var fixture = await StartAsync(1);
        fixture.Server.TransferIdleTimeout = TimeSpan.FromMilliseconds(400);
        var stall = new TaskCompletionSource();
        var patch = StalledPatchAsync(fixture, stall.Task);

        // 服务端因无进展断开连接，客户端收到连接错误。
        await Assert.ThrowsAnyAsync<Exception>(async () => await patch.WaitAsync(TimeSpan.FromSeconds(10)));
        stall.TrySetResult();

        // 名额已释放：新的上传分块可以正常完成。
        using var next = await PatchAsync(fixture, 0, "0123456789");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task WaitingForTransferSlotTimesOutWith503AndRetryAfter()
    {
        await using var fixture = await StartAsync(1);
        fixture.Server.TransferSlotWaitTimeout = TimeSpan.FromMilliseconds(300);
        var stall = new TaskCompletionSource();
        var patch = StalledPatchAsync(fixture, stall.Task);
        await Task.Delay(500);

        using var busy = await PatchAsync(fixture, 0, "0123456789");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.True(busy.Headers.Contains("Retry-After"));

        stall.TrySetResult();
        try { (await patch.WaitAsync(TimeSpan.FromSeconds(10))).Dispose(); } catch { }
    }

    [Fact]
    public async Task AuthErrorsAreDistinguished_401ForUnauthenticated_403ForOtherDeviceTask()
    {
        await using var fixture = await StartAsync(2);

        using var anonymousHandler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var anonymous = new HttpClient(anonymousHandler) { BaseAddress = fixture.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/presence")).StatusCode);

        using var otherHandler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var other = new HttpClient(otherHandler) { BaseAddress = fixture.BaseAddress };
        using var paired = await SendAsync(other, HttpMethod.Post, "api/auth/exchange",
            Json(JsonSerializer.Serialize(new { code = fixture.Server.ManualCode })));
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        using var denied = await other.GetAsync($"api/transfers/{fixture.BatchId}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task RevokingRememberedDevices_InvalidatesOnlineSession()
    {
        await using var fixture = await StartAsync(2);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("api/presence")).StatusCode);

        fixture.Server.RevokeRememberedDevices();

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("api/presence")).StatusCode);
    }

    private static Task<HttpResponseMessage> PatchAsync(Fixture fixture, long offset, string chunk)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"api/uploads/{fixture.UploadId}")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(chunk))
        };
        request.Headers.Add("X-STool-Request", "1");
        request.Headers.Add("Upload-Offset", offset.ToString());
        return fixture.Client.SendAsync(request);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-STool-Request", "1");
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"Unexpected status {(int)response.StatusCode}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
