using Microsoft.AspNetCore.Mvc;
using DicomSCP.Services;
using DicomSCP.Configuration;
using Microsoft.Extensions.Options;
using DicomSCP.Repository;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StoreSCUController(
    IStoreSCU storeSCU,
    IOptions<QueryRetrieveConfig> config,
    IOptions<DicomSettings> settings) : ControllerBase
{
    private readonly IStoreSCU _storeSCU = storeSCU;
    private readonly QueryRetrieveConfig _config = config.Value;
    private readonly string _tempPath = settings.Value.TempPath;

    [HttpGet("nodes")]
    public ActionResult<IEnumerable<RemoteNode>> GetNodes()
    {
        // 只返回支持存储的节点
        var storeNodes = _config.RemoteNodes.Where(n => n.SupportsStore());
        return Ok(storeNodes);
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
            var node = _config.RemoteNodes.FirstOrDefault(n => n.Name == remoteName);
            if (node == null)
            {
                return NotFound($"未找到节点: {remoteName}");
            }

            // 验证节点是否支持存储
            if (!node.SupportsStore())
            {
                return BadRequest($"节点 {remoteName} 不支持存储操作");
            }

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

    [HttpPost("sendStudy/{remoteName}/{studyInstanceUid}")]
    public async Task<IActionResult> SendStudy(string remoteName, string studyInstanceUid)
    {
        try
        {
            var node = _config.RemoteNodes.FirstOrDefault(n => n.Name == remoteName);
            if (node == null)
            {
                return NotFound($"未找到节点: {remoteName}");
            }

            // 验证节点是否支持存储
            if (!node.SupportsStore())
            {
                return BadRequest($"节点 {remoteName} 不支持存储操作");
            }

            // 获取研究相关的所有文件路径
            var repository = HttpContext.RequestServices.GetRequiredService<DicomRepository>();
            var instances = repository.GetInstancesByStudyUid(studyInstanceUid);
            
            if (!instances.Any())
            {
                return NotFound("未找到相关图像");
            }

            // 获取所有文件的完整路径
            var settings = HttpContext.RequestServices.GetRequiredService<IOptions<DicomSettings>>().Value;
            var filePaths = instances.Select(i => Path.Combine(settings.StoragePath, i.FilePath));

            // 发送文件
            await _storeSCU.SendFilesAsync(filePaths, remoteName);
            
            return Ok(new { 
                message = "发送成功",
                totalFiles = filePaths.Count()
            });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "发送研究失败 - StudyInstanceUid: {StudyUid}", studyInstanceUid);
            return StatusCode(500, new { message = "发送失败" });
        }
    }
} 