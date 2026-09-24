using System;
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

        public static void SetProxy(bool enable, string host = "127.0.0.1", int port = 10809)
        {
            try
            {
                const string userRoot = "HKEY_CURRENT_USER";
                const string subkey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
                const string keyName = userRoot + "\\" + subkey;

                if (enable)
                {
                    Registry.SetValue(keyName, "ProxyEnable", 1, RegistryValueKind.DWord);
                    Registry.SetValue(keyName, "ProxyServer", $"http={host}:{port};https={host}:{port}", RegistryValueKind.String);
                    Registry.SetValue(keyName, "ProxyOverride", "localhost;127.*;10.*;172.16.*;192.168.*;<local>", RegistryValueKind.String);
                }
                else
                {
                    Registry.SetValue(keyName, "ProxyEnable", 0, RegistryValueKind.DWord);
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
