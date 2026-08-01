using GhPrApi.GitHub;
using GhPrApi.Models;
using GhPrApi.Options;
using GhPrApi.Services;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GhPrApi.Tests;

public sealed class PullRequestReportServiceTests
{
    [Fact]
    public async Task Second_call_inside_the_ttl_does_not_call_github_again()
    {
        var gitHub = new FakeGitHubGraphQlClient();
        var service = CreateService(gitHub);

        var first = await service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);
        var second = await service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);

        Assert.Equal(1, gitHub.ListingCallCount);
        Assert.Equal(first.TotalCount, second.TotalCount);
    }

    [Fact]
    public async Task Refresh_true_bypasses_both_tiers()
    {
        var gitHub = new FakeGitHubGraphQlClient();
        var service = CreateService(gitHub);

        await service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);
        await service.GetOpenPullRequestsAsync(null, refresh: true, CancellationToken.None);

        Assert.Equal(2, gitHub.ListingCallCount);
    }

    [Fact]
    public async Task Concurrent_misses_hit_github_once()
    {
        var gate = new TaskCompletionSource();
        var gitHub = new FakeGitHubGraphQlClient
        {
            ListingFactory = async (_, _) =>
            {
                await gate.Task;
                return new GitHubOpenPullRequestsResult([], false);
            },
        };
        var service = CreateService(gitHub);

        var first = service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);
        var second = service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, gitHub.ListingCallCount);
    }

    [Fact]
    public async Task One_caller_cancelling_does_not_break_the_other()
    {
        var gate = new TaskCompletionSource();
        var gitHub = new FakeGitHubGraphQlClient
        {
            ListingFactory = async (_, _) =>
            {
                await gate.Task;
                return new GitHubOpenPullRequestsResult([], false);
            },
        };
        var service = CreateService(gitHub);
        using var cts = new CancellationTokenSource();

        var cancelled = service.GetOpenPullRequestsAsync(null, refresh: false, cts.Token);
        var survivor = service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);
        await cts.CancelAsync();
        gate.SetResult();

        var report = await survivor;
        Assert.Equal(0, report.TotalCount);
        _ = cancelled;
    }

    [Fact]
    public async Task Partial_status_failure_returns_a_degraded_report()
    {
        var gitHub = new FakeGitHubGraphQlClient
        {
            ListingFactory = (_, _) => Task.FromResult(new GitHubOpenPullRequestsResult(
                [TestPullRequests.Create(number: 1), TestPullRequests.Create(number: 2)], false)),
            FailingPullRequestNumbers = [2],
        };
        var service = CreateService(gitHub);

        var report = await service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);

        var items = report.Groups.SelectMany(static g => g.PullRequests ?? []).ToDictionary(static i => i.Number);
        Assert.True(report.Degraded);
        Assert.Equal(["ivuorinen/example#2"], report.Unresolved);
        Assert.Equal(NormalizedValues.Ci.Unknown, items[2].Ci);
        Assert.NotEqual(NormalizedValues.Ci.Unknown, items[1].Ci);
    }

    [Fact]
    public async Task Retry_after_partial_failure_only_refetches_the_missing_prs()
    {
        // This is the whole point of the split: a blip must not discard the work that
        // already succeeded.
        var gitHub = new FakeGitHubGraphQlClient
        {
            ListingFactory = (_, _) => Task.FromResult(new GitHubOpenPullRequestsResult(
                [
                    TestPullRequests.Create(number: 1),
                    TestPullRequests.Create(number: 2),
                    TestPullRequests.Create(number: 3),
                ], false)),
            FailingPullRequestNumbers = [3],
        };
        var service = CreateService(gitHub);

        var degraded = await service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);
        Assert.True(degraded.Degraded);
        Assert.Equal(3, gitHub.StatusCallCount);

        gitHub.FailingPullRequestNumbers = [];
        var repaired = await service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);

        Assert.False(repaired.Degraded);
        // 3 from the first attempt plus exactly 1 retry, not 3 more.
        Assert.Equal(4, gitHub.StatusCallCount);
    }

    [Fact]
    public async Task A_settled_status_survives_a_listing_refresh()
    {
        var gitHub = new FakeGitHubGraphQlClient
        {
            ListingFactory = (_, _) => Task.FromResult(new GitHubOpenPullRequestsResult(
                [TestPullRequests.Create(number: 1)], false)),
        };
        var service = CreateService(gitHub);

        await service.GetOpenPullRequestsAsync(null, refresh: false, CancellationToken.None);
        await service.GetOpenPullRequestsAsync(null, refresh: true, CancellationToken.None);

        Assert.Equal(2, gitHub.ListingCallCount);
        Assert.Equal(2, gitHub.StatusCallCount);
    }

    private static PullRequestReportService CreateService(FakeGitHubGraphQlClient gitHub)
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var provider = services.BuildServiceProvider();

        var options = new FakeOptionsMonitor<GitHubOptions>(new GitHubOptions
        {
            Owner = "ivuorinen",
            CacheTtlSeconds = 300,
            StatusCacheTtlSeconds = 30,
        });

        return new PullRequestReportService(
            gitHub,
            TestSupport.CreateBuilder(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero)),
            provider.GetRequiredService<HybridCache>(),
            new CiNormalizer(),
            options,
            NullLogger<PullRequestReportService>.Instance);
    }

    private sealed class FakeGitHubGraphQlClient : IGitHubGraphQlClient
    {
        private int _listingCallCount;
        private int _statusCallCount;

        public Func<string, CancellationToken, Task<GitHubOpenPullRequestsResult>> ListingFactory { get; init; } =
            static (_, _) => Task.FromResult(new GitHubOpenPullRequestsResult([], false));

        public IReadOnlyCollection<int> FailingPullRequestNumbers { get; set; } = [];

        public int ListingCallCount => _listingCallCount;

        public int StatusCallCount => _statusCallCount;

        public Task<GitHubOpenPullRequestsResult> GetOpenPullRequestsAsync(string owner, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _listingCallCount);
            return ListingFactory(owner, cancellationToken);
        }

        public Task<GitHubPullRequestStatusDetails> GetPullRequestStatusDetailsAsync(
            GitHubPullRequest pullRequest,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _statusCallCount);

            if (FailingPullRequestNumbers.Contains(pullRequest.Number))
            {
                return Task.FromException<GitHubPullRequestStatusDetails>(
                    new GitHubQueryException("GitHub GraphQL query failed with HTTP 502."));
            }

            return Task.FromResult(new GitHubPullRequestStatusDetails([], [], RequiresStatusChecks: false));
        }
    }
}
