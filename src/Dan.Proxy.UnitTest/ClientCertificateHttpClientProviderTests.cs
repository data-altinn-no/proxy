using Dan.Proxy.Interfaces;
using Dan.Proxy.Services;
using Dan.Proxy.UnitTest.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Dan.Proxy.UnitTest
{
    [TestClass]
    public class ClientCertificateHttpClientProviderTests
    {
        [TestMethod]
        public void GetClient_SameCertificate_ReturnsSameClient()
        {
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            using var provider = new ClientCertificateHttpClientProvider();

            var first = provider.GetClient(certificate);
            var second = provider.GetClient(certificate);

            Assert.AreSame(first, second);
        }

        [TestMethod]
        public void GetClient_DifferentCertificates_ReturnsDifferentClients()
        {
            var (certificateA, _) = TestCertificates.CreateClientCertificate("client-a");
            var (certificateB, _) = TestCertificates.CreateClientCertificate("client-b");
            using var provider = new ClientCertificateHttpClientProvider();

            var clientA = provider.GetClient(certificateA);
            var clientB = provider.GetClient(certificateB);

            Assert.AreNotSame(clientA, clientB);
        }

        [TestMethod]
        public async Task GetClient_ConcurrentFirstCalls_CreateOneClient()
        {
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            using var provider = new ClientCertificateHttpClientProvider();

            var clients = await Task.WhenAll(
                Enumerable.Range(0, 50).Select(_ => Task.Run(() => provider.GetClient(certificate))));

            Assert.AreEqual(1, clients.Distinct().Count());
        }

        [TestMethod]
        public void GetClient_InvalidHeaderValue_ThrowsEveryTimeAndDoesNotBlockValidCertificates()
        {
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            using var provider = new ClientCertificateHttpClientProvider();

            Assert.ThrowsExactly<FormatException>(() => provider.GetClient("this is not base64"));
            Assert.ThrowsExactly<FormatException>(() => provider.GetClient("this is not base64"));

            Assert.IsNotNull(provider.GetClient(certificate));
        }

        [TestMethod]
        public void DistinctHeaderValues_AreDifferentStringsForTheSameCertificate()
        {
            // The premise for the limit tests below, and the reason the limit exists.
            var headerValues = TestCertificates.CreateDistinctHeaderValues(3);

            Assert.AreEqual(3, headerValues.Distinct().Count());
        }

        [TestMethod]
        public void GetClient_MoreHeaderValuesThanTheLimit_KeepsTheCacheAtTheLimit()
        {
            var headerValues = TestCertificates.CreateDistinctHeaderValues(ClientCertificateHttpClientProvider.MaxClients + 5);
            using var provider = new ClientCertificateHttpClientProvider();

            foreach (var headerValue in headerValues)
            {
                provider.GetClient(headerValue);
            }

            Assert.AreEqual(ClientCertificateHttpClientProvider.MaxClients, provider.CachedClientCount);
        }

        [TestMethod]
        public void GetClient_WhenTheCacheIsFull_DropsTheClientUsedLongestAgo()
        {
            var limit = ClientCertificateHttpClientProvider.MaxClients;
            var headerValues = TestCertificates.CreateDistinctHeaderValues(limit + 1);
            using var provider = new ClientCertificateHttpClientProvider();
            var clients = headerValues.Take(limit).Select(provider.GetClient).ToArray();

            // Using the oldest client again leaves the second one as the one used longest ago.
            provider.GetClient(headerValues[0]);
            provider.GetClient(headerValues[limit]);

            Assert.AreSame(clients[0], provider.GetClient(headerValues[0]), "A client in use must survive.");
            Assert.AreNotSame(clients[1], provider.GetClient(headerValues[1]), "The client used longest ago must have been dropped.");
        }

        [TestMethod]
        public void GetClient_InvalidHeaderValueWhenTheCacheIsFull_DropsNothing()
        {
            var limit = ClientCertificateHttpClientProvider.MaxClients;
            var headerValues = TestCertificates.CreateDistinctHeaderValues(limit);
            using var provider = new ClientCertificateHttpClientProvider();
            var clients = headerValues.Select(provider.GetClient).ToArray();

            Assert.ThrowsExactly<FormatException>(() => provider.GetClient("this is not base64"));

            Assert.AreEqual(limit, provider.CachedClientCount);
            Assert.AreSame(clients[0], provider.GetClient(headerValues[0]));
        }

        [TestMethod]
        public async Task Dispose_DisposesTheClientsItCreated()
        {
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            var provider = new ClientCertificateHttpClientProvider();
            var client = provider.GetClient(certificate);

            provider.Dispose();

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => client.GetAsync("https://127.0.0.1:1/"));
        }

        [TestMethod]
        public void RegisteredAsInProgram_ResolvesAsOneSharedInstance()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IClientCertificateHttpClientProvider, ClientCertificateHttpClientProvider>();
            using var serviceProvider = services.BuildServiceProvider();

            var first = serviceProvider.GetRequiredService<IClientCertificateHttpClientProvider>();
            var second = serviceProvider.GetRequiredService<IClientCertificateHttpClientProvider>();

            Assert.AreSame(first, second);
        }
    }
}
