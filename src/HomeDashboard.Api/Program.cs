using System.Text.Json.Serialization;
using HomeDashboard.Api;
using HomeDashboard.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services
    .AddOptions<DashboardOptions>()
    .Bind(builder.Configuration.GetSection("Dashboard"))
    .Validate(options => options.Services.Select(service => service.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == options.Services.Count, "Service IDs must be unique.");

builder.Services
    .AddOptions<DashboardSecurityOptions>()
    .Bind(builder.Configuration.GetSection("Security"));

builder.Services.AddHttpClient("health-checks", client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHttpClient("news", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("HomeDashboard/0.1");
});

builder.Services.AddSingleton<IDashboardService, DashboardService>();
builder.Services.AddSingleton<IServiceStatusProvider, ConfiguredServiceStatusProvider>();
builder.Services.AddSingleton<ISystemStatsProvider, LocalSystemStatsProvider>();
builder.Services.AddSingleton<INewsProvider, RssNewsProvider>();
builder.Services.AddSingleton<IRestartCoordinator, RestartCoordinator>();
builder.Services.AddSingleton<IAgentSnapshotStore, InMemoryAgentSnapshotStore>();
builder.Services.AddSingleton<IApiKeyValidator, ApiKeyValidator>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());
});

var app = builder.Build();

app.UseCors();
app.UseMiddleware<ApiKeyMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok", checkedAt = DateTimeOffset.UtcNow }));
app.MapGet("/api/dashboard", async (IDashboardService dashboard, CancellationToken cancellationToken)
    => Results.Ok(await dashboard.GetSnapshotAsync(cancellationToken)));
app.MapGet("/api/services", async (IServiceStatusProvider services, CancellationToken cancellationToken)
    => Results.Ok(await services.GetServicesAsync(cancellationToken)));
app.MapGet("/api/system", (ISystemStatsProvider stats) => Results.Ok(stats.GetStats()));
app.MapGet("/api/news", async (INewsProvider news, CancellationToken cancellationToken)
    => Results.Ok(await news.GetNewsAsync(cancellationToken)));
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
app.MapGet("/api/agent/{agentId}/latest", (string agentId, IAgentSnapshotStore store) =>
{
    var snapshot = store.GetLatest(agentId);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
});

app.Run();

public partial class Program;
