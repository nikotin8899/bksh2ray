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
            for (int p = startPort; p < startPort + maxTries; p++)
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, p);
                    listener.Start();
                    listener.Stop();
                    return p;
                }
                catch
                {
                    // Port occupied, try next
                }
            }
            return startPort;
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

            // Direct domains: default RU + custom domains
            var directDomains = new List<string> { "geosite:category-ru" };
            foreach (var d in config.CustomDirectDomains)
            {
                var clean = d.Trim();
                if (!string.IsNullOrEmpty(clean) && !directDomains.Contains(clean))
                    directDomains.Add(clean);
            }

            var rules = new List<Dictionary<string, object>>
            {
                new() { ["type"] = "field", ["inboundTag"] = new[] { "api" }, ["outboundTag"] = "api" },
                new() { ["type"] = "field", ["outboundTag"] = "direct", ["protocol"] = new[] { "bittorrent" } },
                new() { ["type"] = "field", ["outboundTag"] = "block", ["domain"] = new[] { "geosite:category-ads-all" } },
                new() { ["type"] = "field", ["outboundTag"] = "direct", ["ip"] = new[] { "geoip:private" } },
                new() { ["type"] = "field", ["outboundTag"] = "direct", ["domain"] = new[] { "geosite:private" } },
                new()
                {
                    ["type"] = "field",
                    ["outboundTag"] = "direct",
                    ["domain"] = directDomains,
                    ["ip"] = new[] { "geoip:ru" }
                },
                new()
                {
                    ["type"] = "field",
                    ["outboundTag"] = "proxy",
                    ["network"] = "tcp,udp"
                }
            };

            var xrayConfig = new Dictionary<string, object>
            {
                ["log"] = new Dictionary<string, object> { ["loglevel"] = "info" },
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
                    ["domainStrategy"] = "IPIfNonMatch",
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

            SystemProxy.SetProxy(true, "127.0.0.1", HttpPort);

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
