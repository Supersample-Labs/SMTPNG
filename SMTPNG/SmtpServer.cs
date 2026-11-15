using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DummySmtp;

public sealed record CapturedMail(DateTime Received, string From, string To, string Subject, string Remote, string Raw, string File, string Delivery = "Captured");

public sealed class SmtpServer
{
    private TcpListener? listener;
    private DeliveryQueue? deliveryQueue;
    public string QueueDirectory => Path.Combine(StoragePath, "Queue");
    internal QueueStore? Queue => deliveryQueue?.Store;
    private CancellationTokenSource? cancellation;
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
    public event Action<CapturedMail>? Received;
    public event Action<string>? Log;
    private readonly AppSettings settings;
    private readonly Func<X509Certificate2> certificateProvider;
    private RelayNetwork[] relayNetworks = Array.Empty<RelayNetwork>();
    private readonly DailyLogs logs;
    private System.Threading.Timer? retentionTimer;
    private readonly string emailDirectory;
    public string StoragePath { get; }
    public SmtpServer(AppSettings? settings = null, string? storagePath = null, Func<X509Certificate2>? certificateProvider = null, string? emailDirectory = null)
    {
        this.settings = settings ?? new AppSettings();
        this.certificateProvider = certificateProvider ?? (() => CertificateSelector.Select(this.settings));
        StoragePath = storagePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DummySmtp", "Messages");
        this.emailDirectory = emailDirectory ?? AppSettings.EmailDirectory;
        logs = new DailyLogs(StoragePath);
    }

    private void WriteLog(string text)
    {
        Log?.Invoke(text);
        try { logs.Activity(text); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Start(IPAddress address, int port)
    {
        settings.Validate();
        if (settings.IncomingStartTls) { using var certificate = certificateProvider(); }
        relayNetworks = RelayAccess.Parse(settings.AllowedClients);
        Directory.CreateDirectory(StoragePath);
        if (settings.SaveEml)
        {
            Directory.CreateDirectory(emailDirectory);
            // Check actual write access before accepting mail, especially under Program Files.
            var probe = Path.Combine(emailDirectory, $".smtpng-write-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe)) { }
            File.Delete(probe);
        }
        MaintainLogs();
        var next = new TcpListener(address, port);
        try
        {
            if (settings.RelayEnabled) deliveryQueue = new DeliveryQueue(QueueDirectory, settings, WriteLog);
            next.Start(100);
        }
        catch { deliveryQueue?.Dispose(); deliveryQueue = null; throw; }
        listener = next;
        cancellation = new CancellationTokenSource();
        deliveryQueue?.Start();
        var token = cancellation.Token;
        _ = Task.Run(() => AcceptAsync(next, token));
        retentionTimer = new System.Threading.Timer(_ => MaintainLogs(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        WriteLog($"Listening on {address}:{port}. Forwarding {(settings.RelayEnabled ? "enabled" : "disabled")}.");
    }

    public void Stop()
    {
        retentionTimer?.Dispose(); retentionTimer = null;
        cancellation?.Cancel();
        listener?.Stop();
        foreach (var client in clients.Keys) client.Dispose();
        deliveryQueue?.Dispose(); deliveryQueue = null;
        cancellation?.Dispose();
        cancellation = null;
        listener = null;
    }

    private void MaintainLogs()
    {
        try
        {
            foreach (var error in logs.Maintain()) WriteLog(error);
            if (deliveryQueue != null)
            {
                deliveryQueue.Store.Maintain();
                var overview = deliveryQueue.Store.Overview();
                if (overview.Failed > 0 || overview.OldestPendingUtc < DateTime.UtcNow.AddHours(-1))
                    WriteLog($"QUEUE ALERT: pending={overview.Pending}, sending={overview.Sending}, failed={overview.Failed}; inspect Delivery queue.");
                if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(QueueDirectory))!).AvailableFreeSpace < settings.MinimumFreeDiskMegabytes * 1024L * 1024)
                    WriteLog("QUEUE ALERT: low disk space; new relay mail will receive 451.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { WriteLog($"Log maintenance failed: {ex.Message}"); }
    }

    private async Task AcceptAsync(TcpListener server, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await server.AcceptTcpClientAsync(token);
                if (clients.Count >= settings.MaxIncomingConnections)
                {
                    using (client)
                    {
                        try { await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("421 Server busy; retry later\r\n"), token); }
                        catch (IOException) { }
                    }
                    continue;
                }
                clients.TryAdd(client, 0);
                _ = HandleAsync(client, token);
            }
        }
        catch (Exception ex) when (token.IsCancellationRequested || ex is SocketException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                Stream transport = stream;
                var reader = new SmtpLineReader(transport);
                var writer = new StreamWriter(transport, new UTF8Encoding(false), 4096, true) { NewLine = "\r\n", AutoFlush = true };
                X509Certificate2? sessionCertificate = null;
                bool secure = false;
                try
                {
                async Task Reply(string value) => await writer.WriteLineAsync(value.AsMemory(), token);
                async Task<string?> ReadLine() => await reader.ReadLineAsync(token).WaitAsync(TimeSpan.FromMinutes(2), token);
                string? sender = null;
                var recipients = new List<string>();
                bool greeted = false;
                await Reply("220 SMTPNG server ready");
                while (await ReadLine() is { } line)
                {
                    var split = line.IndexOf(' ');
                    var command = (split < 0 ? line : line[..split]).ToUpperInvariant();
                    var argument = split < 0 ? "" : line[(split + 1)..].Trim();
                    switch (command)
                    {
                        case "EHLO":
                        case "HELO":
                            greeted = true; sender = null; recipients.Clear();
                            await Reply(command == "EHLO" ? "250-SMTPNG\r\n" + (settings.IncomingStartTls && !secure ? "250-STARTTLS\r\n" : "") + "250 SIZE 26214400" : "250 SMTPNG");
                            break;
                        case "STARTTLS":
                            if (!settings.IncomingStartTls || secure) { await Reply("502 STARTTLS is not available"); break; }
                            if (!greeted || sender != null || argument.Length > 0) { await Reply("503 Send EHLO and STARTTLS before MAIL FROM"); break; }
                            try { sessionCertificate = certificateProvider(); }
                            catch (Exception ex) when (ex is InvalidOperationException or CryptographicException or UnauthorizedAccessException)
                            { WriteLog($"TLS certificate unavailable: {ex.Message}"); await Reply("454 TLS temporarily unavailable"); break; }
                            await Reply("220 Ready to start TLS");
                            reader.Dispose(); writer.Dispose();
                            var ssl = new SslStream(stream, true);
                            transport = ssl;
                            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
                            {
                                handshake.CancelAfter(TimeSpan.FromSeconds(30));
                                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = sessionCertificate, ClientCertificateRequired = false,
                                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                                    AllowRenegotiation = false, AllowTlsResume = false
                                }, handshake.Token);
                            }
                            secure = true; greeted = false; sender = null; recipients.Clear();
                            reader = new SmtpLineReader(transport);
                            writer = new StreamWriter(transport, new UTF8Encoding(false), 4096, true) { NewLine = "\r\n", AutoFlush = true };
                            WriteLog($"TLS established with {remote}; certificate {sessionCertificate.Thumbprint}; expires {sessionCertificate.NotAfter:O}");
                            break;
                        case "MAIL":
                            if (!greeted) { await Reply("503 Send EHLO first"); break; }
                            if (settings.RequireIncomingTls && !secure) { await Reply("530 STARTTLS is required before MAIL FROM"); break; }
                            if (settings.RelayEnabled && !relayNetworks.Any(x => x.Contains(remoteIp)))
                            { await Reply("550 Client IP is not allowed to relay"); break; }
                            if (!TryAddress(argument, "FROM:", out var from)) { await Reply("501 Expected MAIL FROM:<address>"); break; }
                            sender = from; recipients.Clear(); await Reply("250 Sender accepted"); break;
                        case "RCPT":
                            if (sender is null) { await Reply("503 Send MAIL FROM first"); break; }
                            if (!TryAddress(argument, "TO:", out var to) || to.Length == 0) { await Reply("501 Expected RCPT TO:<address>"); break; }
                            if (settings.DirectDelivery)
                            {
                                try { DirectDelivery.Domain(to); }
                                catch (ArgumentException) { await Reply("501 Direct delivery requires a valid recipient domain"); break; }
                            }
                            if (recipients.Count >= 1000) { await Reply("452 Too many recipients; send another transaction"); break; }
                            recipients.Add(to); await Reply("250 Recipient accepted"); break;
                        case "DATA":
                            if (sender is null || recipients.Count == 0) { await Reply("503 Send MAIL FROM and RCPT TO first"); break; }
                            await Reply("354 End with <CRLF>.<CRLF>");
                            var data = new StringBuilder();
                            bool oversized = false;
                            while (true)
                            {
                                var bodyLine = await ReadLine();
                                if (bodyLine is null) return;
                                if (bodyLine == ".") break;
                                if (bodyLine.StartsWith('.')) bodyLine = bodyLine[1..];
                                if (data.Length + bodyLine.Length + 2 > 25 * 1024 * 1024) oversized = true;
                                if (!oversized) data.Append(bodyLine).Append("\r\n");
                            }
                            if (oversized) await Reply("552 Message exceeds 25 MB limit");
                            else
                            {
                                var raw = data.ToString();
                                var now = DateTime.Now;
                                var id = $"{now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
                                var file = settings.SaveEml ? Path.Combine(emailDirectory, id + ".eml") : "";
                                try
                                {
                                    if (settings.SaveEml) await File.WriteAllTextAsync(file, raw, Encoding.Latin1, token);
                                    var delivery = "Captured";
                                    if (settings.RelayEnabled)
                                    {
                                        try
                                        {
                                            var outgoing = settings.DkimEnabled ? DkimSigning.Sign(settings, raw) : raw;
                                            var queueId = await Task.Run(() => deliveryQueue!.Enqueue(sender, recipients, outgoing, settings), token);
                                            delivery = "Queued " + queueId;
                                        }
                                        catch (Exception ex) when (ex is not OutOfMemoryException)
                                        {
                                            if (token.IsCancellationRequested) throw;
                                            delivery = "Queue failed";
                                            WriteLog($"QUEUE ALERT: Queue acceptance failed for message {id}: {ex.Message}");
                                        }
                                    }
                                    var mail = new CapturedMail(now, sender, string.Join("; ", recipients), Subject(raw), remote, raw.Length <= 65536 ? raw : raw[..65536] + "\r\n[Preview truncated]", file, delivery);
                                    try { logs.Message(mail); }
                                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WriteLog($"Metadata log failed: {ex.Message}"); }
                                    WriteLog($"{delivery}: From=<{sender}> To={mail.To} Subject={mail.Subject}");
                                    Received?.Invoke(mail);
                                    await Reply(delivery == "Queue failed" ? "451 Unable to queue message; retry later" : "250 Message " + delivery.ToLowerInvariant());
                                }
                                catch (IOException ex) { WriteLog($"Capture failed: {ex.Message}"); await Reply("451 Unable to save message"); }
                                catch (UnauthorizedAccessException ex) { WriteLog($"Capture failed: {ex.Message}"); await Reply("451 Unable to save message"); }
                            }
                            sender = null; recipients.Clear(); break;
                        case "RSET": sender = null; recipients.Clear(); await Reply("250 Reset"); break;
                        case "NOOP": await Reply("250 OK"); break;
                        case "QUIT": await Reply("221 Goodbye"); return;
                        default: await Reply("502 Command not supported (no incoming AUTH)"); break;
                    }
                }
                }
                finally { reader.Dispose(); writer.Dispose(); if (transport != stream) await transport.DisposeAsync(); sessionCertificate?.Dispose(); }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or TimeoutException or AuthenticationException or CryptographicException)
        { if (!token.IsCancellationRequested) WriteLog($"Connection {remote}: {ex.Message}"); }
        finally { clients.TryRemove(client, out _); client.Dispose(); }
    }

    private static bool TryAddress(string value, string prefix, out string address)
    {
        address = "";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var remaining = value[prefix.Length..].TrimStart();
        if (!remaining.StartsWith('<') || remaining.IndexOf('>') is var end && end < 0) return false;
        address = remaining[1..remaining.IndexOf('>')];
        return !address.Any(c => char.IsControl(c) || c is '<' or '>') && (address.Length == 0 || System.Net.Mail.MailAddress.TryCreate(address, out var parsed) && parsed.Address == address);
    }

    public static string Subject(string raw)
    {
        var end = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headers = end >= 0 ? raw[..end] : raw;
        headers = Regex.Replace(headers, @"\r\n[ \t]+", " ");
        var match = Regex.Match(headers, @"^Subject:[ \t]*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!match.Success) return "(no subject)";
        var value = match.Groups[1].Value.Trim();
        try { value = new UTF8Encoding(false, true).GetString(Encoding.Latin1.GetBytes(value)); }
        catch (DecoderFallbackException) { }
        value = Regex.Replace(value, @"(\?=)[ \t]+(?==\?)", "$1");
        return Regex.Replace(value, @"=\?([^?]+)\?([bq])\?([^?]*)\?=", m =>
        {
            try
            {
                var bytes = m.Groups[2].Value.Equals("b", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(m.Groups[3].Value)
                    : DecodeQuoted(m.Groups[3].Value);
                return Encoding.GetEncoding(m.Groups[1].Value).GetString(bytes);
            }
            catch { return m.Value; }
        }, RegexOptions.IgnoreCase);
    }

    private static byte[] DecodeQuoted(string value)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length && byte.TryParse(value.Substring(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            { bytes.Add(b); i += 2; }
            else bytes.Add((byte)(value[i] == '_' ? ' ' : value[i]));
        }
        return bytes.ToArray();
    }
}
