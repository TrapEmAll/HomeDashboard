using HomeDashboard.Agent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddWindowsService(options => options.ServiceName = "HomeDashboard Agent");
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services
    .AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection("Agent"));

builder.Services.AddHttpClient("dashboard-api", (services, client) =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
    client.BaseAddress = options.DashboardApiUrl;
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton<ISystemSnapshotCollector, SystemSnapshotCollector>();
builder.Services.AddSingleton<IWindowsServiceSnapshotCollector, WindowsServiceSnapshotCollector>();
builder.Services.AddSingleton<IAgentCollector, AgentCollector>();
builder.Services.AddSingleton<IAgentPublisher, AgentPublisher>();
builder.Services.AddSingleton<IAgentCommandExecutor, AgentCommandExecutor>();
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
