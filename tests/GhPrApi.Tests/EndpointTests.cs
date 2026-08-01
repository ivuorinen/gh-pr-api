using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GhPrApi.GitHub;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GhPrApi.Tests;

// Covers what the unit tests cannot: status codes, content types, error bodies, the format
// aliases, owner validation and rate limiting -- i.e. everything README.md promises callers.
// Each test builds its own host so the process-global rate limiter starts with a full window.
public sealed class EndpointTests
{
    [Theory]
    [InlineData("/api/github/open-pull-requests", "application/json")]
    [InlineData("/api/github/open-pull-requests?format=json", "application/json")]
    [InlineData("/api/github/open-pull-requests?format=markdown", "text/markdown")]
    [InlineData("/api/github/open-pull-requests?format=md", "text/markdown")]
    [InlineData("/api/github/open-pull-requests?format=MARKDOWN", "text/markdown")]
    [InlineData("/api/github/open-pull-requests?format=html", "text/html")]
    [InlineData("/api/github/open-pull-requests.json", "application/json")]
    [InlineData("/api/github/open-pull-requests.html", "text/html")]
    public async Task Supported_formats_return_200_with_the_documented_content_type(string path, string expectedMediaType)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedMediaType, response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("text")]
    [InlineData("markdownn")]
    public async Task Unknown_format_returns_400(string format)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/github/open-pull-requests?format={format}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Format_suffix_routes_ignore_the_format_query_parameter()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var json = await client.GetAsync("/api/github/open-pull-requests.json?format=html", TestContext.Current.CancellationToken);
        var html = await client.GetAsync("/api/github/open-pull-requests.html?format=json", TestContext.Current.CancellationToken);

        Assert.Equal("application/json", json.Content.Headers.ContentType?.MediaType);
        Assert.Equal("text/html", html.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Owner_matching_the_configured_owner_is_accepted()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // README.md documents ?owner=ivuorinen; it must keep working, case-insensitively.
        var exact = await client.GetAsync("/api/github/open-pull-requests?owner=ivuorinen", TestContext.Current.CancellationToken);
        var mixedCase = await client.GetAsync("/api/github/open-pull-requests?owner=IVUORINEN", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mixedCase.StatusCode);
    }

    [Theory]
    [InlineData("/api/github/open-pull-requests?owner=microsoft")]
    [InlineData("/api/github/open-pull-requests.json?owner=microsoft")]
    [InlineData("/api/github/open-pull-requests.html?owner=microsoft")]
    public async Task Owner_other_than_the_configured_owner_is_rejected_without_calling_github(string path)
    {
        // The regression this guards: an unauthenticated caller could point the operator's
        // GitHub token at an arbitrary account and, with ?refresh=true, drain its quota.
        var gitHub = new FakeGitHubGraphQlClient();
        using var factory = CreateFactory(gitHub);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, gitHub.CallCount);
    }

    [Fact]
    public async Task Json_reports_503_problem_details_when_github_is_unreachable()
    {
        using var factory = CreateFactory(new FakeGitHubGraphQlClient { Throws = true });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/github/open-pull-requests", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(TestContext.Current.CancellationToken);
        Assert.Equal("Unable to query GitHub.", problem?.Title);
    }

    [Fact]
    public async Task Markdown_reports_the_exact_documented_error_string_when_github_is_unreachable()
    {
        using var factory = CreateFactory(new FakeGitHubGraphQlClient { Throws = true });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/github/open-pull-requests?format=markdown", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unable to query GitHub.", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Html_reports_an_error_page_when_github_is_unreachable()
    {
        using var factory = CreateFactory(new FakeGitHubGraphQlClient { Throws = true });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/github/open-pull-requests.html", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Unable to query GitHub.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApi_document_generates_and_describes_every_route()
    {
        // Microsoft.AspNetCore.OpenApi declares only Microsoft.OpenApi >= 2.0.0, so the
        // explicit reference that dodges GHSA-v5pm-xwqc-g5wc can drag the runtime across a
        // major version. Document generation is where that surfaces, and it is the one
        // documented endpoint no other test touches.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = document.GetProperty("paths");
        foreach (var route in new[]
                 {
                     "/api/github/open-pull-requests",
                     "/api/github/open-pull-requests.json",
                     "/api/github/open-pull-requests.html",
                     "/health/live",
                     "/health/ready",
                 })
        {
            Assert.True(paths.TryGetProperty(route, out _), $"OpenAPI document is missing {route}.");
        }
    }

    [Fact]
    public async Task Degraded_report_is_200_with_the_unresolved_list()
    {
        using var factory = CreateFactory(new FakeGitHubGraphQlClient { FailStatusForNumber = 1 });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/github/open-pull-requests", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("degraded").GetBoolean());
        Assert.Equal("ivuorinen/example#1", body.GetProperty("unresolved")[0].GetString());
    }

    [Fact]
    public async Task App_starts_and_serves_when_the_cache_path_is_unusable()
    {
        // Fail open: the durable cache is an optimisation, never a source of truth. An
        // unmounted or unwritable volume must cost performance, not availability.
        using var factory = CreateFactory(cachePath: "/proc/definitely-not-writable/cache.db");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Liveness_is_ok_even_with_no_token_configured()
    {
        using var factory = CreateFactory(token: "");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_is_503_when_no_token_is_configured()
    {
        using var factory = CreateFactory(token: "");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_stays_200_and_reports_reachability_when_github_is_down()
    {
        // The regression this guards: readiness used to latch 503 on a single upstream
        // failure, and only a served request could clear it -- so an orchestrator honouring
        // that 503 stopped sending the very requests needed to recover. Report, never gate.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<GitHubApiHealthState>().RecordFailure();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ready", body?.Status);
        Assert.False(body?.GitHubReachable);
    }

    [Fact]
    public async Task Eleventh_request_inside_one_window_is_rate_limited()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            var permitted = await client.GetAsync("/api/github/open-pull-requests", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, permitted.StatusCode);
        }

        var rejected = await client.GetAsync("/api/github/open-pull-requests", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Health_endpoints_are_not_rate_limited()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        for (var i = 0; i < 12; i++)
        {
            var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(
        FakeGitHubGraphQlClient? gitHub = null,
        string token = "test-token",
        string? cachePath = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["GitHub:Owner"] = "ivuorinen",
                    ["GitHub:Token"] = token,
                    ["GitHub:CachePath"] = cachePath
                        ?? Path.Combine(Path.GetTempPath(), $"ghpr-test-{Guid.NewGuid():N}.db"),
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGitHubGraphQlClient>();
                services.AddSingleton<IGitHubGraphQlClient>(gitHub ?? new FakeGitHubGraphQlClient());
            });
        });

    private sealed record ProblemBody(string? Title);

    private sealed record HealthBody(string? Status, bool? GitHubReachable);

    private sealed class FakeGitHubGraphQlClient : IGitHubGraphQlClient
    {
        private int _callCount;

        public bool Throws { get; init; }

        public int? FailStatusForNumber { get; init; }

        public int CallCount => _callCount;

        public Task<GitHubOpenPullRequestsResult> GetOpenPullRequestsAsync(string owner, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);

            return Throws
                ? Task.FromException<GitHubOpenPullRequestsResult>(new GitHubQueryException("GitHub GraphQL query failed with HTTP 502."))
                : Task.FromResult(new GitHubOpenPullRequestsResult([TestPullRequests.Create(number: 1)], false));
        }

        public Task<GitHubPullRequestStatusDetails> GetPullRequestStatusDetailsAsync(GitHubPullRequest pullRequest, CancellationToken cancellationToken)
        {
            if (FailStatusForNumber == pullRequest.Number)
            {
                return Task.FromException<GitHubPullRequestStatusDetails>(
                    new GitHubQueryException("GitHub GraphQL query failed with HTTP 502."));
            }

            return Task.FromResult(new GitHubPullRequestStatusDetails(
                [],
                [],
                RequiresStatusChecks: false));
        }
    }
}
