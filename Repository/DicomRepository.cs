using Dapper;
using DicomSCP.Models;
using Microsoft.Data.Sqlite;

namespace DicomSCP.Repository;

public class DicomRepository(IConfiguration configuration)
    : BaseRepository(configuration.GetConnectionString("DicomDb") ?? throw new ArgumentException("Missing DicomDb connection string"))
{

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
            return [];
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
            return [];
        }
    }

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
                Modalities = (modalities?.Length ?? 0) > 0 ? modalities : [""],
                StudyInstanceUid = studyInstanceUid ?? "",
                Offset = offset,
                Limit = limit
            };

            LogDebug("执行检查查询 - SQL: {Sql}, 参数: {@Parameters}", sql, parameters);

            // 获取总数
            var totalCount = connection.ExecuteScalar<int>(countSql, parameters);

            var studies = connection.Query<Study>(sql, parameters);
            var result = studies?.ToList() ?? [];
            
            LogInformation("检查查询完成 - 返回记录数: {Count}/{Total}, 日期范围: {StartDate} - {EndDate}, StudyInstanceUID: {StudyUID}", 
                result.Count, totalCount, dateRange.StartDate ?? "", dateRange.EndDate ?? "", studyInstanceUid ?? "");

            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "检查查询失败");
            return [];
        }
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