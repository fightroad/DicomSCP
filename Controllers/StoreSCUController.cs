using Microsoft.AspNetCore.Mvc;
using DicomSCP.Services;
using DicomSCP.Configuration;
using Microsoft.Extensions.Options;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StoreSCUController : ControllerBase
{
    private readonly IStoreSCU _storeSCU;
    private readonly QueryRetrieveConfig _config;
    private readonly string _tempPath;

    public StoreSCUController(
        IStoreSCU storeSCU,
        IOptions<QueryRetrieveConfig> config,
        IOptions<DicomSettings> settings)
    {
        _storeSCU = storeSCU;
        _config = config.Value;
        _tempPath = settings.Value.TempPath;
    }

    [HttpGet("nodes")]
    public ActionResult<IEnumerable<RemoteNode>> GetNodes()
    {
        return Ok(_config.RemoteNodes);
    }

    [HttpGet("nodes/default")]
    public ActionResult<RemoteNode> GetDefaultNode()
    {
        var node = _config.RemoteNodes?.FirstOrDefault(n => n.IsDefault);
        if (node == null)
        {
            return NotFound("未配置默认节点");
        }
        return Ok(node);
    }

    [HttpPost("verify/{remoteName}")]
    public async Task<IActionResult> VerifyConnection(string remoteName)
    {
        try
        {
            var result = await _storeSCU.VerifyConnectionAsync(remoteName);
            return Ok(new { success = result });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "验证连接失败");
            return StatusCode(500, "验证连接失败");
        }
    }

    [HttpPost("send/{remoteName}")]
    public async Task<IActionResult> SendFiles(string remoteName, [FromForm] string? folderPath, IFormFileCollection? files)
    {
        try
        {
            if (!string.IsNullOrEmpty(folderPath))
            {
                DicomLogger.Information("Api", "开始发送文件夹 - 路径: {FolderPath}, 目标: {RemoteName}", 
                    folderPath, remoteName);
                
                await _storeSCU.SendFolderAsync(folderPath, remoteName);
                return Ok(new { message = "文件夹发送成功" });
            }
            else if (files != null && files.Count > 0)
            {
                DicomLogger.Information("Api", "开始上传文件 - 文件数: {Count}, 目标: {RemoteName}", 
                    files.Count, remoteName);
                
                // 创建临时目录
                var tempDir = Path.Combine(_tempPath, "temp_" + DateTime.Now.Ticks.ToString());
                Directory.CreateDirectory(tempDir);

                try
                {
                    // 保存上传的文件
                    var filePaths = new List<string>();
                    foreach (var file in files)
                    {
                        // 直接使用文件名保存到临时目录
                        var fileName = Path.GetFileName(file.FileName);
                        var filePath = Path.Combine(tempDir, fileName);

                        using (var stream = new FileStream(filePath, FileMode.Create))
                        {
                            await file.CopyToAsync(stream);
                        }
                        filePaths.Add(filePath);
                        DicomLogger.Debug("Api", "文件已保存到临时目录 - 文件: {FileName}", file.FileName);
                    }

                    // 发送文件
                    await _storeSCU.SendFilesAsync(filePaths, remoteName);
                    return Ok(new { message = "文件发送成功" });
                }
                finally
                {
                    // 清理临时文件
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                        DicomLogger.Debug("Api", "临时目录已清理 - 路径: {TempDir}", tempDir);
                    }
                }
            }
            else
            {
                DicomLogger.Warning("Api", "未提供文件或文件夹路径");
                return BadRequest("请提供文件或文件夹路径");
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "发送失败");
            return StatusCode(500, ex.Message);
        }
    }
} 