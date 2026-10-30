using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pylon.Core.Events;
using Pylon.Core.Monitors;

namespace Pylon.Broker.Storage;

/// <summary>One hour of one monitor's polling, for the Budget tab.</summary>
public sealed record PollHour(
    DateTimeOffset Hour,
    DateTimeOffset FirstPoll,
    int Polls,
    int Requests,
    int NotModified,
    int Events,
    int Errors,
    int Warnings,
    int? MinRemaining,
    int? Limit,
    int Measured200,
    int Cost200,
    int Measured304,
    int Cost304);

/// <summary>
/// Every poll a monitor made: the calls, what it noticed, what it reported or kept quiet, and the events that came of
/// it. Lives in the same SQLite file as the event log. Only the app reads it; nothing here reaches subscribers.
/// </summary>
public sealed class PollJournal
{
    static readonly JsonSerializerOptions Json = PylonEvent.Json;

    readonly string _connectionString;
    readonly TimeProvider _time;
    readonly Retention _retention;

    public PollJournal(string path, TimeProvider time, Retention? retention = null)
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
            CREATE TABLE IF NOT EXISTS polls (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                monitor_id TEXT NOT NULL,
                started_at TEXT NOT NULL,
                milliseconds INTEGER NOT NULL,
                outcome TEXT NOT NULL,
                error TEXT,
                requests INTEGER NOT NULL,
                not_modified INTEGER NOT NULL,
                events INTEGER NOT NULL,
                warnings INTEGER NOT NULL,
                rate_remaining INTEGER,
                rate_limit INTEGER,
                measured_200 INTEGER NOT NULL,
                cost_200 INTEGER NOT NULL,
                measured_304 INTEGER NOT NULL,
                cost_304 INTEGER NOT NULL,
                detail TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_polls_monitor ON polls(monitor_id, id);
            CREATE INDEX IF NOT EXISTS ix_polls_started ON polls(started_at);
            CREATE TABLE IF NOT EXISTS poll_events (
                sequence INTEGER PRIMARY KEY,
                poll_id INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS poll_bodies (
                poll_id INTEGER NOT NULL,
                call INTEGER NOT NULL,
                stored_at TEXT NOT NULL,
                content TEXT NOT NULL,
                size INTEGER NOT NULL,
                PRIMARY KEY (poll_id, call)
            );
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Stores a poll, its response bodies and its links to events. Returns it with its id.</summary>
    public PollRecord Add(PollRecord poll)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO polls (monitor_id, started_at, milliseconds, outcome, error, requests, not_modified, events, warnings,
                rate_remaining, rate_limit, measured_200, cost_200, measured_304, cost_304, detail)
            VALUES ($monitor, $at, $ms, $outcome, $error, $requests, $notModified, $events, $warnings,
                $remaining, $limit, $m200, $c200, $m304, $c304, $detail);
            SELECT last_insert_rowid();
            """;
        var rate = poll.RateLimit;
        var measured = poll.Calls.Where(c => c.RateCost is not null).ToList();
        insert.Parameters.AddWithValue("$monitor", poll.MonitorId);
        insert.Parameters.AddWithValue("$at", Stamp(poll.StartedAt));
        insert.Parameters.AddWithValue("$ms", poll.Milliseconds);
        insert.Parameters.AddWithValue("$outcome", poll.Outcome.ToString().ToLowerInvariant());
        insert.Parameters.AddWithValue("$error", (object?)poll.Error ?? DBNull.Value);
        insert.Parameters.AddWithValue("$requests", poll.Requests);
        insert.Parameters.AddWithValue("$notModified", poll.NotModified);
        insert.Parameters.AddWithValue("$events", poll.Sequences.Count);
        insert.Parameters.AddWithValue("$warnings", poll.HasWarnings ? 1 : 0);
        insert.Parameters.AddWithValue("$remaining", (object?)rate?.Remaining ?? DBNull.Value);
        insert.Parameters.AddWithValue("$limit", (object?)rate?.Limit ?? DBNull.Value);
        insert.Parameters.AddWithValue("$m200", measured.Count(c => !c.NotModified));
        insert.Parameters.AddWithValue("$c200", measured.Where(c => !c.NotModified).Sum(c => c.RateCost!.Value));
        insert.Parameters.AddWithValue("$m304", measured.Count(c => c.NotModified));
        insert.Parameters.AddWithValue("$c304", measured.Where(c => c.NotModified).Sum(c => c.RateCost!.Value));
        insert.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(new Detail(poll.Calls, poll.Observations, poll.Notes, poll.Sequences), Json));
        var id = (long)insert.ExecuteScalar()!;

        var now = Stamp(_time.GetUtcNow());
        for (var i = 0; i < poll.Calls.Count; i++)
        {
            if (poll.Calls[i].Body is not { Length: > 0 } body)
                continue;
            var content = body.Length > _retention.MaxPayloadSize
                ? body[.._retention.MaxPayloadSize] + "\n… (cut: the body was " + body.Length + " characters)"
                : body;
            using var store = connection.CreateCommand();
            store.Transaction = transaction;
            store.CommandText = "INSERT INTO poll_bodies (poll_id, call, stored_at, content, size) VALUES ($p, $c, $at, $content, $size)";
            store.Parameters.AddWithValue("$p", id);
            store.Parameters.AddWithValue("$c", i);
            store.Parameters.AddWithValue("$at", now);
            store.Parameters.AddWithValue("$content", content);
            store.Parameters.AddWithValue("$size", content.Length);
            store.ExecuteNonQuery();
        }

        foreach (var sequence in poll.Sequences)
        {
            using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = "INSERT OR REPLACE INTO poll_events (sequence, poll_id) VALUES ($s, $p)";
            link.Parameters.AddWithValue("$s", sequence);
            link.Parameters.AddWithValue("$p", id);
            link.ExecuteNonQuery();
        }

        transaction.Commit();
        return poll with { Id = id };
    }

    /// <summary>Newest first: up to <paramref name="limit"/> polls of one monitor older than <paramref name="before"/>.</summary>
    public IReadOnlyList<PollRecord> Recent(string monitorId, long? before, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM polls WHERE monitor_id = $m AND id < $before ORDER BY id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$m", monitorId);
        command.Parameters.AddWithValue("$before", before ?? long.MaxValue);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var list = new List<PollRecord>();
        while (reader.Read())
            list.Add(Read(reader));
        return list;
    }

    public PollRecord? Get(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM polls WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>The poll that produced an event, while the journal still has it.</summary>
    public long? PollFor(long sequence)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT poll_id FROM poll_events WHERE sequence = $s";
        command.Parameters.AddWithValue("$s", sequence);
        return command.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>The response body of one call, or null once it's been pruned (or the call was a 304).</summary>
    public string? Body(long pollId, int call)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM poll_bodies WHERE poll_id = $p AND call = $c";
        command.Parameters.AddWithValue("$p", pollId);
        command.Parameters.AddWithValue("$c", call);
        return command.ExecuteScalar() as string;
    }

    /// <summary>Which calls of a poll still have their bodies.</summary>
    public IReadOnlySet<int> BodiesFor(long pollId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT call FROM poll_bodies WHERE poll_id = $p";
        command.Parameters.AddWithValue("$p", pollId);
        using var reader = command.ExecuteReader();
        var calls = new HashSet<int>();
        while (reader.Read())
            calls.Add(reader.GetInt32(0));
        return calls;
    }

    /// <summary>One monitor's polling grouped by UTC hour, newest first, from <paramref name="since"/>.</summary>
    public IReadOnlyList<PollHour> Hours(string monitorId, DateTimeOffset since)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT substr(started_at, 1, 13) AS hour, COUNT(*), SUM(requests), SUM(not_modified), SUM(events),
                SUM(outcome <> 'ok'), SUM(warnings), MIN(rate_remaining), MAX(rate_limit),
                SUM(measured_200), SUM(cost_200), SUM(measured_304), SUM(cost_304), MIN(started_at)
            FROM polls WHERE monitor_id = $m AND started_at >= $since
            GROUP BY hour ORDER BY hour DESC
            """;
        command.Parameters.AddWithValue("$m", monitorId);
        command.Parameters.AddWithValue("$since", Stamp(since));
        using var reader = command.ExecuteReader();
        var hours = new List<PollHour>();
        while (reader.Read())
        {
            int Int(int i) => reader.IsDBNull(i) ? 0 : reader.GetInt32(i);
            int? Nullable(int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
            hours.Add(new PollHour(
                DateTimeOffset.ParseExact(reader.GetString(0), "yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Int(1), Int(2), Int(3), Int(4), Int(5), Int(6), Nullable(7), Nullable(8), Int(9), Int(10), Int(11), Int(12)));
        }
        return hours;
    }

    public (int Polls, int Bodies) Prune()
    {
        var now = _time.GetUtcNow();
        using var connection = Open();
        using var polls = connection.CreateCommand();
        polls.CommandText = """
            DELETE FROM polls WHERE started_at < $before;
            DELETE FROM poll_events WHERE poll_id NOT IN (SELECT id FROM polls);
            """;
        polls.Parameters.AddWithValue("$before", Stamp(now - _retention.PollAge));
        var pruned = polls.ExecuteNonQuery();

        using var bodies = connection.CreateCommand();
        bodies.CommandText = """
            DELETE FROM poll_bodies WHERE stored_at < $before OR poll_id NOT IN (SELECT id FROM polls);
            DELETE FROM poll_bodies WHERE rowid IN (
                SELECT rowid FROM (
                    SELECT rowid, SUM(size) OVER (ORDER BY poll_id DESC, call DESC) AS running FROM poll_bodies
                ) WHERE running > $maxBytes);
            """;
        bodies.Parameters.AddWithValue("$before", Stamp(now - _retention.BodyAge));
        bodies.Parameters.AddWithValue("$maxBytes", _retention.MaxBodyBytes);
        return (pruned, bodies.ExecuteNonQuery());
    }

    static PollRecord Read(SqliteDataReader r)
    {
        var detail = JsonSerializer.Deserialize<Detail>(r.GetString(r.GetOrdinal("detail")), Json) ?? new Detail([], [], [], []);
        return new PollRecord
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            MonitorId = r.GetString(r.GetOrdinal("monitor_id")),
            StartedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("started_at")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Milliseconds = r.GetInt64(r.GetOrdinal("milliseconds")),
            Outcome = Enum.Parse<PollOutcome>(r.GetString(r.GetOrdinal("outcome")), ignoreCase: true),
            Error = r.IsDBNull(r.GetOrdinal("error")) ? null : r.GetString(r.GetOrdinal("error")),
            Requests = r.GetInt32(r.GetOrdinal("requests")),
            NotModified = r.GetInt32(r.GetOrdinal("not_modified")),
            Calls = detail.Calls,
            Observations = detail.Observations,
            Notes = detail.Notes,
            Sequences = detail.Sequences,
        };
    }

    static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    sealed record Detail(IReadOnlyList<RequestTrace> Calls, IReadOnlyList<Observation> Observations, IReadOnlyList<ObservationNote> Notes, IReadOnlyList<long> Sequences);
}
