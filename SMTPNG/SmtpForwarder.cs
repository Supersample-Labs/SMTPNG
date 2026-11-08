using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace DummySmtp;

internal static class SmtpForwarder
{
    public static async Task SendAsync(AppSettings settings, string sender, IReadOnlyList<string> recipients, string raw, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        using var client = new TcpClient();
        await client.ConnectAsync(settings.UpstreamHost, settings.UpstreamPort, token);
        // Reject loops to this listener, including hostname aliases and all-interface binds.
        var endpoint = (IPEndPoint)client.Client.RemoteEndPoint!;
        var local = (IPEndPoint)client.Client.LocalEndPoint!;
        if (endpoint.Port == settings.Port && (IPAddress.IsLoopback(endpoint.Address) || endpoint.Address.Equals(local.Address) || endpoint.Address.Equals(IPAddress.Parse(settings.BindAddress))))
            throw new IOException("Upstream points back to this listener.");
        Stream stream = client.GetStream();
        StreamReader? reader = null; StreamWriter? writer = null;
        void CreateReaders()
        {
            reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
            writer = new StreamWriter(stream, Encoding.Latin1, 4096, true) { NewLine = "\r\n", AutoFlush = true };
        }
        async Task<string> Response(int expected)
        {
            var response = new StringBuilder();
            for (int i = 0; i < 100; i++)
            {
                var line = await reader!.ReadLineAsync(token) ?? throw new IOException("Upstream disconnected.");
                if (line.Length < 3 || !int.TryParse(line[..3], out var code) || code != expected)
                    throw new IOException($"Upstream returned {line}");
                response.AppendLine(line);
                if (line.Length == 3 || line[3] != '-') return response.ToString();
            }
            throw new IOException("Invalid upstream SMTP response.");
        }
        async Task<string> Command(string text, int expected)
        { await writer!.WriteLineAsync(text.AsMemory(), token); return await Response(expected); }
        try
        {
            CreateReaders();
            await Response(220);
            var hello = await Command("EHLO dummy-smtp.local", 250);
            if (settings.StartTls)
            {
                if (!hello.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase)) throw new IOException("Upstream does not offer STARTTLS.");
                await Command("STARTTLS", 220);
                reader!.Dispose(); writer!.Dispose();
                var tls = new SslStream(stream, false);
                stream = tls;
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = settings.UpstreamHost }, token);
                CreateReaders();
                await Command("EHLO dummy-smtp.local", 250);
            }
            if (settings.Username.Length > 0)
            {
                await Command("AUTH LOGIN", 334);
                await Command(Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Username)), 334);
                await Command(Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret.Unprotect(settings.ProtectedPassword))), 235);
            }
            await Command($"MAIL FROM:<{sender}>", 250);
            foreach (var recipient in recipients) await Command($"RCPT TO:<{recipient}>", 250);
            await Command("DATA", 354);
            using var source = new StringReader(raw);
            while (source.ReadLine() is { } line)
                await writer!.WriteLineAsync((line.StartsWith('.') ? "." + line : line).AsMemory(), token);
            await Command(".", 250);
            // The final DATA 250 is the delivery boundary. A dropped QUIT cannot undo it.
        }
        finally { reader?.Dispose(); writer?.Dispose(); await stream.DisposeAsync(); }
    }
}
