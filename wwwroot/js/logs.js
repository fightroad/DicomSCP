class LogManager {
    constructor() {
        this.currentPage = 1;
        this.pageSize = 10;
        this.totalPages = 0;
        this.totalItems = 0;
        this.currentLogs = [];
        this.isLoading = false;
        this.currentType = '';
        this.types = null;
        this.initializeEvents();
    }

    initializeEvents() {
        try {
            // 绑定日志类型切换事件
            const typeList = document.getElementById('logTypes');
            if (typeList) {
                typeList.addEventListener('click', async (e) => {
                    const link = e.target.closest('.list-group-item');
                    if (link && link.dataset.type) {
                        e.preventDefault();
                        const type = link.dataset.type;
                        this.currentType = type;
                        // 更新选中状态
                        typeList.querySelectorAll('.list-group-item').forEach(item => {
                            item.classList.remove('active');
                        });
                        link.classList.add('active');
                        // 加载对应类型的日志
                        await this.loadLogFiles(type);
                    }
                });
            }

            // 绑定刷新按钮事件
            const refreshBtn = document.getElementById('logs-refresh');
            if (refreshBtn) {
                refreshBtn.addEventListener('click', async () => {
                    await this.loadLogTypes();
                });
            }
        } catch (error) {
            console.error('绑定日志事件失败:', error);
            window.showToast('绑定日志事件失败: ' + error.message, 'error');
        }
    }

    async loadLogTypes() {
        try {
            const response = await axios.get('/api/logs/types');
            this.types = response.data;
            
            // 渲染日志类型列表
            const fragment = document.createDocumentFragment();
            this.types.forEach(type => {
                const a = document.createElement('a');
                a.href = '#';
                a.className = 'list-group-item list-group-item-action';
                a.dataset.type = type;
                a.textContent = type;
                if (type === this.currentType) {
                    a.classList.add('active');
                }
                fragment.appendChild(a);
            });
            
            const container = document.getElementById('logTypes');
            if (container) {
                container.innerHTML = '';
                container.appendChild(fragment);
            }

            // 如果有类型且没有当前选中的类型，选中第一个并加载其日志
            if (this.types && this.types.length > 0 && !this.currentType) {
                this.currentType = this.types[0];
                const firstType = container?.querySelector('.list-group-item');
                if (firstType) {
                    firstType.classList.add('active');
                }
                await this.loadLogFiles(this.currentType);
            }
        } catch (error) {
            console.error('加载日志类型失败:', error);
            window.showToast('加载日志类型失败', 'error');
        }
    }

    async loadLogFiles(type) {
        if (this.isLoading) return;

        const tbody = document.getElementById('logFiles');
        showTableLoading(tbody, 4);

        try {
            this.isLoading = true;
            this.currentType = type;
            const response = await axios.get(`/api/logs/files/${type}`);
            this.allFiles = response.data;
            this.renderLogFiles();
            this.updateActiveType();
            this.updatePagination();
        } catch (error) {
            console.error('加载日志文件失败:', error);
            window.showToast('加载日志文件失败', 'error');
        } finally {
            this.isLoading = false;
        }
    }

    renderLogTypes(types) {
        const fragment = document.createDocumentFragment();
        types.forEach(type => {
            const a = document.createElement('a');
            a.href = '#';
            a.className = 'list-group-item list-group-item-action';
            a.dataset.type = type;
            a.textContent = type;
            if (type === this.currentType) {
                a.classList.add('active');
            }
            fragment.appendChild(a);
        });
        
        const container = document.getElementById('logTypes');
        if (container) {
            container.innerHTML = '';
            container.appendChild(fragment);
        }
    }

    renderLogFiles() {
        const start = (this.currentPage - 1) * this.pageSize;
        const end = start + this.pageSize;
        const pageFiles = this.allFiles.slice(start, end);
        
        const now = new Date();
        now.setHours(0, 0, 0, 0);
        
        const fragment = document.createDocumentFragment();
        pageFiles.forEach(file => {
            const fileDate = new Date(file.lastModified);
            fileDate.setHours(0, 0, 0, 0);
            const isToday = fileDate.getTime() === now.getTime();
            
            const tr = document.createElement('tr');
            tr.innerHTML = `
                <td>
                    <a href="#" onclick="logManager.viewLogContent('${file.name}'); return false;">
                        ${file.name}
                    </a>
                </td>
                <td>${this.formatFileSize(file.size)}</td>
                <td>${new Date(file.lastModified).toLocaleString()}</td>
                <td>
                    ${!isToday ? `
                        <button class="btn btn-danger btn-sm" onclick="logManager.deleteLogFile('${file.name}')">
                            <i class="bi bi-trash"></i> 删除
                        </button>
                    ` : ''}
                </td>
            `;
            fragment.appendChild(tr);
        });
        
        const container = document.getElementById('logFiles');
        container.innerHTML = '';
        container.appendChild(fragment);
    }

    updateActiveType() {
        document.querySelectorAll('#logTypes .list-group-item').forEach(item => {
            item.classList.remove('active');
            if (item.dataset.type === this.currentType) {
                item.classList.add('active');
            }
        });
    }

    formatFileSize(bytes) {
        const units = ['B', 'KB', 'MB', 'GB'];
        let size = bytes;
        let unitIndex = 0;
        while (size >= 1024 && unitIndex < units.length - 1) {
            size /= 1024;
            unitIndex++;
        }
        return `${size.toFixed(2)} ${units[unitIndex]}`;
    }

    async deleteLogFile(filename) {
        if (!await showConfirmDialog('确认删除', `确定要删除日志文件 ${filename} 吗？`)) {
            return;
        }

        try {
            await axios.delete(`/api/logs/${this.currentType}/${encodeURIComponent(filename)}`);
            
            await this.loadLogFiles(this.currentType);
            window.showToast('日志文件已删除', 'success');
        } catch (error) {
            console.error('删除日志失败:', error);
            window.showToast('删除日志失败: ' + (error.response?.data || error.message), 'error');
        }
    }

    async viewLogContent(filename) {
        try {
            const modalDiv = document.createElement('div');
            modalDiv.className = 'modal';
            modalDiv.setAttribute('tabindex', '-1');
            modalDiv.setAttribute('data-dynamic', 'true');
            modalDiv.innerHTML = `
                <div class="modal-dialog modal-lg" style="max-width: 1000px;">
                    <div class="modal-content">
                        <div class="modal-header" style="background: white; position: sticky; top: 0; z-index: 1020;">
                            <div class="d-flex align-items-center">
                                <select class="form-select form-select-sm me-2" id="logFileSelect" style="width: auto;">
                                    <!-- 日志文件列表将动态填充 -->
                                </select>
                            </div>
                            <div>
                                <button type="button" class="btn btn-outline-primary btn-sm me-1" onclick="logManager.refreshLogContent()">
                                    刷新
                                </button>
                                <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
                            </div>
                        </div>
                        <div class="modal-body p-2" style="height: calc(100vh - 280px);">
                            <div style="height: 100%; border: 1px solid #dee2e6; border-radius: 4px; overflow: hidden;">
                                <pre class="log-content" style="height: 100%; margin: 0; padding: 1rem; color: #d4d4d4; 
                                    font-family: Consolas, monospace; font-size: 0.9rem; line-height: 1.5;
                                    background-color: #1e1e1e; overflow-y: auto; overflow-x: hidden;
                                    white-space: pre-wrap;"></pre>
                            </div>
                        </div>
                    </div>
                </div>
            `;
            document.body.appendChild(modalDiv);

            const modal = new bootstrap.Modal(modalDiv);
            modal.show();

            // 显示加载动画
            const preElement = modalDiv.querySelector('.log-content');
            if (preElement) {
                preElement.innerHTML = `
                    <div style="height: 100%; display: flex; align-items: center; justify-content: center;">
                        <div class="spinner-border text-secondary" role="status"></div>
                    </div>
                `;
            }

            // 更新文件选择列表
            await this.updateLogFileSelect(filename);

            // 加载日志内容
            await this.loadLogContent(filename);

            // 监听关闭事件
            const handleHidden = () => {
                modalDiv.removeEventListener('hidden.bs.modal', handleHidden);
                const instance = bootstrap.Modal.getInstance(modalDiv);
                if (instance) {
                    instance.dispose();
                }
                if (document.body.contains(modalDiv)) {
                    modalDiv.remove();
                }
            };

            modalDiv.addEventListener('hidden.bs.modal', handleHidden);

        } catch (error) {
            console.error('获取日志内容失败:', error);
            window.showToast('获取日志内容失败: ' + error.message, 'error');
        }
    }

    async updateLogFileSelect(currentFile) {
        try {
            const response = await axios.get(`/api/logs/files/${this.currentType}`);
            const files = response.data;
            
            const select = document.getElementById('logFileSelect');
            if (!select) return;

            select.innerHTML = files.map(file => `
                <option value="${file.name}" ${file.name === currentFile ? 'selected' : ''}>
                    ${file.name}
                </option>
            `).join('');

            // 绑定文件选择事件监听
            select.onchange = (e) => {
                if (e.target.value) {
                    this.loadLogContent(e.target.value);
                }
            };

        } catch (error) {
            console.error('更新日志文件列表失败:', error);
            window.showToast('更新日志文件列表失败: ' + error.message, 'error');
        }
    }

    async refreshLogContent() {
        const select = document.getElementById('logFileSelect');
        const refreshBtn = select?.closest('.modal-content')?.querySelector('.btn-outline-primary');
        if (!select?.value || !refreshBtn) return;

        // 防止重复点击
        if (refreshBtn.disabled) return;

        try {
            // 设置按钮为加载状态
            refreshBtn.disabled = true;
            refreshBtn.innerHTML = '<span class="spinner-border spinner-border-sm"></span>';

            // 先清空内容显示加载动画
            const preElement = document.querySelector('.log-content');
            if (preElement) {
                preElement.innerHTML = `
                    <div style="height: 100%; display: flex; align-items: center; justify-content: center;">
                        <div class="spinner-border text-secondary" role="status"></div>
                    </div>
                `;
            }

            // 强制刷新文件列表
            await this.updateLogFileSelect(select.value);

            // 加载内容
            await this.loadLogContent(select.value);
        } catch (error) {
            console.error('刷新日志内容失败:', error);
            window.showToast('刷新日志内容失败: ' + error.message, 'error');
        } finally {
            // 恢复按钮状态
            refreshBtn.disabled = false;
            refreshBtn.innerHTML = '刷新';
        }
    }

    async loadLogContent(filename) {
        if (!filename) return;
        
        try {
            const preElement = document.querySelector('.log-content');
            if (!preElement) return;

            const response = await axios.get(`/api/logs/${this.currentType}/${encodeURIComponent(filename)}/content`);
            const data = response.data;
            if (!data.content || data.content.length === 0) {
                preElement.innerHTML = '暂无日志内容';
                return;
            }

            // 确保内容是最新的
            const content = data.content.reverse();
            preElement.innerHTML = content.join('\n');

            // 滚动到顶部，因为日志是倒序显示的
            preElement.scrollTop = 0;
        } catch (error) {
            console.error('加载日志内容失败:', error);
            window.showToast('加载日志内容失败: ' + (error.response?.data || error.message), 'error');
        }
    }

    updatePagination() {
        try {
            const totalPages = Math.ceil(this.allFiles.length / this.pageSize);
            const container = document.getElementById('logFiles-pagination');
            if (!container) return;

            const start = (this.currentPage - 1) * this.pageSize + 1;
            const end = Math.min(this.currentPage * this.pageSize, this.allFiles.length);

            container.innerHTML = `
                <div class="d-flex justify-content-between align-items-center mt-3">
                    <div class="pagination-info">
                        显示 <span id="logs-currentRange">${this.allFiles.length > 0 ? `${start}-${end}` : '0-0'}</span> 条，
                        共 <span id="logs-totalCount">${this.allFiles.length}</span> 条
                    </div>
                    <nav aria-label="分页导航">
                        <ul class="pagination mb-0">
                            <li class="page-item ${this.currentPage <= 1 ? 'disabled' : ''}">
                                <button class="page-link" onclick="logManager.changePage(${this.currentPage - 1})" aria-label="上一页">
                                    <span aria-hidden="true">&laquo;</span>
                                </button>
                            </li>
                            <li class="page-item">
                                <span class="page-link">${this.currentPage}</span>
                            </li>
                            <li class="page-item ${this.currentPage >= totalPages ? 'disabled' : ''}">
                                <button class="page-link" onclick="logManager.changePage(${this.currentPage + 1})" aria-label="下一页">
                                    <span aria-hidden="true">&raquo;</span>
                                </button>
                            </li>
                        </ul>
                    </nav>
                </div>
            `;
        } catch (error) {
            handleError(error, '更新分页信息失败');
        }
    }

    changePage(page) {
        const totalPages = Math.ceil(this.allFiles.length / this.pageSize);
        if (page >= 1 && page <= totalPages) {
            this.currentPage = page;
            this.renderLogFiles();
            this.updatePagination();
        }
    }
}

// 导出单个实例
window.logManager = new LogManager(); 