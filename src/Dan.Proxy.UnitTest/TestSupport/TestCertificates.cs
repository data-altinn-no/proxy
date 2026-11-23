using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dan.Proxy.UnitTest.TestSupport
{
    /// <summary>
    /// The two header values a caller can send for one client certificate: the certificate
    /// alone, or together with the intermediate certificate that issued it.
    /// </summary>
    internal sealed record ClientCertificateWithChain(
        string PfxWithoutChain,
        string PfxWithChain,
        string Thumbprint,
        string IntermediateThumbprint);

    internal static class TestCertificates
    {
        /// <summary>
        /// A client certificate in the shape the proxy receives it in the certificate header:
        /// a password-less PFX with the private key, base64 encoded.
        /// </summary>
        public static (string Base64Pfx, string Thumbprint) CreateClientCertificate(string commonName)
        {
            using var key = RSA.Create(2048);
            using var certificate = CreateSelfSignedClientCertificate(commonName, key);
            return (Convert.ToBase64String(certificate.Export(X509ContentType.Pfx)), certificate.Thumbprint);
        }

        /// <summary>
        /// Different header values for one and the same certificate. Every PFX export contains
        /// new random salt, so a caller that exports the certificate again for each request
        /// never sends the same header value twice.
        /// </summary>
        public static string[] CreateDistinctHeaderValues(int count)
        {
            using var key = RSA.Create(2048);
            using var certificate = CreateSelfSignedClientCertificate("client-exported-many-times", key);
            return Enumerable.Range(0, count)
                .Select(_ => Convert.ToBase64String(certificate.Export(X509ContentType.Pfx)))
                .ToArray();
        }

        /// <summary>A header value that holds a certificate but no private key, so it cannot be used to log on with.</summary>
        public static string CreateCertificateWithoutPrivateKey()
        {
            using var key = RSA.Create(2048);
            using var certificate = CreateSelfSignedClientCertificate("client-without-key", key);
            using var withoutKey = X509CertificateLoader.LoadCertificate(certificate.RawData);
            return Convert.ToBase64String(withoutKey.Export(X509ContentType.Pfx));
        }

        /// <summary>
        /// A client certificate issued the way an enterprise certificate is: by an intermediate
        /// certificate authority under a root. The certificates point to <paramref name="downloads"/>
        /// as the place to fetch the intermediate and the root, the way real ones point to the
        /// issuer's download site.
        /// </summary>
        public static ClientCertificateWithChain CreateClientCertificateWithChain(CertificateDownloadServer downloads)
        {
            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = DateTimeOffset.UtcNow.AddDays(1);

            using var rootKey = RSA.Create(2048);
            var rootRequest = CreateAuthorityRequest("Test Root", rootKey);
            using var root = rootRequest.CreateSelfSigned(notBefore, notAfter);

            using var intermediateKey = RSA.Create(2048);
            var intermediateRequest = CreateAuthorityRequest("Test Intermediate", intermediateKey);
            AddIssuer(intermediateRequest, root, downloads.UrlOf("root.cer"));
            using var intermediateWithoutKey = intermediateRequest.Create(root, notBefore, notAfter, RandomNumberGenerator.GetBytes(8));
            using var intermediate = intermediateWithoutKey.CopyWithPrivateKey(intermediateKey);

            using var clientKey = RSA.Create(2048);
            var clientRequest = CreateClientRequest("client-with-chain", clientKey);
            AddIssuer(clientRequest, intermediate, downloads.UrlOf("intermediate.cer"), downloads.UrlOf("status"));
            using var clientWithoutKey = clientRequest.Create(intermediate, notBefore, notAfter, RandomNumberGenerator.GetBytes(8));
            using var client = clientWithoutKey.CopyWithPrivateKey(clientKey);

            downloads.Publish("root.cer", root);
            downloads.Publish("intermediate.cer", intermediate);

            // The intermediate goes in without its private key, as a caller would send it.
            var withChain = new X509Certificate2Collection { client, intermediateWithoutKey };

            return new ClientCertificateWithChain(
                PfxWithoutChain: Convert.ToBase64String(client.Export(X509ContentType.Pfx)),
                PfxWithChain: Convert.ToBase64String(withChain.Export(X509ContentType.Pfx)!),
                Thumbprint: client.Thumbprint,
                IntermediateThumbprint: intermediate.Thumbprint);
        }

        private static X509Certificate2 CreateSelfSignedClientCertificate(string commonName, RSA key) =>
            CreateClientRequest(commonName, key).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        private static CertificateRequest CreateClientRequest(string commonName, RSA key)
        {
            var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, critical: false)); // client authentication

            return request;
        }

        private static CertificateRequest CreateAuthorityRequest(string commonName, RSA key)
        {
            var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
                certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

            return request;
        }

        /// <summary>
        /// Names the issuer, where its certificate can be fetched, and where the issuer answers
        /// whether the certificate has been revoked (OCSP).
        /// </summary>
        private static void AddIssuer(CertificateRequest request, X509Certificate2 issuer, string issuerUrl, string? statusUrl = null)
        {
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
                issuer, includeKeyIdentifier: true, includeIssuerAndSerial: false));
            request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(
                ocspUris: statusUrl is null ? null : [statusUrl], caIssuersUris: [issuerUrl]));
        }

        public static X509Certificate2 CreateServerCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false)); // server authentication

            var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
            subjectAlternativeNames.AddDnsName("localhost");
            subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(subjectAlternativeNames.Build());

            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

            // Reloading from PFX gives a key that SslStream can use as a server certificate on Windows too.
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null);
        }
    }
}
