using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DummySmtp;

internal static class WindowsService
{
    public const string Name = "DummySmtpCapture";
    private static readonly ManualResetEventSlim stopped = new(false);
    private static ServiceMain? main;
    private static Handler? handler;
    private static IntPtr statusHandle;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMain(int count, IntPtr args);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Handler(int control);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Entry { public string? Name; public ServiceMain? Main; }
    [StructLayout(LayoutKind.Sequential)] private struct Status
    { public int Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceCtrlDispatcher([In] Entry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandler(string name, Handler callback);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceStatus(IntPtr handle, ref Status status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr service, out Status status);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);

    private static void Report(int state, int error = 0)
    {
        var status = new Status { Type = 0x10, State = state, Accepted = state == 4 ? 5 : 0, Win32Exit = error, WaitHint = state is 2 or 3 ? 15000 : 0, Checkpoint = state is 2 or 3 ? 1 : 0 };
        SetServiceStatus(statusHandle, ref status);
    }
    public static void Run()
    {
        main = (_, _) =>
        {
            handler = control => { if (control is 1 or 5) { Report(3); stopped.Set(); } };
            statusHandle = RegisterServiceCtrlHandler(Name, handler);
            if (statusHandle == IntPtr.Zero) return;
            Report(2);
            SmtpServer? server = null;
            int error = 0;
            try
            {
                var startupLogs = new DailyLogs(Path.Combine(AppSettings.ServiceRoot, "Messages"));
                startupLogs.ImportLegacy(Path.Combine(AppSettings.ServiceRoot, "service-error.log"), "service-error");
                startupLogs.Maintain();
                var config = File.Exists(AppSettings.ServiceConfig) ? AppSettings.ServiceConfig : AppSettings.LegacyServiceConfig;
                if (!File.Exists(config)) throw new IOException("Service settings file is missing.");
                var settings = AppSettings.Load(config);
                server = new SmtpServer(settings, Path.Combine(AppSettings.ServiceRoot, "Messages"));
                server.Start(IPAddress.Parse(settings.BindAddress), settings.Port);
                Report(4);
                stopped.Wait();
            }
            catch (Exception ex)
            {
                error = 1064;
                try
                {
                    var errorLogs = new DailyLogs(Path.Combine(AppSettings.ServiceRoot, "Messages"));
                    errorLogs.ImportLegacy(Path.Combine(AppSettings.ServiceRoot, "service-error.log"), "service-error");
                    errorLogs.Maintain(); errorLogs.Error(ex.ToString());
                }
                catch { }
            }
            finally { server?.Stop(); Report(1, error); }
        };
        if (!StartServiceCtrlDispatcher(new[] { new Entry { Name = Name, Main = main }, new Entry() }))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static string State()
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) return "Unavailable";
        try
        {
            var service = OpenService(manager, Name, 4);
            if (service == IntPtr.Zero) return Marshal.GetLastWin32Error() == 1060 ? "Not installed" : "Unavailable";
            try { return QueryServiceStatus(service, out var status) ? status.State switch { 1 => "Stopped", 2 => "Starting", 3 => "Stopping", 4 => "Running", _ => "Pending" } : "Unavailable"; }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static void Sc(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd(); var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(output + errors);
    }

    public static void Manage(string action, string configPath)
    {
        if (action.StartsWith("queue-", StringComparison.Ordinal))
        {
            var directory = Path.Combine(AppSettings.ServiceRoot, "Messages", "Queue");
            if (!File.Exists(Path.Combine(directory, "queue.db"))) throw new IOException("The service queue has not been created yet.");
            var request = System.Text.Json.JsonSerializer.Deserialize<QueueRequest>(File.ReadAllText(configPath))!;
            using var queue = new QueueStore(directory, owner: false);
            if (action == "queue-retry") queue.Retry(request.RecipientId);
            else if (action == "queue-pause") queue.SetPaused(true);
            else if (action == "queue-resume") queue.SetPaused(false);
            else if (action is not ("queue-list" or "queue-read")) throw new ArgumentException("Unknown queue operation.");
            File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(new QueueActionResult(queue.Overview(), queue.Paused, action == "queue-read" ? queue.ReadBody(request.RecipientId) : null)));
            return;
        }
        if (action == "load")
        {
            var path = File.Exists(AppSettings.ServiceConfig) ? AppSettings.ServiceConfig : AppSettings.LegacyServiceConfig;
            if (!File.Exists(path)) throw new IOException("No service settings found.");
            AppSettings.Load(path).Save(configPath); return;
        }
        if (action == "load-gui") { AppSettings.Load(AppSettings.UserConfig).Save(configPath); return; }
        if (action == "save-gui")
        {
            var settings = AppSettings.Load(configPath); settings.Validate();
            SaveProtectedConfig(settings, AppSettings.UserConfig); return;
        }
        if (action is "install" or "configure")
        {
            var settings = AppSettings.Load(configPath); settings.Validate();
            if (State() is not ("Stopped" or "Not installed")) throw new InvalidOperationException("Stop the service before installing or applying settings.");
            if (action == "install")
            {
                if (State() != "Not installed") throw new InvalidOperationException("The service is already installed.");
                var destination = AppSettings.InstallDirectory;
                Directory.CreateDirectory(destination);
                if (!Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(destination, StringComparison.OrdinalIgnoreCase))
                {
                    var names = new[] { "SMTPNG.exe", "SMTPNG.dll", "SMTPNG.deps.json", "SMTPNG.runtimeconfig.json" };
                    foreach (var name in names)
                        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, name))) throw new IOException($"Required application file is missing: {name}. Extract the complete ZIP first.");
                    CopyApplicationFiles(AppContext.BaseDirectory, destination);
                }
                SaveServiceSettings(settings);
                SaveProtectedConfig(settings, Path.Combine(destination, "gui-settings.json"));
                CreateEmailDirectory(destination);
                InstallationFiles.Write(destination);
                Sc("create", Name, "binPath=", $"\"{Path.Combine(destination, "SMTPNG.exe")}\" --service", "start=", "auto", "DisplayName=", "SMTPNG");
                Sc("description", Name, "Captures SMTP mail and reliably queues upstream or direct SMTP delivery.");
            }
            else
            {
                if (State() == "Not installed") throw new InvalidOperationException("Install the service before applying service settings.");
                SaveServiceSettings(settings); CreateEmailDirectory(AppSettings.InstallDirectory); InstallationFiles.Write(AppSettings.InstallDirectory);
            }
        }
        else if (action == "start") Sc("start", Name);
        else if (action == "stop") Sc("stop", Name);
        else if (action == "uninstall")
        {
            if (State() != "Stopped") throw new InvalidOperationException("Stop the service before removing it.");
            Sc("delete", Name);
        }
        else throw new ArgumentException("Unknown service action.");
    }

    internal static void CopyApplicationFiles(string source, string destination)
    {
        // Copy dependencies and self-contained runtime files, never mutable user data or settings.
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "Emails" or "Queue" or "artifacts" or "bin" or "obj" or ".git")) continue;
            var name = Path.GetFileName(file);
            if (!(name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name is "SMTPNG.deps.json" or "SMTPNG.runtimeconfig.json")) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
        }
    }

    private static void SaveServiceSettings(AppSettings settings)
    {
        Directory.CreateDirectory(AppSettings.ServiceRoot);
        // Configuration controls a LocalSystem service; ordinary users must not be able to edit it.
        var directoryAcl = new DirectorySecurity();
        directoryAcl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        directoryAcl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            directoryAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directoryAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(AppSettings.ServiceRoot).SetAccessControl(directoryAcl);
        var queueDirectory = Directory.CreateDirectory(Path.Combine(AppSettings.ServiceRoot, "Messages", "Queue"));
        var queueAcl = new DirectorySecurity(); queueAcl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            queueAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        queueDirectory.SetAccessControl(queueAcl);
        SaveProtectedConfig(settings, AppSettings.ServiceConfig);
    }

    private static void SaveProtectedConfig(AppSettings settings, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) using (File.Create(path)) { }
        var fileAcl = new FileSecurity(); fileAcl.SetAccessRuleProtection(true, false);
        fileAcl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            fileAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(fileAcl);
        settings.Save(path);
    }

    private static void CreateEmailDirectory(string installDirectory)
    {
        var directory = Directory.CreateDirectory(Path.Combine(installDirectory, "Emails"));
        var acl = directory.GetAccessControl();
        // Only the Emails subfolder is writable by the installing user, never executable/config folders.
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Cannot identify installing user.");
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }
}
