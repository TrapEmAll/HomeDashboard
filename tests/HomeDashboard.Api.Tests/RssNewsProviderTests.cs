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
                IncludeRecommendedFeeds = false,
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

    [Fact]
    public void ParseFeed_preserves_podcast_discovery_metadata()
    {
        const string xml = "<rss><channel><item><title>New episode</title><link>https://example.test/episode</link></item></channel></rss>";
        var spotify = new Uri("https://open.spotify.com/search/Example");

        var item = Assert.Single(RssNewsProvider.ParseFeed(
            "Example Show",
            XDocument.Parse(xml),
            HomeDashboard.Contracts.NewsContentKind.Podcast,
            "Cybersecurity",
            spotify));

        Assert.Equal(HomeDashboard.Contracts.NewsContentKind.Podcast, item.Kind);
        Assert.Equal("Cybersecurity", item.Category);
        Assert.Equal(spotify, item.ProviderUrl);
    }

    [Fact]
    public void ParseFeed_reads_podcast_audio_artwork_and_duration()
    {
        const string xml = """
            <rss xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
              <channel><item>
                <title>Network tuning</title>
                <link>https://example.test/episode</link>
                <enclosure url="https://cdn.example.test/episode.mp3" type="audio/mpeg" />
                <itunes:image href="https://cdn.example.test/artwork.jpg" />
                <itunes:duration>42:15</itunes:duration>
              </item></channel>
            </rss>
            """;

        var item = Assert.Single(RssNewsProvider.ParseFeed(
            "Example Show", XDocument.Parse(xml), HomeDashboard.Contracts.NewsContentKind.Podcast, "Technology", null));

        Assert.Equal("https://cdn.example.test/episode.mp3", item.MediaUrl?.ToString());
        Assert.Equal("https://cdn.example.test/artwork.jpg", item.ImageUrl?.ToString());
        Assert.Equal("42:15", item.Duration);
    }

    [Fact]
    public void Recommended_catalog_has_unique_article_and_podcast_feeds()
    {
        Assert.True(RecommendedFeedCatalog.All.Count >= 20);
        Assert.Contains(RecommendedFeedCatalog.All, feed => feed.Kind == HomeDashboard.Contracts.NewsContentKind.Article);
        Assert.Contains(RecommendedFeedCatalog.All, feed => feed.Kind == HomeDashboard.Contracts.NewsContentKind.Podcast && feed.ProviderUrl is not null);
        Assert.Equal(
            RecommendedFeedCatalog.All.Count,
            RecommendedFeedCatalog.All.Select(feed => feed.Url.AbsoluteUri).Distinct(StringComparer.OrdinalIgnoreCase).Count());
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
