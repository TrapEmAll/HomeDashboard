using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Api;

public sealed class DashboardSecurityOptions
{
    public string? DashboardApiKey { get; init; }
    public string? AgentApiKey { get; init; }
}

public interface IApiKeyValidator
{
    bool IsDashboardKeyValid(string? apiKey);
    bool IsAgentKeyValid(string? apiKey);
}

public sealed class ApiKeyValidator(IOptions<DashboardSecurityOptions> options) : IApiKeyValidator
{
    public bool IsDashboardKeyValid(string? apiKey)
        => IsValid(apiKey, options.Value.DashboardApiKey);

    public bool IsAgentKeyValid(string? apiKey)
        => IsValid(apiKey, options.Value.AgentApiKey);

    private static bool IsValid(string? provided, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}

public sealed class ApiKeyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IApiKeyValidator validator)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var apiKey = context.Request.Headers["X-HomeDashboard-Key"].FirstOrDefault();
        var isAgentEndpoint = context.Request.Path.StartsWithSegments("/api/agent");
        var isValid = isAgentEndpoint ? validator.IsAgentKeyValid(apiKey) : validator.IsDashboardKeyValid(apiKey);

        if (!isValid)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "A valid HomeDashboard API key is required." });
            return;
        }

        await next(context);
    }
}
