using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace DummySmtp;

internal sealed class SmtpDeliveryException(string message, bool permanent) : IOException(message)
{
    internal bool Permanent { get; } = permanent;
}

internal static class SmtpForwarder
{
    public static async Task SendAsync(AppSettings settings, string sender, IReadOnlyList<string> recipients, string raw, CancellationToken cancellation)
    {
        if (settings.DkimEnabled) raw = DkimSigning.Sign(settings, raw);
        if (settings.DirectDelivery) await DirectDelivery.SendAsync(settings, sender, recipients, raw, cancellation);
        else await SendToHostAsync(settings, settings.UpstreamHost, settings.UpstreamPort, sender, recipients, raw, cancellation);
    }
    internal static async Task SendToHostAsync(AppSettings settings, string host, int port, string sender, IReadOnlyList<string> recipients, string raw, CancellationToken cancellation, bool direct = false)
    {
        await using var session = new SmtpSession();
        await session.SendAsync(settings, host, port, sender, recipients, raw, cancellation, direct);
    }
}

// Each delivery worker owns its upstream session; sessions are never shared concurrently.
internal sealed class SmtpSession : IAsyncDisposable
{
    private TcpClient? client;
    private Stream? stream;
    private SmtpLineReader? reader;
    private StreamWriter? writer;
    private AppSettings? connectedSettings;
    private string connectedHost = "";
    private int connectedPort, transactions;
    private DateTime lastUsed;
    private void CreateReaders()
    {
        reader = new SmtpLineReader(stream!);
        writer = new StreamWriter(stream!, Encoding.Latin1, 4096, true) { NewLine = "\r\n", AutoFlush = true };
    }
    private async Task<string> Response(int expected, CancellationToken token, bool permanentAllowed = false, bool recipient = false)
    {
        var response = new StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            var line = await reader!.ReadLineAsync(token) ?? throw new IOException("SMTP server disconnected.");
            if (line.Length < 3 || !int.TryParse(line[..3], out var code)) throw new IOException("Malformed SMTP response.");
            if (code != expected && !(recipient && code is 251 or 252))
                throw new SmtpDeliveryException($"SMTP returned {line}", permanentAllowed && code is >= 500 and <= 599);
            response.AppendLine(line);
            if (line.Length == 3 || line[3] != '-') return response.ToString();
        }
        throw new IOException("Invalid SMTP response.");
    }
    private async Task<string> Command(string text, int expected, CancellationToken token, bool permanentAllowed = false, bool recipient = false)
    { await writer!.WriteLineAsync(text.AsMemory(), token); return await Response(expected, token, permanentAllowed, recipient); }

    internal async Task SendAsync(AppSettings settings, string host, int port, string sender, IReadOnlyList<string> recipients, string raw, CancellationToken cancellation, bool direct = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(60)); var token = timeout.Token;
        try
        {
            bool reusable = client != null && !direct && connectedSettings == settings && connectedHost == host && connectedPort == port && transactions < 100 && DateTime.UtcNow - lastUsed <= TimeSpan.FromSeconds(30);
            if (reusable)
            {
                // Probe before MAIL: reconnecting here cannot duplicate a message transaction.
                try { await Command("NOOP", 250, token); }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or SocketException) { await DisposeAsync(); }
            }
            if (client == null || direct || connectedSettings != settings || connectedHost != host || connectedPort != port || transactions >= 100 || DateTime.UtcNow - lastUsed > TimeSpan.FromSeconds(30))
            {
                await DisposeAsync();
                client = new TcpClient(); await client.ConnectAsync(host, port, token);
                var endpoint = (IPEndPoint)client.Client.RemoteEndPoint!;
                var local = (IPEndPoint)client.Client.LocalEndPoint!;
                if (endpoint.Port == settings.Port && (IPAddress.IsLoopback(endpoint.Address) || endpoint.Address.Equals(local.Address) || endpoint.Address.Equals(IPAddress.Parse(settings.BindAddress))))
                    throw new IOException("Destination points back to this listener.");
                stream = client.GetStream(); CreateReaders(); await Response(220, token);
                var ehlo = "EHLO " + (settings.HeloHostname.Length > 0 ? settings.HeloHostname : "dummy-smtp.local");
                var hello = await Command(ehlo, 250, token);
                if (direct ? hello.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase) : settings.StartTls)
                {
                    if (!hello.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase)) throw new IOException("SMTP server does not offer required STARTTLS.");
                    await Command("STARTTLS", 220, token);
                    reader!.Dispose(); writer!.Dispose();
                    var tls = new SslStream(stream, false); stream = tls;
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, token);
                    CreateReaders(); await Command(ehlo, 250, token);
                }
                if (!direct && settings.Username.Length > 0)
                {
                    await Command("AUTH LOGIN", 334, token);
                    await Command(Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Username)), 334, token);
                    await Command(Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret.Unprotect(settings.ProtectedPassword))), 235, token);
                }
                connectedSettings = settings; connectedHost = host; connectedPort = port; transactions = 0;
            }
            await Command($"MAIL FROM:<{sender}>", 250, token, permanentAllowed: true);
            foreach (var recipient in recipients) await Command($"RCPT TO:<{recipient}>", 250, token, permanentAllowed: true, recipient: true);
            await Command("DATA", 354, token, permanentAllowed: true);
            using var source = new StringReader(raw);
            while (source.ReadLine() is { } line) await writer!.WriteLineAsync((line.StartsWith('.') ? "." + line : line).AsMemory(), token);
            await Command(".", 250, token, permanentAllowed: true);
            // Only the final DATA 250 marks acceptance. A lost reply remains an ambiguous retry.
            transactions++; lastUsed = DateTime.UtcNow;
            if (direct) await DisposeAsync();
        }
        catch { await DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync()
    {
        reader?.Dispose(); writer?.Dispose(); stream?.Dispose(); client?.Dispose();
        reader = null; writer = null; stream = null; client = null; connectedSettings = null;
        return ValueTask.CompletedTask;
    }
}
