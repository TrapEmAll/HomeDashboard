using System.Net.Http.Json;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Agent;

public interface IAgentPublisher
{
    Task PublishAsync(AgentSnapshot snapshot, CancellationToken cancellationToken);
    Task<AgentCommand?> GetNextCommandAsync(CancellationToken cancellationToken);
    Task CompleteCommandAsync(AgentCommand command, AgentCommandCompletion completion, CancellationToken cancellationToken);
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

    public async Task<AgentCommand?> GetNextCommandAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("dashboard-api");
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/agent/{options.Value.AgentId}/commands/next");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentCommand>(cancellationToken);
    }

    public async Task CompleteCommandAsync(AgentCommand command, AgentCommandCompletion completion, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("dashboard-api");
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, $"/api/agent/{options.Value.AgentId}/commands/{command.Id}/complete");
        request.Content = JsonContent.Create(completion);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string path)
    {
        var apiKey = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Agent:ApiKey must be configured before the agent can connect.");
        }

        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-HomeDashboard-Key", apiKey);
        return request;
    }
}
