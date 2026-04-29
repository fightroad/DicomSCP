using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigController(IWebHostEnvironment env, ILogger<ConfigController> logger) : ControllerBase
{
    private readonly string _configPath = Path.Combine(env.ContentRootPath, "appsettings.json");
    private readonly ILogger<ConfigController> _logger = logger;

    [HttpGet]
    public IActionResult GetConfig()
    {
        try
        {
            var jsonString = System.IO.File.ReadAllText(_configPath);
            // 验证是否为有效的 JSON
            using var document = JsonDocument.Parse(jsonString);
            
            // 返回格式化的 JSON，保持中文字符
            return Ok(JsonSerializer.Serialize(document, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        }
        catch (JsonException)
        {
            return StatusCode(500, "配置文件格式错误");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取配置文件失败");
            return StatusCode(500, "读取配置文件失败");
        }
    }

    [HttpPost]
    public IActionResult UpdateConfig([FromBody] JsonElement config)
    {
        try
        {
            // 验证必要的配置项
            try
            {
                var dicomSettings = config.GetProperty("DicomSettings");
                var aeTitle = dicomSettings.GetProperty("AeTitle").GetString();
                var storeSCPPort = dicomSettings.GetProperty("StoreSCPPort").GetInt32();
                var storagePath = dicomSettings.GetProperty("StoragePath").GetString();

                if (string.IsNullOrEmpty(aeTitle))
                {
                    return BadRequest("配置错误：AE Title 不能为空");
                }
                if (storeSCPPort <= 0 || storeSCPPort > 65535)
                {
                    return BadRequest("配置错误：StoreSCP 端口必须在 1-65535 之间");
                }
                if (string.IsNullOrEmpty(storagePath))
                {
                    return BadRequest("配置错误：存储路径不能为空");
                }
            }
            catch (KeyNotFoundException)
            {
                return BadRequest("配置错误：缺少必要的配置项");
            }
            catch (InvalidOperationException)
            {
                return BadRequest("配置错误：配置项格式不正确");
            }

            // 保存配置
            var updatedJson = JsonSerializer.Serialize(config, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            System.IO.File.WriteAllText(_configPath, updatedJson);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新配置文件失败");
            return StatusCode(500, "更新配置文件失败");
        }
    }
} 