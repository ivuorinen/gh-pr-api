using System.Globalization;
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
/// Every operation fails open: a <see cref="SqliteException"/>, <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/> is logged and swallowed, so a read degrades to a
/// miss and a write to a no-op. That set matches the one Program.cs catches around
/// construction — the same open-then-execute sequence runs here, so it can fail the same ways.
/// The cache is an optimisation, never a source of truth, and HybridCache surfaces backend
/// exceptions to the caller by default — without this a transient lock or a full disk would
/// turn into a failed request. Construction is the one exception: it throws so startup can fall
/// back to the in-memory tier and say so once.
/// ponytail: single-writer SQLite, sized for one replica. If this ever scales out, swap the
/// IDistributedCache registration in Program.cs for Redis; nothing else has to change.
/// </remarks>
public sealed class SqliteDistributedCache : IDistributedCache
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    // Bump this AND the literal in EnsureSchema together for any change to the schema.
    // A cache file outlives the binary that wrote it (the image declares VOLUME ["/data"], by
    // design), and CREATE TABLE IF NOT EXISTS silently accepts an older shape — every write
    // would then fail into the fail-open catch, leaving a permanently dead tier whose only
    // symptom is log noise. The file is disposable, so a mismatch drops the table rather than
    // migrating it. The two values are kept in step by
    // SqliteDistributedCacheTests.The_schema_version_written_matches_the_one_checked.
    internal const int SchemaVersion = 1;

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
        DropIfSchemaVersionDiffers(connection);
        EnsureSchema(connection);
    }

    public byte[]? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // Degrade to a miss. The caller refetches, which is slower but correct.
        return Execute<byte[]?>("read", key, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM cache WHERE key = $key AND expires_at > $now;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$now", Now());

            return command.ExecuteScalar() as byte[];
        },
        fallback: null);
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        // ResolveExpiry throws ArgumentException on a missing expiry, which is a caller bug
        // rather than a backend failure, so it stays outside the try.
        var expiresAt = ResolveExpiry(options);

        // Degrade to a no-op. The entry is simply not durable; L1 still has it.
        Execute("write", key, connection =>
        {
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
            return true;
        },
        fallback: false);
    }

    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // Degrade to a no-op. Worst case is a stale entry that still carries its expiry.
        Execute("evict", key, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM cache WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
            return true;
        },
        fallback: false);
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

    /// <summary>
    /// Runs one cache operation, repairing a vanished schema once and failing open on the
    /// backend errors Program.cs also treats as recoverable.
    /// </summary>
    private T Execute<T>(string operation, string key, Func<SqliteConnection, T> work, T fallback)
    {
        try
        {
            using var connection = OpenConnection();

            try
            {
                return work(connection);
            }
            catch (SqliteException ex) when (IsMissingTable(ex))
            {
                // The file was deleted or replaced under us, and ReadWriteCreate silently made
                // an empty one. Without this every later call fails into the catch below and
                // the tier stays dead until the process restarts.
                _logger.LogWarning("Durable cache schema was missing; recreating it.");
                EnsureSchema(connection);
                return work(connection);
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            LogFailure(ex, operation, key);
            return fallback;
        }
    }

    private static bool IsMissingTable(SqliteException exception) =>
        exception.SqliteErrorCode == 1 // SQLITE_ERROR, which is what "no such table" reports.
        && exception.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        // Inline, not a hoisted constant: PRAGMA arguments cannot be parameterised, so
        // user_version has to be a literal, and assigning CommandText from anything other than a
        // literal reads as SQL injection to static analysis. There is only one call site.
        // The literal below must stay in step with SchemaVersion above; the test
        // The_schema_version_written_matches_the_one_checked enforces that.
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS cache (
                key        TEXT    PRIMARY KEY,
                value      BLOB    NOT NULL,
                expires_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_cache_expires_at ON cache (expires_at);
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
    }

    private void DropIfSchemaVersionDiffers(SqliteConnection connection)
    {
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var found = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);

        // 0 is a fresh file, or one written before versioning existed; both are compatible with
        // the current shape, so only a different non-zero version is a mismatch.
        if (found == 0 || found == SchemaVersion)
        {
            return;
        }

        _logger.LogWarning(
            "Durable cache was written by schema version {Found}, this build expects {Expected}; discarding it.",
            found,
            SchemaVersion);

        using var dropCommand = connection.CreateCommand();
        dropCommand.CommandText = "DROP TABLE IF EXISTS cache;";
        dropCommand.ExecuteNonQuery();
    }

    private void LogFailure(Exception exception, string operation, string key) =>
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
