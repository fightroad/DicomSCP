using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using DicomSCP.Repository;
using DicomSCP.Models;
using DicomSCP.Configuration;
using Microsoft.Extensions.Options;
using TinyPinyin;
using System.Globalization;

namespace DicomSCP.Services;

public record WorklistQueryParameters(
    string PatientId,
    string PatientName,
    string AccessionNumber,
    (string StartDate, string EndDate) DateRange,
    string Modality,
    string ScheduledStationName);

public class WorklistSCP : DicomService, IDicomServiceProvider, IDicomCFindProvider, IDicomCEchoProvider
{
    private static DicomSettings? _settings;
    private static DicomRepository? _repository;

    public static void Configure(
        DicomSettings settings,
        DicomRepository repository)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public WorklistSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        ILogger log, 
        DicomServiceDependencies dependencies,
        IOptions<DicomSettings> settings)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        if (settings?.Value == null || dependencies?.LoggerFactory == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        _settings = settings.Value;
        DicomLogger.Information("服务初始化完成");
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            DicomLogger.Error("WorklistSCP", exception, "连接异常关闭");
        }
        else
        {
            DicomLogger.Debug("WorklistSCP", "连接正常关闭");
        }
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        var calledAE = association.CalledAE;
        var expectedAE = _settings?.WorklistSCP.AeTitle ?? string.Empty;

        if (!string.Equals(expectedAE, calledAE, StringComparison.OrdinalIgnoreCase))
        {
            DicomLogger.Warning("WorklistSCP", "拒绝错误的 Called AE: {CalledAE}, 期望: {ExpectedAE}", 
                calledAE, expectedAE);
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.CalledAENotRecognized);
        }

        if (string.IsNullOrEmpty(association.CallingAE))
        {
            DicomLogger.Warning("WorklistSCP", "拒绝空的 Calling AE");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.CallingAENotRecognized);
        }

        if (_settings?.WorklistSCP.ValidateCallingAE == true)
        {
            if (!_settings.WorklistSCP.AllowedCallingAEs.Contains(association.CallingAE, StringComparer.OrdinalIgnoreCase))
            {
                DicomLogger.Warning("WorklistSCP", "拒绝未授权的调用方AE: {CallingAE}", association.CallingAE);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CallingAENotRecognized);
            }
        }

        DicomLogger.Debug("WorklistSCP", "验证通过 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
            calledAE, association.CallingAE);

        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind ||
                pc.AbstractSyntax == DicomUID.Verification)
            {
                pc.AcceptTransferSyntaxes(
                    DicomTransferSyntax.ImplicitVRLittleEndian,
                    DicomTransferSyntax.ExplicitVRLittleEndian,
                    DicomTransferSyntax.ExplicitVRBigEndian);
                DicomLogger.Debug("WorklistSCP", "接受服务 - AET: {CallingAE}, 服务: {Service}", 
                    association.CallingAE, pc.AbstractSyntax.Name);
            }
            else
            {
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                DicomLogger.Warning("WorklistSCP", "拒绝不支持的服务 - AET: {CallingAE}, AbstractSyntax: {AbstractSyntax}", 
                    association.CallingAE, pc.AbstractSyntax);
            }
        }

        return SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        DicomLogger.Debug("WorklistSCP", "接收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("WorklistSCP", "接收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        if (_settings == null)
        {
            DicomLogger.Error("WorklistSCP", null, "服务未配置");
            yield return new DicomCFindResponse(request, DicomStatus.ProcessingFailure);
            yield break;
        }

        DicomLogger.Debug("WorklistSCP", "收到工作列表查询请求 - 数据集结构:\n{DatasetStructure}",
            FormatDatasetStructure(request.Dataset));

        var responses = await Task.Run(() => ProcessWorklistQuery(request));
        foreach (var response in responses)
        {
            yield return response;
        }
    }

    private List<DicomCFindResponse> ProcessWorklistQuery(DicomCFindRequest request)
    {
        List<WorklistItem> worklistItems;
        try
        {
            var parameters = ExtractQueryParameters(request);

            worklistItems = _repository?.GetWorklistItems(
                parameters.PatientId,
                parameters.PatientName,
                parameters.AccessionNumber,
                parameters.DateRange,
                parameters.Modality,
                parameters.ScheduledStationName) ?? new List<WorklistItem>();

            DicomLogger.Information("WorklistSCP", "查询到工作列表项: {Count} 条记录", worklistItems.Count);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("WorklistSCP", ex, "工作列表查询失败: {Message}", ex.Message);
            return [new DicomCFindResponse(request, DicomStatus.ProcessingFailure)];
        }

        if (worklistItems.Count == 0)
        {
            DicomLogger.Debug("WorklistSCP", "未找到匹配的工作列表项");
            return [new DicomCFindResponse(request, DicomStatus.Success)];
        }

        var responses = new List<DicomCFindResponse>();
        var hasErrors = false;

        foreach (var item in worklistItems)
        {
            try
            {
                var response = CreateWorklistResponse(request, item);
                responses.Add(response);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WorklistSCP", ex, "创建响应失败 - PatientId: {PatientId}", item.PatientId);
                hasErrors = true;
            }
        }

        if (responses.Count == 0 && hasErrors)
        {
            DicomLogger.Error("WorklistSCP", null, "所有响应创建都失败");
            return [new DicomCFindResponse(request, DicomStatus.ProcessingFailure)];
        }

        DicomLogger.Information("WorklistSCP", "工作列表查询完成 - 返回记录数: {Count}, 是否有错误: {HasErrors}", 
            responses.Count, hasErrors);
        responses.Add(new DicomCFindResponse(request, DicomStatus.Success));
        return responses;
    }

    private DicomCFindResponse CreateWorklistResponse(DicomCFindRequest request, WorklistItem item)
    {
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        try
        {
            var dataset = new DicomDataset();
            
            // 从请求中获取字符集
            var requestedCharacterSet = "ISO_IR 100";  // 默认值
            if (request.Dataset.Contains(DicomTag.SpecificCharacterSet))
            {
                var charsets = request.Dataset.GetValues<string>(DicomTag.SpecificCharacterSet);
                if (charsets != null && charsets.Length > 0)
                {
                    requestedCharacterSet = charsets[0];
                }
            }

            DicomLogger.Debug("WorklistSCP", "请求的字符集: {CharacterSet}", requestedCharacterSet);
            
            // 判断是否需要转换中文名
            bool needConvertName = true;
            string patientName = item.PatientName;

            // 根据请求的字符集设置响应的字符集
            switch (requestedCharacterSet.ToUpperInvariant())
            {
                case "ISO_IR 100":  // Latin1
                    dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 100");
                    needConvertName = true;  // Latin1 不支持中文，需要转换
                    break;
                case "GB18030":     // 中文简体
                case "GBK":         // 中文简体
                case "GB2312":      // 中文简体
                    dataset.Add(DicomTag.SpecificCharacterSet, "GB18030");
                    needConvertName = false;  // GB18030 支持中文，不需要转换
                    break;
                case "ISO_IR 192":  // UTF-8
                    dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 192");
                    needConvertName = false;  // UTF-8 支持中文，不需要转换
                    break;
                default:            // 其他未知字符集，使用 Latin1 作为安全选项
                    dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 100");
                    needConvertName = true;  // 使用拼音
                    DicomLogger.Warning("WorklistSCP", "未知的字符集: {CharacterSet}, 使用 Latin1", requestedCharacterSet);
                    break;
            }

            // 根据字符集决定是否需要转换中文名
            if (needConvertName)
            {
                patientName = ConvertToDeviceName(item.PatientName);
                DicomLogger.Debug("WorklistSCP", 
                    "转换患者姓名 - 原始名: {OriginalName}, 转换后: {ConvertedName}, 字符集: {CharacterSet}", 
                    item.PatientName, 
                    patientName,
                    requestedCharacterSet);
            }
            else
            {
                DicomLogger.Debug("WorklistSCP", 
                    "使用原始中文名 - 患者姓名: {PatientName}, 字符集: {CharacterSet}",
                    patientName,
                    requestedCharacterSet);
            }
            
            // 患者信息
            dataset.Add(DicomTag.PatientID, ProcessDicomValue(item.PatientId, DicomTag.PatientID, needConvertName));
            dataset.Add(DicomTag.PatientName, ProcessDicomValue(patientName, DicomTag.PatientName, needConvertName));
            dataset.Add(DicomTag.PatientSex, ProcessDicomValue(item.PatientSex ?? "", DicomTag.PatientSex, needConvertName));

            // 确保出生日期格式正确
            try
            {
                if (!string.IsNullOrEmpty(item.PatientBirthDate))
                {
                    var birthDate = DateTime.ParseExact(item.PatientBirthDate, "yyyyMMdd", null);
                    dataset.Add(DicomTag.PatientBirthDate, birthDate.ToString("yyyyMMdd"));
                }
                else
                {
                    dataset.Add(DicomTag.PatientBirthDate, "19000101");  // 使用默认值
                }
            }
            catch (Exception ex)
            {
                DicomLogger.Warning("WorklistSCP", 
                    "处理出生日期失败 - PatientId: {PatientId}, BirthDate: {BirthDate}, Error: {Error}", 
                    item.PatientId ?? "", 
                    item.PatientBirthDate ?? "", 
                    ex.Message);
                dataset.Add(DicomTag.PatientBirthDate, "19000101");  // 使用默认值
            }

            // 添加年龄信息
            if (!string.IsNullOrEmpty(item.PatientBirthDate))
            {
                try
                {
                    var birthDate = DateTime.ParseExact(item.PatientBirthDate, "yyyyMMdd", null);
                    var age = DateTime.Now.Year - birthDate.Year;
                    if (DateTime.Now.DayOfYear < birthDate.DayOfYear)
                    {
                        age--;
                    }
                    dataset.Add(DicomTag.PatientAge, $"{age:000}Y");  // 格式化为 "045Y" 这样的格式
                }
                catch (Exception ex)
                {
                    DicomLogger.Warning("WorklistSCP", 
                        "计算年龄失败 - PatientId: {PatientId}, BirthDate: {BirthDate}, Error: {Error}", 
                        item.PatientId ?? "", 
                        item.PatientBirthDate ?? "", 
                        ex.Message);
                }
            }

            // 研究信息
            dataset.Add(DicomTag.StudyInstanceUID, ProcessDicomValue(item.StudyInstanceUid, DicomTag.StudyInstanceUID, needConvertName));
            dataset.Add(DicomTag.AccessionNumber, ProcessDicomValue(item.AccessionNumber, DicomTag.AccessionNumber, needConvertName));
            // 医生姓名也需要根据字符集处理
            var physicianName = needConvertName ?
                ConvertToDeviceName(item.ReferringPhysicianName) : 
                item.ReferringPhysicianName;
            dataset.Add(DicomTag.ReferringPhysicianName, ProcessDicomValue(physicianName, DicomTag.ReferringPhysicianName, needConvertName));

            // 预约信息（MWL标准结构：Scheduled Procedure Step Sequence）
            string spsStartDate;
            string spsStartTime;
            try
            {
                if (!string.IsNullOrEmpty(item.ScheduledDateTime))
                {
                    DateTime scheduledDateTime;
                    string dateStr = item.ScheduledDateTime.Trim();
                    string numericOnly = new(dateStr.Where(char.IsDigit).ToArray());

                    if (numericOnly.Length >= 8)
                    {
                        string formattedDate;
                        if (numericOnly.Length >= 12)
                        {
                            formattedDate = numericOnly.Substring(0, 8) +
                                            (numericOnly.Length >= 12 ? numericOnly.Substring(8, 4) : "0000") +
                                            (numericOnly.Length >= 14 ? numericOnly.Substring(12, 2) : "00");
                        }
                        else
                        {
                            formattedDate = string.Concat(numericOnly.AsSpan(0, 8), "000000");
                        }

                        if (DateTime.TryParseExact(formattedDate,
                            "yyyyMMddHHmmss",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out scheduledDateTime))
                        {
                            spsStartDate = scheduledDateTime.ToString("yyyyMMdd");
                            spsStartTime = scheduledDateTime.ToString("HHmmss");
                            DicomLogger.Debug("WorklistSCP",
                                "预约时间处理成功 - 原始值: {Original}, 格式化值: {Formatted}, 转换后日期: {Date}, 时间: {Time}",
                                item.ScheduledDateTime, formattedDate, spsStartDate, spsStartTime);
                        }
                        else
                        {
                            throw new FormatException($"无法解析日期时间格式: {formattedDate}");
                        }
                    }
                    else
                    {
                        throw new FormatException($"日期时间字符串长度不足: {numericOnly}");
                    }
                }
                else
                {
                    var now = DateTime.Now;
                    spsStartDate = now.ToString("yyyyMMdd");
                    spsStartTime = now.ToString("HHmmss");
                }
            }
            catch (Exception ex)
            {
                DicomLogger.Warning("WorklistSCP",
                    "处理预约时间失败 - PatientId: {PatientId}, DateTime: {DateTime}, Error: {Error}",
                    item.PatientId ?? "", item.ScheduledDateTime ?? "", ex.Message);
                var now = DateTime.Now;
                spsStartDate = now.ToString("yyyyMMdd");
                spsStartTime = now.ToString("HHmmss");
            }

            var scheduledStep = new DicomDataset
            {
                { DicomTag.ScheduledStationAETitle, ProcessDicomValue(item.ScheduledAET, DicomTag.ScheduledStationAETitle, needConvertName) },
                { DicomTag.ScheduledProcedureStepStartDate, spsStartDate },
                { DicomTag.ScheduledProcedureStepStartTime, spsStartTime },
                { DicomTag.Modality, ProcessDicomValue(item.Modality, DicomTag.Modality, needConvertName) },
                { DicomTag.ScheduledPerformingPhysicianName, ProcessDicomValue(physicianName, DicomTag.ScheduledPerformingPhysicianName, needConvertName) },
                { DicomTag.ScheduledProcedureStepDescription, ProcessDicomValue(item.ScheduledProcedureStepDescription, DicomTag.ScheduledProcedureStepDescription, needConvertName) },
                { DicomTag.ScheduledProcedureStepID, ProcessDicomValue(item.AccessionNumber, DicomTag.ScheduledProcedureStepID, needConvertName) },
                { DicomTag.ScheduledStationName, ProcessDicomValue(item.ScheduledStationName, DicomTag.ScheduledStationName, needConvertName) },
                { DicomTag.ScheduledProcedureStepLocation, ProcessDicomValue(string.Empty, DicomTag.ScheduledProcedureStepLocation, needConvertName) }
            };

            dataset.Add(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, scheduledStep));
            dataset.Add(DicomTag.RequestedProcedureID, ProcessDicomValue(item.AccessionNumber, DicomTag.RequestedProcedureID, needConvertName));
            dataset.Add(DicomTag.RequestedProcedureDescription, ProcessDicomValue(item.BodyPartExamined, DicomTag.RequestedProcedureDescription, needConvertName));

            var response = new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = dataset };
            DicomLogger.Debug("WorklistSCP", 
                "成功创建响应 - PatientId: {PatientId}, AccessionNumber: {AccessionNumber}", 
                item.PatientId ?? "", 
                item.AccessionNumber ?? "");
            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("WorklistSCP", ex, "创建响应数据集失败 - PatientId: {PatientId}", item.PatientId);
            throw new DicomNetworkException("Failed to create worklist response", ex);
        }
    }

    private WorklistQueryParameters ExtractQueryParameters(DicomCFindRequest request)
    {
        // 记录原始请求参数
        DicomLogger.Debug("WorklistSCP", "接收到查询请求(过滤私有标签) - 数据集结构:\n{DatasetStructure}",
            FormatDatasetStructure(request.Dataset, ignorePrivateTags: true));

        var modality = GetModality(request.Dataset);
        var dateRange = GetDateRange(request.Dataset);
        
        // 获取患者姓名
        var patientName = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty);
        
        var parameters = new WorklistQueryParameters(
            request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty),
            patientName,
            request.Dataset.GetSingleValueOrDefault<string>(DicomTag.AccessionNumber, string.Empty),
            dateRange,
            modality,
            request.Dataset.GetSingleValueOrDefault<string>(DicomTag.ScheduledStationName, string.Empty)
        );

        DicomLogger.Debug("WorklistSCP", "解析后的查询参数 - PatientId: {PatientId}, PatientName: {PatientName}, " +
            "AccessionNumber: {AccessionNumber}, 日期范围: {StartDate} - {EndDate}, Modality: {Modality}, StationName: {StationName}",
            parameters.PatientId,
            parameters.PatientName,
            parameters.AccessionNumber,
            parameters.DateRange.StartDate,
            parameters.DateRange.EndDate,
            parameters.Modality,
            parameters.ScheduledStationName);

        return parameters;
    }

    private string GetModality(DicomDataset dataset)
    {
        // 首先尝试从 ScheduledProcedureStep Sequence 获取
        var modality = string.Empty;
        if (dataset.Contains(DicomTag.ScheduledProcedureStepSequence))
        {
            var stepSequence = dataset.GetSequence(DicomTag.ScheduledProcedureStepSequence);
            if (stepSequence.Items.Any())
            {
                modality = stepSequence.Items[0].GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
                if (!string.IsNullOrEmpty(modality))
                {
                    return modality;
                }
            }
        }

        // 如果没有找到，试从根级别获取
        modality = dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
        DicomLogger.Debug("WorklistSCP", "从根级别获取到 Modality: {Modality}", modality);
        return modality;
    }

    private static (string StartDate, string EndDate) GetDateRange(DicomDataset dataset)
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        string startDate, endDate;

        if (dataset.Contains(DicomTag.ScheduledProcedureStepStartDate))
        {
            var dateElement = dataset.GetDicomItem<DicomElement>(DicomTag.ScheduledProcedureStepStartDate);
            var values = dateElement?.Get<string[]>() ?? Array.Empty<string>();

            if (values.Length > 0)
            {
                var validDates = values
                    .Where(v => !string.IsNullOrEmpty(v) && v.Length == 8)
                    .ToList();

                if (validDates.Count != 0)
                {
                    startDate = validDates.Min() ?? today;
                    endDate = today;
                    var validDatesStr = string.Join(",", validDates);
                    DicomLogger.Debug("WorklistSCP", "日期处理: 有效日期={ValidDates}, 选择的日期范围: {StartDate} - {EndDate}", 
                        validDatesStr, startDate, endDate);
                }
                else
                {
                    // 如果传了日期但无效，使用今天的日期范围
                    startDate = today;
                    endDate = today;
                    DicomLogger.Debug("WorklistSCP", "日期处理: 无效日期, 使用今天: {Today}", today);
                }
            }
            else
            {
                // 传了空的日期值，使用今天的日期范围
                startDate = today;
                endDate = today;
                DicomLogger.Debug("WorklistSCP", "日期处理: 空日期值, 使用今天: {Today}", today);
            }
        }
        else
        {
            // 没有传日期参数，使用过去30天到未来30天的范围
            startDate = DateTime.Now.AddDays(-30).ToString("yyyyMMdd");
            endDate = DateTime.Now.AddDays(30).ToString("yyyyMMdd");
            DicomLogger.Debug("WorklistSCP", "日期处理: 未传日期, 使用默认范围: {StartDate} - {EndDate}", 
                startDate, endDate);
        }

        var dateRange = (StartDate: startDate, EndDate: endDate);
        return dateRange;
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Debug("WorklistSCP", "收到 C-ECHO 请求");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    private static string ConvertToDeviceName(string chineseName)
    {
        try
        {
            if (string.IsNullOrEmpty(chineseName))
                return "";

            var result = new StringBuilder();
            var isFirst = true;

            foreach (var c in chineseName)
            {
                if (PinyinHelper.IsChinese(c))
                {
                    var pinyin = PinyinHelper.GetPinyin(c);
                    
                    // 首字大写
                    if (isFirst)
                    {
                        result.Append(char.ToUpper(pinyin[0]) + pinyin.Substring(1).ToLower());
                        isFirst = false;
                    }
                    else
                    {
                        result.Append(pinyin.ToLower());
                    }
                    result.Append(' ');
                }
                else
                {
                    // 如果是非文字符，直接添加
                    result.Append(c);
                }
            }

            // 移除最后一个分隔符（如果存在）
            if (result.Length > 0 && result[result.Length - 1] == ' ')
            {
                result.Length--;
            }

            return result.ToString().Trim();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("WorklistSCP", ex, "转换患者姓名失败: {Name}", chineseName);
            return chineseName; // 转换失败时返回原始名字
        }
    }

    // 处理 DICOM 值
    private string ProcessDicomValue(string value, DicomTag tag, bool needConvertName)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var vr = tag.DictionaryEntry.ValueRepresentations.First();
        string processedValue = value;

        // 1. 先处理 CS 类型的特殊要求
        if (vr.Name == "CS")
        {
            // CS 类型的字符限制：只允许大写字母、数字、空格和下划线
            processedValue = new string(
                [.. processedValue.ToUpperInvariant().Where(c => char.IsUpper(c) || char.IsDigit(c) || c == ' ' || c == '_')]
            ).Trim();
        }

        // 2. 如果需要转换中文且字符串包含中文字符
        if (needConvertName && ContainsChineseCharacters(processedValue))
        {
            processedValue = ConvertToDeviceName(processedValue);
        }

        return processedValue;
    }

    private static string FormatDatasetStructure(DicomDataset dataset, bool ignorePrivateTags = false)
    {
        var sb = new StringBuilder();
        AppendDatasetStructure(sb, dataset, 0, ignorePrivateTags);
        return sb.ToString();
    }

    private static void AppendDatasetStructure(StringBuilder sb, DicomDataset dataset, int indentLevel, bool ignorePrivateTags)
    {
        var indent = new string(' ', indentLevel * 2);

        foreach (var item in dataset)
        {
            if (ignorePrivateTags && item.Tag.IsPrivate)
            {
                continue;
            }

            if (item is DicomSequence sequence)
            {
                sb.AppendLine($"{indent}- {sequence.Tag} {sequence.Tag.DictionaryEntry.Name}: Sequence ({sequence.Items.Count} item(s))");
                for (var i = 0; i < sequence.Items.Count; i++)
                {
                    sb.AppendLine($"{indent}  [Item {i + 1}]");
                    AppendDatasetStructure(sb, sequence.Items[i], indentLevel + 2, ignorePrivateTags);
                }
            }
            else
            {
                sb.AppendLine($"{indent}- {item.Tag} {item.Tag.DictionaryEntry.Name}: {item}");
            }
        }
    }

    private static bool ContainsChineseCharacters(string text)
    {
        return text.Any(c => PinyinHelper.IsChinese(c));
    }
} 