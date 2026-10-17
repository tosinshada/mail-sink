using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mailsink.Core.Options;

namespace Mailsink.Core.Networking;

/// <summary>
/// Supplies the TLS certificate for STARTTLS and implicit-TLS endpoints: a configured PFX or PEM pair, or a
/// self-signed ECDSA P-256 certificate generated on first start (persisted in <c>DataDir</c> for SQLite storage).
/// </summary>
public sealed class CertificateProvider(IOptions<MailsinkOptions> options, ILogger<CertificateProvider> logger)
{
    private const string GeneratedFileName = "mailsink-tls.pfx";
    private readonly Lazy<X509Certificate2?> _certificate = new(() => Load(options.Value, logger));

    public X509Certificate2? Certificate => _certificate.Value;

    public string? ExportPublicPem() => Certificate?.ExportCertificatePem();

    private static X509Certificate2? Load(MailsinkOptions options, ILogger logger)
    {
        var tls = options.Tls;
        if (!string.IsNullOrEmpty(tls.CertificatePath))
        {
            if (!string.IsNullOrEmpty(tls.KeyPath))
            {
                using var pem = X509Certificate2.CreateFromPemFile(tls.CertificatePath, tls.KeyPath);
                return Reimport(pem);
            }

            return X509CertificateLoader.LoadPkcs12FromFile(tls.CertificatePath, tls.CertificatePassword);
        }

        if (!tls.GenerateSelfSigned)
        {
            return null;
        }

        var persist = options.Storage == StorageKind.Sqlite;
        var path = Path.Combine(Path.GetFullPath(options.DataDir), GeneratedFileName);
        if (persist && File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, null);
                if (existing.NotAfter > DateTime.Now.AddDays(7))
                {
                    return existing;
                }
            }
            catch (CryptographicException ex)
            {
                logger.LogWarning(ex, "Could not load {Path}; generating a new certificate", path);
            }
        }

        var generated = Generate(options);
        if (persist)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, generated.Export(X509ContentType.Pkcs12));
            logger.LogInformation("Generated self-signed TLS certificate {Path}", path);
        }

        return generated;
    }

    private static X509Certificate2 Generate(MailsinkOptions options)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={options.Hostname}, O=Mailsink", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in new[] { "localhost", "mailsink", options.Hostname }.Concat(options.Tls.Hostnames).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            san.AddDnsName(name);
        }

        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        return Reimport(cert);
    }

    // On Windows an ephemeral key cannot be used by SslStream; a PKCS#12 round-trip fixes that.
    private static X509Certificate2 Reimport(X509Certificate2 cert) =>
        X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
}
