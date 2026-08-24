using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using System.IO.Compression;
using System.Net;
using HomeDashboard.Api;
using HomeDashboard.Contracts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Host.UseWindowsService();
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddSingleton<IOptions<DashboardOptions>>(
    Options.Create(DashboardOptionsLoader.Load(builder.Configuration.GetSection("Dashboard"))));

builder.Services
    .AddOptions<DashboardSecurityOptions>()
    .Bind(builder.Configuration.GetSection("Security"));

builder.Services.AddHttpClient("health-checks", client => client.Timeout = TimeSpan.FromSeconds(3))
    .ConfigurePrimaryHttpMessageHandler(CreateOptimizedHandler);
builder.Services.AddHttpClient("news", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("HomeDashboard/0.1");
}).ConfigurePrimaryHttpMessageHandler(CreateOptimizedHandler);
builder.Services.AddHttpClient("operations", client =>
{
    client.Timeout = TimeSpan.FromSeconds(8);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("HomeDashboard/0.1");
}).ConfigurePrimaryHttpMessageHandler(CreateOptimizedHandler);
builder.Services.AddHttpClient("command-center", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("HomeDashboard/1.0");
}).ConfigurePrimaryHttpMessageHandler(CreateOptimizedHandler);
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.AddRateLimiter(rateLimit =>
{
    rateLimit.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimit.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});

builder.Services.AddSingleton<IDashboardService, DashboardService>();
builder.Services.AddSingleton<IServiceStatusProvider, ConfiguredServiceStatusProvider>();
builder.Services.AddSingleton<ISystemStatsProvider, LocalSystemStatsProvider>();
builder.Services.AddSingleton<INewsProvider, RssNewsProvider>();
builder.Services.AddSingleton<IRestartCoordinator, RestartCoordinator>();
builder.Services.AddSingleton<ISetupService, SetupService>();
builder.Services.AddSingleton<ILocalSettingsWriter, LocalSettingsWriter>();
builder.Services.AddSingleton<IAgentLocalSettingsWriter, AgentLocalSettingsWriter>();
builder.Services.AddSingleton<IOpmlImportService, OpmlImportService>();
builder.Services.AddSingleton<IOperationsService, OperationsService>();
builder.Services.AddSingleton<ICommandCenterService, CommandCenterService>();
builder.Services.AddSingleton<DiscordCommandProcessor>();
builder.Services.AddHostedService<DiscordBotService>();
builder.Services.AddSingleton<FileDashboardStateStore>();
builder.Services.AddSingleton<IAgentSnapshotStore>(provider => provider.GetRequiredService<FileDashboardStateStore>());
builder.Services.AddSingleton<IAgentCommandStore>(provider => provider.GetRequiredService<FileDashboardStateStore>());
builder.Services.AddSingleton<IApiKeyValidator, ApiKeyValidator>();
builder.Services.AddSingleton<IBrowserSessionStore, InMemoryBrowserSessionStore>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var eventJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
eventJsonOptions.Converters.Add(new JsonStringEnumConverter());

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins("http://localhost:5173", "https://localhost:5173", "http://127.0.0.1:5173", "https://127.0.0.1:5173")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseResponseCompression();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = context.File.Name.Contains('-', StringComparison.Ordinal)
            ? "public,max-age=31536000,immutable"
            : "no-cache";
    }
});
app.UseRateLimiter();
app.UseMiddleware<ApiKeyMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok", checkedAt = DateTimeOffset.UtcNow }));
app.MapGet("/setup/status", (ISetupService setup) => Results.Ok(setup.GetStatus()));
app.MapPost("/setup", async (SetupRequest request, ISetupService setup, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await setup.SaveAsync(request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.MapPost("/auth/login", (LoginRequest request, IApiKeyValidator validator, IBrowserSessionStore sessions, ICommandCenterService commandCenter, IOptions<DashboardSecurityOptions> options, HttpContext context) =>
{
    var profile = commandCenter.AuthenticateProfile(request.Username, request.Password);
    if (!validator.IsDashboardPasswordValid(request.Password) && profile is null)
    {
        return Results.Unauthorized();
    }

    var session = sessions.Create(out var token, profile?.Id ?? "owner", profile?.DisplayName ?? "Owner", profile?.Role ?? "Administrator");
    context.Response.Cookies.Append(options.Value.SessionCookieName, token, new CookieOptions
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps,
        Expires = session.ExpiresAt
    });

    return Results.Ok(session);
}).RequireRateLimiting("login");
app.MapPost("/auth/logout", (IBrowserSessionStore sessions, IOptions<DashboardSecurityOptions> options, HttpContext context) =>
{
    sessions.Remove(context.Request.Cookies[options.Value.SessionCookieName]);
    context.Response.Cookies.Delete(options.Value.SessionCookieName);
    return Results.NoContent();
});
app.MapGet("/auth/session", (IBrowserSessionStore sessions, IOptions<DashboardSecurityOptions> options, HttpContext context)
    => Results.Ok(sessions.Get(context.Request.Cookies[options.Value.SessionCookieName])));
app.MapGet("/api/dashboard", async (IDashboardService dashboard, CancellationToken cancellationToken)
    => Results.Ok(await dashboard.GetSnapshotAsync(cancellationToken)));
app.MapGet("/api/settings", (ISetupService setup) => Results.Ok(setup.GetSettings()));
app.MapPut("/api/settings", async (UpdateDashboardSettingsRequest request, ISetupService setup, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await setup.UpdateSettingsAsync(request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.MapPost("/api/settings/import-opml", (OpmlImportRequest request, IOpmlImportService importer) =>
{
    try
    {
        return Results.Ok(importer.Parse(request.Content));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.MapGet("/api/services", async (IServiceStatusProvider services, CancellationToken cancellationToken)
    => Results.Ok(await services.GetServicesAsync(cancellationToken)));
app.MapGet("/api/system", (ISystemStatsProvider stats) => Results.Ok(stats.GetStats()));
app.MapGet("/api/news", async (INewsProvider news, CancellationToken cancellationToken)
    => Results.Ok(await news.GetNewsAsync(cancellationToken)));
app.MapGet("/api/audit", (IAgentCommandStore commands) => Results.Ok(commands.GetRecentAuditEvents(50)));
app.MapGet("/api/commands", (IAgentCommandStore commands) => Results.Ok(commands.GetRecentCommands(50)));
app.MapGet("/api/operations", async (IOperationsService operations, CancellationToken cancellationToken)
    => Results.Ok(await operations.GetSnapshotAsync(cancellationToken)));
app.MapGet("/api/command-center", async (ICommandCenterService commandCenter, CancellationToken cancellationToken)
    => Results.Ok(await commandCenter.GetSnapshotAsync(cancellationToken)));
app.MapPost("/api/command-center/items", (CommandCenterItemRequest request, ICommandCenterService commandCenter, IBrowserSessionStore sessions, IOptions<DashboardSecurityOptions> security, HttpContext context) =>
{
    var session = sessions.Get(context.Request.Cookies[security.Value.SessionCookieName]);
    if (request.Kind.Equals("profile", StringComparison.OrdinalIgnoreCase) && session.IsAuthenticated && !string.Equals(session.Role, "Administrator", StringComparison.OrdinalIgnoreCase))
        return Results.Forbid();
    try { return Results.Ok(commandCenter.Upsert(request)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapDelete("/api/command-center/items/{kind}/{id}", (string kind, string id, ICommandCenterService commandCenter, IBrowserSessionStore sessions, IOptions<DashboardSecurityOptions> security, HttpContext context) =>
{
    var session = sessions.Get(context.Request.Cookies[security.Value.SessionCookieName]);
    if (kind.Equals("profile", StringComparison.OrdinalIgnoreCase) && session.IsAuthenticated && !string.Equals(session.Role, "Administrator", StringComparison.OrdinalIgnoreCase))
        return Results.Forbid();
    return commandCenter.Delete(kind, id) ? Results.NoContent() : Results.NotFound();
});
app.MapPost("/api/command-center/actions", async (CommandCenterActionRequest request, ICommandCenterService commandCenter, IBrowserSessionStore sessions, IOptions<DashboardSecurityOptions> security, HttpContext context, CancellationToken cancellationToken) =>
{
    var session = sessions.Get(context.Request.Cookies[security.Value.SessionCookieName]);
    if (request.Tool.StartsWith("machine.", StringComparison.OrdinalIgnoreCase) && session.IsAuthenticated && !string.Equals(session.Role, "Administrator", StringComparison.OrdinalIgnoreCase))
        return Results.Forbid();
    var result = await commandCenter.ExecuteAsync(request, cancellationToken);
    return result.Succeeded ? Results.Ok(result) : result.RequiresConfirmation ? Results.Json(result, statusCode: StatusCodes.Status409Conflict) : Results.BadRequest(result);
});
app.MapGet("/api/command-center/search", (string? q, ICommandCenterService commandCenter)
    => Results.Ok(commandCenter.Search(q ?? "")));
app.MapPost("/api/command-center/assistant", async (AssistantRequest request, ICommandCenterService commandCenter, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await commandCenter.AskAsync(request, cancellationToken)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPut("/api/command-center/integrations/{id}", (string id, UpdateIntegrationRequest request, ICommandCenterService commandCenter) =>
{
    try { return Results.Ok(commandCenter.UpdateIntegration(id, request)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/command-center/webhooks", (CommandCenterWebhook webhook, ICommandCenterService commandCenter)
    => Results.Accepted("/api/command-center", commandCenter.Ingest(webhook)));
app.MapPost("/api/command-center/webhooks/{source}", (string source, CommandCenterWebhook webhook, ICommandCenterService commandCenter)
    => Results.Accepted("/api/command-center", commandCenter.Ingest(webhook with { Source = source })));
app.MapGet("/api/command-center/files", (string? path, ICommandCenterService commandCenter) =>
{
    try { return Results.Ok(commandCenter.BrowseFiles(path)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/command-center/logs", async (int? count, ICommandCenterService commandCenter, CancellationToken cancellationToken)
    => Results.Ok(await commandCenter.GetSystemLogsAsync(count ?? 80, cancellationToken)));
app.MapPost("/api/downloads/control", async (DownloadControlRequest request, IOperationsService operations, CancellationToken cancellationToken)
    => await operations.ControlDownloadAsync(request, cancellationToken)
        ? Results.Ok(new { message = $"{request.Action} request accepted." })
        : Results.BadRequest(new { error = "That download action is not available or the client rejected it." }));
app.MapPost("/api/operations/arr/command", async (ArrCommandRequest request, IOperationsService operations,
    IBrowserSessionStore sessions, IOptions<DashboardSecurityOptions> security, HttpContext context, CancellationToken cancellationToken) =>
{
    var session = sessions.Get(context.Request.Cookies[security.Value.SessionCookieName]);
    var result = await operations.RunArrCommandAsync(request, session.DisplayName ?? session.ProfileId ?? "dashboard", cancellationToken);
    return result.Succeeded ? Results.Ok(result) : result.RequiresConfirmation
        ? Results.Json(result, statusCode: StatusCodes.Status409Conflict) : Results.BadRequest(new { error = result.Message });
});
app.MapGet("/api/discovery", async (IOperationsService operations, CancellationToken cancellationToken)
    => Results.Ok(await operations.DiscoverAsync(cancellationToken)));
app.MapGet("/api/maintenance", (IOperationsService operations) => Results.Ok(operations.GetMaintenance()));
app.MapPost("/api/maintenance", (CreateMaintenanceWindowRequest request, IOperationsService operations) =>
{
    try
    {
        return Results.Ok(operations.AddMaintenance(request, "dashboard"));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.MapDelete("/api/maintenance/{id}", (string id, IOperationsService operations)
    => operations.RemoveMaintenance(id) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/backup", (ISetupService setup, IOperationsService operations, ICommandCenterService commandCenter) => Results.Ok(new DashboardBackup(
    1,
    DateTimeOffset.UtcNow,
    setup.GetSettings(),
    operations.GetMaintenance(),
    typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
    commandCenter.Export())));
app.MapPost("/api/backup/restore", async (DashboardBackup backup, ISetupService setup, IOperationsService operations, ICommandCenterService commandCenter, CancellationToken cancellationToken) =>
{
    if (backup.FormatVersion != 1 || backup.Settings is null || backup.Maintenance is null)
    {
        return Results.BadRequest(new { error = "This backup format is not supported." });
    }
    if (backup.Settings.Services.Count > 200 || backup.Settings.NewsFeeds.Count > 500 || backup.Maintenance.Count > 1_000)
    {
        return Results.BadRequest(new { error = "This backup exceeds the supported dashboard limits." });
    }

    var request = new UpdateDashboardSettingsRequest(
        backup.Settings.DefaultAgentId,
        backup.Settings.IncludeRecommendedFeeds,
        backup.Settings.Services.Select(service => new UpdateServiceSetting(
            service.Id, service.Name, service.Kind, service.Description, service.Url, service.HealthUrl, null, false, service.RestartEnabled)).ToArray(),
        backup.Settings.NewsFeeds);
    await setup.UpdateSettingsAsync(request, cancellationToken);
    operations.ReplaceMaintenance(backup.Maintenance);
    if (backup.CommandCenter is not null)
    {
        try { commandCenter.Restore(backup.CommandCenter); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
    return Results.Ok(new { message = "Backup restored. Restart the API to apply configuration changes.", requiresRestart = true });
});
app.MapGet("/api/events", async (IDashboardService dashboard, HttpContext context, CancellationToken cancellationToken) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    while (!cancellationToken.IsCancellationRequested)
    {
        var snapshot = await dashboard.GetSnapshotAsync(cancellationToken);
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(snapshot, eventJsonOptions)}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
    }
});
app.MapPost("/api/services/{id}/restart", (string id, RestartRequest request, IRestartCoordinator restarts) =>
{
    var result = restarts.QueueRestart(id, request);
    return result.State == RestartState.Queued ? Results.Accepted($"/api/services/{id}", result) : Results.BadRequest(result);
});
app.MapPost("/api/agent/snapshot", (AgentSnapshot snapshot, IAgentSnapshotStore store) =>
{
    store.Save(snapshot);
    return Results.Accepted($"/api/agent/{snapshot.AgentId}/latest", new { acceptedAt = DateTimeOffset.UtcNow });
});
app.MapGet("/api/agents", (IAgentSnapshotStore store) => Results.Ok(store.GetAll()));
app.MapGet("/api/agent/{agentId}/latest", (string agentId, IAgentSnapshotStore store) =>
{
    var snapshot = store.GetLatest(agentId);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
});
app.MapGet("/api/agent/{agentId}/history", (string agentId, IAgentSnapshotStore store)
    => Results.Ok(store.GetHistory(agentId)));
app.MapGet("/api/agent/{agentId}/commands/next", (string agentId, IAgentCommandStore commands) =>
{
    var command = commands.DequeueNext(agentId);
    return command is null ? Results.NoContent() : Results.Ok(command);
});
app.MapPost("/api/agent/{agentId}/commands/{commandId}/complete", (string agentId, string commandId, AgentCommandCompletion completion, IAgentCommandStore commands) =>
{
    commands.Complete(agentId, commandId, completion);
    return Results.NoContent();
});
app.MapFallbackToFile("index.html");

static SocketsHttpHandler CreateOptimizedHandler() => new()
{
    AutomaticDecompression = DecompressionMethods.All,
    ConnectTimeout = TimeSpan.FromSeconds(3),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    MaxConnectionsPerServer = 8
};

app.Run();

public partial class Program;
