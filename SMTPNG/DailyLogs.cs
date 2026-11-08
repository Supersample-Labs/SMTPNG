using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DummySmtp;

public sealed class DailyLogs
{
    public const int RetentionDays = 30;
    private readonly string directory;
    private readonly Func<DateTime> clock;
    private readonly object gate = new();
    private DateTime? maintained;
    private static readonly Regex NamePattern = new(@"^(activity|messages|service-error)-(\d{4}-\d{2}-\d{2})(?:\.legacy-[a-f0-9]{32})?\.(log|jsonl)$", RegexOptions.CultureInvariant);
    public DailyLogs(string directory, Func<DateTime>? clock = null) { this.directory = Path.GetFullPath(directory); this.clock = clock ?? (() => DateTime.Now); }
    private string PathFor(string category, DateTime date) => Path.Combine(directory, $"{category}-{date:yyyy-MM-dd}.{(category == "messages" ? "jsonl" : "log")}");
    public void Activity(string text) { lock (gate) { var now = clock(); Directory.CreateDirectory(directory); File.AppendAllText(PathFor("activity", now), $"[{now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}"); } }
    public void Error(string text) { lock (gate) { var now = clock(); Directory.CreateDirectory(directory); File.AppendAllText(PathFor("service-error", now), $"[{now:O}] {text}{Environment.NewLine}"); } }
    public void Message(CapturedMail mail)
    {
        lock (gate) { Directory.CreateDirectory(directory); File.AppendAllText(PathFor("messages", clock()), JsonSerializer.Serialize(mail with { Raw = "" }) + Environment.NewLine); }
    }
    private static bool TryDate(string path, out DateTime date)
    {
        date = default;
        var match = NamePattern.Match(Path.GetFileName(path));
        return match.Success && (match.Groups[1].Value == "messages" ? match.Groups[3].Value == "jsonl" : match.Groups[3].Value == "log")
            && DateTime.TryParseExact(match.Groups[2].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
    public static string[] Files(string directory, string category, DateTime? today = null)
    {
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        var cutoff = (today ?? DateTime.Now).Date.AddDays(-(RetentionDays - 1));
        return Directory.EnumerateFiles(directory, category + "-*").Where(p => TryDate(p, out var date) && date >= cutoff)
            .OrderByDescending(p => { TryDate(p, out var date); return date; })
            .ThenBy(p => Path.GetFileName(p).Contains(".legacy-", StringComparison.Ordinal) ? 1 : 0).ThenByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray();
    }
    public void ImportLegacy(string path, string category)
    {
        lock (gate)
        {
            if (!File.Exists(path)) return;
            Directory.CreateDirectory(directory);
            var date = File.GetLastWriteTime(path).Date;
            var suffix = category == "messages" ? "jsonl" : "log";
            File.Move(path, Path.Combine(directory, $"{category}-{date:yyyy-MM-dd}.legacy-{Guid.NewGuid():N}.{suffix}"));
        }
    }
    public string[] Maintain()
    {
        lock (gate)
        {
            var today = clock().Date;
            if (maintained == today) return Array.Empty<string>();
            var errors = new List<string>();
            Directory.CreateDirectory(directory);
            foreach (var (name, category) in new[] { ("activity.log", "activity"), ("messages.jsonl", "messages"), ("service-error.log", "service-error") })
            {
                try { ImportLegacy(Path.Combine(directory, name), category); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"Log migration failed for {name}: {ex.Message}"); }
            }
            var cutoff = today.AddDays(-(RetentionDays - 1));
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                // Delete only this application's precisely named dated logs, never EMLs or arbitrary files.
                if (!TryDate(file, out var date) || date >= cutoff) continue;
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"Log retention failed for {Path.GetFileName(file)}: {ex.Message}"); }
            }
            if (errors.Count == 0) maintained = today;
            return errors.ToArray();
        }
    }
    public static string Tail(string path, int bytes = 100000)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > bytes) stream.Seek(-bytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8); return reader.ReadToEnd();
    }
}
