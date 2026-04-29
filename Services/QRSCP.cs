using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FellowOakDicom.Imaging.Codec;
using DicomSCP.Configuration;
using DicomSCP.Repository;
using DicomSCP.Models;
using System.Collections.Concurrent;  // 添加这个引用

namespace DicomSCP.Services;

public class QRSCP : DicomService, IDicomServiceProvider, IDicomCEchoProvider, IDicomCFindProvider, IDicomCMoveProvider, IDicomCGetProvider, IDicomCStoreProvider
{
    private static DicomSettings? _globalSettings;
    private static DicomRepository? _globalRepository;

    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes = new[]
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    private static readonly DicomTransferSyntax[] AcceptedImageTransferSyntaxes = new[]
    {
        DicomTransferSyntax.JPEGLSLossless,            // JPEG-LS Lossless
        DicomTransferSyntax.JPEG2000Lossless,          // JPEG 2000 Lossless
        DicomTransferSyntax.JPEGProcess14SV1,          // JPEG Lossless
        DicomTransferSyntax.RLELossless,               // RLE Lossless
        DicomTransferSyntax.JPEGLSNearLossless,        // JPEG-LS Near Lossless
        DicomTransferSyntax.JPEG2000Lossy,             // JPEG 2000 Lossy
        DicomTransferSyntax.ExplicitVRLittleEndian,    // Explicit Little Endian
        DicomTransferSyntax.ImplicitVRLittleEndian,    // Implicit Little Endian
        DicomTransferSyntax.ExplicitVRBigEndian        // Explicit Big Endian
    };

    private readonly DicomSettings _settings;
    private readonly DicomRepository _repository;
    private bool _associationReleaseLogged = false;
    private bool _transferSyntaxLogged = false;

    // 用于跟踪每个目标 AE 的发送任务
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _activeDestinations = new();

    public static void Configure(DicomSettings settings, DicomRepository repository)
    {
        _globalSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        _globalRepository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public QRSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        ILogger log,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        try
        {
            _settings = _globalSettings ?? throw new InvalidOperationException("QRSCP is not configured");
            _repository = _globalRepository ?? throw new InvalidOperationException("QRSCP is not configured");
            DicomLogger.Information("QRSCP", "QR服务初始化完成");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "QR服务初始化失");
            throw;
        }
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        try
        {
            DicomLogger.Information("QRSCP", "收到关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
                association.CalledAE, association.CallingAE);

            // 验证 Called AE
            var calledAE = association.CalledAE;
            var expectedAE = _settings?.QRSCP.AeTitle ?? string.Empty;

            if (!string.Equals(expectedAE, calledAE, StringComparison.OrdinalIgnoreCase))
            {
                DicomLogger.Warning("QRSCP", "拒绝错误的 Called AE: {CalledAE}, 期望: {ExpectedAE}", 
                    calledAE, expectedAE);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CalledAENotRecognized);
            }

            // 验证 Calling AE
            if (string.IsNullOrEmpty(association.CallingAE))
            {
                DicomLogger.Warning("QRSCP", "拒绝空的 Calling AE");
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CallingAENotRecognized);
            }

            // 只在配置了验证时才检查 AllowedCallingAEs
            if (_settings?.QRSCP.ValidateCallingAE == true)
            {
                if (!_settings.QRSCP.AllowedCallingAEs.Contains(association.CallingAE, StringComparer.OrdinalIgnoreCase))
                {
                    DicomLogger.Warning("QRSCP", "拒绝未授权的调用方AE: {CallingAE}", association.CallingAE);
                    return SendAssociationRejectAsync(
                        DicomRejectResult.Permanent,
                        DicomRejectSource.ServiceUser,
                        DicomRejectReason.CallingAENotRecognized);
                }
            }

            DicomLogger.Information("QRSCP", "验证通过 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
                calledAE, association.CallingAE);

            var storageCount = 0;
            foreach (var pc in association.PresentationContexts)
            {
                // 检查是否支持请求的服务
                if (pc.AbstractSyntax == DicomUID.Verification ||                                // C-ECHO
                    pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelFind || // C-FIND
                    pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelFind || // C-FIND (Patient Root)
                    pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove || // C-MOVE
                    pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove || // C-MOVE (Patient Root)
                    pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelGet ||  // C-GET
                    pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelGet || // C-GET (Patient Root)
                    pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)             // Storage (for C-GET)
                {
                    // 记录服务接受日志
                    if (pc.AbstractSyntax.StorageCategory == DicomStorageCategory.None)
                    {
                        DicomLogger.Information("QRSCP", "接受服务 - AET: {CallingAE}, 服务: {Service}", 
                            association.CallingAE, pc.AbstractSyntax.Name);
                    }

                    // 根据服务类型选择合适的传输语法
                    if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
                    {
                        // 对于存储类服务（C-GET需要），接受所有支持的传输语法
                        pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
                    }
                    else if (pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelGet ||
                             pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelGet ||
                             pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove ||
                             pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove)
                    {
                        // 对于 C-GET/C-MOVE 服务，同时接受基本传输语法和图像传输语法
                        pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes.Concat(AcceptedTransferSyntaxes).Distinct().ToArray());
                        DicomLogger.Information("QRSCP", "为C-GET/C-MOVE服务接受传输语法 - 服务: {Service}", pc.AbstractSyntax.Name);
                    }
                    else
                    {
                        // 对于其他服务，使用基本传输语法
                        pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                        DicomLogger.Information("QRSCP", "为基本服务接受传输语法 - 服务: {Service}", pc.AbstractSyntax.Name);
                    }
                }
                else
                {
                    DicomLogger.Warning("QRSCP", "拒绝不支持的服务 - AET: {CallingAE}, 服务: {Service}", 
                        association.CallingAE, pc.AbstractSyntax.Name);
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                }
            }

            // 如果有存储类服务，只记录一条汇总日志
            if (storageCount > 0)
            {
                DicomLogger.Information("QRSCP", "接受存储类服务 - AET: {CallingAE}, 支持的存储类数: {Count}", 
                    association.CallingAE, storageCount);
            }

            return SendAssociationAcceptAsync(association);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "处理关联请求失败");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.NoReasonGiven);
        }
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        if (!_associationReleaseLogged)
        {
            DicomLogger.Information("QRSCP", "接收到关联释放请求");
            _associationReleaseLogged = true;
        }
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("QRSCP", "接收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        try
        {
            if (exception != null)
            {
                DicomLogger.Error("QRSCP", exception, "连接异常关闭");
            }

            // 重置状态
            _associationReleaseLogged = false;
            _transferSyntaxLogged = false;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "处理连接关闭失败");
        }
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Information("QRSCP", "收到 C-ECHO 请求 - 来自: {CallingAE}", Association.CallingAE);
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        DicomLogger.Information("QRSCP", "收到 C-FIND 请求 - 来自: {CallingAE}, 级别: {Level}", 
            Association.CallingAE, request.Level);

        if (request.Level != DicomQueryRetrieveLevel.Study && 
            request.Level != DicomQueryRetrieveLevel.Series && 
            request.Level != DicomQueryRetrieveLevel.Image &&
            request.Level != DicomQueryRetrieveLevel.Patient)
        {
            yield return new DicomCFindResponse(request, DicomStatus.QueryRetrieveIdentifierDoesNotMatchSOPClass);
            yield break;
        }

        // 使 fo-dicom 的查询理
        var responses = request.Level switch
        {
            DicomQueryRetrieveLevel.Patient => await HandlePatientLevelFind(request),
            DicomQueryRetrieveLevel.Study => await HandleStudyLevelFind(request),
            DicomQueryRetrieveLevel.Series => await HandleSeriesLevelFind(request),
            DicomQueryRetrieveLevel.Image => await HandleImageLevelFind(request),
            _ => new List<DicomCFindResponse>()
        };

        foreach (var response in responses)
        {
            yield return response;
        }

        yield return new DicomCFindResponse(request, DicomStatus.Success);
    }

    private async Task<List<DicomCFindResponse>> HandleStudyLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        try
        {
            // 从请求中获取查询参数
            var queryParams = ExtractStudyQueryParameters(request);
            
            DicomLogger.Information("QRSCP", "Study级查询参数 - PatientId: {PatientId}, PatientName: {PatientName}, " +
                "AccessionNumber: {AccessionNumber}, 日期范围: {StartDate} - {EndDate}, Modality: {Modality},StudyInstanceUid: {StudyInstanceUid}",
                queryParams.PatientId,
                queryParams.PatientName,
                queryParams.AccessionNumber,
                queryParams.DateRange.StartDate,
                queryParams.DateRange.EndDate,
                queryParams.Modalities,
                queryParams.StudyInstanceUid);

            // 从数据库查询数据
            var studies = await Task.Run(() => _repository.GetStudies(
                queryParams.PatientId,
                queryParams.PatientName,
                queryParams.AccessionNumber,
                queryParams.DateRange,
                queryParams.Modalities,
                queryParams.StudyInstanceUid,
                0,     // offset
                1000   // 限制返回1000条记录
            ));

            DicomLogger.Information("QRSCP", "Study级查询结果 - 记录数: {Count}", studies.Count);

            // 构建响应
            foreach (var study in studies)
            {
                var response = CreateStudyResponse(request, study);
                responses.Add(response);
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Study级查询失败: {Message}", ex.Message);
            responses.Add(new DicomCFindResponse(request, DicomStatus.ProcessingFailure));
        }

        return responses;
    }

    private record StudyQueryParameters(
        string PatientId,
        string PatientName,
        string AccessionNumber,
        (string StartDate, string EndDate) DateRange,
        string[] Modalities,
        string StudyInstanceUid);

    private StudyQueryParameters ExtractStudyQueryParameters(DicomCFindRequest request)
    {
        // 处理可能为 null 的字符串
        string ProcessValue(string? value) => 
            (value?.Replace("*", "")) ?? string.Empty;

        // 记录原始日期值
        var studyDate = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDate, string.Empty);
        var studyTime = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyTime, string.Empty);
        if (!string.IsNullOrEmpty(studyDate))
        {
            DicomLogger.Debug("QRSCP", "查询日期: {Date}", studyDate);
        }

        // 处理日期范围
        (string StartDate, string EndDate) ProcessDateRange(string? dateValue)
        {
            if (string.IsNullOrEmpty(dateValue))
                return (string.Empty, string.Empty);  // 返回空，查询所有记录

            // 移除可能的 VR 和标签信息
            var cleanDateValue = dateValue;
            if (dateValue.Contains("DA"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(dateValue, @"\d{8}");
                if (match.Success)
                {
                    cleanDateValue = match.Value;
                }
            }

            // 处理 DICOM 日期范围格式
            if (cleanDateValue.Contains("-"))
            {
                var parts = cleanDateValue.Split('-');
                if (parts.Length == 2)
                {
                    var startDate = parts[0].Trim();
                    var endDate = parts[1].Trim();

                    // 处理开放式范围
                    if (string.IsNullOrEmpty(startDate))
                    {
                        startDate = "19000101";  // 使用最小日期
                    }
                    if (string.IsNullOrEmpty(endDate))
                    {
                        endDate = "99991231";    // 使用最大日期
                    }

                    return (startDate, endDate);
                }
            }
            
            // 如果是个日期，开始和结束日期相同
            return (cleanDateValue, cleanDateValue);
        }

        var dateRange = ProcessDateRange(studyDate);

        // 处理 Modality 列表
        string[] ProcessModalities(DicomDataset dataset)
        {
            var modalities = new List<string>();

            // 尝试取 ModalitiesInStudy
            if (dataset.Contains(DicomTag.ModalitiesInStudy))
            {
                try
                {
                    var modalityValues = dataset.GetValues<string>(DicomTag.ModalitiesInStudy);
                    if (modalityValues != null && modalityValues.Length > 0)
                    {
                        modalities.AddRange(modalityValues.Where(m => !string.IsNullOrEmpty(m)));
                    }
                }
                catch (Exception ex)
                {
                    DicomLogger.Warning("QRSCP", ex, "获取 ModalitiesInStudy 失败");
                }
            }

            // 尝试获取单 Modality
            var singleModality = dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
            if (!string.IsNullOrEmpty(singleModality) && !modalities.Contains(singleModality))
            {
                modalities.Add(singleModality);
            }

            return modalities.ToArray();
        }

        var parameters = new StudyQueryParameters(
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty)),
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty)),
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.AccessionNumber, string.Empty)),
            dateRange,
            ProcessModalities(request.Dataset),
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty)));

        return parameters;
    }

    private DicomCFindResponse CreateStudyResponse(DicomCFindRequest request, Study study)
    {
        var response = new DicomCFindResponse(request, DicomStatus.Pending);
        var dataset = new DicomDataset();

        // 获取请求中的字符集，如果没有指定则默认使用 UTF-8
        var requestedCharacterSet = request.Dataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, "ISO_IR 192");

        // 根据请求的字符集设置响应的字符集
        switch (requestedCharacterSet.ToUpperInvariant())
        {
            case "ISO_IR 100":  // Latin1
                dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 100");
                break;
            case "GB18030":     // 中文简体
                dataset.Add(DicomTag.SpecificCharacterSet, "GB18030");
                break;
            case "ISO_IR 192":  // UTF-8
            default:
                dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 192");
                break;
        }

        AddCommonTags(dataset, request.Dataset);

        dataset.Add(DicomTag.StudyInstanceUID, study.StudyInstanceUid)
              .Add(DicomTag.StudyDate, study.StudyDate ?? string.Empty)
              .Add(DicomTag.StudyTime, study.StudyTime ?? string.Empty)
              .Add(DicomTag.PatientName, study.PatientName ?? string.Empty)
              .Add(DicomTag.PatientID, study.PatientId ?? string.Empty)
              .Add(DicomTag.PatientBirthDate, study.PatientBirthDate ?? string.Empty)
              .Add(DicomTag.StudyDescription, study.StudyDescription ?? string.Empty)
              .Add(DicomTag.ModalitiesInStudy, study.Modality ?? string.Empty)
              .Add(DicomTag.AccessionNumber, study.AccessionNumber ?? string.Empty)
              .Add(DicomTag.NumberOfStudyRelatedSeries, study.NumberOfStudyRelatedSeries.ToString())
              .Add(DicomTag.NumberOfStudyRelatedInstances, study.NumberOfStudyRelatedInstances.ToString());

        response.Dataset = dataset;
        return response;
    }

    private async Task<List<DicomCFindResponse>> HandleSeriesLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
        if (string.IsNullOrEmpty(studyInstanceUid))
        {
            DicomLogger.Warning("QRSCP", "Series级别查询缺少StudyInstanceUID");
            return responses;
        }

        // 使用 Task.Run 来异步执行数查询
        var seriesList = await Task.Run(() => _repository.GetSeriesByStudyUid(studyInstanceUid));

        foreach (var series in seriesList)
        {
            var response = new DicomCFindResponse(request, DicomStatus.Pending);
            var dataset = new DicomDataset();

            // 添加字符集和其他用标签
            AddCommonTags(dataset, request.Dataset);

            // 设置必要的字段
            dataset.Add(DicomTag.StudyInstanceUID, series.StudyInstanceUid);
            dataset.Add(DicomTag.SeriesInstanceUID, series.SeriesInstanceUid);
            dataset.Add(DicomTag.Modality, series.Modality ?? string.Empty);
            dataset.Add(DicomTag.SeriesNumber, series.SeriesNumber ?? string.Empty);
            dataset.Add(DicomTag.SeriesDescription, series.SeriesDescription ?? string.Empty);
            dataset.Add(DicomTag.NumberOfSeriesRelatedInstances, series.NumberOfInstances);

            // 复制请求的其他查询字段（如果不存在）
            foreach (var tag in request.Dataset.Select(x => x.Tag))
            {
                if (!dataset.Contains(tag) && request.Dataset.TryGetString(tag, out string value))
                {
                    dataset.Add(tag, value);
                }
            }

            response.Dataset = dataset;
            responses.Add(response);
        }

        DicomLogger.Information("QRSCP", "Series级别查询完成 - 返回记录数: {Count}", responses.Count);
        return responses;
    }

    private async Task<List<DicomCFindResponse>> HandleImageLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
        var seriesInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesInstanceUID, string.Empty);
        
        if (string.IsNullOrEmpty(studyInstanceUid) || string.IsNullOrEmpty(seriesInstanceUid))
        {
            DicomLogger.Warning("QRSCP", "Image级别查询缺少StudyInstanceUID或SeriesInstanceUID");
            return responses;
        }

        // 使 Task.Run 来异步执行数据库查询
        var instances = await Task.Run(() => _repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid));

        foreach (var instance in instances)
        {
            try
            {
                var response = new DicomCFindResponse(request, DicomStatus.Pending);
                var dataset = new DicomDataset();

                // 添加字符集和其他通用标签
                AddCommonTags(dataset, request.Dataset);

                // 验证 UID 格
                var validStudyUid = ValidateUID(studyInstanceUid);
                var validSeriesUid = ValidateUID(seriesInstanceUid);
                var validSopInstanceUid = ValidateUID(instance.SopInstanceUid);
                var validSopClassUid = ValidateUID(instance.SopClassUid);

                // 置必要的字段
                dataset.Add(DicomTag.StudyInstanceUID, validStudyUid);
                dataset.Add(DicomTag.SeriesInstanceUID, validSeriesUid);
                dataset.Add(DicomTag.SOPInstanceUID, validSopInstanceUid);
                dataset.Add(DicomTag.SOPClassUID, validSopClassUid);
                dataset.Add(DicomTag.InstanceNumber, instance.InstanceNumber ?? string.Empty);

                response.Dataset = dataset;
                responses.Add(response);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("QRSCP", ex, "创建Image响应失败 - SOPInstanceUID: {SopInstanceUid}", 
                    instance.SopInstanceUid);
                continue;
            }
        }

        DicomLogger.Information("QRSCP", "Image级别查询完成 - 返回记录数: {Count}", responses.Count);
        return responses;
    }

    private string ValidateUID(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return string.Empty;
        
        try
        {
            // 分割 UID
            var parts = uid.Split('.');
            var validParts = new List<string>();

            foreach (var part in parts)
            {
                // 跳过空组
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                // 移除前导零并确保至少保留一个数字
                var trimmed = part.TrimStart('0');
                validParts.Add(string.IsNullOrEmpty(trimmed) ? "0" : trimmed);
            }

            // 确保少有两个组件
            if (validParts.Count < 2)
            {
                DicomLogger.Warning("QRSCP", "无效的UID格式 (组件数量不足): {Uid}", 
                    uid ?? string.Empty);
                return "0.0";
            }

            return string.Join(".", validParts);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "UID验证失败: {Uid}", 
                uid ?? string.Empty);
            return "0.0";
        }
    }

    private void AddCommonTags(DicomDataset dataset, DicomDataset requestDataset)
    {
        // 让 fo-dicom 处理字符集
        dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, 
            requestDataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, "ISO_IR 192"));
    }

    public async IAsyncEnumerable<DicomCMoveResponse> OnCMoveRequestAsync(DicomCMoveRequest request)
    {
        var destinationAE = request.DestinationAE;

        // 检查目标 AE 是否正在处理中
        if (_activeDestinations.TryGetValue(destinationAE, out var existingCts))
        {
            DicomLogger.Warning("QRSCP", "目标AE有未完成的任务，取消旧任务 - AET: {AET}", destinationAE);
            existingCts.Cancel();
            existingCts.Dispose();
            _activeDestinations.TryRemove(destinationAE, out _);
        }

        // 创建新的取消令牌
        var cts = new CancellationTokenSource();
        _activeDestinations.TryAdd(destinationAE, cts);

        try
        {
            DicomLogger.Information("QRSCP", "收到C-MOVE请求 - AE: {CallingAE}, 目标: {DestinationAE}", 
                Association.CallingAE, request.DestinationAE);

            // 1. 验证目标 AE
            var moveDestination = _settings.QRSCP.MoveDestinations
                .FirstOrDefault(x => x.AeTitle.Equals(request.DestinationAE, StringComparison.OrdinalIgnoreCase));

            if (moveDestination == null)
            {
                DicomLogger.Warning("QRSCP", "目标AE未配置 - AET: {AET}", request.DestinationAE);
                yield return new DicomCMoveResponse(request, DicomStatus.QueryRetrieveMoveDestinationUnknown);
                yield break;
            }

            // 2. 测试目标 SCP 连接
            var client = CreateDicomClient(moveDestination);
            DicomResponse? response = null;
            try 
            {
                var echoRequest = new DicomCEchoRequest();
                await client.AddRequestAsync(echoRequest);
                await client.SendAsync();
                DicomLogger.Information("QRSCP", "目标SCP连接测试成功 - AET: {AET}", request.DestinationAE);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("QRSCP", ex, "目标SCP连接测试失败 - AET: {AET}", request.DestinationAE);
                response = new DicomCMoveResponse(request, DicomStatus.QueryRetrieveMoveDestinationUnknown);
            }

            if (response != null)
            {
                yield return (DicomCMoveResponse)response;
                yield break;
            }

            // 3. 获取实例列表
            var instances = await GetRequestedInstances(request);
            if (!instances.Any())
            {
                DicomLogger.Information("QRSCP", "未找到匹配实例");
                yield return new DicomCMoveResponse(request, DicomStatus.Success);
                yield break;
            }

            // 4. 处理实例传输并返回进度
            var totalInstances = instances.Count();
            var result = await ProcessInstances(request, instances, client, cts.Token);

            // 5. 返回进度和最终状态
            yield return new DicomCMoveResponse(request, DicomStatus.Pending)
            {
                Dataset = CreateProgressResponse(
                    request,
                    totalInstances,
                    result.successCount,
                    result.failedCount).Dataset
            };

            var finalStatus = result.hasNetworkError ? DicomStatus.QueryRetrieveMoveDestinationUnknown :
                             result.failedCount > 0 ? DicomStatus.ProcessingFailure :
                             DicomStatus.Success;

            yield return new DicomCMoveResponse(request, finalStatus)
            {
                Dataset = CreateProgressResponse(
                    request,
                    totalInstances,
                    result.successCount,
                    result.failedCount,
                    finalStatus).Dataset
            };

            // 添加完成日志
            DicomLogger.Information("QRSCP", 
                "C-MOVE完成 - 总数: {Total}, 成功: {Success}, 失败: {Failed}, 状态: {Status}", 
                totalInstances, 
                result.successCount, 
                result.failedCount, 
                finalStatus);
        }
        finally
        {
            // 清理资源
            if (_activeDestinations.TryRemove(destinationAE, out var currentCts) && currentCts == cts)
            {
                cts.Dispose();
            }
        }
    }

    private async Task<(int successCount, int failedCount, bool hasNetworkError)> SendBatch(
        IDicomClient client, 
        List<DicomFile> files, 
        int currentSuccess, 
        int currentFailed)
    {
        try
        {
            // 批量转码
            var requestedTransferSyntax = GetRequestedTransferSyntax(files[0]);
            if (requestedTransferSyntax != null)
            {
                files = TranscodeFilesIfNeeded(files, requestedTransferSyntax);
            }

            await SendToDestinationAsync(client, files);
            return (currentSuccess, currentFailed, false);
        }
        catch (DicomNetworkException ex)
        {
            DicomLogger.Error("QRSCP", ex, "网络错误，停止发送");
            return (currentSuccess, currentFailed + files.Count, true);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "发送失败");
            return (currentSuccess, currentFailed + files.Count, false);
        }
    }

    private DicomResponse CreateProgressResponse(
        DicomRequest request, 
        int totalInstances, 
        int successCount, 
        int failedCount,
        DicomStatus? status = null)
    {
        var remaining = totalInstances - (successCount + failedCount);
        var currentStatus = status ?? (remaining > 0 ? DicomStatus.Pending : 
            failedCount > 0 ? DicomStatus.ProcessingFailure : DicomStatus.Success);

        var dataset = new DicomDataset()
            .Add(DicomTag.NumberOfRemainingSuboperations, (ushort)remaining)
            .Add(DicomTag.NumberOfCompletedSuboperations, (ushort)successCount)
            .Add(DicomTag.NumberOfFailedSuboperations, (ushort)failedCount);

        return request switch
        {
            DicomCGetRequest getRequest => new DicomCGetResponse(getRequest, currentStatus) { Dataset = dataset },
            DicomCMoveRequest moveRequest => new DicomCMoveResponse(moveRequest, currentStatus) { Dataset = dataset },
            _ => throw new ArgumentException("Unsupported request type", nameof(request))
        };
    }

    private IDicomClient CreateDicomClient(MoveDestination destination)
    {
        var client = DicomClientFactory.Create(
            destination.HostName,
            destination.Port,
            false,
            _settings.QRSCP.AeTitle,
            destination.AeTitle);

        client.NegotiateAsyncOps();

        // 添加所有可能的存储类 PresentationContexts
        var storageUids = new DicomUID[]
        {
            DicomUID.CTImageStorage,
            DicomUID.MRImageStorage,
            DicomUID.UltrasoundImageStorage,
            DicomUID.SecondaryCaptureImageStorage,
            DicomUID.XRayAngiographicImageStorage,
            DicomUID.XRayRadiofluoroscopicImageStorage,
            DicomUID.DigitalXRayImageStorageForPresentation,
            DicomUID.DigitalMammographyXRayImageStorageForPresentation,
            DicomUID.UltrasoundMultiFrameImageStorage,
            DicomUID.EnhancedCTImageStorage,
            DicomUID.EnhancedMRImageStorage,
            DicomUID.EnhancedXAImageStorage,
            DicomUID.NuclearMedicineImageStorage,
            DicomUID.PositronEmissionTomographyImageStorage
        };

        byte pcid = 1;
        foreach (var uid in storageUids)
        {
            var pc = new DicomPresentationContext(pcid++, uid);
            pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
            client.AdditionalPresentationContexts.Add(pc);
        }

        return client;
    }

    public async IAsyncEnumerable<DicomCGetResponse> OnCGetRequestAsync(DicomCGetRequest request)
    {
        DicomLogger.Information("QRSCP", "收到C-GET请求 - AE: {CallingAE}, 级别: {Level}", 
            Association.CallingAE, request.Level);

        // 1. 验证请求参数
        var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
        if (string.IsNullOrEmpty(studyInstanceUid))
        {
            DicomLogger.Warning("QRSCP", "C-GET缺少StudyInstanceUID");
            yield return new DicomCGetResponse(request, DicomStatus.InvalidAttributeValue);
            yield break;
        }

        // 2. 获取实例列表
        var instances = await GetRequestedInstances(request);
        if (!instances.Any())
        {
            DicomLogger.Information("QRSCP", "未找到匹配实例");
            yield return new DicomCGetResponse(request, DicomStatus.Success);
            yield break;
        }

        // 3. 处理实例传输
        var totalInstances = instances.Count();
        var result = await ProcessInstances(request, instances);
        
        // 4. 只返回最终状态
        var finalStatus = result.hasNetworkError ? DicomStatus.ProcessingFailure :
                         result.failedCount > 0 ? DicomStatus.ProcessingFailure :
                         DicomStatus.Success;

        yield return new DicomCGetResponse(request, finalStatus)
        {
            Dataset = CreateProgressResponse(
                request,
                totalInstances,
                result.successCount,
                result.failedCount,
                finalStatus).Dataset
        };
    }

    private async Task<IEnumerable<Instance>> GetRequestedInstances(DicomRequest request)
    {
        try
        {
            var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
            var seriesInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesInstanceUID, string.Empty);
            var sopInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SOPInstanceUID, string.Empty);

            DicomLogger.Information("QRSCP", 
                "获取实例列表 - Study: {StudyUid}, Series: {SeriesUid}, SOP: {SopUid}",
                studyInstanceUid, seriesInstanceUid, sopInstanceUid);

            if (!string.IsNullOrEmpty(sopInstanceUid))
            {
                var instance = await Task.Run(() => _repository.GetInstanceAsync(sopInstanceUid));
                return instance != null ? new[] { instance } : Array.Empty<Instance>();
            }
            
            if (!string.IsNullOrEmpty(seriesInstanceUid))
            {
                return await Task.Run(() => _repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid));
            }
            
            if (!string.IsNullOrEmpty(studyInstanceUid))
            {
                return await Task.Run(() => _repository.GetInstancesByStudyUid(studyInstanceUid));
            }

            DicomLogger.Warning("QRSCP", "请求缺少必要的UID");
            return Array.Empty<Instance>();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "获取实例列表失败");
            return Array.Empty<Instance>();
        }
    }

    private async Task<List<DicomCFindResponse>> HandlePatientLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        try
        {
            // 从请求中获取查询参数
            var patientId = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty);
            var patientName = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty);

            DicomLogger.Information("QRSCP", 
                "Patient级查询 - ID: {PatientId}, Name: {PatientName}", 
                patientId, patientName);

            // 从数据库查询患者数据
            var patients = await Task.Run(() => _repository.GetPatients(patientId, patientName));

            // 构建响应
            foreach (var patient in patients)
            {
                var response = new DicomCFindResponse(request, DicomStatus.Pending);
                var dataset = new DicomDataset();

                AddCommonTags(dataset, request.Dataset);

                dataset.Add(DicomTag.PatientID, patient.PatientId ?? string.Empty)
                      .Add(DicomTag.PatientName, patient.PatientName ?? string.Empty)
                      .Add(DicomTag.PatientBirthDate, patient.PatientBirthDate ?? string.Empty)
                      .Add(DicomTag.PatientSex, patient.PatientSex ?? string.Empty)
                      .Add(DicomTag.NumberOfPatientRelatedStudies, patient.NumberOfStudies.ToString())
                      .Add(DicomTag.NumberOfPatientRelatedSeries, patient.NumberOfSeries.ToString())
                      .Add(DicomTag.NumberOfPatientRelatedInstances, patient.NumberOfInstances.ToString());

                response.Dataset = dataset;
                responses.Add(response);
            }

            DicomLogger.Information("QRSCP", "Patient级查询完成 - 返回记录数: {Count}", responses.Count);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Patient级查询失败");
            responses.Add(new DicomCFindResponse(request, DicomStatus.ProcessingFailure));
        }

        return responses;
    }

    private bool IsNetworkError(Exception ex)
    {
        if (ex == null) return false;

        // 检查是否是网络相关的异常
        return ex is System.Net.Sockets.SocketException ||
               ex is System.Net.WebException ||
               ex is System.IO.IOException ||
               ex is DicomNetworkException ||
               (ex.InnerException != null && IsNetworkError(ex.InnerException));
    }

    private DicomTransferSyntax? GetRequestedTransferSyntax(DicomFile file)
    {
        try
        {
            // 1. 从 C-MOVE/C-GET 服务的 Presentation Context 中获取传输语法
            var moveContext = Association.PresentationContexts
                .FirstOrDefault(pc => pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove ||
                                   pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove);

            var moveTransferSyntax = moveContext?.AcceptedTransferSyntax;
            if (moveTransferSyntax != null)
            {
                if (!_transferSyntaxLogged)
                {
                    DicomLogger.Information("QRSCP", 
                        "传输语法检查 - 本地文件: {SourceSyntax}, 请求格式: {TargetSyntax}", 
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        moveTransferSyntax.UID.Name);
                    _transferSyntaxLogged = true;
                }

                return moveTransferSyntax;
            }

            // 2. 获取当前文件的 SOP Class UID 对应的传输语法
            var sopClassUid = file.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID);
            var storageContext = Association.PresentationContexts
                .FirstOrDefault(pc => pc.AbstractSyntax == sopClassUid);

            var storageTransferSyntax = storageContext?.AcceptedTransferSyntax;
            if (storageTransferSyntax != null)
            {
                if (!_transferSyntaxLogged)
                {
                    DicomLogger.Information("QRSCP", 
                        "存储类传输语法检查 - 本地文件: {SourceSyntax}, 请求格式: {TargetSyntax}", 
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        storageTransferSyntax.UID.Name);
                    _transferSyntaxLogged = true;
                }

                return storageTransferSyntax;
            }

            // 3. 如果都没有指定传输语法，使用默认的传输语法
            DicomLogger.Information("QRSCP", "未指定传输语法，使用默认传输语法: Explicit VR Little Endian");
            return DicomTransferSyntax.ExplicitVRLittleEndian;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "获取请求的传输语法失败，使用默认传输语法: Explicit VR Little Endian");
            return DicomTransferSyntax.ExplicitVRLittleEndian;
        }
    }

    // 实现 IDicomCStoreProvider 接口
    public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        DicomLogger.Warning("QRSCP", "不支持直接存储服务 - AET: {CallingAE}, SOPInstanceUID: {SopInstanceUid}", 
            Association?.CallingAE ?? "Unknown",
            request.SOPInstanceUID?.ToString() ?? string.Empty);

        // 返回不支持的状态
        return Task.FromResult(new DicomCStoreResponse(request, DicomStatus.SOPClassNotSupported));
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        DicomLogger.Warning("QRSCP", e, "存储请求异常 - 临时文件: {TempFile}", 
            tempFileName ?? string.Empty);
        return Task.CompletedTask;
    }

    private List<DicomFile> TranscodeFilesIfNeeded(List<DicomFile> files, DicomTransferSyntax targetSyntax)
    {
        // 1. 快速检查是否需要转码
        if (files.All(f => f.Dataset.InternalTransferSyntax == targetSyntax))
        {
            return files;
        }

        var transcoder = new DicomTranscoder(DicomTransferSyntax.ExplicitVRLittleEndian, targetSyntax);
        var results = new ConcurrentBag<DicomFile>();  // 使用线程安全的集合

        // 2. 并行转码
        Parallel.ForEach(files, 
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            file => 
        {
            try
            {
                if (file.Dataset.InternalTransferSyntax != targetSyntax)
                {
                    var transcodedFile = transcoder.Transcode(file);
                    results.Add(transcodedFile);
                }
                else
                {
                    results.Add(file);
                }
            }
            catch (Exception ex)
            {
                DicomLogger.Warning("QRSCP", ex, "语法转换失败，使用原格式");
                results.Add(file);
            }
        });

        return results.ToList();
    }

    private const int BatchSize = 10;  // 固定批量大小

    private async Task<(int successCount, int failedCount, bool hasNetworkError)> ProcessInstances(
        DicomRequest request,
        IEnumerable<Instance> instances,
        IDicomClient? client = null,
        CancellationToken cancellationToken = default)
    {
        var totalInstances = instances.Count();
        var successCount = 0;
        var failedCount = 0;
        var hasNetworkError = false;

        foreach (var seriesGroup in instances.GroupBy(x => x.SeriesInstanceUid))
        {
            // 检查是否已取消
            if (cancellationToken.IsCancellationRequested)
            {
                DicomLogger.Warning("QRSCP", "任务已取消，停止处理");
                break;
            }

            var batchFiles = new List<DicomFile>();
            try
            {
                foreach (var instance in seriesGroup)
                {
                    // 如果已经发生网络错误或取消，立即停止处理
                    if (hasNetworkError || cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                        if (!File.Exists(filePath))
                        {
                            failedCount++;
                            continue;
                        }

                        var file = await DicomFile.OpenAsync(filePath);
                        batchFiles.Add(file);

                        // 达到批次大小时处理
                        if (batchFiles.Count >= BatchSize)
                        {
                            // 检查是否已取消
                            if (cancellationToken.IsCancellationRequested)
                            {
                                break;
                            }

                            // 发送批次
                            var result = client != null 
                                ? await SendBatch(client, batchFiles, successCount, failedCount)
                                : await SendLocalBatch(request, batchFiles, successCount, failedCount);

                            if (result.hasNetworkError)
                            {
                                hasNetworkError = true;
                                failedCount = result.failedCount;
                                break;
                            }

                            successCount += batchFiles.Count;
                            failedCount = result.failedCount;
                            batchFiles.Clear();
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        DicomLogger.Error("QRSCP", ex, "处理失败 - UID: {SopInstanceUid}", instance.SopInstanceUid);
                    }
                }

                // 如果已经发生网络错误或取消，不处理剩余文件
                if (!hasNetworkError && !cancellationToken.IsCancellationRequested && batchFiles.Any())
                {
                    // 发送批次
                    var result = client != null 
                        ? await SendBatch(client, batchFiles, successCount, failedCount)
                        : await SendLocalBatch(request, batchFiles, successCount, failedCount);

                    if (result.hasNetworkError)
                    {
                        hasNetworkError = true;
                        failedCount = result.failedCount;
                    }
                    else
                    {
                        successCount += batchFiles.Count;
                        failedCount = result.failedCount;
                    }
                }
            }
            finally
            {
                batchFiles.Clear();
            }

            // 如果发生网络错误或取消，立即停止处理下一个序列
            if (hasNetworkError || cancellationToken.IsCancellationRequested)
            {
                DicomLogger.Warning("QRSCP", 
                    cancellationToken.IsCancellationRequested ? "任务已取消，停止处理" : "发现网络错误，停止处理");
                break;
            }
        }

        return (successCount, failedCount, hasNetworkError);
    }

    // C-GET 本地发送
    private async Task<(int successCount, int failedCount, bool hasNetworkError)> SendLocalBatch(
        DicomRequest request,
        List<DicomFile> files,
        int currentSuccess,
        int currentFailed)
    {
        try
        {
            // 1. 批量转码
            var requestedTransferSyntax = GetRequestedTransferSyntax(files[0]);
            if (requestedTransferSyntax != null)
            {
                files = TranscodeFilesIfNeeded(files, requestedTransferSyntax);
            }

            // 2. 逐个发送
            foreach (var file in files)
            {
                await SendRequestAsync(new DicomCStoreRequest(file));  // 使用基类的 SendRequestAsync
            }
            return (currentSuccess + files.Count, currentFailed, false);
        }
        catch (Exception ex)
        {
            if (IsNetworkError(ex))
            {
                DicomLogger.Error("QRSCP", ex, "网络错误，停止发送");
                return (currentSuccess, currentFailed + files.Count, true);
            }
            
            DicomLogger.Error("QRSCP", ex, "发送失败");
            return (currentSuccess, currentFailed + files.Count, false);
        }
    }

    private async Task SendToDestinationAsync(IDicomClient client, List<DicomFile> files)
    {
        // 1. 按 SOP Class 分组添加 PresentationContext
        var sopClassGroups = files.GroupBy(f => f.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID));
        foreach (var group in sopClassGroups)
        {
            var presentationContext = new DicomPresentationContext(
                (byte)client.AdditionalPresentationContexts.Count(),
                group.Key);
            presentationContext.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
            client.AdditionalPresentationContexts.Add(presentationContext);
        }

        // 2. 批量添加请求
        foreach (var file in files)
        {
            await client.AddRequestAsync(new DicomCStoreRequest(file));
        }

        // 3. 一次性发送所有请求
        await client.SendAsync();
    }
}


