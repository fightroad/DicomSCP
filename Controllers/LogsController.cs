using Microsoft.AspNetCore.Mvc;
using DicomSCP.Configuration;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LogsController(IConfiguration configuration) : ControllerBase
{
    private readonly IConfiguration _configuration = configuration;

    [HttpGet("types")]
    public IActionResult GetLogTypes()
    {
        try
        {
            var logSettings = _configuration.GetSection("Logging").Get<LogSettings>();
            if (logSettings?.Services == null)
            {
                return Ok(Array.Empty<string>());
            }

            // 获取所有服务的日志类型
            var types = new List<string>
            {
                "QRSCP",
                "QueryRetrieveSCU",
                "StoreSCP",
                "StoreSCU",
                "WorklistSCP",
                "PrintSCP",
                "PrintSCU",
                "WADO",
                "Database",
                "Api"
            };

            return Ok(types);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"获取日志类型失败: {ex.Message}");
        }
    }

    [HttpGet("files/{type}")]
    public IActionResult GetLogFiles(string type)
    {
        try
        {
            var logSettings = _configuration.GetSection("Logging").Get<LogSettings>();
            if (logSettings == null)
            {
                return NotFound("未找到日志配置");
            }

            // 根据类型获取日志路径
            string? logPath = type.ToLower() switch
            {
                "qrscp" => logSettings.Services.QRSCP.LogPath,
                "queryretrievescu" => logSettings.Services.QueryRetrieveSCU.LogPath,
                "storescp" => logSettings.Services.StoreSCP.LogPath,
                "storescu" => logSettings.Services.StoreSCU.LogPath,
                "worklistscp" => logSettings.Services.WorklistSCP.LogPath,
                "printscp" => logSettings.Services.PrintSCP.LogPath,
                "printscu" => logSettings.Services.PrintSCU.LogPath,
                "wado" => logSettings.Services.WADO.LogPath,
                "database" => logSettings.Database.LogPath,
                "api" => logSettings.Api.LogPath,
                _ => null
            };

            if (string.IsNullOrEmpty(logPath))
            {
                return NotFound("未找到日志路径配置");
            }

            if (!Directory.Exists(logPath))
            {
                return Ok(Array.Empty<object>());
            }

            // 获取日志文件列表
            var files = Directory.GetFiles(logPath, "*.log")
                .Select(f => new FileInfo(f))
                .Select(f => new
                {
                    name = f.Name,
                    size = f.Length,
                    lastModified = f.LastWriteTime
                })
                .OrderByDescending(f => f.lastModified)
                .ToList();

            return Ok(files);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"获取日志文件列表失败: {ex.Message}");
        }
    }

    [HttpDelete("{type}/{filename}")]
    public IActionResult DeleteLogFile(string type, string filename)
    {
        try
        {
            var logSettings = _configuration.GetSection("Logging").Get<LogSettings>();
            if (logSettings == null)
            {
                return NotFound("未找到日志配置");
            }

            // 根据类型获取日志路径
            string? logPath = type.ToLower() switch
            {
                "qrscp" => logSettings.Services.QRSCP.LogPath,
                "queryretrievescu" => logSettings.Services.QueryRetrieveSCU.LogPath,
                "storescp" => logSettings.Services.StoreSCP.LogPath,
                "storescu" => logSettings.Services.StoreSCU.LogPath,
                "worklistscp" => logSettings.Services.WorklistSCP.LogPath,
                "printscp" => logSettings.Services.PrintSCP.LogPath,
                "printscu" => logSettings.Services.PrintSCU.LogPath,
                "wado" => logSettings.Services.WADO.LogPath,
                "database" => logSettings.Database.LogPath,
                "api" => logSettings.Api.LogPath,
                _ => null
            };

            if (string.IsNullOrEmpty(logPath))
            {
                return NotFound("未找到日志路径配置");
            }

            var filePath = Path.Combine(logPath, filename);

            // 验证文件名是否合法（只允许.log文件）
            if (!filename.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                filename.Contains("..") ||
                !System.IO.File.Exists(filePath))
            {
                return NotFound("文件不存在或不是有效的日志文件");
            }

            // 如果是当天的日志文件，不允许删除
            if (filename.Contains(DateTime.Now.ToString("yyyyMMdd")))
            {
                return BadRequest("当天的日志文件不能删除");
            }

            try
            {
                // 尝试重命名文件
                var tempPath = Path.Combine(logPath, $"{Guid.NewGuid()}.tmp");
                System.IO.File.Move(filePath, tempPath);

                // 如果重命名成功，删除临时文件
                System.IO.File.Delete(tempPath);
                return Ok();
            }
            catch (IOException ex) when ((ex.HResult & 0x0000FFFF) == 32) // 文件正在使用
            {
                return StatusCode(409, "文件正在使用中，无法删除");
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"删除日志文件失败: {ex.Message}");
        }
    }

    [HttpGet("{type}/{filename}/content")]
    public IActionResult GetLogContent(string type, string filename, [FromQuery] int? maxLines = 1000)
    {
        try
        {
            var logSettings = _configuration.GetSection("Logging").Get<LogSettings>();
            if (logSettings == null)
            {
                return NotFound("未找到日志配置");
            }

            // 根据类型获取日志路径
            string? logPath = type.ToLower() switch
            {
                "qrscp" => logSettings.Services.QRSCP.LogPath,
                "queryretrievescu" => logSettings.Services.QueryRetrieveSCU.LogPath,
                "storescp" => logSettings.Services.StoreSCP.LogPath,
                "storescu" => logSettings.Services.StoreSCU.LogPath,
                "worklistscp" => logSettings.Services.WorklistSCP.LogPath,
                "printscp" => logSettings.Services.PrintSCP.LogPath,
                "printscu" => logSettings.Services.PrintSCU.LogPath,
                "wado" => logSettings.Services.WADO.LogPath,
                "database" => logSettings.Database.LogPath,
                "api" => logSettings.Api.LogPath,
                _ => null
            };

            if (string.IsNullOrEmpty(logPath))
            {
                return NotFound("未找到日志路径配置");
            }

            var filePath = Path.Combine(logPath, filename);

            // 验证文件名是否合法
            if (!filename.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                filename.Contains("..") ||
                !System.IO.File.Exists(filePath))
            {
                return NotFound("文件不存在或不是有效的日志文件");
            }

            // 读取日志内容
            var lines = new List<string>();
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                    // 如果超过最大行数，保留最后的行
                    if (maxLines.HasValue && lines.Count > maxLines.Value)
                    {
                        lines.RemoveAt(0);
                    }
                }
            }

            return Ok(new { content = lines });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"读取日志内容失败: {ex.Message}");
        }
    }
} 