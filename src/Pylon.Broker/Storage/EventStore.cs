using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pylon.Core.Events;
using Pylon.Core.Monitors;

namespace Pylon.Broker.Storage;

/// <summary>How long the replay log and the observation store keep things (D2).</summary>
public sealed record Retention
{
    public TimeSpan EventAge { get; init; } = TimeSpan.FromDays(30);
    public int MaxEvents { get; init; } = 100_000;
    public TimeSpan PayloadAge { get; init; } = TimeSpan.FromDays(7);
    public long MaxPayloadBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>One payload bigger than this is cut, so a huge poll response can't push everything else out.</summary>
    public int MaxPayloadSize { get; init; } = 512 * 1024;
}

/// <summary>
/// One SQLite file (WAL): the replay log, the observation store (payloads, only for the stream widget) and each
/// monitor's fingerprint state. <c>AUTOINCREMENT</c> on purpose: a sequence is never reused after pruning, which
/// is what makes <c>since=</c> safe.
/// </summary>
public sealed class EventStore
{
    static readonly JsonSerializerOptions Json = PylonEvent.Json;

    readonly string _connectionString;
    readonly TimeProvider _time;
    readonly Retention _retention;

    public EventStore(string path, TimeProvider time, Retention? retention = null)
    {
        _time = time;
        _retention = retention ?? new Retention();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Migrate();
    }

    SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    void Migrate()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                observed_at TEXT NOT NULL,
                source TEXT NOT NULL,
                monitor_id TEXT,
                topic TEXT NOT NULL,
                type TEXT NOT NULL,
                kind TEXT NOT NULL,
                resource_id TEXT,
                hint TEXT,
                summary TEXT NOT NULL,
                status TEXT NOT NULL,
                via TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_events_observed ON events(observed_at);
            CREATE TABLE IF NOT EXISTS payloads (
                sequence INTEGER PRIMARY KEY,
                stored_at TEXT NOT NULL,
                content_type TEXT NOT NULL,
                content TEXT NOT NULL,
                size INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS monitor_state (
                monitor_id TEXT NOT NULL,
                key TEXT NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (monitor_id, key)
            );
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Sequences and stores the drafts in one transaction, in order.</summary>
    public IReadOnlyList<EventRecord> Append(IReadOnlyList<EventDraft> drafts)
    {
        if (drafts.Count == 0)
            return [];
        var now = _time.GetUtcNow();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var records = new List<EventRecord>(drafts.Count);
        foreach (var draft in drafts)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO events (observed_at, source, monitor_id, topic, type, kind, resource_id, hint, summary, status, via)
                VALUES ($at, $source, $monitor, $topic, $type, $kind, $resource, $hint, $summary, $status, $via);
                SELECT last_insert_rowid();
                """;
            var observed = draft.ObservedAtUtc ?? now;
            insert.Parameters.AddWithValue("$at", observed.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$source", draft.Source);
            insert.Parameters.AddWithValue("$monitor", (object?)draft.MonitorId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$topic", draft.Topic);
            insert.Parameters.AddWithValue("$type", draft.Type);
            insert.Parameters.AddWithValue("$kind", Name(draft.Kind));
            insert.Parameters.AddWithValue("$resource", (object?)draft.ResourceId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$hint", draft.Hint is { Count: > 0 } hint ? JsonSerializer.Serialize(hint, Json) : DBNull.Value);
            insert.Parameters.AddWithValue("$summary", draft.Summary);
            insert.Parameters.AddWithValue("$status", Name(draft.Status));
            insert.Parameters.AddWithValue("$via", Name(draft.Via));
            var sequence = (long)insert.ExecuteScalar()!;

            if (draft.Payload is { } payload)
            {
                var content = payload.Content.Length > _retention.MaxPayloadSize
                    ? payload.Content[.._retention.MaxPayloadSize] + "\n… (cut: payload was " + payload.Content.Length + " characters)"
                    : payload.Content;
                using var store = connection.CreateCommand();
                store.Transaction = transaction;
                store.CommandText = "INSERT INTO payloads (sequence, stored_at, content_type, content, size) VALUES ($s, $at, $type, $content, $size)";
                store.Parameters.AddWithValue("$s", sequence);
                store.Parameters.AddWithValue("$at", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                store.Parameters.AddWithValue("$type", payload.ContentType);
                store.Parameters.AddWithValue("$content", content);
                store.Parameters.AddWithValue("$size", content.Length);
                store.ExecuteNonQuery();
            }
            records.Add(EventRecord.From(sequence, draft with { ObservedAtUtc = observed }, now));
        }
        transaction.Commit();
        return records;
    }

    /// <summary>The highest sequence ever issued, including pruned ones (0 when nothing has happened yet).</summary>
    public long LatestSequence()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'events'), 0)";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>The oldest sequence still in the log, or null when it's empty.</summary>
    public long? OldestSequence()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(sequence) FROM events";
        return command.ExecuteScalar() is long value ? value : null;
    }

    public IReadOnlyList<EventRecord> ReadAfter(long after, int limit) =>
        Query("SELECT * FROM events WHERE sequence > $n ORDER BY sequence LIMIT $limit", after, limit);

    /// <summary>Newest first, for the widget's history: the <paramref name="limit"/> events before <paramref name="before"/>.</summary>
    public IReadOnlyList<EventRecord> ReadBefore(long? before, int limit) =>
        Query("SELECT * FROM events WHERE sequence < $n ORDER BY sequence DESC LIMIT $limit", before ?? long.MaxValue, limit);

    public Payload? GetPayload(long sequence)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content_type, content FROM payloads WHERE sequence = $s";
        command.Parameters.AddWithValue("$s", sequence);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new Payload(reader.GetString(0), reader.GetString(1)) : null;
    }

    public (int Events, int Payloads) Prune()
    {
        var now = _time.GetUtcNow();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM events WHERE observed_at < $eventsBefore;
            DELETE FROM events WHERE sequence <= (SELECT MAX(sequence) FROM events) - $maxEvents;
            """;
        command.Parameters.AddWithValue("$eventsBefore", (now - _retention.EventAge).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$maxEvents", _retention.MaxEvents);
        var events = command.ExecuteNonQuery();

        using var payloads = connection.CreateCommand();
        payloads.CommandText = """
            DELETE FROM payloads WHERE stored_at < $before OR sequence NOT IN (SELECT sequence FROM events);
            DELETE FROM payloads WHERE sequence IN (
                SELECT sequence FROM (
                    SELECT sequence, SUM(size) OVER (ORDER BY sequence DESC) AS running FROM payloads
                ) WHERE running > $maxBytes);
            """;
        payloads.Parameters.AddWithValue("$before", (now - _retention.PayloadAge).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        payloads.Parameters.AddWithValue("$maxBytes", _retention.MaxPayloadBytes);
        return (events, payloads.ExecuteNonQuery());
    }

    public IMonitorState StateFor(string monitorId) => new MonitorState(this, monitorId);

    public void ClearState(string monitorId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM monitor_state WHERE monitor_id = $m";
        command.Parameters.AddWithValue("$m", monitorId);
        command.ExecuteNonQuery();
    }

    IReadOnlyList<EventRecord> Query(string sql, long n, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$n", n);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var list = new List<EventRecord>();
        while (reader.Read())
            list.Add(Read(reader));
        if (list.Count == 0)
            return list;

        using var payloads = connection.CreateCommand();
        payloads.CommandText = $"SELECT sequence FROM payloads WHERE sequence IN ({string.Join(',', list.Select(r => r.Sequence))})";
        var withPayload = new HashSet<long>();
        using (var p = payloads.ExecuteReader())
            while (p.Read())
                withPayload.Add(p.GetInt64(0));
        return [.. list.Select(r => r with { HasPayload = withPayload.Contains(r.Sequence) })];
    }

    static EventRecord Read(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("sequence")),
        r.GetString(r.GetOrdinal("source")),
        r.IsDBNull(r.GetOrdinal("monitor_id")) ? null : r.GetString(r.GetOrdinal("monitor_id")),
        r.GetString(r.GetOrdinal("topic")),
        r.GetString(r.GetOrdinal("type")),
        Parse<EventKind>(r.GetString(r.GetOrdinal("kind"))),
        r.IsDBNull(r.GetOrdinal("resource_id")) ? null : r.GetString(r.GetOrdinal("resource_id")),
        r.IsDBNull(r.GetOrdinal("hint")) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(r.GetOrdinal("hint")), Json),
        DateTimeOffset.Parse(r.GetString(r.GetOrdinal("observed_at")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        r.GetString(r.GetOrdinal("summary")),
        Parse<EventStatus>(r.GetString(r.GetOrdinal("status"))),
        Parse<EventVia>(r.GetString(r.GetOrdinal("via"))),
        HasPayload: false);

    static string Name<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    static T Parse<T>(string value) where T : struct, Enum => Enum.Parse<T>(value, ignoreCase: true);

    sealed class MonitorState(EventStore store, string monitorId) : IMonitorState
    {
        public string? Get(string key)
        {
            using var connection = store.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM monitor_state WHERE monitor_id = $m AND key = $k";
            command.Parameters.AddWithValue("$m", monitorId);
            command.Parameters.AddWithValue("$k", key);
            return command.ExecuteScalar() as string;
        }

        public void Set(string key, string value)
        {
            using var connection = store.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO monitor_state (monitor_id, key, value) VALUES ($m, $k, $v) ON CONFLICT (monitor_id, key) DO UPDATE SET value = $v";
            command.Parameters.AddWithValue("$m", monitorId);
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$v", value);
            command.ExecuteNonQuery();
        }

        public void Remove(string key)
        {
            using var connection = store.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM monitor_state WHERE monitor_id = $m AND key = $k";
            command.Parameters.AddWithValue("$m", monitorId);
            command.Parameters.AddWithValue("$k", key);
            command.ExecuteNonQuery();
        }

        public IReadOnlyList<string> Keys()
        {
            using var connection = store.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key FROM monitor_state WHERE monitor_id = $m";
            command.Parameters.AddWithValue("$m", monitorId);
            using var reader = command.ExecuteReader();
            var keys = new List<string>();
            while (reader.Read())
                keys.Add(reader.GetString(0));
            return keys;
        }
    }
}
