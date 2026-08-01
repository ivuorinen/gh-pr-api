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
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
