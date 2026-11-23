using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Dan.Proxy.UnitTest.TestSupport
{
    internal sealed record ReceivedRequest(
        int ConnectionId,
        string? ClientCertificateThumbprint,
        string RequestLine,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body);

    internal sealed record TestResponse(int StatusCode, IReadOnlyList<(string Name, string Value)> Headers, byte[] Body);

    /// <summary>
    /// A local HTTPS server that asks for a client certificate, counts TCP connections and
    /// records every request it gets. Unless <see cref="Respond"/> is set, each response body
    /// is "{connection id}|{thumbprint}", where the thumbprint is that of the client certificate
    /// on the connection, or NONE, and the response sets a cookie so tests can see whether a
    /// client sends it back.
    /// </summary>
    internal sealed class MtlsTestServer : IAsyncDisposable
    {
        public const string NoCertificate = "NONE";

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _certificate = TestCertificates.CreateServerCertificate();
        private readonly ConcurrentQueue<ReceivedRequest> _requests = new();
        private readonly ConcurrentDictionary<string, bool> _certificatesFromClients = new();
        private readonly ConcurrentQueue<string> _handshakes = new();
        private readonly Task _acceptLoop;
        private int _connectionCount;

        public MtlsTestServer()
        {
            Stopping = _stop.Token;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        /// <summary>Host and port without scheme, as the proxy expects it in the url query parameter.</summary>
        public string Authority => $"127.0.0.1:{Port}";

        public Uri BaseAddress => new($"https://{Authority}/");

        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public IReadOnlyList<ReceivedRequest> Requests => _requests.ToArray();

        /// <summary>
        /// One entry per completed TLS handshake: the thumbprint of the client certificate, or
        /// NONE. Also has the connections that never got as far as sending a request.
        /// </summary>
        public IReadOnlyList<string> Handshakes => _handshakes.ToArray();

        /// <summary>Replaces the default response. May wait on <see cref="Stopping"/> to act as a slow target.</summary>
        public Func<ReceivedRequest, Task<TestResponse>>? Respond { get; set; }

        /// <summary>Cancelled when the server is disposed.</summary>
        public CancellationToken Stopping { get; }

        /// <summary>Server certificate validation for test clients: trust exactly this server's certificate.</summary>
        public bool IsServerCertificate(X509Certificate? certificate) =>
            certificate is not null && certificate.GetCertHashString() == _certificate.GetCertHashString();

        /// <summary>True when a client has sent this certificate along with its own during a handshake.</summary>
        public bool WasSentByClient(string thumbprint) => _certificatesFromClients.ContainsKey(thumbprint);

        // Accepts every client certificate. The certificates the client sent along with its own are in the extra store.
        private bool RecordCertificatesFromClient(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            foreach (var sent in chain?.ChainPolicy.ExtraStore ?? [])
            {
                _certificatesFromClients[sent.Thumbprint] = true;
            }

            return true;
        }

        public static (int ConnectionId, string Thumbprint) ParseResponseBody(string body)
        {
            var parts = body.Split('|');
            return (int.Parse(parts[0]), parts[1]);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var connectionId = Interlocked.Increment(ref _connectionCount);
                    _ = Task.Run(() => ServeAsync(client, connectionId));
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                // Shutting down.
            }
        }

        private async Task ServeAsync(TcpClient client, int connectionId)
        {
            try
            {
                using (client)
                await using (var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, RecordCertificatesFromClient))
                {
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _certificate,
                            ClientCertificateRequired = true,
                            // The server must not fetch the client's chain itself. That would hide
                            // whether the client sent it, and count as a download in the tests.
                            CertificateChainPolicy = new X509ChainPolicy
                            {
                                DisableCertificateDownloads = true,
                                RevocationMode = X509RevocationMode.NoCheck
                            }
                        },
                        _stop.Token);

                    var thumbprint = ssl.RemoteCertificate?.GetCertHashString();
                    _handshakes.Enqueue(thumbprint ?? NoCertificate);
                    var buffer = new byte[16 * 1024];
                    var received = new StringBuilder();

                    while (true)
                    {
                        int headerEnd;
                        while ((headerEnd = received.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
                        {
                            if (!await ReadMoreAsync(ssl, buffer, received)) return;
                        }

                        var lines = received.ToString(0, headerEnd).Split("\r\n");
                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var line in lines.Skip(1))
                        {
                            var separator = line.IndexOf(':');
                            if (separator > 0)
                            {
                                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                            }
                        }

                        var bodyLength = headers.TryGetValue("Content-Length", out var contentLength) ? int.Parse(contentLength) : 0;
                        var requestLength = headerEnd + 4 + bodyLength;
                        while (received.Length < requestLength)
                        {
                            if (!await ReadMoreAsync(ssl, buffer, received)) return;
                        }

                        var requestBody = Encoding.Latin1.GetBytes(received.ToString(headerEnd + 4, bodyLength));
                        received.Remove(0, requestLength);

                        var request = new ReceivedRequest(connectionId, thumbprint, lines[0], headers, requestBody);
                        _requests.Enqueue(request);

                        var response = Respond is null ? DefaultResponse(connectionId, thumbprint) : await Respond(request);
                        await ssl.WriteAsync(Serialize(response), _stop.Token);
                    }
                }
            }
            catch (Exception)
            {
                // The client went away or the server is shutting down. Tests assert on what was recorded.
            }
        }

        private static TestResponse DefaultResponse(int connectionId, string? thumbprint) =>
            new(200,
                [("Content-Type", "text/plain"), ("Set-Cookie", "session=set-by-test-server; Path=/")],
                Encoding.ASCII.GetBytes($"{connectionId}|{thumbprint ?? NoCertificate}"));

        private static byte[] Serialize(TestResponse response)
        {
            var head = new StringBuilder($"HTTP/1.1 {response.StatusCode} Test\r\n");
            foreach (var (name, value) in response.Headers)
            {
                head.Append($"{name}: {value}\r\n");
            }

            head.Append($"Content-Length: {response.Body.Length}\r\n\r\n");
            return [.. Encoding.ASCII.GetBytes(head.ToString()), .. response.Body];
        }

        private async Task<bool> ReadMoreAsync(SslStream ssl, byte[] buffer, StringBuilder received)
        {
            var read = await ssl.ReadAsync(buffer, _stop.Token);
            if (read == 0) return false;

            // Latin1 keeps one char per byte, so lengths in the builder are byte counts.
            received.Append(Encoding.Latin1.GetString(buffer, 0, read));
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();

            try
            {
                await _acceptLoop;
            }
            catch (Exception)
            {
                // Already stopping.
            }

            _listener.Dispose();
            _certificate.Dispose();
            _stop.Dispose();
        }
    }
}
