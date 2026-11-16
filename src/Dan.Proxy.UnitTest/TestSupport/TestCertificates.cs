using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dan.Proxy.UnitTest.TestSupport
{
    internal static class TestCertificates
    {
        /// <summary>
        /// A client certificate in the shape the proxy receives it in the certificate header:
        /// a password-less PFX with the private key, base64 encoded.
        /// </summary>
        public static (string Base64Pfx, string Thumbprint) CreateClientCertificate(string commonName)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, critical: false)); // client authentication

            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            return (Convert.ToBase64String(certificate.Export(X509ContentType.Pfx)), certificate.Thumbprint);
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
