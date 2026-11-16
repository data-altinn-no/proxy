using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Dan.Proxy.Interfaces;

namespace Dan.Proxy.Services
{
    /// <summary>
    /// Keeps one HttpClient per client certificate for the lifetime of the worker process.
    ///
    /// Creating a new HttpClient and handler per request gives every request an empty
    /// connection pool, so each call pays for a DNS lookup, a TCP connect and a TLS
    /// handshake, and leaves a socket behind until it is collected. Sharing the handler
    /// lets requests with the same certificate reuse open connections.
    ///
    /// Each certificate gets its own handler, and therefore its own connection pool, so a
    /// connection authenticated with one certificate is never used for a request that
    /// carries another.
    /// </summary>
    public sealed class ClientCertificateHttpClientProvider : IClientCertificateHttpClientProvider, IDisposable
    {
        // Connections are recycled after this long so that DNS changes are picked up.
        private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(10);

        // Kept below the 4 minute idle timeout of Azure outbound NAT.
        private static readonly TimeSpan ConnectionIdleTimeout = TimeSpan.FromMinutes(2);

        private readonly ConcurrentDictionary<string, Lazy<HttpClient>> _clients = new();
        private readonly Action<SocketsHttpHandler>? _configureHandler;

        public ClientCertificateHttpClientProvider() : this(null)
        {
        }

        // Lets tests adjust the handler, for example to trust a local test server.
        internal ClientCertificateHttpClientProvider(Action<SocketsHttpHandler>? configureHandler)
        {
            _configureHandler = configureHandler;
        }

        public HttpClient GetClient(string base64Certificate)
        {
            // Keyed on a hash of the header value, so the certificate is only parsed the
            // first time it is seen and the private key is not kept as a dictionary key.
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(base64Certificate)));

            var lazyClient = _clients.GetOrAdd(key, _ => new Lazy<HttpClient>(() => CreateClient(base64Certificate)));

            try
            {
                return lazyClient.Value;
            }
            catch
            {
                // Do not keep an entry for a header value that could not be turned into a client.
                _clients.TryRemove(new KeyValuePair<string, Lazy<HttpClient>>(key, lazyClient));
                throw;
            }
        }

        private HttpClient CreateClient(string base64Certificate)
        {
            var certificate = new X509Certificate2(Convert.FromBase64String(base64Certificate));

            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = ConnectionLifetime,
                PooledConnectionIdleTimeout = ConnectionIdleTimeout,
                // The handler is shared between requests, so it must not carry cookies from one request to the next.
                UseCookies = false,
                SslOptions = new SslClientAuthenticationOptions
                {
                    ClientCertificates = new X509CertificateCollection { certificate }
                }
            };

            _configureHandler?.Invoke(handler);

            return new HttpClient(handler, disposeHandler: true);
        }

        public void Dispose()
        {
            foreach (var lazyClient in _clients.Values)
            {
                if (lazyClient.IsValueCreated)
                {
                    lazyClient.Value.Dispose();
                }
            }

            _clients.Clear();
        }
    }
}
