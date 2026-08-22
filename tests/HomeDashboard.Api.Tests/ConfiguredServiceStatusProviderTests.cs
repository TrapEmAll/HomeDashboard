using System.Net;
using HomeDashboard.Api;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class ConfiguredServiceStatusProviderTests
{
    [Fact]
    public async Task GetServicesAsync_reads_plex_identity_metrics()
    {
        var provider = CreateProvider(
            new ServiceDefinition
            {
                Id = "plex",
                Name = "Plex",
                Kind = ServiceKind.Plex,
                Url = new Uri("http://server-pc:32400/web")
            },
            request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""<MediaContainer version="1.42.1" machineIdentifier="abcdef123456" />""")
            });

        var services = await provider.GetServicesAsync(CancellationToken.None);

        var service = Assert.Single(services);
        Assert.Equal(ServiceStatus.Online, service.Status);
        Assert.Contains(service.Metrics, metric => metric.Label == "Version" && metric.Value == "1.42.1");
        Assert.Contains(service.Metrics, metric => metric.Label == "Machine" && metric.Value == "abcdef12");
    }

    [Fact]
    public async Task GetServicesAsync_falls_back_to_health_check_when_arr_api_key_is_missing()
    {
        var provider = CreateProvider(
            new ServiceDefinition
            {
                Id = "radarr",
                Name = "Radarr",
                Kind = ServiceKind.Radarr,
                Url = new Uri("http://server-pc:7878"),
                HealthUrl = new Uri("http://server-pc:7878/ping")
            },
            request => new HttpResponseMessage(request.RequestUri!.AbsolutePath == "/ping" ? HttpStatusCode.OK : HttpStatusCode.NotFound));

        var services = await provider.GetServicesAsync(CancellationToken.None);

        var service = Assert.Single(services);
        Assert.Equal(ServiceStatus.Online, service.Status);
        Assert.Equal("Health check returned 200.", service.StatusMessage);
    }

    [Fact]
    public async Task GetServicesAsync_accepts_markdown_wrapped_urls()
    {
        var provider = CreateProvider(
            new ServiceDefinition
            {
                Id = "radarr",
                Name = "Radarr",
                Kind = ServiceKind.Radarr,
                Url = new Uri("[http://server-pc:7878](http://server-pc:7878)", UriKind.RelativeOrAbsolute),
                HealthUrl = new Uri("[http://server-pc:7878/ping](http://server-pc:7878/ping)", UriKind.RelativeOrAbsolute)
            },
            request => new HttpResponseMessage(request.RequestUri!.AbsolutePath == "/ping" ? HttpStatusCode.OK : HttpStatusCode.NotFound));

        var services = await provider.GetServicesAsync(CancellationToken.None);

        var service = Assert.Single(services);
        Assert.Equal(ServiceStatus.Online, service.Status);
        Assert.Equal(new Uri("http://server-pc:7878"), service.Url);
    }

    [Fact]
    public async Task GetServicesAsync_checks_localhost_services_through_ipv4_loopback()
    {
        Uri? checkedUri = null;
        var provider = CreateProvider(
            new ServiceDefinition
            {
                Id = "radarr",
                Name = "Radarr",
                Kind = ServiceKind.Radarr,
                HealthUrl = new Uri("http://localhost:7878/ping")
            },
            request =>
            {
                checkedUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var services = await provider.GetServicesAsync(CancellationToken.None);

        var service = Assert.Single(services);
        Assert.Equal(ServiceStatus.Online, service.Status);
        Assert.Equal("127.0.0.1", checkedUri?.Host);
    }

    private static ConfiguredServiceStatusProvider CreateProvider(
        ServiceDefinition service,
        Func<HttpRequestMessage, HttpResponseMessage> handler)
        => new(
            Options.Create(new DashboardOptions { Services = [service] }),
            new StubHttpClientFactory(handler));

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHandler(handler));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
