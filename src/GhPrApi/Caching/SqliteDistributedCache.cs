using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Distributed;

namespace GhPrApi.Caching;

/// <summary>
/// Durable L2 for <c>HybridCache</c>: one file, one table, absolute expiry only.
/// </summary>
/// <remarks>
/// Sliding expiration is not supported and <see cref="Refresh(string)"/> is a no-op.
/// HybridCache only ever sets an absolute expiry, so nothing is lost.
/// The async members delegate to the synchronous ones on purpose: Microsoft.Data.Sqlite's
/// async API is synchronous underneath because SQLite has no async file I/O, so dispatching to
/// a thread would add a context switch and buy nothing against a local file.
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
        throw new ArgumentException("A cache entry requires an absolute expiry.", nameof(options));
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
