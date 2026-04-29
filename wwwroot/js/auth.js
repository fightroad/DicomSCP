class AuthManager {
    constructor() {
        this.bindEvents();
    }

    bindEvents() {
        try {
            // 绑定修改密码表单事件
            const form = document.getElementById('changePasswordForm');
            if (form) {
                form.addEventListener('submit', (e) => {
                    e.preventDefault();
                    this.savePasswordChange();
                });
            }

            // 绑定修改密码模态框事件
            const modal = document.getElementById('changePasswordModal');
            if (modal) {
                modal.addEventListener('shown.bs.modal', () => {
                    document.getElementById('oldPassword')?.focus();
                });
            }
        } catch (error) {
            console.error('绑定认证事件失败:', error);
            window.showToast('初始化失败', 'error');
            throw error;  // 向上传播错误
        }
    }

    // 修改密码
    changePassword() {
        try {
            const modalElement = document.getElementById('changePasswordModal');
            if (!modalElement) {
                throw new Error('找不到修改密码弹窗元素');
            }
            const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
            const form = document.getElementById('changePasswordForm');
            
            // 清空表单并重置验证状态
            form.reset();
            form.classList.remove('was-validated');
            
            // 显示模态框
            modal.show();
            
        } catch (error) {
            console.error('显示修改密码对话框失败:', error);
            window.showToast('显示修改密码对话框失败', 'error');
        }
    }

    // 保存密码修改
    async savePasswordChange() {
        const form = document.getElementById('changePasswordForm');
        const submitBtn = document.querySelector('#changePasswordModal .modal-footer button.btn-primary');
        
        // 重置按钮状态的辅助函数
        const resetButton = () => {
            submitBtn.disabled = false;
            submitBtn.innerHTML = '确认修改';
        };

        try {
            // 防止重复提交
            if (submitBtn.disabled) {
                return;
            }
            
            // 表单验证
            if (!form.checkValidity()) {
                form.classList.add('was-validated');
                return;
            }

            // 设置按钮为加载状态
            submitBtn.disabled = true;
            submitBtn.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>保存中...';

            const oldPassword = document.getElementById('oldPassword').value.trim();
            const newPassword = document.getElementById('newPassword').value.trim();
            const confirmPassword = document.getElementById('confirmPassword').value.trim();

            // 验证新密码
            if (newPassword !== confirmPassword) {
                window.showToast('两次输入的新密码不一致', 'error');
                resetButton();
                return;
            }

            // 验证新密码长度
            if (newPassword.length < 6) {
                window.showToast('新密码长度不能少于6位', 'error');
                resetButton();
                return;
            }

            // 验证新旧密码不能相同
            if (oldPassword === newPassword) {
                window.showToast('新密码不能与旧密码相同', 'error');
                resetButton();
                return;
            }

            await axios.post('/api/auth/change-password', {
                oldPassword: oldPassword,
                newPassword: newPassword
            });
            
            const modalElement = document.getElementById('changePasswordModal');
            if (modalElement) {
                const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
                modal.hide();
            }
            window.showToast('密码修改成功，请重新登录', 'success');
            setTimeout(() => {
                window.location.href = '/login.html';
            }, 1500);
            
        } catch (error) {
            window.showToast(error.response?.data || '修改密码失败，请重试', 'error');
            resetButton();
        }
    }

    // 获取当前用户名
    async getCurrentUsername() {
        try {
            const response = await axios.get('/api/auth/check-session');
            const username = response.data.username;
            const usernameElement = document.getElementById('currentUsername');
            if (usernameElement) {
                usernameElement.textContent = username;
            }
        } catch (error) {
            // 401 错误会被全局拦截器处理，这里只处理其他错误
            if (error.response?.status !== 401) {
                console.error('获取用户信息失败:', error);
                const usernameElement = document.getElementById('currentUsername');
                if (usernameElement) {
                    usernameElement.textContent = '未知用户';
                }
            }
        }
    }

    // 登出
    async logout() {
        try {
            await axios.post('/api/auth/logout');
            window.location.href = '/login.html';
        } catch (error) {
            console.error('登出失败:', error);
            window.showToast('登出失败', 'error');
            setTimeout(() => {
                window.location.href = '/login.html';
            }, 1500);
        }
    }
} 