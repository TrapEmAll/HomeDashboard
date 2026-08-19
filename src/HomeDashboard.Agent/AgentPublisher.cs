using System.Net.Http.Json;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Agent;

public interface IAgentPublisher
{
    Task PublishAsync(AgentSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed class AgentPublisher(
    IHttpClientFactory httpClientFactory,
    IOptions<AgentOptions> options) : IAgentPublisher
{
    public async Task PublishAsync(AgentSnapshot snapshot, CancellationToken cancellationToken)
    {
        var apiKey = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Agent:ApiKey must be configured before the agent can connect.");
        }

        var client = httpClientFactory.CreateClient("dashboard-api");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/agent/snapshot")
        {
            Content = JsonContent.Create(snapshot)
        };
        request.Headers.Add("X-HomeDashboard-Key", apiKey);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
