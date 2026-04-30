class ServiceManager {
    constructor() {
        // 初始化状态更新
        this.updateStatus();
        // 每3秒更新一次状态
        setInterval(() => this.updateStatus(), 3000);
        // 绑定事件
        this.bindEvents();
    }

    bindEvents() {
        // 系统状态和服务状态点击事件
        const systemStatusGroup = document.querySelector('.system-status-group');
        const serviceStatusGroup = document.querySelector('.service-status-group');
        const statusTip = document.querySelector('.status-tip');

        // 鼠标移入显示提示
        [systemStatusGroup, serviceStatusGroup].forEach(element => {
            if (!element) return;
            
            element.addEventListener('mouseenter', (e) => {
                const rect = e.target.getBoundingClientRect();
                statusTip.style.top = `${rect.bottom + 5}px`;
                statusTip.style.left = `${rect.left}px`;
                statusTip.style.display = 'block';
            });

            element.addEventListener('mouseleave', () => {
                statusTip.style.display = 'none';
            });
        });

        // 点击事件，使用箭头函数保持 this 绑定
        systemStatusGroup?.addEventListener('click', () => this.showSystemAndServiceStatus());
        serviceStatusGroup?.addEventListener('click', () => this.showSystemAndServiceStatus());
    }

    async updateStatus() {
        try {
            const response = await axios.get('/api/dicom/status');
            const status = response.data;
            
            // 更新服务状态
            this.updateServices(status);
            
            // 更新系统状态
            if (status.system) {
                this.updateSystemStatus(status.system);
            }
        } catch (error) {
            console.error('获取服务状态失败:', error);
            this.setAllServicesUnknown();
        }
    }

    // 更新服务状态
    updateServices(status) {
        const services = ['store', 'worklist', 'qr', 'print'];
        services.forEach(service => {
            if (status[service]) {
                this.updateServiceStatus(`${service}Scp`, status[service]);
            }
        });
    }

    // 更新系统状态
    updateSystemStatus(systemStatus) {
        this.updateSystemInfo(systemStatus);
        this.updateUptime(systemStatus);
    }

    // 更新服务状态
    updateServiceStatus(serviceId, status) {
        try {
            this.updateServiceBadge(serviceId, status.isRunning);
            this.updateServiceInfo(serviceId, status);
        } catch (error) {
            console.error(`更新服务状态失败: ${serviceId}`, error);
        }
    }

    // 设置所有服务为未知状态
    setAllServicesUnknown() {
        const services = ['store', 'worklist', 'qr', 'print'];
        services.forEach(service => {
            this.updateServiceStatus(`${service}Scp`, {
                isRunning: false,
                aeTitle: '-',
                port: '-'
            });
        });
    }

    // 更新系统信息
    updateSystemInfo(systemStatus) {
        try {
            // 更新CPU信息
            const cpuUsage = systemStatus.cpuUsage;
            const cpuStatus = document.getElementById('cpuStatus');
            const modalCpuModel = document.getElementById('modalCpuModel');
            const modalCpuUsage = document.getElementById('modalCpuUsage');

            if (cpuStatus) {
                cpuStatus.textContent = `${cpuUsage.toFixed(1)}%`;
                cpuStatus.parentElement.className = `status-item mb-1 ${this.getCpuStatusClass(cpuUsage)}`;
            }
            if (modalCpuModel) {
                modalCpuModel.textContent = systemStatus.cpuModel || 'Unknown';
            }
            if (modalCpuUsage) {
                modalCpuUsage.textContent = `${cpuUsage.toFixed(1)}%`;
            }

            // 更新内存信息
            const memoryUsage = systemStatus.processMemory;
            const memoryStatus = document.getElementById('memoryStatus');
            const modalSystemMemory = document.getElementById('modalSystemMemory');
            const modalProcessMemory = document.getElementById('modalProcessMemory');

            if (memoryStatus) {
                memoryStatus.textContent = `${memoryUsage.toFixed(0)}MB`;
                memoryStatus.parentElement.className = `status-item ${this.getMemoryStatusClass(memoryUsage)}`;
            }
            if (modalSystemMemory) {
                modalSystemMemory.textContent = 
                    `${systemStatus.systemMemoryUsed.toFixed(0)}MB / ${systemStatus.systemMemoryTotal.toFixed(0)}MB (${systemStatus.systemMemoryPercent.toFixed(1)}%)`;
            }
            if (modalProcessMemory) {
                modalProcessMemory.textContent = `${memoryUsage.toFixed(0)}MB`;
            }

            // 更新操作系统信息
            const modalOsVersion = document.getElementById('modalOsVersion');
            if (modalOsVersion) {
                modalOsVersion.textContent = systemStatus.platform || 'Unknown';
            }
        } catch (error) {
            console.error('更新系统信息失败:', error);
        }
    }

    // 更新运行时间
    updateUptime(systemStatus) {
        try {
            const uptime = systemStatus.processStartTime;
            let uptimeText = '';
            if (uptime.days > 0) {
                uptimeText += `${uptime.days}天`;
            }
            if (uptime.hours > 0 || uptime.days > 0) {
                uptimeText += `${uptime.hours}小时`;
            }
            uptimeText += `${uptime.minutes}分钟`;

            const modalUptime = document.getElementById('modalUptime');
            if (modalUptime) {
                modalUptime.textContent = uptimeText;
            }
        } catch (error) {
            console.error('更新运行时间失败:', error);
        }
    }

    // 获取CPU状态样式类
    getCpuStatusClass(cpuUsage) {
        return cpuUsage > 80 ? 'cpu-danger' :
               cpuUsage > 50 ? 'cpu-warning' : 'cpu-normal';
    }

    // 获取内存状态样式类
    getMemoryStatusClass(memoryUsage) {
        return memoryUsage > 1024 ? 'memory-danger' :
               memoryUsage > 512 ? 'memory-warning' : 'memory-normal';
    }

    // 显示系统和服务状态对话框
    showSystemAndServiceStatus() {
        return showDialog({
            title: '系统和服务状态',
            content: `
                <div style="height: calc(100vh - 400px); display: flex; flex-direction: column;">
                    <div class="position-sticky top-0 bg-white" style="z-index: 1;">
                        <h6 class="border-bottom pb-2 mb-3">系统信息</h6>
                    </div>
                    <div style="flex: 1; overflow-y: auto;">
                        <div class="list-group mb-4">
                            <div class="list-group-item">
                                <div class="d-flex justify-content-between">
                                    <strong>CPU型号</strong>
                                    <span id="modalCpuModel">Unknown</span>
                                </div>
                            </div>
                            <div class="list-group-item">
                                <div class="d-flex justify-content-between">
                                    <strong>CPU使用率</strong>
                                    <span id="modalCpuUsage">0%</span>
                                </div>
                            </div>
                            <div class="list-group-item">
                                <div class="d-flex justify-content-between">
                                    <strong>系统内存</strong>
                                    <span id="modalSystemMemory">0MB / 0MB (0%)</span>
                                </div>
                            </div>
                            <div class="list-group-item">
                                <div class="d-flex justify-content-between">
                                    <strong>程序内存</strong>
                                    <span id="modalProcessMemory">0MB</span>
                                </div>
                            </div>
                            <div class="list-group-item">
                                <div class="d-flex justify-content-between">
                                    <strong>运行时间</strong>
                                    <span id="modalUptime">0天0小时0分钟</span>
                                </div>
                            </div>
                            <div class="list-group-item">
                                <div class="d-flex justify-content-between">
                                    <strong>操作系统</strong>
                                    <span id="modalOsVersion">Unknown</span>
                                </div>
                            </div>
                        </div>

                        <div class="position-sticky bg-white" style="top: 0; z-index: 1;">
                            <h6 class="border-bottom pb-2 mb-3">DICOM 服务状态</h6>
                        </div>
                        <div class="list-group">
                            <div class="list-group-item">
                                <div class="d-flex justify-content-between align-items-center">
                                    <strong>存储服务 (StoreSCP)</strong>
                                    <span id="modalStoreScp" class="badge bg-secondary">加载中...</span>
                                </div>
                                <div class="small text-muted mt-1" id="modalStoreScpInfo">AET: - 端口: -</div>
                            </div>
                            <div class="list-group-item">
                                <div class="d-flex justify-content-between align-items-center">
                                    <strong>检查列表 (WorklistSCP)</strong>
                                    <span id="modalWorklistScp" class="badge bg-secondary">加载中...</span>
                                </div>
                                <div class="small text-muted mt-1" id="modalWorklistScpInfo">AET: - 端口: -</div>
                            </div>
                            <div class="list-group-item">
                                <div class="d-flex justify-content-between align-items-center">
                                    <strong>查询服务 (QRSCP)</strong>
                                    <span id="modalQrScp" class="badge bg-secondary">加载中...</span>
                                </div>
                                <div class="small text-muted mt-1" id="modalQrScpInfo">AET: - 端口: -</div>
                            </div>
                            <div class="list-group-item">
                                <div class="d-flex justify-content-between align-items-center">
                                    <strong>打印服务 (PrintSCP)</strong>
                                    <span id="modalPrintScp" class="badge bg-secondary">加载中...</span>
                                </div>
                                <div class="small text-muted mt-1" id="modalPrintScpInfo">AET: - 端口: -</div>
                            </div>
                        </div>
                    </div>
                </div>
            `,
            showFooter: false,
            size: 'lg',
            onShow: () => this.updateStatus()
        });
    }

    // 更新服务状态徽章
    updateServiceBadge(serviceId, isRunning) {
        const badge = document.getElementById(`${serviceId}-status`);
        const modalBadge = document.getElementById(`modal${serviceId.charAt(0).toUpperCase() + serviceId.slice(1)}`);
        
        const className = `badge ${isRunning ? 'bg-success' : 'bg-danger'}`;
        const text = isRunning ? '运行中' : '已停止';
        
        if (badge) {
            badge.className = className;
            badge.textContent = text;
        }
        
        if (modalBadge) {
            modalBadge.className = className;
            modalBadge.textContent = text;
        }
    }

    // 更新服务详细信息
    updateServiceInfo(serviceId, info) {
        const infoText = `AET: ${info.aeTitle} 端口: ${info.port}`;
        
        const infoElement = document.getElementById(`${serviceId}Info`);
        const modalInfoElement = document.getElementById(`modal${serviceId.charAt(0).toUpperCase() + serviceId.slice(1)}Info`);
        
        if (infoElement) {
            infoElement.textContent = infoText;
        }
        
        if (modalInfoElement) {
            modalInfoElement.textContent = infoText;
        }
    }
}
