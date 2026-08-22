using HomeDashboard.Contracts;

namespace HomeDashboard.Api;

public static class RecommendedFeedCatalog
{
    public static IReadOnlyList<NewsFeedDefinition> All { get; } =
    [
        Article("Ars Technica", "https://feeds.arstechnica.com/arstechnica/index", "Technology"),
        Article("The Verge", "https://www.theverge.com/rss/index.xml", "Technology"),
        Article("TechCrunch", "https://techcrunch.com/feed/", "Technology"),
        Article("MIT Technology Review", "https://www.technologyreview.com/feed/", "Technology"),
        Article("Hacker News", "https://news.ycombinator.com/rss", "Development"),
        Article("GitHub Blog", "https://github.blog/feed/", "Development"),
        Article("Cloudflare Blog", "https://blog.cloudflare.com/rss/", "Infrastructure"),
        Article("BleepingComputer", "https://www.bleepingcomputer.com/feed/", "Cybersecurity"),
        Article("Krebs on Security", "https://krebsonsecurity.com/feed/", "Cybersecurity"),
        Article("Microsoft Security", "https://www.microsoft.com/en-us/security/blog/feed/", "Cybersecurity"),
        Article("Google Security", "https://security.googleblog.com/feeds/posts/default", "Cybersecurity"),
        Article("Cisco Security", "https://blogs.cisco.com/security/feed", "Cybersecurity"),
        Article("Schneier on Security", "https://www.schneier.com/feed/atom/", "Cybersecurity"),
        Article("Dark Reading", "https://www.darkreading.com/rss/all.xml", "Cybersecurity"),
        Article("The Hacker News", "https://feeds.feedburner.com/TheHackersNews", "Cybersecurity"),
        Article("NIST Cybersecurity Insights", "https://www.nist.gov/blogs/cybersecurity-insights/rss.xml", "Cybersecurity"),
        Podcast("Darknet Diaries", "https://feeds.megaphone.fm/darknetdiaries", "Cybersecurity"),
        Podcast("Risky Business", "https://risky.biz/feeds/risky-business/", "Cybersecurity"),
        Podcast("Security Now", "https://feeds.twit.tv/sn.xml", "Cybersecurity"),
        Podcast("Smashing Security", "https://www.smashingsecurity.com/rss", "Cybersecurity"),
        Podcast("SANS Internet Stormcenter", "https://isc.sans.edu/dailypodcast.xml", "Cybersecurity"),
        Podcast("Ahead of the Threat", "https://www.fbi.gov/feeds/ahead-of-the-threat-itunes", "Cybersecurity"),
        Podcast("The Changelog", "https://changelog.com/podcast/feed", "Development"),
        Podcast("Syntax", "https://feed.syntax.fm/rss", "Development"),
        Podcast("Accidental Tech Podcast", "https://atp.fm/rss", "Technology"),
        Podcast("Decoder", "https://feeds.megaphone.fm/decoder", "Technology")
    ];

    private static NewsFeedDefinition Article(string name, string url, string category)
        => new() { Name = name, Url = new Uri(url), Category = category };

    private static NewsFeedDefinition Podcast(string name, string url, string category)
        => new()
        {
            Name = name,
            Url = new Uri(url),
            Kind = NewsContentKind.Podcast,
            Category = category,
            ProviderUrl = new Uri($"https://open.spotify.com/search/{Uri.EscapeDataString(name)}")
        };
}
