﻿using System;
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
    }
}
