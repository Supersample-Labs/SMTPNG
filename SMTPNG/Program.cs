namespace DummySmtp;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args is ["--write-manifest", var publishDirectory]) { InstallationFiles.Write(publishDirectory); InstallPayload.WriteManifest(publishDirectory); return; }
        if (args is ["--verify-install", var installDirectory])
        {
            try
            {
                InstallPayload.Validate(installDirectory);
                using var database = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"); database.Open();
                using var message = new MimeKit.MimeMessage();
                _ = new DnsClient.LookupClient();
            }
            catch { Environment.ExitCode = 1; }
            return;
        }
        if (args is ["--write-docs", var directory]) { InstallationFiles.Write(directory); return; }
        if (args is ["--service"]) { WindowsService.Run(); return; }
        if (args is ["--service-action", var action, var config, var result])
        {
            try { WindowsService.Manage(action, config); File.WriteAllText(result, "OK"); }
            catch (Exception ex) { File.WriteAllText(result, ex.Message); }
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
