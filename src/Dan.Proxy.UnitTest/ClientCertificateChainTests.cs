using Dan.Proxy.Services;
using Dan.Proxy.UnitTest.TestSupport;

namespace Dan.Proxy.UnitTest
{
    /// <summary>
    /// The proxy does not fetch the certificates of the authorities that issued a client
    /// certificate: the address to fetch from stands in the certificate, and the download
    /// costs two outgoing calls per handshake. A target is therefore sent the certificate
    /// alone, unless the machine already has its issuers in a certificate store.
    ///
    /// Linux only. On Windows the operating system fetches the issuers itself and keeps them
    /// in the user's certificate store.
    /// </summary>
    [TestClass]
    public class ClientCertificateChainTests
    {
        /// <summary>The target closes the connection after each response, so every call makes a new handshake.</summary>
        private static MtlsTestServer CreateTargetWithoutKeepAlive()
        {
            var target = new MtlsTestServer();
            target.Respond = _ => Task.FromResult(new TestResponse(200, [("Connection", "close")], []));
            return target;
        }

        private static async Task CallAsync(HttpClient client, MtlsTestServer target)
        {
            using var response = await client.GetAsync(target.BaseAddress);
            response.EnsureSuccessStatusCode();
        }

        private static ClientCertificateHttpClientProvider CreateProvider(MtlsTestServer target) =>
            new(handler => handler.SslOptions.RemoteCertificateValidationCallback =
                (_, certificate, _, _) => target.IsServerCertificate(certificate));

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public async Task DownloadSiteNamedInTheCertificate_IsNeverCalled()
        {
            await using var downloads = new CertificateDownloadServer();
            await using var target = CreateTargetWithoutKeepAlive();
            var certificate = TestCertificates.CreateClientCertificateWithChain(downloads);
            using var provider = CreateProvider(target);

            var client = provider.GetClient(certificate.PfxWithoutChain);
            await CallAsync(client, target);
            await CallAsync(client, target);

            Assert.AreEqual(2, target.ConnectionCount, "The two calls must have made a handshake each.");
            Assert.AreEqual(0, downloads.Requests, "The proxy must not fetch the issuers of the certificate.");
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public async Task CertificateWithIssuersTheMachineDoesNotHave_IsPresentedToTheTargetAlone()
        {
            await using var downloads = new CertificateDownloadServer();
            await using var target = CreateTargetWithoutKeepAlive();
            var certificate = TestCertificates.CreateClientCertificateWithChain(downloads);
            using var provider = CreateProvider(target);

            await CallAsync(provider.GetClient(certificate.PfxWithoutChain), target);

            Assert.AreEqual(certificate.Thumbprint, target.Requests.Single().ClientCertificateThumbprint);
            Assert.IsFalse(target.WasSentByClient(certificate.IntermediateThumbprint), "The issuers are in no certificate store here, so the target must get the certificate alone.");
        }
    }
}
