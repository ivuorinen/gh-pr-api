using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

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
/// Every operation fails open: a <see cref="SqliteException"/> is logged and swallowed, so a
/// read degrades to a miss and a write to a no-op. The cache is an optimisation, never a source
/// of truth, and HybridCache surfaces backend exceptions to the caller by default — without this
/// a transient lock or a full disk would turn into a failed request. Construction is the one
/// exception: it throws so startup can fall back to the in-memory tier and say so once.
/// ponytail: single-writer SQLite, sized for one replica. If this ever scales out, swap the
/// IDistributedCache registration in Program.cs for Redis; nothing else has to change.
/// </remarks>
public sealed class SqliteDistributedCache : IDistributedCache
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private long _lastCleanupTicks;

    public SqliteDistributedCache(
        string databasePath,
        TimeProvider timeProvider,
        ILogger<SqliteDistributedCache>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _logger = logger ?? (ILogger)NullLogger<SqliteDistributedCache>.Instance;

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

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM cache WHERE key = $key AND expires_at > $now;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$now", Now());

            return command.ExecuteScalar() as byte[];
        }
        catch (SqliteException ex)
        {
            // Degrade to a miss. The caller refetches, which is slower but correct.
            LogFailure(ex, "read", key);
            return null;
        }
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        // ResolveExpiry throws ArgumentException on a missing expiry, which is a caller bug
        // rather than a backend failure, so it stays outside the try.
        var expiresAt = ResolveExpiry(options);

        try
        {
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
        catch (SqliteException ex)
        {
            // Degrade to a no-op. The entry is simply not durable; L1 still has it.
            LogFailure(ex, "write", key);
        }
    }

    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM cache WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            // Degrade to a no-op. Worst case is a stale entry that still carries its expiry.
            LogFailure(ex, "evict", key);
        }
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

    private void LogFailure(SqliteException exception, string operation, string key) =>
        // Warning, not Debug: a durable tier that has silently stopped working looks exactly
        // like a cold cache, and the whole point of the tier is that it is not cold.
        // ponytail: one line per failed operation. A hard-down database with the rate limiter
        // wide open is noisy; add throttling only if that actually happens.
        _logger.LogWarning(
            exception,
            "Durable cache {Operation} failed for {CacheKey}; continuing without it.",
            operation,
            key);

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
