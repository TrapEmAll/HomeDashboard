using System.Xml.Linq;
using HomeDashboard.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class RssNewsProviderTests
{
    [Fact]
    public void ParseFeed_reads_rss_items()
    {
        const string xml = """
            <rss>
              <channel>
                <item>
                  <title>Dashboard shipped</title>
                  <link>https://example.test/dashboard</link>
                  <pubDate>Wed, 19 Aug 2026 12:00:00 GMT</pubDate>
                  <description>MVP feed item.</description>
                </item>
              </channel>
            </rss>
            """;

        var items = RssNewsProvider.ParseFeed("Example", XDocument.Parse(xml));

        Assert.Single(items);
        Assert.Equal("Dashboard shipped", items[0].Title);
        Assert.Equal("Example", items[0].Source);
        Assert.Equal("https://example.test/dashboard", items[0].Url?.ToString());
    }

    [Fact]
    public async Task GetNewsAsync_reuses_recent_feed_results()
    {
        const string xml = "<rss><channel><item><title>Cached item</title><link>https://example.test/item</link></item></channel></rss>";
        var handler = new CountingHandler(xml);
        var provider = new RssNewsProvider(
            Options.Create(new DashboardOptions
            {
                NewsFeeds = [new NewsFeedDefinition { Name = "Example", Url = new Uri("https://example.test/feed") }]
            }),
            new ClientFactory(handler),
            NullLogger<RssNewsProvider>.Instance);

        var first = await provider.GetNewsAsync(CancellationToken.None);
        var second = await provider.GetNewsAsync(CancellationToken.None);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CountingHandler(string content) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });
        }
    }
}
