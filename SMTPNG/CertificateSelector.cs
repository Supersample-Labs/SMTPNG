using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DummySmtp;

public static class CertificateSelector
{
    public static X509Certificate2 Select(AppSettings settings)
    {
        using var store = new X509Store(settings.CertificateStore, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var certificates = store.Certificates;
        try { return SelectFrom(certificates.Cast<X509Certificate2>(), settings.CertificateFriendlyName); }
        finally { foreach (var cert in certificates) cert.Dispose(); }
    }

    public static X509Certificate2 SelectFrom(IEnumerable<X509Certificate2> certificates, string friendlyName)
    {
        var now = DateTime.Now;
        var matches = certificates.Where(c => string.Equals(c.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase)
            && c.HasPrivateKey && c.NotBefore <= now && c.NotAfter > now && IsServerCertificate(c))
            .OrderByDescending(c => c.NotBefore).ThenByDescending(c => c.NotAfter).ThenBy(c => c.Thumbprint);
        string? keyError = null;
        foreach (var cert in matches)
        {
            try
            {
                // Exercise the key as the current GUI/service identity, not merely HasPrivateKey.
                using var rsa = cert.GetRSAPrivateKey();
                if (rsa != null) rsa.SignData(new byte[] { 1, 2, 3 }, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                else
                {
                    using var ec = cert.GetECDsaPrivateKey();
                    if (ec == null) continue;
                    ec.SignData(new byte[] { 1, 2, 3 }, HashAlgorithmName.SHA256);
                }
                return new X509Certificate2(cert);
            }
            catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException) { keyError = ex.Message; }
        }
        throw new InvalidOperationException($"No currently valid server certificate with an accessible private key has friendly name '{friendlyName}'. Store: Local Computer. {keyError}");
    }

    private static bool IsServerCertificate(X509Certificate2 cert)
    {
        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return eku == null || eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value is "1.3.6.1.5.5.7.3.1" or "2.5.29.37.0");
    }
}
