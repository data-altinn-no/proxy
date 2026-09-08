using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dan.Proxy.Config
{
    public class DanProxySettings
    {
        public bool DebugMode { get; set; } = false;

        public string Ignored { get; set; } = string.Empty;
        public string[] IgnoredHeaders => Ignored.Split(',', StringSplitOptions.RemoveEmptyEntries);

        public bool IgnoreCertificateValidation { get; set; } = true;
        public string CustomCertificateHeaderName { get; set; } = string.Empty;
        /// <summary>
        /// Kommaseparert liste over hostnavn hvor sertifikatvalidering skal ignoreres
        /// F.eks. "nordicinformation.api.prh.fi,annet.host.no", Finnsk api for nsg.
        /// </summary>
        public string IgnoreCertificateValidationHosts { get; set; } = string.Empty;
        public string[] IgnoreCertificateValidationHostsList =>
            IgnoreCertificateValidationHosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
