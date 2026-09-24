using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace bksh2ray
{
    public static class SystemProxy
    {
        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        public static string BuildProxyOverride(IEnumerable<string>? customDomains = null)
        {
            var list = new List<string>
            {
                "<local>",
                "localhost",
                "127.*",
                "10.*",
                // All RFC 1918 172.16.0.0/12 subnets (corporate VPN / Wi-Fi)
                "172.16.*", "172.17.*", "172.18.*", "172.19.*",
                "172.20.*", "172.21.*", "172.22.*", "172.23.*",
                "172.24.*", "172.25.*", "172.26.*", "172.27.*",
                "172.28.*", "172.29.*", "172.30.*", "172.31.*",
                "192.168.*",
                "169.254.*",
                "100.*",
                "217.65.83.*",
                "109.202.29.*",
                // Intranet and Local TLDs
                "*.local",
                "*.corp",
                "*.lan",
                "*.home",
                "*.internal",
                "*.intra",
                "*.domain",
                // Corporate domains
                "*.sp.local",
                "*.rosseti-ural.ru",
                "*.mrsk-ural.ru",
                "*.rosseti.ru",
                "*.cplus.ru",
                "*.teleofis.ru",
                "*.tpk-stimul.com",
                "*.geohide.ru",
                "geohide.ru"
            };

            if (customDomains != null)
            {
                foreach (var d in customDomains)
                {
                    var clean = d.Trim().TrimStart('.').Trim();
                    if (!string.IsNullOrEmpty(clean))
                    {
                        var wildcard = clean.StartsWith("*.") ? clean : $"*.{clean}";
                        if (!list.Contains(wildcard)) list.Add(wildcard);
                        if (!list.Contains(clean)) list.Add(clean);
                    }
                }
            }

            return string.Join(";", list);
        }

        public static void SetProxy(bool enable, string host = "127.0.0.1", int port = 10809, IEnumerable<string>? customDomains = null)
        {
            try
            {
                const string subkey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
                using (var key = Registry.CurrentUser.OpenSubKey(subkey, writable: true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
                            var proxyServer = $"http={host}:{port};https={host}:{port}";
                            var proxyOverride = BuildProxyOverride(customDomains);

                            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                            key.SetValue("ProxyServer", proxyServer, RegistryValueKind.String);
                            key.SetValue("ProxyOverride", proxyOverride, RegistryValueKind.String);
                        }
                        else
                        {
                            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                        }
                    }
                }

                // Notify WinINet so changes apply instantly to all browsers, VPN connections and network adapters
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Proxy] Error setting system proxy: {ex.Message}");
            }
        }
    }
}
