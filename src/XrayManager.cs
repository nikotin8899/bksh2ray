using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
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

        private Process? _process;
        private Process? _singBoxProcess;
        private CancellationTokenSource? _statsCts;
        private CancellationTokenSource? _tunLogCts;

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
                "193.233.112.68/32",
                "193.233.112.67/32",
                "46.8.158.6/32",
                "37.230.192.51/32"
            };

            var rules = new List<Dictionary<string, object>>
            {
                new() { ["type"] = "field", ["inboundTag"] = new[] { "api" }, ["outboundTag"] = "api" },
                new() { ["type"] = "field", ["port"] = "53", ["outboundTag"] = "dns-out" },
                new() { ["type"] = "field", ["outboundTag"] = "direct", ["protocol"] = new[] { "bittorrent" } },
                new() { ["type"] = "field", ["outboundTag"] = "block", ["domain"] = new[] { "geosite:category-ads-all" } },
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
                        ["dns.geohide.ru"] = new[] { "193.233.112.68", "193.233.112.67" },
                        ["geohide.ru"] = new[] { "193.233.112.68", "193.233.112.67" }
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
                            ["address"] = "https://dns.geohide.ru/dns-query",
                            ["queryStrategy"] = "UseIPv4"
                        },
                        new Dictionary<string, object>
                        {
                            ["address"] = "https://geohide.ru/dns-query",
                            ["queryStrategy"] = "UseIPv4"
                        },
                        new Dictionary<string, object>
                        {
                            ["address"] = "tcp://193.233.112.68:53",
                            ["queryStrategy"] = "UseIPv4"
                        },
                        new Dictionary<string, object>
                        {
                            ["address"] = "193.233.112.68",
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

        public void GenerateSingBoxConfig(AppConfig config)
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

            var singBoxLogFile = Path.Combine(_appDir, "singbox.log");

            var singBoxConfig = new Dictionary<string, object>
            {
                ["log"] = new Dictionary<string, object>
                {
                    ["disabled"] = false,
                    ["level"] = "warn",
                    ["output"] = singBoxLogFile,
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
                            ["tag"] = "dns-geohide",
                            ["type"] = "https",
                            ["server"] = "193.233.112.68",
                            ["server_port"] = 443,
                            ["tls"] = new Dictionary<string, object>
                            {
                                ["enabled"] = true,
                                ["server_name"] = "dns.geohide.ru"
                            }
                        }
                    },
                    ["rules"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["domain_suffix"] = directDomainSuffixes,
                            ["server"] = "dns-direct"
                        }
                    },
                    ["final"] = "dns-geohide"
                },
                ["inbounds"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "tun",
                        ["tag"] = "tun-in",
                        ["interface_name"] = "bksh2ray_tun",
                        ["address"] = new[] { "172.19.0.1/30" },
                        ["auto_route"] = true,
                        ["strict_route"] = false,
                        ["stack"] = "mixed"
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
                        new Dictionary<string, object>
                        {
                            ["ip_cidr"] = new[]
                            {
                                "127.0.0.0/8",
                                "193.233.112.68/32",
                                "193.233.112.67/32",
                                "46.8.158.6/32",
                                "37.230.192.51/32"
                            },
                            ["outbound"] = "direct"
                        },
                        new Dictionary<string, object>
                        {
                            ["ip_is_private"] = true,
                            ["outbound"] = "direct"
                        },
                        new Dictionary<string, object>
                        {
                            ["domain_suffix"] = directDomainSuffixes,
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

        private bool StartProxy(AppConfig config)
        {
            if (!File.Exists(_xrayExe))
                throw new FileNotFoundException($"xray.exe не найден в папке bin: {_xrayExe}");

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
            _process.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LogReceived?.Invoke(e.Data); };
            _process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LogReceived?.Invoke(e.Data); };
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
            _process.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LogReceived?.Invoke(e.Data); };
            _process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LogReceived?.Invoke(e.Data); };
            _process.Exited += (s, e) =>
            {
                Stop();
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // 2. Generate Sing-box TUN config (inbound: tun bksh2ray_tun, outbound: socks5 -> 127.0.0.1:SocksPort)
            GenerateSingBoxConfig(config);

            bool isElevated = IsAdministrator();
            var sbStartInfo = new ProcessStartInfo
            {
                FileName = _singBoxExe,
                Arguments = $"run -c \"{_singBoxConfigFile}\"",
                WorkingDirectory = _appDir,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (!isElevated)
            {
                sbStartInfo.UseShellExecute = true;
                sbStartInfo.Verb = "runas";
            }
            else
            {
                sbStartInfo.UseShellExecute = false;
                sbStartInfo.RedirectStandardOutput = true;
                sbStartInfo.RedirectStandardError = true;
            }

            try
            {
                _singBoxProcess = new Process { StartInfo = sbStartInfo, EnableRaisingEvents = true };
                if (isElevated)
                {
                    _singBoxProcess.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LogReceived?.Invoke("[TUN] " + e.Data); };
                    _singBoxProcess.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LogReceived?.Invoke("[TUN] " + e.Data); };
                }
                _singBoxProcess.Exited += (s, e) =>
                {
                    Stop();
                };

                _singBoxProcess.Start();
                if (isElevated)
                {
                    _singBoxProcess.BeginOutputReadLine();
                    _singBoxProcess.BeginErrorReadLine();
                }

                // Start tailing singbox.log for real-time TUN events
                _tunLogCts = new CancellationTokenSource();
                StartTunLogReader(Path.Combine(_appDir, "singbox.log"), _tunLogCts.Token);

                // Disable system proxy (transparent TUN routing)
                SystemProxy.SetProxy(false);

                // Start speedometer worker (queries Xray stats API)
                _statsCts = new CancellationTokenSource();
                _ = RunStatsWorkerAsync(_statsCts.Token);

                LogReceived?.Invoke("[TUN] Адаптер bksh2ray_tun запущен, трафик маршрутизируется через VLESS");
                StateChanged?.Invoke();
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Stop();
                throw new InvalidOperationException("Для включения режима TUN требуются права администратора (UAC был отклонён).");
            }
            catch
            {
                Stop();
                throw;
            }
        }

        private void StartTunLogReader(string logPath, CancellationToken token)
        {
            _ = Task.Run(async () =>
            {
                long lastPos = 0;
                if (File.Exists(logPath))
                {
                    try { lastPos = new FileInfo(logPath).Length; } catch { }
                }

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (File.Exists(logPath))
                        {
                            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            if (fs.Length < lastPos) lastPos = 0;
                            if (fs.Length > lastPos)
                            {
                                fs.Seek(lastPos, SeekOrigin.Begin);
                                using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
                                string? line;
                                while ((line = await reader.ReadLineAsync()) != null)
                                {
                                    if (!string.IsNullOrWhiteSpace(line))
                                    {
                                        LogReceived?.Invoke("[TUN] " + line);
                                    }
                                }
                                lastPos = fs.Position;
                            }
                        }
                    }
                    catch { }

                    await Task.Delay(300, token);
                }
            }, token);
        }

        public void Stop()
        {
            SystemProxy.SetProxy(false);

            _tunLogCts?.Cancel();
            _tunLogCts = null;

            _statsCts?.Cancel();
            _statsCts = null;

            if (_singBoxProcess != null && !_singBoxProcess.HasExited)
            {
                try
                {
                    _singBoxProcess.Kill();
                    _singBoxProcess.WaitForExit(1500);
                }
                catch { }
            }
            _singBoxProcess = null;

            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _process.Kill();
                    _process.WaitForExit(1500);
                }
                catch { }
            }
            _process = null;

            StateChanged?.Invoke();
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
                        var output = await p.StandardOutput.ReadToEndAsync();
                        await p.WaitForExitAsync(token);

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
