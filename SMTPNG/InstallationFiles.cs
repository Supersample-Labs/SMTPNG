using System.Text;

namespace DummySmtp;

public static class InstallationFiles
{
    public static void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "README.md", "Set-SMTPNGCertificateName.ps1" })
        {
            using var source = typeof(InstallationFiles).Assembly.GetManifestResourceStream("SMTPNG." + name)
                ?? throw new IOException("Missing embedded installation guide: " + name);
            using var reader = new StreamReader(source);
            File.WriteAllText(Path.Combine(directory, name), reader.ReadToEnd(), new UTF8Encoding(false));
        }
    }
}
