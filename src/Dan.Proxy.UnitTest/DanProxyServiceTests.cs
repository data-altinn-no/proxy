using System.Net;
using Dan.Proxy.Config;
using Dan.Proxy.Services;
using Dan.Proxy.UnitTest.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dan.Proxy.UnitTest
{
    /// <summary>
    /// Runs DanProxyService.ProxyRequest against a local server that requires a client
    /// certificate, covering the branch that takes the certificate from a request header.
    /// </summary>
    [TestClass]
    public class DanProxyServiceTests
    {
        private const string CertificateHeader = "x-client-certificate";

        private sealed class Fixture : IAsyncDisposable
        {
            public MtlsTestServer Server { get; } = new();
            public ClientCertificateHttpClientProvider CertificateClients { get; }
            public RecordingHttpClientFactory StandardClients { get; }
            public DanProxyService Service { get; }

            /// <param name="settings">Defaults to validation on and the test certificate header configured.</param>
            /// <param name="standardClientTrustsServer">
            /// False leaves the standard client with normal certificate validation, which
            /// rejects the test server's self-signed certificate like any untrusted target.
            /// </param>
            public Fixture(DanProxySettings? settings = null, bool standardClientTrustsServer = true)
            {
                CertificateClients = new ClientCertificateHttpClientProvider(TrustTestServer);
                StandardClients = new RecordingHttpClientFactory(standardClientTrustsServer ? TrustTestServer : _ => { });

                settings ??= new DanProxySettings
                {
                    IgnoreCertificateValidation = false,
                    CustomCertificateHeaderName = CertificateHeader
                };

                Service = new DanProxyService(NullLoggerFactory.Instance, StandardClients, CertificateClients, Options.Create(settings));
            }

            private void TrustTestServer(SocketsHttpHandler handler) =>
                handler.SslOptions.RemoteCertificateValidationCallback =
                    (_, certificate, _, _) => Server.IsServerCertificate(certificate);

            public FakeHttpRequestData CreateRequest(string? certificate = null)
            {
                var target = Uri.EscapeDataString($"{Server.Authority}/some/path");
                var request = new FakeHttpRequestData(new Uri($"https://proxy.test/?url={target}"));

                if (certificate is not null)
                {
                    request.Headers.Add(CertificateHeader, certificate);
                }

                return request;
            }

            public async Task<(HttpStatusCode Status, int ConnectionId, string Thumbprint)> SendAsync(FakeHttpRequestData request)
            {
                var response = (FakeHttpResponseData)await Service.ProxyRequest(request);
                var (connectionId, thumbprint) = MtlsTestServer.ParseResponseBody(response.BodyAsString());
                return (response.StatusCode, connectionId, thumbprint);
            }

            public async ValueTask DisposeAsync()
            {
                CertificateClients.Dispose();
                StandardClients.Dispose();
                await Server.DisposeAsync();
            }
        }

        [TestMethod]
        public void Settings_ValidateTargetCertificatesByDefault()
        {
            Assert.IsFalse(new DanProxySettings().IgnoreCertificateValidation);
        }

        [TestMethod]
        public async Task DefaultSettings_RejectTargetWithUntrustedCertificate()
        {
            await using var fixture = new Fixture(new DanProxySettings(), standardClientTrustsServer: false);

            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => fixture.Service.ProxyRequest(fixture.CreateRequest()));

            Assert.AreEqual(0, fixture.Server.Requests.Count, "No request may reach a target whose certificate was not validated.");
        }

        [TestMethod]
        public async Task IgnoreCertificateValidationTurnedOn_AcceptsTargetWithUntrustedCertificate()
        {
            var settings = new DanProxySettings { IgnoreCertificateValidation = true };
            await using var fixture = new Fixture(settings, standardClientTrustsServer: false);

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.OK, response.Status);
        }

        [TestMethod]
        public async Task TargetInIgnoreValidationHosts_AcceptsUntrustedCertificateForThatHost()
        {
            var settings = new DanProxySettings { IgnoreCertificateValidationHosts = "127.0.0.1" };
            await using var fixture = new Fixture(settings, standardClientTrustsServer: false);

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.OK, response.Status);
        }

        [TestMethod]
        public async Task CertificateInHeader_IsPresentedToTargetAndConnectionIsReused()
        {
            await using var fixture = new Fixture();
            var (certificate, thumbprint) = TestCertificates.CreateClientCertificate("client-a");

            var first = await fixture.SendAsync(fixture.CreateRequest(certificate));
            var second = await fixture.SendAsync(fixture.CreateRequest(certificate));

            Assert.AreEqual(HttpStatusCode.OK, first.Status);
            Assert.AreEqual(HttpStatusCode.OK, second.Status);
            Assert.AreEqual(thumbprint, first.Thumbprint);
            Assert.AreEqual(thumbprint, second.Thumbprint);
            Assert.AreEqual(first.ConnectionId, second.ConnectionId);
            Assert.AreEqual(1, fixture.Server.ConnectionCount);
            Assert.AreEqual(0, fixture.StandardClients.CreateCount, "The standard client must not be used when a certificate is sent.");
        }

        [TestMethod]
        public async Task CertificateInHeader_IsNotForwardedToTarget()
        {
            await using var fixture = new Fixture();
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            var request = fixture.CreateRequest(certificate);
            request.Headers.Add("Accept-Language", "nb");

            await fixture.SendAsync(request);

            var received = fixture.Server.Requests.Single();
            Assert.AreEqual("GET /some/path HTTP/1.1", received.RequestLine);
            Assert.AreEqual("nb", received.Headers["Accept-Language"], "Ordinary headers are forwarded.");
            Assert.IsFalse(received.Headers.ContainsKey(CertificateHeader), "The certificate and its private key must stay in the proxy.");
        }

        [TestMethod]
        public async Task RequestWithoutCertificate_DoesNotGetTheCertificateConnection()
        {
            await using var fixture = new Fixture();
            var (certificate, thumbprint) = TestCertificates.CreateClientCertificate("client-a");

            var withCertificate = await fixture.SendAsync(fixture.CreateRequest(certificate));
            var withoutCertificate = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(thumbprint, withCertificate.Thumbprint);
            Assert.AreEqual(MtlsTestServer.NoCertificate, withoutCertificate.Thumbprint);
            Assert.AreNotEqual(withCertificate.ConnectionId, withoutCertificate.ConnectionId);
            Assert.AreEqual(1, fixture.StandardClients.CreateCount);
        }
    }
}
