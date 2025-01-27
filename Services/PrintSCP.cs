using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using DicomSCP.Configuration;
using DicomSCP.Data;
using DicomSCP.Models;
using Microsoft.Extensions.Options;

namespace DicomSCP.Services;

public class PrintSCP : DicomService, IDicomServiceProvider, IDicomNServiceProvider, IDicomCEchoProvider
{
    private readonly string _printPath;
    private readonly string _relativePrintPath = "prints";
    private readonly DicomSettings _settings;
    private readonly DicomRepository _repository;

    // 会话状态管理
    private class PrintSession
    {
        public DicomFilmSession? FilmSession { get; set; }
        public DicomFilmBox? CurrentFilmBox { get; set; }
        public string? CallingAE { get; set; }
        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public Dictionary<int, DicomDataset> CachedImages { get; set; } = new Dictionary<int, DicomDataset>();
    }

    private PrintSession _session = new();

    // 支持的传输语法
    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes = new[]
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    private static readonly Dictionary<string, (int width, int height)> FilmSizes = new()
    {
        ["14INX17IN"] = (2100, 2550),  // 14x17英寸 (14*150=2100, 17*150=2550)
        ["11INX14IN"] = (1650, 2100),  // 11x14英寸 (11*150=1650, 14*150=2100)
        ["8INX10IN"] = (1200, 1500),   // 8x10英寸 (8*150=1200, 10*150=1500)
        ["10INX12IN"] = (1500, 1800),  // 10x12英寸 (10*150=1500, 12*150=1800)
        ["24CMX30CM"] = (1417, 1772),  // 24x30厘米 (24*59=1417, 30*59=1772)
        ["24CMX24CM"] = (1417, 1417),  // 24x24厘米 (24*59=1417)
        ["A4"] = (1240, 1754),         // A4 (210x297mm = 8.27x11.69英寸 -> 1240x1754)
        ["A3"] = (1754, 2480)          // A3 (297x420mm = 11.69x16.53英寸 -> 1754x2480)
    };

    public PrintSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        Microsoft.Extensions.Logging.ILogger log,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        _settings = DicomServiceProvider.GetRequiredService<IOptions<DicomSettings>>().Value;
        _repository = DicomServiceProvider.GetRequiredService<DicomRepository>();
        _printPath = Path.Combine(_settings.StoragePath, _relativePrintPath);
        Directory.CreateDirectory(_printPath);
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
                association.CalledAE, 
                association.CallingAE);

            // AE Title 验证
            if (_settings.PrintSCP.ValidateCallingAE && 
                !_settings.PrintSCP.AllowedCallingAEs.Contains(association.CallingAE))
            {
                DicomLogger.Warning("PrintSCP", "拒绝未授权的 Calling AE: {CallingAE}", association.CallingAE);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CallingAENotRecognized);
            }

            if (_settings.PrintSCP.AeTitle != association.CalledAE)
            {
                DicomLogger.Warning("PrintSCP", "拒绝错误的 Called AE: {CalledAE}，期望：{ExpectedAE}", 
                    association.CalledAE, 
                    _settings.PrintSCP.AeTitle);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CalledAENotRecognized);
            }

            _session.CallingAE = association.CallingAE;

            // 定义支持的服务类型
            var supportedSOPClasses = new DicomUID[]
            {
                DicomUID.BasicFilmSession,
                DicomUID.BasicFilmBox,
                DicomUID.BasicGrayscalePrintManagementMeta,
                DicomUID.BasicColorPrintManagementMeta,
                DicomUID.BasicGrayscaleImageBox,
                DicomUID.BasicColorImageBox,
                DicomUID.Verification  // C-ECHO
            };

            var hasValidPresentationContext = false;

            foreach (var pc in association.PresentationContexts)
            {
                DicomLogger.Information("PrintSCP", "处理表示上下文 - Abstract Syntax：{AbstractSyntax}", 
                    pc.AbstractSyntax.Name);

                if (!supportedSOPClasses.Contains(pc.AbstractSyntax))
                {
                    DicomLogger.Warning("PrintSCP", "不支持的服务类型：{AbstractSyntax}", 
                        pc.AbstractSyntax.Name);
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                    continue;
                }

                pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                hasValidPresentationContext = true;
            }

            if (!hasValidPresentationContext)
            {
                DicomLogger.Warning("PrintSCP", "没有有效的表示上下文，拒绝关联请求");
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.ApplicationContextNotSupported);
            }

            DicomLogger.Information("PrintSCP", "接受关联请求");
            return SendAssociationAcceptAsync(association);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理关联请求时发生错误");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.ApplicationContextNotSupported);
        }
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        DicomLogger.Information("PrintSCP", "收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("PrintSCP", "收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        try
        {
            if (exception != null)
            {
                DicomLogger.Error("PrintSCP", exception, "连接异常关闭");
            }
            else
            {
                DicomLogger.Information("PrintSCP", "连接正常关闭");
            }

            CleanupSession();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "清理资源时发生错误");
        }
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Information("PrintSCP", "收到 C-ECHO 请求");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    public async Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request)
    {
        try
        {
            if (request.SOPClassUID == DicomUID.BasicFilmSession)
            {
                return await HandleFilmSessionCreateAsync(request);
            }
            else if (request.SOPClassUID == DicomUID.BasicFilmBox)
            {
                return await HandleFilmBoxCreateAsync(request);
            }

            DicomLogger.Warning("PrintSCP", "不支持的SOP Class: {SopClass}", request.SOPClassUID?.Name ?? "Unknown");
            return new DicomNCreateResponse(request, DicomStatus.SOPClassNotSupported);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-CREATE 请求失败");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    private async Task<DicomNCreateResponse> HandleFilmSessionCreateAsync(DicomNCreateRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "创建 Film Session - Calling AE: {CallingAE}", _session.CallingAE ?? "Unknown");
            
            // 生成 FilmSessionId，不依赖 SOPInstanceUID
            var filmSessionId = DicomUID.Generate().UID;
            var printJob = new PrintJob
            {
                JobId = $"{DateTime.Now:yyyyMMddHHmmss}{new Random().Next(1000, 9999)}",
                FilmSessionId = filmSessionId,  // 使用生成的ID
                CallingAE = _session.CallingAE ?? "",
                Status = PrintJobStatus.Created,
                
                // Film Session 参数
                NumberOfCopies = request.Dataset?.GetSingleValueOrDefault(DicomTag.NumberOfCopies, 1) ?? 1,
                PrintPriority = request.Dataset?.GetSingleValueOrDefault(DicomTag.PrintPriority, "LOW") ?? "LOW",
                MediumType = request.Dataset?.GetSingleValueOrDefault(DicomTag.MediumType, "BLUE FILM") ?? "BLUE FILM",
                FilmDestination = request.Dataset?.GetSingleValueOrDefault(DicomTag.FilmDestination, "MAGAZINE") ?? "MAGAZINE",
                
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            };

            // 保存到数据库
            await _repository.AddPrintJobAsync(printJob);

            var response = new DicomNCreateResponse(request, DicomStatus.Success);
            
            if (request.Dataset != null)
            {
                var responseDataset = new DicomDataset();
                foreach (var item in request.Dataset)
                {
                    responseDataset.Add(item);
                }
                responseDataset.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.BasicFilmSession);
                responseDataset.AddOrUpdate(DicomTag.SOPInstanceUID, filmSessionId);
                response.Dataset = responseDataset;
            }

            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, DicomUID.BasicFilmSession },
                { DicomTag.CommandField, (ushort)0x8140 },
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0102 },
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, filmSessionId }
            };

            SetCommandDataset(response, command);

            _session.FilmSession = new DicomFilmSession(request.Dataset)
            {
                SOPInstanceUID = filmSessionId
            };

            DicomLogger.Information("PrintSCP", "Film Session创建成功 - ID: {Id}", filmSessionId);
            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 Film Session 创建失败");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    private async Task<DicomNCreateResponse> HandleFilmBoxCreateAsync(DicomNCreateRequest request)
    {
        try
        {
            if (_session.FilmSession == null)
            {
                DicomLogger.Warning("PrintSCP", "无法创建 Film Box - Film Session 不存在");
                return new DicomNCreateResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            DicomLogger.Information("PrintSCP", "创建 Film Box - Film Session: {SessionId}, Calling AE: {CallingAE}", 
                _session.FilmSession.SOPInstanceUID,
                _session.CallingAE ?? "Unknown");

            // 生成 FilmBoxId
            var filmBoxId = DicomUID.Generate().UID;

            await _repository.UpdatePrintJobAsync(
                _session.FilmSession.SOPInstanceUID,
                filmBoxId: filmBoxId,
                parameters: new Dictionary<string, object>
                {
                    ["FilmOrientation"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.FilmOrientation, "PORTRAIT") ?? "PORTRAIT",
                    ["FilmSizeID"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.FilmSizeID, "8INX10IN") ?? "8INX10IN",
                    ["ImageDisplayFormat"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.ImageDisplayFormat, "STANDARD\\1,1") ?? "STANDARD\\1,1",
                    ["MagnificationType"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.MagnificationType, "REPLICATE") ?? "REPLICATE",
                    ["SmoothingType"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.SmoothingType, "MEDIUM") ?? "MEDIUM",
                    ["BorderDensity"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.BorderDensity, "BLACK") ?? "BLACK",
                    ["EmptyImageDensity"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.EmptyImageDensity, "BLACK") ?? "BLACK",
                    ["Trim"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.Trim, "NO") ?? "NO"
                });

            var responseDataset = new DicomDataset();
            
            // 复制原始请求中的属性
            if (request.Dataset != null)
            {
                foreach (var item in request.Dataset)
                {
                    if (item.Tag != DicomTag.ReferencedImageBoxSequence)
                    {
                        responseDataset.Add(item);
                    }
                }
            }

            responseDataset.AddOrUpdate(DicomTag.SOPClassUID, request.SOPClassUID);
            responseDataset.AddOrUpdate(DicomTag.SOPInstanceUID, filmBoxId);

            // 获取布局格式
            var imageDisplayFormat = request.Dataset?.GetSingleValueOrDefault(DicomTag.ImageDisplayFormat, "STANDARD\\1,1") ?? "STANDARD\\1,1";
            var layoutParts = imageDisplayFormat.Split('\\')[1].Split(',');
            if (layoutParts.Length != 2 || !int.TryParse(layoutParts[0], out int columns) || !int.TryParse(layoutParts[1], out int rows))
            {
                DicomLogger.Warning("PrintSCP", "无效的布局格式: {Format}", imageDisplayFormat);
                return new DicomNCreateResponse(request, DicomStatus.InvalidAttributeValue);
            }

            DicomLogger.Information("PrintSCP", "布局: {Columns}列 x {Rows}行", columns, rows);

            // 根据布局创建Image Box引用
            var imageBoxSequence = new DicomSequence(DicomTag.ReferencedImageBoxSequence);
            var totalBoxes = columns * rows;

            // 判断是否是彩色打印
            var isColorPrint = request.SOPClassUID == DicomUID.BasicColorPrintManagementMeta;

            for (int i = 1; i <= totalBoxes; i++)
            {
                var imageBoxDataset = new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, isColorPrint ? DicomUID.BasicColorImageBox : DicomUID.BasicGrayscaleImageBox },
                    { DicomTag.ReferencedSOPInstanceUID, $"{filmBoxId}.{i}" },
                    { DicomTag.ImageBoxPosition, (ushort)i }
                };
                imageBoxSequence.Items.Add(imageBoxDataset);
                DicomLogger.Information("PrintSCP", "创建 {Type} Image Box {Position}/{Total}", 
                    isColorPrint ? "Color" : "Grayscale", i, totalBoxes);
            }

            responseDataset.Add(imageBoxSequence);

            var response = new DicomNCreateResponse(request, DicomStatus.Success);
            response.Dataset = responseDataset;

            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8140 },
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0102 },
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, filmBoxId }
            };

            SetCommandDataset(response, command);

            _session.CurrentFilmBox = new DicomFilmBox(request.Dataset)
            {
                SOPInstanceUID = filmBoxId
            };

            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 Film Box 创建失败");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到 N-SET 请求 - SOP Class: {SopClass}, Calling AE: {CallingAE}", 
                request.SOPClassUID?.Name ?? "Unknown", 
                _session.CallingAE ?? "Unknown");

            if (_session.FilmSession == null || _session.CurrentFilmBox == null)
            {
                DicomLogger.Warning("PrintSCP", "Film Session或Film Box为空，无法处理 N-SET 请求");
                return Task.FromResult(new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance));
            }

            // 检查是否是图像盒请求
            if (request.SOPClassUID == DicomUID.BasicGrayscaleImageBox || request.SOPClassUID == DicomUID.BasicColorImageBox)
            {
                // 根据SOP Class选择正确的序列标签
                var sequenceTag = request.SOPClassUID == DicomUID.BasicColorImageBox
                    ? DicomTag.BasicColorImageSequence
                    : DicomTag.BasicGrayscaleImageSequence;

                // 获取图像序列
                var imageSequence = request.Dataset?.GetSequence(sequenceTag);
                if (imageSequence == null)
                {
                    // 如果没有找到图像序列，尝试创建一个
                    var imageDataset = new DicomDataset();
                    if (request.Dataset != null)
                    {
                        foreach (var item in request.Dataset)
                        {
                            if (item.Tag != sequenceTag)
                            {
                                imageDataset.Add(item);
                            }
                        }
                    }

                    // 创建图像序列
                    var sequence = new DicomSequence(sequenceTag, imageDataset);
                    if (request.Dataset != null)
                    {
                        request.Dataset.AddOrUpdate(sequence);
                    }
                    imageSequence = request.Dataset?.GetSequence(sequenceTag);
                }

                if (imageSequence == null || !imageSequence.Items.Any())
                {
                    DicomLogger.Warning("PrintSCP", "未找到图像序列或序列为空");
                    return Task.FromResult(new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance));
                }

                // 从SOPInstanceUID中获取图像位置
                var sopInstanceUid = request.SOPInstanceUID?.UID ?? "";
                var position = 1;
                var parts = sopInstanceUid.Split('.');
                if (parts.Length > 1 && int.TryParse(parts[parts.Length - 1], out int pos))
                {
                    position = pos;
                }

                DicomLogger.Information("PrintSCP", "处理图像 - SOPInstanceUID: {Uid}, 位置: {Position}", sopInstanceUid, position);

                // 处理接收到的图像
                foreach (var item in imageSequence.Items)
                {
                    if (item.Contains(DicomTag.PixelData))
                    {
                        var pixelData = item.GetValues<byte>(DicomTag.PixelData);
                        DicomLogger.Information("PrintSCP", "图像 {Position} - 像素数据大小: {Size} bytes", position, pixelData?.Length ?? 0);
                        _session.CachedImages[position] = item;
                    }
                    else
                    {
                        DicomLogger.Warning("PrintSCP", "图像 {Position} 没有像素数据", position);
                    }
                }

                return Task.FromResult(new DicomNSetResponse(request, DicomStatus.Success));
            }

            return Task.FromResult(new DicomNSetResponse(request, DicomStatus.NoSuchSOPClass));
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-SET 请求失败");
            return Task.FromResult(new DicomNSetResponse(request, DicomStatus.ProcessingFailure));
        }
    }

    public async Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
    {
        try
        {
            if (_session.CurrentFilmBox?.Dataset == null)
            {
                return new DicomNActionResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            // 获取并验证布局格式
            var displayFormat = _session.CurrentFilmBox.Dataset.GetSingleValueOrDefault(DicomTag.ImageDisplayFormat, "STANDARD\\1,1");
            var layoutParts = displayFormat.Split('\\')[1].Split(',');
            if (layoutParts.Length != 2 || !int.TryParse(layoutParts[0], out int columns) || !int.TryParse(layoutParts[1], out int rows))
            {
                DicomLogger.Warning("PrintSCP", "无效的布局格式: {Format}", displayFormat);
                return new DicomNActionResponse(request, DicomStatus.InvalidAttributeValue);
            }

            // 获取胶片尺寸
            var filmSizeID = _session.CurrentFilmBox.Dataset.GetSingleValueOrDefault(DicomTag.FilmSizeID, "14INX17IN");
            var filmOrientation = _session.CurrentFilmBox.Dataset.GetSingleValueOrDefault(DicomTag.FilmOrientation, "PORTRAIT");
            
            var filmSize = GetFilmSize(filmSizeID);
            var (filmWidth, filmHeight) = filmOrientation.Equals("LANDSCAPE", StringComparison.OrdinalIgnoreCase)
                ? (filmSize.height, filmSize.width)
                : (filmSize.width, filmSize.height);

            // 计算分割线
            const int lineWidth = 1;  // 分割线宽度
            var totalSlots = rows * columns;
            var isSingleImage = totalSlots == 1;  // 判断是否为单图布局

            // 计算每个图像的尺寸（不包括分割线）
            var imageWidth = isSingleImage ? filmWidth : (filmWidth - (columns - 1) * lineWidth) / columns;
            var imageHeight = isSingleImage ? filmHeight : (filmHeight - (rows - 1) * lineWidth) / rows;

            // 检查第一个图像是否为彩色
            var firstImage = _session.CachedImages.First().Value;
            var photometricInterpretation = firstImage.GetSingleValue<string>(DicomTag.PhotometricInterpretation);
            var samplesPerPixel = firstImage.GetSingleValue<ushort>(DicomTag.SamplesPerPixel);
            var isColor = samplesPerPixel == 3 && photometricInterpretation == "RGB";

            // 创建输出数据集
            var outputDataset = CreateOutputDataset(firstImage, filmWidth, filmHeight);

            // 根据图像类型创建正确大小的像素缓冲区
            var bytesPerPixel = isColor ? 3 : 1;
            var pixels = new byte[filmWidth * filmHeight * bytesPerPixel];

            // 处理每个位置
            for (int position = 1; position <= totalSlots; position++)
            {
                int row = (position - 1) / columns;
                int col = (position - 1) % columns;

                // 计算图像位置（考虑分割线）
                int xBase = isSingleImage ? 0 : col * (imageWidth + lineWidth);
                int yBase = isSingleImage ? 0 : row * (imageHeight + lineWidth);

                if (_session.CachedImages.TryGetValue(position, out var image))
                {
                    ProcessImage(image, pixels, xBase, yBase, imageWidth, imageHeight, filmWidth, isSingleImage);
                }
                else
                {
                    DrawEmptyImage(pixels, xBase, yBase, imageWidth, imageHeight, filmWidth, isSingleImage);
                }
            }

            // 只在多图布局时绘制分割线和边框
            if (!isSingleImage)
            {
                if (isColor)
                {
                    // 彩色图像的分割线处理
                    for (int i = 0; i <= columns; i++)
                    {
                        int x = i * (imageWidth + lineWidth) - lineWidth;
                        if (i == 0) x = 0;  // 左边框
                        if (i == columns) x = filmWidth - 1;  // 右边框
                        
                        for (int y = 0; y < filmHeight; y++)
                        {
                            var index = (y * filmWidth + x) * 3;
                            if (index + 2 < pixels.Length)
                            {
                                // 白色线 (RGB: 255,255,255)
                                pixels[index] = 255;     // R
                                pixels[index + 1] = 255; // G
                                pixels[index + 2] = 255; // B
                            }
                        }
                    }

                    for (int i = 0; i <= rows; i++)
                    {
                        int y = i * (imageHeight + lineWidth) - lineWidth;
                        if (i == 0) y = 0;  // 上边框
                        if (i == rows) y = filmHeight - 1;  // 下边框
                        
                        for (int x = 0; x < filmWidth; x++)
                        {
                            var index = (y * filmWidth + x) * 3;
                            if (index + 2 < pixels.Length)
                            {
                                // 白色线 (RGB: 255,255,255)
                                pixels[index] = 255;     // R
                                pixels[index + 1] = 255; // G
                                pixels[index + 2] = 255; // B
                            }
                        }
                    }
                }
                else
                {
                    // 灰度图像的分割线处理（保持原有逻辑）
                    for (int i = 0; i <= columns; i++)
                    {
                        int x = i * (imageWidth + lineWidth) - lineWidth;
                        if (i == 0) x = 0;  // 左边框
                        if (i == columns) x = filmWidth - 1;  // 右边框
                        
                        for (int y = 0; y < filmHeight; y++)
                        {
                            var index = y * filmWidth + x;
                            if (index < pixels.Length)
                            {
                                pixels[index] = 255;  // 白色线
                            }
                        }
                    }

                    for (int i = 0; i <= rows; i++)
                    {
                        int y = i * (imageHeight + lineWidth) - lineWidth;
                        if (i == 0) y = 0;  // 上边框
                        if (i == rows) y = filmHeight - 1;  // 下边框
                        
                        for (int x = 0; x < filmWidth; x++)
                        {
                            var index = y * filmWidth + x;
                            if (index < pixels.Length)
                            {
                                pixels[index] = 255;  // 白色线
                            }
                        }
                    }
                }
            }

            // 保存结果
            outputDataset.AddOrUpdate(DicomTag.PixelData, pixels);
            await SaveResult(outputDataset, request.SOPInstanceUID?.UID ?? _session.CurrentFilmBox.SOPInstanceUID);

            return CreateResponse(request);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理打印请求时发生错误");
            return new DicomNActionResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    private DicomDataset CreateOutputDataset(DicomDataset sourceImage, int width, int height)
    {
        var dataset = new DicomDataset();
        dataset.AddOrUpdate(DicomTag.Rows, (ushort)height);
        dataset.AddOrUpdate(DicomTag.Columns, (ushort)width);
        
        // 获取图像类型
        var photometricInterpretation = sourceImage.GetSingleValue<string>(DicomTag.PhotometricInterpretation);
        var samplesPerPixel = sourceImage.GetSingleValue<ushort>(DicomTag.SamplesPerPixel);
        var isColor = samplesPerPixel == 3 && photometricInterpretation == "RGB";

        if (isColor)
        {
            // 彩色图像属性
            dataset.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)3);
            dataset.AddOrUpdate(DicomTag.PhotometricInterpretation, "RGB");
            dataset.AddOrUpdate(DicomTag.PlanarConfiguration, (ushort)0); // color-by-pixel
            dataset.AddOrUpdate(DicomTag.BitsAllocated, (ushort)8);
            dataset.AddOrUpdate(DicomTag.BitsStored, (ushort)8);
            dataset.AddOrUpdate(DicomTag.HighBit, (ushort)7);
            dataset.AddOrUpdate(DicomTag.PixelRepresentation, (ushort)0);
        }
        else
        {
            // 保持原有灰度图像属性
            dataset.AddOrUpdate(DicomTag.BitsAllocated, sourceImage.GetSingleValue<ushort>(DicomTag.BitsAllocated));
            dataset.AddOrUpdate(DicomTag.BitsStored, sourceImage.GetSingleValue<ushort>(DicomTag.BitsStored));
            dataset.AddOrUpdate(DicomTag.HighBit, sourceImage.GetSingleValue<ushort>(DicomTag.HighBit));
            dataset.AddOrUpdate(DicomTag.PixelRepresentation, sourceImage.GetSingleValue<ushort>(DicomTag.PixelRepresentation));
            dataset.AddOrUpdate(DicomTag.SamplesPerPixel, sourceImage.GetSingleValue<ushort>(DicomTag.SamplesPerPixel));
            dataset.AddOrUpdate(DicomTag.PhotometricInterpretation, sourceImage.GetSingleValue<string>(DicomTag.PhotometricInterpretation));
        }
        
        var studyUid = sourceImage.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, _session.FilmSession?.SOPInstanceUID) 
            ?? DicomUID.Generate().UID;
        dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
        dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, DicomUID.Generate().UID);

        var sopInstanceUid = _session.CurrentFilmBox?.SOPInstanceUID ?? DicomUID.Generate().UID;
        dataset.AddOrUpdate(DicomTag.SOPInstanceUID, sopInstanceUid);
        dataset.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage);
        dataset.AddOrUpdate(DicomTag.InstanceNumber, "1");

        return dataset;
    }

    private void ProcessImage(DicomDataset image, byte[] pixels, int xBase, int yBase, int maxWidth, int maxHeight, int filmWidth, bool isSingleImage)
    {
        try
        {
            if (image == null || !image.Contains(DicomTag.PixelData))
            {
                DrawEmptyImage(pixels, xBase, yBase, maxWidth, maxHeight, filmWidth, isSingleImage);
                return;
            }

            var photometricInterpretation = image.GetSingleValue<string>(DicomTag.PhotometricInterpretation);
            var samplesPerPixel = image.GetSingleValue<ushort>(DicomTag.SamplesPerPixel);
            var isColor = samplesPerPixel == 3 && photometricInterpretation == "RGB";

            if (isColor)
            {
                ProcessColorImage(image, pixels, xBase, yBase, maxWidth, maxHeight, filmWidth, isSingleImage);
            }
            else
            {
                ProcessGrayscaleImage(image, pixels, xBase, yBase, maxWidth, maxHeight, filmWidth, isSingleImage);
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理图像时发生错误");
            DrawEmptyImage(pixels, xBase, yBase, maxWidth, maxHeight, filmWidth, isSingleImage);
        }
    }

    private void ProcessColorImage(DicomDataset image, byte[] pixels, int xBase, int yBase, int maxWidth, int maxHeight, int filmWidth, bool isSingleImage)
    {
        var pixelData = image.GetValues<byte>(DicomTag.PixelData);
        var srcWidth = image.GetSingleValue<ushort>(DicomTag.Columns);
        var srcHeight = image.GetSingleValue<ushort>(DicomTag.Rows);
        var planarConfiguration = image.GetSingleValue<ushort>(DicomTag.PlanarConfiguration);

        if (srcWidth == 0 || srcHeight == 0 || pixelData == null || pixelData.Length == 0)
        {
            DrawEmptyImage(pixels, xBase, yBase, maxWidth, maxHeight, filmWidth, isSingleImage);
            return;
        }

        // 在多图布局时才添加边距
        double marginPercent = isSingleImage ? 0 : 0.02;
        var marginX = (int)(maxWidth * marginPercent);
        var marginY = (int)(maxHeight * marginPercent);
        var availableWidth = maxWidth - 2 * marginX;
        var availableHeight = maxHeight - 2 * marginY;

        // 计算缩放比例并保持原始比例
        var scale = Math.Min((double)availableWidth / srcWidth, (double)availableHeight / srcHeight);
        var scaledWidth = (int)(srcWidth * scale);
        var scaledHeight = (int)(srcHeight * scale);

        // 居中显示（考虑边距）
        var xOffset = xBase + marginX + (availableWidth - scaledWidth) / 2;
        var yOffset = yBase + marginY + (availableHeight - scaledHeight) / 2;

        // 处理彩色图像数据
        var bytesPerPixel = 3;
        var outputStride = filmWidth * bytesPerPixel;

        for (int y = 0; y < scaledHeight; y++)
        {
            var srcY = Math.Min((int)(y / scale), srcHeight - 1);
            var dstRowOffset = (yOffset + y) * outputStride;

            for (int x = 0; x < scaledWidth; x++)
            {
                var srcX = Math.Min((int)(x / scale), srcWidth - 1);
                var dstIndex = dstRowOffset + (xOffset + x) * bytesPerPixel;
                var srcIndex = planarConfiguration == 0
                    ? srcY * srcWidth * bytesPerPixel + srcX * bytesPerPixel
                    : srcX + srcY * srcWidth + (srcWidth * srcHeight * 0); // R plane

                if (srcIndex + 2 < pixelData.Length && dstIndex + 2 < pixels.Length)
                {
                    if (planarConfiguration == 0)
                    {
                        // Color-by-pixel (RGB RGB RGB)
                        pixels[dstIndex] = pixelData[srcIndex];     // R
                        pixels[dstIndex + 1] = pixelData[srcIndex + 1]; // G
                        pixels[dstIndex + 2] = pixelData[srcIndex + 2]; // B
                    }
                    else
                    {
                        // Color-by-plane (RRR...GGG...BBB)
                        var planeSize = srcWidth * srcHeight;
                        pixels[dstIndex] = pixelData[srcIndex];                    // R
                        pixels[dstIndex + 1] = pixelData[srcIndex + planeSize];    // G
                        pixels[dstIndex + 2] = pixelData[srcIndex + planeSize * 2]; // B
                    }
                }
            }
        }
    }

    private void ProcessGrayscaleImage(DicomDataset image, byte[] pixels, int xBase, int yBase, int maxWidth, int maxHeight, int filmWidth, bool isSingleImage)
    {
        var pixelData = image.GetValues<byte>(DicomTag.PixelData);
        var srcWidth = image.GetSingleValue<ushort>(DicomTag.Columns);
        var srcHeight = image.GetSingleValue<ushort>(DicomTag.Rows);

        if (srcWidth == 0 || srcHeight == 0 || pixelData == null || pixelData.Length == 0)
        {
            DrawEmptyImage(pixels, xBase, yBase, maxWidth, maxHeight, filmWidth, isSingleImage);
            return;
        }

        // 在多图布局时才添加边距
        double marginPercent = isSingleImage ? 0 : 0.02;
        var marginX = (int)(maxWidth * marginPercent);
        var marginY = (int)(maxHeight * marginPercent);
        var availableWidth = maxWidth - 2 * marginX;
        var availableHeight = maxHeight - 2 * marginY;

        // 计算缩放比例并保持原始比例
        var scale = Math.Min((double)availableWidth / srcWidth, (double)availableHeight / srcHeight);
        var scaledWidth = (int)(srcWidth * scale);
        var scaledHeight = (int)(srcHeight * scale);

        // 居中显示（考虑边距）
        var xOffset = xBase + marginX + (availableWidth - scaledWidth) / 2;
        var yOffset = yBase + marginY + (availableHeight - scaledHeight) / 2;

        // 复制和缩放图像
        for (int y = 0; y < scaledHeight; y++)
        {
            var srcY = Math.Min((int)(y / scale), srcHeight - 1);
            var dstRowOffset = (yOffset + y) * filmWidth;
            var srcRowOffset = srcY * srcWidth;

            for (int x = 0; x < scaledWidth; x++)
            {
                var srcX = Math.Min((int)(x / scale), srcWidth - 1);
                var dstIndex = dstRowOffset + xOffset + x;
                var srcIndex = srcRowOffset + srcX;

                if (srcIndex < pixelData.Length && dstIndex < pixels.Length)
                {
                    pixels[dstIndex] = pixelData[srcIndex];
                }
            }
        }
    }

    private void DrawEmptyImage(byte[] pixels, int xBase, int yBase, int maxWidth, int maxHeight, int filmWidth, bool isSingleImage)
    {
        // 检查是否为彩色图像
        var bytesPerPixel = pixels.Length / (filmWidth * maxHeight);

        // 在多图布局时才添加边距
        double marginPercent = isSingleImage ? 0 : 0.02;
        var marginX = (int)(maxWidth * marginPercent);
        var marginY = (int)(maxHeight * marginPercent);

        // 填充黑色（考虑边距）
        for (int y = 0; y < maxHeight; y++)
        {
            var rowOffset = (yBase + y) * filmWidth;
            for (int x = 0; x < maxWidth; x++)
            {
                if (bytesPerPixel == 3)
                {
                    var index = (rowOffset + xBase + x) * 3;
                    if (index + 2 < pixels.Length)
                    {
                        // 只在内部区域填充黑色
                        if (y >= marginY && y < (maxHeight - marginY) &&
                            x >= marginX && x < (maxWidth - marginX))
                        {
                            pixels[index] = 0;     // R
                            pixels[index + 1] = 0; // G
                            pixels[index + 2] = 0; // B
                        }
                    }
                }
                else
                {
                    // 保持原有灰度图像处理逻辑
                    var index = rowOffset + xBase + x;
                    if (index < pixels.Length)
                    {
                        // 只在内部区域填充黑色
                        if (y >= marginY && y < (maxHeight - marginY) &&
                            x >= marginX && x < (maxWidth - marginX))
                        {
                            pixels[index] = 0;  // 黑色
                        }
                    }
                }
            }
        }
    }

    private async Task SaveResult(DicomDataset dataset, string filmBoxId)
    {
        try
        {
            var dateFolder = DateTime.Now.ToString("yyyyMMdd");
            var filename = $"{filmBoxId}.dcm";
            var relativePath = Path.Combine(_relativePrintPath, dateFolder, filename);
            var fullPath = Path.Combine(_settings.StoragePath, relativePath);

            // 确保目录存在
            var directoryPath = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            // 记录保存信息
            DicomLogger.Information("PrintSCP", "保存打印结果 - ID: {Id}, 路径: {Path}", 
                filmBoxId, relativePath);

            await new DicomFile(dataset).SaveAsync(fullPath);

            if (_session.FilmSession?.SOPInstanceUID != null)
            {
                await _repository.UpdatePrintJobAsync(
                    _session.FilmSession.SOPInstanceUID,
                    parameters: new Dictionary<string, object>
                    {
                        ["ImagePath"] = relativePath,
                        ["Status"] = PrintJobStatus.ImageReceived.ToString(),
                        ["StudyInstanceUID"] = dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID),
                        ["UpdateTime"] = DateTime.Now
                    });
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "保存打印结果时发生错误");
            throw;
        }
    }

    private DicomNActionResponse CreateResponse(DicomNActionRequest request)
    {
        try
        {
            var response = new DicomNActionResponse(request, DicomStatus.Success);
            var command = new DicomDataset
            {
                // 必需的命令集元素 (Type 1)
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8130 },  // N-ACTION-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },  // 无数据集
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID },
                { DicomTag.ActionTypeID, request.ActionTypeID }
            };

            SetCommandDataset(response, command);
            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "创建响应时发生错误");
            return new DicomNActionResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    private void SetCommandDataset(DicomResponse response, DicomDataset command)
    {
        try
        {
            var commandProperty = typeof(DicomMessage).GetProperty("Command", 
                System.Reflection.BindingFlags.Public | 
                System.Reflection.BindingFlags.Instance);

            if (commandProperty != null)
            {
                commandProperty.SetValue(response, command);
            }
            else
            {
                DicomLogger.Error("PrintSCP", "无法设置命令数据集：Command属性不存在");
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "设置命令数据集时发生错误");
        }
    }

    public Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request)
    {
        try
        {
            // 验证请求
            if (request.SOPClassUID == null || request.SOPInstanceUID?.UID == null)
            {
                DicomLogger.Warning("PrintSCP", "无效的请求参数");
                return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.InvalidArgumentValue));
            }

            var response = new DicomNDeleteResponse(request, DicomStatus.Success);
            var command = new DicomDataset
            {
                // 必需的命令集元素 (Type 1)
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8150 },  // N-DELETE-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },  // 无数据集
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID }
            };

            SetCommandDataset(response, command);

            if (request.SOPClassUID == DicomUID.BasicFilmSession)
            {
                if (_session.FilmSession?.SOPInstanceUID != request.SOPInstanceUID.UID)
                {
                    return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.NoSuchObjectInstance));
                }

                DicomLogger.Information("PrintSCP", "删除 Film Session: {Uid}", request.SOPInstanceUID.UID);
                _session.FilmSession = null;
                _session.CurrentFilmBox = null;
                _session.CachedImages.Clear();
            }
            else if (request.SOPClassUID == DicomUID.BasicFilmBox)
            {
                if (_session.CurrentFilmBox?.SOPInstanceUID != request.SOPInstanceUID.UID)
                {
                    return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.NoSuchObjectInstance));
                }

                DicomLogger.Information("PrintSCP", "删除 Film Box: {Uid}", request.SOPInstanceUID.UID);
                _session.CurrentFilmBox = null;
                _session.CachedImages.Clear();
            }
            else
            {
                return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.NoSuchSOPClass));
            }

            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-DELETE 请求时发生错误");
            return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.ProcessingFailure));
        }
    }

    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
    {
        try
        {
            var response = new DicomNEventReportResponse(request, DicomStatus.Success);
            var command = new DicomDataset
            {
                // 必需的命令集元素 (Type 1)
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8110 },  // N-EVENT-REPORT-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },  // 无数据集
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID },
                { DicomTag.EventTypeID, request.EventTypeID }
            };

            SetCommandDataset(response, command);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-EVENT-REPORT 请求时发生错误");
            return Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.ProcessingFailure));
        }
    }

    public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request)
    {
        try
        {
            var response = new DicomNGetResponse(request, DicomStatus.Success);
            var command = new DicomDataset
            {
                // 必需的命令集元素 (Type 1)
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8110 },  // N-GET-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },  // 无数据集
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID }
            };

            SetCommandDataset(response, command);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-GET 请求时发生错误");
            return Task.FromResult(new DicomNGetResponse(request, DicomStatus.ProcessingFailure));
        }
    }

    private void CleanupSession()
    {
        if (_session.CurrentFilmBox != null)
        {
            DicomLogger.Information("PrintSCP", "清理 Film Box: {Uid}", 
                _session.CurrentFilmBox.SOPInstanceUID);
        }
        if (_session.FilmSession != null)
        {
            DicomLogger.Information("PrintSCP", "清理 Film Session: {Uid}", 
                _session.FilmSession.SOPInstanceUID);
        }

        _session = new PrintSession();
    }

    private (int width, int height) GetFilmSize(string sizeId)
    {
        return FilmSizes.TryGetValue(sizeId, out var size) ? size : FilmSizes["14INX17IN"];
    }
}

public class DicomFilmSession
{
    public DicomFilmSession(DicomDataset? dataset)
    {
    }
    public string SOPInstanceUID { get; set; } = "";
}

public class DicomFilmBox
{
    public DicomFilmBox(DicomDataset? dataset)
    {
        Dataset = dataset;
    }
    public string SOPInstanceUID { get; set; } = "";
    public DicomDataset? Dataset { get; set; }
} 