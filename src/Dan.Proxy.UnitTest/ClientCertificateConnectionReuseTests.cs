using Dan.Proxy.Services;
using Dan.Proxy.UnitTest.TestSupport;

namespace Dan.Proxy.UnitTest
{
    /// <summary>
    /// Runs the provider against a local server that requires a client certificate, to check
    /// what actually happens on the wire: which certificate is presented and how many
    /// connections are opened.
    /// </summary>
    [TestClass]
    public class ClientCertificateConnectionReuseTests
    {
        private static ClientCertificateHttpClientProvider CreateProvider(MtlsTestServer server) =>
            new(handler => handler.SslOptions.RemoteCertificateValidationCallback =
                (_, certificate, _, _) => server.IsServerCertificate(certificate));

        private static async Task<(int ConnectionId, string Thumbprint)> CallAsync(HttpClient client, MtlsTestServer server) =>
            MtlsTestServer.ParseResponseBody(await client.GetStringAsync(server.BaseAddress));

        [TestMethod]
        public async Task SameCertificate_ReusesOneConnection()
        {
            await using var server = new MtlsTestServer();
            using var provider = CreateProvider(server);
            var (certificate, thumbprint) = TestCertificates.CreateClientCertificate("client-a");

            for (var call = 0; call < 3; call++)
            {
                var response = await CallAsync(provider.GetClient(certificate), server);
                Assert.AreEqual(thumbprint, response.Thumbprint);
            }

            Assert.AreEqual(1, server.ConnectionCount);
        }

        [TestMethod]
        public async Task DifferentCertificates_UseSeparateConnections()
        {
            await using var server = new MtlsTestServer();
            using var provider = CreateProvider(server);
            var (certificateA, thumbprintA) = TestCertificates.CreateClientCertificate("client-a");
            var (certificateB, thumbprintB) = TestCertificates.CreateClientCertificate("client-b");

            var firstA = await CallAsync(provider.GetClient(certificateA), server);
            var firstB = await CallAsync(provider.GetClient(certificateB), server);
            var secondA = await CallAsync(provider.GetClient(certificateA), server);

            Assert.AreEqual(thumbprintA, firstA.Thumbprint);
            Assert.AreEqual(thumbprintB, firstB.Thumbprint);
            Assert.AreEqual(thumbprintA, secondA.Thumbprint);
            Assert.AreEqual(firstA.ConnectionId, secondA.ConnectionId);
            Assert.AreNotEqual(firstA.ConnectionId, firstB.ConnectionId);
            Assert.AreEqual(2, server.ConnectionCount);
        }

        [TestMethod]
        public async Task ParallelCalls_AlwaysPresentTheRequestedCertificate()
        {
            await using var server = new MtlsTestServer();
            using var provider = CreateProvider(server);
            var (certificateA, thumbprintA) = TestCertificates.CreateClientCertificate("client-a");
            var (certificateB, thumbprintB) = TestCertificates.CreateClientCertificate("client-b");

            var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(async call =>
            {
                var useA = call % 2 == 0;
                var response = await CallAsync(provider.GetClient(useA ? certificateA : certificateB), server);
                return (Expected: useA ? thumbprintA : thumbprintB, Actual: response.Thumbprint);
            }));

            foreach (var response in responses)
            {
                Assert.AreEqual(response.Expected, response.Actual);
            }
        }

        [TestMethod]
        public async Task Cookies_AreNotCarriedFromOneRequestToTheNext()
        {
            await using var server = new MtlsTestServer();
            using var provider = CreateProvider(server);
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");

            // The test server sets a cookie on every response.
            await CallAsync(provider.GetClient(certificate), server);
            await CallAsync(provider.GetClient(certificate), server);

            Assert.AreEqual(2, server.Requests.Count);
            foreach (var request in server.Requests)
            {
                Assert.IsFalse(request.Headers.ContainsKey("Cookie"), "The shared handler must not replay cookies.");
            }
        }

        [TestMethod]
        public async Task ClientDroppedFromTheCache_KeepsWorkingForARequestThatStillUsesIt()
        {
            await using var server = new MtlsTestServer();
            using var provider = CreateProvider(server);
            var headerValues = TestCertificates.CreateDistinctHeaderValues(ClientCertificateHttpClientProvider.MaxClients + 1);
            var first = provider.GetClient(headerValues[0]);
            await CallAsync(first, server);

            // Fill the cache with other header values until the first client is pushed out.
            foreach (var headerValue in headerValues.Skip(1))
            {
                provider.GetClient(headerValue);
            }

            // A request that got hold of the client before it was dropped must still complete.
            await CallAsync(first, server);

            Assert.AreNotSame(first, provider.GetClient(headerValues[0]), "The first client should have been dropped from the cache.");
        }
    }
}
