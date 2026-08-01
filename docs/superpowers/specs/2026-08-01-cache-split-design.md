# Split the GitHub fetch into independently cached units

Date: 2026-08-01
Status: approved, not yet implemented

## Problem

One report for `owner=ivuorinen` costs about 60 GitHub GraphQL requests:

| Work | Requests | Why |
| --- | ---: | --- |
| Paginate repositories with their open PRs nested | 9 | 86 public non-archived repos at page size 10 |
| Fetch status/check details, one call per PR | 51 | 51 open PRs, 8 concurrent |
| **Total** | **60** | |

(Counts measured against the live account on 2026-08-01, not estimated.)

All 60 are cached as a single `PullRequestReport` under
`github-open-pull-requests:{owner}` with a 300s TTL, so:

- **Any single failure discards all 60.** `GitHubQueryException` propagates out of
  `PullRequestReportService.FetchAndCacheAsync`, nothing is written to the cache, and
  the next request starts from zero. A blip on request 58 is as expensive as a blip on
  request 1.
- **A process restart discards all 60.** The cache is `IMemoryCache`, so every redeploy
  is a cold start.
- **Expiry is all-or-nothing.** The repository listing and a settled CI verdict expire on
  the same 300s clock as a CI run that is still in progress, even though they change on
  wildly different timescales.

## Goals

1. A failure part-way through a fetch keeps the work that already succeeded, so a retry
   costs only what is missing.
2. Cached work survives a process restart or redeploy.
3. A refresh re-queries only the parts that can actually have changed.
4. A partial upstream failure still returns a useful report rather than an error.

## Non-goals

- Multi-replica deployment. The design assumes one container (see Ceilings).
- Changing the GraphQL queries beyond adding one field (`headRefOid`).
- Caching anything derived. The assembled report is not cached; see Data flow.

## Decisions

| Decision | Choice |
| --- | --- |
| Failure scope to survive | Both in-process failures and process restarts |
| Cache tiering | L1 in-memory + L2 durable, via `HybridCache` |
| Durable store | SQLite file on a mounted volume |
| Partial upstream failure | Serve `200` with a `degraded` flag, not `503` |

`HybridCache` (`Microsoft.Extensions.Caching.Hybrid` 10.8.0, stable, targets `net10.0`)
was chosen over hand-rolling the two tiers because it provides **per-key stampede
protection**, which lets `PullRequestReportCoalescer` be deleted. Adding durability
therefore removes hand-rolled concurrency code instead of adding more.

## Cache units

Two units. The repository listing cannot be split further without making things worse:
the current query fetches repositories *and their open PRs nested in one paginated call*.
Separating them would mean 1 repo-list call plus 86 per-repo PR calls, replacing 9
requests with 87.

| Unit | Key | Rebuild cost | TTL |
| --- | --- | ---: | --- |
| Listing (repos + their open PRs) | `listing:v1:{owner}` | 9 requests | `GitHub:CacheTtlSeconds` (300s) |
| Per-PR status and checks | `status:v1:{owner}/{repo}#{number}@{headSha}` | 1 request | adaptive, below |

The 51 status calls are 85% of the work and are mutually independent. That is the split
that matters.

### Why the key carries `headSha`

CI runs against a specific commit. Including the head commit SHA in the key means a new
push mints a new key, so invalidation is a consequence of the data changing rather than a
TTL guess.

This requires adding `headRefOid` to `OpenPullRequestsQuery`. One scalar field per PR is
negligible against the 500,000-node ceiling the query already guards, and against the
execution-time ceiling recorded in `GitHubGraphQlClient`.

### Adaptive TTL

A naive "all checks terminal, so cache for 6h" rule is wrong: **re-running a failed CI job
keeps the same commit SHA.** The key would not change and the entry would not expire, so a
re-run red PR would keep reporting the old failure for up to six hours.

The rule follows from why people re-run: nobody re-runs green.

| Checks at `headSha` | TTL | Reasoning |
| --- | ---: | --- |
| any pending / queued / in progress / waiting / expected | 30s | actively changing |
| any failing / errored / cancelled / timed out | 60s | the case a human re-runs |
| all successful / neutral / skipped | 6h | settled, and nobody re-runs green |

Green PRs are the bulk of the 51, so this keeps nearly all of the saving while removing
the stale-red trap. 30s comes from `GitHub:StatusCacheTtlSeconds`; failing uses twice that
value; 6h is a constant, not a fourth configuration knob.

### Expected request counts

| Scenario | Today | After |
| --- | ---: | ---: |
| Cold, empty cache | 60 | 60 |
| Warm, inside every TTL | 0 | 0 |
| Listing expired, CI mostly settled | 60 | 9 + ~3 |
| Restart, cache on disk, 10 minutes later | 60 | 9 + ~3 |
| Failure mid-fetch, then retry | 60 | only the missing parts |

## Architecture

```
GET /api/github/open-pull-requests
        |
PullRequestReportService
        |
        +-- HybridCache.GetOrCreateAsync("listing:v1:{owner}")
        |        miss -> IGitHubGraphQlClient.GetOpenPullRequestsAsync  (9 requests)
        |
        +-- Parallel.ForEachAsync(prs, MaxDoP = 8)
        |        HybridCache.GetOrCreateAsync("status:v1:...@{sha}")
        |        miss -> IGitHubGraphQlClient.GetPullRequestStatusDetailsAsync  (1 request)
        |
        +-- PullRequestReportBuilder.Build(...)   assembled fresh every request

HybridCache
   L1  in-memory, per-key stampede protection
   L2  SqliteDistributedCache -> /data/cache.db
```

### Files

| File | Change |
| --- | --- |
| `src/GhPrApi/Caching/SqliteDistributedCache.cs` | new, ~110 LOC, `IDistributedCache` over one file |
| `src/GhPrApi/Caching/StatusCacheTtl.cs` | new, ~20 LOC, the three-tier TTL decision |
| `src/GhPrApi/Services/PullRequestReportService.cs` | rewritten around `HybridCache` |
| `src/GhPrApi/Services/PullRequestReportCoalescer.cs` | deleted, superseded by `HybridCache` |
| `src/GhPrApi/GitHub/GitHubGraphQlClient.cs` | add `headRefOid` to the listing query |
| `src/GhPrApi/GitHub/GitHubModels.cs` | add `HeadRefOid`, `StatusUnresolved` |
| `src/GhPrApi/Models/PullRequestReport.cs` | add `Degraded`, `Unresolved` |
| `src/GhPrApi/Models/NormalizedValues.cs` | add `Ci.Unknown` |
| `src/GhPrApi/Services/PullRequestReportBuilder.cs` | map unresolved status to `Ci.Unknown` |
| `src/GhPrApi/Services/HtmlReportFormatter.cs` | degraded note beside the truncated note |
| `src/GhPrApi/Program.cs` | register `IDistributedCache`, `HybridCache`, new options |
| `Dockerfile` | create `/data` owned by `app` before `USER app` |
| `compose.yml` | named volume for `/data` |

Net: roughly +250 LOC, -28 LOC.

## Data flow

1. Resolve `owner` (already constrained to the configured owner).
2. `listing:v1:{owner}` through `HybridCache`. On miss, the existing paginated fetch runs.
3. For each PR, `status:v1:...@{headSha}` through `HybridCache`, at `MaxDoP` 8. Cache hits
   return from L1 immediately, so the parallel loop mostly does nothing on a warm path.
4. Build the report from the parts.

**The assembled report is deliberately not cached.** It is derived, and rebuilding it is
pure in-memory work over 51 items. Caching it would reintroduce exactly the
all-or-nothing unit this design removes.

Consequence: `generatedAt` becomes the time the response was assembled rather than a value
frozen for up to 300s. This is a visible change and is more accurate, but it is a change.

`refresh=true` bypasses both tiers for reads and still writes results, via
`HybridCacheEntryFlags.DisableLocalCacheRead | DisableDistributedCacheRead`.

Cancellation: `FetchAndCacheAsync` currently passes `CancellationToken.None` because the
hand-rolled coalescer could not let one caller's disconnect kill a fetch shared with other
callers. `HybridCache` owns that concern, so the real request token can flow through.
**This must be verified against `HybridCache`'s shared-operation semantics during
implementation, not assumed.** If it does hold, it also closes the uncancellable-fan-out
finding raised in the audit.

## Error handling

Three distinct layers, deliberately different:

| Failure | Behaviour |
| --- | --- |
| Listing fetch fails | `503 Unable to query GitHub.` — nothing can be built. Unchanged. |
| Some per-PR status fetches fail | `200` degraded: those PRs get `ci: "unknown"`, report carries `degraded: true` and `unresolved: [...]`. |
| SQLite unavailable or unwritable | Log a warning once, run L1-only. |

A factory that throws writes no cache entry, so the successful parts stay cached in both
tiers. That is the whole resume mechanism: the retry only calls GitHub for what is missing.

The SQLite behaviour is **deliberate fail-open**. The cache is an optimisation, never a
source of truth, so an unmounted or unwritable volume must degrade performance rather than
take the service down. It is logged so the degradation is visible rather than silent.

## Contract changes

```
PullRequestReport   + Degraded:   bool = false
                    + Unresolved: IReadOnlyList<string>?   omitted when null
NormalizedValues.Ci + Unknown = "unknown"
GitHubPullRequest   + HeadRefOid: string
                    + StatusUnresolved: bool = false
```

Example degraded response:

```json
{
  "owner": "ivuorinen",
  "generatedAt": "2026-08-01T04:12:00+00:00",
  "totalCount": 51,
  "degraded": true,
  "unresolved": ["ivuorinen/foo#12", "ivuorinen/bar#3"],
  "groups": [ "..." ]
}
```

`Ci.Unknown` sorts with the non-urgent group: `PullRequestReportBuilder.GetSortRank`
returns 0 for failing and 1 for stale, and `unknown` takes the default 2 alongside passing
and pending. An unresolved status is an absence of information, not a signal, so it must
not be promoted to the top of the report. It contributes no prefix.

`Degraded` and the existing `Truncated` are independent and may both be true. `Truncated`
means a configured limit cut the data short; `Degraded` means some status lookups failed.
Neither implies the other.

HTML gains a degraded note alongside the existing truncated note. Markdown is unchanged,
consistent with it having no truncated note today.

`GitHub:CacheTtlSeconds` keeps its name and its 300s default but **changes meaning**: it is
now the listing TTL, not "the TTL for generated reports". README must be updated rather
than left to drift.

## Configuration

| Key | Default | Meaning |
| --- | --- | --- |
| `GitHub:CachePath` | `cache.db` | SQLite file. `compose.yml` sets `/data/cache.db`. |
| `GitHub:StatusCacheTtlSeconds` | `30` | Pending-check TTL. Failing checks use twice this. |
| `GitHub:CacheTtlSeconds` | `300` | Listing TTL (repurposed). |

Validation on start, matching the existing `.Validate(...)` clauses in `Program.cs`:
`StatusCacheTtlSeconds` in `[5, 3600]`, `CachePath` non-empty.

New packages, both first-party: `Microsoft.Extensions.Caching.Hybrid` 10.8.0,
`Microsoft.Data.Sqlite` 10.0.10. `src/GhPrApi/packages.lock.json` regenerates;
locked-mode restore in CI and the Dockerfile is unaffected.

## Deployment

The container runs as `USER app`, uid 1654 (verified by running the published image). A
freshly created Docker volume mounted at `/data` is root-owned, so the app cannot write to
it. The Dockerfile must create and chown the directory **before** dropping to `app`:

```dockerfile
RUN mkdir -p /data && chown app:app /data
```

`compose.yml` gains:

```yaml
    volumes:
      - gh-pr-api-cache:/data
volumes:
  gh-pr-api-cache:
```

Coolify needs a matching persistent volume so `/data` survives redeploys. Without it the
service still runs, L1-only, with the warning logged.

## Testing

| # | Test | Proves |
| --- | --- | --- |
| 1 | `SqliteDistributedCacheTests`: roundtrip, absolute expiry honoured, remove, missing key returns null, unwritable path throws | the L2 store, including that startup can detect an unusable path |
| 2 | `StatusCacheTtlTests`: table-driven over pending / failing / all-green | the adaptive TTL, in the style of `CiNormalizerTests` |
| 3 | `retry_after_partial_failure_only_refetches_the_missing_prs`: 51 PRs, 3 fail, assert the retry makes exactly 3 calls | **goal 1**, the core requirement |
| 4 | `partial_status_failure_returns_a_degraded_report`: `unresolved` list, `ci: "unknown"`, `degraded: true` | goal 4 |
| 5 | `settled_status_survives_a_listing_refresh` | the tiers are genuinely independent |
| 6 | `concurrent_misses_hit_github_once` rewritten against `HybridCache` | coalescing survives deleting the coalescer |
| 7 | `refresh_true_bypasses_both_tiers` | refresh semantics |
| 8 | `EndpointTests`: degraded `200` shape | the HTTP contract |

All 78 existing tests must still pass; several need updating for the new report fields.

Goal 3 (a refresh re-queries only what can have changed) is covered by tests 2, 5 and 7
together: the TTL tiers are correct, they expire independently, and `refresh=true` still
overrides them.

Goal 2 (surviving a restart) is covered indirectly: test 1 proves entries persist to and
load from the file, which is the mechanism. An end-to-end restart test would need a
container harness and is not proposed.

## Ceilings

To be recorded as `ponytail:` comments in code, not left implicit:

- **One replica.** SQLite on a shared volume means write contention if the service is ever
  scaled out. The upgrade path is swapping the registered `IDistributedCache` for Redis,
  which is a one-line DI change precisely because everything goes through that interface.
- **The cache is disposable.** No migration. Keys carry `v1:`, so a shape change bumps the
  prefix and stale entries simply expire.
- **6h settled TTL assumes green stays green.** A green check re-run on the same SHA is
  masked until expiry. `?refresh=true` is the escape hatch.

## Open question for implementation

Whether `HybridCache` lets a per-caller `CancellationToken` flow through without one
caller's cancellation aborting a fetch shared with others. If it does not, keep
`CancellationToken.None` for the shared factory and note it, rather than reintroducing the
bug the coalescer comment describes.
