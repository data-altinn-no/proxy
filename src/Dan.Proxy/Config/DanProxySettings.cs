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
        /// Komma-separert liste over hostnavn der sertifikatvalidering skal ignoreres.
        /// Gjelder kun når IgnoreCertificateValidation = false (ellers ignoreres validering globalt).
        /// Eksempel: "nordicinformation.api.prh.fi, annet.host.no" (Finsk API for NSG).
        /// </summary>
        public string IgnoreCertificateValidationHosts { get; set; } = string.Empty;
        public string[] IgnoreCertificateValidationHostsList =>
            IgnoreCertificateValidationHosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
