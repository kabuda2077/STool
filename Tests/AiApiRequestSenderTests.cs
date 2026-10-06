using System.IO;
using System.Net;
using System.Net.Http;
using STool.Core;
using Xunit;

namespace STool.Tests;

public class AiApiRequestSenderTests
{
    [Fact]
    public async Task Deadline_CancelsBodyAfterHeadersHaveArrived()
    {
        using var client = new HttpClient(new DelayedBodyHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var request = AiApiRequestSender.SendAsync(client, ["https://example.test/completions"],
            url => new HttpRequestMessage(HttpMethod.Post, url),
            requestTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task CallerCancellation_CancelsBodyRead()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new DelayedBodyHandler());
        var request = AiApiRequestSender.SendAsync(client, ["https://example.test/completions"],
            url => new HttpRequestMessage(HttpMethod.Post, url), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private sealed class DelayedBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new DelayedContent() });
    }

    private sealed class DelayedContent : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(Timeout.InfiniteTimeSpan);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
