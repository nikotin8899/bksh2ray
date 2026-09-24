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
        private readonly string _configFile;

        private Process? _process;
        private CancellationTokenSource? _statsCts;

        public bool IsRunning => _process != null && !_process.HasExited;
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
            _configFile = Path.Combine(_appDir, "xray_config.json");
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

            // Direct IPs: corporate subnets, private subnets (RFC 1918, CGNAT, link-local)
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
                "109.202.29.0/24"
            };

            var rules = new List<Dictionary<string, object>>
            {
                new() { ["type"] = "field", ["inboundTag"] = new[] { "api" }, ["outboundTag"] = "api" },
                new() { ["type"] = "field", ["outboundTag"] = "direct", ["protocol"] = new[] { "bittorrent" } },
                new() { ["type"] = "field", ["outboundTag"] = "block", ["domain"] = new[] { "geosite:category-ads-all" } },
                // 1. Corporate, LAN & Private IPs -> Direct
                new()
                {
                    ["type"] = "field",
                    ["outboundTag"] = "direct",
                    ["ip"] = directIps
                },
                // 2. Corporate, Local, RU and Custom Domains -> Direct
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
                        ["tag"] = "socks-in"
                    },
                    new Dictionary<string, object>
                    {
                        ["port"] = HttpPort,
                        ["listen"] = "127.0.0.1",
                        ["protocol"] = "http",
                        ["tag"] = "http-in"
                    }
                },
                ["outbounds"] = new object[]
                {
                    outboundProxy,
                    new Dictionary<string, object> { ["protocol"] = "freedom", ["tag"] = "direct" },
                    new Dictionary<string, object> { ["protocol"] = "blackhole", ["tag"] = "block" }
                },
                ["routing"] = new Dictionary<string, object>
                {
                    ["domainStrategy"] = "AsIs",
                    ["rules"] = rules
                }
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_configFile, JsonSerializer.Serialize(xrayConfig, options));
        }

        public bool Start(AppConfig config)
        {
            if (IsRunning) return true;

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

        public void Stop()
        {
            SystemProxy.SetProxy(false);

            _statsCts?.Cancel();
            _statsCts = null;

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

            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(1000);
                }
            }
            catch { }
            _process = null;

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
