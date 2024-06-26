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

        private string Ignored = string.Empty;
        public string[] IgnoredHeaders => Ignored.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }
}
