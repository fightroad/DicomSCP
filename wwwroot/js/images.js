// 影像管理的分页变量
let imagesCurrentPage = 1;
const imagesPageSize = 10;

// 加载影像数据
async function loadImages(page = 1) {
    const tbody = document.getElementById('images-table-body');
    showTableLoading(tbody, 8);  // 影像列表有8列

    try {
        const patientId = document.getElementById('images-searchPatientId')?.value || '';
        const patientName = document.getElementById('images-searchPatientName')?.value || '';
        const accessionNumber = document.getElementById('images-searchAccessionNumber')?.value || '';
        const modality = document.getElementById('images-searchModality')?.value || '';
        const studyDate = document.getElementById('images-searchStudyDate')?.value || '';
        
        const params = {
            page,
            pageSize: imagesPageSize,
            patientId,
            patientName,
            accessionNumber,
            modality,
            studyDate
        };

        const response = await axios.get('/api/images', { params });
        const result = response.data;

        if (result.items.length === 0) {
            showEmptyTable(tbody, '暂无影像数据', 8);
            return;
        }

        displayImages(result.items);
        updateImagesPagination(result);
        
        // 更新当前页码
        imagesCurrentPage = page;
        
    } catch (error) {
        handleError(error, '加载影像失败');
        showEmptyTable(tbody, '加载失败，请重试', 8);
    }
}

// 显示影像数据
function displayImages(items) {
    const tbody = document.getElementById('images-table-body');
    if (!tbody) return;

    const baseUrl = `${window.location.protocol}//${window.location.host}`;
    const fragment = document.createDocumentFragment();
    
    items.forEach(item => {
        const tr = document.createElement('tr');
        tr.setAttribute('onclick', 'toggleSeriesInfo(this)');
        tr.setAttribute('data-study-uid', item.studyInstanceUid);
        tr.innerHTML = `
            <td>${item.patientId || ''}</td>
            <td>${item.patientName || ''}</td>
            <td>${item.accessionNumber || ''}</td>
            <td>${item.modality || ''}</td>
            <td>${formatDate(item.studyDate) || ''}</td>
            <td>${item.studyDescription || ''}</td>
            <td>${item.numberOfInstances || 0}</td>
            <td>
                <button class="btn btn-sm btn-primary me-1" onclick="openOHIF('${item.studyInstanceUid}', event)" title="OHIF预览">
                    <i class="bi bi-eye me-1"></i>OHIF
                </button>
                <button class="btn btn-sm btn-primary me-1" onclick="openWeasis('${item.studyInstanceUid}', event)" title="Weasis预览">
                    <i class="bi bi-eye me-1"></i>Weasis
                </button>
                <button class="btn btn-sm btn-danger" onclick="deleteStudy('${item.studyInstanceUid}', event)" title="删除">
                    <i class="bi bi-trash me-1"></i>删除
                </button>
            </td>
        `;
        fragment.appendChild(tr);
    });

    tbody.innerHTML = '';
    tbody.appendChild(fragment);
}

// 更新影像分页信息
function updateImagesPagination(result) {
    try {
        const { totalCount, page, pageSize, totalPages } = result;
        const start = (page - 1) * pageSize + 1;
        const end = Math.min(page * pageSize, totalCount);
        
        const elements = {
            currentPage: document.getElementById('images-currentPage'),
            currentRange: document.getElementById('images-currentRange'),
            totalCount: document.getElementById('images-totalCount'),
            prevPage: document.getElementById('images-prevPage'),
            nextPage: document.getElementById('images-nextPage')
        };

        // 检查所有必需的元素是否存在
        Object.entries(elements).forEach(([key, element]) => {
            if (!element) throw new Error(`找不到元素: ${key}`);
        });
        
        elements.currentPage.textContent = page;
        elements.currentRange.textContent = totalCount > 0 ? `${start}-${end}` : '0-0';
        elements.totalCount.textContent = totalCount;
        
        elements.prevPage.disabled = page <= 1;
        elements.nextPage.disabled = page >= totalPages || totalCount === 0;
    } catch (error) {
        handleError(error, '更新分页信息失败');
    }
}

// 绑定影像管理相关事件
function bindImagesEvents() {
    try {
        // 分页按钮事件绑定
        const prevPageBtn = document.getElementById('images-prevPage');
        const nextPageBtn = document.getElementById('images-nextPage');

        if (prevPageBtn) {
            prevPageBtn.replaceWith(prevPageBtn.cloneNode(true));
            const newPrevBtn = document.getElementById('images-prevPage');
            newPrevBtn.addEventListener('click', () => {
                if (imagesCurrentPage > 1) {
                    imagesCurrentPage--;
                    loadImages(imagesCurrentPage);
                }
            });
        }

        if (nextPageBtn) {
            nextPageBtn.replaceWith(nextPageBtn.cloneNode(true));
            const newNextBtn = document.getElementById('images-nextPage');
            newNextBtn.addEventListener('click', () => {
                const totalPages = parseInt(newNextBtn.getAttribute('data-total-pages') || '1');
                if (imagesCurrentPage < totalPages) {
                    imagesCurrentPage++;
                    loadImages(imagesCurrentPage);
                }
            });
        }

        // 搜索表单事件
        const searchForm = document.getElementById('imagesSearchForm');
        if (searchForm) {
            searchForm.addEventListener('submit', (e) => {
                e.preventDefault();
                imagesCurrentPage = 1;
                loadImages();
            });

            // 重置按钮事件
            const resetButton = searchForm.querySelector('button[type="reset"]');
            if (resetButton) {
                resetButton.addEventListener('click', (e) => {
                    e.preventDefault();
                    searchForm.reset();
                    imagesCurrentPage = 1;
                    loadImages();
                });
            }
        }
    } catch (error) {
        console.error('绑定影像管理事件失败:', error);
        window.showToast('初始化失败', 'error');
    }
}

// 删除影像研究
async function deleteStudy(studyInstanceUid, event) {
    if (event) {
        event.stopPropagation();
    }

    if (!await showConfirmDialog('确认删除', '确定要删除这个检查吗？此操作不可恢复。')) {
        return;
    }

    try {
        await axios.delete(`/api/images/${studyInstanceUid}`);
        window.showToast('操作成功', 'success');

        // 获取当前页的数据数量
        const tbody = document.getElementById('images-table-body');
        const currentPageItems = tbody.getElementsByTagName('tr').length;
        
        // 如果当前页只有一条数据，且不是第一页，则加载上一页
        if (currentPageItems === 1 && imagesCurrentPage > 1) {
            imagesCurrentPage--;
        }

        loadImages(imagesCurrentPage);
    } catch (error) {
        handleError(error, '删除失败');
    }
}

// 切换序列信息显示
async function toggleSeriesInfo(row) {
    const studyUid = $(row).data('study-uid');
    const seriesRow = $(row).next('.series-info');
    
    if (seriesRow.is(':visible')) {
        seriesRow.hide();
        return;
    }

    try {
        // 显示加载动画
        const loadingRow = $(`
            <tr class="series-info">
                <td colspan="8" class="text-center py-3">
                    <div class="spinner-border text-primary" role="status">
                        <span class="visually-hidden">加载中...</span>
                    </div>
                </td>
            </tr>
        `);
        $(row).after(loadingRow);

        const response = await axios.get(`/api/images/${studyUid}/series`);
        const data = response.data;
        
        // 创建序列信息行
        const seriesInfoRow = $(`
            <tr class="series-info">
                <td colspan="8">
                    <div class="series-container">
                        <table class="table table-sm table-bordered series-detail-table">
                            <thead>
                                <tr>
                                    <th style="width: 50px">序列号</th>
                                    <th style="width: 100px">检查类型</th>
                                    <th style="width: 500px">序列描述</th>
                                    <th style="width: 80px">图像数量</th>
                                    <th style="width: 80px">操作</th>
                                </tr>
                            </thead>
                            <tbody></tbody>
                        </table>
                    </div>
                </td>
            </tr>
        `);
        
        const tbody = seriesInfoRow.find('tbody');
        if (data.length === 0) {
            tbody.append(`
                <tr>
                    <td colspan="5" class="text-center text-muted py-3">
                        <i class="bi bi-inbox fs-2 mb-2 d-block"></i>
                        暂无序列数据
                    </td>
                </tr>
            `);
        } else {
            data.forEach(series => {
                tbody.append(`
                    <tr>
                        <td>${series.seriesNumber || ''}</td>
                        <td>${series.modality || '未知'}</td>
                        <td title="${series.seriesDescription || ''}">${series.seriesDescription || ''}</td>
                        <td>${series.numberOfInstances || 0}</td>
                        <td>
                            <button class="btn btn-sm btn-primary" onclick="previewSeries('${studyUid}', '${series.seriesInstanceUid}')">
                                <i class="bi bi-eye me-1"></i>预览
                            </button>
                        </td>
                    </tr>
                `);
            });
        }
        
        // 移除加载动画和已存在的序列信息行
        $(row).siblings('.series-info').remove();
        // 添加新的序列信息行
        $(row).after(seriesInfoRow);

    } catch (error) {
        console.error('获取序列数据失败:', error);
        window.showToast('获取失败', 'error');
        // 移除加载动画
        $(row).siblings('.series-info').remove();
    }
}

// 预览序列
function previewSeries(studyUid, seriesUid) {
    try {
        return showDialog({
            title: 'DICOM 查看器',
            content: `
                <div style="height: calc(90vh - 120px);">
                    <iframe 
                        src="/viewer.html?studyUid=${encodeURIComponent(studyUid)}&seriesUid=${encodeURIComponent(seriesUid)}"
                        style="width: 100%; height: 100%; border: none;"
                        onload="this.style.opacity='1'"
                    ></iframe>
                </div>
            `,
            showFooter: false,  // 不显示底部按钮
            size: 'xl',  // 使用超大对话框
            fullHeight: true  // 使用全高度
        });
    } catch (error) {
        console.error('预览序列失败:', error);
        window.showToast('预览序列失败', 'error');
    }
}

// 格式化日期
function formatDate(dateStr) {
    if (!dateStr) return '';
    return dateStr.replace(/(\d{4})(\d{2})(\d{2})/, '$1-$2-$3');
}

// 添加打开Weasis的函数
function openWeasis(studyUid, event) {
    try {
        if (event) {
            event.stopPropagation();
        }

        const baseUrl = `${window.location.protocol}//${window.location.host}`;
        const manifestUrl = `${baseUrl}/viewer/weasis/${studyUid}`;
        const weasisUrl = `weasis://?$dicom:get -w "${manifestUrl}"`;
        
        console.log('Opening Weasis URL:', weasisUrl);

        // 创建并点击隐藏的链接
        const link = document.createElement('a');
        link.style.display = 'none';
        link.href = weasisUrl;
        link.rel = 'noopener noreferrer';
        document.body.appendChild(link);
        link.click();
        
        // 延迟移除链接
        setTimeout(() => {
            document.body.removeChild(link);
        }, 100);

        // 显示提示
        window.showToast('正在启动Weasis...', 'info');
    } catch (error) {
        console.error('打开Weasis失败:', error);
        window.showToast('打开Weasis失败', 'error');
    }
}

// 添加打开 OHIF 的函数
function openOHIF(studyUid, event) {
    try {
        if (event) {
            event.stopPropagation();
        }

        // 移除已存在的对话框
        const existingDialog = document.getElementById('ohifViewerDialog');
        if (existingDialog) {
            existingDialog.remove();
        }

        const baseUrl = `${window.location.protocol}//${window.location.host}`;
        const ohifUrl = `${baseUrl}/dicomviewer/viewer/dicomjson?url=${encodeURIComponent(`${baseUrl}/viewer/ohif/${studyUid}`)}`;
        
        console.log('Opening OHIF URL:', ohifUrl);

        // 创建对话框 HTML
        const dialogHtml = `
            <div class="modal fade" id="ohifViewerDialog" tabindex="-1" aria-labelledby="ohifViewerDialogLabel" aria-hidden="true">
                <div class="modal-dialog modal-fullscreen p-0 m-0">
                    <div class="modal-content border-0 rounded-0 vh-100" style="background: #000;">
                        <div class="modal-header border-0 p-0 d-flex align-items-center justify-content-between" style="background: #090c3b; height: 40px; min-height: 40px;">
                            <h5 class="modal-title text-white m-0 ps-3" id="ohifViewerDialogLabel" style="font-size: 14px; font-weight: normal;">OHIF 查看器</h5>
                            <button type="button" class="btn-close-custom me-3" data-bs-dismiss="modal" aria-label="Close">
                                <svg width="14" height="14" fill="currentColor" style="color: #91b9cd;" viewBox="0 0 16 16">
                                    <path d="M2.146 2.146a.5.5 0 0 1 .708 0L8 7.293l5.146-5.147a.5.5 0 0 1 .708.708L8.707 8l5.147 5.146a.5.5 0 0 1-.708.708L8 8.707l-5.146 5.147a.5.5 0 0 1-.708-.708L7.293 8 2.146 2.854a.5.5 0 0 1 0-.708z"/>
                                </svg>
                            </button>
                        </div>
                        <div class="modal-body p-0 h-100" style="height: calc(100vh - 40px) !important;">
                            <iframe 
                                src="${ohifUrl}"
                                style="width: 100%; height: 100%; border: none; display: block; background: #000;"
                                onload="this.style.opacity='1'"
                            ></iframe>
                        </div>
                    </div>
                </div>
            </div>
        `;

        // 添加自定义样式
        const styleId = 'ohif-viewer-styles';
        if (!document.getElementById(styleId)) {
            const style = document.createElement('style');
            style.id = styleId;
            style.textContent = `
                #ohifViewerDialog {
                    padding: 0 !important;
                }
                #ohifViewerDialog .modal-dialog {
                    margin: 0 !important;
                    max-width: 100% !important;
                    width: 100% !important;
                    height: 100% !important;
                }
                #ohifViewerDialog .modal-content {
                    min-height: 100vh !important;
                }
                #ohifViewerDialog .modal-header {
                    box-shadow: 0 2px 4px rgba(0,0,0,0.3);
                }
                #ohifViewerDialog .modal-body {
                    overflow: hidden !important;
                }
                #ohifViewerDialog .btn-close-custom {
                    background: none;
                    border: none;
                    padding: 8px;
                    cursor: pointer;
                    opacity: 0.8;
                    transition: all 0.2s ease;
                    line-height: 1;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                }
                #ohifViewerDialog .btn-close-custom:hover {
                    opacity: 1;
                    transform: scale(1.1);
                }
                #ohifViewerDialog .btn-close-custom svg {
                    display: block;
                }
            `;
            document.head.appendChild(style);
        }

        // 添加对话框到 body
        document.body.insertAdjacentHTML('beforeend', dialogHtml);

        // 获取对话框元素
        const dialogEl = document.getElementById('ohifViewerDialog');
        
        // 创建 Bootstrap 模态框实例
        const modal = new bootstrap.Modal(dialogEl, {
            backdrop: 'static',
            keyboard: false
        });

        // 监听对话框关闭事件
        dialogEl.addEventListener('hidden.bs.modal', function () {
            // 移除 aria-hidden 属性
            this.removeAttribute('aria-hidden');
            // 移除对话框
            dialogEl.remove();
        });

        // 显示对话框
        modal.show();

    } catch (error) {
        console.error('打开OHIF失败:', error);
        window.showToast('打开OHIF失败', 'error');
    }
} 