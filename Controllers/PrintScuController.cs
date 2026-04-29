using Microsoft.AspNetCore.Mvc;
using DicomSCP.Services;
using DicomSCP.Models;
using DicomSCP.Repository;
using DicomSCP.Configuration;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PrintScuController(
    IPrintSCU printSCU,
    DicomRepository repository,
    ILogger<PrintScuController> logger,
    IOptions<DicomSettings> settings) : ControllerBase
{
    private readonly IPrintSCU _printSCU = printSCU;
    private readonly DicomRepository _repository = repository;
    private readonly ILogger<PrintScuController> _logger = logger;
    private readonly DicomSettings _settings = settings.Value;

    /// <summary>
    /// 获取打印机列表
    /// </summary>
    [HttpGet("printers")]
    public ActionResult<IEnumerable<Configuration.PrinterConfig>> GetPrinters()
    {
        var printers = _repository.GetPrinters();
        return Ok(printers);
    }

    /// <summary>
    /// 发送打印请求
    /// </summary>
    /// <remarks>
    /// 请求示例:
    /// ```json
    /// {
    ///     "filePath": "D:/dicom/image.dcm",
    ///     "calledAE": "PRINT-SCP",
    ///     "hostName": "192.168.1.100",
    ///     "port": 104,
    ///     "numberOfCopies": 1,
    ///     "enableDpi": false,
    ///     "dpi": 150,
    ///     "printPriority": "MEDIUM",
    ///     "mediumType": "BLUE FILM",
    ///     "filmDestination": "PROCESSOR",
    ///     "filmOrientation": "PORTRAIT",
    ///     "filmSizeID": "14INX17IN",
    ///     "imageDisplayFormat": "STANDARD\\1,1",
    ///     "magnificationType": "REPLICATE",
    ///     "smoothingType": "MEDIUM",
    ///     "borderDensity": "BLACK",
    ///     "emptyImageDensity": "BLACK",
    ///     "trim": "NO"
    /// }
    /// ```
    /// 
    /// 有效值说明：
    /// - enableDpi: true/false，默认false
    /// - dpi: 100-300，默认150，仅在enableDpi=true时生成
    /// - printPriority: "HIGH", "MEDIUM", "LOW"
    /// - mediumType: "PAPER", "CLEAR FILM", "BLUE FILM"
    /// - filmDestination: "MAGAZINE", "PROCESSOR", "BIN_1", "BIN_2"
    /// - filmOrientation: "PORTRAIT", "LANDSCAPE"
    /// - filmSizeID: "8INX10IN", "10INX12IN", "11INX14IN", "14INX14IN", "14INX17IN", "24CMX30CM", "A4"
    /// - magnificationType: "REPLICATE", "BILINEAR", "CUBIC", "NONE"
    /// - smoothingType: "NONE", "LOW", "MEDIUM", "HIGH"
    /// - borderDensity: "BLACK", "WHITE"
    /// - emptyImageDensity: "BLACK", "WHITE"
    /// - trim: "YES", "NO"
    /// </remarks>
    [HttpPost("print")]
    public async Task<ActionResult> Print([FromBody] PrintRequest request)
    {
        try
        {
            // 验证打印份数
            if (request.NumberOfCopies < 1)
            {
                return BadRequest(new { message = "打印份数必须大于0" });
            }

            // 只在启用DPI时验证DPI值
            if (request.EnableDpi)
            {
                if (!request.Dpi.HasValue)
                {
                    return BadRequest(new { message = "启用DPI时必须指定DPI值" });
                }
                if (request.Dpi.Value < 100 || request.Dpi.Value > 300)
                {
                    return BadRequest(new { message = "DPI值必须在100-300之间" });
                }
            }
            else
            {
                // 如果没有启用DPI，清除DPI值
                request.Dpi = null;
            }

            _logger.LogInformation("发送打印请求 - AET: {AET}, Host: {Host}, Port: {Port}, DPI: {DPI}, Copies: {Copies}", 
                request.CalledAE, 
                request.HostName, 
                request.Port,
                request.EnableDpi ? request.Dpi.ToString() : "未启用",
                request.NumberOfCopies);

            // 发送打印请求
            var success = await _printSCU.PrintAsync(request);

            if (success)
                return Ok(new { message = "打印请求已发送" });

            return StatusCode(500, new { message = "打印失败" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理打印请求时发生错误");
            return StatusCode(500, new { message = "打印失败", error = ex.Message });
        }
    }

    /// <summary>
    /// 测试打印机连接
    /// </summary>
    [HttpPost("printers/{name}/test")]
    public async Task<ActionResult> TestPrinter(string name)
    {
        try
        {
            var printers = _repository.GetPrinters();
            
            var printer = printers.FirstOrDefault(p => p.Name == name);
            if (printer == null)
                return NotFound($"打印机 '{name}' 未找到");

            var success = await _printSCU.VerifyAsync(
                printer.HostName,
                printer.Port,
                printer.AeTitle);

            if (success)
                return Ok(new { 
                    success = true,
                    message = "打印机连接测试成功" 
                });

            return Ok(new { 
                success = false,
                message = "打印机连接被拒绝，请检查打印机配置和状态" 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "测试打印机连接时发生错误");
            return Ok(new { 
                success = false,
                message = "连接打印机失败，请检查网络连接" 
            });
        }
    }

    [HttpPost("print-by-job")]
    public async Task<IActionResult> PrintByJobId([FromBody] PrintByJobRequest request)
    {
        try
        {
            // 获取打印作业
            var printJob = await _repository.GetPrintJobByIdAsync(request.JobId);
            if (printJob == null)
            {
                return NotFound($"未找到打印作业: {request.JobId}");
            }

            // 获取打印机配置
            var printer = _repository.GetPrinterByName(request.PrinterName);
            if (printer == null)
            {
                return NotFound($"未找到打印机: {request.PrinterName}");
            }

            // 构建完整的文件路径
            var fullPath = Path.Combine(_settings.StoragePath, printJob.ImagePath);
            if (!System.IO.File.Exists(fullPath))
            {
                _logger.LogError("打印文件不存在: {FilePath}", fullPath);
                return NotFound($"打印文件不存在: {fullPath}");
            }

            // 构建打印请求
            var printRequest = new PrintRequest
            {
                FilePath = fullPath,  // 使用完整路径
                HostName = printer.HostName,
                Port = printer.Port,
                CalledAE = printer.AeTitle,
                
                // 使用默认打印参数或从数据库获取
                PrintPriority = printJob.PrintPriority,
                MediumType = printJob.MediumType,
                FilmDestination = printJob.FilmDestination,
                NumberOfCopies = printJob.NumberOfCopies,
                FilmOrientation = printJob.FilmOrientation,
                FilmSizeID = printJob.FilmSizeID,
                ImageDisplayFormat = "STANDARD\\1,1",  // 固定为1,1布局
                MagnificationType = printJob.MagnificationType,
                SmoothingType = printJob.SmoothingType,
                BorderDensity = printJob.BorderDensity,
                EmptyImageDensity = printJob.EmptyImageDensity,
                Trim = printJob.Trim
            };

            // 执行打印
            var result = await _printSCU.PrintAsync(printRequest);
            if (result)
            {
                await _repository.UpdatePrintJobStatusAsync(request.JobId, PrintJobStatus.Completed);
                return Ok(new { Message = "打印成功", JobId = request.JobId });
            }
            else
            {
                return BadRequest(new { Message = "打印失败", JobId = request.JobId });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打印失败");
            return StatusCode(500, new { Message = ex.Message });
        }
    }
}

// 请求模型
public class PrintByJobRequest
{
    [Required]
    public string JobId { get; set; } = "";

    [Required]
    public string PrinterName { get; set; } = "";
} 