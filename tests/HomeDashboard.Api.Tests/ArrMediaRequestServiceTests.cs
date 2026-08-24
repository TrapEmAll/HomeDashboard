using System.Net;
using System.Text;
using System.Text.Json;
using HomeDashboard.Api;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class ArrMediaRequestServiceTests
{
    [Fact]
    public async Task SearchQueriesSonarrAndRadarrAndAddsSelectedMovie()
    {
        var handler = new ArrHandler();
        var service = new ArrMediaRequestService(Options.Create(new DashboardOptions
        {
            Services =
            [
                new ServiceDefinition { Id = "radarr", Name = "Radarr", Kind = ServiceKind.Radarr, Url = new Uri("http://localhost:7878"), ApiKey = "radarr-key" },
                new ServiceDefinition { Id = "sonarr", Name = "Sonarr", Kind = ServiceKind.Sonarr, Url = new Uri("http://localhost:8989"), ApiKey = "sonarr-key" }
            ]
        }), new HandlerFactory(handler), NullLogger<ArrMediaRequestService>.Instance);

        var matches = await service.SearchAsync("Dune", CancellationToken.None);
        var movie = Assert.Single(matches, item => item.MediaType == "Movie");
        var series = Assert.Single(matches, item => item.MediaType == "TV");

        Assert.Equal("Dune: Part Two", movie.Title);
        Assert.Equal("tt15239678", movie.ImdbId);
        Assert.Equal(693134, movie.TmdbId);
        Assert.Equal(366668, series.TvdbId);
        Assert.Contains(handler.Requests, item => item.PathAndQuery.StartsWith("/api/v3/movie/lookup?term=Dune", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, item => item.PathAndQuery.StartsWith("/api/v3/series/lookup?term=Dune", StringComparison.Ordinal));

        var result = await service.RequestAsync(movie.SelectionId, true, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Submitted", result.Status);
        Assert.NotNull(handler.RadarrPostBody);
        using var submitted = JsonDocument.Parse(handler.RadarrPostBody);
        Assert.Equal(693134, submitted.RootElement.GetProperty("tmdbId").GetInt32());
        Assert.Equal(6, submitted.RootElement.GetProperty("qualityProfileId").GetInt32());
        Assert.Equal("D:\\Movies", submitted.RootElement.GetProperty("rootFolderPath").GetString());
        Assert.True(submitted.RootElement.GetProperty("addOptions").GetProperty("searchForMovie").GetBoolean());

        var seriesResult = await service.RequestAsync(series.SelectionId, false, CancellationToken.None);

        Assert.True(seriesResult.Succeeded, seriesResult.Message);
        using var submittedSeries = JsonDocument.Parse(handler.SonarrPostBody!);
        Assert.Equal(366668, submittedSeries.RootElement.GetProperty("tvdbId").GetInt32());
        Assert.True(submittedSeries.RootElement.GetProperty("seasonFolder").GetBoolean());
        Assert.False(submittedSeries.RootElement.GetProperty("addOptions").GetProperty("searchForMissingEpisodes").GetBoolean());
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class ArrHandler : HttpMessageHandler
    {
        public List<(string PathAndQuery, string? ApiKey)> Requests { get; } = [];
        public string? RadarrPostBody { get; private set; }
        public string? SonarrPostBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add((path, request.Headers.TryGetValues("X-Api-Key", out var values) ? values.Single() : null));
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/api/v3/movie")
            {
                RadarrPostBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json("{}", HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/api/v3/series")
            {
                SonarrPostBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json("{}", HttpStatusCode.Created);
            }

            return request.RequestUri.AbsolutePath switch
            {
                "/api/v3/movie/lookup" => Json("""
                    [{"title":"Dune: Part Two","year":2024,"tmdbId":693134,"imdbId":"tt15239678","overview":"Paul continues his journey.","images":[{"coverType":"poster","remoteUrl":"https://example.test/dune.jpg"}]}]
                    """),
                "/api/v3/series/lookup" => Json("""
                    [{"title":"Dune: Prophecy","year":2024,"tvdbId":366668,"imdbId":"tt10466872","overview":"A television series.","images":[]}]
                    """),
                "/api/v3/rootfolder" => Json("""[{"id":1,"path":"D:\\Movies","freeSpace":1000000}]"""),
                "/api/v3/qualityprofile" => Json("""[{"id":6,"name":"Any"}]"""),
                _ => Json("{}", HttpStatusCode.NotFound)
            };
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
