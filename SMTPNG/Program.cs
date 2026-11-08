namespace DummySmtp;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
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
