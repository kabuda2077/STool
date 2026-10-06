using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using STool.Modules.LanTransfer;
using Xunit;

namespace STool.Tests;

/// <summary>
/// 在本机随机端口启动真实服务，覆盖配对认证、防跨站请求头、记住设备、分块上传与 Range 下载。
/// 监听权限或启动失败必须使测试失败，不能把没有执行的测试报告成通过。
/// </summary>
public class LanTransferServerIntegrationTests
{
    [Fact]
    public async Task PairUploadAndRangeDownload_EndToEnd()
    {
        var root = Path.Combine(Path.GetTempPath(), "STool.Tests", Guid.NewGuid().ToString("N"));
        var receive = Path.Combine(root, "Receive");
        Directory.CreateDirectory(receive);
        var port = GetFreePort();
        var catalog = new SharedFileCatalog();
        var server = new LanTransferServer(
            new NetworkEndpoint(IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), "Loopback"),
            port,
            receive,
            2,
            catalog,
            new DeviceTokenStore(Path.Combine(root, "devices.json")),
            listenerHost: "localhost");
        try
        {
            await server.StartAsync();

            var baseAddress = new Uri($"http://localhost:{port}/");
            var cookies = new CookieContainer();
            using var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/session")).StatusCode);

            // 缺少防跨站请求头的状态修改请求直接拒绝，不计入配对尝试。
            var withoutHeader = await client.PostAsync("api/auth/exchange", Json($"{{\"code\":\"{server.ManualCode}\"}}"));
            Assert.Equal(HttpStatusCode.Unauthorized, withoutHeader.StatusCode);

            var exchange = await SendAsync(client, HttpMethod.Post, "api/auth/exchange",
                Json($"{{\"code\":\"{server.ManualCode}\",\"remember\":true}}"));
            Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("api/presence")).StatusCode);

            // 会话失效后，只发往 /api/session 的设备令牌可以换回新会话。
            foreach (Cookie cookie in cookies.GetCookies(baseAddress))
            {
                if (cookie.Name == LanTransferAuthService.SessionCookieName)
                    cookie.Expired = true;
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/presence")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("api/session")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("api/presence")).StatusCode);

            // 分块上传：位置不一致时返回 409 和服务端记录的偏移，按偏移续传后整批落盘。
            var batch = await ReadJsonAsync(await SendAsync(client, HttpMethod.Post, "api/upload-batches",
                Json("{\"name\":\"hello.txt\",\"fileCount\":1,\"totalBytes\":5,\"isFolder\":false}")));
            var batchId = batch.GetProperty("id").GetString();
            var upload = await ReadJsonAsync(await SendAsync(client, HttpMethod.Post, "api/uploads",
                Json($"{{\"name\":\"hello.txt\",\"relativePath\":\"hello.txt\",\"size\":5,\"lastModified\":0,\"batchId\":\"{batchId}\"}}")));
            var uploadId = upload.GetProperty("id").GetString()!;

            // 知道任务 id 也不能控制其他设备的上传。
            using (var otherHandler = new HttpClientHandler { CookieContainer = new CookieContainer() })
            using (var other = new HttpClient(otherHandler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) })
            {
                using var paired = await SendAsync(other, HttpMethod.Post, "api/auth/exchange",
                    Json(JsonSerializer.Serialize(new { code = server.ManualCode })));
                Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
                using var head = new HttpRequestMessage(HttpMethod.Head, $"api/uploads/{uploadId}");
                using var deniedRead = await other.SendAsync(head);
                Assert.Equal(HttpStatusCode.Unauthorized, deniedRead.StatusCode);
                using var deniedWrite = await PatchAsync(other, uploadId, 0, "he");
                Assert.Equal(HttpStatusCode.Unauthorized, deniedWrite.StatusCode);
                using var deniedAdd = await SendAsync(other, HttpMethod.Post, "api/uploads",
                    Json(JsonSerializer.Serialize(new { name = "other.txt", size = 0, batchId })));
                Assert.Equal(HttpStatusCode.Unauthorized, deniedAdd.StatusCode);
            }

            var mismatch = await PatchAsync(client, uploadId, 2, "llo");
            Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
            Assert.Equal("0", mismatch.Headers.GetValues("Upload-Offset").Single());

            Assert.Equal(HttpStatusCode.OK, (await PatchAsync(client, uploadId, 0, "he")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await PatchAsync(client, uploadId, 2, "llo")).StatusCode);
            Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(receive, "hello.txt")));

            // Range 下载只返回请求的区间。
            var sourcePath = Path.Combine(root, "download.bin");
            await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 100).Select(value => (byte)value).ToArray());
            var entry = Assert.Single(catalog.AddPaths([sourcePath]));
            var transfer = server.QueueOutgoing([entry], offerRequired: false);

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"api/transfers/{transfer.Id}/download");
            rangeRequest.Headers.Range = new RangeHeaderValue(10, 19);
            var ranged = await client.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
            Assert.Equal(
                Enumerable.Range(10, 10).Select(value => (byte)value).ToArray(),
                await ranged.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            await server.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, HttpContent content)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-STool-Request", "1");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, string uploadId, long offset, string chunk)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"api/uploads/{uploadId}")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(chunk))
        };
        request.Headers.Add("X-STool-Request", "1");
        request.Headers.Add("Upload-Offset", offset.ToString());
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"Unexpected status {(int)response.StatusCode}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
