using System.Globalization;
using GhPrApi.Caching;
using Microsoft.Data.Sqlite;
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
    public void Get_degrades_to_a_miss_when_the_database_file_is_corrupt()
    {
        // Fail open: HybridCache surfaces backend exceptions to the caller by default, so a
        // throwing Get would turn a cache problem into a failed request.
        var cache = Create(out _);
        cache.Set("k", [1], Expires(TimeSpan.FromMinutes(5)));
        Corrupt();

        Assert.Null(cache.Get("k"));
    }

    [Fact]
    public void Set_degrades_to_a_no_op_when_the_database_file_is_corrupt()
    {
        var cache = Create(out _);
        cache.Set("k", [1], Expires(TimeSpan.FromMinutes(5)));
        Corrupt();

        cache.Set("k", [2], Expires(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Remove_degrades_to_a_no_op_when_the_database_file_is_corrupt()
    {
        var cache = Create(out _);
        cache.Set("k", [1], Expires(TimeSpan.FromMinutes(5)));
        Corrupt();

        cache.Remove("k");
    }

    [Fact]
    public void Set_still_rejects_an_entry_with_no_absolute_expiry_after_a_corrupt_file()
    {
        // The fail-open catch must not swallow caller bugs: a missing expiry is an
        // ArgumentException, not a backend failure, and still has to surface.
        var cache = Create(out _);
        Corrupt();

        Assert.Throws<ArgumentException>(() => cache.Set("k", [1], new DistributedCacheEntryOptions()));
    }

    [Fact]
    public void Constructor_throws_a_type_the_startup_fallback_catches()
    {
        var unwritable = Path.Combine(Path.GetTempPath(), $"ghpr-{Guid.NewGuid():N}", "nested", "cache.db");

        // Program.cs falls back to the in-memory tier on SqliteException, IOException and
        // UnauthorizedAccessException only. Anything else escapes the DI factory and stops the
        // process from starting, so the exception *type* is the contract here -- asserting
        // merely that it throws would let that regression through.
        var exception = Assert.ThrowsAny<Exception>(
            () => new SqliteDistributedCache(unwritable, new FakeTimeProvider()));

        Assert.True(
            exception is SqliteException or IOException or UnauthorizedAccessException,
            $"Program.cs's fallback filter does not catch {exception.GetType().Name}.");
    }

    [Fact]
    public void A_deleted_database_file_is_recreated_rather_than_killing_the_tier()
    {
        // ReadWriteCreate silently remakes a deleted file, empty and table-less. Without schema
        // repair every later call would fail into the fail-open catch and the durable tier would
        // stay dead until the process restarted -- with no symptom but log noise.
        var cache = Create(out _);
        cache.Set("k", [1], Expires(TimeSpan.FromMinutes(5)));

        SqliteConnection.ClearAllPools();
        File.Delete(_path);

        cache.Set("k", [2], Expires(TimeSpan.FromMinutes(5)));

        Assert.Equal([2], cache.Get("k"));
    }

    [Fact]
    public void The_schema_version_written_matches_the_one_checked()
    {
        // PRAGMA arguments cannot be parameterised, so the version appears twice: as the
        // SchemaVersion constant the constructor compares against, and as a literal inside the
        // DDL. Bumping one and not the other would make every start discard a file it had just
        // written. This is what keeps the two honest.
        _ = Create(out _);
        SqliteConnection.ClearAllPools();

        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";

        Assert.Equal(SqliteDistributedCache.SchemaVersion, Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_file_from_a_different_schema_version_is_discarded_rather_than_reused()
    {
        // A cache file outlives the binary that wrote it (the image declares VOLUME ["/data"]),
        // and CREATE TABLE IF NOT EXISTS would silently accept an incompatible older shape.
        var cache = Create(out _);
        cache.Set("k", [1], Expires(TimeSpan.FromMinutes(5)));
        SqliteConnection.ClearAllPools();

        using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 999;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var rebuilt = new SqliteDistributedCache(_path, new FakeTimeProvider());

        Assert.Null(rebuilt.Get("k"));
        rebuilt.Set("k", [3], Expires(TimeSpan.FromMinutes(5)));
        Assert.Equal([3], rebuilt.Get("k"));
    }

    private SqliteDistributedCache Create(out FakeTimeProvider time)
    {
        time = new FakeTimeProvider();
        return new SqliteDistributedCache(_path, time);
    }

    private static DistributedCacheEntryOptions Expires(TimeSpan ttl) =>
        new() { AbsoluteExpirationRelativeToNow = ttl };

    /// <summary>Replaces the database with bytes SQLite will reject, so every subsequent
    /// operation raises a real SqliteException rather than a simulated one.</summary>
    private void Corrupt()
    {
        SqliteConnection.ClearAllPools();
        File.WriteAllText(_path, "this is not a sqlite database");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
