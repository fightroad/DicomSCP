// 存储选中的文件
let selectedFiles = new Map();
// 存储选中的节点
let selectedStoreNode = null;

// 移除 DOMContentLoaded 事件监听，由 main.js 统一管理初始化

// 初始化拖放区域
function initDropZone() {
    const dropZone = document.getElementById('dropZone');
    if (!dropZone) return;

    ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(eventName => {
        dropZone.addEventListener(eventName, preventDefaults, false);
    });

    ['dragenter', 'dragover'].forEach(eventName => {
        dropZone.addEventListener(eventName, highlight, false);
    });

    ['dragleave', 'drop'].forEach(eventName => {
        dropZone.addEventListener(eventName, unhighlight, false);
    });

    dropZone.addEventListener('drop', handleDrop, false);

    function highlight(e) {
        dropZone.classList.add('drag-over');
    }

    function unhighlight(e) {
        dropZone.classList.remove('drag-over');
    }

    function preventDefaults(e) {
        e.preventDefault();
        e.stopPropagation();
    }
}

// 处理文件拖放
async function handleDrop(e) {
    e.preventDefault();
    const items = e.dataTransfer.items;
    const counts = { added: 0, skipped: 0 };
    const filePromises = [];

    // 显示加载提示
    window.showToast('正在扫描文件...', 'info');

    for (let item of items) {
        if (item.kind === 'file') {
            const entry = item.webkitGetAsEntry();
            if (entry) {
                if (entry.isDirectory) {
                    // 如果是文件夹，显示文件夹名称
                    window.showToast(`正在扫描文件夹: ${entry.name}`, 'info');
                }
                filePromises.push(processEntry(entry, counts));
            }
        }
    }

    try {
        // 等待所有文件处理完成
        await Promise.all(filePromises);

        // 显示处理结果
        if (counts.added > 0 || counts.skipped > 0) {
            let message = '';
            if (items.length === 1 && items[0].webkitGetAsEntry().isDirectory) {
                // 如果只拖入了一个文件夹，显示文件夹名称和文件数量
                const folderName = items[0].webkitGetAsEntry().name;
                message = `文件夹 "${folderName}" 中包含 ${counts.added} 个DICOM文件`;
            } else {
                // 如果是多个文件或文件夹，只显示总数
                message = `已添加 ${counts.added} 个DICOM文件`;
            }
            if (counts.skipped > 0) {
                message += `，跳过 ${counts.skipped} 个非DICOM文件`;
            }
            window.showToast(message, 'success');
        }
    } catch (error) {
        console.error('处理文件失败:', error);
        window.showToast('处理文件失败', 'error');
    }

    updateUI();
}

// 处理文件系统入口
async function processEntry(entry, counts = { added: 0, skipped: 0 }) {
    try {
        if (entry.isFile) {
            const file = await getFileFromEntry(entry);
            if (file.name.toLowerCase().endsWith('.dcm')) {
                addFileToSelection(file, true);
                counts.added++;
            } else {
                counts.skipped++;
            }
        } else if (entry.isDirectory) {
            await processDirectory(entry, counts);
        }
        return counts;
    } catch (error) {
        console.error('处理文件失败:', error);
        throw error;
    }
}

// 处理目录
async function processDirectory(dirEntry, counts) {
    try {
        const entries = await readDirectoryEntries(dirEntry);
        const promises = entries.map(entry => processEntry(entry, counts));
        await Promise.all(promises);
    } catch (error) {
        console.error('处理目录失败:', error);
        throw error;
    }
}

// 读取目录内容
function readDirectoryEntries(dirEntry) {
    return new Promise((resolve, reject) => {
        const reader = dirEntry.createReader();
        const allEntries = [];

        function readAllEntries() {
            reader.readEntries(
                (results) => {
                    if (results.length) {
                        allEntries.push(...results);
                        readAllEntries();
                    } else {
                        resolve(allEntries);
                    }
                },
                (error) => {
                    console.error('读取目录失败:', error);
                    reject(error);
                }
            );
        }

        readAllEntries();
    });
}

// 从 FileEntry 获取 File 对象
function getFileFromEntry(entry) {
    return new Promise((resolve, reject) => {
        entry.file(resolve, reject);
    });
}

// 初始化文件输入
function initFileInputs() {
    const fileInput = document.getElementById('fileInput');
    const folderInput = document.getElementById('folderInput');
    const clearButton = document.getElementById('clearButton');
    const sendButton = document.getElementById('sendButton');

    fileInput.addEventListener('click', function(e) {
        e.preventDefault();
        const tempInput = document.createElement('input');
        tempInput.type = 'file';
        tempInput.multiple = true;
        tempInput.accept = '.dcm';
        tempInput.style.display = 'none';
        
        tempInput.addEventListener('change', handleFileSelect);
        document.body.appendChild(tempInput);
        tempInput.click();
        
        setTimeout(() => {
            document.body.removeChild(tempInput);
        }, 100);
    });

    folderInput.addEventListener('click', function(e) {
        e.preventDefault();
        const tempInput = document.createElement('input');
        tempInput.type = 'file';
        tempInput.webkitdirectory = true;
        tempInput.directory = true;
        tempInput.style.display = 'none';
        
        tempInput.addEventListener('change', handleFileSelect);
        document.body.appendChild(tempInput);
        tempInput.click();
        
        setTimeout(() => {
            document.body.removeChild(tempInput);
        }, 100);
    });

    clearButton.addEventListener('click', clearSelection);
    sendButton.addEventListener('click', sendFiles);
}

// 处理文件选择
function handleFileSelect(e) {
    const files = Array.from(e.target.files);
    let addedCount = 0;
    let skippedCount = 0;
    
    files.forEach(file => {
        if (file.name.toLowerCase().endsWith('.dcm')) {
            addFileToSelection(file, true);
            addedCount++;
        } else {
            skippedCount++;
        }
    });
    
    if (addedCount > 0 || skippedCount > 0) {
        let message = `已添加 ${addedCount} 个DICOM文件`;
        if (skippedCount > 0) {
            message += `，跳过 ${skippedCount} 个非DICOM文件`;
        }
        window.showToast(message, 'success');
    }
    
    updateUI();
}

// 添加文件到选择列表
function addFileToSelection(file, skipUiUpdate = false) {
    const fileId = generateFileId(file);
    if (!selectedFiles.has(fileId)) {
        selectedFiles.set(fileId, {
            file: file,
            status: 'pending',
            message: ''
        });
    }
    if (!skipUiUpdate) {
        updateUI();
    }
}

// 生成文件ID
function generateFileId(file) {
    return `${file.name}-${file.size}-${file.lastModified}`;
}

// 更新UI显示
function updateUI() {
    const selectedFilesDiv = document.getElementById('selectedFiles');
    const fileList = document.getElementById('fileList');
    const sendButton = document.getElementById('sendButton');
    const clearButton = document.getElementById('clearButton');
    const totalFiles = selectedFiles.size;

    if (totalFiles > 0) {
        selectedFilesDiv.style.display = 'block';
        
        // 统计文件状态
        const stats = Array.from(selectedFiles.values()).reduce((acc, info) => {
            acc[info.status] = (acc[info.status] || 0) + 1;
            return acc;
        }, {});

        // 更新状态显示
        const statusInfo = document.createElement('div');
        statusInfo.className = 'mb-2';
        statusInfo.innerHTML = `
            总计: ${totalFiles} 个文件
            ${stats.pending ? `<span class="badge bg-secondary ms-2">待发送: ${stats.pending}</span>` : ''}
            ${stats.sending ? `<span class="badge bg-primary ms-2">发送中: ${stats.sending}</span>` : ''}
            ${stats.success ? `<span class="badge bg-success ms-2">已完成: ${stats.success}</span>` : ''}
            ${stats.error ? `<span class="badge bg-danger ms-2">失败: ${stats.error}</span>` : ''}
        `;
        
        // 替换或添加状态信息
        const existingStatus = selectedFilesDiv.querySelector('.status-info');
        if (existingStatus) {
            existingStatus.replaceWith(statusInfo);
        } else {
            selectedFilesDiv.insertBefore(statusInfo, selectedFilesDiv.firstChild);
        }
        statusInfo.classList.add('status-info');

        // 只有当有待发送或失败的文件时才启用发送按钮
        const hasPendingFiles = stats.pending > 0 || stats.error > 0;
        sendButton.disabled = !hasPendingFiles;

        // 文件列表
        fileList.innerHTML = Array.from(selectedFiles.entries())
            .map(([fileId, fileInfo]) => `
                <tr data-file-id="${fileId}">
                    <td title="${fileInfo.file.name}">
                        ${fileInfo.file.name}
                    </td>
                    <td>${formatFileSize(fileInfo.file.size)}</td>
                    <td>
                        <span class="badge bg-${getStatusBadgeClass(fileInfo.status)}">
                            ${getStatusText(fileInfo.status)}
                        </span>
                        ${fileInfo.message ? 
                            `<br><small class="${fileInfo.status === 'error' ? 'text-danger' : 'text-muted'}">${fileInfo.message}</small>` : ''}
                    </td>
                    <td>
                        ${fileInfo.status !== 'sending' ? 
                            `<button type="button" class="btn btn-sm btn-danger" onclick="removeFile('${fileId}')">
                                删除
                            </button>` : ''}
                        ${fileInfo.status === 'error' ? 
                            `<button type="button" class="btn btn-sm btn-warning ms-1" onclick="retryFile('${fileId}')">
                                重试
                            </button>` : ''}
                    </td>
                </tr>
            `).join('');
    } else {
        selectedFilesDiv.style.display = 'none';
        sendButton.disabled = true;
        fileList.innerHTML = '';
    }

    // 清空按钮状态
    clearButton.disabled = totalFiles === 0 || Array.from(selectedFiles.values()).some(info => info.status === 'sending');
}

// 获取状态对应的Bootstrap样式类
function getStatusBadgeClass(status) {
    switch (status) {
        case 'pending': return 'secondary';
        case 'sending': return 'primary';
        case 'success': return 'success';
        case 'error': return 'danger';
        default: return 'secondary';
    }
}

// 获取状态文本
function getStatusText(status) {
    switch (status) {
        case 'pending': return '待发送';
        case 'sending': return '发送中';
        case 'success': return '已发送';
        case 'error': return '发送失败';
        default: return status;
    }
}

// 格式化文件大小
function formatFileSize(bytes) {
    if (bytes === 0) return '0 Bytes';
    const k = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
}

// 移除文件
function removeFile(fileId) {
    selectedFiles.delete(fileId);
    updateUI();
}

// 清空选择
function clearSelection() {
    selectedFiles.clear();
    updateUI();
}

// 重试文件
function retryFile(fileId) {
    const fileInfo = selectedFiles.get(fileId);
    if (fileInfo) {
        fileInfo.status = 'pending';
        fileInfo.message = '';
        updateUI();
    }
}

// 加载节点列表
async function loadStoreNodes() {
    try {
        const response = await axios.get('/api/StoreScu/nodes');
        const nodes = response.data;

        const select = document.getElementById('storeNode');
        if (!select) return;

        if (nodes.length === 0) {
            select.innerHTML = '<option value="">未配置DICOM节点</option>';
            return;
        }

        // 生成节点选项
        select.innerHTML = nodes.map(node => `
            <option value="${node.name}">
                ${node.name} (${node.aeTitle}@${node.hostName})
            </option>
        `).join('');

        // 在目标节点文字后添加测试按钮
        const label = select.previousElementSibling;
        if (label && label.classList.contains('form-label')) {
            // 将文本节点和按钮分开
            label.textContent = '目标节点';
            
            // 添加测试按钮
            const testButton = document.createElement('button');
            testButton.type = 'button';
            testButton.className = 'btn btn-outline-primary btn-sm ms-2';
            testButton.style.padding = '0 0.5rem';
            testButton.style.fontSize = '0.875rem';
            testButton.style.lineHeight = '1.5';
            testButton.style.verticalAlign = 'middle';
            testButton.innerHTML = '<i class="bi bi-broadcast"></i> 测试';
            testButton.onclick = verifyStoreNode;
            
            // 添加按钮到标签中
            label.appendChild(testButton);
        }

        // 如果有保存的选择，恢复它
        if (selectedStoreNode) {
            select.value = selectedStoreNode;
        } else {
            // 否则使用第一个节点
            selectedStoreNode = nodes[0]?.name;
            if (selectedStoreNode) {
                select.value = selectedStoreNode;
            }
        }

        // 监听节点选择变化
        select.addEventListener('change', (e) => {
            selectedStoreNode = e.target.value;
        });

    } catch (error) {
        console.error('加载节点列表失败:', error);
        window.showToast('加载节点列表失败', 'error');
    }
}

// 测试存储节点连通性
async function verifyStoreNode(event) {
    const nodeId = document.getElementById('storeNode').value;
    if (!nodeId) {
        window.showToast('请选择要测试的节点', 'error');
        return;
    }

    // 获取点击的按钮
    const testButton = event.target.closest('button');
    const originalHtml = testButton.innerHTML;
    testButton.disabled = true;
    testButton.innerHTML = '<span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> 测试中...';

    try {
        const response = await axios.post(`/api/StoreScu/verify/${nodeId}`);
        const result = response.data;

        if (result.success) {
            window.showToast('节点连接测试成功', 'success');
        } else {
            window.showToast('节点连接测试失败', 'error');
        }
    } catch (error) {
        console.error('测试节点连接失败:', error);
        window.showToast(error.response?.data || '节点连接测试失败', 'error');
    } finally {
        testButton.disabled = false;
        testButton.innerHTML = originalHtml;
    }
}

// 发送文件
async function sendFiles() {
    const nodeId = document.getElementById('storeNode').value;
    if (!nodeId) {
        window.showToast('请选择目标节点', 'error');
        return;
    }

    const sendButton = document.getElementById('sendButton');
    const clearButton = document.getElementById('clearButton');
    sendButton.disabled = true;
    clearButton.disabled = true;

    try {
        const pendingFiles = Array.from(selectedFiles.entries())
            .filter(([_, fileInfo]) => fileInfo.status === 'pending' || fileInfo.status === 'error');

        if (pendingFiles.length === 0) {
            window.showToast('没有需要发送的文件', 'error');
            return;
        }

        // 分批处理文件
        const batchSize = 10;
        const totalBatches = Math.ceil(pendingFiles.length / batchSize);
        let successCount = 0;
        let failureCount = 0;

        for (let i = 0; i < pendingFiles.length; i += batchSize) {
            const batch = pendingFiles.slice(i, i + batchSize);
            const formData = new FormData();
            
            batch.forEach(([_, fileInfo]) => {
                fileInfo.status = 'sending';
                formData.append('files', fileInfo.file);
            });
            updateUI();

            try {
                await axios.post(`/api/StoreSCU/send/${nodeId}`, formData);

                batch.forEach(([_, fileInfo]) => {
                    fileInfo.status = 'success';
                    successCount++;
                });
            } catch (error) {
                batch.forEach(([_, fileInfo]) => {
                    fileInfo.status = 'error';
                    fileInfo.message = error.message;
                    failureCount++;
                });
            }
            updateUI();
        }

        // 显示最终结果
        window.showToast(
            `发送完成：${successCount} 个成功${failureCount > 0 ? `，${failureCount} 个失败` : ''}`, 
            failureCount === 0 ? 'success' : 'error'
        );

    } catch (error) {
        console.error('发送失败:', error);
        window.showToast(error.response?.data || error.message, 'error');
    } finally {
        sendButton.disabled = false;
        clearButton.disabled = false;
        updateUI();
    }
}
 