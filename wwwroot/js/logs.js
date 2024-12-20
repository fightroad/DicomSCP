class LogManager {
    constructor() {
        this.currentType = '';
        this.pageSize = 10;
        this.currentPage = 1;
        this.allFiles = [];
        this.isLoading = false;
    }

    async init() {
        try {
            await this.loadLogTypes();
            this.bindEvents();
        } catch (error) {
            console.error('初始化日志管理器失败:', error);
            window.showToast('初始化失败', 'error');
        }
    }

    async loadLogTypes() {
        try {
            const response = await axios.get('/api/logs/types');
            const types = response.data;
            this.renderLogTypes(types);
            if (types.length > 0) {
                await this.loadLogFiles(types[0]);
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
            fragment.appendChild(a);
        });
        
        const container = document.getElementById('logTypes');
        container.innerHTML = '';
        container.appendChild(fragment);
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

    bindEvents() {
        document.getElementById('logTypes').addEventListener('click', e => {
            e.preventDefault();
            const type = e.target.dataset.type;
            if (type) {
                this.loadLogFiles(type);
            }
        });

        document.body.addEventListener('change', e => {
            if (e.target.id === 'logFileSelect') {
                this.loadLogContent(e.target.value);
            }
        });
    }

    async deleteLogFile(filename) {
        if (!await showConfirmDialog('确认删除', `确定要删除日志文件 ${filename} 吗？`)) {
            return;
        }

        try {
            const response = await fetch(`/api/logs/${this.currentType}/${filename}`, {
                method: 'DELETE'
            });
            
            if (!response.ok) {
                throw new Error(await response.text());
            }
            
            await this.loadLogFiles(this.currentType);
            window.showToast('日志文件已删除', 'success');
        } catch (error) {
            window.showToast('删除日志失败', 'error');
        }
    }

    async viewLogContent(filename) {
        try {
            const response = await fetch(`/api/logs/${this.currentType}/${filename}/content`);
            if (!response.ok) {
                throw new Error('获取日志内容失败');
            }
            
            const data = await response.json();
            
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
                                <button type="button" class="btn btn-outline-warning btn-sm me-1" onclick="logManager.clearLogContent()">
                                    清空
                                </button>
                                <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
                            </div>
                        </div>
                        <div class="modal-body p-2" style="height: calc(100vh - 280px);">
                            <div style="height: 100%; border: 1px solid #dee2e6; border-radius: 4px; overflow: hidden;">
                                <pre class="log-content" style="height: 100%; margin: 0; padding: 1rem; color: #d4d4d4; 
                                    font-family: Consolas, monospace; font-size: 0.9rem; line-height: 1.5;
                                    background-color: #1e1e1e; overflow-y: auto; overflow-x: hidden;
                                    white-space: pre-wrap;">${data.content.reverse().join('\n') || '暂无日志内容'}</pre>
                            </div>
                        </div>
                    </div>
                </div>
            `;
            document.body.appendChild(modalDiv);

            const modal = new bootstrap.Modal(modalDiv);

            // 更新文件选择列表
            await this.updateLogFileSelect(filename);

            // 监听关闭事件
            const handleHidden = () => {
                // 移除事件监听
                modalDiv.removeEventListener('hidden.bs.modal', handleHidden);
                
                // 确保模态框实例还存在
                const instance = bootstrap.Modal.getInstance(modalDiv);
                if (instance) {
                    instance.dispose();
                }
                
                // 从 DOM 中移除
                if (document.body.contains(modalDiv)) {
                    modalDiv.remove();
                }
            };

            modalDiv.addEventListener('hidden.bs.modal', handleHidden);

            modal.show();

        } catch (error) {
            console.error('获取日志内容失败:', error);
            window.showToast('获取日志内容失败', 'error');
        }
    }

    async updateLogFileSelect(currentFile) {
        try {
            const response = await fetch(`/api/logs/files/${this.currentType}`);
            if (!response.ok) {
                throw new Error('获取日志文件列表失败');
            }
            const files = await response.json();
            
            const select = document.getElementById('logFileSelect');
            select.innerHTML = files.map(file => `
                <option value="${file.name}" ${file.name === currentFile ? 'selected' : ''}>
                    ${file.name}
                </option>
            `).join('');
        } catch (error) {
            console.error('更新日志文件列表失败:', error);
            window.showToast('更新日志文件列表失败', 'error');
        }
    }

    async refreshLogContent() {
        const select = document.getElementById('logFileSelect');
        if (select) {
            await this.loadLogContent(select.value);
        }
    }

    async loadLogContent(filename) {
        try {
            const preElement = document.querySelector('.log-content');
            if (preElement) {
                // 显示简单的加载动画
                preElement.innerHTML = `
                    <div style="height: 100%; display: flex; align-items: center; justify-content: center;">
                        <div class="spinner-border text-secondary" role="status"></div>
                    </div>
                `;
            }

            const response = await fetch(`/api/logs/${this.currentType}/${filename}/content`);
            if (!response.ok) {
                throw new Error('获取日志内容失败');
            }
            
            const data = await response.json();
            
            if (preElement) {
                preElement.innerHTML = data.content.reverse().join('\n') || '暂无日志内容';
            }
        } catch (error) {
            console.error('加载日志内容失败:', error);
            window.showToast('加载日志内容失败', 'error');
        }
    }

    async clearLogContent() {
        const select = document.getElementById('logFileSelect');
        if (!select) return;

        try {
            if (!await showConfirmDialog('确认清空', `确定要清空日志文件 ${select.value} 吗？`)) {
                return;
            }

            const response = await fetch(`/api/logs/${this.currentType}/${select.value}/clear`, {
                method: 'POST'
            });
            
            if (response.status === 409) {
                window.showToast('文件正在被使用，请稍后重试', 'warning');
                return;
            }
            
            if (!response.ok) {
                const errorText = await response.text();
                throw new Error(errorText);
            }
            
            await this.refreshLogContent();
            await this.loadLogFiles(this.currentType);
            window.showToast('日志文件已清空', 'success');
        } catch (error) {
            console.error('清空日志失败:', error);
            window.showToast(error.message || '清空日志失败', 'error');
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

document.addEventListener('DOMContentLoaded', () => {
    // 移除这里的初始化，由 main.js 控制
    // window.logManager = new LogManager();
}); 