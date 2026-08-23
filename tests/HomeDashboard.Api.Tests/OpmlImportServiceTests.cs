using HomeDashboard.Contracts;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class OpmlImportServiceTests
{
    private readonly OpmlImportService importer = new();

    [Fact]
    public void Imports_nested_feeds_and_preserves_folder_categories()
    {
        const string opml = """
            <?xml version="1.0" encoding="utf-8"?>
            <opml version="2.0">
              <head><title>Subscriptions</title></head>
              <body>
                <outline text="Technology">
                  <outline text="Example Tech" type="rss" xmlUrl="https://example.com/feed.xml" htmlUrl="https://example.com/" />
                </outline>
                <outline text="Podcasts">
                  <outline title="Example Show" type="rss" xmlUrl="https://audio.example.com/rss" />
                </outline>
              </body>
            </opml>
            """;

        var result = importer.Parse(opml);

        Assert.Equal(2, result.FeedOutlineCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Collection(result.Feeds,
            feed =>
            {
                Assert.Equal("Example Tech", feed.Name);
                Assert.Equal("Technology", feed.Category);
                Assert.Equal(NewsContentKind.Article, feed.Kind);
                Assert.Equal("https://example.com/", feed.ProviderUrl);
            },
            feed =>
            {
                Assert.Equal("Example Show", feed.Name);
                Assert.Equal("Podcasts", feed.Category);
                Assert.Equal(NewsContentKind.Podcast, feed.Kind);
            });
    }

    [Fact]
    public void Skips_duplicate_and_invalid_feed_urls()
    {
        const string opml = """
            <opml version="1.0"><body>
              <outline text="First" xmlUrl="https://example.com/feed/" />
              <outline text="Duplicate" xmlUrl="https://example.com/feed" />
              <outline text="Invalid" xmlUrl="file:///private/feed.xml" />
            </body></opml>
            """;

        var result = importer.Parse(opml);

        Assert.Equal(3, result.FeedOutlineCount);
        Assert.Single(result.Feeds);
        Assert.Equal(2, result.SkippedCount);
    }

    [Fact]
    public void Rejects_documents_with_dtds()
    {
        const string opml = """
            <!DOCTYPE opml [<!ENTITY example SYSTEM "file:///private.txt">]>
            <opml version="2.0"><body><outline text="&example;" xmlUrl="https://example.com/feed" /></body></opml>
            """;

        var error = Assert.Throws<InvalidOperationException>(() => importer.Parse(opml));

        Assert.Contains("valid OPML XML", error.Message);
    }

    [Fact]
    public void Rejects_non_opml_xml()
    {
        var error = Assert.Throws<InvalidOperationException>(() => importer.Parse("<rss />"));

        Assert.Contains("OPML document", error.Message);
    }
}
