using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
                // All RFC 1918 172.16.0.0/12 subnets
                "172.16.*", "172.17.*", "172.18.*", "172.19.*",
                "172.20.*", "172.21.*", "172.22.*", "172.23.*",
                "172.24.*", "172.25.*", "172.26.*", "172.27.*",
                "172.28.*", "172.29.*", "172.30.*", "172.31.*",
                // RFC 1918 192.168.0.0/16
                "192.168.*",
                // Link-local & CGNAT
                "169.254.*",
                "100.*",
                // Rosseti Ural & regional enterprise public subnets
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
                // Russian TLDs (always direct)
                "*.ru",
                "*.рф",
                "*.su",
                "*.xn--p1ai"
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

        private static byte[] ConstructConnectionBlob(bool enable, string proxyServer, string proxyOverride)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // 0x00: struct size (70 bytes min or header)
            bw.Write((int)70);
            // 0x04: counter / sequence number
            bw.Write((int)1);
            // 0x08: flags: 0x01 = direct, 0x03 = manual proxy enabled
            bw.Write((int)(enable ? 3 : 1));

            // 0x0C: proxy server string
            var proxyBytes = enable ? Encoding.ASCII.GetBytes(proxyServer) : Array.Empty<byte>();
            bw.Write((int)proxyBytes.Length);
            if (proxyBytes.Length > 0) bw.Write(proxyBytes);

            // proxy override string
            var overrideBytes = enable ? Encoding.ASCII.GetBytes(proxyOverride) : Array.Empty<byte>();
            bw.Write((int)overrideBytes.Length);
            if (overrideBytes.Length > 0) bw.Write(overrideBytes);

            // autoconfig url (empty)
            bw.Write((int)0);

            // 32 bytes reserved zeros
            bw.Write(new byte[32]);

            return ms.ToArray();
        }

        public static void SetProxy(bool enable, string host = "127.0.0.1", int port = 10809, IEnumerable<string>? customDomains = null)
        {
            try
            {
                const string subkey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
                var proxyServer = $"http={host}:{port};https={host}:{port}";
                var proxyOverride = BuildProxyOverride(customDomains);

                using (var key = Registry.CurrentUser.OpenSubKey(subkey, writable: true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
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

                // Update active connections blobs (DefaultConnectionSettings, SavedLegacySettings, and all VPN connections)
                const string connSubkey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections";
                using (var connKey = Registry.CurrentUser.OpenSubKey(connSubkey, writable: true))
                {
                    if (connKey != null)
                    {
                        var blob = ConstructConnectionBlob(enable, proxyServer, proxyOverride);

                        foreach (var valName in connKey.GetValueNames())
                        {
                            try
                            {
                                connKey.SetValue(valName, blob, RegistryValueKind.Binary);
                            }
                            catch { }
                        }
                    }
                }

                // Notify WinINet that settings changed so all browsers/apps apply it immediately
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
