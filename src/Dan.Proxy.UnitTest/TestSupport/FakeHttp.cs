using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Dan.Proxy.UnitTest.TestSupport
{
    /// <summary>The proxy never reads the function context, it is only needed to construct request and response.</summary>
    internal sealed class FakeFunctionContext : FunctionContext
    {
        public override string InvocationId => "test-invocation";
        public override string FunctionId => "test-function";
        public override TraceContext TraceContext => throw new NotSupportedException();
        public override BindingContext BindingContext => throw new NotSupportedException();
        public override Microsoft.Azure.Functions.Worker.RetryContext RetryContext => throw new NotSupportedException();
        public override IServiceProvider InstanceServices { get; set; } = null!;
        public override FunctionDefinition FunctionDefinition => throw new NotSupportedException();
        public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();
        public override IInvocationFeatures Features => throw new NotSupportedException();
    }

    internal sealed class FakeHttpRequestData : HttpRequestData
    {
        public FakeHttpRequestData(Uri url, string method = "GET") : base(new FakeFunctionContext())
        {
            Url = url;
            Method = method;
        }

        public override Stream Body { get; } = new MemoryStream();
        public override HttpHeadersCollection Headers { get; } = new();
        public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = Array.Empty<IHttpCookie>();
        public override Uri Url { get; }
        public override IEnumerable<ClaimsIdentity> Identities { get; } = Array.Empty<ClaimsIdentity>();
        public override string Method { get; }

        public override HttpResponseData CreateResponse() => new FakeHttpResponseData(FunctionContext);
    }

    internal sealed class FakeHttpResponseData : HttpResponseData
    {
        public FakeHttpResponseData(FunctionContext functionContext) : base(functionContext)
        {
        }

        public override HttpStatusCode StatusCode { get; set; }
        public override HttpHeadersCollection Headers { get; set; } = new();
        public override Stream Body { get; set; } = new MemoryStream();
        public override HttpCookies Cookies => throw new NotSupportedException();

        public string BodyAsString() => Encoding.UTF8.GetString(((MemoryStream)Body).ToArray());
    }

    /// <summary>
    /// Stands in for the IHttpClientFactory used by the standard proxy branch. Counts how often
    /// it is asked for a client, and hands out clients that share one handler without a client certificate.
    /// </summary>
    internal sealed class RecordingHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly SocketsHttpHandler _handler;
        private int _createCount;

        public RecordingHttpClientFactory(Action<SocketsHttpHandler> configureHandler)
        {
            _handler = new SocketsHttpHandler();
            configureHandler(_handler);
        }

        public int CreateCount => Volatile.Read(ref _createCount);

        public HttpClient CreateClient(string name)
        {
            Interlocked.Increment(ref _createCount);
            return new HttpClient(_handler, disposeHandler: false);
        }

        public void Dispose() => _handler.Dispose();
    }
}
