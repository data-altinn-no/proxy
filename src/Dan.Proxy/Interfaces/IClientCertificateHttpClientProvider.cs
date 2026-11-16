namespace Dan.Proxy.Interfaces
{
    public interface IClientCertificateHttpClientProvider
    {
        /// <summary>
        /// Returns a long-lived HttpClient that presents the given client certificate.
        /// The same certificate always yields the same client, so its connections are reused.
        /// The returned client is shared and must not be disposed by the caller.
        /// </summary>
        /// <param name="base64Certificate">The certificate (with private key) as received in the certificate header.</param>
        HttpClient GetClient(string base64Certificate);
    }
}
