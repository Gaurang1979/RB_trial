using System.Net;
using System.Text;
using System.Text.Json;
using StationAgent;
using Xunit;

public class LinkTests
{
    sealed class Capture : HttpMessageHandler
    {
        public readonly List<string> Bodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = await r.Content!.ReadAsStringAsync(ct);
            Bodies.Add(body);
            var last = JsonDocument.Parse(body).RootElement.EnumerateArray().Last().GetProperty("seq").GetInt64();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"stored_up_to\":{last}}}", Encoding.UTF8, "application/json") };
        }
    }

    static QueuedEvent Big(long seq, int size) =>
        new(seq, 1, "inspection.result", $"{{\"seq\":{seq},\"pad\":\"{new string('x', size)}\"}}");

    [Fact]
    public void PlainHttpIsRefusedUnlessAskedFor()
    {
        Assert.Throws<ArgumentException>(() => new HttpPlantLink(new HttpClient { BaseAddress = new Uri("http://plant.local/") }, "t"));
        _ = new HttpPlantLink(new HttpClient { BaseAddress = new Uri("https://plant.local/") }, "t");
        _ = new HttpPlantLink(new HttpClient { BaseAddress = new Uri("http://localhost/") }, "t", allowHttp: true);
    }

    [Fact]
    public async Task ABatchOverTheSizeLimitIsSentInPart()
    {
        var h = new Capture();
        var link = new HttpPlantLink(new HttpClient(h) { BaseAddress = new Uri("https://plant.local/") }, "t");
        var batch = Enumerable.Range(1, 6).Select(i => Big(i, 1024 * 1024)).ToList();
        var acked = await link.SendAsync(batch, default);
        Assert.InRange(acked, 1, 5);
        Assert.True(Encoding.UTF8.GetByteCount(h.Bodies.Single()) <= HttpPlantLink.MaxBytes);
    }

    [Fact]
    public async Task OneEventLargerThanTheLimitStillGoes()
    {
        var h = new Capture();
        var link = new HttpPlantLink(new HttpClient(h) { BaseAddress = new Uri("https://plant.local/") }, "t");
        Assert.Equal(1, await link.SendAsync([Big(1, HttpPlantLink.MaxBytes + 10)], default));
    }
}
