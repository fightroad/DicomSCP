using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using DicomSCP.Repository;
using System.Text.Json;
using DicomSCP.Services;
using DicomSCP.Models;

namespace DicomSCP.Controllers
{
    [ApiController]
    [Route("dicomweb")]
    [AllowAnonymous]
    public class QidoRsController(DicomRepository repository) : ControllerBase
    {
        private readonly DicomRepository _repository = repository;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = null,
            WriteIndented = true
        };

        // QIDO-RS: 查询研究
        [HttpGet("studies")]
        [Produces("application/dicom+json")]
        public async Task<IActionResult> SearchStudies(
            [FromQuery] string? PatientID = null, 
            [FromQuery(Name = "00100020")] string? patientId = null,
            [FromQuery] string? PatientName = null, 
            [FromQuery(Name = "00100010")] string? patientNameTag = null,
            [FromQuery] string? StudyDate = null, 
            [FromQuery(Name = "00080020")] string? studyDateTag = null,
            [FromQuery] string? StudyInstanceUID = null, 
            [FromQuery(Name = "0020000D")] string? studyInstanceUidTag = null,
            [FromQuery] string? AccessionNumber = null, 
            [FromQuery(Name = "00080050")] string? accessionNumberTag = null,
            [FromQuery] string? ModalitiesInStudy = null, 
            [FromQuery(Name = "00080061")] string? modalitiesInStudyTag = null,
            [FromQuery] bool fuzzymatching = false,
            [FromQuery] int offset = 0,
            [FromQuery] int limit = 100)
        {
            try
            {
                // 记录查询参数
                DicomLogger.Information("WADO", "DICOMweb - QIDO-RS 查询研究 - 参数: PatientID={PatientID}, PatientName={PatientName}, " +
                    "StudyDate={StudyDate}, StudyUID={StudyUID}, AccessionNumber={AccessionNumber}, Modalities={Modalities}, offset={Offset}, limit={Limit}",
                    patientId ?? "", patientNameTag ?? "", studyDateTag ?? "", studyInstanceUidTag ?? "", 
                    accessionNumberTag ?? "", modalitiesInStudyTag ?? "", offset, limit);

                // 使用第一个非空值
                var finalPatientId = patientId ?? PatientID;
                var finalPatientName = patientNameTag ?? PatientName;
                var finalStudyDate = studyDateTag ?? StudyDate;
                var finalStudyInstanceUid = studyInstanceUidTag ?? StudyInstanceUID;
                var finalAccessionNumber = accessionNumberTag ?? AccessionNumber;
                var finalModalitiesInStudy = modalitiesInStudyTag ?? ModalitiesInStudy;

                // 如果启用模糊匹配，将 DICOM 通配符转换为 SQL 通配符
                if (fuzzymatching)
                {
                    if (!string.IsNullOrEmpty(finalPatientId))
                    {
                        finalPatientId = finalPatientId.Replace('*', '%').Replace('?', '_');
                    }
                    if (!string.IsNullOrEmpty(finalPatientName))
                    {
                        finalPatientName = finalPatientName.Replace('*', '%').Replace('?', '_');
                    }
                    if (!string.IsNullOrEmpty(finalAccessionNumber))
                    {
                        finalAccessionNumber = finalAccessionNumber.Replace('*', '%').Replace('?', '_');
                    }
                }

                // 构建查询请求
                var request = new QueryRequest
                {
                    PatientId = finalPatientId,
                    PatientName = finalPatientName,
                    StudyDate = finalStudyDate,
                    StudyInstanceUid = finalStudyInstanceUid,
                    AccessionNumber = finalAccessionNumber,
                    Modality = finalModalitiesInStudy
                };

                // 使用 GetStudies 查询，传入分页参数
                var studies = await Task.FromResult(_repository.GetStudies(
                    request.PatientId ?? "",
                    request.PatientName ?? "",
                    request.AccessionNumber ?? "",
                    (GetStartDate(request.StudyDate)?.ToString("yyyyMMdd") ?? "", 
                     GetEndDate(request.StudyDate)?.ToString("yyyyMMdd") ?? ""),
                    !string.IsNullOrEmpty(request.Modality) ? request.Modality.Split(',') : null,
                    request.StudyInstanceUid,
                    offset,  // 添加偏移量
                    limit)); // 添加每页数量

                // 记录查询结果
                DicomLogger.Information("WADO", "DICOMweb - QIDO-RS 查询研究 - 返回记录数: {Count}, 日期范围: {StartDate} - {EndDate}",
                    studies.Count,
                    GetStartDate(request.StudyDate)?.ToString("yyyyMMdd") ?? "",
                    GetEndDate(request.StudyDate)?.ToString("yyyyMMdd") ?? "");

                // 如果当前页数据量等于 limit，说明可能还有下一页
                if (studies.Count >= limit)
                {
                    var nextLink = $"<{Request.Path}?offset={offset + limit}&limit={limit}>; rel=\"next\"";
                    Response.Headers.Append("Link", nextLink);
                }

                // 转换为 DICOM JSON 格式
                var result = studies.Select(s => new Dictionary<string, object>
                {
                    { "00080020", new { vr = "DA", Value = new[] { s.StudyDate } } },
                    { "00080030", new { vr = "TM", Value = new[] { s.StudyTime } } },
                    { "00080050", new { vr = "SH", Value = new[] { s.AccessionNumber } } },
                    { "00080061", new { vr = "CS", Value = s.Modality?.Split('\\') } },
                    { "00081030", new { vr = "LO", Value = new[] { s.StudyDescription } } },
                    { "00100010", new { vr = "PN", Value = new[] { s.PatientName } } },
                    { "00100020", new { vr = "LO", Value = new[] { s.PatientId } } },
                    { "00100030", new { vr = "DA", Value = new[] { s.PatientBirthDate } } },
                    { "00100040", new { vr = "CS", Value = new[] { s.PatientSex } } },
                    { "0020000D", new { vr = "UI", Value = new[] { s.StudyInstanceUid } } },
                    { "00201206", new { vr = "IS", Value = new[] { s.NumberOfStudyRelatedSeries.ToString() } } },
                    { "00201208", new { vr = "IS", Value = new[] { s.NumberOfStudyRelatedInstances.ToString() } } },
                    { "00080080", new { vr = "LO", Value = new[] { s.InstitutionName } } }
                });

                return new JsonResult(result, _jsonOptions);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - QIDO-RS 查询研究失败");
                return StatusCode(500, "Error searching studies");
            }
        }

        // QIDO-RS: 查询序列
        [HttpGet("studies/{StudyInstanceUID}/series")]
        [Produces("application/dicom+json")]
        public async Task<IActionResult> SearchSeries(
            string StudyInstanceUID,
            [FromQuery(Name = "SeriesInstanceUID")] string? SeriesInstanceUID = null,
            [FromQuery(Name = "Modality")] string? Modality = null)
        {
            try
            {
                var series = await _repository.GetSeriesAsync(StudyInstanceUID);
                
                if (!string.IsNullOrEmpty(SeriesInstanceUID))
                {
                    series = series.Where(s => s.SeriesInstanceUid == SeriesInstanceUID);
                }

                if (!string.IsNullOrEmpty(Modality))
                {
                    series = series.Where(s => s.Modality == Modality);
                }

                var result = series.Select(s => new Dictionary<string, object>
                {
                    { "00080060", new { vr = "CS", Value = new[] { s.Modality } } },
                    { "0020000E", new { vr = "UI", Value = new[] { s.SeriesInstanceUid } } },
                    { "00200011", new { vr = "IS", Value = new[] { s.SeriesNumber } } },
                    { "0008103E", new { vr = "LO", Value = new[] { s.SeriesDescription } } },
                    { "00080021", new { vr = "DA", Value = new[] { s.SeriesDate } } },
                    { "00080031", new { vr = "TM", Value = new[] { "000000" } } },
                    { "00201209", new { vr = "IS", Value = new[] { s.NumberOfInstances.ToString() } } },
                    { "00081190", new { vr = "UR", Value = new[] { 
                        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/dicomweb/studies/{StudyInstanceUID}/series/{s.SeriesInstanceUid}" 
                    }}}
                }).ToList();

                return new JsonResult(result, _jsonOptions);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - QIDO-RS 查询序列失败");
                return StatusCode(500, "Error searching series");
            }
        }

        // QIDO-RS: 查询实例
        [HttpGet("studies/{StudyInstanceUID}/series/{SeriesInstanceUID}/instances")]
        [Produces("application/dicom+json")]
        public async Task<IActionResult> SearchInstances(
            string StudyInstanceUID,
            string SeriesInstanceUID,
            [FromQuery(Name = "SOPInstanceUID")] string? SOPInstanceUID = null)
        {
            try
            {
                var instances = await Task.FromResult(_repository.GetInstancesBySeriesUid(StudyInstanceUID, SeriesInstanceUID).ToList());
                
                if (!string.IsNullOrEmpty(SOPInstanceUID))
                {
                    instances = instances.Where(i => i.SopInstanceUid == SOPInstanceUID).ToList();
                }

                if (!instances.Any())
                {
                    return NotFound();
                }

                var result = instances.Select(i => new Dictionary<string, object>
                {
                    { "00080016", new { vr = "UI", Value = new[] { i.SopClassUid } } },
                    { "00080018", new { vr = "UI", Value = new[] { i.SopInstanceUid } } },
                    { "00200013", new { vr = "IS", Value = new[] { int.Parse(i.InstanceNumber ?? "0") } } },
                    { "00280010", new { vr = "US", Value = new[] { i.Rows } } },
                    { "00280011", new { vr = "US", Value = new[] { i.Columns } } },
                    { "00280004", new { vr = "CS", Value = new[] { i.PhotometricInterpretation ?? "" } } },
                    { "00280100", new { vr = "US", Value = new[] { i.BitsAllocated } } },
                    { "00280101", new { vr = "US", Value = new[] { i.BitsStored } } },
                    { "00280102", new { vr = "US", Value = new[] { i.HighBit } } },
                    { "00280103", new { vr = "US", Value = new[] { i.PixelRepresentation } } },
                    { "00280002", new { vr = "US", Value = new[] { i.SamplesPerPixel } } },
                    { "00280030", new { vr = "DS", Value = new[] { i.PixelSpacing ?? "" } } },
                    { "00200037", new { vr = "DS", Value = new[] { i.ImageOrientationPatient ?? "" } } },
                    { "00200032", new { vr = "DS", Value = new[] { i.ImagePositionPatient ?? "" } } },
                    { "00200052", new { vr = "UI", Value = new[] { i.FrameOfReferenceUID ?? "" } } },
                    { "00080008", new { vr = "CS", Value = i.ImageType?.Split('\\') ?? new[] { "" } } },
                    { "00281050", new { vr = "DS", Value = new[] { i.WindowCenter ?? "" } } },
                    { "00281051", new { vr = "DS", Value = new[] { i.WindowWidth ?? "" } } },
                    { "00081190", new { vr = "UR", Value = new[] { 
                        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/dicomweb/studies/{StudyInstanceUID}/series/{SeriesInstanceUID}/instances/{i.SopInstanceUid}" 
                    }}}
                }).ToList();

                return new JsonResult(result, _jsonOptions);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - QIDO-RS 查询实例失败");
                return StatusCode(500, "Error searching instances");
            }
        }

        private DateTime? GetStartDate(string? dateRange)
        {
            if (string.IsNullOrEmpty(dateRange))
                return null;

            if (dateRange.Contains("-"))
            {
                var parts = dateRange.Split('-');
                if (parts.Length == 2 && !string.IsNullOrEmpty(parts[0]))
                {
                    if (DateTime.TryParseExact(parts[0], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime startDate))
                    {
                        return startDate;
                    }
                }
                return DateTime.MinValue;
            }

            if (DateTime.TryParseExact(dateRange, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime date))
            {
                return date;
            }

            return null;
        }

        private DateTime? GetEndDate(string? dateRange)
        {
            if (string.IsNullOrEmpty(dateRange))
                return null;

            if (dateRange.Contains("-"))
            {
                var parts = dateRange.Split('-');
                if (parts.Length == 2 && !string.IsNullOrEmpty(parts[1]))
                {
                    if (DateTime.TryParseExact(parts[1], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime endDate))
                    {
                        return endDate;
                    }
                }
                return DateTime.MaxValue;
            }

            if (DateTime.TryParseExact(dateRange, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime date))
            {
                return date;
            }

            return null;
        }
    }
} 