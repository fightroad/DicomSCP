using Dapper;
using DicomSCP.Models;
using Microsoft.Data.Sqlite;

namespace DicomSCP.Repository;

/// <summary>
/// 检查（Study）基础信息更新命令。
/// </summary>
public sealed class StudyBasicInfoRepository(IConfiguration configuration) : BaseRepository(configuration.GetConnectionString("DicomDb") ?? throw new ArgumentException("Missing DicomDb connection string"))
{
    public async Task DeleteStudyAsync(string studyInstanceUid)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var instanceCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Instances WHERE SeriesInstanceUid IN (SELECT SeriesInstanceUid FROM Series WHERE StudyInstanceUid = @StudyInstanceUid)",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );

            await connection.ExecuteAsync(
                "DELETE FROM Instances WHERE SeriesInstanceUid IN (SELECT SeriesInstanceUid FROM Series WHERE StudyInstanceUid = @StudyInstanceUid)",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );

            await connection.ExecuteAsync(
                "DELETE FROM Series WHERE StudyInstanceUid = @StudyInstanceUid",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );

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

    public async Task<IEnumerable<Series>> GetSeriesByStudyUidAsync(string studyUid)
    {
        await using var connection = new SqliteConnection(_connectionString);
        var sql = @"
            SELECT s.*, 
                   (SELECT COUNT(*) FROM Instances i WHERE i.SeriesInstanceUid = s.SeriesInstanceUid) as NumberOfInstances,
                   (SELECT Modality FROM Studies WHERE StudyInstanceUid = s.StudyInstanceUid) as StudyModality
            FROM Series s
            WHERE s.StudyInstanceUid = @StudyUid
            ORDER BY CAST(s.SeriesNumber as INTEGER)";

        return await connection.QueryAsync<Series>(sql, new { StudyUid = studyUid });
    }

    public async Task<PagedResult<StudyInfo>> GetStudiesAsync(
        int page,
        int pageSize,
        string? patientId = null,
        string? patientName = null,
        string? accessionNumber = null,
        string? keyword = null,
        string? modality = null,
        DateTime? startDate = null,
        DateTime? endDate = null)
    {
        var sql = new System.Text.StringBuilder(@"
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
            parameters.Add("@PatientId", $"%{patientId.Trim()}%");
        }

        if (!string.IsNullOrEmpty(patientName))
        {
            sql.Append(" AND p.PatientName LIKE @PatientName");
            parameters.Add("@PatientName", $"%{patientName.Trim()}%");
        }

        if (!string.IsNullOrEmpty(accessionNumber))
        {
            sql.Append(" AND s.AccessionNumber LIKE @AccessionNumber");
            parameters.Add("@AccessionNumber", $"%{accessionNumber.Trim()}%");
        }

        if (!string.IsNullOrEmpty(keyword))
        {
            sql.Append(" AND s.Remark LIKE @Keyword");
            parameters.Add("@Keyword", $"%{keyword.Trim()}%");
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

        sql.Append(" ORDER BY s.StudyDate DESC, s.StudyTime DESC");
        sql.Append(" LIMIT @PageSize OFFSET @Offset");

        parameters.Add("@PageSize", pageSize);
        parameters.Add("@Offset", (page - 1) * pageSize);

        await using var connection = new SqliteConnection(_connectionString);
        var totalCount = await connection.ExecuteScalarAsync<int>(countSql, parameters);
        var items = await connection.QueryAsync<StudyInfo>(sql.ToString(), parameters);

        return new PagedResult<StudyInfo>
        {
            Items = [.. items],
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<bool> UpdateStudyBasicInfoAsync(string studyInstanceUid, StudyUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(studyInstanceUid))
        {
            throw new ArgumentException("StudyInstanceUid is required", nameof(studyInstanceUid));
        }

        var hasStudyUpdates =
            request.StudyDate != null ||
            request.StudyDescription != null ||
            request.AccessionNumber != null ||
            request.InstitutionName != null ||
            request.Remark != null;

        var hasPatientUpdates =
            request.PatientName != null ||
            request.PatientSex != null ||
            request.PatientBirthDate != null;

        if (!hasStudyUpdates && !hasPatientUpdates)
        {
            return false;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var currentPatientId = await connection.ExecuteScalarAsync<string?>(
                "SELECT PatientId FROM Studies WHERE StudyInstanceUid = @StudyInstanceUid",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction);
            var targetPatientId = currentPatientId;

            if (hasStudyUpdates)
            {
                var updates = new List<string>();
                var parameters = new DynamicParameters();
                parameters.Add("@StudyInstanceUid", studyInstanceUid);

                if (request.StudyDate != null)
                {
                    updates.Add("StudyDate = @StudyDate");
                    parameters.Add("@StudyDate", request.StudyDate);
                }
                if (request.StudyDescription != null)
                {
                    updates.Add("StudyDescription = @StudyDescription");
                    parameters.Add("@StudyDescription", request.StudyDescription);
                }
                if (request.AccessionNumber != null)
                {
                    updates.Add("AccessionNumber = @AccessionNumber");
                    parameters.Add("@AccessionNumber", request.AccessionNumber);
                }
                if (request.InstitutionName != null)
                {
                    updates.Add("InstitutionName = @InstitutionName");
                    parameters.Add("@InstitutionName", request.InstitutionName);
                }
                if (request.Remark != null)
                {
                    updates.Add("Remark = @Remark");
                    parameters.Add("@Remark", request.Remark);
                }

                if (updates.Count > 0)
                {
                    var sql = $"UPDATE Studies SET {string.Join(", ", updates)} WHERE StudyInstanceUid = @StudyInstanceUid";
                    await connection.ExecuteAsync(sql, parameters, transaction);
                }
            }

            if (hasPatientUpdates && !string.IsNullOrWhiteSpace(targetPatientId))
            {
                var pUpdates = new List<string>();
                var pParams = new DynamicParameters();
                pParams.Add("@PatientId", targetPatientId);

                if (request.PatientName != null)
                {
                    pUpdates.Add("PatientName = @PatientName");
                    pParams.Add("@PatientName", request.PatientName);
                }
                if (request.PatientSex != null)
                {
                    pUpdates.Add("PatientSex = @PatientSex");
                    pParams.Add("@PatientSex", request.PatientSex);
                }
                if (request.PatientBirthDate != null)
                {
                    pUpdates.Add("PatientBirthDate = @PatientBirthDate");
                    pParams.Add("@PatientBirthDate", request.PatientBirthDate);
                }

                if (pUpdates.Count > 0)
                {
                    var pSql = $"UPDATE Patients SET {string.Join(", ", pUpdates)} WHERE PatientId = @PatientId";
                    await connection.ExecuteAsync(pSql, pParams, transaction);
                }
            }

            await transaction.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            LogError(ex, "更新检查基本信息失败 - StudyInstanceUid: {StudyInstanceUid}", studyInstanceUid);
            throw;
        }
    }
}

