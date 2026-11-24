using System.Security.Cryptography;
using System.Text.Json;

namespace DummySmtp;

internal static class InstallPayload
{
    internal const string ManifestName = "SMTPNG.install-manifest.json";
    private static bool Mutable(string relative) => relative.Split('/', '\\').Any(p => new[] { "Emails", "Logs", "Messages", "Queue", "artifacts", "bin", "obj", ".git" }.Contains(p, StringComparer.OrdinalIgnoreCase)) ||
        new[] { "settings.json", "gui-settings.json", "queue.db", "queue.db-wal", "queue.db-shm", "worker.lock" }.Contains(Path.GetFileName(relative), StringComparer.OrdinalIgnoreCase) ||
        new[] { ".eml", ".log", ".jsonl", ".pfx", ".pem", ".key" }.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase);
    private static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || Mutable(relative)) throw new IOException("Invalid install manifest path: " + relative);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Install manifest path escapes the application folder.");
        return full;
    }
    internal static void WriteManifest(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(directory, f)).Where(p => !Mutable(p) && p != ManifestName).OrderBy(p => p, StringComparer.Ordinal).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Resolve(directory, p)))));
        File.WriteAllText(Path.Combine(directory, ManifestName), JsonSerializer.Serialize(files, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static Dictionary<string, string> Validate(string directory)
    {
        var manifest = Path.Combine(directory, ManifestName);
        if (!File.Exists(manifest)) throw new IOException("Installation manifest is missing. Extract the complete new release ZIP before installing or updating the service.");
        var files = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest)) ?? throw new IOException("Invalid installation manifest.");
        foreach (var required in new[] { "SMTPNG.exe", "SMTPNG.dll", "SMTPNG.deps.json", "SMTPNG.runtimeconfig.json" })
            if (!files.ContainsKey(required)) throw new IOException("Application manifest is missing " + required);
        foreach (var pair in files)
        {
            var file = Resolve(directory, pair.Key);
            if (!File.Exists(file) || !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Required application file is missing or damaged: " + pair.Key + ". Extract the complete release ZIP again.");
        }
        return files;
    }
    internal static void Copy(string source, string destination)
    {
        var files = Validate(source); // Validate the entire payload before changing the installed application.
        if (Path.GetFullPath(source).TrimEnd('\\', '/').Equals(Path.GetFullPath(destination).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(destination);
        foreach (var pair in files)
        {
            var target = Resolve(destination, pair.Key); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var staging = target + ".install-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(Resolve(source, pair.Key), staging);
                if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(staging))).Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) throw new IOException("Installed file verification failed: " + pair.Key);
                File.Move(staging, target, true);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
        File.Copy(Path.Combine(source, ManifestName), Path.Combine(destination, ManifestName), true);
        Validate(destination);
    }
}
