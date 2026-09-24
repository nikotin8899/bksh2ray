using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Web;

namespace bksh2ray
{
    public class ConfigManager
    {
        private readonly string _configFile;
        public AppConfig Config { get; private set; }

        public ConfigManager(string appDir)
        {
            _configFile = Path.Combine(appDir, "user_config.json");
            Config = Load();
        }

        public AppConfig Load()
        {
            var paths = new[] { _configFile, _configFile + ".tmp" };
            foreach (var path in paths)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    try
                    {
                        var json = File.ReadAllText(path);
                        var conf = JsonSerializer.Deserialize<AppConfig>(json);
                        if (conf != null)
                        {
                            conf.PresetRu = true;
                            if (conf.CustomDirectDomains == null)
                                conf.CustomDirectDomains = new List<string>();
                            return conf;
                        }
                    }
                    catch { }
                }
            }
            return new AppConfig();
        }

        public void Save()
        {
            try
            {
                var tmp = _configFile + ".tmp";
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(Config, options);
                File.WriteAllText(tmp, json);
                if (File.Exists(_configFile))
                    File.Move(tmp, _configFile, overwrite: true);
                else
                    File.Move(tmp, _configFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Error saving config: {ex.Message}");
            }
        }

        public (bool success, string message, string? error) ImportVless(string link)
        {
            try
            {
                link = link.Trim().Trim('"', '\'', '`', '<', '>');
                if (!link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                    return (false, "", "Ссылка должна начинаться с vless://");

                var uri = new Uri(link);
                var uuid = uri.UserInfo;
                var host = uri.Host;
                var port = uri.Port > 0 ? uri.Port : 443;

                var query = HttpUtility.ParseQueryString(uri.Query);

                var spx = query.Get("spx") ?? "";
                if (!string.IsNullOrEmpty(spx))
                    spx = HttpUtility.UrlDecode(spx);

                var name = !string.IsNullOrEmpty(uri.Fragment) 
                    ? HttpUtility.UrlDecode(uri.Fragment.TrimStart('#')) 
                    : host;

                var server = new VlessServer
                {
                    Name = name,
                    Server = host,
                    ServerPort = port,
                    Uuid = uuid,
                    Flow = query.Get("flow") ?? "",
                    Security = query.Get("security") ?? "none",
                    Sni = query.Get("sni") ?? "",
                    Pbk = query.Get("pbk") ?? "",
                    Sid = query.Get("sid") ?? "",
                    Spx = spx,
                    Fp = query.Get("fp") ?? "chrome",
                    Type = query.Get("type") ?? "tcp"
                };

                Config.Servers.Add(server);
                if (Config.ActiveServerIndex == -1)
                    Config.ActiveServerIndex = 0;

                Save();
                return (true, $"Сервер «{name}» успешно добавлен!", null);
            }
            catch (Exception ex)
            {
                return (false, "", ex.Message);
            }
        }
    }
}
