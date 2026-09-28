using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bksh2ray
{
    public class XrayManager
    {
        private readonly string _appDir;
        private readonly string _binDir;
        private readonly string _xrayExe;
        private readonly string _singBoxExe;
        private readonly string _configFile;
        private readonly string _singBoxConfigFile;

        private readonly List<string> _addedRoutes = new();

        private Process? _process;
        private Process? _singBoxProcess;
        private CancellationTokenSource? _statsCts;

        private static int _tunSessionCounter = Environment.TickCount & 0x7FFFFFFF;
        private string _currentTunInterface = "singbox_tun_0";
        private readonly object _startStopLock = new();

        private static readonly System.Text.RegularExpressions.Regex AnsiRegex =
            new(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", System.Text.RegularExpressions.RegexOptions.Compiled);

        public static string StripAnsi(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return AnsiRegex.Replace(text, "");
        }

        public bool IsRunning => _process != null && !_process.HasExited;
        public string CurrentMode { get; private set; } = "proxy";
        public int SocksPort { get; private set; } = 10808;
        public int HttpPort { get; private set; } = 10809;
        public int ApiPort { get; private set; } = 10085;

        public event Action<string>? LogReceived;
        public event Action<double, double, long, long>? SpeedUpdated;
        public event Action? StateChanged;

        public XrayManager(string appDir)
        {
            _appDir = appDir;
            _binDir = Path.Combine(_appDir, "bin");
            _xrayExe = Path.Combine(_binDir, "xray.exe");
            _singBoxExe = Path.Combine(_binDir, "sing-box.exe");
            _configFile = Path.Combine(_appDir, "xray_config.json");
            _singBoxConfigFile = Path.Combine(_appDir, "singbox_config.json");
        }

        public static int FindFreePort(int startPort, int maxTries = 50)
        {
            var activeListeners = new HashSet<int>();
            try
            {
                var endpoints = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                foreach (var ep in endpoints)
                {
                    activeListeners.Add(ep.Port);
                }
            }
            catch { }

            for (int p = startPort; p < startPort + maxTries; p++)
            {
                if (activeListeners.Contains(p))
                    continue;

                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, p);
                    listener.ExclusiveAddressUse = true;
                    listener.Start();
                    listener.Stop();
                    return p;
                }
                catch
                {
                    // Port occupied, try next
                }
            }
            return startPort + maxTries;
        }

        public void GenerateConfig(AppConfig config)
        {
            if (config.ActiveServerIndex < 0 || config.ActiveServerIndex >= config.Servers.Count)
                throw new InvalidOperationException("Сервер не выбран");

            var vless = config.Servers[config.ActiveServerIndex];

            SocksPort = FindFreePort(10808);
            HttpPort = FindFreePort(SocksPort != 10809 ? 10809 : 10810);
            if (HttpPort == SocksPort)
                HttpPort = FindFreePort(SocksPort + 1);
            ApiPort = FindFreePort(10085);

            // VLESS Outbound
            var vnextUser = new Dictionary<string, object>
            {
                ["id"] = vless.Uuid,
                ["encryption"] = "none"
            };
            if (!string.IsNullOrEmpty(vless.Flow))
                vnextUser["flow"] = vless.Flow;

            var streamSettings = new Dictionary<string, object>
            {
                ["network"] = string.IsNullOrEmpty(vless.Type) ? "tcp" : vless.Type
            };

            var sec = string.IsNullOrEmpty(vless.Security) ? "none" : vless.Security;
            if (sec == "reality")
            {
                streamSettings["security"] = "reality";
                var reality = new Dictionary<string, object>
                {
                    ["show"] = false,
                    ["fingerprint"] = string.IsNullOrEmpty(vless.Fp) ? "chrome" : vless.Fp,
                    ["serverName"] = vless.Sni ?? "",
                    ["publicKey"] = vless.Pbk ?? "",
                    ["shortId"] = vless.Sid ?? ""
                };
                if (!string.IsNullOrEmpty(vless.Spx))
                    reality["spiderX"] = vless.Spx;
                streamSettings["realitySettings"] = reality;
            }
            else if (sec == "tls")
            {
                streamSettings["security"] = "tls";
                streamSettings["tlsSettings"] = new Dictionary<string, object>
                {
                    ["serverName"] = vless.Sni ?? "",
                    ["fingerprint"] = string.IsNullOrEmpty(vless.Fp) ? "chrome" : vless.Fp
                };
            }

            var outboundProxy = new Dictionary<string, object>
            {
                ["protocol"] = "vless",
                ["tag"] = "proxy",
                ["settings"] = new Dictionary<string, object>
                {
                    ["vnext"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["address"] = vless.Server,
                            ["port"] = vless.ServerPort,
                            ["users"] = new[] { vnextUser }
                        }
                    }
                },
                ["streamSettings"] = streamSettings
            };

            // Direct domains: corporate, private, local, all Russian TLDs + custom domains
            var directDomains = new List<string>
            {
                "geosite:private",
                "domain:local",
                "domain:corp",
                "domain:lan",
                "domain:home",
                "domain:internal",
                "domain:intra",
                "domain:sp.local",
                "domain:rosseti-ural.ru",
                "domain:mrsk-ural.ru",
                "domain:rosseti.ru",
                "domain:cplus.ru",
                "domain:teleofis.ru",
                "domain:tpk-stimul.com",
                "domain:geohide.ru",
                "domain:dns.geohide.ru",
                "domain:ru",
                "domain:su",
                "domain:рф",
                "domain:xn--p1ai",
                "geosite:category-ru"
            };

            foreach (var d in config.CustomDirectDomains)
            {
                var clean = d.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(clean))
                {
                    var rule = clean.StartsWith("domain:") || clean.StartsWith("geosite:") || clean.StartsWith("regexp:")
                        ? clean
                        : $"domain:{clean}";
                    if (!directDomains.Contains(rule))
                        directDomains.Add(rule);
                }
            }

            // Direct IPs: corporate subnets, private subnets (RFC 1918, CGNAT, link-local), GeoHide DNS IPs
            var directIps = new List<string>
            {
                "geoip:private",
                "10.0.0.0/8",
                "172.16.0.0/12",
                "192.168.0.0/16",
                "100.64.0.0/10",
                "169.254.0.0/16",
                "127.0.0.0/8",
                "217.65.83.0/24",
                "109.202.29.0/24",
                "193.233.112.0/24",
                "45.155.204.0/24",
                "46.8.158.0/24",
                "37.230.192.0/24"
            };

            var rules = new List<Dictionary<string, object>>
            {
                new() { ["type"] = "field", ["inboundTag"] = new[] { "api" }, ["outboundTag"] = "api" },
                new() { ["type"] = "field", ["port"] = "53", ["outboundTag"] = "dns-out" },
                // Block QUIC (UDP 443) locally so browsers immediately fall back to TCP (prevents YouTube 5s hang)
                new() { ["type"] = "field", ["port"] = "443", ["network"] = "udp", ["outboundTag"] = "block" },
                new() { ["type"] = "field", ["outboundTag"] = "direct", ["protocol"] = new[] { "bittorrent" } },
                new() { ["type"] = "field", ["outboundTag"] = "block", ["domain"] = new[] { "geosite:category-ads-all" } },
                // Explicit proxy domains (Google, YouTube, Gemini, AI, developer tools)
                // MUST go through proxy - guaranteed to never route to direct or geoip:ru (prevents real IP leaks)
                new()
                {
                    ["type"] = "field",
                    ["outboundTag"] = "proxy",
                    ["domain"] = new[]
                    {
                        "geosite:google",
                        "geosite:youtube",
                        "geosite:openai",
                        "geosite:anthropic",
                        "geosite:github",
                        "domain:googleapis.com",
                        "domain:google.com",
                        "domain:gstatic.com",
                        "domain:youtube.com",
                        "domain:googlevideo.com",
                        "domain:ytimg.com",
                        "domain:ggpht.com",
                        "domain:gvt1.com",
                        "domain:gvt2.com",
                        "domain:gemini.google.com",
                        "domain:cloudaicompanion.googleapis.com",
                        "domain:deepmind.google",
                        "domain:deepmind.com",
                        "domain:cloud.google.com",
                        "domain:googlecloud.com",
                        "domain:googleusercontent.com",
                        "domain:1e100.net",
                        "domain:g.co",
                        "domain:goog",
                        "domain:openai.com",
                        "domain:anthropic.com",
                        "domain:claude.ai",
                        "domain:cursor.sh",
                        "domain:cursor.com",
                        "domain:github.com",
                        "domain:githubusercontent.com",
                        "domain:githubassets.com"
                    }
                },
                // 1. Corporate, LAN & Private IPs, GeoHide IPs -> Direct
                new()
                {
                    ["type"] = "field",
                    ["outboundTag"] = "direct",
                    ["ip"] = directIps
                },
                // 2. Corporate, Local, RU, GeoHide and Custom Domains -> Direct
                new()
                {
                    ["type"] = "field",
                    ["outboundTag"] = "direct",
                    ["domain"] = directDomains
                },
                // 3. Direct connections to Russian IPs (direct IP traffic only)
                new()
                {
                    ["type"] = "field",
                    ["outboundTag"] = "direct",
                    ["ip"] = new[] { "geoip:ru" }
                },
                // 4. Everything else (YouTube, Gemini, blocked & foreign sites) -> Proxy (VLESS)
                new()
                {
                    ["type"] = "field",
                    ["outboundTag"] = "proxy",
                    ["network"] = "tcp,udp"
                }
            };

            var xrayConfig = new Dictionary<string, object>
            {
                ["log"] = new Dictionary<string, object> { ["loglevel"] = "warning" },
                ["stats"] = new Dictionary<string, object>(),
                ["api"] = new Dictionary<string, object>
                {
                    ["tag"] = "api",
                    ["services"] = new[] { "StatsService" }
                },
                ["policy"] = new Dictionary<string, object>
                {
                    ["levels"] = new Dictionary<string, object>
                    {
                        ["0"] = new Dictionary<string, object>
                        {
                            ["statsUserUplink"] = true,
                            ["statsUserDownlink"] = true
                        }
                    },
                    ["system"] = new Dictionary<string, object>
                    {
                        ["statsInboundUplink"] = true,
                        ["statsInboundDownlink"] = true,
                        ["statsOutboundUplink"] = true,
                        ["statsOutboundDownlink"] = true
                    }
                },
                ["dns"] = new Dictionary<string, object>
                {
                    ["hosts"] = new Dictionary<string, object>
                    {
                        ["dns.geohide.ru"] = new[] { "193.233.112.68", "193.233.112.67", "193.233.112.88", "37.230.192.51", "45.155.204.190", "46.8.158.6" },
                        ["geohide.ru"] = new[] { "193.233.112.68", "193.233.112.67", "193.233.112.88", "37.230.192.51", "45.155.204.190", "46.8.158.6" }
                    },
                    ["queryStrategy"] = "UseIPv4",
                    ["servers"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["address"] = "localhost",
                            ["domains"] = directDomains,
                            ["skipFallback"] = true
                        },
                        new Dictionary<string, object>
                        {
                            ["address"] = "193.233.112.68",
                            ["port"] = 53,
                            ["domains"] = directDomains,
                            ["queryStrategy"] = "UseIPv4"
                        },
                        new Dictionary<string, object>
                        {
                            ["address"] = "8.8.8.8",
                            ["port"] = 53,
                            ["queryStrategy"] = "UseIPv4"
                        },
                        new Dictionary<string, object>
                        {
                            ["address"] = "1.1.1.1",
                            ["port"] = 53,
                            ["queryStrategy"] = "UseIPv4"
                        }
                    }
                },
                ["inbounds"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["port"] = ApiPort,
                        ["listen"] = "127.0.0.1",
                        ["protocol"] = "dokodemo-door",
                        ["settings"] = new Dictionary<string, object> { ["address"] = "127.0.0.1" },
                        ["tag"] = "api"
                    },
                    new Dictionary<string, object>
                    {
                        ["port"] = SocksPort,
                        ["listen"] = "127.0.0.1",
                        ["protocol"] = "socks",
                        ["settings"] = new Dictionary<string, object> { ["udp"] = true },
                        ["sniffing"] = new Dictionary<string, object>
                        {
                            ["enabled"] = true,
                            ["destOverride"] = new[] { "http", "tls" },
                            ["routeOnly"] = true
                        },
                        ["tag"] = "socks-in"
                    },
                    new Dictionary<string, object>
                    {
                        ["port"] = HttpPort,
                        ["listen"] = "127.0.0.1",
                        ["protocol"] = "http",
                        ["sniffing"] = new Dictionary<string, object>
                        {
                            ["enabled"] = true,
                            ["destOverride"] = new[] { "http", "tls" },
                            ["routeOnly"] = true
                        },
                        ["tag"] = "http-in"
                    }
                },
                ["outbounds"] = new object[]
                {
                    outboundProxy,
                    new Dictionary<string, object> { ["protocol"] = "freedom", ["tag"] = "direct" },
                    new Dictionary<string, object> { ["protocol"] = "blackhole", ["tag"] = "block" },
                    new Dictionary<string, object> { ["protocol"] = "dns", ["tag"] = "dns-out" }
                },
                ["routing"] = new Dictionary<string, object>
                {
                    ["domainStrategy"] = "IPIfNonMatch",
                    ["rules"] = rules
                }
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_configFile, JsonSerializer.Serialize(xrayConfig, options));
        }

        public static bool IsAdministrator()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public void GenerateSingBoxConfig(AppConfig config, string interfaceName = "singbox_tun_0", string tunAddress = "172.18.0.1/30", string tunDns = "172.18.0.2")
        {
            var directDomainSuffixes = new List<string>
            {
                "ru",
                "su",
                "xn--p1ai",
                "sp.local",
                "rosseti-ural.ru",
                "mrsk-ural.ru",
                "rosseti.ru",
                "cplus.ru",
                "teleofis.ru",
                "tpk-stimul.com",
                "geohide.ru",
                "dns.geohide.ru"
            };

            foreach (var d in config.CustomDirectDomains)
            {
                var clean = d.Trim().ToLowerInvariant().TrimStart('.');
                if (!string.IsNullOrEmpty(clean) && !directDomainSuffixes.Contains(clean))
                    directDomainSuffixes.Add(clean);
            }

            // Exclude direct IPs, LAN subnets, DNS, and server endpoints from TUN auto_route to prevent infinite loops
            var directIps = new List<string>
            {
                "127.0.0.0/8",
                "10.0.0.0/8",
                "172.16.0.0/12",
                "192.168.0.0/16",
                "169.254.0.0/16",
                "217.65.83.0/24",
                "109.202.29.0/24",
                "193.233.112.0/24",
                "45.155.204.0/24",
                "46.8.158.0/24",
                "37.230.192.0/24"
            };

            var routeExcludeAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "127.0.0.0/8",
                "10.0.0.0/8",
                "172.16.0.0/12",
                "192.168.0.0/16",
                "169.254.0.0/16",
                "217.65.83.0/24",
                "109.202.29.0/24",
                "193.233.112.0/24",
                "45.155.204.0/24",
                "46.8.158.0/24",
                "37.230.192.0/24"
            };

            if (config.Servers != null)
            {
                foreach (var s in config.Servers)
                {
                    if (string.IsNullOrWhiteSpace(s.Server)) continue;
                    var host = s.Server.Trim();

                    if (IPAddress.TryParse(host, out var ip))
                    {
                        if (ip.AddressFamily == AddressFamily.InterNetwork)
                        {
                            var cidr = $"{ip}/32";
                            routeExcludeAddresses.Add(cidr);
                            if (!directIps.Contains(cidr)) directIps.Add(cidr);
                        }
                    }
                    else
                    {
                        var lowerHost = host.ToLowerInvariant();
                        if (!directDomainSuffixes.Contains(lowerHost))
                            directDomainSuffixes.Add(lowerHost);

                        try
                        {
                            var ips = Dns.GetHostAddresses(host);
                            foreach (var a in ips)
                            {
                                if (a.AddressFamily == AddressFamily.InterNetwork)
                                {
                                    var cidr = $"{a}/32";
                                    routeExcludeAddresses.Add(cidr);
                                    if (!directIps.Contains(cidr)) directIps.Add(cidr);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            var singBoxLogFile = Path.Combine(_appDir, "singbox.log");

            var singBoxConfig = new Dictionary<string, object>
            {
                ["log"] = new Dictionary<string, object>
                {
                    ["disabled"] = false,
                    ["level"] = "info",
                    ["timestamp"] = true
                },
                ["dns"] = new Dictionary<string, object>
                {
                    ["servers"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["tag"] = "dns-direct",
                            ["type"] = "local"
                        },
                        new Dictionary<string, object>
                        {
                            ["tag"] = "dns-remote",
                            ["type"] = "tcp",
                            ["server"] = "1.1.1.1",
                            ["server_port"] = 53,
                            ["detour"] = "proxy"
                        },
                        new Dictionary<string, object>
                        {
                            ["tag"] = "dns-geohide-udp",
                            ["type"] = "udp",
                            ["server"] = "193.233.112.68",
                            ["server_port"] = 53
                        }
                    },
                    ["rules"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["domain_suffix"] = directDomainSuffixes.ToArray(),
                            ["server"] = "dns-direct"
                        }
                    },
                    ["final"] = "dns-remote",
                    ["strategy"] = "ipv4_only"
                },
                ["inbounds"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "tun",
                        ["tag"] = "tun-in",
                        ["interface_name"] = interfaceName,
                        ["address"] = new[] { tunAddress },
                        ["dns_address"] = new[] { tunDns },
                        ["dns_mode"] = "hijack",
                        ["auto_route"] = true,
                        ["strict_route"] = false,
                        ["stack"] = "mixed",
                        ["endpoint_independent_nat"] = true,
                        ["route_exclude_address"] = routeExcludeAddresses.ToArray()
                    }
                },
                ["outbounds"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "socks",
                        ["tag"] = "proxy",
                        ["server"] = "127.0.0.1",
                        ["server_port"] = SocksPort
                    },
                    new Dictionary<string, object> { ["type"] = "direct", ["tag"] = "direct" },
                    new Dictionary<string, object> { ["type"] = "block", ["tag"] = "block" }
                },
                ["route"] = new Dictionary<string, object>
                {
                    ["default_domain_resolver"] = "dns-direct",
                    ["rules"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["action"] = "sniff"
                        },
                        new Dictionary<string, object>
                        {
                            ["protocol"] = "dns",
                            ["action"] = "hijack-dns"
                        },
                        // Block QUIC (UDP 443) locally so browsers fall back to TCP immediately (prevents YouTube 5s hang)
                        new Dictionary<string, object>
                        {
                            ["port"] = 443,
                            ["network"] = "udp",
                            ["outbound"] = "block"
                        },
                        // Explicit proxy domains (Google, Gemini, Cloud AI Companion, OpenAI, Claude, Cursor, GitHub)
                        new Dictionary<string, object>
                        {
                            ["domain_suffix"] = new[]
                            {
                                "google.com",
                                "googleapis.com",
                                "gstatic.com",
                                "googlevideo.com",
                                "youtube.com",
                                "ytimg.com",
                                "ggpht.com",
                                "gvt1.com",
                                "gvt2.com",
                                "gemini.google.com",
                                "cloudaicompanion.googleapis.com",
                                "deepmind.google",
                                "deepmind.com",
                                "cloud.google.com",
                                "googlecloud.com",
                                "googleusercontent.com",
                                "1e100.net",
                                "g.co",
                                "goog",
                                "openai.com",
                                "anthropic.com",
                                "claude.ai",
                                "cursor.sh",
                                "cursor.com",
                                "github.com",
                                "githubusercontent.com",
                                "githubassets.com"
                            },
                            ["outbound"] = "proxy"
                        },
                        new Dictionary<string, object>
                        {
                            ["ip_cidr"] = directIps.ToArray(),
                            ["outbound"] = "direct"
                        },
                        new Dictionary<string, object>
                        {
                            ["ip_is_private"] = true,
                            ["outbound"] = "direct"
                        },
                        new Dictionary<string, object>
                        {
                            ["domain_suffix"] = directDomainSuffixes.ToArray(),
                            ["outbound"] = "direct"
                        }
                    },
                    ["final"] = "proxy",
                    ["auto_detect_interface"] = true
                }
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_singBoxConfigFile, JsonSerializer.Serialize(singBoxConfig, options));
        }

        public bool Start(AppConfig config)
        {
            lock (_startStopLock)
            {
                if (IsRunning) return true;

                CurrentMode = (config.Mode ?? "proxy").ToLowerInvariant();
                if (CurrentMode == "tun")
                {
                    return StartTun(config);
                }
                else
                {
                    return StartProxy(config);
                }
            }
        }

        public static string? GetPhysicalDefaultGateway()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.Name.IndexOf("tun", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (ni.Description.IndexOf("tun", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    var props = ni.GetIPProperties();
                    foreach (var gw in props.GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !gw.Address.Equals(IPAddress.Any) &&
                            !gw.Address.ToString().StartsWith("172.18."))
                        {
                            return gw.Address.ToString();
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private void AddHostRoute(string destination, string gateway, string mask = "255.255.255.255")
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "route.exe",
                    Arguments = $"add {destination} mask {mask} {gateway} metric 1",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(1000);
                lock (_addedRoutes)
                {
                    if (!_addedRoutes.Contains(destination))
                        _addedRoutes.Add(destination);
                }
            }
            catch { }
        }

        private void RemoveHostRoutes()
        {
            lock (_addedRoutes)
            {
                foreach (var dest in _addedRoutes)
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "route.exe",
                            Arguments = $"delete {dest}",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit(1000);
                    }
                    catch { }
                }
                _addedRoutes.Clear();
            }
        }

        public static void WaitForTunAdapterCleanup(string? specificName = null, int maxChecks = 5)
        {
            try
            {
                for (int i = 0; i < maxChecks; i++)
                {
                    bool hasTun = false;
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (!string.IsNullOrEmpty(specificName))
                        {
                            if (string.Equals(ni.Name, specificName, StringComparison.OrdinalIgnoreCase))
                            {
                                hasTun = true;
                                break;
                            }
                        }
                        else
                        {
                            if (ni.Name.IndexOf("singbox_tun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                ni.Description.IndexOf("sing-tun", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                hasTun = true;
                                break;
                            }
                        }
                    }
                    if (!hasTun) break;
                    Thread.Sleep(100);
                }
            }
            catch { }
        }

        public static void KillOrphanProcesses()
        {
            foreach (var name in new[] { "sing-box", "xray" })
            {
                try
                {
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            proc.Kill();
                            proc.WaitForExit(1000);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        private bool StartProxy(AppConfig config)
        {
            if (!File.Exists(_xrayExe))
                throw new FileNotFoundException($"xray.exe не найден в папке bin: {_xrayExe}");

            KillOrphanProcesses();
            RemoveHostRoutes();

            GenerateConfig(config);

            var startInfo = new ProcessStartInfo
            {
                FileName = _xrayExe,
                Arguments = $"run -c \"{_configFile}\"",
                WorkingDirectory = _appDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.EnvironmentVariables["xray.location.asset"] = _binDir;

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LogReceived?.Invoke(StripAnsi(e.Data)); };
            _process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LogReceived?.Invoke(StripAnsi(e.Data)); };
            _process.Exited += (s, e) =>
            {
                SystemProxy.SetProxy(false);
                StateChanged?.Invoke();
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            SystemProxy.SetProxy(true, "127.0.0.1", HttpPort, config.CustomDirectDomains);

            _statsCts = new CancellationTokenSource();
            _ = RunStatsWorkerAsync(_statsCts.Token);

            StateChanged?.Invoke();
            return true;
        }

        private bool StartTun(AppConfig config)
        {
            if (!File.Exists(_singBoxExe))
                throw new FileNotFoundException($"sing-box.exe не найден в папке bin: {_singBoxExe}");
            if (!File.Exists(_xrayExe))
                throw new FileNotFoundException($"xray.exe не найден в папке bin: {_xrayExe}");

            // Ensure elevation for TUN mode virtual adapter creation
            if (!IsAdministrator())
            {
                var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(_appDir, "bksh2ray.exe");
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = _appDir
                };
                try
                {
                    Process.Start(psi);
                    Environment.Exit(0);
                    return false;
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    throw new InvalidOperationException("Для включения режима TUN требуются права администратора (UAC был отклонён).");
                }
            }

            KillOrphanProcesses();
            RemoveHostRoutes();

            // Add static host routes to physical default gateway to prevent outbound traffic looping into singbox_tun
            var gateway = GetPhysicalDefaultGateway();
            if (!string.IsNullOrEmpty(gateway))
            {
                if (config.Servers != null)
                {
                    foreach (var s in config.Servers)
                    {
                        if (string.IsNullOrWhiteSpace(s.Server)) continue;
                        var host = s.Server.Trim();
                        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                        {
                            AddHostRoute(ip.ToString(), gateway, "255.255.255.255");
                        }
                        else
                        {
                            try
                            {
                                foreach (var a in Dns.GetHostAddresses(host))
                                {
                                    if (a.AddressFamily == AddressFamily.InterNetwork)
                                    {
                                        AddHostRoute(a.ToString(), gateway, "255.255.255.255");
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }

                // Subnets for GeoHide DNS and corporate networks
                AddHostRoute("193.233.112.0", gateway, "255.255.255.0");
                AddHostRoute("45.155.204.0", gateway, "255.255.255.0");
                AddHostRoute("46.8.158.0", gateway, "255.255.255.0");
                AddHostRoute("37.230.192.0", gateway, "255.255.255.0");
                AddHostRoute("217.65.83.0", gateway, "255.255.255.0");
                AddHostRoute("109.202.29.0", gateway, "255.255.255.0");
            }

            // 1. Generate Xray config and start Xray backend to handle VLESS and GeoHide DNS
            GenerateConfig(config);

            var xrayStartInfo = new ProcessStartInfo
            {
                FileName = _xrayExe,
                Arguments = $"run -c \"{_configFile}\"",
                WorkingDirectory = _appDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            xrayStartInfo.EnvironmentVariables["xray.location.asset"] = _binDir;

            _process = new Process { StartInfo = xrayStartInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LogReceived?.Invoke(StripAnsi(e.Data)); };
            _process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LogReceived?.Invoke(StripAnsi(e.Data)); };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            Thread.Sleep(300);

            // 2. Generate Sing-box TUN config with rotating adapter name and subnet to prevent Wintun NDIS teardown collision
            int sessionIndex = Interlocked.Increment(ref _tunSessionCounter);
            int slot = sessionIndex % 4;
            _currentTunInterface = $"singbox_tun_{slot}";
            string tunIp = $"172.18.{slot * 4}.1/30";
            string tunDns = $"172.18.{slot * 4}.2";

            WaitForTunAdapterCleanup(_currentTunInterface, 3);
            GenerateSingBoxConfig(config, _currentTunInterface, tunIp, tunDns);

            var sbStartInfo = new ProcessStartInfo
            {
                FileName = _singBoxExe,
                Arguments = $"run --disable-color -c \"{_singBoxConfigFile}\"",
                WorkingDirectory = _appDir,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            bool startedSuccessfully = false;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                if (attempt > 1)
                {
                    LogReceived?.Invoke("[TUN] Повторная попытка запуска адаптера...");
                    try
                    {
                        if (_singBoxProcess != null && !_singBoxProcess.HasExited)
                        {
                            _singBoxProcess.Kill();
                            _singBoxProcess.WaitForExit(1000);
                        }
                    }
                    catch { }

                    sessionIndex = Interlocked.Increment(ref _tunSessionCounter);
                    slot = sessionIndex % 4;
                    _currentTunInterface = $"singbox_tun_{slot}";
                    tunIp = $"172.18.{slot * 4}.1/30";
                    tunDns = $"172.18.{slot * 4}.2";
                    GenerateSingBoxConfig(config, _currentTunInterface, tunIp, tunDns);
                    Thread.Sleep(500);
                }

                // Verify Xray backend is still running; restart if needed
                if (_process == null || _process.HasExited)
                {
                    LogReceived?.Invoke("[TUN] Перезапуск Xray backend...");
                    try { _process?.Kill(); } catch { }
                    _process = Process.Start(xrayStartInfo);
                    _process?.BeginOutputReadLine();
                    _process?.BeginErrorReadLine();
                    Thread.Sleep(500);
                }

                try
                {
                    var tunReadyEvent = new ManualResetEventSlim(false);

                    _singBoxProcess = new Process { StartInfo = sbStartInfo, EnableRaisingEvents = true };
                    _singBoxProcess.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            var clean = StripAnsi(e.Data);
                            LogReceived?.Invoke("[TUN] " + clean);
                            if (clean.Contains("started at", StringComparison.OrdinalIgnoreCase) ||
                                clean.Contains("sing-box started", StringComparison.OrdinalIgnoreCase) ||
                                clean.Contains("inbound connection", StringComparison.OrdinalIgnoreCase) ||
                                clean.Contains("inbound DNS packet", StringComparison.OrdinalIgnoreCase) ||
                                clean.Contains("interface created", StringComparison.OrdinalIgnoreCase) ||
                                clean.Contains("router: completed", StringComparison.OrdinalIgnoreCase))
                            {
                                tunReadyEvent.Set();
                            }
                        }
                    };
                    _singBoxProcess.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            var clean = StripAnsi(e.Data);
                            LogReceived?.Invoke("[TUN] " + clean);
                            if (clean.Contains("started at", StringComparison.OrdinalIgnoreCase) ||
                                clean.Contains("sing-box started", StringComparison.OrdinalIgnoreCase) ||
                                clean.Contains("inbound connection", StringComparison.OrdinalIgnoreCase) ||
                                clean.Contains("inbound DNS packet", StringComparison.OrdinalIgnoreCase) ||
                                clean.Contains("interface created", StringComparison.OrdinalIgnoreCase) ||
                                clean.Contains("router: completed", StringComparison.OrdinalIgnoreCase))
                            {
                                tunReadyEvent.Set();
                            }
                        }
                    };

                    _singBoxProcess.Start();
                    _singBoxProcess.BeginOutputReadLine();
                    _singBoxProcess.BeginErrorReadLine();

                    // Wait up to 8 seconds for sing-box to signal TUN adapter initialization
                    bool ready = tunReadyEvent.Wait(8000);
                    if (ready && !_singBoxProcess.HasExited)
                    {
                        startedSuccessfully = true;
                        break;
                    }
                    else if (!_singBoxProcess.HasExited)
                    {
                        Thread.Sleep(1000);
                        if (!_singBoxProcess.HasExited)
                        {
                            startedSuccessfully = true;
                            break;
                        }
                    }
                }
                catch
                {
                    if (attempt == 2) throw;
                }
            }

            if (!startedSuccessfully)
            {
                Stop();
                throw new InvalidOperationException("Не удалось инициализировать виртуальный адаптер TUN. Попробуйте еще раз через несколько секунд.");
            }

            // Hook exit handlers now that both processes are verified running
            if (_process != null) _process.Exited += (s, e) => Stop();
            if (_singBoxProcess != null) _singBoxProcess.Exited += (s, e) => Stop();

            // Disable system proxy (transparent TUN routing)
            SystemProxy.SetProxy(false);

            // Start speedometer worker (queries Xray stats API)
            _statsCts = new CancellationTokenSource();
            _ = RunStatsWorkerAsync(_statsCts.Token);

            LogReceived?.Invoke($"[TUN] Адаптер {_currentTunInterface} запущен, трафик маршрутизируется через VLESS");
            StateChanged?.Invoke();
            return true;
        }

        public void Stop()
        {
            lock (_startStopLock)
            {
                SystemProxy.SetProxy(false);
                RemoveHostRoutes();

                _statsCts?.Cancel();
                _statsCts = null;

                if (_singBoxProcess != null && !_singBoxProcess.HasExited)
                {
                    try
                    {
                        _singBoxProcess.Kill();
                        _singBoxProcess.WaitForExit(1000);
                    }
                    catch { }
                }
                _singBoxProcess = null;

                if (_process != null && !_process.HasExited)
                {
                    try
                    {
                        _process.Kill();
                        _process.WaitForExit(1000);
                    }
                    catch { }
                }
                _process = null;

                KillOrphanProcesses();

                StateChanged?.Invoke();
            }
        }

        public void RestartIfRunning(AppConfig config)
        {
            if (!IsRunning) return;
            Stop();
            Thread.Sleep(500);
            Start(config);
        }

        private async Task RunStatsWorkerAsync(CancellationToken token)
        {
            long prevDown = 0;
            long prevUp = 0;
            var prevTime = DateTime.UtcNow;

            while (!token.IsCancellationRequested && IsRunning)
            {
                await Task.Delay(1000, token);
                var now = DateTime.UtcNow;
                var dt = Math.Max((now - prevTime).TotalSeconds, 0.1);

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = _xrayExe,
                        Arguments = $"api statsquery --server=127.0.0.1:{ApiPort}",
                        WorkingDirectory = _appDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        cts.CancelAfter(1500);

                        var outputTask = p.StandardOutput.ReadToEndAsync();
                        var exitTask = p.WaitForExitAsync(cts.Token);
                        await Task.WhenAll(outputTask, exitTask);
                        var output = outputTask.Result;

                        if (!string.IsNullOrEmpty(output))
                        {
                            using var doc = JsonDocument.Parse(output);
                            long currDown = 0;
                            long currUp = 0;

                            if (doc.RootElement.TryGetProperty("stat", out var statArr))
                            {
                                foreach (var item in statArr.EnumerateArray())
                                {
                                    var name = item.GetProperty("name").GetString() ?? "";
                                    long val = 0;
                                    if (item.TryGetProperty("value", out var valProp))
                                        val = valProp.GetInt64();

                                    if (name.Contains("outbound>>>proxy>>>traffic>>>downlink"))
                                        currDown += val;
                                    else if (name.Contains("outbound>>>proxy>>>traffic>>>uplink"))
                                        currUp += val;
                                }

                                if (currDown == 0 && currUp == 0)
                                {
                                    foreach (var item in statArr.EnumerateArray())
                                    {
                                        var name = item.GetProperty("name").GetString() ?? "";
                                        long val = 0;
                                        if (item.TryGetProperty("value", out var valProp))
                                            val = valProp.GetInt64();

                                        if (name.Contains("traffic>>>downlink") && !name.Contains("api"))
                                            currDown += val;
                                        else if (name.Contains("traffic>>>uplink") && !name.Contains("api"))
                                            currUp += val;
                                    }
                                }

                                double downBps = prevDown > 0 ? Math.Max(0.0, (currDown - prevDown) / dt) : 0.0;
                                double upBps = prevUp > 0 ? Math.Max(0.0, (currUp - prevUp) / dt) : 0.0;

                                prevDown = currDown;
                                prevUp = currUp;
                                prevTime = now;

                                SpeedUpdated?.Invoke(downBps, upBps, currDown, currUp);
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore stats errors during shutdown or polling
                }
            }
        }
    }
}
