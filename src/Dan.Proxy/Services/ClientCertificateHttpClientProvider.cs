using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Dan.Proxy.Interfaces;

namespace Dan.Proxy.Services
{
    /// <summary>
    /// Keeps one HttpClient per certificate header value for the lifetime of the worker process.
    ///
    /// Creating a new HttpClient and handler per request gives every request an empty
    /// connection pool, so each call pays for a DNS lookup, a TCP connect and a TLS
    /// handshake, and leaves a socket behind until it is collected. Sharing the handler
    /// lets requests with the same header value reuse open connections.
    ///
    /// Each header value gets its own handler, and therefore its own connection pool, so a
    /// connection authenticated with one certificate is never used for a request that
    /// carries another.
    ///
    /// Callers must send exactly the same header value on every request to get reuse. A PFX
    /// that is exported again is a different value, because every export contains new random
    /// salt. The cache is therefore limited to <see cref="MaxClients"/> clients, so a caller
    /// that sends a new value on every request cannot make it grow without bound.
    /// </summary>
    public sealed class ClientCertificateHttpClientProvider : IClientCertificateHttpClientProvider, IDisposable
    {
        internal const int MaxClients = 20;

        // Connections are recycled after this long so that DNS changes are picked up.
        private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(10);

        // Kept below the 4 minute idle timeout of Azure outbound NAT.
        private static readonly TimeSpan ConnectionIdleTimeout = TimeSpan.FromMinutes(2);

        private readonly ConcurrentDictionary<string, CachedClient> _clients = new();
        private readonly Action<SocketsHttpHandler>? _configureHandler;
        private long _useCounter;

        public ClientCertificateHttpClientProvider() : this(null)
        {
        }

        // Lets tests adjust the handler, for example to trust a local test server.
        internal ClientCertificateHttpClientProvider(Action<SocketsHttpHandler>? configureHandler)
        {
            _configureHandler = configureHandler;
        }

        internal int CachedClientCount => _clients.Count;

        public HttpClient GetClient(string base64Certificate)
        {
            // Keyed on a hash of the header value, so the certificate is only parsed the
            // first time it is seen and the private key is not kept as a dictionary key.
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(base64Certificate)));

            if (!_clients.TryGetValue(key, out var cached))
            {
                // Created before anything is dropped, so a header value that cannot be
                // turned into a client throws here and costs no existing client its place.
                var created = CreateClient(base64Certificate);
                MakeRoomForOneMore();

                cached = _clients.GetOrAdd(key, created);
                if (!ReferenceEquals(cached, created))
                {
                    // Another request added the same header value first. This one was never handed out.
                    created.Dispose();
                }
            }

            cached.LastUsed = Interlocked.Increment(ref _useCounter);
            return cached.Client;
        }

        private void MakeRoomForOneMore()
        {
            while (_clients.Count >= MaxClients)
            {
                var usedLongestAgo = _clients.MinBy(entry => entry.Value.LastUsed);
                if (usedLongestAgo.Key is null)
                {
                    return;
                }

                // Dropped, not disposed: a request may still be using the client. Its idle
                // connections close after ConnectionIdleTimeout, and the rest is left to the
                // garbage collector.
                _clients.TryRemove(usedLongestAgo);
            }
        }

        private CachedClient CreateClient(string base64Certificate)
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

            return new CachedClient(new HttpClient(handler, disposeHandler: true), certificate);
        }

        public void Dispose()
        {
            foreach (var cached in _clients.Values)
            {
                cached.Dispose();
            }

            _clients.Clear();
        }

        private sealed class CachedClient : IDisposable
        {
            private long _lastUsed;

            public CachedClient(HttpClient client, X509Certificate2 certificate)
            {
                Client = client;
                Certificate = certificate;
            }

            public HttpClient Client { get; }

            private X509Certificate2 Certificate { get; }

            // A counter rather than a clock, so the order is exact even for requests in the same millisecond.
            public long LastUsed
            {
                get => Interlocked.Read(ref _lastUsed);
                set => Interlocked.Exchange(ref _lastUsed, value);
            }

            public void Dispose()
            {
                Client.Dispose();
                Certificate.Dispose();
            }
        }
    }
}
