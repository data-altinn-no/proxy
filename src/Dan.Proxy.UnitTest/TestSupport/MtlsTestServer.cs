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
        IReadOnlyDictionary<string, string> Headers);

    /// <summary>
    /// A local HTTPS server that asks for a client certificate, counts TCP connections and
    /// records every request it gets. Each response body is "{connection id}|{thumbprint}",
    /// where the thumbprint is that of the client certificate on the connection, or NONE.
    /// Every response also sets a cookie, so tests can see whether a client sends it back.
    /// </summary>
    internal sealed class MtlsTestServer : IAsyncDisposable
    {
        public const string NoCertificate = "NONE";

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _certificate = TestCertificates.CreateServerCertificate();
        private readonly ConcurrentQueue<ReceivedRequest> _requests = new();
        private readonly Task _acceptLoop;
        private int _connectionCount;

        public MtlsTestServer()
        {
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

        /// <summary>Server certificate validation for test clients: trust exactly this server's certificate.</summary>
        public bool IsServerCertificate(X509Certificate? certificate) =>
            certificate is not null && certificate.GetCertHashString() == _certificate.GetCertHashString();

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
                await using (var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true))
                {
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _certificate,
                            ClientCertificateRequired = true
                        },
                        _stop.Token);

                    var thumbprint = ssl.RemoteCertificate?.GetCertHashString();
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

                        received.Remove(0, requestLength);
                        _requests.Enqueue(new ReceivedRequest(connectionId, thumbprint, lines[0], headers));

                        var body = $"{connectionId}|{thumbprint ?? NoCertificate}";
                        var response =
                            "HTTP/1.1 200 OK\r\n" +
                            "Content-Type: text/plain\r\n" +
                            "Set-Cookie: session=set-by-test-server; Path=/\r\n" +
                            $"Content-Length: {body.Length}\r\n" +
                            "\r\n" +
                            body;
                        await ssl.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                    }
                }
            }
            catch (Exception)
            {
                // The client went away or the server is shutting down. Tests assert on what was recorded.
            }
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
