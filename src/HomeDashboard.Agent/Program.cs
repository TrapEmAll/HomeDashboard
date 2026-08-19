using HomeDashboard.Agent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection("Agent"));

builder.Services.AddSingleton<ISystemSnapshotCollector, SystemSnapshotCollector>();
builder.Services.AddSingleton<IWindowsServiceSnapshotCollector, WindowsServiceSnapshotCollector>();
builder.Services.AddSingleton<IAgentCollector, AgentCollector>();
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
