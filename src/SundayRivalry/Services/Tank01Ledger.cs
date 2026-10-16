using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SundayRivalry.Services;

public sealed record LiveFeedPaths(string Directory);
public sealed record Tank01Usage(int Used, int Limit, int Today, DateTimeOffset? BlockedUntil, string Reason);

// A separate durable ledger reserves calls BEFORE sending them. SQLite's immediate
// transaction serializes reservations even across two app processes sharing data.
public sealed class Tank01Ledger(LiveFeedPaths paths, TimeProvider clock)
{
    public const int MonthlyLimit = 900;
    public const int DailyLimit = 100;
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);
    SqliteConnection Open()
    {
        Directory.CreateDirectory(paths.Directory);
        var db = new SqliteConnection($"Data Source={Path.Combine(paths.Directory, "tank01-usage.db")};Default Timeout=10"); db.Open();
        using var schema = db.CreateCommand();
        schema.CommandText = "CREATE TABLE IF NOT EXISTS Requests(Id TEXT PRIMARY KEY, AtUtc TEXT NOT NULL, Resource TEXT NOT NULL); CREATE INDEX IF NOT EXISTS IX_Requests_Time ON Requests(AtUtc); CREATE TABLE IF NOT EXISTS Guard(Id INTEGER PRIMARY KEY CHECK(Id=1), UntilUtc TEXT NOT NULL, Reason TEXT NOT NULL);";
        schema.ExecuteNonQuery(); return db;
    }
    static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static long Count(SqliteConnection db, SqliteTransaction? tx, DateTimeOffset since, string? resource = null)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM Requests WHERE AtUtc >= $since" + (resource is null ? "" : " AND Resource=$resource");
        cmd.Parameters.AddWithValue("$since", Stamp(since)); if (resource is not null) cmd.Parameters.AddWithValue("$resource", resource);
        return (long)cmd.ExecuteScalar()!;
    }
    static (DateTimeOffset? Until, string Reason) Guard(SqliteConnection db, SqliteTransaction? tx)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT UntilUtc, Reason FROM Guard WHERE Id=1";
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetString(1)) : (null, "");
    }
    public Tank01Usage Usage()
    {
        using var db = Open(); var now = clock.GetUtcNow(); var guard = Guard(db, null);
        return new((int)Count(db, null, now.AddDays(-31)), MonthlyLimit,
            (int)Count(db, null, new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero)), guard.Until, guard.Reason);
    }
    public bool Reserve(string resource, out string reason)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var now = clock.GetUtcNow();
        var guard = Guard(db, tx);
        if (guard.Until > now) { reason = guard.Reason; return false; }
        if (Count(db, tx, now.AddDays(-31)) >= MonthlyLimit) { reason = "Monthly safety cap reached. Calls resume only as older requests leave the rolling 31-day window."; return false; }
        if (Count(db, tx, new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero)) >= DailyLimit) { reason = "Daily safety cap reached. Saved scores remain available."; return false; }
        if (Count(db, tx, now - PollInterval, resource) > 0) { reason = "Waiting for the next 30-minute update."; return false; }
        using var insert = db.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = "INSERT INTO Requests(Id,AtUtc,Resource) VALUES($id,$at,$resource)";
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); insert.Parameters.AddWithValue("$at", Stamp(now)); insert.Parameters.AddWithValue("$resource", resource);
        insert.ExecuteNonQuery(); tx.Commit(); reason = ""; return true;
    }
    public void Block(DateTimeOffset until, string reason)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var current = Guard(db, tx); if (current.Until > until) return;
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO Guard(Id,UntilUtc,Reason) VALUES(1,$until,$reason) ON CONFLICT(Id) DO UPDATE SET UntilUtc=$until,Reason=$reason";
        cmd.Parameters.AddWithValue("$until", Stamp(until)); cmd.Parameters.AddWithValue("$reason", reason); cmd.ExecuteNonQuery(); tx.Commit();
    }
}
