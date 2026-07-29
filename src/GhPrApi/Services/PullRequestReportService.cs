using GhPrApi.GitHub;
using GhPrApi.Models;
using GhPrApi.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GhPrApi.Services;

public sealed class PullRequestReportService : IPullRequestReportService
{
    private const int MaxConcurrentStatusRequests = 8;

    private readonly IGitHubGraphQlClient _gitHub;
    private readonly PullRequestReportBuilder _builder;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<GitHubOptions> _options;
    private readonly PullRequestReportCoalescer _coalescer;

    public PullRequestReportService(
        IGitHubGraphQlClient gitHub,
        PullRequestReportBuilder builder,
        IMemoryCache cache,
        IOptionsMonitor<GitHubOptions> options,
        PullRequestReportCoalescer coalescer)
    {
        _gitHub = gitHub;
        _builder = builder;
        _cache = cache;
        _options = options;
        _coalescer = coalescer;
    }

    public Task<PullRequestReport> GetOpenPullRequestsAsync(
        string? ownerOverride,
        bool refresh,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = _options.CurrentValue;
        var owner = string.IsNullOrWhiteSpace(ownerOverride)
            ? options.Owner
            : ownerOverride.Trim();

        var cacheKey = $"github-open-pull-requests:{owner}";
        if (!refresh && _cache.TryGetValue(cacheKey, out PullRequestReport? cachedReport) && cachedReport is not null)
        {
            return Task.FromResult(cachedReport);
        }

        // Coalesced against a shared key, so this fetch must outlive any single caller's
        // cancellation -- otherwise one caller disconnecting would cancel the response for
        // every other request piggybacking on the same in-flight fetch.
        return _coalescer.GetOrAddAsync(cacheKey, () => FetchAndCacheAsync(owner, cacheKey));
    }

    private async Task<PullRequestReport> FetchAndCacheAsync(string owner, string cacheKey)
    {
        var result = await _gitHub.GetOpenPullRequestsAsync(owner, CancellationToken.None).ConfigureAwait(false);
        var enrichedPullRequests = new GitHubPullRequest[result.PullRequests.Count];

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentStatusRequests };
        await Parallel.ForEachAsync(
            Enumerable.Range(0, result.PullRequests.Count),
            parallelOptions,
            async (index, ct) =>
            {
                var pullRequest = result.PullRequests[index];
                var statusDetails = await _gitHub.GetPullRequestStatusDetailsAsync(pullRequest, ct).ConfigureAwait(false);
                enrichedPullRequests[index] = pullRequest with { StatusDetails = statusDetails };
            }).ConfigureAwait(false);

        var report = _builder.Build(owner, enrichedPullRequests, result.Truncated);

        var currentOptions = _options.CurrentValue;
        if (currentOptions.CacheTtlSeconds > 0)
        {
            _cache.Set(cacheKey, report, TimeSpan.FromSeconds(currentOptions.CacheTtlSeconds));
        }

        return report;
    }
}
