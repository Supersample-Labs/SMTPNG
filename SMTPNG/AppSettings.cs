using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DummySmtp;

public sealed record AppSettings
{
    public string BindAddress { get; init; } = "0.0.0.0";
    public int Port { get; init; } = 25;
    public int DeliveryWorkers { get; init; } = 4;
    public int ConnectionsPerDestination { get; init; } = 2;
    public int MaxIncomingConnections { get; init; } = 32;
    public int MaxQueueMessages { get; init; } = 10000;
    public int MaxQueueMegabytes { get; init; } = 1024;
    public int MinimumFreeDiskMegabytes { get; init; } = 256;
    public int RetryMinutes { get; init; } = 30;
    public int RetryDays { get; init; } = 5;
    public bool NotifyDeliveryFailures { get; init; } = true;
    public string DmarcDomain { get; init; } = "";
    public string DmarcPolicy { get; init; } = "none";
    public string DmarcReportAddress { get; init; } = "";
    public string DmarcDkimAlignment { get; init; } = "r";
    public string DmarcSpfAlignment { get; init; } = "r";
    public bool DirectDelivery { get; init; }
    public string HeloHostname { get; init; } = "";
    public bool DkimEnabled { get; init; }
    public string DkimDomain { get; init; } = "";
    public string DkimSelector { get; init; } = "smtp";
    public string ProtectedDkimKey { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RelayEnabled => Forwarding || DirectDelivery;
    public bool Forwarding { get; init; }
    public string UpstreamHost { get; init; } = "";
    public int UpstreamPort { get; init; } = 587;
    public bool StartTls { get; init; } = true;
    public string Username { get; init; } = "";
    public string ProtectedPassword { get; init; } = "";
    public string AllowedClients { get; init; } = "127.0.0.1;::1";
    public bool SaveEml { get; init; }
    public static string EmailDirectory => Path.Combine(AppContext.BaseDirectory, "Emails");
    public bool IncomingStartTls { get; init; }
    public bool RequireIncomingTls { get; init; }
    public string CertificateStore { get; init; } = "My";
    public string CertificateFriendlyName { get; init; } = "SMTPNG";
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SMTPNG");
    public static string LogDirectory => Path.Combine(AppContext.BaseDirectory, "Logs");
    public static string ServiceLogDirectory => Path.Combine(InstallDirectory, "Logs");
    public static string ServiceQueueDirectory => Path.Combine(ServiceRoot, "Messages", "Queue");
    public static string GuiQueueDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DummySmtp", "Messages", "Queue");
    public static string ServiceRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DummySmtp");
    public static string ServiceConfig => Path.Combine(InstallDirectory, "settings.json");
    public static string LegacyServiceConfig => Path.Combine(ServiceRoot, "settings.json");
    public static string UserConfig => Path.Combine(AppContext.BaseDirectory, "gui-settings.json");
    public static string LegacyUserConfig => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DummySmtp", "settings.json");
    public static AppSettings Load(string path)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new(); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }
    public static AppSettings LoadGui()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(UserConfig)) ?? new(); }
        catch (FileNotFoundException) { return Load(LegacyUserConfig); }
        catch (DirectoryNotFoundException) { return Load(LegacyUserConfig); }
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
    public void Validate()
    {
        if (!System.Net.IPAddress.TryParse(BindAddress, out _)) throw new ArgumentException("Enter a valid bind IP.");
        if (Port is < 1 or > 65535 || UpstreamPort is < 1 or > 65535) throw new ArgumentException("Invalid port.");
        if (DeliveryWorkers is < 1 or > 32 || ConnectionsPerDestination is < 1 or > 16 || MaxIncomingConnections is < 1 or > 256 || MaxQueueMessages is < 1 or > 1000000 || MaxQueueMegabytes is < 1 or > 1048576 || MinimumFreeDiskMegabytes is < 0 or > 1048576 || RetryMinutes is < 1 or > 1440 || RetryDays is < 1 or > 30)
            throw new ArgumentException("Invalid queue limits, worker count or retry settings.");
        var allowed = RelayAccess.Parse(AllowedClients);
        if (RequireIncomingTls && !IncomingStartTls) throw new ArgumentException("Enable incoming STARTTLS before requiring TLS.");
        if (IncomingStartTls && (string.IsNullOrWhiteSpace(CertificateFriendlyName) || CertificateStore is not ("My" or "WebHosting")))
            throw new ArgumentException("Select Local Computer My or WebHosting store and enter a certificate friendly name.");
        if (DkimEnabled) DkimSigning.Validate(this);
        if (DirectDelivery && Forwarding) throw new ArgumentException("Choose either upstream forwarding or direct delivery.");
        if ((DirectDelivery || HeloHostname.Length > 0) && (Uri.CheckHostName(HeloHostname) != UriHostNameType.Dns || !HeloHostname.Contains('.') || HeloHostname.Any(char.IsWhiteSpace)))
            throw new ArgumentException("Direct delivery requires your public SMTP hostname (for EHLO).");
        if (RelayEnabled && allowed.Length == 0) throw new ArgumentException("Add an allowed relay IP or subnet.");
        if (!Forwarding) return;
        if (string.IsNullOrWhiteSpace(UpstreamHost) || UpstreamHost.Any(char.IsWhiteSpace)) throw new ArgumentException("Enter an upstream SMTP hostname.");
        if (allowed.Length == 0) throw new ArgumentException("Add at least one allowed relay IP address or CIDR subnet on the Relay access tab.");
        if (Username.Length > 0 && !StartTls) throw new ArgumentException("Enable STARTTLS when using upstream credentials.");
    }
}

// Windows DPAPI machine scope permits the installed service to decrypt GUI-saved credentials.
internal static class Secret
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static string Protect(string value) => value.Length == 0 ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value) => value.Length == 0 ? "" : Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, "DummySmtp", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 5, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}
