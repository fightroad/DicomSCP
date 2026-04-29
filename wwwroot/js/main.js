// ================ 全局变量 ================

// 定义默认路由
const defaultRoute = 'images';  // 修改默认路由为影像管理页面

// ================ 通用工具函数 ================
// 统一错误处理
function handleError(error, message) {
    console.error(message, error);
    window.showToast(error.response?.data || error.message, 'error');
}

// 统一成功提示
function showSuccessMessage(message) {
    window.showToast(message, 'success');
}

// 显示加载状态
function showLoading(element) {
    if (!element) return;
    element.innerHTML = `
        <div class="d-flex justify-content-center align-items-center p-3">
            <div class="spinner-border text-primary" role="status">
                <span class="visually-hidden">加载中...</span>
            </div>
        </div>
    `;
}

// 统一的 Toast 显示函数
window.showToast = function(message, type = 'success') {
    try {
        const toastEl = document.getElementById('storeToast');
        if (!toastEl) {
            console.error('找不到 Toast 元素');
            return;
        }

        const titleEl = toastEl.querySelector('.toast-header strong');
        const messageEl = toastEl.querySelector('.toast-body');
        if (!titleEl || !messageEl) {
            console.error('找不到 Toast 内部元素');
            return;
        }

        // 设置标题和消息
        switch (type) {
            case 'success':
                titleEl.textContent = '操作成功';
                toastEl.classList.remove('bg-danger', 'bg-secondary');
                toastEl.classList.add('bg-success', 'text-white');
                break;
            case 'error':
                titleEl.textContent = '操作失败';
                toastEl.classList.remove('bg-success', 'bg-secondary');
                toastEl.classList.add('bg-danger', 'text-white');
                break;
            default:
                titleEl.textContent = '提示';
                toastEl.classList.remove('bg-success', 'bg-danger');
                toastEl.classList.add('bg-secondary', 'text-white');
        }

        messageEl.textContent = message;

        // 显示 Toast
        const toast = new bootstrap.Toast(toastEl, {
            animation: true,
            autohide: true,
            delay: 3000
        });
        toast.show();

    } catch (error) {
        // 避免递归调用
        console.error('显示提示失败:', error);
    }
};

// ================ 初始化函数 ================
// 全局 axios 拦截器
initAxiosInterceptors();  // 确保这个在最开始就执行

function initAxiosInterceptors() {
    axios.interceptors.response.use(
        response => response,
        error => {
            console.log('Axios Error:', error.response?.status);
            if (error.response?.status === 401) {
                console.log('Caught 401 error, redirecting to login...');
                window.showToast('会话已过期，请重新登录', 'error');
                window.location.href = '/login.html';
                return Promise.reject(error);
            }
            return Promise.reject(error);
        }
    );
}

// 页面加载完成后初始化
document.addEventListener('DOMContentLoaded', function() {
    // 初始化全局功能
    initGlobalFeatures();
    // 初始化路由
    initRouting();
});

// 初始化全局功能
function initGlobalFeatures() {
    // 初始化服务管理器
    if (!window.serviceManager) {
        window.serviceManager = new ServiceManager();
    }
    // 初始化认证管理器
    if (!window.authManager) {
        window.authManager = new AuthManager();
    }
    // 初始化模态框管理器
    ModalManager.init();
    // 获取当前登录用户名
    window.authManager.getCurrentUsername();
    // 添加版权信息
    addCopyright();
    // 隐藏加载遮罩
    const loadingMask = document.getElementById('loading-mask');
    if (loadingMask) {
        setTimeout(() => {
            loadingMask.classList.add('hide');
        }, 500);
    }
}

// 初始化路由
function initRouting() {
    // 绑定导航事件
    document.querySelectorAll('.nav-link[data-page]').forEach(link => {
        link.addEventListener('click', function(e) {
            e.preventDefault();
            const pageId = this.getAttribute('data-page');
            if (pageId !== location.hash.slice(1)) {
                history.pushState(null, '', `#${pageId}`);
                handleRoute();
            }
        });
    });

    // 处理浏览器前进后退
    window.addEventListener('popstate', handleRoute);

    // 初始化第一个页面
    handleRoute();
}

// 添加版权信息
function addCopyright() {
    const footer = document.createElement('footer');
    footer.className = 'footer mt-auto py-3';
    footer.innerHTML = `
        <div class="container text-center">
            <span class="text-muted">
                © ${new Date().getFullYear()} DICOM管理系统 by 
                <a href="https://gitee.com/fightroad/DicomSCP" target="_blank">
                    平凡之路 <i class="bi bi-github"></i>
                </a>
            </span>
        </div>
    `;
    document.body.appendChild(footer);

    // 添加样式以确保页脚始终在底部
    const style = document.createElement('style');
    style.textContent = `
        body {
            min-height: 100vh;
            display: flex;
            flex-direction: column;
        }
        .footer {
            margin-top: auto;
            background-color: #f8f9fa;
        }
        .footer a {
            color: inherit;
            text-decoration: none;
        }
        .footer a:hover {
            color: #0d6efd;
        }
    `;
    document.head.appendChild(style);
}

// ================ 页面切换 ================
// 定义页面配置
const PAGE_CONFIG = {
    worklist: { 
        id: 'worklist-page', 
        init: () => {
            if (!window.moduleInitialized?.worklist) {
                bindWorklistEvents();
                window.moduleInitialized = window.moduleInitialized || {};
                window.moduleInitialized.worklist = true;
            }
            loadWorklistData();
        }
    },
    images: { 
        id: 'images-page', 
        init: () => {
            if (!window.moduleInitialized?.images) {
                bindImagesEvents();
                window.moduleInitialized = window.moduleInitialized || {};
                window.moduleInitialized.images = true;
            }
            loadImages();
        }
    },
    qr: { 
        id: 'qr-page', 
        init: () => {
            if (!window.moduleInitialized?.qr) {
                bindQRPaginationEvents();
                initQRSeriesModal();
                window.moduleInitialized = window.moduleInitialized || {};
                window.moduleInitialized.qr = true;
            }
            loadQRNodes();
        }
    },
    store: { 
        id: 'store-page', 
        init: () => {
            if (!window.moduleInitialized?.store) {
                initDropZone();
                initFileInputs();
                window.moduleInitialized = window.moduleInitialized || {};
                window.moduleInitialized.store = true;
            }
            loadStoreNodes();
        }
    },
    logs: { 
        id: 'logs-page', 
        init: async () => {
            if (!window.logManager) {
                window.logManager = new LogManager();
            }
            await window.logManager.loadLogTypes();
        }
    },
    print: { 
        id: 'print-page', 
        init: async () => {
            if (!window.printManager) {
                window.printManager = new PrintManager();
                await window.printManager.init();
            } else {
                // 每次切换到打印页面时都重新加载数据
                await window.printManager.loadPrintJobs();
            }
        }
    },
    settings: { 
        id: 'settings-page', 
        init: () => {
            if (!window.configManager) {
                window.configManager = new ConfigManager();
            } else {
                window.configManager.loadConfig();
            }
        }
    }
};

// 切换页面函数
function switchPage(page) {
    try {
        // 检查页面是否存在
        if (!PAGE_CONFIG[page]) {
            console.warn(`未知的页面: ${page}`);
            page = defaultRoute;
        }

        // 隐藏所有页面
        Object.values(PAGE_CONFIG).forEach(config => {
            const pageEl = document.getElementById(config.id);
            if (pageEl) {
                pageEl.style.display = 'none';
                pageEl.classList.remove('show');
            }
        });
        
        // 移除所有导航链接的active类
        document.querySelectorAll('.nav-link').forEach(link => link.classList.remove('active'));
        
        // 显示选中的页面
        const targetPage = document.getElementById(PAGE_CONFIG[page].id);
        if (targetPage) {
            targetPage.style.display = 'block';
            // 使用 setTimeout 确保 display:block 生效后再添加 show 类
            setTimeout(() => targetPage.classList.add('show'), 50);
        }
        
        // 添加active类到当前导航链接
        const activeLink = document.querySelector(`.nav-link[data-page="${page}"]`);
        if (activeLink) {
            activeLink.classList.add('active');
        }

        // 保存当前页面到 localStorage
        localStorage.setItem('lastActivePage', page);

        // 初始化并加载页面数据
        try {
            PAGE_CONFIG[page].init();
        } catch (error) {
            console.error(`初始化页面 ${page} 失败:`, error);
            window.showToast(`初始化页面失败: ${error.message}`, 'error');
        }
        
    } catch (error) {
        console.error('切换页面失败:', error);
        window.showToast('页面切换失败', 'error');
    }
}

// 路由处理
function handleRoute() {
    const path = window.location.hash.slice(1) || defaultRoute;
    switchPage(path);
}

// ================ 用户相关函数 ================
// 登出函数已移至 AuthManager 类中，通过 window.authManager.logout() 调用

// ================ 对话框相关函数 ================
// 显示节点选择对话框
function showNodeSelectionDialog(nodeOptions) {
    return showDialog({
        title: '选择目标节点',
        content: `
            <select class="form-select" id="nodeSelect">
                ${nodeOptions.map((node, index) => 
                    `<option value="${index}">${node}</option>`
                ).join('')}
            </select>
        `,
        onConfirm: async () => {
            const select = document.getElementById('nodeSelect');
            return parseInt(select.value);
        }
    });
}

// 显示确认对话框
function showConfirmDialog(title, message) {
    return showDialog({
        title: title,
        content: `<p>${message}</p>`,
        onConfirm: async () => true
    });
}

// 全局模态框管理器
const ModalManager = {
    init() {
        try {
            // 监听所有模态框隐藏（原生事件委托）
            document.addEventListener('hidden.bs.modal', (event) => {
                const modalEl = event.target;
                if (!(modalEl instanceof HTMLElement) || !modalEl.classList.contains('modal')) {
                    return;
                }

                // 移除焦点
                modalEl.querySelectorAll('button, [role="button"], a, input, select, textarea')
                    .forEach(el => {
                        if (el instanceof HTMLElement) {
                            el.blur();
                        }
                    });

                // 如果是动态创建的模态框，清理它
                if (modalEl.dataset.dynamic) {
                    const modal = bootstrap.Modal.getInstance(modalEl);
                    if (modal) {
                        modal.dispose();
                    }
                    modalEl.remove();
                }
            });
        } catch (error) {
            console.error('初始化模态框管理器失败:', error);
        }
    },

    // 关闭所有模态框
    closeAll() {
        try {
            document.querySelectorAll('.modal.show').forEach((modalEl) => {
                const modal = bootstrap.Modal.getInstance(modalEl);
                if (modal) {
                    modal.hide();
                }
            });
        } catch (error) {
            console.error('关闭所有模态框失败:', error);
        }
    }
};

// ================ 工具函数 ================
// 显示列表加载动画
function showTableLoading(tbody, colSpan = 6) {
    if (!tbody) return;
    tbody.innerHTML = `
        <tr>
            <td colspan="${colSpan}" class="text-center py-4">
                <div class="d-flex justify-content-center align-items-center">
                    <div class="spinner-border text-primary me-2" role="status">
                        <span class="visually-hidden">加载中...</span>
                    </div>
                    <span class="text-primary">正在加载数据...</span>
                </div>
            </td>
        </tr>
    `;
}

// 显示空数据提示
function showEmptyTable(tbody, message = '暂无数据', colSpan = 6) {
    if (!tbody) return;
    tbody.innerHTML = `
        <tr>
            <td colspan="${colSpan}" class="text-center py-4 text-muted">
                <i class="bi bi-inbox fs-2 mb-2 d-block"></i>
                ${message}
            </td>
        </tr>
    `;
}

// 通用对话框函数
function showDialog({ 
    title, 
    content, 
    onShow, 
    onConfirm,
    showFooter = true,
    size = '',
    fullHeight = false
}) {
    return new Promise((resolve) => {
        try {
            const modalDiv = document.createElement('div');
            modalDiv.className = 'modal';
            modalDiv.setAttribute('tabindex', '-1');
            modalDiv.setAttribute('data-dynamic', 'true');
            modalDiv.innerHTML = `
                <div class="modal-dialog ${size ? `modal-${size}` : ''} ${fullHeight ? 'modal-dialog-scrollable' : ''}">
                    <div class="modal-content">
                        <div class="modal-header">
                            <h5 class="modal-title">${title}</h5>
                            <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
                        </div>
                        <div class="modal-body">
                            ${content}
                        </div>
                        ${showFooter ? `
                        <div class="modal-footer">
                            <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">取消</button>
                            <button type="button" class="btn btn-primary" id="dialogConfirm">确定</button>
                        </div>
                        ` : ''}
                    </div>
                </div>
            `;
            document.body.appendChild(modalDiv);

            const modal = new bootstrap.Modal(modalDiv);
            let isResolved = false;

            // 确认按钮点击事件
            const confirmButton = modalDiv.querySelector('#dialogConfirm');
            if (confirmButton) {
                confirmButton.addEventListener('click', async () => {
                    if (onConfirm) {
                        const result = await onConfirm();
                        if (result === false) return;
                    }
                    isResolved = true;
                    modal.hide();
                    resolve(true);
                });
            }

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
                if (!isResolved) {
                    resolve(false);
                }
            };

            modalDiv.addEventListener('hidden.bs.modal', handleHidden);

            // 显示对话框
            if (onShow) onShow();
            modal.show();

        } catch (error) {
            console.error('显示对话框失败:', error);
            resolve(false);
        }
    });
}


