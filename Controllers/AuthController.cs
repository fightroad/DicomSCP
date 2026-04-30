using Microsoft.AspNetCore.Mvc;
using DicomSCP.Repository;
using DicomSCP.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(UserRepository repository) : ControllerBase
{
    private readonly UserRepository _repository = repository;

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var isValid = await _repository.ValidateUserAsync(request.Username, request.Password);
        if (!isValid)
        {
            return Unauthorized("用户名或密码错误");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, request.Username)
        };

        await HttpContext.SignInAsync(
            "CustomAuth",
            new ClaimsPrincipal(new ClaimsIdentity(claims, "CustomAuth")),
            new AuthenticationProperties
            {
                IsPersistent = true,
                IssuedUtc = DateTimeOffset.UtcNow,
                AllowRefresh = true
            });

        return Ok();
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        try
        {
            var username = User.Identity?.Name ?? "unknown";
            await HttpContext.SignOutAsync("CustomAuth");
            
            DicomLogger.Information("Api", "[API] 用户登出 - 用户名: {Username}", username);
            return Ok();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 登出异常");
            return StatusCode(500, "登出失败");
        }
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        try
        {
            var username = User.Identity?.Name;
            if (string.IsNullOrEmpty(username))
            {
                DicomLogger.Warning("Api", "[API] 修改密码失败 - 原因: 未登录");
                return Unauthorized("未登录");
            }

            // 验证旧密码
            var isValid = await _repository.ValidateUserAsync(username, request.OldPassword);
            if (!isValid)
            {
                DicomLogger.Warning("Api", "[API] 修改密码失败 - 用户名: {Username}, 原因: 旧密码错误", username);
                return BadRequest("旧密码错误");
            }

            // 修改密码
            await _repository.ChangePasswordAsync(username, request.NewPassword);

            // 清除登录状态
            await HttpContext.SignOutAsync("CustomAuth");
            
            DicomLogger.Information("Api", "[API] 修改密码成功 - 用户名: {Username}", username);
            return Ok();
        }
        catch (Exception ex)
        {
            var username = User.Identity?.Name ?? "unknown";
            DicomLogger.Error("Api", ex, "[API] 修改密码异常 - 用户名: {Username}", username);
            return StatusCode(500, "修改密码失败");
        }
    }

    [Authorize]
    [HttpGet("check-session")]
    public IActionResult CheckSession()
    {
        // 由于中间件已经处理了认证，这里只需要返回用户信息
        return Ok(new { username = User.Identity?.Name });
    }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    public string OldPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
} 