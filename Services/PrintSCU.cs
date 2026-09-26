using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FellowOakDicom.Imaging;
using DicomSCP.Configuration;
using DicomSCP.Models;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DicomSCP.Services;

public interface IPrintSCU
{
    Task<bool> PrintAsync(PrintRequest request);
    Task<bool> VerifyAsync(string hostName, int port, string calledAE);
}

public class PrintSCU : IPrintSCU
{
    private readonly DicomSettings _settings;
    private readonly string _aeTitle;

    // DICOM 打印常量
    private static class PrintConstants
    {
        // DICOM标准值
        public static readonly string[] PrintPriorities = { "HIGH", "MED", "LOW" };
        public static readonly string[] MediumTypes = { "PAPER", "CLEAR FILM", "BLUE FILM" };
        public static readonly string[] FilmDestinations = { "MAGAZINE", "PROCESSOR", "BIN_1", "BIN_2" };
        public static readonly string[] FilmOrientations = { "PORTRAIT", "LANDSCAPE" };
        public static readonly string[] FilmSizes = { 
            "8INX10IN", "10INX12IN", "11INX14IN", "14INX14IN", 
            "14INX17IN", "24CMX30CM", "A4" 
        };
        public static readonly string[] MagnificationTypes = { 
            "REPLICATE", "BILINEAR", "CUBIC", "NONE" 
        };
        public static readonly string[] SmoothingTypes = { 
            "NONE", "MEDIUM", "SMOOTH"  // 修正为标准值
        };
        public static readonly string[] Densities = { "BLACK", "WHITE" };
        public static readonly string[] TrimOptions = { "YES", "NO" };

        // 默认值 - 使用DICOM标准值
        public const string DefaultPriority = "MED";
        public const string DefaultMediumType = "BLUE FILM";
        public const string DefaultDestination = "PROCESSOR";
        public const string DefaultOrientation = "PORTRAIT";
        public const string DefaultSize = "14INX17IN";
        public const string DefaultDisplayFormat = "STANDARD\\1,1";
        public const string DefaultMagnification = "REPLICATE";
        public const string DefaultSmoothing = "MEDIUM";  // 修正为标准值
        public const string DefaultDensity = "BLACK";
        public const string DefaultTrim = "NO";
    }

    public PrintSCU(IOptions<DicomSettings> settings)
    {
        _settings = settings.Value;
        _aeTitle = _settings.PrintSCU?.AeTitle ?? "PRINTSCU";
    }

    private IDicomClient CreateClient(
        string hostName,
        int port,
        string callingAE,
        string calledAE,
        DicomPresentationContext? printMetaContext = null)
    {
        var client = DicomClientFactory.Create(hostName, port, false, callingAE, calledAE);
        client.NegotiateAsyncOps();
        if (printMetaContext != null)
        {
            client.AdditionalPresentationContexts.Add(printMetaContext);
        }
        return client;
    }

    private static DicomPresentationContext CreatePrintMetaPresentationContext(bool printInColor)
    {
        var metaUid = printInColor
            ? DicomUID.BasicColorPrintManagementMeta
            : DicomUID.BasicGrayscalePrintManagementMeta;
        var pc = new DicomPresentationContext(1, metaUid);
        pc.AddTransferSyntax(DicomTransferSyntax.ExplicitVRLittleEndian);
        pc.AddTransferSyntax(DicomTransferSyntax.ImplicitVRLittleEndian);
        return pc;
    }

    private bool ValidateAETitle(string aeTitle)
    {
        // AE Title 必须是 16 个字符以内的 ASCII 字符
        if (string.IsNullOrEmpty(aeTitle) || aeTitle.Length > 16)
            return false;

        // 只允许大写字母、数字和特定符号
        return aeTitle.All(c => (c >= 'A' && c <= 'Z') || 
                               (c >= '0' && c <= '9') || 
                               c == '-' || c == '_');
    }

    private bool ValidateHostName(string hostName)
    {
        // 检查是否为IP地址或有效的主机名
        return !string.IsNullOrEmpty(hostName) && 
               (System.Net.IPAddress.TryParse(hostName, out _) || 
                Uri.CheckHostName(hostName) != UriHostNameType.Unknown);
    }

    private bool ValidatePort(int port)
    {
        // 端口号范围：1-65535
        return port > 0 && port <= 65535;
    }

    private void SetDefaultValues(PrintRequest request)
    {
        if (request.NumberOfCopies < 1)
            request.NumberOfCopies = 1;
        if (string.IsNullOrEmpty(request.PrintPriority))
            request.PrintPriority = PrintConstants.DefaultPriority;
        if (string.IsNullOrEmpty(request.MediumType))
            request.MediumType = PrintConstants.DefaultMediumType;
        if (string.IsNullOrEmpty(request.FilmDestination))
            request.FilmDestination = PrintConstants.DefaultDestination;
        if (string.IsNullOrEmpty(request.FilmOrientation))
            request.FilmOrientation = PrintConstants.DefaultOrientation;
        if (string.IsNullOrEmpty(request.FilmSizeID))
            request.FilmSizeID = PrintConstants.DefaultSize;
        if (string.IsNullOrEmpty(request.ImageDisplayFormat))
            request.ImageDisplayFormat = PrintConstants.DefaultDisplayFormat;
        if (string.IsNullOrEmpty(request.MagnificationType))
            request.MagnificationType = PrintConstants.DefaultMagnification;
        if (string.IsNullOrEmpty(request.SmoothingType))
            request.SmoothingType = PrintConstants.DefaultSmoothing;
        if (string.IsNullOrEmpty(request.BorderDensity))
            request.BorderDensity = PrintConstants.DefaultDensity;
        if (string.IsNullOrEmpty(request.EmptyImageDensity))
            request.EmptyImageDensity = PrintConstants.DefaultDensity;
        if (string.IsNullOrEmpty(request.Trim))
            request.Trim = PrintConstants.DefaultTrim;
    }

    private string NormalizeSmoothingType(string? smoothing)
    {
        if (string.IsNullOrEmpty(smoothing))
            return PrintConstants.DefaultSmoothing;

        switch (smoothing.ToUpper().Trim())
        {
            case "NONE":
                return "NONE";
            case "MEDIUM":
            case "MED":
                return "MEDIUM";
            case "SMOOTH":
            case "HIGH":
                return "SMOOTH";
            default:
                return PrintConstants.DefaultSmoothing;
        }
    }

    private bool ValidateConnectionParameters(string hostName, int port, string calledAE, string? errorSource = null)
    {
        if (!ValidateAETitle(calledAE))
        {
            DicomLogger.Error(errorSource ?? "PrintSCU", "无效的 Called AE Title: {CalledAE}", calledAE);
            return false;
        }

        if (!ValidateHostName(hostName))
        {
            DicomLogger.Error(errorSource ?? "PrintSCU", "无效的主机名: {HostName}", hostName);
            return false;
        }

        if (!ValidatePort(port))
        {
            DicomLogger.Error(errorSource ?? "PrintSCU", "无效的端口号: {Port}", port);
            return false;
        }

        return true;
    }

    private bool ValidatePrintParameters(PrintRequest request)
    {
        // 验证文件路径
        if (string.IsNullOrEmpty(request.FilePath))
        {
            DicomLogger.Error("PrintSCU", "文件路径不能为空");
            return false;
        }

        if (!System.IO.File.Exists(request.FilePath))
        {
            DicomLogger.Error("PrintSCU", "文件不存在: {FilePath}", request.FilePath);
            return false;
        }

        // 验证连接参数
        if (!ValidateConnectionParameters(request.HostName, request.Port, request.CalledAE))
            return false;

        // 标准化并验证打印优先级
        switch (request.PrintPriority?.ToUpper()?.Trim() ?? "")
        {
            case "HIGH":
            case "HI":
            case "H":
                request.PrintPriority = "HIGH";
                break;
            case "MEDIUM":
            case "MED":
            case "M":
                request.PrintPriority = "MED";
                break;
            case "LOW":
            case "LO":
            case "L":
                request.PrintPriority = "LOW";
                break;
            default:
                DicomLogger.Error("PrintSCU", "无效的打印优先级: {Priority}", 
                    request.PrintPriority ?? "NULL");
                return false;
        }

        // 标准化平滑类型
        request.SmoothingType = NormalizeSmoothingType(request.SmoothingType);

        // 验证打印参数
        if (!PrintConstants.MediumTypes.Contains(request.MediumType))
        {
            DicomLogger.Error("PrintSCU", "无效的类型: {MediumType}", request.MediumType);
            return false;
        }

        if (!PrintConstants.FilmOrientations.Contains(request.FilmOrientation))
        {
            DicomLogger.Error("PrintSCU", "无效的胶片方向: {Orientation}", request.FilmOrientation);
            return false;
        }

        if (!PrintConstants.FilmSizes.Contains(request.FilmSizeID))
        {
            DicomLogger.Error("PrintSCU", "无效的胶片尺寸: {Size}", request.FilmSizeID);
            return false;
        }

        if (!PrintConstants.MagnificationTypes.Contains(request.MagnificationType))
        {
            DicomLogger.Error("PrintSCU", "无效的放大类型: {Type}", request.MagnificationType);
            return false;
        }

        if (!PrintConstants.SmoothingTypes.Contains(request.SmoothingType))
        {
            DicomLogger.Error("PrintSCU", "无效的平滑类型: {Type}", request.SmoothingType);
            return false;
        }

        if (!PrintConstants.Densities.Contains(request.BorderDensity))
        {
            DicomLogger.Error("PrintSCU", "无效的边密度: {Density}", request.BorderDensity);
            return false;
        }

        if (!PrintConstants.Densities.Contains(request.EmptyImageDensity))
        {
            DicomLogger.Error("PrintSCU", "无效的空白密度: {Density}", request.EmptyImageDensity);
            return false;
        }

        if (!PrintConstants.TrimOptions.Contains(request.Trim))
        {
            DicomLogger.Error("PrintSCU", "无效的裁剪选项: {Trim}", request.Trim);
            return false;
        }

        return true;
    }

    private async Task<DicomFile> LoadDicomFileAsync(string filePath)
    {
        DicomLogger.Information("PrintSCU", "读取DICOM文件 - 路径: {FilePath}, 大小: {Size} bytes", 
            filePath, new FileInfo(filePath).Length);
        return await DicomFile.OpenAsync(filePath);
    }

    private DicomDataset CreateFilmSessionDataset(PrintRequest request)
    {
        return new DicomDataset
        {
            { DicomTag.NumberOfCopies, request.NumberOfCopies.ToString() },
            { DicomTag.PrintPriority, request.PrintPriority },
            { DicomTag.MediumType, request.MediumType },
            { DicomTag.FilmDestination, request.FilmDestination }
        };
    }

    private DicomDataset CreateFilmBoxDataset(PrintRequest request)
    {
        return new DicomDataset
        {
            { DicomTag.ImageDisplayFormat, request.ImageDisplayFormat },
            { DicomTag.FilmOrientation, request.FilmOrientation },
            { DicomTag.FilmSizeID, request.FilmSizeID },
            { DicomTag.MagnificationType, request.MagnificationType },
            { DicomTag.SmoothingType, request.SmoothingType },
            { DicomTag.BorderDensity, request.BorderDensity },
            { DicomTag.EmptyImageDensity, request.EmptyImageDensity },
            { DicomTag.Trim, request.Trim }
        };
    }

    private DicomDataset CreateImageBoxDataset(DicomFile file, bool printInColor)
    {
        var dicomImage = new DicomImage(file.Dataset);
        using var rendered = dicomImage.RenderImage();
        var sharpImage = rendered.AsSharpImage();

        DicomDataset imageDataset;
        DicomTag sequenceTag;

        if (printInColor)
        {
            var pixelData = new byte[sharpImage.Width * sharpImage.Height * 3];
            ExtractRgb(sharpImage, pixelData);
            imageDataset = new DicomDataset
            {
                { DicomTag.Columns, (ushort)sharpImage.Width },
                { DicomTag.Rows, (ushort)sharpImage.Height },
                { DicomTag.BitsAllocated, (ushort)8 },
                { DicomTag.BitsStored, (ushort)8 },
                { DicomTag.HighBit, (ushort)7 },
                { DicomTag.PixelRepresentation, (ushort)0 },
                { DicomTag.SamplesPerPixel, (ushort)3 },
                { DicomTag.PhotometricInterpretation, "RGB" },
                { DicomTag.PlanarConfiguration, (ushort)0 },
                { DicomTag.PixelData, pixelData }
            };
            sequenceTag = DicomTag.BasicColorImageSequence;
        }
        else
        {
            var pixelData = new byte[sharpImage.Width * sharpImage.Height];
            ConvertToGrayscale(sharpImage, pixelData);
            imageDataset = new DicomDataset
            {
                { DicomTag.Columns, (ushort)sharpImage.Width },
                { DicomTag.Rows, (ushort)sharpImage.Height },
                { DicomTag.BitsAllocated, (ushort)8 },
                { DicomTag.BitsStored, (ushort)8 },
                { DicomTag.HighBit, (ushort)7 },
                { DicomTag.PixelRepresentation, (ushort)0 },
                { DicomTag.SamplesPerPixel, (ushort)1 },
                { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
                { DicomTag.PixelData, pixelData }
            };
            sequenceTag = DicomTag.BasicGrayscaleImageSequence;
        }

        return new DicomDataset
        {
            { DicomTag.ImageBoxPosition, (ushort)1 },
            { DicomTag.Polarity, "NORMAL" },
            { sequenceTag, new DicomDataset[] { imageDataset } }
        };
    }

    private static void ConvertToGrayscale(Image<Bgra32> image, byte[] pixelData)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var rowOffset = y * accessor.Width;
                for (var x = 0; x < row.Length; x++)
                {
                    ref readonly var pixel = ref row[x];
                    pixelData[rowOffset + x] = (byte)((pixel.R * 38 + pixel.G * 75 + pixel.B * 15) >> 7);
                }
            }
        });
    }

    private static void ExtractRgb(Image<Bgra32> image, byte[] pixelData)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var rowOffset = y * accessor.Width * 3;
                for (var x = 0; x < row.Length; x++)
                {
                    ref readonly var pixel = ref row[x];
                    var i = rowOffset + x * 3;
                    pixelData[i] = pixel.R;
                    pixelData[i + 1] = pixel.G;
                    pixelData[i + 2] = pixel.B;
                }
            }
        });
    }

    private static bool IsColorDicom(DicomDataset dataset)
    {
        return dataset.GetSingleValueOrDefault(DicomTag.SamplesPerPixel, (ushort)1) == 3
            && string.Equals(
                dataset.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, string.Empty),
                "RGB",
                StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> VerifyAsync(string hostName, int port, string calledAE)
    {
        try
        {
            if (!ValidateConnectionParameters(hostName, port, calledAE, "VerifyAsync"))
                return false;

            var client = CreateClient(hostName, port, _aeTitle, calledAE);
            var echo = new DicomCEchoRequest();
            await client.AddRequestAsync(echo);
            await client.SendAsync();
            return true;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCU", ex, "验证连接时发生错误");
            return false;
        }
    }

    public async Task<bool> PrintAsync(PrintRequest request)
    {
        try
        {
            SetDefaultValues(request);
            if (!ValidatePrintParameters(request))
                return false;

            DicomLogger.Information("PrintSCU", "开始打印任务 - 从 {CallingAE} 到 {CalledAE}@{Host}:{Port}", 
                _aeTitle, request.CalledAE, request.HostName, request.Port);

            var file = await LoadDicomFileAsync(request.FilePath);
            // 任务标记为彩色，或图像本身是 RGB，都走彩色打印
            var printInColor = request.PrintInColor || IsColorDicom(file.Dataset);
            DicomLogger.Information("PrintSCU", "打印模式: {Mode}", printInColor ? "彩色" : "灰度");

            var printMetaContext = CreatePrintMetaPresentationContext(printInColor);
            var client = CreateClient(request.HostName, request.Port, _aeTitle, request.CalledAE, printMetaContext);

            // 1. 创建 Film Session
            var filmSessionRequest = new DicomNCreateRequest(DicomUID.BasicFilmSession, DicomUID.Generate())
            {
                Dataset = CreateFilmSessionDataset(request),
                PresentationContext = printMetaContext
            };

            DicomResponse? filmSessionResponse = null;
            var filmSessionTcs = new TaskCompletionSource<bool>();
            
            filmSessionRequest.OnResponseReceived = (req, res) =>
            {
                filmSessionResponse = res;
                if (res.Status.State != DicomState.Success)
                {
                    DicomLogger.Error("PrintSCU", "创建 Film Session 失败: {Status}", res.Status);
                    filmSessionTcs.SetResult(false);
                    return;
                }

                var filmSessionUid = res.Command.GetString(DicomTag.AffectedSOPInstanceUID);
                DicomLogger.Information("PrintSCU", "Film Session 创建成功, UID: {Uid}", filmSessionUid);

                // 2. 创建 Film Box
                var filmBoxRequest = new DicomNCreateRequest(DicomUID.BasicFilmBox, DicomUID.Generate())
                {
                    Dataset = CreateFilmBoxDataset(request),
                    PresentationContext = printMetaContext
                };
                filmBoxRequest.Dataset.Add(DicomTag.ReferencedFilmSessionSequence, new DicomDataset[] 
                {
                    new DicomDataset 
                    {
                        { DicomTag.ReferencedSOPClassUID, DicomUID.BasicFilmSession },
                        { DicomTag.ReferencedSOPInstanceUID, filmSessionUid }
                    }
                });

                filmBoxRequest.OnResponseReceived = (fbReq, fbRes) =>
                {
                    if (fbRes.Status.State != DicomState.Success)
                    {
                        DicomLogger.Error("PrintSCU", "创建 Film Box 失败: {Status}", fbRes.Status);
                        filmSessionTcs.SetResult(false);
                        return;
                    }

                    var imageBoxSequence = fbRes.Dataset?.GetSequence(DicomTag.ReferencedImageBoxSequence);
                    if (imageBoxSequence == null || !imageBoxSequence.Items.Any())
                    {
                        DicomLogger.Error("PrintSCU", "未找到 Image Box 引用");
                        filmSessionTcs.SetResult(false);
                        return;
                    }

                    var imageBoxItem = imageBoxSequence.Items[0];
                    var imageBoxClassUid = imageBoxItem.GetSingleValue<DicomUID>(DicomTag.ReferencedSOPClassUID);
                    var imageBoxInstanceUid = imageBoxItem.GetSingleValue<DicomUID>(DicomTag.ReferencedSOPInstanceUID);

                    // 3. 设置 Image Box
                    var imageBoxRequest = new DicomNSetRequest(imageBoxClassUid, imageBoxInstanceUid)
                    {
                        Dataset = CreateImageBoxDataset(file, printInColor),
                        PresentationContext = printMetaContext
                    };

                    imageBoxRequest.OnResponseReceived = (ibReq, ibRes) =>
                    {
                        if (ibRes.Status.State != DicomState.Success)
                        {
                            DicomLogger.Error("PrintSCU", "设置 Image Box 失败: {Status}", ibRes.Status);
                            filmSessionTcs.SetResult(false);
                            return;
                        }

                        // 4. 执行打印
                        var actionRequest = new DicomNActionRequest(DicomUID.BasicFilmSession, DicomUID.Parse(filmSessionUid), 1)
                        {
                            PresentationContext = printMetaContext
                        };
                        actionRequest.OnResponseReceived = (pReq, pRes) =>
                        {
                            if (pRes.Status.State != DicomState.Success)
                            {
                                DicomLogger.Error("PrintSCU", "打印操作失败: {Status}", pRes.Status);
                                filmSessionTcs.SetResult(false);
                                return;
                            }
                            filmSessionTcs.SetResult(true);
                        };

                        client.AddRequestAsync(actionRequest).Wait();
                        client.SendAsync().Wait();
                    };

                    client.AddRequestAsync(imageBoxRequest).Wait();
                    client.SendAsync().Wait();
                };

                client.AddRequestAsync(filmBoxRequest).Wait();
                client.SendAsync().Wait();
            };

            await client.AddRequestAsync(filmSessionRequest);
            await client.SendAsync();

            var result = await filmSessionTcs.Task;
            if (result)
            {
                DicomLogger.Information("PrintSCU", "打印任务已完成");
            }
            return result;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCU", ex, "打印过程中发生错误");
            return false;
        }
    }
} 

