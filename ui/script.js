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
    const statusModeInfo = document.getElementById('status-mode-info');
    
    // Speedometer elements
    const speedIcon = document.getElementById('speed-icon');
    const speedDownVal = document.getElementById('speed-down-val');
    const speedUpVal = document.getElementById('speed-up-val');
    const speedTotal = document.getElementById('speed-total');

    // Context Menus
    const modeContextMenu = document.getElementById('mode-context-menu');
    const menuModeProxy = document.getElementById('menu-mode-proxy');
    const menuModeTun = document.getElementById('menu-mode-tun');
    const checkModeProxy = document.getElementById('check-mode-proxy');
    const checkModeTun = document.getElementById('check-mode-tun');

    const serverContextMenu = document.getElementById('server-context-menu');
    const ctxServerConnect = document.getElementById('ctx-server-connect');
    const ctxServerEdit = document.getElementById('ctx-server-edit');
    const ctxServerDelete = document.getElementById('ctx-server-delete');
    let contextMenuServerIndex = -1;

    // Edit Server Modal
    const editServerModal = document.getElementById('edit-server-modal');
    const btnCloseEdit = document.getElementById('btn-close-edit');
    const btnCancelEdit = document.getElementById('btn-cancel-edit');
    const btnSaveEdit = document.getElementById('btn-save-edit');
    const editServerIndex = document.getElementById('edit-server-index');
    const editServerName = document.getElementById('edit-server-name');
    const editServerHost = document.getElementById('edit-server-host');
    const editServerPort = document.getElementById('edit-server-port');
    const editServerUuid = document.getElementById('edit-server-uuid');
    const editServerSecurity = document.getElementById('edit-server-security');
    const editServerFlow = document.getElementById('edit-server-flow');
    const editServerSni = document.getElementById('edit-server-sni');
    const editServerFp = document.getElementById('edit-server-fp');
    const editServerPbk = document.getElementById('edit-server-pbk');
    const editServerSid = document.getElementById('edit-server-sid');
    const editServerSpx = document.getElementById('edit-server-spx');

    // --- State ---
    let servers = [];
    let activeIndex = -1;
    let isRunning = false;
    let currentMode = "proxy";
    
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

        if (modeContextMenu && !modeContextMenu.classList.contains('hidden')) {
            if (e.key === 'Escape') {
                e.preventDefault();
                hideModeContextMenu();
                return;
            }
        }

        if (serverContextMenu && !serverContextMenu.classList.contains('hidden')) {
            if (e.key === 'Escape') {
                e.preventDefault();
                hideServerContextMenu();
                return;
            }
        }

        if (editServerModal && !editServerModal.classList.contains('hidden')) {
            if (e.key === 'Escape') {
                e.preventDefault();
                closeEditModal();
                return;
            }
        }

        if (bypassModal && !bypassModal.classList.contains('hidden')) {
            if (e.key === 'Escape') {
                e.preventDefault();
                closeBypassModal();
                return;
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

    // Right-click on btnToggle -> Mode context menu
    btnToggle.addEventListener('contextmenu', (e) => {
        e.preventDefault();
        showModeContextMenu(e.clientX, e.clientY);
    });

    function showModeContextMenu(x, y) {
        hideServerContextMenu();
        if (!modeContextMenu) return;

        if (currentMode === 'tun') {
            if (checkModeTun) checkModeTun.style.display = 'inline';
            if (checkModeProxy) checkModeProxy.style.display = 'none';
        } else {
            if (checkModeTun) checkModeTun.style.display = 'none';
            if (checkModeProxy) checkModeProxy.style.display = 'inline';
        }

        const menuW = 270;
        const menuH = 150;
        const posX = Math.min(x, window.innerWidth - menuW - 10);
        const posY = Math.min(y, window.innerHeight - menuH - 10);

        modeContextMenu.style.left = `${Math.max(10, posX)}px`;
        modeContextMenu.style.top = `${Math.max(10, posY)}px`;
        modeContextMenu.classList.remove('hidden');
    }

    function hideModeContextMenu() {
        if (modeContextMenu) modeContextMenu.classList.add('hidden');
    }

    menuModeProxy?.addEventListener('click', () => switchMode('proxy'));
    menuModeTun?.addEventListener('click', () => switchMode('tun'));

    async function switchMode(newMode) {
        hideModeContextMenu();
        if (newMode === currentMode) return;
        try {
            const res = await window.pywebview.api.set_mode(newMode);
            if (res && res.success) {
                currentMode = newMode;
                showToast(newMode === 'tun' ? "Включен режим TUN (сетевой адаптер)" : "Включен системный прокси");
                await updateConnectionState();
            } else {
                showToast("Ошибка смены режима: " + (res?.error || "Сбой"));
            }
        } catch (e) {
            showToast("Ошибка: " + e);
        }
    }

    // --- Server Context Menu & Editing ---
    function showServerContextMenu(idx, x, y) {
        hideModeContextMenu();
        if (!serverContextMenu) return;
        contextMenuServerIndex = idx;

        const menuW = 200;
        const menuH = 150;
        const posX = Math.min(x, window.innerWidth - menuW - 10);
        const posY = Math.min(y, window.innerHeight - menuH - 10);

        serverContextMenu.style.left = `${Math.max(10, posX)}px`;
        serverContextMenu.style.top = `${Math.max(10, posY)}px`;
        serverContextMenu.classList.remove('hidden');
    }

    function hideServerContextMenu() {
        if (serverContextMenu) serverContextMenu.classList.add('hidden');
        contextMenuServerIndex = -1;
    }

    ctxServerConnect?.addEventListener('click', async () => {
        const targetIdx = contextMenuServerIndex;
        hideServerContextMenu();
        if (targetIdx < 0 || targetIdx >= servers.length) return;

        const srv = servers[targetIdx];
        await window.pywebview.api.set_active_server(targetIdx);
        await loadConfig();

        if (!isRunning) {
            const res = await window.pywebview.api.start_proxy();
            if (res && res.success) await updateConnectionState();
            else showToast(res ? res.error : "Ошибка запуска");
        } else {
            showToast(`Переключено на «${srv.name || srv.server}»`);
            await updateConnectionState();
        }
    });

    ctxServerEdit?.addEventListener('click', () => {
        const targetIdx = contextMenuServerIndex;
        hideServerContextMenu();
        if (targetIdx < 0 || targetIdx >= servers.length) return;
        openEditModal(targetIdx);
    });

    ctxServerDelete?.addEventListener('click', () => {
        const targetIdx = contextMenuServerIndex;
        hideServerContextMenu();
        if (targetIdx < 0 || targetIdx >= servers.length) return;

        const srv = servers[targetIdx];
        const srvName = srv.name || srv.server || `Сервер #${targetIdx + 1}`;
        showConfirmModal(`Вы действительно хотите удалить сервер «${srvName}»?`, async () => {
            const res = await window.pywebview.api.remove_server(targetIdx);
            if (res && res.success) {
                showToast(`Сервер «${srvName}» удален`);
                await loadConfig();
                if (isRunning) await updateConnectionState();
            } else {
                showToast("Ошибка: " + (res?.error || "Сбой"));
            }
        });
    });

    function openEditModal(idx) {
        const srv = servers[idx];
        if (!srv || !editServerModal) return;

        editServerIndex.value = idx;
        editServerName.value = srv.name || '';
        editServerHost.value = srv.server || '';
        editServerPort.value = srv.server_port || 443;
        editServerUuid.value = srv.uuid || '';
        editServerSecurity.value = srv.security || 'reality';
        editServerFlow.value = srv.flow || '';
        editServerSni.value = srv.sni || '';
        editServerFp.value = srv.fp || '';
        editServerPbk.value = srv.pbk || '';
        editServerSid.value = srv.sid || '';
        editServerSpx.value = srv.spx || '';

        editServerModal.classList.remove('hidden');
        setTimeout(() => editServerName.focus(), 60);
    }

    function closeEditModal() {
        if (editServerModal) editServerModal.classList.add('hidden');
    }

    btnCloseEdit?.addEventListener('click', closeEditModal);
    btnCancelEdit?.addEventListener('click', closeEditModal);

    btnSaveEdit?.addEventListener('click', async () => {
        const idx = parseInt(editServerIndex.value, 10);
        if (isNaN(idx) || idx < 0 || idx >= servers.length) return;

        const srvName = editServerName.value.trim();
        const serverHost = editServerHost.value.trim();
        const serverPort = parseInt(editServerPort.value, 10);
        const serverUuid = editServerUuid.value.trim();

        if (!serverHost) {
            showToast("Укажите адрес сервера");
            editServerHost.focus();
            return;
        }
        if (isNaN(serverPort) || serverPort <= 0 || serverPort > 65535) {
            showToast("Укажите корректный порт (1-65535)");
            editServerPort.focus();
            return;
        }
        if (!serverUuid) {
            showToast("Укажите UUID");
            editServerUuid.focus();
            return;
        }

        const updatedData = {
            name: srvName || serverHost,
            server: serverHost,
            server_port: serverPort,
            uuid: serverUuid,
            security: editServerSecurity.value,
            flow: editServerFlow.value.trim(),
            sni: editServerSni.value.trim(),
            fp: editServerFp.value.trim(),
            pbk: editServerPbk.value.trim(),
            sid: editServerSid.value.trim(),
            spx: editServerSpx.value.trim()
        };

        try {
            const res = await window.pywebview.api.edit_server(idx, updatedData);
            if (res && res.success) {
                closeEditModal();
                showToast("Параметры сервера сохранены");
                await loadConfig();
                if (isRunning) await updateConnectionState();
            } else {
                showToast("Ошибка сохранения: " + (res?.error || "Сбой"));
            }
        } catch (e) {
            showToast("Ошибка сохранения: " + e);
        }
    });

    // Close context menus on document click
    document.addEventListener('click', (e) => {
        if (modeContextMenu && !modeContextMenu.classList.contains('hidden')) {
            if (!e.target.closest('#mode-context-menu') && !e.target.closest('#btn-toggle')) {
                hideModeContextMenu();
            }
        }
        if (serverContextMenu && !serverContextMenu.classList.contains('hidden')) {
            if (!e.target.closest('#server-context-menu')) {
                hideServerContextMenu();
            }
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
            
            // Left click: select server, and if VPN is running, switch instantly on the fly!
            tr.addEventListener('click', async () => {
                if (idx === activeIndex && !isRunning) return;
                await window.pywebview.api.set_active_server(idx);
                await loadConfig();
                if (isRunning) {
                    showToast(`Переключено на «${srv.name || srv.server}»`);
                    await updateConnectionState();
                }
            });

            // Right click: open server context menu
            tr.addEventListener('contextmenu', (e) => {
                e.preventDefault();
                showServerContextMenu(idx, e.clientX, e.clientY);
            });
            
            serverList.appendChild(tr);
        });
        
        if (activeIndex >= 0 && activeIndex < servers.length) {
            statusServer.textContent = `Активный: ${servers[activeIndex].name || servers[activeIndex].server} (${servers[activeIndex].server}:${servers[activeIndex].server_port})`;
        } else {
            statusServer.textContent = "Сервер не выбран";
        }
    }
    
    // Export for python to call
    window.updateConnectionState = async function() {
        if (!window.pywebview) return;
        const status = await window.pywebview.api.get_status();
        isRunning = status.is_running;
        if (status.mode) currentMode = status.mode;

        if (statusModeInfo) {
            if (currentMode === 'tun') {
                statusModeInfo.textContent = 'Режим: TUN (bksh2ray_tun)';
            } else {
                statusModeInfo.textContent = '127.0.0.1:10809 (SOCKS5: 10808)';
            }
        }
        
        if (isRunning) {
            btnToggle.innerHTML = '🛑 Остановить';
            btnToggle.classList.add('running');
            statusDot.classList.replace("disconnected", "connected");
            statusText.textContent = currentMode === 'tun' ? "Подключено (TUN адаптер)" : "Подключено (Прокси активен)";
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
        }, 3500);
    }

    // --- Updates Modal & Logic ---
    const btnOpenUpdates = document.getElementById('btn-open-updates');
    const updateModal = document.getElementById('update-modal');
    const btnCloseUpdate = document.getElementById('btn-close-update');
    const btnCloseUpdateFooter = document.getElementById('btn-close-update-footer');
    const btnCheckUpdates = document.getElementById('btn-check-updates');
    const btnUpdateAll = document.getElementById('btn-update-all');
    const updateStatusSummary = document.getElementById('update-status-summary');
    const componentsList = document.getElementById('components-list');
    const btnRestartApp = document.getElementById('btn-restart-app');
    const updateBadge = document.getElementById('update-badge');
    const checkIcon = document.getElementById('check-icon');
    const checkBtnText = document.getElementById('check-btn-text');

    let currentComponents = [];
    let isUpdating = false;

    // Open/Close modal
    btnOpenUpdates?.addEventListener('click', () => {
        updateModal.classList.remove('hidden');
        checkUpdates();
    });

    btnCloseUpdate?.addEventListener('click', () => {
        if (!isUpdating) updateModal.classList.add('hidden');
    });

    btnCloseUpdateFooter?.addEventListener('click', () => {
        if (!isUpdating) updateModal.classList.add('hidden');
    });

    btnCheckUpdates?.addEventListener('click', () => {
        checkUpdates();
    });

    btnRestartApp?.addEventListener('click', async () => {
        try {
            if (window.pywebview?.api?.restart_app) {
                await window.pywebview.api.restart_app();
            }
        } catch (e) {
            showToast("Ошибка при перезапуске: " + e);
        }
    });

    // Check updates
    async function checkUpdates(silent = false) {
        if (!window.pywebview?.api?.check_updates) return;
        if (isUpdating) return;

        if (!silent) {
            btnCheckUpdates.disabled = true;
            if (checkIcon) checkIcon.textContent = "⏳";
            if (checkBtnText) checkBtnText.textContent = "Проверка...";
            if (updateStatusSummary) updateStatusSummary.textContent = "Запрос версий с GitHub...";
        }

        try {
            const res = await window.pywebview.api.check_updates();
            if (res && res.success && res.components) {
                if (res.app_version) {
                    const tag = document.getElementById('app-version-tag');
                    if (tag) tag.textContent = res.app_version;
                }
                currentComponents = res.components;
                renderComponentCards(currentComponents);

                const outdated = currentComponents.filter(c => c.HasUpdate || c.has_update);
                if (outdated.length > 0) {
                    updateBadge.classList.remove('hidden');
                    btnUpdateAll.disabled = false;
                    if (updateStatusSummary) {
                        updateStatusSummary.textContent = `Доступно обновлений: ${outdated.length} из ${currentComponents.length}`;
                    }
                } else {
                    updateBadge.classList.add('hidden');
                    btnUpdateAll.disabled = true;
                    if (updateStatusSummary) {
                        updateStatusSummary.textContent = "Все компоненты актуальны ✔";
                    }
                }
            } else if (!silent) {
                if (updateStatusSummary) updateStatusSummary.textContent = "Не удалось проверить обновления";
            }
        } catch (e) {
            console.error("Check updates error:", e);
            if (!silent && updateStatusSummary) {
                updateStatusSummary.textContent = "Ошибка проверки обновлений";
            }
        } finally {
            if (!silent) {
                btnCheckUpdates.disabled = false;
                if (checkIcon) checkIcon.textContent = "🔍";
                if (checkBtnText) checkBtnText.textContent = "Проверить обновления";
            }
        }
    }

    // Render component list
    function renderComponentCards(comps) {
        if (!componentsList) return;
        componentsList.innerHTML = '';

        const iconMap = {
            'bksh2ray': '🚀',
            'sing-box': '📦',
            'xray': '⚡',
            'geo': '🌐'
        };

        comps.forEach(c => {
            const id = c.Id || c.id;
            const title = c.Title || c.title;
            const desc = c.Description || c.description || '';
            const source = c.Source || c.source || '';
            const curVer = c.CurrentVersion || c.current_version || 'Неизвестно';
            const latVer = c.LatestVersion || c.latest_version || curVer;
            const hasUpdate = (c.HasUpdate !== undefined) ? c.HasUpdate : !!c.has_update;
            const icon = iconMap[id] || '⚙️';

            const card = document.createElement('div');
            card.className = `component-card ${hasUpdate ? 'has-update-border' : ''}`;
            card.id = `comp-card-${id}`;

            let badgeHtml = '';
            let btnActionHtml = '';

            if (hasUpdate) {
                badgeHtml = `<span class="badge-version-status badge-update-ready">★ Есть обновление</span>`;
                btnActionHtml = `<button class="btn-comp-update" data-comp="${id}">Обновить</button>`;
            } else {
                badgeHtml = `<span class="badge-version-status badge-ok">✔ Актуально</span>`;
                btnActionHtml = `<button class="btn-comp-update btn-secondary" style="font-size:0.75rem; padding: 5px 10px;" data-comp="${id}">Переустановить</button>`;
            }

            card.innerHTML = `
                <div class="comp-main-row">
                    <div class="comp-header-left">
                        <div class="comp-icon-box">${icon}</div>
                        <div class="comp-meta">
                            <div class="comp-title-row">
                                <span class="comp-title">${title}</span>
                                <span class="comp-source">${source}</span>
                            </div>
                            <div class="comp-desc">${desc}</div>
                        </div>
                    </div>
                    <div class="comp-header-right">
                        ${badgeHtml}
                        <div id="btn-wrapper-${id}">${btnActionHtml}</div>
                    </div>
                </div>

                <div class="comp-versions-row">
                    <div class="ver-item">
                        <span class="ver-label">Текущая версия:</span>
                        <span class="ver-val" id="ver-cur-${id}">${curVer}</span>
                    </div>
                    <div class="ver-item">
                        <span class="ver-label">В репозитории GitHub:</span>
                        <span class="ver-val" id="ver-lat-${id}">${latVer}</span>
                    </div>
                </div>

                <div class="comp-progress-box hidden" id="progress-box-${id}">
                    <div class="comp-progress-bar-bg">
                        <div class="comp-progress-bar-fill" id="progress-fill-${id}"></div>
                    </div>
                    <div class="comp-progress-text" id="progress-text-${id}">Подготовка...</div>
                </div>
            `;

            // Action button listener
            const updateBtn = card.querySelector(`[data-comp="${id}"]`);
            updateBtn?.addEventListener('click', () => {
                triggerUpdateComponent(id);
            });

            componentsList.appendChild(card);
        });
    }

    // Update single component
    async function triggerUpdateComponent(compName) {
        if (isUpdating) return;
        if (!window.pywebview?.api?.update_component) return;

        isUpdating = true;
        setUpdateUiBusy(true);

        const progressBox = document.getElementById(`progress-box-${compName}`);
        const progressFill = document.getElementById(`progress-fill-${compName}`);
        const progressText = document.getElementById(`progress-text-${compName}`);

        if (progressBox) progressBox.classList.remove('hidden');
        if (progressFill) progressFill.style.width = '5%';
        if (progressText) progressText.textContent = 'Инициализация загрузки...';

        try {
            const res = await window.pywebview.api.update_component(compName);
            if (res && res.success) {
                if (progressFill) progressFill.style.width = '100%';
                if (progressText) progressText.textContent = 'Завершено успешно ✔';

                if (res.need_restart) {
                    btnRestartApp?.classList.remove('hidden');
                    showToast("bksh2ray обновлен! Нажмите «Перезапустить», чтобы применить обновление.");
                } else {
                    showToast(res.message || "Компонент успешно обновлен!");
                }
            } else {
                if (progressText) progressText.textContent = `Ошибка: ${res ? res.error : 'Неизвестный сбой'}`;
                showToast(`Ошибка обновления ${compName}: ${res ? res.error : 'Сбой'}`);
            }
        } catch (e) {
            console.error("Update component error:", e);
            if (progressText) progressText.textContent = `Исключение: ${e}`;
            showToast("Ошибка при загрузке обновления");
        } finally {
            isUpdating = false;
            setUpdateUiBusy(false);
            await checkUpdates(true);
        }
    }

    // Update all components
    btnUpdateAll?.addEventListener('click', async () => {
        if (isUpdating) return;
        const outdated = currentComponents.filter(c => (c.HasUpdate !== undefined) ? c.HasUpdate : !!c.has_update);
        const toUpdate = outdated.length > 0 ? outdated : currentComponents;

        if (toUpdate.length === 0) {
            showToast("Все компоненты уже актуальны!");
            return;
        }

        isUpdating = true;
        setUpdateUiBusy(true);

        let successCount = 0;
        let needsRestart = false;

        for (const comp of toUpdate) {
            const id = comp.Id || comp.id;
            const progressBox = document.getElementById(`progress-box-${id}`);
            const progressFill = document.getElementById(`progress-fill-${id}`);
            const progressText = document.getElementById(`progress-text-${id}`);

            if (progressBox) progressBox.classList.remove('hidden');
            if (progressFill) progressFill.style.width = '10%';
            if (progressText) progressText.textContent = 'Загрузка...';

            try {
                const res = await window.pywebview.api.update_component(id);
                if (res && res.success) {
                    successCount++;
                    if (progressFill) progressFill.style.width = '100%';
                    if (progressText) progressText.textContent = 'Обновлено ✔';
                    if (res.need_restart) needsRestart = true;
                } else {
                    if (progressText) progressText.textContent = `Ошибка: ${res ? res.error : 'Сбой'}`;
                }
            } catch (err) {
                if (progressText) progressText.textContent = `Ошибка: ${err}`;
            }
        }

        isUpdating = false;
        setUpdateUiBusy(false);

        if (needsRestart) {
            btnRestartApp?.classList.remove('hidden');
            showToast("Обновление завершено! Нажмите «Перезапустить bksh2ray».");
        } else {
            showToast(`Успешно обновлено компонентов: ${successCount} из ${toUpdate.length}`);
        }

        await checkUpdates(true);
    });

    function setUpdateUiBusy(busy) {
        if (btnCheckUpdates) btnCheckUpdates.disabled = busy;
        if (btnUpdateAll) btnUpdateAll.disabled = busy;
        const allCompBtns = document.querySelectorAll('.btn-comp-update');
        allCompBtns.forEach(b => b.disabled = busy);
    }

    // Real-time progress callback from C#
    window.onUpdateProgress = (comp, pct, text) => {
        const progressBox = document.getElementById(`progress-box-${comp}`);
        const progressFill = document.getElementById(`progress-fill-${comp}`);
        const progressText = document.getElementById(`progress-text-${comp}`);

        if (progressBox) progressBox.classList.remove('hidden');
        if (progressFill) progressFill.style.width = `${Math.min(100, Math.max(0, pct))}%`;
        if (progressText) progressText.textContent = text;
    };

    window.loadConfig = loadConfig;
    window.startApp = async () => {
        await startApp();
        // Silent update check in background on start
        setTimeout(() => checkUpdates(true), 1500);
    };
    
    init();
});

