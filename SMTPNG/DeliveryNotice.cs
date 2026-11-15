using MimeKit;
using System.Text;

namespace DummySmtp;

internal static class DeliveryNotice
{
    internal static string? Create(AppSettings settings, string sender, string recipient, string error, string id, string original)
    {
        if (!settings.NotifyDeliveryFailures || sender.Length == 0 || !System.Net.Mail.MailAddress.TryCreate(sender, out var mailbox) || mailbox.Address != sender) return null;
        using var input = new MemoryStream(Encoding.Latin1.GetBytes(original));
        using var source = MimeMessage.Load(input);
        if (source.Headers.Contains(HeaderId.AutoSubmitted) && !string.Equals(source.Headers[HeaderId.AutoSubmitted]?.Trim(), "no", StringComparison.OrdinalIgnoreCase) || source.Body is MultipartReport) return null;
        var domain = settings.DkimEnabled ? settings.DkimDomain : settings.HeloHostname;
        if (string.IsNullOrWhiteSpace(domain)) domain = "localhost";
        using var notice = new MimeMessage();
        notice.From.Add(new MailboxAddress("SMTPNG delivery service", "postmaster@" + domain));
        notice.To.Add(MailboxAddress.Parse(sender)); notice.Subject = "Delivery failed: " + source.Subject;
        notice.Date = DateTimeOffset.UtcNow; notice.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        notice.Headers.Add(HeaderId.AutoSubmitted, "auto-replied");
        static string Clean(string value) => value.Replace("\r", " ").Replace("\n", " ");
        var text = $"SMTPNG could not deliver queue message {id} to {recipient}.\r\n{Clean(error)}\r\nThe failed message is retained on the relay for administrator review.";
        var status = $"Reporting-MTA: dns; {Clean(domain)}\r\n\r\nFinal-Recipient: rfc822; {Clean(recipient)}\r\nAction: failed\r\nStatus: 5.0.0\r\nDiagnostic-Code: smtp; {Clean(error)}\r\n";
        notice.Body = new MultipartReport("delivery-status") {
            new TextPart("plain") { Text = text },
            new MimePart("message", "delivery-status") { Content = new MimeContent(new MemoryStream(Encoding.ASCII.GetBytes(status))) }
        };
        using var output = new MemoryStream(); var format = FormatOptions.Default.Clone(); format.NewLineFormat = NewLineFormat.Dos;
        notice.Prepare(EncodingConstraint.SevenBit); notice.WriteTo(format, output);
        var raw = Encoding.Latin1.GetString(output.ToArray());
        return settings.DkimEnabled ? DkimSigning.Sign(settings, raw) : raw;
    }
}
