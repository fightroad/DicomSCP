using Microsoft.AspNetCore.Mvc;
using DicomSCP.Models;
using DicomSCP.Repository;
using DicomSCP.Services;
using FellowOakDicom;
using FellowOakDicom.Imaging.Codec;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImagesController : ControllerBase
{
    private readonly DicomRepository _repository;
    private readonly StudyBasicInfoRepository _studyBasicInfoRepository;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public ImagesController(
        DicomRepository repository,
        StudyBasicInfoRepository studyBasicInfoRepository,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        _repository = repository;
        _studyBasicInfoRepository = studyBasicInfoRepository;
        _configuration = configuration;
        _environment = environment;

        // 验证必要的配置是否存在
        var storagePath = configuration["DicomSettings:StoragePath"];
        if (string.IsNullOrEmpty(storagePath))
        {
            throw new InvalidOperationException("DicomSettings:StoragePath must be configured in appsettings.json");
        }
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<StudyInfo>>> GetStudies(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? patientId = null,
        [FromQuery] string? patientName = null,
        [FromQuery] string? accessionNumber = null,
        [FromQuery] string? keyword = null,
        [FromQuery] string? modality = null,
        [FromQuery] string? studyDate = null)
    {
        try
        {
            // 解析日期
            DateTime? searchDate = null;
            if (!string.IsNullOrEmpty(studyDate))
            {
                if (DateTime.TryParse(studyDate, out DateTime date))
                {
                    searchDate = date;
                }
            }

            var result = await _studyBasicInfoRepository.GetStudiesAsync(
                page, 
                pageSize, 
                patientId, 
                patientName, 
                accessionNumber, 
                keyword,
                modality, 
                searchDate,    // 开始时间
                searchDate?.AddDays(1).AddSeconds(-1)  // 结束时间设为当天最后一秒
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 获取影像列表失败");
            return StatusCode(500, "获取数据失败");
        }
    }

    [HttpGet("{studyUid}/series")]
    public async Task<ActionResult<IEnumerable<SeriesInfo>>> GetSeries(string studyUid)
    {
        try
        {
            var seriesList = await _studyBasicInfoRepository.GetSeriesByStudyUidAsync(studyUid);
            var result = seriesList.Select(series => new SeriesInfo
            {
                SeriesInstanceUid = series.SeriesInstanceUid,
                SeriesNumber = series.SeriesNumber ?? "",
                Modality = series.StudyModality ?? series.Modality ?? "",
                SeriesDescription = series.SeriesDescription ?? "",
                NumberOfInstances = series.NumberOfInstances
            });

            return Ok(result);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 获取序列列表失败 - StudyUid: {StudyUid}", studyUid);
            return StatusCode(500, "获取数据失败");
        }
    }

    [HttpDelete("{studyInstanceUid}")]
    public async Task<IActionResult> Delete(string studyInstanceUid)
    {
        if (string.IsNullOrEmpty(studyInstanceUid))
        {
            return BadRequest("StudyInstanceUID is required");
        }

        try
        {
            // 1. 获取检查信息
            var study = await _repository.GetStudyAsync(studyInstanceUid);
            if (study == null)
            {
                return NotFound("检查不存在");
            }

            // 2. 删除文件系统中的文件
            var storagePath = _configuration["DicomSettings:StoragePath"] 
                ?? throw new InvalidOperationException("DicomSettings:StoragePath is not configured");

            // 构建检查目录路径，处理 StudyDate 为 null 的情况
            var studyPath = string.IsNullOrEmpty(study.StudyDate)
                ? Path.Combine(storagePath, studyInstanceUid)  // 如果没有日期，直接用检查UID
                : Path.Combine(storagePath, study.StudyDate.Substring(0, 4), 
                    study.StudyDate.Substring(4, 2), 
                    study.StudyDate.Substring(6, 2), 
                    studyInstanceUid);  // 按年/月/日/检查UID组织

            if (Directory.Exists(studyPath))
            {
                try
                {
                    // 递归删除目录及其内容
                    Directory.Delete(studyPath, true);
                    DicomLogger.Information("Api", "删除检查目录成功 - 路径: {Path}", studyPath);

                    // 3. 删除数据库记录
                    await _studyBasicInfoRepository.DeleteStudyAsync(studyInstanceUid);

                    return Ok(new { message = "删除成功" });
                }
                catch (Exception ex)
                {
                    DicomLogger.Error("Api", ex, "删除检查目录失败 - 路径: {Path}", studyPath);
                    return StatusCode(500, new { error = "删除文件失败，请重试" });
                }
            }
            else
            {
                // 如果目录不存在，只删除数据库记录
                await _studyBasicInfoRepository.DeleteStudyAsync(studyInstanceUid);
                DicomLogger.Warning("Api", "检查目录不存在 - 路径: {Path}", studyPath);
                return Ok(new { message = "删除成功" });
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 删除检查失败 - StudyUID: {StudyUID}", studyInstanceUid);
            return StatusCode(500, new { error = "删除失败，请重试" });
        }
    }

    [HttpPut("{studyInstanceUid}")]
    public async Task<IActionResult> UpdateStudy(string studyInstanceUid, [FromBody] StudyUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(studyInstanceUid))
        {
            return BadRequest("StudyInstanceUID is required");
        }

        try
        {
            var ok = await _studyBasicInfoRepository.UpdateStudyBasicInfoAsync(studyInstanceUid, request);
            if (!ok)
            {
                return BadRequest("No fields to update");
            }

            return Ok(new { Message = "更新成功" });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 更新检查信息失败 - StudyInstanceUID: {StudyInstanceUid}", studyInstanceUid);
            return StatusCode(500, "更新失败");
        }
    }

    [HttpGet("{studyUid}/series/{seriesUid}/instances")]
    public async Task<ActionResult<IEnumerable<object>>> GetSeriesInstances(string studyUid, string seriesUid)
    {
        try
        {
            var instances = await _repository.GetSeriesInstancesAsync(seriesUid);
            var result = instances.Select(instance => new
            {
                sopInstanceUid = instance.SopInstanceUid,
                instanceNumber = instance.InstanceNumber
            });
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 获取序列实例失败");
            return StatusCode(500, "获取序列实例失败");
        }
    }

    private string GetStoragePath(string configPath)
    {
        // 如果是绝对路径，直接返回
        if (Path.IsPathRooted(configPath))
        {
            return configPath;
        }

        // 使用 ContentRootPath 作为基准路径
        return Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configPath));
    }

    [HttpGet("download/{instanceUid}")]
    public async Task<IActionResult> DownloadInstance(string instanceUid, [FromQuery] string? transferSyntax)
    {
        try
        {
            var instance = await _repository.GetInstanceAsync(instanceUid);
            if (instance == null)
            {
                DicomLogger.Warning("Api", "[API] 实例不存在 - InstanceUid: {InstanceUid}", instanceUid);
                return NotFound("实例不存在");
            }

            // 从配置获取存储根路径
            var configPath = _configuration["DicomSettings:StoragePath"] 
                ?? throw new InvalidOperationException("DicomSettings:StoragePath is not configured");
            
            // 处理存储路径
            var storagePath = GetStoragePath(configPath);
            DicomLogger.Debug("Api", "存储路径解析 - 配置路径: {ConfigPath}, 实际路径: {StoragePath}", 
                configPath, storagePath);

            // 拼接完整的文件路径并规范化
            var fullPath = Path.GetFullPath(Path.Combine(storagePath, instance.FilePath));

            // 添加路径安全检查
            if (!fullPath.StartsWith(storagePath))
            {
                DicomLogger.Error("Api", null,
                    "[API] 非法的文件路径 - InstanceUid: {InstanceUid}, StoragePath: {StoragePath}, FullPath: {FullPath}", 
                    instanceUid,
                    storagePath,
                    fullPath);
                return BadRequest("非法的文件路径");
            }

            if (!System.IO.File.Exists(fullPath))
            {
                DicomLogger.Error("Api", null,
                    "[API] 文件不存在 - InstanceUid: {InstanceUid}, StoragePath: {StoragePath}, FullPath: {FullPath}", 
                    instanceUid,
                    storagePath,
                    fullPath);
                return NotFound("图像文件不存在");
            }

            // 读取DICOM文件
            var file = await DicomFile.OpenAsync(fullPath);

            // 如果指定了传输语法，进行转码
            if (!string.IsNullOrEmpty(transferSyntax))
            {
                var currentSyntax = file.Dataset.InternalTransferSyntax;
                var requestedSyntax = GetRequestedTransferSyntax(transferSyntax);

                if (currentSyntax != requestedSyntax)
                {
                    try
                    {
                        DicomLogger.Information("Api", 
                            "[API] 开始转码 - InstanceUid: {InstanceUid}, 原格式: {Original}, 目标格式: {Target}",
                            instanceUid,
                            currentSyntax.UID.Name,
                            requestedSyntax.UID.Name);

                        var transcoder = new DicomTranscoder(currentSyntax, requestedSyntax);
                        file = transcoder.Transcode(file);

                        DicomLogger.Information("Api", "[API] 转码完成");
                    }
                    catch (Exception ex)
                    {
                        DicomLogger.Error("Api", ex, 
                            "[API] 转码失败 - InstanceUid: {InstanceUid}, 原格式: {Original}, 目标格式: {Target}",
                            instanceUid,
                            currentSyntax.UID.Name,
                            requestedSyntax.UID.Name);
                        // 转码失败时使用原始文件
                    }
                }
            }

            // 构造文件名
            var fileName = $"{instance.SopInstanceUid}.dcm";

            // 准备内存流
            var memoryStream = new MemoryStream();
            await file.SaveAsync(memoryStream);
            memoryStream.Position = 0;

            // 设置响应头
            Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{fileName}\"");
            Response.Headers.Append("X-Transfer-Syntax", file.Dataset.InternalTransferSyntax.UID.Name);
            
            return File(memoryStream, "application/dicom");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 下载文件失败 - InstanceUid: {InstanceUid}", instanceUid);
            return StatusCode(500, "下载文件失败");
        }
    }

    private DicomTransferSyntax GetRequestedTransferSyntax(string syntax)
    {
        return syntax.ToLower() switch
        {
            "jpeg" => DicomTransferSyntax.JPEGProcess14SV1,
            "jpeg2000" => DicomTransferSyntax.JPEG2000Lossless,
            "jpegls" => DicomTransferSyntax.JPEGLSLossless,
            "rle" => DicomTransferSyntax.RLELossless,
            "explicit" => DicomTransferSyntax.ExplicitVRLittleEndian,
            "implicit" => DicomTransferSyntax.ImplicitVRLittleEndian,
            _ => DicomTransferSyntax.ExplicitVRLittleEndian
        };
    }
} 