using Dapper;
using DicomSCP.Models;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;

namespace DicomSCP.Data;

public class DicomRepository(IConfiguration configuration, ILogger<DicomRepository> logger, IOptions<DicomSettings> settings)
    : BaseRepository(configuration.GetConnectionString("DicomDb") ?? throw new ArgumentException("Missing DicomDb connection string"), logger)
{
    private readonly DicomSettings _settings = settings.Value;

    public async Task<IEnumerable<Series>> GetSeriesByStudyUidAsync(string studyUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = @"
            SELECT s.*, 
                   (SELECT COUNT(*) FROM Instances i WHERE i.SeriesInstanceUid = s.SeriesInstanceUid) as NumberOfInstances,
                   (SELECT Modality FROM Studies WHERE StudyInstanceUid = s.StudyInstanceUid) as StudyModality
            FROM Series s
            WHERE s.StudyInstanceUid = @StudyUid
            ORDER BY CAST(s.SeriesNumber as INTEGER)";

        return await connection.QueryAsync<Series>(sql, new { StudyUid = studyUid });
    }

    public async Task<bool> ValidateUserAsync(string username, string password)
    {
        using var connection = new SqliteConnection(_connectionString);
        var hashedPassword = HashPassword(password);
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Users WHERE Username = @Username AND Password = @Password",
            new { Username = username, Password = hashedPassword }
        );
        return count > 0;
    }

    public async Task<bool> ChangePasswordAsync(string username, string newPassword)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var hashedPassword = HashPassword(newPassword);
        
        var sql = @"
            UPDATE Users 
            SET Password = @Password 
            WHERE Username = @Username";
        
        var result = await connection.ExecuteAsync(sql, new { 
            Username = username, 
            Password = hashedPassword 
        });

        return result > 0;
    }

    private string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(hashedBytes);
    }

    public async Task DeleteStudyAsync(string studyInstanceUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
  
        try
        {
            // 先检查是否存在相关记录
            var instanceCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Instances WHERE SeriesInstanceUid IN (SELECT SeriesInstanceUid FROM Series WHERE StudyInstanceUid = @StudyInstanceUid)",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );
  
            // 删除与 Series 相关的 Instances
            await connection.ExecuteAsync(
                "DELETE FROM Instances WHERE SeriesInstanceUid IN (SELECT SeriesInstanceUid FROM Series WHERE StudyInstanceUid = @StudyInstanceUid)",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );
  
            // 删除与 Study 相关的 Series
            await connection.ExecuteAsync(
                "DELETE FROM Series WHERE StudyInstanceUid = @StudyInstanceUid",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );
  
            // 删除 Study
            await connection.ExecuteAsync(
                "DELETE FROM Studies WHERE StudyInstanceUid = @StudyInstanceUid",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );
  
            await transaction.CommitAsync();
            LogInformation("成功删除检查 - StudyInstanceUID: {StudyInstanceUid}, 删除实例数: {InstanceCount}", 
                studyInstanceUid, instanceCount);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            LogError(ex, "删除检查失败 - 检查实例UID: {StudyInstanceUid}", studyInstanceUid);
            throw;
        }
    }

    public async Task<Study?> GetStudyAsync(string studyInstanceUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        var sql = @"
            SELECT s.*, p.PatientName, p.PatientId, p.PatientSex, p.PatientBirthDate
            FROM Studies s
            LEFT JOIN Patients p ON s.PatientId = p.PatientId
            WHERE s.StudyInstanceUid = @StudyInstanceUid";
        
        return await connection.QueryFirstOrDefaultAsync<Study>(
            sql,
            new { StudyInstanceUid = studyInstanceUid }
        );
    }

    public async Task<IEnumerable<Instance>> GetSeriesInstancesAsync(string seriesInstanceUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = @"
            SELECT * FROM Instances 
            WHERE SeriesInstanceUid = @SeriesInstanceUid 
            ORDER BY CAST(InstanceNumber as INTEGER)";
        
        return await connection.QueryAsync<Instance>(sql, new { SeriesInstanceUid = seriesInstanceUid });
    }

    public async Task<Instance?> GetInstanceAsync(string sopInstanceUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = "SELECT * FROM Instances WHERE SopInstanceUid = @SopInstanceUid";
        
        return await connection.QueryFirstOrDefaultAsync<Instance>(
            sql, 
            new { SopInstanceUid = sopInstanceUid }
        );
    }

    public List<WorklistItem> GetWorklistItems(
        string patientId,
        string patientName,
        string accessionNumber,
        (string StartDate, string EndDate) dateRange,
        string modality,
        string scheduledStationName)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT * FROM Worklist 
                WHERE 1=1
                AND (@PatientId = '' OR PatientId LIKE @PatientId)
                AND (@PatientName = '' OR PatientName LIKE @PatientName)
                AND (@AccessionNumber = '' OR AccessionNumber LIKE @AccessionNumber)
                AND (@StartDate = '' OR @EndDate = '' OR 
                     substr(ScheduledDateTime, 1, 8) >= @StartDate AND 
                     substr(ScheduledDateTime, 1, 8) <= @EndDate)
                AND (@Modality = '' OR Modality = @Modality)
                AND (@ScheduledStationName = '' OR ScheduledStationName = @ScheduledStationName)
                AND Status = 'SCHEDULED'
                ORDER BY CreateTime DESC";

            var parameters = new
            {
                PatientId = string.IsNullOrEmpty(patientId) ? "" : $"%{patientId}%",
                PatientName = string.IsNullOrEmpty(patientName) ? "" : $"%{patientName}%",
                AccessionNumber = string.IsNullOrEmpty(accessionNumber) ? "" : $"%{accessionNumber}%",
                StartDate = dateRange.StartDate,
                EndDate = dateRange.EndDate,
                Modality = string.IsNullOrEmpty(modality) ? "" : modality,
                ScheduledStationName = string.IsNullOrEmpty(scheduledStationName) ? "" : scheduledStationName
            };

            LogDebug("执行工作列表查询 - SQL: {Sql}, 参数: {@Parameters}", sql, parameters);

            var items = connection.Query<WorklistItem>(sql, parameters);
            var result = items?.ToList() ?? new List<WorklistItem>();
            LogInformation("工作列表查询完成 - 返回记录数: {Count}", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "工作列表查询失败");
            throw;
        }
    }

    public async Task<PagedResult<StudyInfo>> GetStudiesAsync(
        int page,
        int pageSize,
        string? patientId = null,
        string? patientName = null,
        string? accessionNumber = null,
        string? modality = null,
        DateTime? startDate = null,
        DateTime? endDate = null)
    {
        var sql = new StringBuilder(@"
            SELECT 
                s.StudyInstanceUid,
                s.PatientId,
                p.PatientName,
                p.PatientSex,
                p.PatientBirthDate,
                s.AccessionNumber,
                s.Modality,
                s.StudyDate,
                s.StudyDescription,
                s.Remark,
                COUNT(DISTINCT i.SopInstanceUid) as NumberOfInstances
            FROM Studies s
            LEFT JOIN Patients p ON s.PatientId = p.PatientId
            LEFT JOIN Series ser ON s.StudyInstanceUid = ser.StudyInstanceUid
            LEFT JOIN Instances i ON ser.SeriesInstanceUid = i.SeriesInstanceUid
            WHERE 1=1");

        var parameters = new DynamicParameters();
        
        if (!string.IsNullOrEmpty(patientId))
        {
            sql.Append(" AND s.PatientId LIKE @PatientId");
            parameters.Add("@PatientId", $"%{patientId}%");
        }

        if (!string.IsNullOrEmpty(patientName))
        {
            sql.Append(" AND p.PatientName LIKE @PatientName");
            parameters.Add("@PatientName", $"%{patientName}%");
        }

        if (!string.IsNullOrEmpty(accessionNumber))
        {
            sql.Append(" AND s.AccessionNumber LIKE @AccessionNumber");
            parameters.Add("@AccessionNumber", $"%{accessionNumber}%");
        }

        if (!string.IsNullOrEmpty(modality))
        {
            sql.Append(" AND s.Modality = @Modality");
            parameters.Add("@Modality", modality);
        }

        if (startDate.HasValue)
        {
            sql.Append(" AND s.StudyDate >= @StartDate");
            parameters.Add("@StartDate", startDate.Value.ToString("yyyyMMdd"));
        }

        if (endDate.HasValue)
        {
            sql.Append(" AND s.StudyDate <= @EndDate");
            parameters.Add("@EndDate", endDate.Value.ToString("yyyyMMdd"));
        }

        // 添加 GROUP BY 子句，包含所有选择的字段
        sql.Append(@" 
            GROUP BY 
                s.StudyInstanceUid,
                s.PatientId,
                p.PatientName,
                p.PatientSex,
                p.PatientBirthDate,
                s.AccessionNumber,
                s.Modality,
                s.StudyDate,
                s.StudyDescription,
                s.Remark");

        // 获取总记录数（修改子查询以包含 GROUP BY）
        var countSql = $@"
            SELECT COUNT(*) FROM (
                SELECT s.StudyInstanceUid
                FROM Studies s
                LEFT JOIN Patients p ON s.PatientId = p.PatientId
                LEFT JOIN Series ser ON s.StudyInstanceUid = ser.StudyInstanceUid
                LEFT JOIN Instances i ON ser.SeriesInstanceUid = i.SeriesInstanceUid
                WHERE 1=1
                {sql.ToString().Substring(sql.ToString().IndexOf("WHERE 1=1") + 9)}
            ) as t";

        // 添加排序和分页
        sql.Append(" ORDER BY s.StudyDate DESC, s.StudyTime DESC");
        sql.Append(" LIMIT @PageSize OFFSET @Offset");
        
        parameters.Add("@PageSize", pageSize);
        parameters.Add("@Offset", (page - 1) * pageSize);

        await using var connection = new SqliteConnection(_connectionString);
        var totalCount = await connection.ExecuteScalarAsync<int>(countSql, parameters);
        var items = await connection.QueryAsync<StudyInfo>(sql.ToString(), parameters);

        return new PagedResult<StudyInfo>
        {
            Items = items.ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public List<Series> GetSeriesByStudyUid(string studyInstanceUid)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT s.*, 
                       (SELECT COUNT(*) FROM Instances i WHERE i.SeriesInstanceUid = s.SeriesInstanceUid) as NumberOfInstances
                FROM Series s
                WHERE s.StudyInstanceUid = @StudyInstanceUid
                ORDER BY CAST(s.SeriesNumber as INTEGER)";

            LogDebug("执行序列查询 - SQL: {Sql}, StudyInstanceUid: {StudyInstanceUid}", 
                sql, studyInstanceUid);

            var series = connection.Query<Series>(sql, new { StudyInstanceUid = studyInstanceUid });

            var result = series?.ToList() ?? new List<Series>();
            LogInformation("序列查询完成 - StudyInstanceUid: {StudyInstanceUid}, 返回记录数: {Count}", 
                studyInstanceUid, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "序列查询失败 - StudyInstanceUid: {StudyInstanceUid}", studyInstanceUid);
            return new List<Series>();
        }
    }

    public List<Instance> GetInstancesBySeriesUid(string studyInstanceUid, string seriesInstanceUid)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT i.*, s.StudyInstanceUid, s.Modality
                FROM Instances i
                JOIN Series s ON i.SeriesInstanceUid = s.SeriesInstanceUid
                WHERE s.StudyInstanceUid = @StudyInstanceUid 
                AND i.SeriesInstanceUid = @SeriesInstanceUid
                ORDER BY CAST(i.InstanceNumber as INTEGER)";

            LogDebug("执行图像查询 - SQL: {Sql}, StudyInstanceUid: {StudyInstanceUid}, SeriesInstanceUid: {SeriesInstanceUid}", 
                sql, studyInstanceUid, seriesInstanceUid);

            var instances = connection.Query<Instance>(sql, new 
            { 
                StudyInstanceUid = studyInstanceUid,
                SeriesInstanceUid = seriesInstanceUid
            });

            var result = instances?.ToList() ?? new List<Instance>();
            LogInformation("图像查询完成 - StudyInstanceUid: {StudyInstanceUid}, SeriesInstanceUid: {SeriesInstanceUid}, 返回记录数: {Count}", 
                studyInstanceUid, seriesInstanceUid, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "图像查询失败 - StudyInstanceUid: {StudyInstanceUid}, SeriesInstanceUid: {SeriesInstanceUid}", 
                studyInstanceUid, seriesInstanceUid);
            return new List<Instance>();
        }
    }

    public IEnumerable<Instance> GetInstancesByStudyUid(string studyInstanceUid)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT i.*, s.StudyInstanceUid, s.Modality
                FROM Instances i
                JOIN Series s ON i.SeriesInstanceUid = s.SeriesInstanceUid
                WHERE s.StudyInstanceUid = @StudyInstanceUid
                ORDER BY CAST(i.InstanceNumber as INTEGER)";

            LogDebug("执行实例查询 - SQL: {Sql}, StudyInstanceUid: {StudyInstanceUid}", 
                sql, studyInstanceUid);

            var instances = connection.Query<Instance>(sql, new { StudyInstanceUid = studyInstanceUid });

            var result = instances?.ToList() ?? new List<Instance>();
            LogInformation("实例查询完成 - StudyInstanceUid: {StudyInstanceUid}, 返回记录数: {Count}", 
                studyInstanceUid, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "实例查询失败 - StudyInstanceUid: {StudyInstanceUid}", studyInstanceUid);
            return new List<Instance>();
        }
    }

    #region PrintSCP Operations

    /// <summary>
    /// 添加新的打印任务
    /// </summary>
    public async Task<bool> AddPrintJobAsync(PrintJob job)
    {
        try
        {
            using var connection = CreateConnection();
            var parameters = new DynamicParameters();
            
            // 基本信息
            parameters.Add("@JobId", job.JobId);
            parameters.Add("@FilmSessionId", job.FilmSessionId);
            parameters.Add("@FilmBoxId", job.FilmBoxId);
            parameters.Add("@CallingAE", job.CallingAE);
            parameters.Add("@Status", job.Status.ToString());
            parameters.Add("@ErrorMessage", job.ErrorMessage);

            // Film Session 参数
            parameters.Add("@NumberOfCopies", job.NumberOfCopies);
            parameters.Add("@PrintPriority", job.PrintPriority);
            parameters.Add("@MediumType", job.MediumType);
            parameters.Add("@FilmDestination", job.FilmDestination);

            // Film Box 参数
            parameters.Add("@PrintInColor", job.PrintInColor ? 1 : 0);  // 布尔值转换为整数
            parameters.Add("@FilmOrientation", job.FilmOrientation);
            parameters.Add("@FilmSizeID", job.FilmSizeID);
            parameters.Add("@ImageDisplayFormat", job.ImageDisplayFormat);
            parameters.Add("@MagnificationType", job.MagnificationType);
            parameters.Add("@SmoothingType", job.SmoothingType);
            parameters.Add("@BorderDensity", job.BorderDensity);
            parameters.Add("@EmptyImageDensity", job.EmptyImageDensity);
            parameters.Add("@Trim", job.Trim);

            // 其他参数
            parameters.Add("@ImagePath", job.ImagePath);
            parameters.Add("@StudyInstanceUID", job.StudyInstanceUID);
            parameters.Add("@CreateTime", job.CreateTime);
            parameters.Add("@UpdateTime", job.UpdateTime);

            var sql = @"
                INSERT INTO PrintJobs (
                    JobId, FilmSessionId, FilmBoxId, CallingAE, Status, ErrorMessage,
                    NumberOfCopies, PrintPriority, MediumType, FilmDestination,
                    PrintInColor, FilmOrientation, FilmSizeID, ImageDisplayFormat,
                    MagnificationType, SmoothingType, BorderDensity, EmptyImageDensity,
                    Trim, ImagePath, StudyInstanceUID,
                    CreateTime, UpdateTime
                ) VALUES (
                    @JobId, @FilmSessionId, @FilmBoxId, @CallingAE, @Status, @ErrorMessage,
                    @NumberOfCopies, @PrintPriority, @MediumType, @FilmDestination,
                    @PrintInColor, @FilmOrientation, @FilmSizeID, @ImageDisplayFormat,
                    @MagnificationType, @SmoothingType, @BorderDensity, @EmptyImageDensity,
                    @Trim, @ImagePath, @StudyInstanceUID,
                    @CreateTime, @UpdateTime
                )";

            var result = await connection.ExecuteAsync(sql, parameters);
            LogDebug("添加打印任务 - JobId: {JobId}, 结果: {Result}", job.JobId, result > 0);
            return result > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "添加打印任务失败 - JobId: {JobId}", job.JobId);
            throw;
        }
    }

    /// <summary>
    /// 更打印任务状态和图像路径
    /// </summary>
    public async Task<bool> UpdatePrintJobStatusAsync(string jobId, PrintJobStatus status, string? imagePath = null)
    {
        try
        {
            using var connection = CreateConnection();
            var updates = new List<string> { "Status = @Status", "UpdateTime = @UpdateTime" };
            var parameters = new DynamicParameters();
            parameters.Add("@JobId", jobId);
            parameters.Add("@Status", status.ToString());
            parameters.Add("@UpdateTime", DateTime.Now);

            if (imagePath != null)
            {
                updates.Add("ImagePath = @ImagePath");
                parameters.Add("@ImagePath", imagePath);
            }

            var sql = $"UPDATE PrintJobs SET {string.Join(", ", updates)} WHERE JobId = @JobId";
            var result = await connection.ExecuteAsync(sql, parameters);
            LogDebug("更新打印任务状态 - JobId: {JobId}, 状态: {Status}, 结果: {Result}", jobId, status, result > 0);
            return result > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "更新打印任务状态失败 - JobId: {JobId}, 状态: {Status}", jobId, status);
            throw;
        }
    }

    /// <summary>
    /// 更打印任务信息
    /// </summary>
    public async Task<bool> UpdatePrintJobAsync(string filmSessionId, string? filmBoxId = null, Dictionary<string, object>? parameters = null)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync();
        using var transaction = await connection.BeginTransactionAsync();
        
        try
        {
            var updates = new List<string>();
            var dbParams = new DynamicParameters();
            dbParams.Add("@FilmSessionId", filmSessionId);
            dbParams.Add("@UpdateTime", DateTime.Now);

            if (filmBoxId != null)
            {
                updates.Add("FilmBoxId = @FilmBoxId");
                dbParams.Add("@FilmBoxId", filmBoxId);
            }

            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    updates.Add($"{param.Key} = @{param.Key}");
                    dbParams.Add($"@{param.Key}", param.Value);
                    LogDebug("更新参数 - {ParamName}: {ParamValue}", param.Key, param.Value);
                }
            }

            updates.Add("UpdateTime = @UpdateTime");

            var sql = $"UPDATE PrintJobs SET {string.Join(", ", updates)} WHERE FilmSessionId = @FilmSessionId";
            var result = await connection.ExecuteAsync(sql, dbParams, transaction);
            await transaction.CommitAsync();
            return result > 0;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            LogError(ex, "更新打印任务失败 - FilmSessionId: {FilmSessionId}", filmSessionId);
            throw;
        }
    }

    /// <summary>
    /// 根据FilmBoxId获取打印任务
    /// </summary>
    public async Task<PrintJob?> GetPrintJobByFilmBoxIdAsync(string filmBoxId)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT 
                    JobId, FilmSessionId, FilmBoxId, CallingAE, Status, ErrorMessage,
                    NumberOfCopies, PrintPriority, MediumType, FilmDestination,
                    CASE WHEN PrintInColor = 1 THEN true ELSE false END as PrintInColor,
                    FilmOrientation, FilmSizeID, ImageDisplayFormat,
                    MagnificationType, SmoothingType, BorderDensity, EmptyImageDensity,
                    Trim, ImagePath, StudyInstanceUID,
                    CreateTime, UpdateTime
                FROM PrintJobs 
                WHERE FilmBoxId = @FilmBoxId";

            return await connection.QueryFirstOrDefaultAsync<PrintJob>(sql, new { FilmBoxId = filmBoxId });
        }
        catch (Exception ex)
        {
            LogError(ex, "获取打印任务失败 - FilmBoxId: {FilmBoxId}", filmBoxId);
            throw;
        }
    }

    /// <summary>
    /// 获取指定状态的打印任务列表
    /// </summary>
    #endregion

    #region Print Management API

    /// <summary>
    /// 获取所有打印任务列表
    /// </summary>
    public async Task<(List<PrintJob> Items, int Total, int Page, int PageSize, int TotalPages)> GetPrintJobsAsync(
        string? callingAE = null,
        string? studyUID = null,
        string? status = null,
        DateTime? date = null,
        int page = 1,
        int pageSize = 10)
    {
        try
        {
            using var connection = CreateConnection();
            
            // 构建查询
            var sql = new StringBuilder("SELECT COUNT(*) FROM PrintJobs WHERE 1=1");
            var parameters = new DynamicParameters();

            if (!string.IsNullOrEmpty(callingAE))
            {
                sql.Append(" AND CallingAE LIKE @CallingAE");
                parameters.Add("@CallingAE", $"%{callingAE}%");
            }

            if (!string.IsNullOrEmpty(studyUID))
            {
                sql.Append(" AND StudyInstanceUID LIKE @StudyUID");
                parameters.Add("@StudyUID", $"%{studyUID}%");
            }

            if (!string.IsNullOrEmpty(status))
            {
                sql.Append(" AND Status = @Status");
                parameters.Add("@Status", status);
            }

            if (date.HasValue)
            {
                sql.Append(" AND DATE(CreateTime) = DATE(@Date)");
                parameters.Add("@Date", date.Value.Date);
            }

            // 获取总记录数
            var total = await connection.ExecuteScalarAsync<int>(sql.ToString(), parameters);
            
            // 计算总页数
            var totalPages = (int)Math.Ceiling(total / (double)pageSize);
            
            // 构建分页查询
            sql = new StringBuilder(@"
                SELECT * FROM PrintJobs WHERE 1=1");

            if (!string.IsNullOrEmpty(callingAE))
            {
                sql.Append(" AND CallingAE LIKE @CallingAE");
            }

            if (!string.IsNullOrEmpty(studyUID))
            {
                sql.Append(" AND StudyInstanceUID LIKE @StudyUID");
            }

            if (!string.IsNullOrEmpty(status))
            {
                sql.Append(" AND Status = @Status");
            }

            if (date.HasValue)
            {
                sql.Append(" AND DATE(CreateTime) = DATE(@Date)");
            }

            sql.Append(" ORDER BY CreateTime DESC LIMIT @PageSize OFFSET @Offset");
            parameters.Add("@PageSize", pageSize);
            parameters.Add("@Offset", (page - 1) * pageSize);

            var items = await connection.QueryAsync<PrintJob>(sql.ToString(), parameters);

            return (items.ToList(), total, page, pageSize, totalPages);
        }
        catch (Exception ex)
        {
            LogError(ex, "获取打印任务列表失败");
            throw;
        }
    }

    /// <summary>
    /// 获取单个打印任务
    /// </summary>
    public async Task<PrintJob?> GetPrintJobAsync(string jobId)
    {
        using var connection = CreateConnection();
        var sql = "SELECT * FROM PrintJobs WHERE JobId = @JobId";
        return await connection.QueryFirstOrDefaultAsync<PrintJob>(sql, new { JobId = jobId });
    }

    /// <summary>
    /// 删除打印任务
    /// </summary>
    public async Task<bool> DeletePrintJobAsync(string jobId)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = "DELETE FROM PrintJobs WHERE JobId = @JobId";
            var rowsAffected = await connection.ExecuteAsync(sql, new { JobId = jobId });
            LogDebug("删除印任务 - JobId: {JobId}, 成功: {Success}", jobId, rowsAffected > 0);
            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "删除打印任务失败 - JobId: {JobId}", jobId);
            throw;
        }
    }

    #endregion

    public IEnumerable<Patient> GetPatients(string patientId, string patientName)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT 
                    p.PatientId, 
                    p.PatientName, 
                    p.PatientBirthDate, 
                    p.PatientSex,
                    p.CreateTime,
                    COUNT(DISTINCT s.StudyInstanceUid) as NumberOfStudies,
                    COUNT(DISTINCT ser.SeriesInstanceUid) as NumberOfSeries,
                    COUNT(DISTINCT i.SopInstanceUid) as NumberOfInstances
                FROM Patients p
                LEFT JOIN Studies s ON p.PatientId = s.PatientId
                LEFT JOIN Series ser ON s.StudyInstanceUid = ser.StudyInstanceUid
                LEFT JOIN Instances i ON ser.SeriesInstanceUid = i.SeriesInstanceUid
                WHERE 1=1";

            var parameters = new DynamicParameters();

            if (!string.IsNullOrEmpty(patientId))
            {
                sql += " AND p.PatientId LIKE @PatientId";
                parameters.Add("@PatientId", $"%{patientId}%");
            }

            if (!string.IsNullOrEmpty(patientName))
            {
                sql += " AND p.PatientName LIKE @PatientName";
                parameters.Add("@PatientName", $"%{patientName}%");
            }

            sql += @" 
                GROUP BY p.PatientId, p.PatientName, p.PatientBirthDate, p.PatientSex, p.CreateTime
                ORDER BY p.PatientName";

            LogDebug("执行Patient查询 - SQL: {Sql}, PatientId: {PatientId}, PatientName: {PatientName}", 
                sql, patientId, patientName);

            var patients = connection.Query<Patient>(sql, parameters).ToList();

            LogInformation("Patient查询完成 - 返回记录数: {Count}", patients.Count);

            return patients;
        }
        catch (Exception ex)
        {
            LogError(ex, "Patient查询失败 - PatientId: {PatientId}, PatientName: {PatientName}", 
                patientId, patientName);
            return Enumerable.Empty<Patient>();
        }
    }

    // 保原有法，供 QRSCP 使用
    public List<Study> GetStudies(
        string patientId, 
        string patientName, 
        string accessionNumber, 
        (string StartDate, string EndDate) dateRange,
        string[]? modalities,
        string? studyInstanceUid = null,
        int? offset = null,
        int? limit = null)
    {
        try
        {
            using var connection = CreateConnection();
            // 先查询总数
            var countSql = @"
                SELECT COUNT(DISTINCT s.StudyInstanceUid)
                FROM Studies s
                LEFT JOIN Patients p ON s.PatientId = p.PatientId
                WHERE 1=1
                AND (@PatientId = '' OR s.PatientId LIKE @PatientId)
                AND (@PatientName = '' OR p.PatientName LIKE @PatientName)
                AND (@AccessionNumber = '' OR s.AccessionNumber LIKE @AccessionNumber)
                AND (@StartDate = '' OR s.StudyDate >= @StartDate)
                AND (@EndDate = '' OR s.StudyDate <= @EndDate)
                AND (@ModCount = 0 OR s.Modality IN @Modalities)
                AND (@StudyInstanceUid = '' OR s.StudyInstanceUid = @StudyInstanceUid)";

            var sql = @"
                SELECT 
                    s.*,
                    p.PatientName,
                    p.PatientSex,
                    p.PatientBirthDate,
                    COUNT(DISTINCT ser.SeriesInstanceUid) as NumberOfStudyRelatedSeries,
                    COUNT(DISTINCT i.SopInstanceUid) as NumberOfStudyRelatedInstances
                FROM Studies s
                LEFT JOIN Patients p ON s.PatientId = p.PatientId
                LEFT JOIN Series ser ON s.StudyInstanceUid = ser.StudyInstanceUid
                LEFT JOIN Instances i ON ser.SeriesInstanceUid = i.SeriesInstanceUid
                WHERE 1=1
                AND (@PatientId = '' OR s.PatientId LIKE @PatientId)
                AND (@PatientName = '' OR p.PatientName LIKE @PatientName)
                AND (@AccessionNumber = '' OR s.AccessionNumber LIKE @AccessionNumber)
                AND (@StartDate = '' OR s.StudyDate >= @StartDate)
                AND (@EndDate = '' OR s.StudyDate <= @EndDate)
                AND (@ModCount = 0 OR s.Modality IN @Modalities)
                AND (@StudyInstanceUid = '' OR s.StudyInstanceUid = @StudyInstanceUid)
                GROUP BY 
                    s.StudyInstanceUid,
                    s.PatientId,
                    s.StudyDate,
                    s.StudyTime,
                    s.StudyDescription,
                    s.AccessionNumber,
                    s.Modality,
                    s.CreateTime,
                    p.PatientName,
                    p.PatientSex,
                    p.PatientBirthDate
                ORDER BY s.CreateTime DESC";

            // 如果指定了分页参数，添加分页
            if (offset.HasValue && limit.HasValue)
            {
                sql += " LIMIT @Limit OFFSET @Offset";
            }

            var parameters = new
            {
                PatientId = string.IsNullOrEmpty(patientId) ? "" : $"%{patientId}%",
                PatientName = string.IsNullOrEmpty(patientName) ? "" : $"%{patientName}%",
                AccessionNumber = string.IsNullOrEmpty(accessionNumber) ? "" : $"%{accessionNumber}%",
                StartDate = dateRange.StartDate,
                EndDate = dateRange.EndDate,
                ModCount = modalities?.Length ?? 0,
                Modalities = (modalities?.Length ?? 0) > 0 ? modalities : new[] { "" },
                StudyInstanceUid = studyInstanceUid ?? "",
                Offset = offset,
                Limit = limit
            };

            LogDebug("执行检查查询 - SQL: {Sql}, 参数: {@Parameters}", sql, parameters);

            // 获取总数
            var totalCount = connection.ExecuteScalar<int>(countSql, parameters);

            var studies = connection.Query<Study>(sql, parameters);
            var result = studies?.ToList() ?? new List<Study>();
            
            LogInformation("检查查询完成 - 返回记录数: {Count}/{Total}, 日期范围: {StartDate} - {EndDate}, StudyInstanceUID: {StudyUID}", 
                result.Count, totalCount, dateRange.StartDate ?? "", dateRange.EndDate ?? "", studyInstanceUid ?? "");

            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "检查查询失败");
            return new List<Study>();
        }
    }

    public async Task<PrintJob?> GetPrintJobByIdAsync(string jobId)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = "SELECT * FROM PrintJobs WHERE JobId = @JobId";
        return await connection.QueryFirstOrDefaultAsync<PrintJob>(sql, new { JobId = jobId });
    }

    public Configuration.PrinterConfig? GetPrinterByName(string printerName)
    {
        var printers = _settings.PrintSCU?.Printers;
        return printers?.FirstOrDefault(p => p.Name == printerName);
    }

    public List<Configuration.PrinterConfig> GetPrinters()
    {
        return _settings.PrintSCU?.Printers ?? new List<Configuration.PrinterConfig>();
    }

    public async Task<IEnumerable<Series>> GetSeriesAsync(string studyInstanceUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = @"
            SELECT s.*, COUNT(i.SopInstanceUid) as NumberOfInstances
            FROM Series s
            LEFT JOIN Instances i ON s.SeriesInstanceUid = i.SeriesInstanceUid
            WHERE s.StudyInstanceUid = @StudyInstanceUid
            GROUP BY s.SeriesInstanceUid, s.StudyInstanceUid, s.Modality, 
                     s.SeriesNumber, s.SeriesDescription, s.SliceThickness, 
                     s.SeriesDate, s.CreateTime";

        return await connection.QueryAsync<Series>(sql, new { StudyInstanceUid = studyInstanceUid });
    }
}