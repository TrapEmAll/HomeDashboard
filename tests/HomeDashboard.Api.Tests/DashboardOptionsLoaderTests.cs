using HomeDashboard.Contracts;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class DashboardOptionsLoaderTests
{
    [Fact]
    public void Load_repairs_legacy_urls_and_skips_invalid_entries()
    {
        var values = new Dictionary<string, string?>
        {
            ["Dashboard:DefaultAgentId"] = "media-pc",
            ["Dashboard:AgentHistoryLimit"] = "invalid",
            ["Dashboard:Services:0:Id"] = "plex",
            ["Dashboard:Services:0:Name"] = "Plex",
            ["Dashboard:Services:0:Kind"] = "not-a-kind",
            ["Dashboard:Services:0:Url"] = "[http://localhost:32400/](http://localhost:32400/)",
            ["Dashboard:Services:1:Id"] = "PLEX",
            ["Dashboard:Services:1:Name"] = "Duplicate",
            ["Dashboard:NewsFeeds:0:Name"] = "Legacy feed",
            ["Dashboard:NewsFeeds:0:Url"] = "[https://example.com/feed.xml](https://example.com/feed.xml)",
            ["Dashboard:NewsFeeds:0:Kind"] = "unexpected",
            ["Dashboard:NewsFeeds:1:Name"] = "Broken feed",
            ["Dashboard:NewsFeeds:1:Url"] = "not a URL"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var options = DashboardOptionsLoader.Load(configuration.GetSection("Dashboard"));

        Assert.Equal("media-pc", options.DefaultAgentId);
        Assert.Equal(120, options.AgentHistoryLimit);
        var service = Assert.Single(options.Services);
        Assert.Equal(ServiceKind.Generic, service.Kind);
        Assert.Equal("http://localhost:32400/", service.Url!.AbsoluteUri);
        var feed = Assert.Single(options.NewsFeeds);
        Assert.Equal(NewsContentKind.Article, feed.Kind);
        Assert.Equal("https://example.com/feed.xml", feed.Url.AbsoluteUri);
    }
}
