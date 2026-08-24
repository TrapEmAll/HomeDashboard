using Discord;

namespace HomeDashboard.Api;

internal static class DiscordSlashCommandCatalog
{
    public const string Name = "home";
    public const string SchemaVersion = "1";

    public static SlashCommandProperties Build()
    {
        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Control HomeDashboard and capture things from Discord")
            .AddOption(Group("task", "Manage tasks",
                Command("add", "Add a task", Text("title", "What needs doing", true), Text("due", "Date/time, for example tomorrow 18:00"), Choice("priority", "Task priority", "Normal", "Low", "High", "Urgent"), Text("list", "Task list")),
                Command("list", "Show open tasks"),
                Command("done", "Complete a task", Existing("task", "Task to complete")),
                Command("remove", "Remove a task", Existing("task", "Task to remove"))))
            .AddOption(Group("shopping", "Manage shopping lists",
                Command("add", "Add one or more items", Text("items", "Comma-separated shopping items", true), Text("list", "Shopping list")),
                Command("list", "Show unpurchased items"),
                Command("done", "Mark an item purchased", Existing("shopping_item", "Shopping item")),
                Command("remove", "Remove a shopping item", Existing("shopping_item", "Shopping item"))))
            .AddOption(Group("calendar", "Manage the agenda",
                Command("add", "Add an agenda entry", Text("title", "Event title", true), Text("when", "Date and time", true), Text("location", "Optional location")),
                Command("list", "Show upcoming agenda entries"),
                Command("remove", "Remove an agenda entry", Existing("event", "Agenda entry"))))
            .AddOption(Group("note", "Manage quick notes",
                Command("add", "Save a note", Text("title", "Note title", true), Text("body", "Note details")),
                Command("list", "Show recent notes"),
                Command("remove", "Remove a note", Existing("note", "Note"))))
            .AddOption(Group("package", "Manage tracked deliveries",
                Command("add", "Track a delivery", Text("description", "What is arriving", true), Text("carrier", "Carrier"), Text("tracking", "Tracking number"), Text("eta", "Expected delivery date")),
                Command("list", "Show tracked deliveries"),
                Command("remove", "Stop tracking a delivery", Existing("package", "Package"))))
            .AddOption(Group("media", "Manage media requests",
                Command("add", "Request a movie, show, book, or game", Text("title", "Title", true), Choice("type", "Media type", "Movie", "TV", "Book", "Game", "Music", "Other")),
                Command("list", "Show recent media requests"),
                Command("remove", "Remove a media request", Existing("media", "Media request"))))
            .AddOption(Group("inbox", "Manage dashboard alerts",
                Command("list", "Show unread alerts"),
                Command("ack", "Acknowledge an alert", Existing("alert", "Alert")),
                Command("snooze", "Snooze an alert", Existing("alert", "Alert"), Number("minutes", "Minutes to snooze", 60, 5, 10080))))
            .AddOption(Group("reminder", "Create dashboard reminders",
                Command("add", "Create a reminder", Text("title", "Reminder title", true), Text("message", "Reminder details"))))
            .AddOption(Group("automation", "Inspect and run automations",
                Command("list", "Show enabled automations"),
                Command("run", "Run an automation", Existing("automation", "Automation"))))
            .AddOption(Group("device", "Inspect and control Home Assistant devices",
                Command("list", "Show Home Assistant devices"),
                Command("control", "Call a Home Assistant service", Existing("device", "Device"),
                    Choice("service", "Service to call", ["toggle", "turn_on", "turn_off"], true),
                    Boolean("confirm", "Confirm this device action", true))))
            .AddOption(Group("system", "Inspect HomeDashboard",
                Command("status", "Show a concise daily status"),
                Command("integrations", "Show integration connection status"),
                Command("assets", "Show systems needing attention"),
                Command("devices", "Show Home Assistant devices"),
                Command("profiles", "Show household profiles"),
                Command("activity", "Show recent dashboard activity"),
                Command("search", "Search the command center", Text("query", "What to find", true)),
                Command("mode", "Set the household mode", Choice("mode", "Mode", ["Home", "Away", "Sleep", "Work", "Gaming", "Movie", "Guest"], true))))
            .AddOption(Command("help", "Show all HomeDashboard Discord commands"))
            .Build();
    }

    private static SlashCommandOptionBuilder Group(string name, string description, params SlashCommandOptionBuilder[] commands) =>
        new SlashCommandOptionBuilder().WithName(name).WithDescription(description).WithType(ApplicationCommandOptionType.SubCommandGroup).AddOptions(commands);

    private static SlashCommandOptionBuilder Command(string name, string description, params SlashCommandOptionBuilder[] options) =>
        new SlashCommandOptionBuilder().WithName(name).WithDescription(description).WithType(ApplicationCommandOptionType.SubCommand).AddOptions(options);

    private static SlashCommandOptionBuilder Text(string name, string description, bool required = false) =>
        new SlashCommandOptionBuilder().WithName(name).WithDescription(description).WithType(ApplicationCommandOptionType.String).WithRequired(required).WithMaxLength(500);

    private static SlashCommandOptionBuilder Existing(string name, string description) => Text(name, description, true).WithAutocomplete(true);

    private static SlashCommandOptionBuilder Choice(string name, string description, params string[] values) => Choice(name, description, values, false);

    private static SlashCommandOptionBuilder Choice(string name, string description, string[] values, bool required)
    {
        var option = Text(name, description, required);
        foreach (var value in values) option.AddChoice(value, value);
        return option;
    }

    private static SlashCommandOptionBuilder Number(string name, string description, long defaultValue, double min, double max) =>
        new SlashCommandOptionBuilder().WithName(name).WithDescription($"{description} (default {defaultValue})")
            .WithType(ApplicationCommandOptionType.Integer).WithMinValue(min).WithMaxValue(max);

    private static SlashCommandOptionBuilder Boolean(string name, string description, bool required = false) =>
        new SlashCommandOptionBuilder().WithName(name).WithDescription(description).WithType(ApplicationCommandOptionType.Boolean).WithRequired(required);
}
