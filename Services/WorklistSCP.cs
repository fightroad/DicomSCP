using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using DicomSCP.Data;
using DicomSCP.Models;
using DicomSCP.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Dapper;
using Serilog;
using ILogger = Serilog.ILogger;

namespace DicomSCP.Services;

public class WorklistSCP : DicomService, IDicomServiceProvider, IDicomCFindProvider, IDicomCEchoProvider
{
    private static DicomSettings? _settings;
    private static string? _connectionString;
    private static DicomRepository? _repository;
    private readonly ILogger _logger = Log.ForContext<WorklistSCP>();

    public static void Configure(
        DicomSettings settings,
        IConfiguration configuration,
        DicomRepository repository)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _connectionString = configuration?.GetConnectionString("DicomDb") ?? "Data Source=dicom.db";
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public WorklistSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        Microsoft.Extensions.Logging.ILogger log, 
        DicomServiceDependencies dependencies,
        IOptions<DicomSettings> settings)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        if (settings?.Value == null || dependencies?.LoggerFactory == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        _settings = settings.Value;
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind ||
                pc.AbstractSyntax == DicomUID.Verification)
            {
                pc.AcceptTransferSyntaxes(
                    DicomTransferSyntax.ImplicitVRLittleEndian,
                    DicomTransferSyntax.ExplicitVRLittleEndian,
                    DicomTransferSyntax.ExplicitVRBigEndian);
                _logger.Information("接受服务 - AET: {CallingAE}, 服务: {Service}", 
                    association.CallingAE, pc.AbstractSyntax.Name);
            }
            else
            {
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                _logger.Warning("拒绝不支持的服务 - AET: {CallingAE}, AbstractSyntax: {AbstractSyntax}", 
                    association.CallingAE, pc.AbstractSyntax);
            }
        }

        return SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        _logger.Information("接收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        _logger.Warning("接收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            _logger.Error(exception, "连接异常关闭");
        }
        else
        {
            _logger.Information("连接正常关闭");
        }
    }

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        _logger.Information(
            "收到 Worklist 查询请求 - 请求数据集: {@Dataset}", 
            new 
            { 
                PatientId = request.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, ""),
                AccessionNumber = request.Dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, ""),
                Modality = request.Dataset.GetSingleValueOrDefault(DicomTag.Modality, ""),
                ScheduledDate = request.Dataset.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepStartDate, "")
            });

        // 从请求中提取查询条件
        var filters = ExtractFilters(request.Dataset);
        _logger.Information("查询条件 - {@Filters}", filters);

        // 查询数据库
        List<WorklistItem>? worklistItems = null;
        DicomStatus status = DicomStatus.Success;

        try
        {
            worklistItems = await QueryWorklistItemsAsync(request, filters);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "数据库查询失败");
            status = DicomStatus.ProcessingFailure;
        }

        if (status != DicomStatus.Success)
        {
            yield return new DicomCFindResponse(request, status);
            yield break;
        }

        if (worklistItems == null || worklistItems.Count == 0)
        {
            _logger.Warning("未找到匹配的预约记录");
            yield return new DicomCFindResponse(request, DicomStatus.Success);
            yield break;
        }

        _logger.Information("查询到 {Count} 条预约记录", worklistItems.Count);

        // 返回查询结果
        foreach (var item in worklistItems)
        {
            DicomCFindResponse? response = null;
            try
            {
                _logger.Debug("处理预约记录 - PatientId: {PatientId}, AccessionNumber: {AccessionNumber}", 
                    item.PatientId, item.AccessionNumber);

                response = CreateWorklistResponse(request, item);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "创建响应失败 - PatientId: {PatientId}", item.PatientId);
                continue;
            }

            if (response != null)
            {
                _logger.Debug("发送响应 - PatientId: {PatientId}", item.PatientId);
                yield return response;
            }
        }

        _logger.Information("Worklist查询完成 - 总记录数: {Count}", worklistItems.Count);
        yield return new DicomCFindResponse(request, DicomStatus.Success);
    }

    private async Task<List<WorklistItem>> QueryWorklistItemsAsync(DicomCFindRequest request, 
        (string PatientId, string AccessionNumber, string ScheduledDateTime, string Modality, string ScheduledStationName) filters)
    {
        if (_connectionString == null)
        {
            _logger.Error("数据库连接字符串未配置");
            throw new InvalidOperationException("Database connection string not configured");
        }

        try
        {
            _logger.Debug("执行数据库查询 - 连接字符串: {ConnectionString}", _connectionString);
            using var connection = new SqliteConnection(_connectionString);

            // 修改 SQL 查询，当参数为空时返回所有记录
            var sql = @"SELECT * FROM Worklist 
                WHERE (@PatientId IS NULL OR @PatientId = '' OR PatientId LIKE @PatientId)
                AND (@AccessionNumber IS NULL OR @AccessionNumber = '' OR AccessionNumber LIKE @AccessionNumber)
                AND (@ScheduledDateTime IS NULL OR @ScheduledDateTime = '' OR ScheduledDateTime LIKE @ScheduledDateTime)
                AND (@Modality IS NULL OR @Modality = '' OR Modality = @Modality)
                AND (@ScheduledStationName IS NULL OR @ScheduledStationName = '' OR ScheduledStationName = @ScheduledStationName)
                AND Status = 'SCHEDULED'";

            _logger.Debug("SQL查询: {Sql} - 参数: {@Parameters}", sql, new
            {
                PatientId = filters.PatientId,
                AccessionNumber = filters.AccessionNumber,
                ScheduledDateTime = filters.ScheduledDateTime,
                Modality = filters.Modality,
                ScheduledStationName = filters.ScheduledStationName
            });

            var items = await connection.QueryAsync<WorklistItem>(sql,
                new
                {
                    PatientId = string.IsNullOrEmpty(filters.PatientId) ? "" : $"%{filters.PatientId}%",
                    AccessionNumber = string.IsNullOrEmpty(filters.AccessionNumber) ? "" : $"%{filters.AccessionNumber}%",
                    ScheduledDateTime = string.IsNullOrEmpty(filters.ScheduledDateTime) ? "" : $"%{filters.ScheduledDateTime}%",
                    Modality = string.IsNullOrEmpty(filters.Modality) ? "" : filters.Modality,
                    ScheduledStationName = string.IsNullOrEmpty(filters.ScheduledStationName) ? "" : filters.ScheduledStationName
                });

            var result = items?.ToList() ?? new List<WorklistItem>();
            _logger.Information("数据库查询完成 - 返回记录数: {Count}", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "数据库查询失败 - 参数: {@Parameters}", filters);
            throw;
        }
    }

    private DicomCFindResponse? CreateWorklistResponse(DicomCFindRequest request, WorklistItem item)
    {
        try
        {
            var dataset = new DicomDataset();
            
            // 患者信息
            dataset.Add(DicomTag.PatientID, item.PatientId);
            dataset.Add(DicomTag.PatientName, item.PatientName);
            // 确保出生日期格式正确
            try
            {
                var birthDate = DateTime.Parse(item.PatientBirthDate);
                dataset.Add(DicomTag.PatientBirthDate, birthDate.ToString("yyyyMMdd"));
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "处理出生日期失败 - PatientId: {PatientId}, BirthDate: {BirthDate}", 
                    item.PatientId, item.PatientBirthDate);
                dataset.Add(DicomTag.PatientBirthDate, "19000101");  // 使用默认值
            }
            dataset.Add(DicomTag.PatientSex, item.PatientSex);
            
            // 添加年龄信息
            if (!string.IsNullOrEmpty(item.PatientBirthDate))
            {
                try
                {
                    var birthDate = DateTime.Parse(item.PatientBirthDate);
                    var age = DateTime.Now.Year - birthDate.Year;
                    if (DateTime.Now.DayOfYear < birthDate.DayOfYear)
                    {
                        age--;
                    }
                    dataset.Add(DicomTag.PatientAge, $"{age:000}Y");  // 格式化为 "045Y" 这样的格式
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "计算年龄失败 - PatientId: {PatientId}, BirthDate: {BirthDate}", 
                        item.PatientId, item.PatientBirthDate);
                }
            }

            // 研究信息
            dataset.Add(DicomTag.StudyInstanceUID, item.StudyInstanceUid);
            dataset.Add(DicomTag.AccessionNumber, item.AccessionNumber);
            dataset.Add(DicomTag.ReferringPhysicianName, item.ReferringPhysicianName);
            dataset.Add(DicomTag.StudyDescription, item.StudyDescription);

            // 检查部位信息
            dataset.Add(DicomTag.BodyPartExamined, item.BodyPartExamined ?? "");
            dataset.Add(DicomTag.RequestedProcedureDescription, item.RequestedProcedureDescription);
            dataset.Add(DicomTag.ScheduledProcedureStepDescription, item.ScheduledProcedureStepDescription);
            dataset.Add(DicomTag.ReasonForTheRequestedProcedure, item.ReasonForRequest ?? "");

            // 预约信息
            dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 192");  // UTF-8
            dataset.Add(DicomTag.Modality, item.Modality);
            dataset.Add(DicomTag.ScheduledStationAETitle, item.ScheduledAET);

            // 处理预约日期时间
            try
            {
                var scheduledDateTime = DateTime.Parse(item.ScheduledDateTime);
                dataset.Add(DicomTag.ScheduledProcedureStepStartDate, scheduledDateTime.ToString("yyyyMMdd"));
                dataset.Add(DicomTag.ScheduledProcedureStepStartTime, scheduledDateTime.ToString("HHmmss"));
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "处理预约时间失败 - PatientId: {PatientId}, DateTime: {DateTime}", 
                    item.PatientId, item.ScheduledDateTime);
                // 使用默认值或跳过
                dataset.Add(DicomTag.ScheduledProcedureStepStartDate, "19000101");
                dataset.Add(DicomTag.ScheduledProcedureStepStartTime, "000000");
            }

            dataset.Add(DicomTag.ScheduledStationName, item.ScheduledStationName);
            dataset.Add(DicomTag.ScheduledProcedureStepID, item.ScheduledProcedureStepID);
            dataset.Add(DicomTag.RequestedProcedureID, item.RequestedProcedureID);

            return new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = dataset };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "处理单条记录失败 - PatientId: {PatientId}, AccessionNumber: {AccessionNumber}", 
                item.PatientId, item.AccessionNumber);
            return null;
        }
    }

    private (string PatientId, string AccessionNumber, string ScheduledDateTime, 
             string Modality, string ScheduledStationName) ExtractFilters(DicomDataset dataset)
    {
        return (
            dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
            dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty),
            dataset.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepStartDate, string.Empty),
            dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty),
            dataset.GetSingleValueOrDefault(DicomTag.ScheduledStationName, string.Empty)
        );
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        _logger.Information("收到 C-ECHO 请求");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }
} 