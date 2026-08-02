using System.Globalization;
using GhPrApi.Caching;
using GhPrApi.GitHub;
using GhPrApi.Models;
using GhPrApi.Options;
using GhPrApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Distributed;
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

        // Observe the cancelled task so a faulted or cancelled result cannot surface later as
        // an unobserved exception and make the suite flaky. Its outcome is deliberately not
        // asserted; this test is about the survivor.
        try
        {
            await cancelled;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Cache_ttl_of_zero_disables_listing_caching_instead_of_throwing()
    {
        // CacheTtlSeconds is validated as >= 0 and 0 has always meant "do not cache". Handing
        // TimeSpan.Zero to HybridCache throws ArgumentOutOfRangeException, so 0 must bypass it.
        var gitHub = new FakeGitHubGraphQlClient();
        var service = CreateService(gitHub, cacheTtlSeconds: 0);

        await service.GetOpenPullRequestsAsync(null, refresh: false, TestContext.Current.CancellationToken);
        await service.GetOpenPullRequestsAsync(null, refresh: false, TestContext.Current.CancellationToken);

        Assert.Equal(2, gitHub.ListingCallCount);
    }

    [Fact]
    public async Task Owner_casing_does_not_split_the_cache_entry()
    {
        // ?owner= is accepted case-insensitively, so a raw pass-through would mint one cache
        // key per casing and double the GitHub quota usage.
        var gitHub = new FakeGitHubGraphQlClient();
        var service = CreateService(gitHub);

        await service.GetOpenPullRequestsAsync("ivuorinen", refresh: false, TestContext.Current.CancellationToken);
        await service.GetOpenPullRequestsAsync("IVUORINEN", refresh: false, TestContext.Current.CancellationToken);
        await service.GetOpenPullRequestsAsync("  IvUoRiNeN  ", refresh: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, gitHub.ListingCallCount);
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

        // DistinctBy because the "Easy wins" group is an overlay: a ready-to-merge PR is listed
        // both there and in its normal group, so flattening Groups yields it twice.
        var items = report.Groups
            .SelectMany(static g => g.PullRequests ?? [])
            .DistinctBy(static i => i.Number)
            .ToDictionary(static i => i.Number);
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
    public async Task Refresh_bypasses_the_per_pr_status_cache_too()
    {
        // ?refresh=true threads DisableLocalCacheRead|DisableDistributedCacheRead into the status
        // cache as well as the listing, so even a settled six-hour entry is re-fetched. That is
        // deliberate: refresh is the documented escape hatch for a CI re-run against an unchanged
        // head commit, which the SHA-keyed cache key cannot see. Two status calls, not one.
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

    [Fact]
    public async Task Status_fan_out_is_capped_and_reported_as_truncated()
    {
        // The listing is capped and says so; the work derived from it was not. At the default
        // 1000x100 limits one request could fan out to 100,000 upstream calls. Past the budget a
        // PR keeps its listing data and reads ci: unknown -- truncated, not degraded, because
        // nothing failed, the work was simply not attempted.
        const int budget = 500;
        var gitHub = new FakeGitHubGraphQlClient
        {
            ListingFactory = (_, _) => Task.FromResult(new GitHubOpenPullRequestsResult(
                [.. Enumerable.Range(1, budget + 5).Select(number => TestPullRequests.Create(number: number))],
                Truncated: false)),
        };
        var service = CreateService(gitHub);

        var report = await service.GetOpenPullRequestsAsync(null, refresh: false, TestContext.Current.CancellationToken);

        Assert.Equal(budget, gitHub.StatusCallCount);
        Assert.True(report.Truncated);
        Assert.False(report.Degraded);
        Assert.Null(report.Unresolved);
    }

    [Fact]
    public async Task A_cached_listing_survives_a_process_restart()
    {
        // L2 is the only reason the SQLite tier exists, and its serializer constrains the shape
        // of every cached model (see the comment in GitHubModels.cs). A second provider over the
        // same file is what a redeploy looks like: L1 empty, L2 warm. Without this the
        // deserialize direction was never executed by any test.
        var path = Path.Combine(Path.GetTempPath(), $"ghpr-l2-{Guid.NewGuid():N}.db");
        var gitHub = new FakeGitHubGraphQlClient
        {
            ListingFactory = (_, _) => Task.FromResult(new GitHubOpenPullRequestsResult(
                [TestPullRequests.Create(number: 1, labels: ["dependencies"])], Truncated: false)),
        };

        try
        {
            await CreateService(gitHub, cachePath: path)
                .GetOpenPullRequestsAsync(null, refresh: false, TestContext.Current.CancellationToken);

            // HybridCache writes L2 on a background task, so the restart has to be sequenced
            // after the listing entry actually lands, not merely after the call returns.
            await WaitForDurableWriteAsync(path, "listing:v1:ivuorinen", TestContext.Current.CancellationToken);

            var afterRestart = await CreateService(gitHub, cachePath: path)
                .GetOpenPullRequestsAsync(null, refresh: false, TestContext.Current.CancellationToken);

            Assert.Equal(1, gitHub.ListingCallCount);
            Assert.Equal(1, afterRestart.TotalCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }

    /// <summary>Blocks until the durable cache holds a row for the given key fragment.</summary>
    /// <remarks>
    /// Waiting for *any* row is not enough: one report writes a listing entry and a status entry,
    /// and whichever lands first would satisfy a bare count while the one under test is still in
    /// flight. On timeout the keys that did arrive are reported, so a change to how HybridCache
    /// names L2 entries fails with the reason rather than as a mystery.
    /// </remarks>
    private static async Task WaitForDurableWriteAsync(string path, string keyFragment, CancellationToken cancellationToken)
    {
        var seen = new List<string>();

        for (var attempt = 0; attempt < 200; attempt++)
        {
            seen.Clear();

            await using (var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly"))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT key FROM cache;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    seen.Add(reader.GetString(0));
                }
            }

            if (seen.Any(key => key.Contains(keyFragment, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        Assert.Fail($"No durable cache key containing '{keyFragment}' appeared. Keys seen: [{string.Join(", ", seen)}]");
    }

    private static PullRequestReportService CreateService(
        FakeGitHubGraphQlClient gitHub,
        int cacheTtlSeconds = 300,
        string? cachePath = null)
    {
        var services = new ServiceCollection();

        if (cachePath is not null)
        {
            services.AddSingleton<IDistributedCache>(
                new SqliteDistributedCache(cachePath, TimeProvider.System));
        }

        services.AddHybridCache();
        var provider = services.BuildServiceProvider();

        var options = new FakeOptionsMonitor<GitHubOptions>(new GitHubOptions
        {
            Owner = "ivuorinen",
            CacheTtlSeconds = cacheTtlSeconds,
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
