using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Cryptography;

namespace DummySmtp;

internal static class DkimSigning
{
    public static void Validate(AppSettings settings)
    {
        if (!Regex.IsMatch(settings.DkimDomain, @"^(?=.{1,253}$)[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?$") ||
            settings.DkimDomain.Split('.').Any(x => x.Length is 0 or > 63 || x.StartsWith('-') || x.EndsWith('-')) ||
            !Regex.IsMatch(settings.DkimSelector, @"^[a-zA-Z0-9][a-zA-Z0-9_-]{0,62}$"))
            throw new ArgumentException("Enter a valid DKIM domain and selector.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(Secret.Unprotect(settings.ProtectedDkimKey));
        if (rsa.KeySize < 2048 || rsa.ExportParameters(true).D == null) throw new ArgumentException("Generate a DKIM RSA key of at least 2048 bits.");
    }

    public static string DnsRecord(string protectedKey, string domain, string selector)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(Secret.Unprotect(protectedKey));
        var value = "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        return $"Name: {selector}._domainkey.{domain}\r\nType: TXT\r\nValue: {value}\r\n\r\nZone-file format (one TXT record, multiple quoted strings):\r\n" +
            string.Join(" ", Enumerable.Range(0, (value.Length + 199) / 200).Select(i => "\"" + value.Substring(i * 200, Math.Min(200, value.Length - i * 200)) + "\""));
    }

    public static string Sign(AppSettings settings, string raw)
    {
        using var input = new MemoryStream(Encoding.Latin1.GetBytes(raw));
        using var message = MimeMessage.Load(input);
        if (message.From.Mailboxes.Count() != 1 || !message.From.Mailboxes.Single().Address.Split('@').Last().Equals(settings.DkimDomain, StringComparison.OrdinalIgnoreCase))
            throw new IOException("DKIM requires exactly one From mailbox in the configured signing domain.");
        using var key = new MemoryStream(Encoding.ASCII.GetBytes(Secret.Unprotect(settings.ProtectedDkimKey)));
        var signer = new DkimSigner(key, settings.DkimDomain, settings.DkimSelector)
        {
            HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
            BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed
        };
        message.Prepare(EncodingConstraint.SevenBit);
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        signer.Sign(options, message, new[] { HeaderId.From, HeaderId.To, HeaderId.Subject, HeaderId.Date, HeaderId.MessageId, HeaderId.MimeVersion, HeaderId.ContentType, HeaderId.ContentTransferEncoding, HeaderId.ReplyTo });
        using var output = new MemoryStream();
        message.WriteTo(options, output);
        return Encoding.Latin1.GetString(output.ToArray());
    }
}

