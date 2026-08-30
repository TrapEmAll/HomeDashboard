using HomeDashboard.Contracts;
using Microsoft.Extensions.Configuration;

namespace HomeDashboard.Api;

public static class DashboardOptionsLoader
{
    public static DashboardOptions Load(IConfiguration section)
    {
        var services = section.GetSection("Services")
            .GetChildren()
            .Select(ReadService)
            .Where(service => service is not null)
            .Cast<ServiceDefinition>()
            .GroupBy(service => service.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var feeds = section.GetSection("NewsFeeds")
            .GetChildren()
            .Select(ReadFeed)
            .Where(feed => feed is not null)
            .Cast<NewsFeedDefinition>()
            .GroupBy(feed => feed.Url.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        return new DashboardOptions
        {
            DefaultAgentId = ReadString(section, "DefaultAgentId", "server-pc"),
            DataPath = ReadString(section, "DataPath", "data/homedashboard-state.json"),
            AgentHistoryLimit = ReadPositiveInt(section, "AgentHistoryLimit", 120),
            NetworkProbeTarget = string.IsNullOrWhiteSpace(section["NetworkProbeTarget"]) ? null : section["NetworkProbeTarget"]!.Trim(),
            NetworkProbeIntervalSeconds = ReadPositiveInt(section, "NetworkProbeIntervalSeconds", 30),
            HostDetailRefreshSeconds = ReadPositiveInt(section, "HostDetailRefreshSeconds", 60),
            DiscordConfirmationTimeoutSeconds = ReadPositiveInt(section, "DiscordConfirmationTimeoutSeconds", 60),
            IncludeRecommendedFeeds = ReadBool(section, "IncludeRecommendedFeeds", true),
            Services = services,
            NewsFeeds = feeds
        };
    }

    private static ServiceDefinition? ReadService(IConfiguration section)
    {
        var id = section["Id"]?.Trim();
        var name = section["Name"]?.Trim();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new ServiceDefinition
        {
            Id = id,
            Name = name,
            Kind = ReadEnum(section["Kind"], ServiceKind.Generic),
            Description = section["Description"]?.Trim() ?? "",
            Url = ReadHttpUri(section["Url"]),
            HealthUrl = ReadHttpUri(section["HealthUrl"]),
            ApiKey = section["ApiKey"],
            RestartEnabled = bool.TryParse(section["RestartEnabled"], out var restartEnabled) && restartEnabled
        };
    }

    private static NewsFeedDefinition? ReadFeed(IConfiguration section)
    {
        var url = ReadHttpUri(section["Url"]);
        if (url is null)
        {
            return null;
        }

        return new NewsFeedDefinition
        {
            Name = ReadString(section, "Name", url.Host),
            Url = url,
            Kind = ReadEnum(section["Kind"], NewsContentKind.Article),
            Category = ReadString(section, "Category", "Technology"),
            ProviderUrl = ReadHttpUri(section["ProviderUrl"])
        };
    }

    internal static Uri? ReadHttpUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        var markdownStart = value.IndexOf("](", StringComparison.Ordinal);
        if (value.StartsWith("[", StringComparison.Ordinal) && markdownStart > 0 && value.EndsWith(")", StringComparison.Ordinal))
        {
            value = value[(markdownStart + 2)..^1].Trim();
        }

        value = value.Trim('<', '>');
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
                ? uri
                : null;
    }

    private static TEnum ReadEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(value, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;

    private static string ReadString(IConfiguration section, string key, string fallback)
        => string.IsNullOrWhiteSpace(section[key]) ? fallback : section[key]!.Trim();

    private static bool ReadBool(IConfiguration section, string key, bool fallback)
        => bool.TryParse(section[key], out var value) ? value : fallback;

    private static int ReadPositiveInt(IConfiguration section, string key, int fallback)
        => int.TryParse(section[key], out var value) && value > 0 ? value : fallback;
}

