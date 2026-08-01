# Cache Split Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the GitHub fetch into independently cached units so a partial failure resumes instead of restarting, and cached work survives a redeploy.

**Architecture:** Two cache units — a listing (repos + their open PRs, 9 requests) and per-PR status keyed by head commit SHA (1 request each). Both go through `HybridCache`: L1 in-memory with per-key stampede protection, L2 a SQLite file on a mounted volume. Per-PR status TTL adapts to whether checks are pending, failing, or settled. A failed status lookup degrades that PR to `ci: "unknown"` instead of failing the whole request.

**Tech Stack:** .NET 10, ASP.NET Core minimal API, `Microsoft.Extensions.Caching.Hybrid` 10.8.0, `Microsoft.Data.Sqlite` 10.0.10, xunit 2.9.3.

**Spec:** `docs/superpowers/specs/2026-08-01-cache-split-design.md`

## Global Constraints

- Target framework `net10.0`. Do not change it.
- `TreatWarningsAsErrors` is on and `Nullable` is enabled — the build fails on any warning.
- **XML comments in `.csproj` / `.props` files must never contain a literal `--`.** MSBuild rejects it with `MSB4025` and the project will not load.
- `src/GhPrApi` uses a committed `packages.lock.json`. After changing any `PackageReference` there, run `dotnet restore src/GhPrApi/GhPrApi.csproj --force-evaluate` and commit the regenerated lockfile, or CI's `--locked-mode` restore fails.
- The test project must NOT get a lockfile (`RestorePackagesWithLockFile` is set only in `src/GhPrApi/GhPrApi.csproj`).
- All 78 existing tests must still pass at every commit.
- Every action in `.github/workflows/` stays pinned to a commit SHA with the version in a trailing comment.
- Cache keys carry a `v1:` prefix. If an entry's shape changes, bump the prefix.

---

### Task 1: SQLite distributed cache (the L2 store)

**Files:**
- Create: `src/GhPrApi/Caching/SqliteDistributedCache.cs`
- Modify: `src/GhPrApi/GhPrApi.csproj`
- Test: `tests/GhPrApi.Tests/SqliteDistributedCacheTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `GhPrApi.Caching.SqliteDistributedCache`, a `public sealed class` implementing `Microsoft.Extensions.Caching.Distributed.IDistributedCache`, constructor `SqliteDistributedCache(string databasePath, TimeProvider timeProvider)`. Throws `SqliteException` from the constructor if the path is unwritable. Task 5 registers it.

- [ ] **Step 1: Add the SQLite package**

In `src/GhPrApi/GhPrApi.csproj`, inside the existing `<ItemGroup>` holding `PackageReference` items, add:

```xml
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.10" />
```

- [ ] **Step 2: Regenerate the lockfile**

Run: `dotnet restore src/GhPrApi/GhPrApi.csproj --force-evaluate`
Expected: `Restored /home/ivuorinen/Code/ivuorinen/gh-pr-api/src/GhPrApi/GhPrApi.csproj`

- [ ] **Step 3: Write the failing tests**

Create `tests/GhPrApi.Tests/SqliteDistributedCacheTests.cs`:

```csharp
using GhPrApi.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GhPrApi.Tests;

public sealed class SqliteDistributedCacheTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ghpr-{Guid.NewGuid():N}.db");

    [Fact]
    public void Set_then_Get_round_trips_the_value()
    {
        var cache = Create(out _);

        cache.Set("k", [1, 2, 3], Expires(TimeSpan.FromMinutes(5)));

        Assert.Equal([1, 2, 3], cache.Get("k"));
    }

    [Fact]
    public void Get_returns_null_for_a_missing_key()
    {
        var cache = Create(out _);

        Assert.Null(cache.Get("nope"));
    }

    [Fact]
    public void Get_returns_null_once_the_entry_has_expired()
    {
        var cache = Create(out var time);
        cache.Set("k", [1], Expires(TimeSpan.FromSeconds(30)));

        time.Advance(TimeSpan.FromSeconds(31));

        Assert.Null(cache.Get("k"));
    }

    [Fact]
    public void Set_overwrites_an_existing_key()
    {
        var cache = Create(out _);
        cache.Set("k", [1], Expires(TimeSpan.FromMinutes(5)));

        cache.Set("k", [9], Expires(TimeSpan.FromMinutes(5)));

        Assert.Equal([9], cache.Get("k"));
    }

    [Fact]
    public void Remove_deletes_the_entry()
    {
        var cache = Create(out _);
        cache.Set("k", [1], Expires(TimeSpan.FromMinutes(5)));

        cache.Remove("k");

        Assert.Null(cache.Get("k"));
    }

    [Fact]
    public void Entries_survive_a_new_instance_over_the_same_file()
    {
        var time = new FakeTimeProvider();
        var first = new SqliteDistributedCache(_path, time);
        first.Set("k", [7], Expires(TimeSpan.FromMinutes(5)));

        var second = new SqliteDistributedCache(_path, time);

        Assert.Equal([7], second.Get("k"));
    }

    [Fact]
    public void Set_without_an_absolute_expiry_throws()
    {
        var cache = Create(out _);

        Assert.Throws<ArgumentException>(() => cache.Set("k", [1], new DistributedCacheEntryOptions()));
    }

    [Fact]
    public void Constructor_throws_when_the_path_is_not_writable()
    {
        var unwritable = Path.Combine(Path.GetTempPath(), $"ghpr-{Guid.NewGuid():N}", "nested", "cache.db");

        Assert.ThrowsAny<Exception>(() => new SqliteDistributedCache(unwritable, new FakeTimeProvider()));
    }

    private SqliteDistributedCache Create(out FakeTimeProvider time)
    {
        time = new FakeTimeProvider();
        return new SqliteDistributedCache(_path, time);
    }

    private static DistributedCacheEntryOptions Expires(TimeSpan ttl) =>
        new() { AbsoluteExpirationRelativeToNow = ttl };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
```

`FakeTimeProvider` needs `Microsoft.Extensions.TimeProvider.Testing`. Add to `tests/GhPrApi.Tests/GhPrApi.Tests.csproj` inside the existing `<ItemGroup>` of package references:

```xml
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.8.0" />
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SqliteDistributedCacheTests`
Expected: FAIL to compile with `CS0246: The type or namespace name 'SqliteDistributedCache' could not be found`.

- [ ] **Step 5: Implement the cache**

Create `src/GhPrApi/Caching/SqliteDistributedCache.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Distributed;

namespace GhPrApi.Caching;

/// <summary>
/// Durable L2 for <c>HybridCache</c>: one file, one table, absolute expiry only.
/// </summary>
/// <remarks>
/// Sliding expiration is not supported; <see cref="Refresh(string)"/> is a no-op. HybridCache
/// only ever sets an absolute expiry, so nothing is lost.
/// The async members delegate to the synchronous ones on purpose: Microsoft.Data.Sqlite's async
/// API is synchronous underneath because SQLite has no async file I/O, so wrapping in a thread
/// would add a context switch and buy nothing against a local file.
/// ponytail: single-writer SQLite, sized for one replica. If this ever scales out, swap the
/// IDistributedCache registration in Program.cs for Redis; nothing else has to change.
/// </remarks>
public sealed class SqliteDistributedCache : IDistributedCache
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private long _lastCleanupTicks;

    public SqliteDistributedCache(string databasePath, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS cache (
                key        TEXT    PRIMARY KEY,
                value      BLOB    NOT NULL,
                expires_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_cache_expires_at ON cache (expires_at);
            """;
        command.ExecuteNonQuery();
    }

    public byte[]? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM cache WHERE key = $key AND expires_at > $now;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$now", Now());

        return command.ExecuteScalar() as byte[];
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        var expiresAt = ResolveExpiry(options);

        using var connection = OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO cache (key, value, expires_at)
                VALUES ($key, $value, $expires)
                ON CONFLICT(key) DO UPDATE SET
                    value = excluded.value,
                    expires_at = excluded.expires_at;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$expires", expiresAt);
            command.ExecuteNonQuery();
        }

        RemoveExpiredIfDue(connection);
    }

    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM cache WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    public void Refresh(string key)
    {
        // No sliding expiration, so there is nothing to extend.
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Get(key));
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Set(key, value, options);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Remove(key);
        return Task.CompletedTask;
    }

    public Task RefreshAsync(string key, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private long Now() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private long ResolveExpiry(DistributedCacheEntryOptions options)
    {
        if (options.AbsoluteExpirationRelativeToNow is { } relative)
        {
            return _timeProvider.GetUtcNow().Add(relative).ToUnixTimeMilliseconds();
        }

        if (options.AbsoluteExpiration is { } absolute)
        {
            return absolute.ToUnixTimeMilliseconds();
        }

        // An entry with no absolute expiry would never leave the file. Fail loudly rather than
        // cache something forever.
        throw new ArgumentException(
            "A cache entry requires an absolute expiry.",
            nameof(options));
    }

    private void RemoveExpiredIfDue(SqliteConnection connection)
    {
        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastCleanupTicks);
        if (nowTicks - last < CleanupInterval.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastCleanupTicks, nowTicks, last) != last)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM cache WHERE expires_at <= $now;";
        command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery();
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SqliteDistributedCacheTests`
Expected: `Passed! - Failed: 0, Passed: 8`

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 86` (78 existing + 8 new)

- [ ] **Step 8: Commit**

```bash
git add src/GhPrApi/Caching/SqliteDistributedCache.cs src/GhPrApi/GhPrApi.csproj src/GhPrApi/packages.lock.json tests/GhPrApi.Tests/GhPrApi.Tests.csproj tests/GhPrApi.Tests/SqliteDistributedCacheTests.cs
git commit -m "feat: add a SQLite-backed IDistributedCache for durable caching"
```

---

### Task 2: Adaptive status TTL policy

**Files:**
- Create: `src/GhPrApi/Caching/StatusCacheTtl.cs`
- Test: `tests/GhPrApi.Tests/StatusCacheTtlTests.cs`

**Interfaces:**
- Consumes: `GhPrApi.Models.NormalizedValues.Ci` (existing constants `Passing`, `Failing`, `Pending`).
- Produces: `GhPrApi.Caching.StatusCacheTtl.For(string normalizedCi, TimeSpan pendingTtl) -> TimeSpan` and `StatusCacheTtl.Settled` (a `static readonly TimeSpan` of 6 hours). Task 6 calls `For`.

The policy keys off the *already normalized* CI string rather than re-deriving check states, so the TTL can never disagree with what the report shows.

- [ ] **Step 1: Write the failing tests**

Create `tests/GhPrApi.Tests/StatusCacheTtlTests.cs`:

```csharp
using GhPrApi.Caching;
using GhPrApi.Models;
using Xunit;

namespace GhPrApi.Tests;

public sealed class StatusCacheTtlTests
{
    private static readonly TimeSpan Pending = TimeSpan.FromSeconds(30);

    [Fact]
    public void Pending_checks_use_the_configured_short_ttl()
    {
        Assert.Equal(Pending, StatusCacheTtl.For(NormalizedValues.Ci.Pending, Pending));
    }

    [Fact]
    public void Failing_checks_use_double_the_short_ttl_because_a_rerun_keeps_the_same_sha()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), StatusCacheTtl.For(NormalizedValues.Ci.Failing, Pending));
    }

    [Fact]
    public void Passing_checks_are_settled_and_cached_for_hours()
    {
        Assert.Equal(StatusCacheTtl.Settled, StatusCacheTtl.For(NormalizedValues.Ci.Passing, Pending));
    }

    [Fact]
    public void An_unrecognised_value_is_treated_as_volatile_rather_than_settled()
    {
        Assert.Equal(Pending, StatusCacheTtl.For("something-else", Pending));
    }

    [Fact]
    public void Settled_is_six_hours()
    {
        Assert.Equal(TimeSpan.FromHours(6), StatusCacheTtl.Settled);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~StatusCacheTtlTests`
Expected: FAIL to compile with `CS0246: The type or namespace name 'StatusCacheTtl' could not be found`.

- [ ] **Step 3: Implement the policy**

Create `src/GhPrApi/Caching/StatusCacheTtl.cs`:

```csharp
using GhPrApi.Models;

namespace GhPrApi.Caching;

/// <summary>
/// How long a per-pull-request status entry stays cached, given the CI verdict it produced.
/// </summary>
/// <remarks>
/// The cache key carries the head commit SHA, so a push invalidates by itself. What the key
/// cannot catch is a re-run against the same commit, and people only ever re-run red. So a
/// failing verdict stays volatile while a passing one is treated as final.
/// ponytail: a re-run of a passing check is masked until Settled expires. ?refresh=true is the
/// escape hatch; shorten Settled if that ever actually bites.
/// </remarks>
public static class StatusCacheTtl
{
    public static readonly TimeSpan Settled = TimeSpan.FromHours(6);

    public static TimeSpan For(string normalizedCi, TimeSpan pendingTtl)
    {
        ArgumentNullException.ThrowIfNull(normalizedCi);

        if (normalizedCi.Equals(NormalizedValues.Ci.Passing, StringComparison.OrdinalIgnoreCase))
        {
            return Settled;
        }

        if (normalizedCi.Equals(NormalizedValues.Ci.Failing, StringComparison.OrdinalIgnoreCase))
        {
            return pendingTtl * 2;
        }

        // Pending, and anything unrecognised: treat as still moving.
        return pendingTtl;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~StatusCacheTtlTests`
Expected: `Passed! - Failed: 0, Passed: 5`

- [ ] **Step 5: Commit**

```bash
git add src/GhPrApi/Caching/StatusCacheTtl.cs tests/GhPrApi.Tests/StatusCacheTtlTests.cs
git commit -m "feat: add adaptive TTL policy for per-PR status cache entries"
```

---

### Task 3: Carry the head commit SHA, and make status details serializable

**Files:**
- Modify: `src/GhPrApi/GitHub/GitHubModels.cs`
- Modify: `src/GhPrApi/GitHub/GitHubGraphQlClient.cs`
- Modify: `tests/GhPrApi.Tests/GitHubGraphQlClientTests.cs`
- Modify: `tests/GhPrApi.Tests/TestPullRequests.cs`
- Modify: `tests/GhPrApi.Tests/PullRequestReportServiceTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `GitHubPullRequest.HeadRefOid` (`string`) and `GitHubPullRequest.StatusUnresolved` (`bool`, defaults `false`); `GitHubPullRequestStatusDetails.RequiredStatusCheckNames` changes type from `IReadOnlySet<string>` to `IReadOnlyList<string>`. Tasks 4 and 6 depend on all three.

The type change is required, not cosmetic: `System.Text.Json` cannot deserialize `IReadOnlySet<T>`, and HybridCache's L2 serializes every cached value. `CiNormalizer` only ever calls `.Count` and iterates, so no set behaviour is lost — de-duplication still happens in the `HashSet` inside the client before conversion.

- [ ] **Step 1: Write the failing test**

In `tests/GhPrApi.Tests/GitHubGraphQlClientTests.cs`, add this test after `GetOpenPullRequestsAsync_maps_a_single_page_with_no_truncation`:

```csharp
    [Fact]
    public async Task GetOpenPullRequestsAsync_maps_the_head_commit_sha()
    {
        var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(SinglePageResponse));
        var client = CreateClient(handler);

        var result = await client.GetOpenPullRequestsAsync("ivuorinen", CancellationToken.None);

        var pullRequest = Assert.Single(result.PullRequests);
        Assert.Equal("abc123def456", pullRequest.HeadRefOid);
    }

    [Fact]
    public void OpenPullRequestsQuery_requests_headRefOid()
    {
        var queryField = typeof(GitHubGraphQlClient).GetField(
            "OpenPullRequestsQuery",
            BindingFlags.NonPublic | BindingFlags.Static);
        var query = Assert.IsType<string>(queryField?.GetValue(null));

        Assert.Contains("headRefOid", query, StringComparison.Ordinal);
    }
```

In the same file, add `"headRefOid": "abc123def456",` to the PR node in **both** `SinglePageResponse` (after the `"headRefName"` line) and `RepositoryPageResponse` (after its `"headRefName"` line).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~GitHubGraphQlClientTests`
Expected: FAIL to compile with `CS1061: 'GitHubPullRequest' does not contain a definition for 'HeadRefOid'`.

- [ ] **Step 3: Update the models**

In `src/GhPrApi/GitHub/GitHubModels.cs`, replace the `GitHubPullRequest` and `GitHubPullRequestStatusDetails` records with:

```csharp
public sealed record GitHubPullRequest(
    string Id,
    string RepositoryNameWithOwner,
    string RepositoryOwner,
    string RepositoryName,
    int Number,
    string Title,
    Uri Url,
    DateTimeOffset CreatedAt,
    bool IsDraft,
    string? ReviewDecision,
    string? MergeStateStatus,
    string? Mergeable,
    string HeadRefName,
    string HeadRefOid,
    string BaseRefName,
    GitHubActor? Author,
    IReadOnlyList<string> Labels,
    GitHubPullRequestStatusDetails? StatusDetails = null,
    bool StatusUnresolved = false);

public sealed record GitHubPullRequestStatusDetails(
    IReadOnlyList<GitHubStatusCheck> StatusChecks,
    IReadOnlyList<string> RequiredStatusCheckNames,
    bool RequiresStatusChecks);
```

- [ ] **Step 4: Update the client**

In `src/GhPrApi/GitHub/GitHubGraphQlClient.cs`:

In `OpenPullRequestsQuery`, add `headRefOid` immediately after the `headRefName` line:

```graphql
                    headRefName
                    headRefOid
                    baseRefName
```

Add `string HeadRefOid,` to the `PullRequestNode` record immediately after `string HeadRefName,`:

```csharp
    private sealed record PullRequestNode(
        string Id,
        int Number,
        string Title,
        string Url,
        DateTimeOffset CreatedAt,
        bool IsDraft,
        string? ReviewDecision,
        string? MergeStateStatus,
        string? Mergeable,
        string HeadRefName,
        string HeadRefOid,
        string BaseRefName,
        ActorNode? Author,
        LabelConnection? Labels);
```

In `MapPullRequest`, add the argument immediately after `pullRequest.HeadRefName,`:

```csharp
            pullRequest.HeadRefName,
            pullRequest.HeadRefOid,
            pullRequest.BaseRefName,
```

In `GetPullRequestStatusDetailsAsync`, change the returned required-names collection to a list. Replace:

```csharp
        return new GitHubPullRequestStatusDetails(
            checks,
            requiredNames,
            branchProtectionRule?.RequiresStatusChecks == true);
```

with:

```csharp
        return new GitHubPullRequestStatusDetails(
            checks,
            // HashSet above de-duplicates case-insensitively; the list keeps that result while
            // staying serializable, which IReadOnlySet is not under System.Text.Json.
            requiredNames.ToArray(),
            branchProtectionRule?.RequiresStatusChecks == true);
```

- [ ] **Step 5: Update the test helpers that construct these types**

In `tests/GhPrApi.Tests/TestPullRequests.cs` — this file uses **named** constructor arguments, so add named ones, not positional.

Add a parameter to the `Create` signature, immediately after `string headRefName = "feature/example",`:

```csharp
        string headRefOid = "sha-default",
```

In the `new GitHubPullRequest(...)` call, add immediately after the `HeadRefName: headRefName,` line:

```csharp
            HeadRefOid: headRefOid,
```

In the same call, the default `StatusDetails` builds its required-names collection from a `HashSet`, which no longer compiles. Replace:

```csharp
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "build" },
```

with:

```csharp
                ["build"],
```

In `tests/GhPrApi.Tests/PullRequestReportServiceTests.cs`, replace both occurrences of
`new HashSet<string>(StringComparer.OrdinalIgnoreCase)` (in `StatusDetailsFactory` defaults and in the enrichment test) with `Array.Empty<string>()`.

In `tests/GhPrApi.Tests/EndpointTests.cs`, replace `new HashSet<string>(StringComparer.OrdinalIgnoreCase)` in `FakeGitHubGraphQlClient.GetPullRequestStatusDetailsAsync` with `Array.Empty<string>()`.

In `tests/GhPrApi.Tests/CiNormalizerTests.cs`, replace any `new HashSet<string>(...)` used to build `GitHubPullRequestStatusDetails` with a `string[]` containing the same values.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 93` (78 baseline + 8 from Task 1 + 5 from Task 2 + 2 here)

- [ ] **Step 7: Commit**

```bash
git add src/GhPrApi/GitHub/GitHubModels.cs src/GhPrApi/GitHub/GitHubGraphQlClient.cs tests/GhPrApi.Tests/
git commit -m "feat: carry headRefOid and make status details JSON-serializable"
```

---

### Task 4: Degraded report contract

**Files:**
- Modify: `src/GhPrApi/Models/NormalizedValues.cs`
- Modify: `src/GhPrApi/Models/PullRequestReport.cs`
- Modify: `src/GhPrApi/Services/PullRequestReportBuilder.cs`
- Modify: `src/GhPrApi/Services/HtmlReportFormatter.cs`
- Test: `tests/GhPrApi.Tests/PullRequestReportBuilderTests.cs`
- Test: `tests/GhPrApi.Tests/HtmlReportFormatterTests.cs`

**Interfaces:**
- Consumes: `GitHubPullRequest.StatusUnresolved` from Task 3.
- Produces: `NormalizedValues.Ci.Unknown` (`"unknown"`); `PullRequestReport.Degraded` (`bool`) and `PullRequestReport.Unresolved` (`IReadOnlyList<string>?`); the builder signature becomes `Build(string owner, IReadOnlyList<GitHubPullRequest> pullRequests, bool truncated = false, IReadOnlyList<string>? unresolved = null)`. Task 6 calls this overload.

- [ ] **Step 1: Write the failing tests**

Append to `tests/GhPrApi.Tests/PullRequestReportBuilderTests.cs`:

```csharp
    [Fact]
    public void Build_marks_an_unresolved_pull_request_as_unknown_ci()
    {
        var builder = TestSupport.CreateBuilder(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));
        var pullRequest = TestPullRequests.Create(number: 1) with { StatusUnresolved = true };

        var report = builder.Build("ivuorinen", [pullRequest], false, ["ivuorinen/example#1"]);

        var item = report.Groups.SelectMany(static g => g.PullRequests ?? []).Single();
        Assert.Equal(NormalizedValues.Ci.Unknown, item.Ci);
        Assert.True(report.Degraded);
        Assert.Equal(["ivuorinen/example#1"], report.Unresolved);
    }

    [Fact]
    public void Build_is_not_degraded_when_nothing_is_unresolved()
    {
        var builder = TestSupport.CreateBuilder(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));

        var report = builder.Build("ivuorinen", [TestPullRequests.Create(number: 1)]);

        Assert.False(report.Degraded);
        Assert.Null(report.Unresolved);
    }

    [Fact]
    public void Unknown_ci_does_not_get_promoted_above_failing_or_stale()
    {
        var builder = TestSupport.CreateBuilder(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));
        var unresolved = TestPullRequests.Create(number: 1) with { StatusUnresolved = true };
        var failing = TestPullRequests.Create(number: 2) with
        {
            StatusDetails = new GitHubPullRequestStatusDetails(
                [new GitHubStatusCheck("CheckRun", "build", "COMPLETED", "FAILURE", null, true)],
                [],
                RequiresStatusChecks: true),
        };

        var report = builder.Build("ivuorinen", [unresolved, failing]);

        var items = report.Groups.SelectMany(static g => g.PullRequests ?? []).ToArray();
        Assert.Equal(2, items[0].Number);
        Assert.Equal(NormalizedValues.Ci.Failing, items[0].Ci);
        Assert.Equal(NormalizedValues.Ci.Unknown, items[1].Ci);
    }
```

Append to `tests/GhPrApi.Tests/HtmlReportFormatterTests.cs`:

```csharp
    [Fact]
    public void Format_notes_a_degraded_report()
    {
        var formatter = new HtmlReportFormatter();
        var report = new PullRequestReport(
            "ivuorinen",
            DateTimeOffset.UtcNow,
            TotalCount: 0,
            Groups: [],
            Message: "No open PRs.",
            Truncated: false,
            Degraded: true,
            Unresolved: ["ivuorinen/example#1"]);

        var html = formatter.Format(report);

        Assert.Contains("Some pull requests could not be checked", html, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PullRequestReportBuilderTests|FullyQualifiedName~HtmlReportFormatterTests"`
Expected: FAIL to compile with `CS0117: 'NormalizedValues.Ci' does not contain a definition for 'Unknown'`.

- [ ] **Step 3: Add the CI constant**

In `src/GhPrApi/Models/NormalizedValues.cs`, add to the `Ci` class:

```csharp
        public const string Unknown = "unknown";
```

- [ ] **Step 4: Extend the report record**

In `src/GhPrApi/Models/PullRequestReport.cs`, replace the `PullRequestReport` record with:

```csharp
public sealed record PullRequestReport(
    string Owner,
    DateTimeOffset GeneratedAt,
    int TotalCount,
    IReadOnlyList<PullRequestGroup> Groups,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null,
    bool Truncated = false,
    bool Degraded = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Unresolved = null);
```

`Truncated` and `Degraded` are independent and may both be true: `Truncated` means a configured limit cut the data short, `Degraded` means some status lookups failed.

- [ ] **Step 5: Update the builder**

In `src/GhPrApi/Services/PullRequestReportBuilder.cs`, change the signature:

```csharp
    public PullRequestReport Build(
        string owner,
        IReadOnlyList<GitHubPullRequest> pullRequests,
        bool truncated = false,
        IReadOnlyList<string>? unresolved = null)
    {
```

Immediately after that line, add:

```csharp
        var degraded = unresolved is { Count: > 0 };
        var unresolvedIds = degraded ? unresolved : null;
```

In `BuildItem`, replace the `ci` assignment:

```csharp
        // An unresolved status is an absence of information, not a signal.
        var ci = pullRequest.StatusUnresolved
            ? NormalizedValues.Ci.Unknown
            : _ciNormalizer.Normalize(pullRequest.StatusDetails);
```

Update **both** `return new PullRequestReport(...)` sites. The empty-report one becomes:

```csharp
            return new PullRequestReport(
                owner,
                now,
                TotalCount: 0,
                Groups: [],
                Message: "No open PRs.",
                Truncated: truncated,
                Degraded: degraded,
                Unresolved: unresolvedIds);
```

and the final one:

```csharp
        return new PullRequestReport(
            owner,
            now,
            items.Length,
            groups,
            Truncated: truncated,
            Degraded: degraded,
            Unresolved: unresolvedIds);
```

`GetSortRank` is deliberately left alone: `unknown` falls through to the default rank 2, alongside passing and pending.

- [ ] **Step 6: Add the HTML note**

In `src/GhPrApi/Services/HtmlReportFormatter.cs`, in `Format`, directly after the existing `report.Truncated` block:

```csharp
        if (report.Degraded)
        {
            builder.Append("<p class=\"meta\">Some pull requests could not be checked; their CI shows as unknown.</p>\n");
        }
```

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 97` (93 + 3 builder + 1 formatter)

- [ ] **Step 8: Commit**

```bash
git add src/GhPrApi/Models/ src/GhPrApi/Services/PullRequestReportBuilder.cs src/GhPrApi/Services/HtmlReportFormatter.cs tests/GhPrApi.Tests/
git commit -m "feat: add degraded report contract with unknown CI status"
```

---

### Task 5: Wire HybridCache with the SQLite L2, fail-open

**Files:**
- Modify: `src/GhPrApi/GhPrApi.csproj`
- Modify: `src/GhPrApi/Options/GitHubOptions.cs`
- Modify: `src/GhPrApi/Program.cs`
- Test: `tests/GhPrApi.Tests/EndpointTests.cs`

**Interfaces:**
- Consumes: `SqliteDistributedCache` from Task 1.
- Produces: `HybridCache` resolvable from DI; `GitHubOptions.CachePath` (`string`, default `"cache.db"`) and `GitHubOptions.StatusCacheTtlSeconds` (`int`, default `30`). Task 6 injects `HybridCache` and reads both options.

- [ ] **Step 1: Add the HybridCache package**

In `src/GhPrApi/GhPrApi.csproj`, add to the `PackageReference` item group:

```xml
    <PackageReference Include="Microsoft.Extensions.Caching.Hybrid" Version="10.8.0" />
```

Then run: `dotnet restore src/GhPrApi/GhPrApi.csproj --force-evaluate`

- [ ] **Step 2: Write the failing test**

Append to `tests/GhPrApi.Tests/EndpointTests.cs`:

```csharp
    [Fact]
    public async Task App_starts_and_serves_when_the_cache_path_is_unusable()
    {
        // Fail open: the durable cache is an optimisation, never a source of truth. An
        // unmounted or unwritable volume must cost performance, not availability.
        using var factory = CreateFactory(cachePath: "/proc/definitely-not-writable/cache.db");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
```

Change the `CreateFactory` helper in that file to accept the path:

```csharp
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
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~App_starts_and_serves_when_the_cache_path_is_unusable`
Expected: FAIL — the app throws at startup because nothing catches the SQLite open error yet.

- [ ] **Step 4: Add the options**

In `src/GhPrApi/Options/GitHubOptions.cs`, add:

```csharp
    public string CachePath { get; set; } = "cache.db";

    public int StatusCacheTtlSeconds { get; set; } = 30;
```

- [ ] **Step 5: Register the cache**

In `src/GhPrApi/Program.cs`, add these usings at the top:

```csharp
using GhPrApi.Caching;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
```

Add two validation clauses to the existing options chain, immediately before `.ValidateOnStart()`:

```csharp
    .Validate(options => !string.IsNullOrWhiteSpace(options.CachePath), "GitHub:CachePath is required.")
    .Validate(options => options.StatusCacheTtlSeconds is >= 5 and <= 3600, "GitHub:StatusCacheTtlSeconds must be between 5 and 3600.")
```

**Leave `builder.Services.AddSingleton<PullRequestReportCoalescer>();` in place.** `PullRequestReportService` still takes a `PullRequestReportCoalescer` in its constructor until Task 6, so removing the registration here makes DI validation fail and every endpoint test 500. Task 6 removes both together.

Add the following directly **after** that existing line:

```csharp
builder.Services.AddSingleton<IDistributedCache>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptionsMonitor<GitHubOptions>>().CurrentValue;
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("GhPrApi.Caching");
    var timeProvider = serviceProvider.GetRequiredService<TimeProvider>();

    try
    {
        return new SqliteDistributedCache(options.CachePath, timeProvider);
    }
    catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
    {
        // Fail open. Losing the durable tier costs a cold start, not availability.
        logger.LogWarning(
            ex,
            "Durable cache unavailable at {CachePath}; running with the in-memory tier only.",
            options.CachePath);
        return new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    }
});

builder.Services.AddHybridCache();
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~App_starts_and_serves_when_the_cache_path_is_unusable`
Expected: `Passed! - Failed: 0, Passed: 1`

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 98` (97 + 1)

- [ ] **Step 8: Commit**

```bash
git add src/GhPrApi/GhPrApi.csproj src/GhPrApi/packages.lock.json src/GhPrApi/Options/GitHubOptions.cs src/GhPrApi/Program.cs tests/GhPrApi.Tests/EndpointTests.cs
git commit -m "feat: wire HybridCache with a SQLite L2 that fails open"
```

---

### Task 6: Rewrite the report service around the split

**Files:**
- Modify: `src/GhPrApi/Services/PullRequestReportService.cs`
- Delete: `src/GhPrApi/Services/PullRequestReportCoalescer.cs`
- Modify: `src/GhPrApi/Program.cs`
- Test: `tests/GhPrApi.Tests/PullRequestReportServiceTests.cs`

**Interfaces:**
- Consumes: `StatusCacheTtl.For` (Task 2), `GitHubPullRequest.HeadRefOid` / `.StatusUnresolved` (Task 3), `PullRequestReportBuilder.Build(owner, prs, truncated, unresolved)` (Task 4), `HybridCache` + options (Task 5).
- Produces: the final behaviour. Nothing depends on it.

- [ ] **Step 1: Write the failing tests**

Replace the whole body of `tests/GhPrApi.Tests/PullRequestReportServiceTests.cs` with the version below. It drops the `IMemoryCache`/coalescer plumbing and drives a real `HybridCache`.

```csharp
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
```

`A_settled_status_survives_a_listing_refresh` expects 2 status calls, not 1: `refresh: true` disables reads on every key, including status. It proves the listing and status keys are separate entries rather than one bundle.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~PullRequestReportServiceTests`
Expected: FAIL to compile — `PullRequestReportService` has no constructor taking `HybridCache`.

- [ ] **Step 3: Rewrite the service**

Replace the entire contents of `src/GhPrApi/Services/PullRequestReportService.cs`:

```csharp
using System.Collections.Concurrent;
using GhPrApi.Caching;
using GhPrApi.GitHub;
using GhPrApi.Models;
using GhPrApi.Options;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace GhPrApi.Services;

public sealed class PullRequestReportService : IPullRequestReportService
{
    private const int MaxConcurrentStatusRequests = 8;

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
        var owner = string.IsNullOrWhiteSpace(ownerOverride) ? options.Owner : ownerOverride.Trim();
        var flags = refresh
            ? HybridCacheEntryFlags.DisableLocalCacheRead | HybridCacheEntryFlags.DisableDistributedCacheRead
            : HybridCacheEntryFlags.None;

        var listing = await _cache.GetOrCreateAsync(
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

        await Parallel.ForEachAsync(
            Enumerable.Range(0, listing.PullRequests.Count),
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
                    // One PR's status failing must not discard the whole fetch. Nothing is
                    // cached for this key, so a retry re-fetches only this one.
                    var id = $"{pullRequest.RepositoryNameWithOwner}#{pullRequest.Number}";
                    _logger.LogWarning(ex, "Status details unresolved for {PullRequest}.", id);
                    enriched[index] = pullRequest with { StatusUnresolved = true };
                    unresolved.Add(id);
                }
            }).ConfigureAwait(false);

        var unresolvedIds = unresolved.Count == 0
            ? null
            : unresolved.Order(StringComparer.Ordinal).ToArray();

        // The assembled report is deliberately not cached: it is derived from the parts above,
        // and caching it would restore the all-or-nothing unit this split exists to remove.
        return _builder.Build(owner, enriched, listing.Truncated, unresolvedIds);
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
```

- [ ] **Step 4: Delete the coalescer and its registration**

```bash
git rm src/GhPrApi/Services/PullRequestReportCoalescer.cs
```

In `src/GhPrApi/Program.cs`, delete the line:

```csharp
builder.Services.AddSingleton<PullRequestReportCoalescer>();
```

`HybridCache` provides per-key stampede protection, which is what the coalescer was for — and now at per-PR granularity rather than per-report.

- [ ] **Step 5: Run the service tests**

Run: `dotnet test --filter FullyQualifiedName~PullRequestReportServiceTests`
Expected: `Passed! - Failed: 0, Passed: 6`

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 99` — this task replaces the 5 old service tests with 6 new ones, so 98 - 5 + 6.

- [ ] **Step 7: Verify the cancellation question from the spec**

The spec leaves one item open: whether a per-caller `CancellationToken` can flow through `HybridCache` without one caller's disconnect aborting a fetch shared with others. Confirm `Concurrent_misses_hit_github_once` still passes when the first caller cancels:

```csharp
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
    }
```

Run: `dotnet test --filter FullyQualifiedName~One_caller_cancelling_does_not_break_the_other`

If it PASSES, keep the real token flowing and note in the commit that the audit's uncancellable-fan-out finding is closed. If it FAILS, change both `cancellationToken:` arguments in `GetOpenPullRequestsAsync` and `GetStatusDetailsAsync` to `CancellationToken.None`, delete this test, and record why in a comment. **Do not leave it failing.**

- [ ] **Step 8: Commit**

```bash
git add -A src/GhPrApi/ tests/GhPrApi.Tests/PullRequestReportServiceTests.cs
git commit -m "feat: split the fetch into per-listing and per-PR cache units"
```

---

### Task 7: Deployment, endpoint contract, and docs

**Files:**
- Modify: `Dockerfile`
- Modify: `compose.yml`
- Modify: `.env.example`
- Modify: `README.md`
- Test: `tests/GhPrApi.Tests/EndpointTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: nothing further.

- [ ] **Step 1: Write the failing endpoint test**

Append to `tests/GhPrApi.Tests/EndpointTests.cs`:

```csharp
    [Fact]
    public async Task Degraded_report_is_200_with_the_unresolved_list()
    {
        using var factory = CreateFactory(new FakeGitHubGraphQlClient { FailStatusForNumber = 1 });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/github/open-pull-requests");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("degraded").GetBoolean());
        Assert.Equal("ivuorinen/example#1", body.GetProperty("unresolved")[0].GetString());
    }
```

Add to `FakeGitHubGraphQlClient` in that file:

```csharp
        public int? FailStatusForNumber { get; init; }
```

and make its `GetPullRequestStatusDetailsAsync` honour it:

```csharp
        public Task<GitHubPullRequestStatusDetails> GetPullRequestStatusDetailsAsync(GitHubPullRequest pullRequest, CancellationToken cancellationToken)
        {
            if (FailStatusForNumber == pullRequest.Number)
            {
                return Task.FromException<GitHubPullRequestStatusDetails>(
                    new GitHubQueryException("GitHub GraphQL query failed with HTTP 502."));
            }

            return Task.FromResult(new GitHubPullRequestStatusDetails([], [], RequiresStatusChecks: false));
        }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~Degraded_report_is_200`
Expected: FAIL — `FailStatusForNumber` does not exist yet.

- [ ] **Step 3: Run the test to verify it passes**

After Step 1's edits compile, the service from Task 6 already produces the degraded shape.

Run: `dotnet test --filter FullyQualifiedName~Degraded_report_is_200`
Expected: `Passed! - Failed: 0, Passed: 1`

- [ ] **Step 4: Give the container a writable cache directory**

In `Dockerfile`, immediately after the `curl` install block and before `ENV ASPNETCORE_URLS`, add:

```dockerfile
# The image runs as uid 1654 (app); a freshly mounted volume is root-owned, so create and
# chown the mount point before dropping privileges or the cache silently falls back to L1.
RUN mkdir -p /data && chown app:app /data
VOLUME ["/data"]
```

- [ ] **Step 5: Mount the volume in compose**

In `compose.yml`, add to the `gh-pr-api` service after the `expose` block:

```yaml
    volumes:
      - gh-pr-api-cache:/data
```

and add at the end of the file:

```yaml
volumes:
  gh-pr-api-cache:
```

Add to the service's `environment` block:

```yaml
      GitHub__CachePath: "${GITHUB_CACHE_PATH:-/data/cache.db}"
      GitHub__StatusCacheTtlSeconds: "${GITHUB_STATUS_CACHE_TTL_SECONDS:-30}"
```

Add to `.env.example`:

```text
GITHUB_CACHE_PATH=/data/cache.db
GITHUB_STATUS_CACHE_TTL_SECONDS=30
```

- [ ] **Step 6: Verify the container really can write the cache**

```bash
docker build -t ghpr:cache-check .
docker volume create ghpr-cache-check
docker run -d --name ghpr-cache-check -p 18097:8080 \
  -v ghpr-cache-check:/data \
  -e GitHub__Owner=ivuorinen -e GitHub__Token=dummy \
  ghpr:cache-check
sleep 8
# Hit the REPORT endpoint, not /health/live. IDistributedCache is resolved lazily, so a
# health check never touches the cache and /data stays empty whether or not it works.
curl -s -o /dev/null "http://localhost:18097/api/github/open-pull-requests"
sleep 2
docker exec ghpr-cache-check ls -l /data
docker logs ghpr-cache-check 2>&1 | grep -c "Durable cache unavailable"
docker restart ghpr-cache-check && sleep 9
docker exec ghpr-cache-check ls -l /data
docker rm -f ghpr-cache-check && docker volume rm ghpr-cache-check
```

Expected: `/data` contains `cache.db` owned by `app`, the warning count is `0`, and `cache.db`
is still there after the restart.

A count of `1` means the durable tier fell back to in-memory. Read the logged exception before
assuming it is the chown: the path in the message tells you whether `GitHub:CachePath` even
pointed at `/data`. The image sets `ENV GitHub__CachePath=/data/cache.db` precisely because the
code default is a relative `cache.db` that resolves under the read-only `/app`.

- [ ] **Step 7: Update the docs**

In `README.md`:

Add to the configuration key table:

| Key | Default | Description |
|---|---:|---|
| `GitHub:CachePath` | `cache.db` | SQLite file backing the durable cache tier. Set to `/data/cache.db` in the container. |
| `GitHub:StatusCacheTtlSeconds` | `30` | TTL for a pull request whose checks are still running. A failing verdict uses twice this; an all-green verdict is cached for 6 hours. |

Change the `GitHub:CacheTtlSeconds` row's description to:
`TTL for the repository and pull-request listing. Per-PR check status has its own adaptive TTL.`

Add after the JSON response section:

```markdown
If some pull requests' check status cannot be fetched, the response is still `200` and
carries `"degraded": true` plus `"unresolved": ["owner/repo#1"]`. Those pull requests
report `"ci": "unknown"`. A total failure to list repositories is still `503`.

Results are cached in two tiers: an in-memory tier and a SQLite file at `GitHub:CachePath`
that survives a restart. If that path is not writable the service logs a warning and runs
in-memory only, so a missing volume costs a cold start rather than availability.
```

Add to the Coolify section's environment block:

```text
GitHub__CachePath=/data/cache.db
GitHub__StatusCacheTtlSeconds=30
```

and a line after that block:

```markdown
Add a Coolify persistent volume mapped to `/data` so the cache survives redeploys.
```

- [ ] **Step 8: Full verification**

```bash
dotnet test
rm -rf src/GhPrApi/obj tests/GhPrApi.Tests/obj && dotnet restore --locked-mode
docker build -q -t ghpr:final . && echo "docker OK"
hadolint Dockerfile && echo "hadolint clean"
zizmor -p .github/workflows
checkov -d . --compact --quiet | grep -E 'Passed checks|Failed checks'
```

Expected: `Passed: 100` (or `101` if the cancellation test from Task 6 Step 7 was kept); locked restore succeeds; docker builds; hadolint silent; zizmor reports no findings; checkov shows `Failed checks: 0` twice.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: persist the cache to a mounted volume and document the degraded contract"
```

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
| --- | --- |
| Cache units, keys, `headSha` | 3, 6 |
| Adaptive TTL (pending / failing / settled) | 2, 6 |
| `SqliteDistributedCache` | 1 |
| HybridCache L1+L2 wiring, fail-open | 5 |
| Report not cached; `generatedAt` becomes live | 6 |
| `refresh=true` bypasses both tiers | 6 |
| Cancellation open question | 6, Step 7 |
| Listing failure still 503 | unchanged; covered by the existing `Json_reports_503_problem_details_when_github_is_unreachable` |
| Degraded contract, `Ci.Unknown`, sort rank, HTML note | 4 |
| `Degraded` / `Truncated` independence | 4, Step 4 |
| Config keys + validation | 5, 7 |
| Dockerfile `/data` chown, compose volume | 7 |
| Ceilings as `ponytail:` comments | 1, 2 |
| Tests 1-8 from the spec | 1, 2, 6, 6, 6, 6, 6, 7 |

No gaps.

**Placeholder scan:** none. Every code step carries complete code; every run step carries an exact command and expected output. Task 6 Step 7 is a conditional, but both branches are fully specified with a required resolution.

**Type consistency:** `Build(owner, prs, truncated, unresolved)` is defined in Task 4 and called with four arguments in Task 6. `StatusCacheTtl.For(string, TimeSpan)` is defined in Task 2 and called in Task 6. `HeadRefOid` / `StatusUnresolved` are added in Task 3 and used in Tasks 4 and 6. `RequiredStatusCheckNames` becomes `IReadOnlyList<string>` in Task 3, and every construction site listed in Task 3 Step 5 is updated to match. **Test-count arithmetic**, corrected during this review (the first draft drifted after Task 2):

| After task | Change | Total |
| --- | --- | ---: |
| baseline | | 78 |
| 1 | +8 `SqliteDistributedCacheTests` | 86 |
| 2 | +5 `StatusCacheTtlTests` | 91 |
| 3 | +2 `GitHubGraphQlClientTests` | 93 |
| 4 | +3 builder, +1 formatter | 97 |
| 5 | +1 endpoint | 98 |
| 6 | -5 old service tests, +6 new | 99 |
| 7 | +1 endpoint | 100 |

Task 6 Step 7 adds one more if the cancellation test is kept, giving 101.

**Fixture correctness:** `TestPullRequests.Create` defaults `repositoryNameWithOwner` to
`"ivuorinen/example"`, so the `"ivuorinen/example#2"` assertions in Task 6 are right. That
file constructs `GitHubPullRequest` with *named* arguments and builds
`RequiredStatusCheckNames` from a `HashSet`, so Task 3 Step 5 spells out both edits
explicitly rather than saying "add it positionally".
