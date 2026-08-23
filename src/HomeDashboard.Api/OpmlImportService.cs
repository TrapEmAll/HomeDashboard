using System.Xml;
using System.Xml.Linq;
using HomeDashboard.Contracts;

namespace HomeDashboard.Api;

public interface IOpmlImportService
{
    OpmlImportPreview Parse(string content);
}

public sealed class OpmlImportService : IOpmlImportService
{
    private const int MaximumCharacters = 2_000_000;

    public OpmlImportPreview Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("The OPML file is empty.");
        }

        if (content.Length > MaximumCharacters)
        {
            throw new InvalidOperationException("The OPML file is larger than the 2 MB import limit.");
        }

        XDocument document;
        try
        {
            using var textReader = new StringReader(content);
            using var xmlReader = XmlReader.Create(textReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumCharacters
            });
            document = XDocument.Load(xmlReader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException("The selected file is not valid OPML XML.", ex);
        }

        if (document.Root is null || !IsNamed(document.Root, "opml"))
        {
            throw new InvalidOperationException("The selected file does not contain an OPML document.");
        }

        var body = document.Root.Elements().FirstOrDefault(element => IsNamed(element, "body"));
        if (body is null)
        {
            throw new InvalidOperationException("The OPML document does not contain a body section.");
        }

        var feeds = new List<NewsFeedSetting>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var feedOutlineCount = 0;
        var skippedCount = 0;

        ParseOutlines(body, [], feeds, seenUrls, ref feedOutlineCount, ref skippedCount);
        return new OpmlImportPreview(feeds, feedOutlineCount, skippedCount);
    }

    private static void ParseOutlines(
        XElement parent,
        IReadOnlyList<string> categoryPath,
        List<NewsFeedSetting> feeds,
        HashSet<string> seenUrls,
        ref int feedOutlineCount,
        ref int skippedCount)
    {
        foreach (var outline in parent.Elements().Where(element => IsNamed(element, "outline")))
        {
            var xmlUrl = Attribute(outline, "xmlUrl");
            var title = FirstValue(Attribute(outline, "title"), Attribute(outline, "text"));
            var childCategoryPath = categoryPath;

            if (string.IsNullOrWhiteSpace(xmlUrl) && outline.Elements().Any(element => IsNamed(element, "outline")))
            {
                childCategoryPath = string.IsNullOrWhiteSpace(title)
                    ? categoryPath
                    : [.. categoryPath, title.Trim()];
            }

            if (!string.IsNullOrWhiteSpace(xmlUrl))
            {
                feedOutlineCount++;
                if (!TryHttpUri(xmlUrl, out var feedUri) || !seenUrls.Add(NormalizeForComparison(feedUri)))
                {
                    skippedCount++;
                }
                else
                {
                    var explicitCategory = Attribute(outline, "category")?.Trim().Trim('/');
                    var category = !string.IsNullOrWhiteSpace(explicitCategory)
                        ? explicitCategory.Replace("/", " / ", StringComparison.Ordinal)
                        : categoryPath.Count > 0 ? string.Join(" / ", categoryPath) : "Imported";
                    var name = string.IsNullOrWhiteSpace(title) ? feedUri.Host : title.Trim();
                    var htmlUrl = Attribute(outline, "htmlUrl");
                    var providerUrl = TryHttpUri(htmlUrl, out var providerUri) ? providerUri.ToString() : null;
                    var kind = IsPodcast(outline, category, name) ? NewsContentKind.Podcast : NewsContentKind.Article;

                    feeds.Add(new NewsFeedSetting(name, feedUri.ToString(), kind, category, providerUrl));
                }
            }

            ParseOutlines(outline, childCategoryPath, feeds, seenUrls, ref feedOutlineCount, ref skippedCount);
        }
    }

    private static bool IsPodcast(XElement outline, string category, string name)
        => new[] { Attribute(outline, "type"), category, name }
            .Any(value => value?.Contains("podcast", StringComparison.OrdinalIgnoreCase) == true);

    private static bool TryHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate)
            && candidate.Scheme is "http" or "https")
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static string NormalizeForComparison(Uri uri)
        => uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped).TrimEnd('/');

    private static string? Attribute(XElement element, string name)
        => element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool IsNamed(XElement element, string name)
        => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static string? FirstValue(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
