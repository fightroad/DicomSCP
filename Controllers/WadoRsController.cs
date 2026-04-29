using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using DicomSCP.Repository;
using DicomSCP.Configuration;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using DicomSCP.Services;

namespace DicomSCP.Controllers
{
    [ApiController]
    [Route("dicomweb")]
    [AllowAnonymous]
    public class WadoRsController : ControllerBase
    {
        private readonly DicomRepository _repository;
        private readonly DicomSettings _settings;
        private const string AppDicomContentType = "application/dicom";
        private const string JpegImageContentType = "image/jpeg";

        public WadoRsController(DicomRepository repository, IOptions<DicomSettings> settings)
        {
            _repository = repository;
            _settings = settings.Value;
        }

        #region WADO-RS 检索接口

        // WADO-RS: 检索DICOM实例
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}")]
        [Produces("multipart/related", "application/dicom")]
        public async Task<IActionResult> RetrieveDicomInstance(
            string studyInstanceUid,
            string seriesInstanceUid,
            string sopInstanceUid)
        {
            try
            {
                // 记录请求信息
                var acceptHeader = Request.Headers["Accept"].ToString();
                DicomLogger.Information("WADO", "DICOMweb - 收到实例检索请求 - StudyUID: {StudyUID}, SeriesUID: {SeriesUID}, SopUID: {SopUID}, Accept: {Accept}",
                    studyInstanceUid, seriesInstanceUid, sopInstanceUid, acceptHeader);

                // 获取实例信息
                var instances = await Task.FromResult(_repository.GetInstancesByStudyUid(studyInstanceUid));
                var instance = instances.FirstOrDefault(i => 
                    i.SeriesInstanceUid == seriesInstanceUid && 
                    i.SopInstanceUid == sopInstanceUid);

                if (instance == null)
                {
                    DicomLogger.Warning("WADO", "DICOMweb - 未找到实例: {SopInstanceUid}", sopInstanceUid);
                    return NotFound("Instance not found");
                }

                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    DicomLogger.Error("WADO", "DICOMweb - DICOM文件不存在: {FilePath}", filePath);
                    return NotFound("DICOM file not found");
                }

                // 读取DICOM文件
                var dicomFile = await DicomFile.OpenAsync(filePath);
                var currentTransferSyntax = dicomFile.FileMetaInfo.TransferSyntax;

                // 解析 Accept 头
                var acceptParts = acceptHeader.Split(';').Select(p => p.Trim()).ToList();
                var mediaType = acceptParts.First().Trim();
                var transferSyntaxPart = acceptParts.FirstOrDefault(p => p.StartsWith("transfer-syntax=", StringComparison.OrdinalIgnoreCase));
                var typePart = acceptParts.FirstOrDefault(p => p.StartsWith("type=", StringComparison.OrdinalIgnoreCase));

                // 确定媒体类型
                if (mediaType == "*/*" || string.IsNullOrEmpty(mediaType))
                {
                    mediaType = AppDicomContentType;
                }

                // 从 multipart/related 中提取实际的媒体类型
                if (mediaType == "multipart/related" && typePart != null)
                {
                    var type = typePart.Split('=')[1].Trim('"', ' ');
                    mediaType = type;
                }

                // 确定传输语法
                DicomTransferSyntax targetTransferSyntax;
                if (transferSyntaxPart != null)
                {
                    var requestedSyntax = transferSyntaxPart.Split('=')[1].Trim('"', ' ');
                    if (requestedSyntax == "*")
                    {
                        // 使用原始传输语法
                        targetTransferSyntax = currentTransferSyntax;
                        DicomLogger.Information("WADO", "DICOMweb - 使用原始传输语法: {TransferSyntax}", 
                            currentTransferSyntax.UID.Name);
                    }
                    else
                    {
                        targetTransferSyntax = DicomTransferSyntax.Parse(requestedSyntax);
                        DicomLogger.Information("WADO", "DICOMweb - 使用请求的传输语法: {TransferSyntax}", 
                            targetTransferSyntax.UID.Name);
                    }
                }
                else
                {
                    // 默认使用显式 VR 小端 (1.2.840.10008.1.2.1)
                    targetTransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;
                    DicomLogger.Information("WADO", "DICOMweb - 使用默认传输语法: ExplicitVRLittleEndian");
                }

                // 如果需要转换传输语法
                if (currentTransferSyntax != targetTransferSyntax)
                {
                    DicomLogger.Information("WADO", 
                        "DICOMweb - 传输语法转换 - 从: {CurrentSyntax} 到: {TargetSyntax}",
                        currentTransferSyntax.UID.Name,
                        targetTransferSyntax.UID.Name);

                    var transcoder = new DicomTranscoder(currentTransferSyntax, targetTransferSyntax);
                    dicomFile = transcoder.Transcode(dicomFile);
                }

                // 将 DICOM 文件保存到内存流
                using var memoryStream = new MemoryStream();
                await dicomFile.SaveAsync(memoryStream);
                var dicomBytes = memoryStream.ToArray();

                // 如果请求单个 DICOM 文件
                if (mediaType == AppDicomContentType && !acceptHeader.Contains("multipart/related"))
                {
                    Response.Headers["Content-Type"] = AppDicomContentType;
                    Response.Headers["Content-Length"] = dicomBytes.Length.ToString();
                    Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;
                    return File(dicomBytes, AppDicomContentType, $"{sopInstanceUid}.dcm");
                }

                // 默认返回 multipart/related 格式
                var boundary = $"boundary_{Guid.NewGuid():N}";
                var responseStream = new MemoryStream();
                var writer = new StreamWriter(responseStream, System.Text.Encoding.UTF8);

                // 写入第一个分隔符
                await writer.WriteLineAsync($"--{boundary}");

                // 写入 MIME 头部
                await writer.WriteLineAsync("Content-Type: application/dicom");
                await writer.WriteLineAsync($"Content-Length: {dicomBytes.Length}");
                await writer.WriteLineAsync($"Content-Location: /dicomweb/studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}");
                await writer.WriteLineAsync($"transfer-syntax: {targetTransferSyntax.UID.UID}");
                await writer.WriteLineAsync();
                await writer.FlushAsync();

                // 写入 DICOM 数据
                await responseStream.WriteAsync(dicomBytes, 0, dicomBytes.Length);

                // 写入结束分隔符
                var endBoundary = $"\r\n--{boundary}--\r\n";
                var endBoundaryBytes = System.Text.Encoding.UTF8.GetBytes(endBoundary);
                await responseStream.WriteAsync(endBoundaryBytes, 0, endBoundaryBytes.Length);

                // 准备返回数据
                responseStream.Position = 0;
                var responseBytes = responseStream.ToArray();

                // 设置响应头
                Response.Headers.Clear();
                Response.Headers["Content-Type"] = $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"";
                Response.Headers["Content-Length"] = responseBytes.Length.ToString();
                Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;

                DicomLogger.Information("WADO", 
                    "DICOMweb - 返回DICOM实例: {SopInstanceUid}, Size: {Size} bytes, TransferSyntax: {TransferSyntax}", 
                    sopInstanceUid ?? string.Empty, responseBytes.Length, targetTransferSyntax.UID.Name ?? string.Empty);

                return File(responseBytes, $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"");
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", "检索DICOM实例失败: {Error}", ex.Message ?? string.Empty);
                return StatusCode(500, "Error retrieving DICOM instance");
            }
        }

        // WADO-RS: 检索帧
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}/frames/{frameNumbers}")]
        [Produces("multipart/related")]
        public async Task<IActionResult> RetrieveFrames(
            string studyInstanceUid,
            string seriesInstanceUid,
            string sopInstanceUid,
            string frameNumbers)
        {
            try
            {
                // 记录请求信息
                var acceptHeader = Request.Headers["Accept"].ToString();
                DicomLogger.Information("WADO", 
                    "DICOMweb - 收到帧检索请求 - StudyUID: {StudyUID}, SeriesUID: {SeriesUID}, SopUID: {SopUID}, Frames: {Frames}, Accept: {Accept}",
                    studyInstanceUid, seriesInstanceUid, sopInstanceUid, frameNumbers, acceptHeader);

                // 验证帧号格式
                if (!frameNumbers.Split(',').All(f => int.TryParse(f, out int n) && n >= 1))
                {
                    return BadRequest("Invalid frame numbers. Frame numbers must be positive integers.");
                }

                // 获取实例
                var instances = await Task.FromResult(_repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid));
                var instance = instances.FirstOrDefault(i => i.SopInstanceUid == sopInstanceUid);
                if (instance == null)
                {
                    return NotFound("Instance not found");
                }

                // 读取 DICOM 文件
                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound("DICOM file not found");
                }

                var dicomFile = await DicomFile.OpenAsync(filePath);
                var dataset = dicomFile.Dataset;

                // 修改这部分逻辑：如果不存在 NumberOfFrames，则认为是单帧图像
                int numberOfFrames = 1;
                if (dataset.Contains(DicomTag.NumberOfFrames))
                {
                    numberOfFrames = dataset.GetSingleValue<int>(DicomTag.NumberOfFrames);
                }

                var requestedFrames = frameNumbers.Split(',').Select(int.Parse).ToList();

                // 验证帧号范围
                if (requestedFrames.Any(f => f < 1 || f > numberOfFrames))
                {
                    return BadRequest($"Frame numbers must be between 1 and {numberOfFrames}");
                }

                // 处理 Accept 头
                var (mediaType, targetTransferSyntax) = GetFrameMediaTypeAndTransferSyntax(
                    acceptHeader, 
                    dicomFile.FileMetaInfo.TransferSyntax);

                // 创建响应
                var boundary = $"boundary.{Guid.NewGuid():N}";
                using var responseStream = new MemoryStream();
                using var writer = new StreamWriter(responseStream, leaveOpen: true);

                // 预处理数据集
                var pixelData = DicomPixelData.Create(dataset);
                var failedFrames = new List<int>();

                // 处理每一帧
                foreach (var frameNumber in requestedFrames)
                {
                    try
                    {
                        var frameData = GetFrameData(dataset, frameNumber, mediaType, targetTransferSyntax);

                        // 写入分隔符和头部
                        await writer.WriteLineAsync($"--{boundary}");
                        await writer.WriteLineAsync($"Content-Type: {mediaType}");
                        await writer.WriteLineAsync($"Content-Length: {frameData.Length}");
                        await writer.WriteLineAsync($"Content-Location: /dicomweb/studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}/frames/{frameNumber}");
                        if (mediaType == "application/octet-stream")
                        {
                            await writer.WriteLineAsync($"transfer-syntax: {targetTransferSyntax.UID.UID}");
                        }
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();

                        // 写入帧数据
                        await responseStream.WriteAsync(frameData, 0, frameData.Length);
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();

                        DicomLogger.Debug("WADO", 
                            "DICOMweb - 已添加帧到响应: Frame={FrameNumber}, Size={Size} bytes", 
                            frameNumber, frameData.Length);
                    }
                    catch (Exception ex)
                    {
                        DicomLogger.Error("WADO", ex, 
                            "DICOMweb - 处理帧失败: Frame={FrameNumber}", frameNumber);
                        failedFrames.Add(frameNumber);
                        continue;
                    }
                }

                // 如果所有帧都失败，返回错误
                if (failedFrames.Count == requestedFrames.Count)
                {
                    return StatusCode(500, "Failed to process all requested frames");
                }

                // 写入结束分隔符
                await writer.WriteLineAsync($"--{boundary}--");
                await writer.FlushAsync();

                // 准备返回数据
                responseStream.Position = 0;
                var responseBytes = responseStream.ToArray();

                // 设置响应头
                Response.Headers.Clear();
                Response.Headers["Content-Type"] = $"multipart/related; type=\"{mediaType}\"; boundary=\"{boundary}\"";
                Response.Headers["Content-Length"] = responseBytes.Length.ToString();
                if (mediaType == "application/octet-stream")
                {
                    Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;
                }
                if (failedFrames.Any())
                {
                    Response.Headers["Warning"] = $"299 {Request.Host} \"Failed to process frames: {string.Join(",", failedFrames)}\"";
                }

                return File(responseBytes, $"multipart/related; type=\"{mediaType}\"; boundary=\"{boundary}\"");
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - 帧检索失败");
                return StatusCode(500, "Error retrieving frames");
            }
        }

        private byte[] GetFrameData(DicomDataset dataset, int frameNumber, string mediaType, DicomTransferSyntax targetTransferSyntax)
        {
            var pixelData = DicomPixelData.Create(dataset);
            var originalFrameData = pixelData.GetFrame(frameNumber - 1).Data.ToArray();

            // 如果请求的是原始传输语法或当前传输语法与目标相同，直接返回
            if (targetTransferSyntax == dataset.InternalTransferSyntax ||
                targetTransferSyntax.UID.UID == "*")
            {
                return originalFrameData;
            }

            // 根据媒体类型和传输语法进行转码
            var transcoder = new DicomTranscoder(
                dataset.InternalTransferSyntax,
                mediaType == "image/jp2" ? DicomTransferSyntax.JPEG2000Lossless : targetTransferSyntax);

            var newDataset = transcoder.Transcode(dataset);
            var newPixelData = DicomPixelData.Create(newDataset);
            return newPixelData.GetFrame(frameNumber - 1).Data.ToArray();
        }

        private (string MediaType, DicomTransferSyntax TransferSyntax) GetFrameMediaTypeAndTransferSyntax(
            string acceptHeader,
            DicomTransferSyntax originalTransferSyntax)
        {
            var mediaType = "application/octet-stream";
            var targetTransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;

            if (string.IsNullOrEmpty(acceptHeader) || acceptHeader == "*/*")
            {
                return (mediaType, targetTransferSyntax);
            }

            var acceptParts = acceptHeader.Split(';').Select(p => p.Trim()).ToList();
            var typePart = acceptParts.FirstOrDefault(p => p.StartsWith("type=", StringComparison.OrdinalIgnoreCase));
            var transferSyntaxPart = acceptParts.FirstOrDefault(p => p.StartsWith("transfer-syntax=", StringComparison.OrdinalIgnoreCase));

            // 处理媒体类型
            if (typePart != null)
            {
                var type = typePart.Split('=')[1].Trim('"', ' ');
                if (type == "image/jp2")
                {
                    mediaType = "image/jp2";
                    targetTransferSyntax = DicomTransferSyntax.JPEG2000Lossless;
                }
            }

            // 处理传输语法
            if (transferSyntaxPart != null)
            {
                var transferSyntax = transferSyntaxPart.Split('=')[1].Trim('"', ' ');
                if (transferSyntax == "*")
                {
                    targetTransferSyntax = originalTransferSyntax;
                }
                else
                {
                    targetTransferSyntax = DicomTransferSyntax.Parse(transferSyntax);
                }
            }
            else if (mediaType == "image/jp2")
            {
                targetTransferSyntax = DicomTransferSyntax.JPEG2000Lossless;
            }

            return (mediaType, targetTransferSyntax);
        }

        // WADO-RS: 检索序列元数据
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/metadata")]
        [Produces("application/dicom+json")]
        public async Task<IActionResult> GetSeriesMetadata(string studyInstanceUid, string seriesInstanceUid)
        {
            // 需要排除的标签
            var excludedTags = new HashSet<DicomTag>
            {
                // 像素数据相关
                DicomTag.PixelData,
                DicomTag.FloatPixelData,
                DicomTag.DoubleFloatPixelData,
                DicomTag.OverlayData,
                DicomTag.EncapsulatedDocument,
                DicomTag.SpectroscopyData
            };

            // 必需包含的标签
            var requiredTags = new HashSet<DicomTag>
            {
                // 文件元信息
                DicomTag.TransferSyntaxUID,
                DicomTag.SpecificCharacterSet,
                
                // Study 级别
                DicomTag.StudyInstanceUID,
                DicomTag.StudyDate,
                DicomTag.StudyTime,
                DicomTag.StudyID,
                DicomTag.AccessionNumber,
                DicomTag.StudyDescription,
                
                // Series 级别
                DicomTag.SeriesInstanceUID,
                DicomTag.SeriesNumber,
                DicomTag.Modality,
                DicomTag.SeriesDescription,
                DicomTag.SeriesDate,
                DicomTag.SeriesTime,
                
                // Instance 级别
                DicomTag.SOPClassUID,
                DicomTag.SOPInstanceUID,
                DicomTag.InstanceNumber,
                DicomTag.ImageType,
                DicomTag.AcquisitionNumber,
                DicomTag.AcquisitionDate,
                DicomTag.AcquisitionTime,
                DicomTag.ContentDate,
                DicomTag.ContentTime,
                
                // 设备信息
                DicomTag.Manufacturer,
                DicomTag.ManufacturerModelName,
                DicomTag.StationName,
                
                // 患者信息
                DicomTag.PatientName,
                DicomTag.PatientID,
                DicomTag.PatientBirthDate,
                DicomTag.PatientSex,
                DicomTag.PatientAge,
                DicomTag.PatientWeight,
                DicomTag.PatientPosition,
                
                // 图像相关
                DicomTag.Rows,
                DicomTag.Columns,
                DicomTag.BitsAllocated,
                DicomTag.BitsStored,
                DicomTag.HighBit,
                DicomTag.PixelRepresentation,
                DicomTag.SamplesPerPixel,
                DicomTag.PhotometricInterpretation,
                DicomTag.PlanarConfiguration,
                DicomTag.RescaleIntercept,
                DicomTag.RescaleSlope,
                
                // 空间信息
                DicomTag.ImageOrientationPatient,
                DicomTag.ImagePositionPatient,
                DicomTag.SliceLocation,
                DicomTag.SliceThickness,
                DicomTag.PixelSpacing,
                DicomTag.WindowCenter,
                DicomTag.WindowWidth,
                DicomTag.WindowCenterWidthExplanation,
                
                // 扫描参数
                DicomTag.KVP,
                DicomTag.ExposureTime,
                DicomTag.XRayTubeCurrent,
                DicomTag.Exposure,
                DicomTag.ConvolutionKernel,
                DicomTag.PatientOrientation,
                DicomTag.ImageComments,
                
                // 多帧相关
                DicomTag.NumberOfFrames,
                DicomTag.FrameIncrementPointer
            };

            try
            {
                // 1. 从数据库获取该序列的所有实例
                var instances = _repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid);
                if (!instances.Any())
                {
                    return NotFound($"未找到序列: {seriesInstanceUid}");
                }

                var metadata = new List<Dictionary<string, object>>();

                // 2. 读取每个实例的 DICOM 标签
                foreach (var instance in instances)
                {
                    var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                    if (!System.IO.File.Exists(filePath))
                    {
                        DicomLogger.Warning("WADO", "文件不存在: {Path}", filePath);
                        continue;
                    }

                    try
                    {
                        var dicomFile = await DicomFile.OpenAsync(filePath);
                        var tags = new Dictionary<string, object>();

                        // 确保必需标签存在
                        foreach (var tag in requiredTags)
                        {
                            if (dicomFile.Dataset.Contains(tag))
                            {
                                var tagKey = $"{tag.Group:X4}{tag.Element:X4}";
                                var value = ConvertDicomValueToJson(dicomFile.Dataset.GetDicomItem<DicomItem>(tag));
                                if (value != null)
                                {
                                    tags[tagKey] = value;
                                }
                            }
                        }

                        // 添加其他标签
                        foreach (var tag in dicomFile.Dataset)
                        {
                            if (excludedTags.Contains(tag.Tag) || tag.Tag.IsPrivate)
                            {
                                continue;
                            }

                            // 跳过已处理的必需标签
                            if (requiredTags.Contains(tag.Tag))
                            {
                                continue;
                            }

                            var tagKey = $"{tag.Tag.Group:X4}{tag.Tag.Element:X4}";
                            var value = ConvertDicomValueToJson(tag);
                            if (value != null)
                            {
                                tags[tagKey] = value;
                            }
                        }

                        metadata.Add(tags);
                    }
                    catch (Exception ex)
                    {
                        DicomLogger.Error("WADO", ex, "读取DICOM文件失败: {Path}", filePath);
                    }
                }

                return Ok(metadata);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "获取序列元数据失败 - Study: {Study}, Series: {Series}", 
                    studyInstanceUid, seriesInstanceUid);
                return StatusCode(500, "获取序列元数据失败");
            }
        }

        private object? ConvertDicomValueToJson(DicomItem item)
        {
            try
            {
                // DICOMweb JSON 格式要求
                var result = new Dictionary<string, object?>
                {
                    { "vr", item.ValueRepresentation.Code }
                };

                // 根据 VR 类型处理值
                if (item is DicomElement element)
                {
                    switch (item.ValueRepresentation.Code)
                    {
                        case "SQ":
                            if (item is DicomSequence sq)
                            {
                                var items = new List<Dictionary<string, object>>();
                                foreach (var dataset in sq.Items)
                                {
                                    var seqItem = new Dictionary<string, object>();
                                    foreach (var tag in dataset)
                                    {
                                        var tagKey = $"{tag.Tag.Group:X4}{tag.Tag.Element:X4}";
                                        var value = ConvertDicomValueToJson(tag);
                                        if (value != null)
                                        {
                                            seqItem[tagKey] = value;
                                        }
                                    }
                                    items.Add(seqItem);
                                }
                                if (items.Any())
                                {
                                    result["Value"] = items;
                                }
                            }
                            break;

                        case "PN":
                            var personNames = element.Get<string[]>();
                            result["Value"] = personNames?.Select(pn => new Dictionary<string, string>
                            {
                                { "Alphabetic", pn }
                            }).ToArray() ?? Array.Empty<Dictionary<string, string>>();
                            break;

                        case "DA":
                            var dates = element.Get<string[]>();
                            if (dates?.Any() == true)
                            {
                                result["Value"] = dates.Select(x => x?.Replace("-", "")).ToArray();
                            }
                            break;

                        case "TM":
                            var times = element.Get<string[]>();
                            if (times?.Any() == true)
                            {
                                result["Value"] = times.Select(x => x?.Replace(":", "")).ToArray();
                            }
                            break;

                        case "AT":
                            var tags = element.Get<DicomTag[]>();
                            if (tags?.Any() == true)
                            {
                                result["Value"] = tags.Select(t => $"{t.Group:X4}{t.Element:X4}").ToArray();
                            }
                            break;

                        default:
                            if (element.Count > 0)
                            {
                                var values = element.Get<string[]>();
                                if (values?.Any() == true)
                                {
                                    result["Value"] = values;
                                }
                                else
                                {
                                    result["Value"] = Array.Empty<string>();
                                }
                            }
                            else
                            {
                                result["Value"] = Array.Empty<string>();
                            }
                            break;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "转换DICOM标签失败: {Tag}", item.Tag);
                return null;
            }
        }

        #endregion

        #region WADO-RS 缩略图接口

        // WADO-RS: 检索序列缩略图
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/thumbnail")]
        [Produces(JpegImageContentType)]
        public async Task<IActionResult> RetrieveSeriesThumbnail(
            string studyInstanceUid,
            string seriesInstanceUid,
            [FromQuery] int? size = null,
            [FromQuery] string? viewport = null)
        {
            try
            {
                // 从 viewport 参数解析尺寸（格式：width,height）
                if (viewport != null && size == null)
                {
                    var dimensions = viewport.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (dimensions.Length > 0 && int.TryParse(dimensions[0].Trim('%'), out int width))
                    {
                        size = width;
                        DicomLogger.Debug("WADO", "DICOMweb - 使用 viewport 参数: {SeriesInstanceUid}, Viewport: {Viewport}, 解析尺寸: {Size}", 
                            seriesInstanceUid, viewport, size);
                    }
                }
                else if (viewport != null && size != null)
                {
                    DicomLogger.Debug("WADO", "DICOMweb - 同时提供了 size 和 viewport 参数，优先使用 size: {Size}, 忽略 viewport: {Viewport}", 
                        size, viewport);
                }
                else if (size != null)
                {
                    DicomLogger.Debug("WADO", "DICOMweb - 使用 size 参数: {Size}", size);
                }
                else
                {
                    DicomLogger.Debug("WADO", "DICOMweb - 使用默认尺寸: 128");
                }

                // 如果既没有 size 也没有 viewport，使用默认值
                var thumbnailSize = size ?? 128;

                // 获取序列中的第一个实例
                var instances = _repository.GetInstancesByStudyUid(studyInstanceUid);
                var instance = instances.FirstOrDefault(i => 
                    i.SeriesInstanceUid == seriesInstanceUid);

                if (instance == null)
                {
                    DicomLogger.Warning("WADO", "DICOMweb - 未找到序列: {SeriesInstanceUid}", seriesInstanceUid);
                    return NotFound("Series not found");
                }

                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    DicomLogger.Error("WADO", "DICOMweb - DICOM文件不存在: {FilePath}", filePath);
                    return NotFound("DICOM file not found");
                }

                // 读取DICOM文件
                var dicomFile = await DicomFile.OpenAsync(filePath);
                DicomLogger.Debug("WADO", "DICOMweb - 生成序列缩略图: {SeriesInstanceUid}, Size: {Size}", 
                    seriesInstanceUid, thumbnailSize);
                var dicomImage = new DicomImage(dicomFile.Dataset);
                var renderedImage = dicomImage.RenderImage();

                // 转换为JPEG缩略图
                byte[] jpegBytes;
                using (var memoryStream = new MemoryStream())
                {
                    using var image = Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
                        renderedImage.AsBytes(),
                        renderedImage.Width,
                        renderedImage.Height);

                    // 计算缩略图尺寸，保持宽高比
                    var ratio = Math.Min((double)thumbnailSize / image.Width, (double)thumbnailSize / image.Height);
                    var newWidth = (int)(image.Width * ratio);
                    var newHeight = (int)(image.Height * ratio);

                    DicomLogger.Debug("WADO", "DICOMweb - 序列缩略图尺寸: {SeriesInstanceUid}, Original: {OriginalWidth}x{OriginalHeight}, New: {NewWidth}x{NewHeight}", 
                        seriesInstanceUid, image.Width, image.Height, newWidth, newHeight);

                    // 调整图像大小
                    image.Mutate(x => x.Resize(newWidth, newHeight));

                    // 配置JPEG编码器选项 - 对缩略图使用较低的质量以减小文件大小
                    var encoder = new JpegEncoder
                    {
                        Quality = 75  // 缩略图使用较低的质量
                    };

                    // 保存为JPEG
                    await image.SaveAsJpegAsync(memoryStream, encoder);
                    jpegBytes = memoryStream.ToArray();
                }

                DicomLogger.Debug("WADO", "DICOMweb - 返回序列缩略图: {SeriesInstanceUid}, Size: {Size} bytes", 
                    seriesInstanceUid, jpegBytes.Length);

                return File(jpegBytes, JpegImageContentType, $"{seriesInstanceUid}_thumbnail.jpg");
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - 检索序列缩略图失败");
                return StatusCode(500, "Error retrieving series thumbnail");
            }
        }

        #endregion
    }
} 