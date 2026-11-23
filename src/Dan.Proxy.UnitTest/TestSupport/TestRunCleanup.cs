namespace Dan.Proxy.UnitTest.TestSupport
{
    [TestClass]
    public sealed class TestRunCleanup
    {
        /// <summary>
        /// On Windows, a certificate loaded with its private key keeps the key in a file in the
        /// user's profile until the certificate is released. The proxy leaves that to the garbage
        /// collector, which does not get to finish when the test process exits. Collecting here
        /// keeps a test run from leaving key files behind.
        /// </summary>
        [AssemblyCleanup]
        public static void ReleaseCertificates()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
