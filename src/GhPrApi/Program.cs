using System.Text.Json.Serialization;
using GhPrApi.GitHub;
using GhPrApi.Models;
using GhPrApi.Options;
using GhPrApi.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services
    .AddOptions<GitHubOptions>()
    .Bind(builder.Configuration.GetSection(GitHubOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Owner), "GitHub:Owner is required.")
    .Validate(options => options.CacheTtlSeconds is >= 0 and <= 86_400, "GitHub:CacheTtlSeconds must be between 0 and 86400.")
    .Validate(options => options.RepositoryLimit is >= 1 and <= 1_000, "GitHub:RepositoryLimit must be between 1 and 1000.")
    .Validate(options => options.PullRequestLimitPerRepository is >= 1 and <= 100, "GitHub:PullRequestLimitPerRepository must be between 1 and 100.")
    .Validate(options => options.StatusCheckLimitPerPullRequest is >= 1 and <= 100, "GitHub:StatusCheckLimitPerPullRequest must be between 1 and 100.")
    .ValidateOnStart();

builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ReviewNormalizer>();
builder.Services.AddSingleton<CiNormalizer>();
builder.Services.AddSingleton<BranchNormalizer>();
builder.Services.AddSingleton<RobotDetector>();
builder.Services.AddSingleton<DependencyNameDetector>();
builder.Services.AddSingleton<SecurityDetector>();
builder.Services.AddSingleton<PullRequestReportBuilder>();
builder.Services.AddSingleton<MarkdownReportFormatter>();
builder.Services.AddSingleton<HtmlReportFormatter>();
builder.Services.AddSingleton<PullRequestReportCoalescer>();
builder.Services.AddSingleton<GitHubApiHealthState>();
builder.Services.AddScoped<IPullRequestReportService, PullRequestReportService>();

builder.Services.AddHttpClient<IGitHubGraphQlClient, GitHubGraphQlClient>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/graphql");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("gh-pr-api/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
})
.AddStandardResilienceHandler(options =>
{
    // Default AttemptTimeout (10s) is too tight for GitHub GraphQL under load -- raised so a
    // slow-but-real response isn't cut off and retried needlessly. CircuitBreaker.SamplingDuration
    // must be >= 2x AttemptTimeout; TotalRequestTimeout comfortably covers the retry budget
    // (up to 4 attempts at 15s each) so a sustained outage still fails within a bounded time.
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(70);
});

builder.Services.AddOpenApi();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter(RateLimiterPolicies.GitHubApi, limiterOptions =>
    {
        limiterOptions.PermitLimit = 10;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseRateLimiter();

app.MapOpenApi("/openapi/v1.json");

app.MapGet("/health/live", () => Results.Ok(new HealthResponse("ok")))
    .WithName("Liveness")
    .WithSummary("Returns process liveness.");

app.MapGet("/health/ready", GetReadiness)
    .WithName("Readiness")
    .WithSummary("Returns readiness based on required configuration and last known GitHub reachability.");

app.MapGet("/api/github/open-pull-requests", GetOpenPullRequestsAsync)
    .WithName("GetOpenPullRequests")
    .WithSummary("Lists currently open pull requests across public, non-archived GitHub repositories owned by the configured account. Format via ?format=json|markdown|html, defaults to json.")
    .Produces<PullRequestReport>()
    .Produces<string>(StatusCodes.Status200OK, "text/markdown")
    .Produces<string>(StatusCodes.Status200OK, "text/html")
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .RequireRateLimiting(RateLimiterPolicies.GitHubApi);

app.MapGet("/api/github/open-pull-requests.json", GetOpenPullRequestsJsonAsync)
    .WithName("GetOpenPullRequestsJson")
    .WithSummary("Same as GET /api/github/open-pull-requests, always as JSON regardless of ?format=.")
    .Produces<PullRequestReport>()
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .RequireRateLimiting(RateLimiterPolicies.GitHubApi);

app.MapGet("/api/github/open-pull-requests.html", GetOpenPullRequestsHtmlAsync)
    .WithName("GetOpenPullRequestsHtml")
    .WithSummary("Same as GET /api/github/open-pull-requests, rendered as an HTML page, regardless of ?format=.")
    .Produces<string>(StatusCodes.Status200OK, "text/html")
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .RequireRateLimiting(RateLimiterPolicies.GitHubApi);

app.Run();

static IResult GetReadiness(IOptionsMonitor<GitHubOptions> options, GitHubApiHealthState healthState)
{
    if (string.IsNullOrWhiteSpace(options.CurrentValue.Token))
    {
        return Results.Problem(
            title: "GitHub token is not configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!healthState.IsHealthy)
    {
        return Results.Problem(
            title: "GitHub is not reachable.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new HealthResponse("ready"));
}

static async Task<IResult> GetOpenPullRequestsAsync(
    string? owner,
    bool? refresh,
    string? format,
    IPullRequestReportService reports,
    MarkdownReportFormatter markdown,
    HtmlReportFormatter html,
    CancellationToken cancellationToken)
{
    var responseFormat = ParseFormat(format);
    if (responseFormat is null)
    {
        return Results.Problem(
            title: "Unsupported format.",
            detail: "Use format=json, format=markdown, or format=html.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    return await RenderReportAsync(responseFormat.Value, owner, refresh == true, reports, markdown, html, cancellationToken).ConfigureAwait(false);
}

static Task<IResult> GetOpenPullRequestsJsonAsync(
    string? owner,
    bool? refresh,
    IPullRequestReportService reports,
    MarkdownReportFormatter markdown,
    HtmlReportFormatter html,
    CancellationToken cancellationToken) =>
    RenderReportAsync(ResponseFormat.Json, owner, refresh == true, reports, markdown, html, cancellationToken);

static Task<IResult> GetOpenPullRequestsHtmlAsync(
    string? owner,
    bool? refresh,
    IPullRequestReportService reports,
    MarkdownReportFormatter markdown,
    HtmlReportFormatter html,
    CancellationToken cancellationToken) =>
    RenderReportAsync(ResponseFormat.Html, owner, refresh == true, reports, markdown, html, cancellationToken);

static ResponseFormat? ParseFormat(string? format)
{
    if (string.IsNullOrWhiteSpace(format))
    {
        return ResponseFormat.Json;
    }

    return format.Trim().ToUpperInvariant() switch
    {
        "JSON" => ResponseFormat.Json,
        "MARKDOWN" or "MD" => ResponseFormat.Markdown,
        "HTML" => ResponseFormat.Html,
        _ => null,
    };
}

static async Task<IResult> RenderReportAsync(
    ResponseFormat format,
    string? owner,
    bool refresh,
    IPullRequestReportService reports,
    MarkdownReportFormatter markdown,
    HtmlReportFormatter html,
    CancellationToken cancellationToken)
{
    try
    {
        var report = await reports.GetOpenPullRequestsAsync(owner, refresh, cancellationToken).ConfigureAwait(false);

        return format switch
        {
            ResponseFormat.Markdown => Results.Text(markdown.Format(report), "text/markdown; charset=utf-8"),
            ResponseFormat.Html => Results.Text(html.Format(report), "text/html; charset=utf-8"),
            _ => Results.Ok(report),
        };
    }
    catch (GitHubQueryException)
    {
        return format switch
        {
            ResponseFormat.Markdown => Results.Text("Unable to query GitHub.", "text/plain; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable),
            ResponseFormat.Html => Results.Text(HtmlReportFormatter.FormatError("Unable to query GitHub."), "text/html; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Problem(title: "Unable to query GitHub.", statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }
}

internal enum ResponseFormat
{
    Json,
    Markdown,
    Html,
}

internal static class RateLimiterPolicies
{
    public const string GitHubApi = "github-api";
}

public sealed record HealthResponse(string Status);
