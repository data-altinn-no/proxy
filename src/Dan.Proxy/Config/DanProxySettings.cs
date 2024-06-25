using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dan.Proxy.Config
{
    public class DanProxySettings
    {
        public string validEndPoints { get; set; } = string.Empty;

        public bool DebugMode { get; set; } = false;
    }
}
