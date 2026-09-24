document.addEventListener('DOMContentLoaded', () => {
    
    // --- Elements ---
    const btnImport = document.getElementById('btn-import-clipboard');
    const btnRemove = document.getElementById('btn-remove-server');
    const btnToggle = document.getElementById('btn-toggle');
    const btnOpenBypass = document.getElementById('btn-open-bypass');
    const bypassModal = document.getElementById('bypass-modal');
    const btnCloseBypass = document.getElementById('btn-close-bypass');
    const btnCloseBypassFooter = document.getElementById('btn-close-bypass-footer');
    const inputBypassDomain = document.getElementById('input-bypass-domain');
    const btnAddDomain = document.getElementById('btn-add-domain');
    const customDomainList = document.getElementById('custom-domain-list');
    const customDomainCount = document.getElementById('custom-domain-count');
    const defaultRuList = document.getElementById('default-ru-list');
    
    const serverList = document.getElementById('server-list');
    const logOutput = document.getElementById('log-output');
    const btnClearLogs = document.getElementById('btn-clear-logs');
    
    const statusDot = document.getElementById('status-dot');
    const statusText = document.getElementById('status-text');
    const statusServer = document.getElementById('status-server');
    
    // Speedometer elements
    const speedIcon = document.getElementById('speed-icon');
    const speedDownVal = document.getElementById('speed-down-val');
    const speedUpVal = document.getElementById('speed-up-val');
    const speedTotal = document.getElementById('speed-total');

    // --- State ---
    let servers = [];
    let activeIndex = -1;
    let isRunning = false;
    
    // --- Formatting helper ---
    function formatBytes(bytes, isSpeed = false) {
        if (!bytes || bytes <= 0) return isSpeed ? '0.0 KB/s' : '0 B';
        const k = 1024;
        const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        const num = (bytes / Math.pow(k, i)).toFixed(1);
        const unit = sizes[i] || 'B';
        return isSpeed ? `${num} ${unit}/s` : `${num} ${unit}`;
    }

    // --- Reliable API Init ---
    function init() {
        if (window.pywebview && window.pywebview.api && window.pywebview.api.get_config) {
            startApp();
        } else {
            window.addEventListener('pywebviewready', startApp, { once: true });
            let retries = 0;
            const poller = setInterval(() => {
                retries++;
                if (window.pywebview && window.pywebview.api && window.pywebview.api.get_config) {
                    clearInterval(poller);
                    startApp();
                } else if (retries > 60) {
                    clearInterval(poller);
                }
            }, 100);
        }
    }
    
    async function startApp() {
        await loadConfig();
        await updateConnectionState();
    }
    
    async function loadConfig() {
        try {
            if (!window.pywebview || !window.pywebview.api) return;
            const conf = await window.pywebview.api.get_config();
            if (conf) {
                servers = conf.servers || [];
                activeIndex = conf.active_server_index !== undefined ? conf.active_server_index : -1;
                renderTable();
            }
        } catch (e) {
            console.error("loadConfig error:", e);
        }
    }
    
    // --- Actions ---
    // Reads directly from Windows native clipboard in Python (NO browser prompt!)
    btnImport.addEventListener('click', async () => {
        try {
            if (!window.pywebview || !window.pywebview.api) {
                showToast("Приложение ещё инициализируется...");
                return;
            }
            
            const res = await window.pywebview.api.import_from_clipboard();
            if (res.success) {
                showToast(res.message || "Сервер успешно добавлен!");
                await loadConfig();
            } else {
                showToast("Ошибка: " + res.error);
            }
        } catch (e) {
            showToast("Не удалось вставить из буфера");
        }
    });
    
    // Confirmation Modal helper
    function showConfirmModal(message, onConfirm) {
        const modal = document.getElementById('confirm-modal');
        const msgEl = document.getElementById('modal-msg');
        const btnConfirm = document.getElementById('btn-modal-confirm');
        const btnCancel = document.getElementById('btn-modal-cancel');
        if (!modal) return;

        msgEl.textContent = message;
        modal.classList.remove('hidden');

        const cleanup = () => {
            modal.classList.add('hidden');
            btnConfirm.onclick = null;
            btnCancel.onclick = null;
        };

        btnConfirm.onclick = () => {
            cleanup();
            onConfirm();
        };

        btnCancel.onclick = () => {
            cleanup();
        };
    }

    async function confirmDeleteActiveServer() {
        if (activeIndex >= 0 && activeIndex < servers.length) {
            const srv = servers[activeIndex];
            const srvName = srv.name || srv.server || `Сервер #${activeIndex + 1}`;
            showConfirmModal(`Вы действительно хотите удалить сервер «${srvName}»?`, async () => {
                const res = await window.pywebview.api.remove_server(activeIndex);
                if (res.success) {
                    showToast(`Сервер «${srvName}» удален`);
                    await loadConfig();
                } else {
                    showToast("Ошибка: " + res.error);
                }
            });
        } else {
            showToast("Сначала выберите сервер для удаления");
        }
    }

    btnRemove.addEventListener('click', confirmDeleteActiveServer);

    // Keyboard Shortcuts: Del to delete, Ctrl+V and Ctrl+C to insert
    document.addEventListener('keydown', (e) => {
        const modal = document.getElementById('confirm-modal');
        if (modal && !modal.classList.contains('hidden')) {
            if (e.key === 'Escape') {
                e.preventDefault();
                document.getElementById('btn-modal-cancel').click();
            } else if (e.key === 'Enter') {
                e.preventDefault();
                document.getElementById('btn-modal-confirm').click();
            }
            return;
        }

        if (bypassModal && !bypassModal.classList.contains('hidden')) {
            if (e.key === 'Escape') {
                e.preventDefault();
                closeBypassModal();
            }
            return;
        }

        const tag = document.activeElement ? document.activeElement.tagName.toLowerCase() : '';
        if (tag === 'input' || tag === 'textarea') return;

        // Delete key
        if (e.key === 'Delete' || e.code === 'Delete') {
            e.preventDefault();
            confirmDeleteActiveServer();
            return;
        }

        // Paste shortcuts: Ctrl+V or Ctrl+C
        if (e.ctrlKey || e.metaKey) {
            const key = e.key ? e.key.toLowerCase() : '';
            const code = e.code || '';
            if (key === 'v' || code === 'KeyV') {
                e.preventDefault();
                btnImport.click();
            } else if (key === 'c' || code === 'KeyC') {
                // If user doesn't have text highlighted in logs, treat Ctrl+C as paste request
                const selection = window.getSelection ? window.getSelection().toString() : '';
                if (!selection) {
                    e.preventDefault();
                    btnImport.click();
                }
            }
        }
    });

    // --- Bypass / Direct Sites Management ---
    function openBypassModal() {
        if (!bypassModal) return;
        bypassModal.classList.remove('hidden');
        loadBypassRules();
        if (inputBypassDomain) {
            setTimeout(() => inputBypassDomain.focus(), 50);
        }
    }

    function closeBypassModal() {
        if (bypassModal) {
            bypassModal.classList.add('hidden');
        }
    }

    if (btnOpenBypass) btnOpenBypass.addEventListener('click', openBypassModal);
    if (btnCloseBypass) btnCloseBypass.addEventListener('click', closeBypassModal);
    if (btnCloseBypassFooter) btnCloseBypassFooter.addEventListener('click', closeBypassModal);

    async function loadBypassRules() {
        try {
            if (!window.pywebview || !window.pywebview.api) return;
            const data = await window.pywebview.api.get_bypass_rules();
            if (!data) return;

            // Render Custom Domains
            const custom = data.custom_domains || [];
            if (customDomainCount) customDomainCount.textContent = custom.length;
            if (customDomainList) {
                customDomainList.innerHTML = '';
                if (custom.length === 0) {
                    const hint = document.createElement('div');
                    hint.className = 'custom-empty-hint';
                    hint.textContent = 'Нет добавленных сайтов. Введите домен выше, чтобы направить его в обход VPN.';
                    customDomainList.appendChild(hint);
                } else {
                    custom.forEach((domain) => {
                        const tag = document.createElement('div');
                        tag.className = 'custom-domain-tag';
                        tag.innerHTML = `
                            <span>${domain}</span>
                            <button class="btn-del-domain" title="Удалить «${domain}»">✕</button>
                        `;
                        const delBtn = tag.querySelector('.btn-del-domain');
                        delBtn.addEventListener('click', async (evt) => {
                            evt.stopPropagation();
                            const res = await window.pywebview.api.remove_bypass_domain(domain);
                            if (res.success) {
                                showToast(`Сайт «${domain}» удален из прямого доступа`);
                                await loadBypassRules();
                            } else {
                                showToast("Ошибка: " + res.error);
                            }
                        });
                        customDomainList.appendChild(tag);
                    });
                }
            }

            // Render Default RU Rules
            const defaults = data.default_rules || [];
            if (defaultRuList) {
                defaultRuList.innerHTML = '';
                defaults.forEach((item) => {
                    const card = document.createElement('div');
                    card.className = 'default-ru-item';
                    card.innerHTML = `
                        <div class="default-ru-top">
                            <span class="default-ru-name">${item.name}</span>
                            <span class="default-ru-rule">${item.rule}</span>
                        </div>
                        <span class="default-ru-desc">${item.desc}</span>
                    `;
                    defaultRuList.appendChild(card);
                });
            }
        } catch (e) {
            console.error("loadBypassRules error:", e);
        }
    }

    async function handleAddDomain() {
        if (!inputBypassDomain) return;
        let val = inputBypassDomain.value.trim().toLowerCase();
        val = val.replace(/^https?:\/\//, '').replace(/\/.*$/, '').trim();
        if (!val) {
            showToast("Введите адрес сайта для обхода VPN");
            return;
        }

        try {
            const res = await window.pywebview.api.add_bypass_domain(val);
            if (res.success) {
                showToast(`Сайт «${val}» добавлен для прямого доступа!`);
                inputBypassDomain.value = '';
                await loadBypassRules();
            } else {
                showToast("Ошибка: " + (res.error || "Не удалось добавить"));
            }
        } catch (e) {
            showToast("Ошибка при добавлении сайта");
        }
    }

    if (btnAddDomain) btnAddDomain.addEventListener('click', handleAddDomain);
    if (inputBypassDomain) {
        inputBypassDomain.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                handleAddDomain();
            }
        });
    }
    
    btnToggle.addEventListener('click', async () => {
        if (isRunning) {
            const res = await window.pywebview.api.stop_proxy();
            if (res.success) await updateConnectionState();
            else showToast(res.error);
        } else {
            const res = await window.pywebview.api.start_proxy();
            if (res.success) await updateConnectionState();
            else showToast(res.error);
        }
    });
    
    btnClearLogs.addEventListener('click', () => {
        logOutput.innerHTML = '';
        showToast("Журнал очищен");
    });
    
    // --- UI Render ---
    function renderTable() {
        serverList.innerHTML = '';
        if (servers.length === 0) {
            const tr = document.createElement('tr');
            tr.innerHTML = `<td colspan="6" style="text-align:center; padding: 24px; color: #94a3b8;">
                Список серверов пуст. Нажмите «Вставить из буфера», чтобы добавить сервер.
            </td>`;
            serverList.appendChild(tr);
            statusServer.textContent = "Сервер не выбран";
            return;
        }

        servers.forEach((srv, idx) => {
            const tr = document.createElement('tr');
            if (idx === activeIndex) tr.classList.add('selected');
            
            tr.innerHTML = `
                <td>${idx + 1}</td>
                <td><span class="badge-protocol">VLESS</span></td>
                <td><strong>${srv.name || 'Без названия'}</strong></td>
                <td>${srv.server}</td>
                <td>${srv.server_port}</td>
                <td><span class="badge-sec">${srv.security || 'none'}</span></td>
            `;
            
            tr.addEventListener('click', async () => {
                await window.pywebview.api.set_active_server(idx);
                await loadConfig();
            });
            
            serverList.appendChild(tr);
        });
        
        if (activeIndex >= 0 && activeIndex < servers.length) {
            statusServer.textContent = `Активный сервер: ${servers[activeIndex].name || servers[activeIndex].server} (${servers[activeIndex].server}:${servers[activeIndex].server_port})`;
        } else {
            statusServer.textContent = "Сервер не выбран";
        }
    }
    
    // Export for python to call
    window.updateConnectionState = async function() {
        if (!window.pywebview) return;
        const status = await window.pywebview.api.get_status();
        isRunning = status.is_running;
        
        if (isRunning) {
            btnToggle.innerHTML = '🛑 Остановить';
            btnToggle.classList.add('running');
            statusDot.classList.replace("disconnected", "connected");
            statusText.textContent = "Подключено (Прокси активен)";
        } else {
            btnToggle.innerHTML = '🚀 Запустить';
            btnToggle.classList.remove('running');
            statusDot.classList.replace("connected", "disconnected");
            statusText.textContent = "Готов к работе";
            // Reset speedometer
            if (speedDownVal) speedDownVal.textContent = '0.0 KB/s';
            if (speedUpVal) speedUpVal.textContent = '0.0 KB/s';
            if (speedIcon) speedIcon.classList.remove('active');
        }
    }
    
    window.updateActivePorts = function(httpPort, socksPort) {
        const el = document.querySelector('.status-proxy-info');
        if (el) el.textContent = `127.0.0.1:${httpPort} (SOCKS5: ${socksPort})`;
    }

    // Speedometer live updates from Python
    window.updateSpeed = function(downBps, upBps, totalDown, totalUp) {
        if (speedDownVal) speedDownVal.textContent = formatBytes(downBps, true);
        if (speedUpVal) speedUpVal.textContent = formatBytes(upBps, true);
        if (speedTotal) speedTotal.textContent = `(${formatBytes(totalDown + totalUp)})`;
        
        if (speedIcon) {
            if (downBps > 1024 || upBps > 1024) {
                speedIcon.classList.add('active');
            } else {
                speedIcon.classList.remove('active');
            }
        }
    }
    
    window.addLog = function(line) {
        const div = document.createElement('div');
        div.className = 'log-line';
        
        if (line.toLowerCase().includes('error')) div.classList.add('error');
        else if (line.toLowerCase().includes('warn')) div.classList.add('warn');
        
        div.textContent = line;
        logOutput.appendChild(div);
        
        // Auto scroll to bottom
        logOutput.scrollTop = logOutput.scrollHeight;
    }
    
    // --- Resizer logic ---
    const resizer = document.getElementById('log-resizer');
    const logPanel = document.getElementById('log-panel');
    let isResizing = false;
    
    resizer.addEventListener('mousedown', (e) => {
        isResizing = true;
        document.body.style.cursor = 'row-resize';
    });
    
    document.addEventListener('mousemove', (e) => {
        if (!isResizing) return;
        const newHeight = document.body.clientHeight - e.clientY - 32;
        if (newHeight > 60 && newHeight < document.body.clientHeight - 120) {
            logPanel.style.height = `${newHeight}px`;
        }
    });
    
    document.addEventListener('mouseup', () => {
        if (isResizing) {
            isResizing = false;
            document.body.style.cursor = 'default';
        }
    });
    
    // Toast Utility
    const toast = document.getElementById('toast');
    let toastTimeout;
    function showToast(msg) {
        toast.textContent = msg;
        toast.classList.remove('hidden');
        clearTimeout(toastTimeout);
        toastTimeout = setTimeout(() => {
            toast.classList.add('hidden');
        }, 3000);
    }
    
    window.loadConfig = loadConfig;
    window.startApp = startApp;
    
    init();
});
