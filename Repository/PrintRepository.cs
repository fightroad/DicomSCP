using Dapper;
using DicomSCP.Configuration;
using DicomSCP.Models;
using Microsoft.Extensions.Options;
using System.Text;

namespace DicomSCP.Repository;

public sealed class PrintRepository(IConfiguration configuration, IOptions<DicomSettings> settings)
    : BaseRepository(configuration.GetConnectionString("DicomDb") ?? throw new ArgumentException("Missing DicomDb connection string"))
{
    private readonly DicomSettings _settings = settings.Value;

    public async Task<bool> AddPrintJobAsync(PrintJob job)
    {
        try
        {
            using var connection = CreateConnection();
            var parameters = new DynamicParameters();
            parameters.Add("@JobId", job.JobId);
            parameters.Add("@FilmSessionId", job.FilmSessionId);
            parameters.Add("@FilmBoxId", job.FilmBoxId);
            parameters.Add("@CallingAE", job.CallingAE);
            parameters.Add("@Status", job.Status.ToString());
            parameters.Add("@ErrorMessage", job.ErrorMessage);
            parameters.Add("@NumberOfCopies", job.NumberOfCopies);
            parameters.Add("@PrintPriority", job.PrintPriority);
            parameters.Add("@MediumType", job.MediumType);
            parameters.Add("@FilmDestination", job.FilmDestination);
            parameters.Add("@PrintInColor", job.PrintInColor ? 1 : 0);
            parameters.Add("@FilmOrientation", job.FilmOrientation);
            parameters.Add("@FilmSizeID", job.FilmSizeID);
            parameters.Add("@ImageDisplayFormat", job.ImageDisplayFormat);
            parameters.Add("@MagnificationType", job.MagnificationType);
            parameters.Add("@SmoothingType", job.SmoothingType);
            parameters.Add("@BorderDensity", job.BorderDensity);
            parameters.Add("@EmptyImageDensity", job.EmptyImageDensity);
            parameters.Add("@Trim", job.Trim);
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

            return await connection.ExecuteAsync(sql, parameters) > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "添加打印任务失败 - JobId: {JobId}", job.JobId);
            throw;
        }
    }

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
            return await connection.ExecuteAsync(sql, parameters) > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "更新打印任务状态失败 - JobId: {JobId}, 状态: {Status}", jobId, status);
            throw;
        }
    }

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
            var total = await connection.ExecuteScalarAsync<int>(sql.ToString(), parameters);
            var totalPages = (int)Math.Ceiling(total / (double)pageSize);
            sql = new StringBuilder("SELECT * FROM PrintJobs WHERE 1=1");
            if (!string.IsNullOrEmpty(callingAE)) sql.Append(" AND CallingAE LIKE @CallingAE");
            if (!string.IsNullOrEmpty(studyUID)) sql.Append(" AND StudyInstanceUID LIKE @StudyUID");
            if (!string.IsNullOrEmpty(status)) sql.Append(" AND Status = @Status");
            if (date.HasValue) sql.Append(" AND DATE(CreateTime) = DATE(@Date)");
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

    public async Task<PrintJob?> GetPrintJobAsync(string jobId)
    {
        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<PrintJob>(
            "SELECT * FROM PrintJobs WHERE JobId = @JobId", new { JobId = jobId });
    }

    public async Task<bool> DeletePrintJobAsync(string jobId)
    {
        try
        {
            using var connection = CreateConnection();
            return await connection.ExecuteAsync("DELETE FROM PrintJobs WHERE JobId = @JobId", new { JobId = jobId }) > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "删除打印任务失败 - JobId: {JobId}", jobId);
            throw;
        }
    }

    public Task<PrintJob?> GetPrintJobByIdAsync(string jobId) => GetPrintJobAsync(jobId);

    public PrinterConfig? GetPrinterByName(string printerName)
    {
        var printers = _settings.PrintSCU?.Printers;
        return printers?.FirstOrDefault(p => p.Name == printerName);
    }

    public List<PrinterConfig> GetPrinters() => _settings.PrintSCU?.Printers ?? [];
}
