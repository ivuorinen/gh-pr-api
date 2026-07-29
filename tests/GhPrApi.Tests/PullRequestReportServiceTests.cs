using GhPrApi.GitHub;
using GhPrApi.Models;
using GhPrApi.Options;
using GhPrApi.Services;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace GhPrApi.Tests;

public sealed class PullRequestReportServiceTests
{
    [Fact]
    public async Task GetOpenPullRequestsAsync_returns_cached_report_without_calling_github()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cachedReport = new PullRequestReport("ivuorinen", DateTimeOffset.UtcNow, 0, [], "No open PRs.");
        cache.Set("github-open-pull-requests:ivuorinen", cachedReport, TimeSpan.FromMinutes(5));
        var gitHub = new FakeGitHubGraphQlClient();
        var service = CreateService(gitHub, cache);

        var report = await service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);

        Assert.Same(cachedReport, report);
        Assert.Equal(0, gitHub.OpenPullRequestsCallCount);
    }

    [Fact]
    public async Task GetOpenPullRequestsAsync_fetches_and_caches_on_a_miss()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gitHub = new FakeGitHubGraphQlClient();
        var service = CreateService(gitHub, cache);

        var first = await service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);
        var second = await service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);

        Assert.Equal(1, gitHub.OpenPullRequestsCallCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetOpenPullRequestsAsync_refresh_true_bypasses_the_cache()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gitHub = new FakeGitHubGraphQlClient();
        var service = CreateService(gitHub, cache);

        await service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);
        await service.GetOpenPullRequestsAsync(null, refresh: true, CancellationToken.None);

        Assert.Equal(2, gitHub.OpenPullRequestsCallCount);
    }

    [Fact]
    public async Task GetOpenPullRequestsAsync_coalesces_concurrent_cache_misses_into_one_github_fetch()
    {
        var gate = new TaskCompletionSource();
        var gitHub = new FakeGitHubGraphQlClient
        {
            OpenPullRequestsFactory = async (_, _) =>
            {
                await gate.Task;
                return new GitHubOpenPullRequestsResult([], false);
            },
        };
        var service = CreateService(gitHub);

        var first = service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);
        var second = service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, gitHub.OpenPullRequestsCallCount);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task GetOpenPullRequestsAsync_enriches_each_pull_request_with_its_own_status_details()
    {
        var pr1 = TestPullRequests.Create(number: 1);
        var pr2 = TestPullRequests.Create(number: 2);
        var gitHub = new FakeGitHubGraphQlClient
        {
            OpenPullRequestsFactory = (_, _) => Task.FromResult(new GitHubOpenPullRequestsResult([pr1, pr2], false)),
            StatusDetailsFactory = pr => new GitHubPullRequestStatusDetails(
                [],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                RequiresStatusChecks: pr.Number == 1),
        };
        var service = CreateService(gitHub);

        var report = await service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);
        var items = report.Groups.SelectMany(static g => g.PullRequests ?? []).ToDictionary(static item => item.Number);

        Assert.Equal(NormalizedValues.Ci.Pending, items[1].Ci);
        Assert.Equal(NormalizedValues.Ci.Passing, items[2].Ci);
    }

    private static PullRequestReportService CreateService(
        FakeGitHubGraphQlClient gitHub,
        IMemoryCache? cache = null,
        int cacheTtlSeconds = 300)
    {
        var builder = TestSupport.CreateBuilder(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));
        var options = new FakeOptionsMonitor<GitHubOptions>(new GitHubOptions
        {
            Owner = "ivuorinen",
            CacheTtlSeconds = cacheTtlSeconds,
        });

        return new PullRequestReportService(
            gitHub,
            builder,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            options,
            new PullRequestReportCoalescer());
    }

    private sealed class FakeGitHubGraphQlClient : IGitHubGraphQlClient
    {
        private int _openPullRequestsCallCount;

        public Func<string, CancellationToken, Task<GitHubOpenPullRequestsResult>> OpenPullRequestsFactory { get; init; } =
            static (_, _) => Task.FromResult(new GitHubOpenPullRequestsResult([], false));

        public Func<GitHubPullRequest, GitHubPullRequestStatusDetails> StatusDetailsFactory { get; init; } =
            static _ => new GitHubPullRequestStatusDetails([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);

        public int OpenPullRequestsCallCount => _openPullRequestsCallCount;

        public Task<GitHubOpenPullRequestsResult> GetOpenPullRequestsAsync(string owner, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _openPullRequestsCallCount);
            return OpenPullRequestsFactory(owner, cancellationToken);
        }

        public Task<GitHubPullRequestStatusDetails> GetPullRequestStatusDetailsAsync(GitHubPullRequest pullRequest, CancellationToken cancellationToken) =>
            Task.FromResult(StatusDetailsFactory(pullRequest));
    }
}
