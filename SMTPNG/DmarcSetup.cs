using DnsClient;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DummySmtp;

internal static class DmarcSetup
{
    internal static string Domain(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (value.Length > 253 || !value.Contains('.') || value.Split('.').Any(x => !Regex.IsMatch(x, @"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")))
            throw new ArgumentException("Enter the visible From domain, for example example.com (ASCII/punycode).");
        return value;
    }

    internal static string Record(string domain, string policy, string report, string dkimAlignment, string spfAlignment)
    {
        Domain(domain);
        if (policy is not ("none" or "quarantine" or "reject") || dkimAlignment is not ("r" or "s") || spfAlignment is not ("r" or "s"))
            throw new ArgumentException("Choose a DMARC policy and alignment modes.");
        if (report.Length > 0 && (!MailAddress.TryCreate(report, out var address) || address.Address != report || report.Any(c => c > 127 || char.IsWhiteSpace(c) || ";,!?%".Contains(c))))
            throw new ArgumentException("Enter one plain reporting email address, or leave it blank.");
        return $"v=DMARC1; p={policy}; adkim={dkimAlignment}; aspf={spfAlignment}" + (report.Length == 0 ? "" : "; rua=mailto:" + report);
    }

    internal static Dictionary<string, string> Tags(string record)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in record.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            var split = part.IndexOf('=');
            if (split < 1 || !tags.TryAdd(part[..split].Trim(), part[(split + 1)..].Trim())) throw new ArgumentException("Malformed or duplicate DNS record tags.");
        }
        return tags;
    }

    internal static async Task<string[]> LookupTxt(string name, CancellationToken token)
    {
        var dns = new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(5), Retries = 1, UseCache = false });
        var result = await dns.QueryAsync(name, QueryType.TXT, cancellationToken: token);
        if (result.HasError) throw new IOException($"DNS lookup for {name}: {result.ErrorMessage}");
        return result.Answers.TxtRecords().Select(x => string.Concat(x.Text)).ToArray();
    }

    internal static async Task<string> CheckAsync(AppSettings settings, CancellationToken token, Func<string, CancellationToken, Task<string[]>>? lookup = null)
    {
        var domain = Domain(settings.DmarcDomain);
        lookup ??= LookupTxt;
        var output = new StringBuilder();
        string alignment = settings.DmarcDkimAlignment;
        try
        {
            var records = (await lookup("_dmarc." + domain, token)).Where(x => Regex.IsMatch(x, @"^v=DMARC1(?:;|$)")).ToArray();
            if (records.Length != 1) output.AppendLine($"DMARC: found {records.Length} records at _dmarc.{domain}; expected one. Parent/inherited policies are not checked.");
            else
            {
                var tags = Tags(records[0]);
                if (!tags.TryGetValue("p", out var policy) || policy is not ("none" or "quarantine" or "reject")) throw new ArgumentException("Published DMARC record has no valid p policy.");
                alignment = tags.GetValueOrDefault("adkim", "r");
                if (alignment is not ("r" or "s") || tags.GetValueOrDefault("aspf", "r") is not ("r" or "s")) throw new ArgumentException("Published DMARC record has invalid alignment tags.");
                output.AppendLine("Published DMARC: " + records[0]);
                var desired = Record(domain, settings.DmarcPolicy, settings.DmarcReportAddress, settings.DmarcDkimAlignment, settings.DmarcSpfAlignment);
                var wanted = Tags(desired);
                output.AppendLine(wanted.All(x => tags.GetValueOrDefault(x.Key, x.Key is "adkim" or "aspf" ? "r" : "") == x.Value) && (wanted.ContainsKey("rua") || !tags.ContainsKey("rua")) ? "Published policy matches the helper settings." : "Published policy differs from the helper settings.");
            }
        }
        catch (Exception ex) when (ex is IOException or DnsResponseException or ArgumentException) { output.AppendLine("DMARC check: " + ex.Message); }

        if (!settings.DkimEnabled) output.AppendLine("DKIM signing is disabled in SMTPNG. Aligned SPF or upstream DKIM would be needed for DMARC to pass.");
        else
        {
            output.AppendLine(domain.Equals(settings.DkimDomain, StringComparison.OrdinalIgnoreCase)
                ? "DKIM domain matches the From domain: aligned for both relaxed and strict modes."
                : alignment == "s" ? "DKIM domains differ: strict alignment fails. SMTPNG also requires the From domain to match its signing domain."
                : "DKIM domains differ: relaxed alignment is not confirmed here. SMTPNG requires the From domain to match its signing domain.");
            try
            {
                DkimSigning.Validate(settings);
                var keys = (await lookup(settings.DkimSelector + "._domainkey." + settings.DkimDomain, token)).Where(x => Regex.IsMatch(x, @"(?:^|;)\s*p\s*=")).ToArray();
                if (keys.Length != 1) output.AppendLine($"DKIM DNS: found {keys.Length} key records; expected one.");
                else
                {
                    var tags = Tags(keys[0]);
                    using var rsa = RSA.Create(); rsa.ImportFromPem(Secret.Unprotect(settings.ProtectedDkimKey));
                    var matches = tags.GetValueOrDefault("k", "rsa") == "rsa" && tags.GetValueOrDefault("v", "DKIM1") == "DKIM1" &&
                        Convert.FromBase64String(tags["p"]).SequenceEqual(rsa.ExportSubjectPublicKeyInfo());
                    output.AppendLine(matches ? "DKIM DNS public key matches the configured signing key." : "DKIM DNS key does not match the configured signing key (or is revoked/unsupported).");
                }
            }
            catch (Exception ex) when (ex is IOException or DnsResponseException or ArgumentException or CryptographicException or FormatException or System.ComponentModel.Win32Exception)
            { output.AppendLine("DKIM check: " + ex.Message); }
        }
        output.AppendLine("This is a DNS/configuration check, not a DMARC pass verdict. SPF authorization, actual message authentication, inherited policy and external report authorization are not evaluated. Send a test and inspect the receiver's Authentication-Results.");
        return output.ToString();
    }
}
