using System.Xml.Linq;
using HomeDashboard.Api;
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
}
