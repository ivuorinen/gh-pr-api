using System.Collections.Concurrent;
using GhPrApi.Caching;
using GhPrApi.GitHub;
using GhPrApi.Models;
using GhPrApi.Options;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace GhPrApi.Services;

public sealed class PullRequestReportService
{
    private const int MaxConcurrentStatusRequests = 8;

    // The listing is capped and reports it via `truncated`; the work derived from it was not.
    // MaxConcurrentStatusRequests throttles concurrency, not total, so at the default
    // RepositoryLimit x PullRequestLimitPerRepository of 1000 x 100 a single inbound request
    // could fan out to 100,000 upstream calls -- more than the token's hourly GraphQL budget,
    // and hours of wall clock with no request timeout to cut it short.
    // ponytail: a flat cap, well above any realistic open-PR count. Promote it to a
    // GitHub:StatusRequestBudget option only if a deployment actually needs a different one.
    private const int MaxStatusRequestsPerReport = 500;

    private readonly IGitHubGraphQlClient _gitHub;
    private readonly PullRequestReportBuilder _builder;
    private readonly HybridCache _cache;
    private readonly CiNormalizer _ciNormalizer;
    private readonly IOptionsMonitor<GitHubOptions> _options;
    private readonly ILogger<PullRequestReportService> _logger;

    public PullRequestReportService(
        IGitHubGraphQlClient gitHub,
        PullRequestReportBuilder builder,
        HybridCache cache,
        CiNormalizer ciNormalizer,
        IOptionsMonitor<GitHubOptions> options,
        ILogger<PullRequestReportService> logger)
    {
        _gitHub = gitHub;
        _builder = builder;
        _cache = cache;
        _ciNormalizer = ciNormalizer;
        _options = options;
        _logger = logger;
    }

    public async Task<PullRequestReport> GetOpenPullRequestsAsync(
        string? ownerOverride,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var owner = ResolveOwner(ownerOverride, options.Owner);
        var flags = refresh
            ? HybridCacheEntryFlags.DisableLocalCacheRead | HybridCacheEntryFlags.DisableDistributedCacheRead
            : HybridCacheEntryFlags.None;

        // CacheTtlSeconds is validated as >= 0, and 0 has always meant "do not cache the
        // listing". HybridCache rejects a non-positive Expiration outright
        // (ArgumentOutOfRangeException: "The relative expiration value must be positive"), so
        // that case has to bypass the cache rather than be handed to it.
        var listing = options.CacheTtlSeconds <= 0
            ? await _gitHub.GetOpenPullRequestsAsync(owner, cancellationToken).ConfigureAwait(false)
            : await _cache.GetOrCreateAsync(
                $"listing:v1:{owner}",
                (Client: _gitHub, Owner: owner),
                static (state, token) => new ValueTask<GitHubOpenPullRequestsResult>(
                    state.Client.GetOpenPullRequestsAsync(state.Owner, token)),
                new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromSeconds(options.CacheTtlSeconds),
                    Flags = flags,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

        var enriched = new GitHubPullRequest[listing.PullRequests.Count];
        var unresolved = new ConcurrentBag<string>();
        var statusBudget = Math.Min(listing.PullRequests.Count, MaxStatusRequestsPerReport);

        // Past the budget the pull request keeps its listing data and reports ci: unknown. That
        // is `truncated`, not `degraded`: nothing failed, the work was simply not attempted, and
        // the two flags mean different things to a caller.
        for (var index = statusBudget; index < listing.PullRequests.Count; index++)
        {
            enriched[index] = listing.PullRequests[index] with { StatusUnresolved = true };
        }

        await Parallel.ForEachAsync(
            Enumerable.Range(0, statusBudget),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentStatusRequests,
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                var pullRequest = listing.PullRequests[index];
                try
                {
                    var details = await GetStatusDetailsAsync(pullRequest, flags, token).ConfigureAwait(false);
                    enriched[index] = pullRequest with { StatusDetails = details };
                }
                catch (GitHubQueryException ex)
                {
                    // One pull request's status failing must not discard the whole fetch.
                    // Nothing is cached for this key, so a retry re-fetches only this one.
                    var id = $"{pullRequest.RepositoryNameWithOwner}#{pullRequest.Number}";
                    _logger.LogWarning(ex, "Status details unresolved for {PullRequest}.", id);
                    enriched[index] = pullRequest with { StatusUnresolved = true };
                    unresolved.Add(id);
                }
            }).ConfigureAwait(false);

        var unresolvedIds = unresolved.IsEmpty
            ? null
            : unresolved.Order(StringComparer.Ordinal).ToArray();

        // The assembled report is deliberately not cached: it is derived from the parts above,
        // and caching it would restore the all-or-nothing unit this split exists to remove.
        var truncated = listing.Truncated || statusBudget < listing.PullRequests.Count;

        return _builder.Build(owner, enriched, truncated, unresolvedIds);
    }

    private static string ResolveOwner(string? ownerOverride, string configuredOwner)
    {
        if (string.IsNullOrWhiteSpace(ownerOverride))
        {
            return configuredOwner;
        }

        var owner = ownerOverride.Trim();

        // The endpoint accepts ?owner= case-insensitively, and the owner is part of the cache
        // key. Folding to the configured casing keeps ?owner=IVUORINEN and ?owner=ivuorinen on
        // one entry; passing the raw value through would mint two and double the GitHub quota
        // usage this split exists to reduce.
        return owner.Equals(configuredOwner, StringComparison.OrdinalIgnoreCase)
            ? configuredOwner
            : owner;
    }

    private async Task<GitHubPullRequestStatusDetails> GetStatusDetailsAsync(
        GitHubPullRequest pullRequest,
        HybridCacheEntryFlags flags,
        CancellationToken cancellationToken)
    {
        var pendingTtl = TimeSpan.FromSeconds(_options.CurrentValue.StatusCacheTtlSeconds);
        var key = $"status:v1:{pullRequest.RepositoryNameWithOwner}#{pullRequest.Number}@{pullRequest.HeadRefOid}";
        var fetched = false;

        var details = await _cache.GetOrCreateAsync(
            key,
            (Client: _gitHub, PullRequest: pullRequest),
            async (state, token) =>
            {
                fetched = true;
                return await state.Client
                    .GetPullRequestStatusDetailsAsync(state.PullRequest, token)
                    .ConfigureAwait(false);
            },
            new HybridCacheEntryOptions { Expiration = pendingTtl, Flags = flags },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // HybridCache fixes entry options before the factory runs, so the TTL cannot depend on
        // what was fetched. Write with the short TTL, then extend once if the checks turned out
        // to be settled: two writes on a miss, none on a hit.
        if (fetched)
        {
            var ttl = StatusCacheTtl.For(_ciNormalizer.Normalize(details), pendingTtl);
            if (ttl != pendingTtl)
            {
                await _cache.SetAsync(
                    key,
                    details,
                    new HybridCacheEntryOptions { Expiration = ttl },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        return details;
    }
}
