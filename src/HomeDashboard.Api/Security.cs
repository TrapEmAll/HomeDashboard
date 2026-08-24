using System.Security.Cryptography;
using System.Text;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Api;

public sealed class DashboardSecurityOptions
{
    public string? DashboardApiKey { get; init; }
    public string? AgentApiKey { get; init; }
    public string? DashboardPassword { get; init; }
    public string? DashboardPasswordHash { get; init; }
    public string SessionCookieName { get; init; } = "HomeDashboard.Session";
    public TimeSpan SessionDuration { get; init; } = TimeSpan.FromHours(12);
}

public interface IApiKeyValidator
{
    bool IsDashboardKeyValid(string? apiKey);
    bool IsAgentKeyValid(string? apiKey);
    bool IsDashboardPasswordValid(string? password);
}

public sealed class ApiKeyValidator(IOptions<DashboardSecurityOptions> options) : IApiKeyValidator
{
    public bool IsDashboardKeyValid(string? apiKey)
        => IsValid(apiKey, options.Value.DashboardApiKey);

    public bool IsAgentKeyValid(string? apiKey)
        => IsValid(apiKey, options.Value.AgentApiKey);

    public bool IsDashboardPasswordValid(string? password)
    {
        if (!string.IsNullOrWhiteSpace(options.Value.DashboardPasswordHash))
        {
            return IsValid(HashSecret(password), options.Value.DashboardPasswordHash);
        }

        return IsValid(password, options.Value.DashboardPassword);
    }

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

    public static string HashSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return "";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hash);
    }
}

public interface IBrowserSessionStore
{
    AuthSession Create(out string token, string? profileId = null, string? displayName = null, string? role = null);
    AuthSession Get(string? token);
    void Remove(string? token);
}

public sealed class InMemoryBrowserSessionStore(IOptions<DashboardSecurityOptions> options) : IBrowserSessionStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, AuthSession> sessions = new(StringComparer.Ordinal);

    public AuthSession Create(out string token, string? profileId = null, string? displayName = null, string? role = null)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        token = Convert.ToBase64String(tokenBytes);
        var expiresAt = DateTimeOffset.UtcNow.Add(options.Value.SessionDuration);

        lock (gate)
        {
            sessions[token] = new AuthSession(true, expiresAt, profileId, displayName, role);
        }

        return new AuthSession(true, expiresAt, profileId, displayName, role);
    }

    public AuthSession Get(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new AuthSession(false, null);
        }

        lock (gate)
        {
            if (!sessions.TryGetValue(token, out var session))
            {
                return new AuthSession(false, null);
            }

            if (session.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                sessions.Remove(token);
                return new AuthSession(false, null);
            }

            return session;
        }
    }

    public void Remove(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        lock (gate)
        {
            sessions.Remove(token);
        }
    }
}

public sealed class ApiKeyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IApiKeyValidator validator,
        IBrowserSessionStore sessions,
        IOptions<DashboardSecurityOptions> options)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var apiKey = context.Request.Headers["X-HomeDashboard-Key"].FirstOrDefault();
        var session = sessions.Get(context.Request.Cookies[options.Value.SessionCookieName]);
        var validApiKey = RequiresAgentKey(context) ? validator.IsAgentKeyValid(apiKey) : validator.IsDashboardKeyValid(apiKey);
        var isValid = validApiKey
            || (!RequiresAgentKey(context) && session.IsAuthenticated);

        if (!isValid)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "A valid HomeDashboard API key is required." });
            return;
        }

        if (!validApiKey && !IsRoleAuthorized(context, session.Role))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This household role cannot perform that action." });
            return;
        }

        await next(context);
    }

    private static bool IsRoleAuthorized(HttpContext context, string? role)
    {
        if (role is null || role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return true;
        if (role.Equals("Viewer", StringComparison.OrdinalIgnoreCase)) return HttpMethods.IsGet(context.Request.Method);
        if (!role.Equals("Member", StringComparison.OrdinalIgnoreCase)) return false;
        if (context.Request.Path.StartsWithSegments("/api/settings") || context.Request.Path.StartsWithSegments("/api/agent")) return false;
        if (context.Request.Path.StartsWithSegments("/api/backup") && !HttpMethods.IsGet(context.Request.Method)) return false;
        if (context.Request.Path.StartsWithSegments("/api/services") && !HttpMethods.IsGet(context.Request.Method)) return false;
        return !context.Request.Path.StartsWithSegments("/api/command-center/integrations");
    }

    private static bool RequiresAgentKey(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/agent"))
        {
            return false;
        }

        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api/agent/snapshot"))
        {
            return true;
        }

        return context.Request.Path.Value?.Contains("/commands", StringComparison.OrdinalIgnoreCase) == true;
    }
}
