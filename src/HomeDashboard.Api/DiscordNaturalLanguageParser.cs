using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HomeDashboard.Api;

internal sealed record DiscordParseResult(string Command, string? Reply, string Intent, string? Entity)
{
    public bool Matched => Command.Length > 0;
}

internal static class DiscordNaturalLanguageParser
{
    private static readonly string[] KnownCommands =
    [
        "restart service <name>", "backup now", "restore <id>", "maintenance <task>",
        "machine lock <agentId>", "machine sleep <agentId>", "machine restart <agentId>",
        "machine shutdown <agentId>", "status", "health", "download queue", "help"
    ];

    public static DiscordParseResult Parse(string input, string logPath)
    {
        var text = Regex.Replace(input.Trim(), @"\s+", " ");
        if (text.Length == 0) return new("", "Please enter a HomeDashboard command.", "", null);

        var direct = ParseDirect(text);
        if (direct is not null) return direct;

        var match = Regex.Match(text, @"^(?:please\s+)?(?:restart|reboot)(?:\s+service)?\s+(?<target>.+)$", RegexOptions.IgnoreCase);
        if (match.Success) return Result($"restart service {match.Groups["target"].Value.Trim()}", "restart", match.Groups["target"].Value.Trim());

        match = Regex.Match(text, @"^(?:is|are|what(?:'s| is)|show|check)\s+(?:the\s+)?download\s+queue(?:\s+(?:backed\s+up|healthy|ok|okay|clear|status))?[?!.]*$", RegexOptions.IgnoreCase);
        if (match.Success) return Result("status", "status", "download queue");

        match = Regex.Match(text, @"^(?:what(?:'s| is)|how is)\s+(?:the\s+)?(?:system|dashboard)\s*(?:status|health)?[?!.]*$", RegexOptions.IgnoreCase);
        if (match.Success) return Result("status", "status", "system");

        match = Regex.Match(text, @"^(?:back\s*up|backup)(?:\s+now)?$", RegexOptions.IgnoreCase);
        if (match.Success) return Result("backup now", "backup", "now");

        match = Regex.Match(text, @"^restore\s+(?<id>\S+)$", RegexOptions.IgnoreCase);
        if (match.Success) return Result($"restore {match.Groups["id"].Value}", "restore", match.Groups["id"].Value);

        match = Regex.Match(text, @"^maintenance\s+(?<task>.+)$", RegexOptions.IgnoreCase);
        if (match.Success) return Result($"maintenance {match.Groups["task"].Value.Trim()}", "maintenance", match.Groups["task"].Value.Trim());

        match = Regex.Match(text, @"^(?:machine\s+)?(?<action>lock|sleep|shutdown)\s+(?:machine\s+)?(?<agent>\S+)$", RegexOptions.IgnoreCase);
        if (match.Success) return Result($"machine {match.Groups["action"].Value.ToLowerInvariant()} {match.Groups["agent"].Value}", "machine", match.Groups["agent"].Value);

        var closest = Closest(text);
        LogUnmatched(text, logPath);
        return new("", $"Did you mean `{closest}`?", "", null);
    }

    private static DiscordParseResult? ParseDirect(string text)
    {
        var find = Regex.Match(text, @"^find\s+(?<query>.+)$", RegexOptions.IgnoreCase);
        if (find.Success) return Result($"search {find.Groups["query"].Value.Trim()}", "search", find.Groups["query"].Value.Trim());
        if (Regex.IsMatch(text, @"^(?:list rules|(?:run|enable|disable) rule\s+.+)$", RegexOptions.IgnoreCase))
            return Result(text, text.Split(' ', 2)[0].ToLowerInvariant(), text.Length > text.IndexOf(' ') + 1 ? text[(text.IndexOf(' ') + 1)..] : null);
        var first = text.Split(' ', 2)[0].ToLowerInvariant();
        if (first is "light" or "lights" or "lamp" or "lamps" or "thermostat" or "thermostats" or "temperature" or "climate" or "fan" or "fans" or "switch" or "switches")
            return Result($"home control {text}", "home", text.Length > first.Length ? text[(first.Length + 1)..] : null);
        if (first is "status" or "health" or "help" or "brief" or "attention" or "search" or "ask")
            return Result(text, first, text.Length > first.Length ? text[(first.Length + 1)..] : null);
        if (first is "add" or "list" or "done" or "remove" or "missing" or "queue" or "feeds" or "rss"
            or "unread" or "latest" or "subscribe" or "unsubscribe" or "mark"
            or "task" or "tasks" or "shopping" or "shop" or "agenda" or "calendar" or "note" or "notes"
            or "package" or "delivery" or "media" or "request" or "inbox" or "alert" or "notify" or "notification"
            or "reminder" or "automation" or "automations" or "assistant" or "device" or "devices" or "home" or "mode"
            or "system" or "integration" or "integrations" or "asset" or "assets" or "activity")
            return Result(text, first, text.Length > first.Length ? text[(first.Length + 1)..] : null);
        return null;
    }

    private static DiscordParseResult Result(string command, string intent, string? entity) => new(command, null, intent, entity);

    private static string Closest(string input) => KnownCommands
        .OrderBy(command => Distance(input.ToLowerInvariant(), command.ToLowerInvariant()))
        .ThenBy(command => command.Length)
        .First();

    private static int Distance(string left, string right)
    {
        var row = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (left[i - 1] == right[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[^1];
    }

    private static void LogUnmatched(string input, string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path, AppContext.BaseDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
            File.AppendAllText(fullPath, $"{DateTimeOffset.UtcNow:O}\tlength={input.Length}\tfingerprint={fingerprint}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

