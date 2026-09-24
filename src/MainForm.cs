using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace bksh2ray
{
    public class MainForm : Form
    {
        private readonly string _appDir;
        private readonly ConfigManager _configMgr;
        private readonly XrayManager _xrayMgr;
        private readonly UpdateManager _updateMgr;

        private readonly WebView2 _webView;
        private readonly NotifyIcon _trayIcon;
        private bool _reallyQuit = false;

        public MainForm()
        {
            _appDir = AppDomain.CurrentDomain.BaseDirectory;
            _configMgr = new ConfigManager(_appDir);
            _xrayMgr = new XrayManager(_appDir);
            _updateMgr = new UpdateManager(_appDir, _xrayMgr, _configMgr);

            // Window properties
            Text = "bksh2ray";
            Width = 1020;
            Height = 750;
            MinimumSize = new Size(800, 600);
            StartPosition = FormStartPosition.CenterScreen;

            // Load chicken shish application icon
            var appIcon = CreateAppIcon(_appDir);
            Icon = appIcon;

            // Tray icon setup
            var trayMenu = new ContextMenuStrip();
            var itemShow = new ToolStripMenuItem("Показать bksh2ray", null, (s, e) => RestoreFromTray()) { Font = new Font(Font, FontStyle.Bold) };
            var itemQuit = new ToolStripMenuItem("Выход", null, (s, e) => ExitApplication());
            trayMenu.Items.Add(itemShow);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(itemQuit);

            _trayIcon = new NotifyIcon
            {
                Icon = appIcon,
                Text = "bksh2ray",
                Visible = true,
                ContextMenuStrip = trayMenu
            };
            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    RestoreFromTray();
            };

            // WebView2 control
            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);

            // Event handlers
            _xrayMgr.LogReceived += (line) =>
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(() =>
                    {
                        var json = JsonSerializer.Serialize(line);
                        _webView.CoreWebView2?.ExecuteScriptAsync($"window.addLog && window.addLog({json});");
                    });
                }
            };

            _xrayMgr.SpeedUpdated += (downBps, upBps, currDown, currUp) =>
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(() =>
                    {
                        _webView.CoreWebView2?.ExecuteScriptAsync($"window.updateSpeed && window.updateSpeed({downBps.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {upBps.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {currDown}, {currUp});");
                    });
                }
            };

            _xrayMgr.StateChanged += () =>
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(() =>
                    {
                        _webView.CoreWebView2?.ExecuteScriptAsync("window.updateConnectionState && window.updateConnectionState();");
                    });
                }
            };

            _updateMgr.ProgressChanged += (comp, pct, text) =>
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(() =>
                    {
                        var compJson = JsonSerializer.Serialize(comp);
                        var textJson = JsonSerializer.Serialize(text);
                        _webView.CoreWebView2?.ExecuteScriptAsync($"window.onUpdateProgress && window.onUpdateProgress({compJson}, {pct}, {textJson});");
                    });
                }
            };

            Load += OnFormLoadAsync;
            FormClosing += OnFormClosing;
        }

        private async void OnFormLoadAsync(object? sender, EventArgs e)
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(_appDir, "cache"));
                await _webView.EnsureCoreWebView2Async(env);

                // Disable default context menu & status bar
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                // Hook WebMessageReceived for native bridge
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                // Inject transparent PyWebView API bridge
                await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                    const pendingCalls = new Map();
                    let callId = 0;

                    window.chrome.webview.addEventListener('message', (event) => {
                        const data = event.data;
                        if (data && data.id && pendingCalls.has(data.id)) {
                            const { resolve, reject } = pendingCalls.get(data.id);
                            pendingCalls.delete(data.id);
                            if (data.error) reject(new Error(data.error));
                            else resolve(data.result);
                        }
                    });

                    function callNative(method, ...args) {
                        return new Promise((resolve, reject) => {
                            const id = (++callId).toString();
                            pendingCalls.set(id, { resolve, reject });
                            window.chrome.webview.postMessage({ id, method, args });
                        });
                    }

                    window.pywebview = {
                        api: {
                            get_config: () => callNative('get_config'),
                            get_status: () => callNative('get_status'),
                            set_active_server: (idx) => callNative('set_active_server', idx),
                            remove_server: (idx) => callNative('remove_server', idx),
                            import_from_clipboard: () => callNative('import_from_clipboard'),
                            start_proxy: () => callNative('start_proxy'),
                            stop_proxy: () => callNative('stop_proxy'),
                            get_bypass_rules: () => callNative('get_bypass_rules'),
                            add_bypass_domain: (dom) => callNative('add_bypass_domain', dom),
                            remove_bypass_domain: (dom) => callNative('remove_bypass_domain', dom),
                            check_updates: () => callNative('check_updates'),
                            update_component: (name) => callNative('update_component', name),
                            update_all: () => callNative('update_all'),
                            restart_app: () => callNative('restart_app')
                        }
                    };

                    window.dispatchEvent(new CustomEvent('pywebviewready'));
                ");

                // Navigate to UI HTML file
                var uiHtml = Path.Combine(_appDir, "ui", "index.html");
                _webView.CoreWebView2.NavigationCompleted += (s, args) =>
                {
                    _webView.CoreWebView2.ExecuteScriptAsync("window.startApp && window.startApp();");
                };

                if (File.Exists(uiHtml))
                {
                    _webView.CoreWebView2.Navigate(new Uri(uiHtml).AbsoluteUri);
                }
                else
                {
                    _webView.CoreWebView2.NavigateToString("<h1>Ошибка: ui/index.html не найден</h1>");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка инициализации WebView2: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                var id = root.GetProperty("id").GetString() ?? "";
                var method = root.GetProperty("method").GetString() ?? "";

                object? result = null;
                string? error = null;

                switch (method)
                {
                    case "get_config":
                        result = _configMgr.Config;
                        break;

                    case "get_status":
                        result = new { is_running = _xrayMgr.IsRunning };
                        break;

                    case "set_active_server":
                        int srvIdx = root.GetProperty("args")[0].GetInt32();
                        if (srvIdx >= 0 && srvIdx < _configMgr.Config.Servers.Count)
                        {
                            _configMgr.Config.ActiveServerIndex = srvIdx;
                            _configMgr.Save();
                            result = new { success = true };
                        }
                        else
                        {
                            result = new { success = false, error = "Неверный индекс сервера" };
                        }
                        break;

                    case "remove_server":
                        int delIdx = root.GetProperty("args")[0].GetInt32();
                        if (delIdx >= 0 && delIdx < _configMgr.Config.Servers.Count)
                        {
                            _configMgr.Config.Servers.RemoveAt(delIdx);
                            if (_configMgr.Config.ActiveServerIndex >= _configMgr.Config.Servers.Count)
                                _configMgr.Config.ActiveServerIndex = _configMgr.Config.Servers.Count - 1;
                            _configMgr.Save();
                            result = new { success = true };
                        }
                        else
                        {
                            result = new { success = false, error = "Неверный индекс сервера" };
                        }
                        break;

                    case "import_from_clipboard":
                        string text = "";
                        try
                        {
                            if (Clipboard.ContainsText())
                                text = Clipboard.GetText();
                        }
                        catch { }

                        text = text.Trim().Replace("\ufeff", "");
                        if (string.IsNullOrEmpty(text))
                        {
                            result = new { success = false, error = "Буфер обмена пуст или не содержит текст" };
                            break;
                        }

                        string targetLink = "";
                        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var clean = line.Trim().Trim('"', '\'', '`', '<', '>');
                            if (clean.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                            {
                                targetLink = clean;
                                break;
                            }
                        }

                        if (string.IsNullOrEmpty(targetLink) && text.Contains("vless://", StringComparison.OrdinalIgnoreCase))
                        {
                            int idx = text.IndexOf("vless://", StringComparison.OrdinalIgnoreCase);
                            targetLink = text.Substring(idx).Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim('"', '\'', '`', '<', '>');
                        }

                        if (!string.IsNullOrEmpty(targetLink))
                        {
                            var (ok, msg, err) = _configMgr.ImportVless(targetLink);
                            if (ok) result = new { success = true, message = msg };
                            else result = new { success = false, error = err };
                        }
                        else
                        {
                            string snippet = text.Length > 35 ? text.Substring(0, 35) + "..." : text;
                            result = new { success = false, error = $"В буфере не найдена ссылка vless:// (текст: \"{snippet}\")" };
                        }
                        break;

                    case "start_proxy":
                        try
                        {
                            _xrayMgr.Start(_configMgr.Config);
                            _webView.CoreWebView2?.ExecuteScriptAsync($"window.updateActivePorts && window.updateActivePorts({_xrayMgr.HttpPort}, {_xrayMgr.SocksPort});");
                            result = new { success = true };
                        }
                        catch (Exception ex)
                        {
                            result = new { success = false, error = ex.Message };
                        }
                        break;

                    case "stop_proxy":
                        _xrayMgr.Stop();
                        result = new { success = true };
                        break;

                    case "get_bypass_rules":
                        result = new
                        {
                            custom_domains = _configMgr.Config.CustomDirectDomains,
                            default_ru_enabled = true,
                            default_rules = new[]
                            {
                                new { name = "Все домены зоны .RU, .РФ, .SU", rule = "geosite:category-ru", desc = "Все государственные и коммерческие сайты РФ" },
                                new { name = "Российские IP-адреса операторов", rule = "geoip:ru", desc = "Все подсети и провайдеры РФ" },
                                new { name = "Госуслуги, Мос.ру и ведомства", rule = "gosuslugi.ru, mos.ru, nalog.gov.ru", desc = "Государственные сервисы" },
                                new { name = "Банки РФ (Сбер, Т-Банк, ВТБ, Альфа...)", rule = "sberbank.ru, tbank.ru, vtb.ru, alfabank.ru", desc = "Банковские приложения и онлайн-кабинеты" },
                                new { name = "Экосистемы Яндекс, VK, Mail.ru", rule = "yandex.ru, ya.ru, vk.com, mail.ru", desc = "Поиск, почта, музыка, видео, соцсети" },
                                new { name = "Маркетплейсы (Ozon, Wildberries, Avito...)", rule = "ozon.ru, wildberries.ru, avito.ru, market.yandex.ru", desc = "Онлайн-покупки и службы доставки" },
                                new { name = "Стриминг и видео (Кинопоиск, Rutube, Иви...)", rule = "kinopoisk.ru, rutube.ru, ivi.ru, okko.tv", desc = "Российские онлайн-кинотеатры и видео" }
                            }
                        };
                        break;

                    case "add_bypass_domain":
                        var domToAdd = root.GetProperty("args")[0].GetString() ?? "";
                        domToAdd = domToAdd.Trim().ToLowerInvariant();
                        if (domToAdd.Contains("://")) domToAdd = domToAdd.Substring(domToAdd.IndexOf("://") + 3);
                        domToAdd = domToAdd.Split('/')[0].Split(':')[0].Trim('"', '\'', '`', '<', '>');

                        if (string.IsNullOrEmpty(domToAdd))
                        {
                            result = new { success = false, error = "Пустой адрес сайта" };
                        }
                        else if (_configMgr.Config.CustomDirectDomains.Contains(domToAdd))
                        {
                            result = new { success = false, error = "Этот сайт уже есть в списке" };
                        }
                        else
                        {
                            _configMgr.Config.CustomDirectDomains.Add(domToAdd);
                            _configMgr.Save();
                            _xrayMgr.RestartIfRunning(_configMgr.Config);
                            result = new { success = true, domains = _configMgr.Config.CustomDirectDomains };
                        }
                        break;

                    case "remove_bypass_domain":
                        var domToRem = root.GetProperty("args")[0].GetString() ?? "";
                        domToRem = domToRem.Trim().ToLowerInvariant();
                        if (_configMgr.Config.CustomDirectDomains.Remove(domToRem))
                        {
                            _configMgr.Save();
                            _xrayMgr.RestartIfRunning(_configMgr.Config);
                            result = new { success = true, domains = _configMgr.Config.CustomDirectDomains };
                        }
                        else
                        {
                            result = new { success = false, error = "Сайт не найден в списке" };
                        }
                        break;

                    case "check_updates":
                        var updateList = await _updateMgr.CheckAllUpdatesAsync();
                        result = new { success = true, components = updateList, app_version = UpdateManager.CurrentAppVersion };
                        break;

                    case "update_component":
                        var compName = root.GetProperty("args")[0].GetString() ?? "";
                        result = await _updateMgr.UpdateComponentAsync(compName);
                        break;

                    case "update_all":
                        var allComps = await _updateMgr.CheckAllUpdatesAsync();
                        var resList = new List<object>();
                        foreach (var c in allComps)
                        {
                            if (c.HasUpdate)
                            {
                                var res = await _updateMgr.UpdateComponentAsync(c.Id);
                                resList.Add(res);
                            }
                        }
                        result = new { success = true, updated = resList.Count, details = resList };
                        break;

                    case "restart_app":
                        RestartApplication();
                        result = new { success = true };
                        break;

                    default:
                        error = $"Неизвестный метод: {method}";
                        break;
                }

                var reply = JsonSerializer.Serialize(new { id, result, error });
                _webView.CoreWebView2?.PostWebMessageAsJson(reply);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bridge] Error processing message: {ex.Message}");
            }
        }

        private void RestartApplication()
        {
            _reallyQuit = true;
            try
            {
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
            }
            catch { }

            try
            {
                _xrayMgr.Stop();
            }
            catch { }

            var currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(_appDir, "bksh2ray.exe");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = currentExe,
                    WorkingDirectory = _appDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка перезапуска: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Environment.Exit(0);
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            _reallyQuit = true;
            _xrayMgr.Stop();
            _trayIcon.Visible = false;
            Close();
            Application.Exit();
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            if (!_reallyQuit)
            {
                e.Cancel = true;
                Hide(); // Silent minimize - completely silent, without any notification
            }
            else
            {
                _xrayMgr.Stop();
                _trayIcon.Visible = false;
            }
        }

        private static Icon CreateAppIcon(string appDir)
        {
            var icoFile = Path.Combine(appDir, "app.ico");
            if (File.Exists(icoFile))
            {
                try
                {
                    return new Icon(icoFile);
                }
                catch { }
            }

            try
            {
                var exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exeIcon != null) return exeIcon;
            }
            catch { }

            return SystemIcons.Application;
        }
    }
}
