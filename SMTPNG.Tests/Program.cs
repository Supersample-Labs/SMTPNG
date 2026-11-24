using DummySmtp;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Sockets;

if (args is ["--copy-payload", var payloadSource, var payloadDestination])
{
    WindowsService.CopyApplicationFiles(payloadSource, payloadDestination);
    Console.WriteLine($"Verified {InstallPayload.Validate(payloadDestination).Count} published files in copied service payload.");
    return;
}

if (args is ["--queue-abrupt-exit", var crashDirectory])
{
    var crashStore = new QueueStore(crashDirectory);
    crashStore.Enqueue("sender@example.com", new[] { "recipient@example.net" }, "From: sender@example.com\r\nSubject: Crash recovery\r\n\r\nDurable message\r\n", new AppSettings { Forwarding = true, UpstreamHost = "localhost", MinimumFreeDiskMegabytes = 0, NotifyDeliveryFailures = false });
    crashStore.Claim(1);
    Environment.Exit(0); // Simulates termination without disposing the queue or recording the delivery result.
}

static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
using var rsa = RSA.Create(2048);
var settings = new AppSettings { DirectDelivery = true, HeloHostname = "smtp.example.com", DkimEnabled = true, DkimDomain = "example.com", DkimSelector = "smtp", ProtectedDkimKey = Secret.Protect(rsa.ExportRSAPrivateKeyPem()) };
settings.Validate();
var raw = "From: sender@example.com\r\nTo: user@example.net\r\nSubject: DKIM test\r\nDate: Sat, 14 Nov 2026 12:00:00 +0000\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\nTest body with trailing spaces   \r\n\r\n";
var signed = DkimSigning.Sign(settings, raw);
using var message = MimeMessage.Load(new MemoryStream(Encoding.Latin1.GetBytes(signed)));
var verifier = new DkimVerifier(new KeyLocator(rsa.ExportSubjectPublicKeyInfo()));
Check(verifier.Verify(message, message.Headers.First(x => x.Id == HeaderId.DkimSignature)), "DKIM signature verifies after serialization");
using var tampered = MimeMessage.Load(new MemoryStream(Encoding.Latin1.GetBytes(signed.Replace("Test body", "Changed body"))));
Check(!verifier.Verify(tampered, tampered.Headers.First(x => x.Id == HeaderId.DkimSignature)), "DKIM detects body tampering");
Check(DkimSigning.DnsRecord(settings.ProtectedDkimKey, "example.com", "smtp").Contains(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())), "DNS public key matches signing key");
Check(DirectDelivery.Domain("user@example.net") == "example.net", "Recipient domain parsed");
try { (settings with { Forwarding = true }).Validate(); throw new Exception("Expected rejection"); } catch (ArgumentException) { Console.WriteLine("PASS mutually exclusive modes"); }
try { DkimSigning.Sign(settings, raw.Replace("sender@example.com", "sender@other.net")); throw new Exception("Expected rejection"); } catch (IOException) { Console.WriteLine("PASS signing domain enforced"); }
var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
var receive = Task.Run(async () => {
    using var peer = await listener.AcceptTcpClientAsync();
    using var stream = peer.GetStream();
    using var reader = new StreamReader(stream, Encoding.Latin1);
    using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
    await writer.WriteLineAsync("220 local test");
    Check((await reader.ReadLineAsync()) == "EHLO smtp.example.com", "configured EHLO");
    await writer.WriteLineAsync("250 local test");
    await reader.ReadLineAsync(); await writer.WriteLineAsync("250 sender ok");
    await reader.ReadLineAsync(); await writer.WriteLineAsync("250 recipient ok");
    await reader.ReadLineAsync(); await writer.WriteLineAsync("354 send data");
    var body = new StringBuilder(); string? line;
    while ((line = await reader.ReadLineAsync()) != ".") { if (line == null) throw new Exception("Disconnected"); body.Append(line.StartsWith("..") ? line[1..] : line).Append("\r\n"); }
    await writer.WriteLineAsync("250 accepted");
    return body.ToString();
});
await SmtpForwarder.SendToHostAsync(settings with { Port = 25 }, "127.0.0.1", port, "sender@example.com", new[] { "user@example.net" }, signed, CancellationToken.None, direct: true);
Check(await receive == signed, "direct SMTP preserves signed bytes, accepts server without STARTTLS");
listener.Stop();
Check(DmarcSetup.Record("example.com", "none", "", "r", "r") == "v=DMARC1; p=none; adkim=r; aspf=r", "DMARC monitoring record");
Check(DmarcSetup.Record("example.com", "reject", "reports@example.com", "s", "s").Contains("rua=mailto:reports@example.com"), "DMARC reporting and strict alignment");
try { DmarcSetup.Record("example.com", "none", "reports@example.com; p=reject", "r", "r"); throw new Exception("Expected rejection"); } catch (ArgumentException) { Console.WriteLine("PASS reporting tag injection rejected"); }
try { DmarcSetup.Domain("example.com\\bad"); throw new Exception("Expected rejection"); } catch (ArgumentException) { Console.WriteLine("PASS malformed domain rejected"); }
var dmarcSettings = settings with { DmarcDomain = "example.com" };
var publicRecord = "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
Task<string[]> Lookup(string name, CancellationToken token) => Task.FromResult(name.StartsWith("_dmarc.") ? new[] { "v=DMARC1; p=none" } : new[] { publicRecord });
var report = await DmarcSetup.CheckAsync(dmarcSettings, CancellationToken.None, Lookup);
Check(report.Contains("matches the helper") && report.Contains("aligned for both") && report.Contains("public key matches"), "Published policy, alignment and DKIM key checked");
var duplicate = await DmarcSetup.CheckAsync(dmarcSettings, CancellationToken.None, (name, token) => Task.FromResult(new[] { "v=DMARC1; p=none", "v=DMARC1; p=reject" }));
Check(duplicate.Contains("found 2 records"), "Duplicate DMARC records reported");
var missing = await DmarcSetup.CheckAsync(dmarcSettings with { DkimEnabled = false }, CancellationToken.None, (name, token) => Task.FromResult(Array.Empty<string>()));
Check(missing.Contains("found 0 records") && missing.Contains("disabled"), "Missing DMARC and disabled DKIM reported");
var malformed = await DmarcSetup.CheckAsync(dmarcSettings, CancellationToken.None, (name, token) => Task.FromResult(name.StartsWith("_dmarc.") ? new[] { "v=DMARC1; p=none; p=reject" } : new[] { "v=DKIM1; p=invalid" }));
Check(malformed.Contains("duplicate") && malformed.Contains("DKIM check:"), "Malformed published records reported");
var mismatch = await DmarcSetup.CheckAsync(dmarcSettings with { DkimDomain = "other.net" }, CancellationToken.None, Lookup);
Check(mismatch.Contains("alignment is not confirmed"), "Different domains never falsely reported aligned");
var json = System.Text.Json.JsonSerializer.Serialize(dmarcSettings with { DmarcReportAddress = "reports@example.com", DmarcPolicy = "quarantine" });
var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
Check(restored.DmarcReportAddress == "reports@example.com" && restored.DmarcPolicy == "quarantine", "DMARC settings persist");
await QueueTests.RunAsync(Check);
Console.WriteLine("All checks passed.");

sealed class KeyLocator(byte[] publicKey) : IDkimPublicKeyLocator
{
    public AsymmetricKeyParameter LocatePublicKey(string methods, string domain, string selector, CancellationToken cancellationToken = default) => PublicKeyFactory.CreateKey(publicKey);
    public Task<AsymmetricKeyParameter> LocatePublicKeyAsync(string methods, string domain, string selector, CancellationToken cancellationToken = default) => Task.FromResult(LocatePublicKey(methods, domain, selector));
}
