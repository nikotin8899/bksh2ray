using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace bksh2ray
{
    public class UpdateComponentInfo
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Category { get; set; } = "";
        public string Description { get; set; } = "";
        public string Source { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public bool HasUpdate { get; set; } = false;
        public string DownloadUrl { get; set; } = "";
        public string Status { get; set; } = "idle";
        public string? ErrorMessage { get; set; }
    }

    public class UpdateManager
    {
        public const string CurrentAppVersion = "v1.2.2";
        public const string AppRepoOwner = "nikotin8899";
        public const string AppRepoName = "bksh2ray";

        private readonly string _appDir;
        private readonly string _binDir;
        private readonly XrayManager _xrayMgr;
        private readonly ConfigManager _configMgr;

        public event Action<string, int, string>? ProgressChanged;

        public UpdateManager(string appDir, XrayManager xrayMgr, ConfigManager configMgr)
        {
            _appDir = appDir;
            _binDir = Path.Combine(_appDir, "bin");
            _xrayMgr = xrayMgr;
            _configMgr = configMgr;

            // Clean up any .old files from previous updates
            CleanOldFiles();
        }

        private void CleanOldFiles()
        {
            try
            {
                var files = new[]
                {
                    Path.Combine(_appDir, "bksh2ray.exe.old"),
                    Path.Combine(_appDir, "bksh2ray.exe.new"),
                    Path.Combine(_binDir, "sing-box.exe.old"),
                    Path.Combine(_binDir, "xray.exe.old")
                };

                foreach (var f in files)
                {
                    if (File.Exists(f))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }

        private HttpClient CreateHttpClient(bool allowAutoRedirect = true, int timeoutSec = 45)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = allowAutoRedirect,
                CheckCertificateRevocationList = false
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(timeoutSec)
            };
            client.DefaultRequestHeaders.Add("User-Agent", "bksh2ray-Updater/1.1");
            return client;
        }

        public async Task<List<UpdateComponentInfo>> CheckAllUpdatesAsync()
        {
            var taskApp = CheckBksh2rayAsync();
            var taskSingBox = CheckSingBoxAsync();
            var taskXray = CheckXrayAsync();
            var taskGeo = CheckGeoAsync();

            await Task.WhenAll(taskApp, taskSingBox, taskXray, taskGeo);

            return new List<UpdateComponentInfo>
            {
                taskApp.Result,
                taskSingBox.Result,
                taskXray.Result,
                taskGeo.Result
            };
        }

        public async Task<UpdateComponentInfo> CheckBksh2rayAsync()
        {
            var info = new UpdateComponentInfo
            {
                Id = "bksh2ray",
                Title = "bksh2ray (Программа)",
                Category = "app",
                Description = "Основное приложение и графический интерфейс",
                Source = $"github.com/{AppRepoOwner}/{AppRepoName}",
                CurrentVersion = CurrentAppVersion,
                LatestVersion = CurrentAppVersion,
                HasUpdate = false,
                DownloadUrl = $"https://github.com/{AppRepoOwner}/{AppRepoName}/raw/main/bksh2ray.exe"
            };

            try
            {
                // 1. First check if a release tag exists
                using var clientNoRedirect = CreateHttpClient(allowAutoRedirect: false, timeoutSec: 15);
                var releaseCheckUrl = $"https://github.com/{AppRepoOwner}/{AppRepoName}/releases/latest";
                var resp = await clientNoRedirect.GetAsync(releaseCheckUrl);

                string? tag = null;
                if (resp.StatusCode == System.Net.HttpStatusCode.Found || resp.StatusCode == System.Net.HttpStatusCode.MovedPermanently)
                {
                    var loc = resp.Headers.Location?.ToString() ?? "";
                    if (loc.Contains("/tag/"))
                    {
                        tag = loc.Substring(loc.LastIndexOf("/tag/") + 5).Trim();
                    }
                }

                if (!string.IsNullOrEmpty(tag))
                {
                    info.LatestVersion = tag;
                    info.HasUpdate = !string.Equals(tag, CurrentAppVersion, StringComparison.OrdinalIgnoreCase);
                    info.DownloadUrl = $"https://github.com/{AppRepoOwner}/{AppRepoName}/releases/download/{tag}/bksh2ray.exe";
                    return info;
                }

                // 2. Check latest commit via GitHub API
                using var client = CreateHttpClient(timeoutSec: 15);
                var apiUrl = $"https://api.github.com/repos/{AppRepoOwner}/{AppRepoName}/commits/main";
                var commitResp = await client.GetAsync(apiUrl);

                if (commitResp.IsSuccessStatusCode)
                {
                    var json = await commitResp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var sha = doc.RootElement.GetProperty("sha").GetString() ?? "";
                    var shortSha = sha.Length > 7 ? sha.Substring(0, 7) : sha;
                    
                    var commitDateStr = "";
                    try
                    {
                        commitDateStr = doc.RootElement.GetProperty("commit").GetProperty("author").GetProperty("date").GetString() ?? "";
                    }
                    catch { }

                    string dateLabel = "";
                    if (DateTime.TryParse(commitDateStr, out var dt))
                    {
                        dateLabel = $" ({dt:dd.MM.yyyy})";
                    }

                    info.LatestVersion = $"{CurrentAppVersion}-{shortSha}{dateLabel}";

                    // Check file size of local vs remote bksh2ray.exe
                    var currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(_appDir, "bksh2ray.exe");
                    long localSize = File.Exists(currentExe) ? new FileInfo(currentExe).Length : 0;

                    var rawHeadUrl = $"https://github.com/{AppRepoOwner}/{AppRepoName}/raw/main/bksh2ray.exe";
                    using var headReq = new HttpRequestMessage(HttpMethod.Head, rawHeadUrl);
                    var headResp = await client.SendAsync(headReq);
                    if (headResp.IsSuccessStatusCode && headResp.Content.Headers.ContentLength.HasValue)
                    {
                        long remoteSize = headResp.Content.Headers.ContentLength.Value;
                        // If file size or last-modified differs, or local version doesn't include current short commit
                        info.HasUpdate = (remoteSize > 0 && Math.Abs(remoteSize - localSize) > 32);
                    }
                    else
                    {
                        info.HasUpdate = false;
                    }

                    info.DownloadUrl = rawHeadUrl;
                }
            }
            catch (Exception ex)
            {
                info.ErrorMessage = ex.Message;
            }

            return info;
        }

        public async Task<UpdateComponentInfo> CheckSingBoxAsync()
        {
            var curVer = GetSingBoxVersion();
            var info = new UpdateComponentInfo
            {
                Id = "sing-box",
                Title = "Ядро Sing-box",
                Category = "core",
                Description = "Универсальное высокопроизводительное сетевое прокси-ядро",
                Source = "github.com/SagerNet/sing-box",
                CurrentVersion = curVer,
                LatestVersion = curVer,
                HasUpdate = false
            };

            try
            {
                using var client = CreateHttpClient(allowAutoRedirect: false, timeoutSec: 15);
                var resp = await client.GetAsync("https://github.com/SagerNet/sing-box/releases/latest");
                string tag = "";
                if (resp.StatusCode == System.Net.HttpStatusCode.Found || resp.StatusCode == System.Net.HttpStatusCode.MovedPermanently)
                {
                    var loc = resp.Headers.Location?.ToString() ?? "";
                    if (loc.Contains("/tag/"))
                    {
                        tag = loc.Substring(loc.LastIndexOf("/tag/") + 5).Trim();
                    }
                }

                if (string.IsNullOrEmpty(tag))
                {
                    using var apiClient = CreateHttpClient(timeoutSec: 15);
                    var json = await apiClient.GetStringAsync("https://api.github.com/repos/SagerNet/sing-box/releases/latest");
                    using var doc = JsonDocument.Parse(json);
                    tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                }

                if (!string.IsNullOrEmpty(tag))
                {
                    info.LatestVersion = tag;
                    var cleanTag = tag.TrimStart('v');
                    var cleanCur = curVer.TrimStart('v');
                    info.HasUpdate = !string.Equals(cleanTag, cleanCur, StringComparison.OrdinalIgnoreCase);
                    info.DownloadUrl = $"https://github.com/SagerNet/sing-box/releases/download/{tag}/sing-box-{cleanTag}-windows-amd64.zip";
                }
            }
            catch (Exception ex)
            {
                info.ErrorMessage = ex.Message;
            }

            return info;
        }

        public async Task<UpdateComponentInfo> CheckXrayAsync()
        {
            var curVer = GetXrayVersion();
            var info = new UpdateComponentInfo
            {
                Id = "xray",
                Title = "Ядро Xray-core",
                Category = "core",
                Description = "Ядро VLESS / Reality / Trojan и маршрутизации трафика",
                Source = "github.com/XTLS/Xray-core",
                CurrentVersion = curVer,
                LatestVersion = curVer,
                HasUpdate = false,
                DownloadUrl = "https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip"
            };

            try
            {
                using var client = CreateHttpClient(allowAutoRedirect: false, timeoutSec: 15);
                var resp = await client.GetAsync("https://github.com/XTLS/Xray-core/releases/latest");
                string tag = "";
                if (resp.StatusCode == System.Net.HttpStatusCode.Found || resp.StatusCode == System.Net.HttpStatusCode.MovedPermanently)
                {
                    var loc = resp.Headers.Location?.ToString() ?? "";
                    if (loc.Contains("/tag/"))
                    {
                        tag = loc.Substring(loc.LastIndexOf("/tag/") + 5).Trim();
                    }
                }

                if (string.IsNullOrEmpty(tag))
                {
                    using var apiClient = CreateHttpClient(timeoutSec: 15);
                    var json = await apiClient.GetStringAsync("https://api.github.com/repos/XTLS/Xray-core/releases/latest");
                    using var doc = JsonDocument.Parse(json);
                    tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                }

                if (!string.IsNullOrEmpty(tag))
                {
                    info.LatestVersion = tag;
                    var cleanTag = tag.TrimStart('v');
                    var cleanCur = curVer.TrimStart('v');
                    info.HasUpdate = !string.Equals(cleanTag, cleanCur, StringComparison.OrdinalIgnoreCase);
                    info.DownloadUrl = $"https://github.com/XTLS/Xray-core/releases/download/{tag}/Xray-windows-64.zip";
                }
            }
            catch (Exception ex)
            {
                info.ErrorMessage = ex.Message;
            }

            return info;
        }

        public async Task<UpdateComponentInfo> CheckGeoAsync()
        {
            var geoip = Path.Combine(_binDir, "geoip.dat");
            var geosite = Path.Combine(_binDir, "geosite.dat");

            string curDate = "Не найдено";
            if (File.Exists(geoip) && File.Exists(geosite))
            {
                var dt = File.GetLastWriteTime(geoip);
                curDate = dt.ToString("yyyy-MM-dd");
            }

            var info = new UpdateComponentInfo
            {
                Id = "geo",
                Title = "Базы GeoIP и GeoSite",
                Category = "rules",
                Description = "Базы доменов и IP для прямого подключения к сервисам РФ без VPN",
                Source = "github.com/Loyalsoldier/v2ray-rules-dat",
                CurrentVersion = curDate,
                LatestVersion = curDate,
                HasUpdate = false,
                DownloadUrl = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat"
            };

            try
            {
                using var client = CreateHttpClient(allowAutoRedirect: false, timeoutSec: 15);
                var resp = await client.GetAsync("https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest");
                string tag = "";
                if (resp.StatusCode == System.Net.HttpStatusCode.Found || resp.StatusCode == System.Net.HttpStatusCode.MovedPermanently)
                {
                    var loc = resp.Headers.Location?.ToString() ?? "";
                    if (loc.Contains("/tag/"))
                    {
                        tag = loc.Substring(loc.LastIndexOf("/tag/") + 5).Trim();
                    }
                }

                if (!string.IsNullOrEmpty(tag))
                {
                    info.LatestVersion = tag;
                    // Tag format: e.g. 202609240010
                    string formattedTag = tag;
                    if (tag.Length >= 8 && int.TryParse(tag.Substring(0, 8), out _))
                    {
                        formattedTag = $"{tag.Substring(0, 4)}-{tag.Substring(4, 2)}-{tag.Substring(6, 2)}";
                    }

                    info.HasUpdate = !string.Equals(formattedTag, curDate, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                info.ErrorMessage = ex.Message;
            }

            return info;
        }

        private string GetSingBoxVersion()
        {
            var path = Path.Combine(_binDir, "sing-box.exe");
            if (!File.Exists(path)) return "Не найдено";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var outStr = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);
                    var match = Regex.Match(outStr, @"version\s+([0-9\.]+[a-zA-Z0-9\.\-]*)");
                    if (match.Success) return "v" + match.Groups[1].Value;
                }
            }
            catch { }
            return "Установлено";
        }

        private string GetXrayVersion()
        {
            var path = Path.Combine(_binDir, "xray.exe");
            if (!File.Exists(path)) return "Не найдено";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var outStr = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);
                    var match = Regex.Match(outStr, @"Xray\s+([0-9\.]+)");
                    if (match.Success) return "v" + match.Groups[1].Value;
                }
            }
            catch { }
            return "Установлено";
        }

        public async Task<object> UpdateComponentAsync(string componentId)
        {
            switch (componentId.ToLowerInvariant())
            {
                case "bksh2ray":
                    return await UpdateBksh2rayAsync();

                case "sing-box":
                    return await UpdateSingBoxAsync();

                case "xray":
                    return await UpdateXrayAsync();

                case "geo":
                    return await UpdateGeoAsync();

                default:
                    return new { success = false, error = $"Неизвестный компонент: {componentId}" };
            }
        }

        private async Task<object> UpdateBksh2rayAsync()
        {
            try
            {
                ProgressChanged?.Invoke("bksh2ray", 5, "Подготовка к загрузке bksh2ray...");

                var exeUrl = $"https://github.com/{AppRepoOwner}/{AppRepoName}/raw/main/bksh2ray.exe";
                var tempExe = Path.Combine(_appDir, "bksh2ray.exe.new");

                await DownloadFileAsync(exeUrl, tempExe, (pct, msg) =>
                {
                    ProgressChanged?.Invoke("bksh2ray", (int)(5 + pct * 0.7), $"Загрузка bksh2ray.exe: {pct}%");
                });

                // Also update UI files from GitHub raw if available
                ProgressChanged?.Invoke("bksh2ray", 78, "Синхронизация интерфейса ui/...");
                await DownloadUiFileSafeAsync("index.html");
                await DownloadUiFileSafeAsync("script.js");
                await DownloadUiFileSafeAsync("style.css");

                ProgressChanged?.Invoke("bksh2ray", 90, "Применение исполняемого файла...");

                var currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(_appDir, "bksh2ray.exe");
                var oldExe = currentExe + ".old";

                if (File.Exists(oldExe))
                {
                    try { File.Delete(oldExe); } catch { }
                }

                File.Move(currentExe, oldExe);
                File.Move(tempExe, currentExe);

                ProgressChanged?.Invoke("bksh2ray", 100, "Обновление готово! Требуется перезапуск.");
                return new
                {
                    success = true,
                    need_restart = true,
                    message = "bksh2ray успешно обновлен из GitHub! Нажмите «Перезапустить», чтобы применить обновление."
                };
            }
            catch (Exception ex)
            {
                ProgressChanged?.Invoke("bksh2ray", 0, $"Ошибка: {ex.Message}");
                return new { success = false, error = ex.Message };
            }
        }

        private async Task DownloadUiFileSafeAsync(string fileName)
        {
            try
            {
                var url = $"https://raw.githubusercontent.com/{AppRepoOwner}/{AppRepoName}/main/ui/{fileName}";
                var target = Path.Combine(_appDir, "ui", fileName);
                var temp = target + ".tmp";

                using var client = CreateHttpClient(timeoutSec: 20);
                var bytes = await client.GetByteArrayAsync(url);
                if (bytes != null && bytes.Length > 100)
                {
                    await File.WriteAllBytesAsync(temp, bytes);
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(temp, target);
                }
            }
            catch { }
        }

        private async Task<object> UpdateSingBoxAsync()
        {
            try
            {
                ProgressChanged?.Invoke("sing-box", 5, "Поиск свежей версии Sing-box на GitHub...");
                var check = await CheckSingBoxAsync();
                var downloadUrl = check.DownloadUrl;

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    downloadUrl = $"https://github.com/SagerNet/sing-box/releases/latest/download/sing-box-{check.LatestVersion.TrimStart('v')}-windows-amd64.zip";
                }

                var tempZip = Path.Combine(_appDir, "cache", "sing-box-download.zip");
                var cacheDir = Path.Combine(_appDir, "cache");
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                await DownloadFileAsync(downloadUrl, tempZip, (pct, msg) =>
                {
                    ProgressChanged?.Invoke("sing-box", (int)(5 + pct * 0.8), $"Загрузка Sing-box: {pct}%");
                });

                ProgressChanged?.Invoke("sing-box", 88, "Распаковка ядра Sing-box...");

                var targetExe = Path.Combine(_binDir, "sing-box.exe");
                var oldTarget = targetExe + ".old";

                using (var archive = ZipFile.OpenRead(tempZip))
                {
                    ZipArchiveEntry? sbEntry = null;
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Name.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            sbEntry = entry;
                            break;
                        }
                    }

                    if (sbEntry == null)
                        throw new InvalidOperationException("Файл sing-box.exe не найден внутри загруженного zip-архива");

                    if (File.Exists(oldTarget))
                    {
                        try { File.Delete(oldTarget); } catch { }
                    }

                    if (File.Exists(targetExe))
                    {
                        try { File.Move(targetExe, oldTarget); }
                        catch { File.Delete(targetExe); }
                    }

                    sbEntry.ExtractToFile(targetExe, overwrite: true);
                }

                try { File.Delete(tempZip); } catch { }

                var newVer = GetSingBoxVersion();
                ProgressChanged?.Invoke("sing-box", 100, $"Ядро Sing-box обновлено ({newVer})!");
                return new { success = true, new_version = newVer, message = $"Ядро Sing-box успешно обновлено до {newVer}!" };
            }
            catch (Exception ex)
            {
                ProgressChanged?.Invoke("sing-box", 0, $"Ошибка: {ex.Message}");
                return new { success = false, error = ex.Message };
            }
        }

        private async Task<object> UpdateXrayAsync()
        {
            bool wasRunning = _xrayMgr.IsRunning;
            try
            {
                ProgressChanged?.Invoke("xray", 5, "Поиск свежей версии Xray на GitHub...");
                var check = await CheckXrayAsync();
                var downloadUrl = check.DownloadUrl;
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    downloadUrl = "https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip";
                }

                var tempZip = Path.Combine(_appDir, "cache", "xray-download.zip");
                var cacheDir = Path.Combine(_appDir, "cache");
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                await DownloadFileAsync(downloadUrl, tempZip, (pct, msg) =>
                {
                    ProgressChanged?.Invoke("xray", (int)(5 + pct * 0.8), $"Загрузка Xray: {pct}%");
                });

                ProgressChanged?.Invoke("xray", 88, "Остановка и обновление Xray...");

                if (wasRunning)
                {
                    _xrayMgr.Stop();
                    await Task.Delay(500);
                }

                var targetExe = Path.Combine(_binDir, "xray.exe");
                var oldTarget = targetExe + ".old";

                using (var archive = ZipFile.OpenRead(tempZip))
                {
                    ZipArchiveEntry? xrayEntry = null;
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Name.Equals("xray.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            xrayEntry = entry;
                            break;
                        }
                    }

                    if (xrayEntry == null)
                        throw new InvalidOperationException("Файл xray.exe не найден внутри загруженного zip-архива");

                    if (File.Exists(oldTarget))
                    {
                        try { File.Delete(oldTarget); } catch { }
                    }

                    if (File.Exists(targetExe))
                    {
                        try { File.Move(targetExe, oldTarget); }
                        catch { File.Delete(targetExe); }
                    }

                    xrayEntry.ExtractToFile(targetExe, overwrite: true);
                }

                try { File.Delete(tempZip); } catch { }

                if (wasRunning)
                {
                    ProgressChanged?.Invoke("xray", 95, "Перезапуск прокси...");
                    _xrayMgr.Start(_configMgr.Config);
                }

                var newVer = GetXrayVersion();
                ProgressChanged?.Invoke("xray", 100, $"Ядро Xray обновлено ({newVer})!");
                return new { success = true, new_version = newVer, message = $"Ядро Xray успешно обновлено до {newVer}!" };
            }
            catch (Exception ex)
            {
                if (wasRunning && !_xrayMgr.IsRunning)
                {
                    try { _xrayMgr.Start(_configMgr.Config); } catch { }
                }
                ProgressChanged?.Invoke("xray", 0, $"Ошибка: {ex.Message}");
                return new { success = false, error = ex.Message };
            }
        }

        private async Task<object> UpdateGeoAsync()
        {
            bool wasRunning = _xrayMgr.IsRunning;
            try
            {
                ProgressChanged?.Invoke("geo", 5, "Загрузка базы GeoIP...");

                var geoipUrl = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat";
                var geositeUrl = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat";

                var targetGeoip = Path.Combine(_binDir, "geoip.dat");
                var tempGeoip = targetGeoip + ".new";

                var targetGeosite = Path.Combine(_binDir, "geosite.dat");
                var tempGeosite = targetGeosite + ".new";

                await DownloadFileAsync(geoipUrl, tempGeoip, (pct, msg) =>
                {
                    ProgressChanged?.Invoke("geo", (int)(5 + pct * 0.45), $"Загрузка geoip.dat: {pct}%");
                });

                ProgressChanged?.Invoke("geo", 50, "Загрузка базы GeoSite...");

                await DownloadFileAsync(geositeUrl, tempGeosite, (pct, msg) =>
                {
                    ProgressChanged?.Invoke("geo", (int)(50 + pct * 0.45), $"Загрузка geosite.dat: {pct}%");
                });

                ProgressChanged?.Invoke("geo", 95, "Применение баз маршрутизации...");

                if (File.Exists(targetGeoip)) File.Delete(targetGeoip);
                File.Move(tempGeoip, targetGeoip);

                if (File.Exists(targetGeosite)) File.Delete(targetGeosite);
                File.Move(tempGeosite, targetGeosite);

                if (wasRunning)
                {
                    _xrayMgr.RestartIfRunning(_configMgr.Config);
                }

                var dtStr = DateTime.Now.ToString("yyyy-MM-dd");
                ProgressChanged?.Invoke("geo", 100, $"Базы GeoIP и GeoSite обновлены (от {dtStr})!");
                return new { success = true, new_version = dtStr, message = "Базы правил GeoIP и GeoSite успешно обновлены!" };
            }
            catch (Exception ex)
            {
                ProgressChanged?.Invoke("geo", 0, $"Ошибка: {ex.Message}");
                return new { success = false, error = ex.Message };
            }
        }

        private async Task DownloadFileAsync(string url, string destinationPath, Action<int, string>? progress = null)
        {
            using var client = CreateHttpClient(allowAutoRedirect: true, timeoutSec: 180);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            int lastReportedPct = -1;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    int pct = (int)((totalRead * 100) / totalBytes);
                    if (pct != lastReportedPct)
                    {
                        lastReportedPct = pct;
                        progress?.Invoke(pct, $"Загрузка: {pct}% ({totalRead / 1024 / 1024} МБ / {totalBytes / 1024 / 1024} МБ)");
                    }
                }
                else
                {
                    progress?.Invoke(50, $"Загружено {totalRead / 1024 / 1024} МБ");
                }
            }
        }

        public void RestartApplication()
        {
            try
            {
                try { _xrayMgr.Stop(); } catch { }
                var currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(_appDir, "bksh2ray.exe");
                Process.Start(new ProcessStartInfo
                {
                    FileName = currentExe,
                    WorkingDirectory = _appDir,
                    UseShellExecute = true
                });
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка перезапуска: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
