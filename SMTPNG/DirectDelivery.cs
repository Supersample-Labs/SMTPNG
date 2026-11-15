using DnsClient;
using DnsClient.Protocol;
using System.Net;

namespace DummySmtp;

internal static class DirectDelivery
{
    private static readonly LookupClient dns = new(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(10), Retries = 1, UseCache = true });

    internal static async Task<string[]> ResolveAsync(string domain, CancellationToken token)
    {
        var answer = await dns.QueryAsync(domain, QueryType.MX, cancellationToken: token);
        if (answer.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain) throw new SmtpDeliveryException($"Recipient domain {domain} does not exist.", true);
        if (answer.HasError) throw new IOException($"MX lookup failed for {domain}: {answer.ErrorMessage}");
        var records = answer.Answers.MxRecords().ToArray();
        if (records.Any(x => x.Exchange.Value == ".")) throw new SmtpDeliveryException($"{domain} does not accept email (null MX).", true);
        return records.Length == 0 ? new[] { domain } : records.OrderBy(x => x.Preference).ThenBy(_ => Random.Shared.Next()).Select(x => x.Exchange.Value.TrimEnd('.')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task SendAsync(AppSettings settings, string sender, IReadOnlyList<string> recipients, string raw, CancellationToken token)
    {
        // Resolve every destination before sending anything, avoiding partial delivery on DNS failure.
        var destinations = new List<(string[] Hosts, string[] Recipients)>();
        foreach (var group in recipients.Distinct(StringComparer.OrdinalIgnoreCase).GroupBy(Domain))
            destinations.Add((await ResolveAsync(group.Key, token), group.ToArray()));
        foreach (var destination in destinations)
        {
            Exception? failure = null;
            foreach (var host in destination.Hosts)
            {
                try
                {
                    await SmtpForwarder.SendToHostAsync(settings, host, 25, sender, destination.Recipients, raw, token, direct: true);
                    failure = null;
                    break;
                }
                catch (SmtpDeliveryException ex) when (ex.Permanent) { throw; }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
                { failure = ex; }
            }
            if (failure != null) throw new IOException($"Direct delivery failed: {failure.Message}. Some recipients may already have accepted the message; retries can duplicate delivery.", failure);
        }
    }

    internal static string Domain(string address)
    {
        if (!System.Net.Mail.MailAddress.TryCreate(address, out var parsed) || parsed.Address != address || Uri.CheckHostName(parsed.Host) != UriHostNameType.Dns)
            throw new ArgumentException("Direct delivery requires a mailbox with a DNS domain.");
        return parsed.Host.ToLowerInvariant();
    }
}
