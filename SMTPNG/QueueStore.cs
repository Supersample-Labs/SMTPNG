using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

namespace DummySmtp;

internal sealed class QueueCapacityException(string message) : IOException(message);
internal sealed record QueueRequest(long RecipientId = 0);
internal sealed record QueueActionResult(QueueOverview Overview, bool Paused, string? Body = null);
public sealed record QueueRow(long RecipientId, string MessageId, string Sender, string Recipient, string Subject, string State, int Attempts, DateTime CreatedUtc, DateTime NextAttemptUtc, string LastError);
public sealed record QueueOverview(long Pending, long Sending, long Failed, long Delivered, long BodyBytes, DateTime? OldestPendingUtc, QueueRow[] Rows);
internal sealed record DeliveryJob(long RecipientId, string MessageId, string Sender, string Recipient, string Raw, AppSettings Settings, int Attempts, DateTime CreatedUtc);

// SQLite FULL commits are the SMTP acceptance boundary. No network work occurs inside a transaction.
internal sealed class QueueStore : IDisposable
{
    private readonly string path;
    private readonly object gate = new();
    private readonly FileStream? ownerLock;
    private readonly Func<long> freeBytes;
    internal QueueStore(string directory, bool owner = true, Func<long>? freeBytes = null)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "queue.db");
        this.freeBytes = freeBytes ?? (() => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace);
        if (owner) ownerLock = new FileStream(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            using var db = Open();
            Execute(db, "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS messages(id TEXT PRIMARY KEY,sender TEXT NOT NULL,subject TEXT NOT NULL,body BLOB,settings TEXT NOT NULL,created INTEGER NOT NULL,notified INTEGER NOT NULL DEFAULT 0); CREATE TABLE IF NOT EXISTS recipients(id INTEGER PRIMARY KEY,message TEXT NOT NULL REFERENCES messages(id),address TEXT NOT NULL,target TEXT NOT NULL,state TEXT NOT NULL DEFAULT 'Pending',attempts INTEGER NOT NULL DEFAULT 0,next INTEGER NOT NULL,error TEXT NOT NULL DEFAULT '',notified INTEGER NOT NULL DEFAULT 0); CREATE INDEX IF NOT EXISTS due ON recipients(state,next); CREATE INDEX IF NOT EXISTS message_recipients ON recipients(message); CREATE TABLE IF NOT EXISTS controls(id INTEGER PRIMARY KEY CHECK(id=1),paused INTEGER NOT NULL); INSERT OR IGNORE INTO controls VALUES(1,0);");
            if (owner) Execute(db, "UPDATE recipients SET state='Retrying',error='Interrupted delivery; retrying after restart' WHERE state='Sending';");
        }
        catch { ownerLock?.Dispose(); throw; }
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5 }.ToString());
        db.Open(); Execute(db, "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;"); return db;
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? tx = null, params object[] args)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.Transaction = tx;
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + i, args[i]);
        return cmd;
    }
    private static void Execute(SqliteConnection db, string sql, SqliteTransaction? tx = null, params object[] args) { using var cmd = Command(db, sql, tx, args); cmd.ExecuteNonQuery(); }
    private static long Scalar(SqliteConnection db, string sql, SqliteTransaction? tx = null, params object[] args) { using var cmd = Command(db, sql, tx, args); return Convert.ToInt64(cmd.ExecuteScalar()); }
    internal static long Stamp(DateTime date) => new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)).ToUnixTimeSeconds();
    private static DateTime Date(long stamp) => DateTimeOffset.FromUnixTimeSeconds(stamp).UtcDateTime;

    internal string Enqueue(string sender, IReadOnlyList<string> recipients, string raw, AppSettings settings, DateTime? now = null)
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            var id = Insert(db, tx, sender, recipients, raw, settings, now ?? DateTime.UtcNow);
            tx.Commit(); return id;
        }
    }
    private string Insert(SqliteConnection db, SqliteTransaction tx, string sender, IReadOnlyList<string> recipients, string raw, AppSettings settings, DateTime now, bool notice = false)
    {
        var body = Encoding.Latin1.GetBytes(raw);
        if (recipients.Count == 0 || recipients.Count > 1000) throw new IOException("Queue requires 1–1000 recipients.");
        if (Scalar(db, "SELECT count(*) FROM messages WHERE body IS NOT NULL", tx) >= settings.MaxQueueMessages + (notice ? 1000L : 0) ||
            Scalar(db, "SELECT coalesce(sum(length(body)),0) FROM messages", tx) + body.Length > settings.MaxQueueMegabytes * 1024L * 1024 + (notice ? 16L * 1024 * 1024 : 0) ||
            freeBytes() < settings.MinimumFreeDiskMegabytes * 1024L * 1024 + body.Length * 3L)
            throw new QueueCapacityException("Queue full or disk space low; retry later.");
        var id = Guid.NewGuid().ToString("N");
        Execute(db, "INSERT INTO messages(id,sender,subject,body,settings,created) VALUES($0,$1,$2,$3,$4,$5)", tx, id, sender, SmtpServer.Subject(raw), body, JsonSerializer.Serialize(settings), Stamp(now));
        foreach (var recipient in recipients.Distinct(StringComparer.Ordinal))
        {
            var target = settings.DirectDelivery ? DirectDelivery.Domain(recipient) : settings.UpstreamHost.ToLowerInvariant() + ":" + settings.UpstreamPort;
            Execute(db, "INSERT INTO recipients(message,address,target,next) VALUES($0,$1,$2,$3)", tx, id, recipient, target, Stamp(now));
        }
        return id;
    }
    internal DeliveryJob? Claim(int perDestination, DateTime? now = null)
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            if (Scalar(db, "SELECT paused FROM controls WHERE id=1", tx) != 0) return null;
            using var cmd = Command(db, "SELECT r.id,m.id,m.sender,r.address,m.body,m.settings,r.attempts,m.created FROM recipients r JOIN messages m ON m.id=r.message WHERE r.state IN ('Pending','Retrying') AND r.next <= $0 AND (SELECT count(*) FROM recipients busy WHERE busy.state='Sending' AND busy.target=r.target) < $1 ORDER BY r.next,r.id LIMIT 1", tx, Stamp(now ?? DateTime.UtcNow), perDestination);
            DeliveryJob job;
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read()) return null;
                job = new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Encoding.Latin1.GetString((byte[])reader[4]), JsonSerializer.Deserialize<AppSettings>(reader.GetString(5))!, reader.GetInt32(6) + 1, Date(reader.GetInt64(7)));
            }
            Execute(db, "UPDATE recipients SET state='Sending',attempts=attempts+1 WHERE id=$0", tx, job.RecipientId);
            tx.Commit(); return job;
        }
    }
    internal static TimeSpan RetryDelay(AppSettings settings, int attempts) => TimeSpan.FromMinutes(Math.Min(1440, settings.RetryMinutes * Math.Pow(2, Math.Min(attempts - 1, 10))));
    internal void Complete(DeliveryJob job, string? error, bool permanent, DateTime? now = null)
    {
        var time = now ?? DateTime.UtcNow;
        var state = error == null ? "Delivered" : permanent || time >= job.CreatedUtc.AddDays(job.Settings.RetryDays) ? "Failed" : "Retrying";
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            Execute(db, "UPDATE recipients SET state=$0,error=$1,next=$2 WHERE id=$3 AND state='Sending'", tx, state, error ?? "", Stamp(time + RetryDelay(job.Settings, job.Attempts) < job.CreatedUtc.AddDays(job.Settings.RetryDays) ? time + RetryDelay(job.Settings, job.Attempts) : job.CreatedUtc.AddDays(job.Settings.RetryDays)), job.RecipientId);
            // Failed bodies remain until explicitly handled; delivered bodies are released only after every recipient succeeded.
            if (Scalar(db, "SELECT count(*) FROM recipients WHERE message=$0 AND state!='Delivered'", tx, job.MessageId) == 0)
                Execute(db, "UPDATE messages SET body=NULL,settings='{}' WHERE id=$0", tx, job.MessageId);
            tx.Commit();
        }
    }
    internal void Retry(long recipientId)
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            Execute(db, "UPDATE recipients SET state='Pending',next=$0,attempts=0,error='' WHERE id=$1 AND state IN ('Retrying','Failed')", tx, Stamp(DateTime.UtcNow), recipientId);
            // Reset age only for an explicitly retried message. Never reset other recipients' delivery states.
            Execute(db, "UPDATE messages SET created=$0 WHERE id=(SELECT message FROM recipients WHERE id=$1 AND state='Pending')", tx, Stamp(DateTime.UtcNow), recipientId);
            tx.Commit();
        }
    }
    internal void SetPaused(bool paused) { lock (gate) { using var db = Open(); Execute(db, "UPDATE controls SET paused=$0 WHERE id=1", null, paused ? 1 : 0); } }
    internal bool Paused { get { lock (gate) { using var db = Open(); return Scalar(db, "SELECT paused FROM controls WHERE id=1") != 0; } } }
    internal string ReadBody(long recipientId)
    {
        lock (gate) { using var db = Open(); using var cmd = Command(db, "SELECT body FROM messages WHERE id=(SELECT message FROM recipients WHERE id=$0)", null, recipientId); return cmd.ExecuteScalar() is byte[] body ? Encoding.Latin1.GetString(body) : throw new IOException("Message body released after successful delivery."); }
    }
    internal QueueOverview Overview()
    {
        lock (gate)
        {
            using var db = Open();
            long Count(string state) => Scalar(db, "SELECT count(*) FROM recipients WHERE state=$0", null, state);
            var rows = new List<QueueRow>();
            using var cmd = Command(db, "SELECT r.id,m.id,m.sender,r.address,m.subject,r.state,r.attempts,m.created,r.next,r.error FROM recipients r JOIN messages m ON m.id=r.message ORDER BY CASE r.state WHEN 'Failed' THEN 0 WHEN 'Sending' THEN 1 WHEN 'Retrying' THEN 2 WHEN 'Pending' THEN 3 ELSE 4 END,m.created DESC LIMIT 500");
            using (var reader = cmd.ExecuteReader()) while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6), Date(reader.GetInt64(7)), Date(reader.GetInt64(8)), reader.GetString(9)));
            using var oldest = Command(db, "SELECT min(m.created) FROM messages m JOIN recipients r ON r.message=m.id WHERE r.state IN ('Pending','Retrying','Sending')");
            var stamp = oldest.ExecuteScalar();
            return new(Count("Pending") + Count("Retrying"), Count("Sending"), Count("Failed"), Count("Delivered"), Scalar(db, "SELECT coalesce(sum(length(body)),0) FROM messages"), stamp is long n ? Date(n) : null, rows.ToArray());
        }
    }
    internal void Maintain()
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            Execute(db, "DELETE FROM recipients WHERE message IN (SELECT id FROM messages WHERE body IS NULL AND created<$0)", tx, Stamp(DateTime.UtcNow.AddDays(-30)));
            Execute(db, "DELETE FROM messages WHERE body IS NULL AND created<$0", tx, Stamp(DateTime.UtcNow.AddDays(-30)));
            tx.Commit(); Execute(db, "PRAGMA wal_checkpoint(PASSIVE)");
        }
    }
    internal bool CreateFailureNotice()
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            using var cmd = Command(db, "SELECT r.id,r.address,r.error,m.id,m.sender,m.settings,m.body FROM recipients r JOIN messages m ON m.id=r.message WHERE r.state='Failed' AND r.notified=0 LIMIT 1", tx);
            long id; string recipient, error, messageId, sender, raw; AppSettings settings;
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return false;
                id = r.GetInt64(0); recipient = r.GetString(1); error = r.GetString(2); messageId = r.GetString(3); sender = r.GetString(4); settings = JsonSerializer.Deserialize<AppSettings>(r.GetString(5))!; raw = Encoding.Latin1.GetString((byte[])r[6]);
            }
            string? notice;
            try { notice = DeliveryNotice.Create(settings, sender, recipient, error, messageId, raw); }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                notice = null;
                Execute(db, "UPDATE recipients SET error=error || $0 WHERE id=$1", tx, "; Failure notice suppressed: " + ex.Message, id);
            }
            if (notice != null) Insert(db, tx, "", new[] { sender }, notice, settings, DateTime.UtcNow, notice: true);
            Execute(db, "UPDATE recipients SET notified=1 WHERE id=$0", tx, id); tx.Commit(); return true;
        }
    }
    public void Dispose() => ownerLock?.Dispose();
}
