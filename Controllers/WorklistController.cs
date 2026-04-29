using Microsoft.AspNetCore.Mvc;
using DicomSCP.Models;
using DicomSCP.Repository;
using DicomSCP.Services;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WorklistController(WorklistRepository repository) : ControllerBase
{
    private readonly WorklistRepository _repository = repository;

    // 添加 DICOM 格式转换为 ISO 格式的辅助方法
    private static string ConvertToIsoDateTime(string? dicomDateTime)
    {
        if (string.IsNullOrEmpty(dicomDateTime)) return string.Empty;
        
        try
        {
            var numericOnly = new string(dicomDateTime.Where(char.IsDigit).ToArray());
            if (numericOnly.Length >= 8)
            {
                var year = numericOnly.Substring(0, 4);
                var month = numericOnly.Substring(4, 2);
                var day = numericOnly.Substring(6, 2);
                var hour = numericOnly.Length >= 10 ? numericOnly.Substring(8, 2) : "00";
                var minute = numericOnly.Length >= 12 ? numericOnly.Substring(10, 2) : "00";
                
                return $"{year}-{month}-{day}T{hour}:{minute}";
            }

            // 如果已经是 ISO 格式，直接返回
            if (dicomDateTime.Contains("T"))
            {
                return dicomDateTime;
            }

            // 尝试解析其他格式
            if (DateTime.TryParse(dicomDateTime, out var dt))
            {
                return dt.ToString("yyyy-MM-ddTHH:mm");
            }

            return dicomDateTime;
        }
        catch
        {
            return dicomDateTime;
        }
    }

    // 添加 ISO 格式转换为 DICOM 格式的辅助方法
    private static string ConvertToDicomDateTime(string? isoDateTime)
    {
        if (string.IsNullOrEmpty(isoDateTime)) return string.Empty;

        try
        {
            if (DateTime.TryParse(isoDateTime, out var dt))
            {
                return dt.ToString("yyyyMMddHHmm");
            }

            // 如果已经是数字格式，确保长度正确
            var numericOnly = new string(isoDateTime.Where(char.IsDigit).ToArray());
            if (numericOnly.Length >= 8)
            {
                return numericOnly.PadRight(12, '0');
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // 添加年龄和生日转换的辅助方法
    private static (string birthDate, int age) CalculateAgeAndBirthDate(int? providedAge = null, string? birthDate = null)
    {
        var today = DateTime.Today;
        
        // 如果提供了年龄，计算出生日期
        if (providedAge.HasValue)
        {
            var birthYear = today.Year - providedAge.Value;
            return ($"{birthYear}0101", providedAge.Value);
        }
        
        // 如果提供了出生日期，计算年龄
        if (!string.IsNullOrEmpty(birthDate))
        {
            var numericOnly = new string(birthDate.Where(char.IsDigit).ToArray());
            if (numericOnly.Length >= 8)
            {
                var year = int.Parse(numericOnly.Substring(0, 4));
                var month = int.Parse(numericOnly.Substring(4, 2));
                var day = int.Parse(numericOnly.Substring(6, 2));
                
                var birthDateTime = new DateTime(year, month, day);
                var age = today.Year - birthDateTime.Year;
                
                // 如果今年的生日还没到，年龄减1
                if (today.DayOfYear < birthDateTime.DayOfYear)
                {
                    age--;
                }
                
                return (numericOnly.Substring(0, 8), age);
            }
        }
        
        // 如果都没有提供，返回默认值
        return ("19000101", 0);
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<WorklistItem>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? patientId = null,
        [FromQuery] string? patientName = null,
        [FromQuery] string? accessionNumber = null,
        [FromQuery] string? modality = null,
        [FromQuery] string? scheduledDate = null,
        [FromQuery] string? status = null)
    {
        try
        {
            // 验证分页参数
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 10;
            if (pageSize > 100) pageSize = 100;

            string? searchDate = null;
            if (!string.IsNullOrEmpty(scheduledDate))
            {
                DicomLogger.Debug("Api", "接收到的查询日期: {ScheduledDate}", scheduledDate);

                // 处理前端传来的 ISO 格式 (yyyy-MM-ddTHH:mm)
                if (DateTime.TryParse(scheduledDate, out var date))
                {
                    searchDate = date.ToString("yyyyMMdd");
                    DicomLogger.Debug("Api", "日期处理 - 原始值: {Original}, 转换后: {Converted}", 
                        scheduledDate, searchDate);
                }
                else
                {
                    DicomLogger.Warning("Api", "无法解析日期: {ScheduledDate}", scheduledDate);
                }
            }

            var result = await _repository.GetPagedAsync(
                page,
                pageSize,
                patientId,
                patientName,
                accessionNumber,
                modality,
                searchDate,
                status);

            DicomLogger.Debug("Api", "查询结果 - 总数: {Total}, 当前页: {Page}, 每页大小: {PageSize}", 
                result.TotalCount, result.Page, result.PageSize);

            // 统一转换返回的时间格式为 ISO 格式
            foreach (var item in result.Items)
            {
                item.ScheduledDateTime = ConvertToIsoDateTime(item.ScheduledDateTime);
                
                // 如果有出生日期但没有年龄，计算年龄
                if (!item.Age.HasValue && !string.IsNullOrEmpty(item.PatientBirthDate))
                {
                    var (_, age) = CalculateAgeAndBirthDate(birthDate: item.PatientBirthDate);
                    item.Age = age;
                }
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 获取Worklist列表失败");
            return StatusCode(500, "获取数据失败");
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<WorklistItem>> GetById(string id)
    {
        try
        {
            var item = await _repository.GetByIdAsync(id);
            if (item == null)
            {
                return NotFound();
            }

            // 转换时间格式为前端格式
            item.ScheduledDateTime = ConvertToIsoDateTime(item.ScheduledDateTime);

            // 如果有出生日期但没有年龄，计算年龄
            if (!item.Age.HasValue && !string.IsNullOrEmpty(item.PatientBirthDate))
            {
                var (_, age) = CalculateAgeAndBirthDate(birthDate: item.PatientBirthDate);
                item.Age = age;
            }

            return Ok(item);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 获取Worklist项目失败 - ID: {Id}", id);
            return StatusCode(500, "获取数据失败");
        }
    }

    [HttpPost]
    public async Task<ActionResult<WorklistItem>> Create(WorklistItem item)
    {
        try
        {
            DicomLogger.Information("Api", "[API] 正在创建Worklist项目: {@Item}", item);

            // 验证必填字段
            if (string.IsNullOrEmpty(item.PatientId))
                return BadRequest("患者ID不能为空");
            if (string.IsNullOrEmpty(item.PatientName))
                return BadRequest("患者姓名不能为空");
            if (!item.Age.HasValue || item.Age < 0 || item.Age > 150)
                return BadRequest("年龄必须在0-150岁之间");
            if (string.IsNullOrEmpty(item.AccessionNumber))
                return BadRequest("检查号不能为空");
            if (string.IsNullOrEmpty(item.Modality))
                return BadRequest("检查类型不能为空");
            if (string.IsNullOrEmpty(item.ScheduledDateTime))
                return BadRequest("预约时间不能为空");
            if (string.IsNullOrEmpty(item.ScheduledAET))
                return BadRequest("预约AE Title不能为空");

            // 验证状态值
            var validStatuses = new[] { "SCHEDULED", "IN_PROGRESS", "COMPLETED", "DISCONTINUED" };
            if (!validStatuses.Contains(item.Status))
            {
                item.Status = "SCHEDULED";  // 如果状态无效，默认为已预约
            }

            // 生成新的 WorklistId
            item.WorklistId = Guid.NewGuid().ToString("N");

            // 生成 StudyInstanceUID
            if (string.IsNullOrEmpty(item.StudyInstanceUid))
            {
                // 使用时间戳（精确到毫秒）作为基础，加上4位随机数
                var timestamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
                var random = new Random().Next(1000, 9999);
                item.StudyInstanceUid = $"2.25.{timestamp}{random}";
            }

            // 使用辅助方法计算生日
            var (birthDate, _) = CalculateAgeAndBirthDate(item.Age);
            item.PatientBirthDate = birthDate;

            // 转换预约时间为 DICOM 格式
            item.ScheduledDateTime = ConvertToDicomDateTime(item.ScheduledDateTime);
            if (string.IsNullOrEmpty(item.ScheduledDateTime))
            {
                return BadRequest("无效的预约时间格式");
            }

            var worklistId = await _repository.CreateAsync(item);
            item.WorklistId = worklistId;

            DicomLogger.Information("Api", "[API] 成功创建Worklist项目 - ID: {WorklistId}", worklistId);
            return Ok(item);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 创建Worklist项目失败");
            return StatusCode(500, $"创建失败: {ex.Message}");
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, WorklistItem item)
    {
        try
        {
            if (id != item.WorklistId)
            {
                return BadRequest();
            }

            // 获取原有记录，保留 StudyInstanceUID
            var existingItem = await _repository.GetByIdAsync(id);
            if (existingItem == null)
            {
                return NotFound();
            }
            
            // 保持原有的 StudyInstanceUID
            item.StudyInstanceUid = existingItem.StudyInstanceUid;

            // 验证年龄
            if (!item.Age.HasValue || item.Age < 0 || item.Age > 150)
                return BadRequest("年龄必须在0-150岁之间");

            // 使用辅助方法计算生日
            var (birthDate, _) = CalculateAgeAndBirthDate(item.Age);
            item.PatientBirthDate = birthDate;

            // 验证并转换预约时间为 DICOM 格式
            if (!DateTime.TryParse(item.ScheduledDateTime, out var dateTime))
            {
                return BadRequest("无效的预约时间格式");
            }
            item.ScheduledDateTime = dateTime.ToString("yyyyMMddHHmm");

            var success = await _repository.UpdateAsync(item);
            if (!success)
            {
                return NotFound();
            }

            DicomLogger.Information("Api", "[API] 成功更新Worklist项目 - ID: {WorklistId}", id);
            return NoContent();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 更新Worklist项目失败 - ID: {WorklistId}", id);
            return StatusCode(500, $"更新失败: {ex.Message}");
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        try
        {
            var success = await _repository.DeleteAsync(id);
            if (!success)
            {
                return NotFound();
            }
            return NoContent();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 删除Worklist项目失败 - ID: {Id}", id);
            return StatusCode(500, "删除失败");
        }
    }
} 