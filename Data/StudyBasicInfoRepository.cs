using Dapper;
using DicomSCP.Models;
using Microsoft.Data.Sqlite;

namespace DicomSCP.Data;

/// <summary>
/// 检查（Study）基础信息更新命令。
/// </summary>
public sealed class StudyBasicInfoRepository : BaseRepository
{
    public StudyBasicInfoRepository(IConfiguration configuration, ILogger<StudyBasicInfoRepository> logger)
        : base(configuration.GetConnectionString("DicomDb") ?? throw new ArgumentException("Missing DicomDb connection string"), logger)
    {
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

