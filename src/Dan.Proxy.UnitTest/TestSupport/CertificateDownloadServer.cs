using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Dan.Proxy.UnitTest.TestSupport
{
    /// <summary>
    /// A local HTTP server that plays the part of a certificate authority's download site,
    /// such as crt.buypassca.com. Certificates point to it in their Authority Information
    /// Access extension, and it counts how many times a certificate is fetched.
    /// </summary>
    internal sealed class CertificateDownloadServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<string, byte[]> _certificates = new();
        private readonly Task _acceptLoop;
        private volatile bool _available = true;
        private int _downloads;
        private int _requests;
        private readonly ConcurrentQueue<string> _requestedPaths = new();

        public CertificateDownloadServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        /// <summary>False makes every download fail with 404, as when the site cannot be reached.</summary>
        public bool Available
        {
            get => _available;
            set => _available = value;
        }

        /// <summary>The number of requests received, answered or not.</summary>
        public int Requests => Volatile.Read(ref _requests);

        /// <summary>The paths that have been asked for, found or not.</summary>
        public IReadOnlyList<string> RequestedPaths => _requestedPaths.ToArray();

        /// <summary>The number of certificates handed out.</summary>
        public int Downloads => Volatile.Read(ref _downloads);

        public string UrlOf(string fileName) => $"http://127.0.0.1:{Port}/{fileName}";

        public void Publish(string fileName, X509Certificate2 certificate) =>
            _certificates[$"/{fileName}"] = certificate.RawData;

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                // Shutting down.
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    Interlocked.Increment(ref _requests);
                    var stream = client.GetStream();
                    var buffer = new byte[8 * 1024];
                    var received = new StringBuilder();

                    while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0) return;
                        received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    // The request line is "GET /file HTTP/1.1".
                    var path = received.ToString().Split(' ')[1];
                    _requestedPaths.Enqueue(path);

                    if (Available && _certificates.TryGetValue(path, out var certificate))
                    {
                        Interlocked.Increment(ref _downloads);
                        var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/pkix-cert\r\nContent-Length: {certificate.Length}\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync((byte[])[.. Encoding.ASCII.GetBytes(head), .. certificate], _stop.Token);
                    }
                    else
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                    }
                }
            }
            catch (Exception)
            {
                // The client went away or the server is shutting down.
            }
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
            _stop.Dispose();
        }
    }
}
