using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Services;
using DicomSCP.Repository;
using System.Management;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DicomController(
    DicomServer server,
    IOptions<DicomSettings> settings,
    DicomDatasetPersistence persistence) : ControllerBase
{
    private readonly DicomServer _server = server;
    private readonly DicomSettings _settings = settings.Value;
    private readonly DicomDatasetPersistence _persistence = persistence;

    // 缓存静态系统信息，避免每次请求都执行昂贵系统查询
    private static string? _cachedCpuModel;
    private static string? _cachedPlatformName;

    // CPU 使用率采样缓存（跨请求增量计算）
    private static readonly object _cpuLock = new();
    private static DateTime _lastCpuSampleTimeUtc = DateTime.MinValue;
    private static TimeSpan _lastProcessCpuTime = TimeSpan.Zero;
    private static (long Idle, long Total)? _lastLinuxCpuStat;
    private static float _windowsCpuUsage;
    private static PerformanceCounter? _windowsCpuCounter;

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var serverStatus = _server.GetServicesStatus();
        var process = Process.GetCurrentProcess();
        
        // 获取进程私有内存使用情况（MB）
        var processMemory = process.PrivateMemorySize64 / 1024.0 / 1024.0;
        
        // 获取系统信息
        double totalPhysicalMemory = 0;
        double availablePhysicalMemory = 0;
        string cpuModel = GetCpuModelCached();
        double cpuUsage = GetCpuUsage(process);

        try 
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows 系统内存信息
                var performanceInfo = PerformanceInfo.GetPerformanceInfo();
                var physicalMemoryInBytes = performanceInfo.PhysicalTotal.ToInt64() * performanceInfo.PageSize.ToInt64();
                totalPhysicalMemory = physicalMemoryInBytes / 1024.0 / 1024.0;  // 转换为 MB
                
                var availableMemoryInBytes = performanceInfo.PhysicalAvailable.ToInt64() * performanceInfo.PageSize.ToInt64();
                availablePhysicalMemory = availableMemoryInBytes / 1024.0 / 1024.0;  // 转换为 MB

                // CPU 信息和 CPU 使用率已使用缓存/增量采样计算
            }
            else if (OperatingSystem.IsLinux())
            {
                // Linux 内存信息
                var memInfo = System.IO.File.ReadAllLines("/proc/meminfo");
                foreach (var line in memInfo)
                {
                    if (line.StartsWith("MemTotal:"))
                    {
                        totalPhysicalMemory = ParseLinuxMemInfo(line) / 1024.0; // 转换为 MB
                    }
                    else if (line.StartsWith("MemAvailable:"))
                    {
                        availablePhysicalMemory = ParseLinuxMemInfo(line) / 1024.0; // 转换为 MB
                    }
                }

                // CPU 信息和 CPU 使用率已使用缓存/增量采样计算
            }
            else if (OperatingSystem.IsMacOS())
            {
                // macOS 系统信息获取 (需要通过 sysctl 命令)
                totalPhysicalMemory = GetMacMemoryInfo();
                availablePhysicalMemory = 0; // 暂未适配可用内存采集，后续按需实现
                // CPU 信息和 CPU 使用率已使用缓存/增量采样计算
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "获取系统信息失败");
        }

        var usedPhysicalMemory = totalPhysicalMemory - availablePhysicalMemory;
        var memoryUsagePercent = totalPhysicalMemory > 0 ? (usedPhysicalMemory / totalPhysicalMemory) * 100 : 0;

        return Ok(new
        {
            store = new
            {
                aeTitle = _settings.AeTitle,
                port = _settings.StoreSCPPort,
                isRunning = serverStatus.Services.StoreScp
            },
            worklist = new
            {
                aeTitle = _settings.WorklistSCP.AeTitle,
                port = _settings.WorklistSCP.Port,
                isRunning = serverStatus.Services.WorklistScp
            },
            qr = new
            {
                aeTitle = _settings.QRSCP.AeTitle,
                port = _settings.QRSCP.Port,
                isRunning = serverStatus.Services.QrScp
            },
            print = new
            {
                aeTitle = _settings.PrintSCP.AeTitle,
                port = _settings.PrintSCP.Port,
                isRunning = serverStatus.Services.PrintScp
            },
            system = new
            {
                cpuUsage = Math.Round(cpuUsage, 2),
                cpuModel = cpuModel,
                processMemory = Math.Round(processMemory, 2),
                systemMemoryTotal = Math.Round(totalPhysicalMemory, 2),
                systemMemoryUsed = Math.Round(usedPhysicalMemory, 2),
                systemMemoryPercent = Math.Round(memoryUsagePercent, 2),
                processorCount = Environment.ProcessorCount,
                processStartTime = new
                {
                    days = (DateTime.Now - process.StartTime).Days,
                    hours = (DateTime.Now - process.StartTime).Hours,
                    minutes = (DateTime.Now - process.StartTime).Minutes
                },
                osVersion = RuntimeInformation.OSDescription,
                platform = GetPlatformNameCached()
            }
        });
    }

    private static string GetCpuModelCached()
    {
        if (!string.IsNullOrEmpty(_cachedCpuModel))
        {
            return _cachedCpuModel;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                #pragma warning disable CA1416
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
                _cachedCpuModel = searcher.Get()
                    .Cast<ManagementObject>()
                    .Select(obj => obj["Name"]?.ToString())
                    .FirstOrDefault();
                #pragma warning restore CA1416
            }
            else if (OperatingSystem.IsLinux())
            {
                _cachedCpuModel = System.IO.File.ReadAllLines("/proc/cpuinfo")
                    .FirstOrDefault(line => line.StartsWith("model name"))
                    ?.Split(':')
                    .LastOrDefault()
                    ?.Trim();
            }
            else if (OperatingSystem.IsMacOS())
            {
                _cachedCpuModel = ExecuteCommand("sysctl", "-n machdep.cpu.brand_string").Trim();
            }
        }
        catch
        {
            // ignore and fallback
        }

        return string.IsNullOrEmpty(_cachedCpuModel) ? "Unknown" : _cachedCpuModel;
    }

    private static string GetPlatformNameCached()
    {
        if (!string.IsNullOrEmpty(_cachedPlatformName))
        {
            return _cachedPlatformName;
        }

        _cachedPlatformName = GetPlatformName();
        return _cachedPlatformName;
    }

    private static double GetCpuUsage(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            lock (_cpuLock)
            {
                #pragma warning disable CA1416
                _windowsCpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                var value = _windowsCpuCounter.NextValue();
                #pragma warning restore CA1416
                if (value > 0)
                {
                    _windowsCpuUsage = value;
                }
                return _windowsCpuUsage;
            }
        }

        if (OperatingSystem.IsLinux())
        {
            lock (_cpuLock)
            {
                try
                {
                    var cpu = System.IO.File.ReadAllText("/proc/stat")
                        .Split('\n')[0]
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Skip(1)
                        .Take(7)
                        .Select(long.Parse)
                        .ToArray();
                    var current = (Idle: cpu[3], Total: cpu.Sum());
                    if (_lastLinuxCpuStat.HasValue)
                    {
                        var idleDiff = current.Idle - _lastLinuxCpuStat.Value.Idle;
                        var totalDiff = current.Total - _lastLinuxCpuStat.Value.Total;
                        if (totalDiff > 0)
                        {
                            return (1.0 - idleDiff / (double)totalDiff) * 100;
                        }
                    }
                    _lastLinuxCpuStat = current;
                }
                catch
                {
                    // fallback below
                }
            }
        }

        // 兜底：使用进程 CPU 增量估算（不阻塞）
        lock (_cpuLock)
        {
            var now = DateTime.UtcNow;
            var currentCpu = process.TotalProcessorTime;
            if (_lastCpuSampleTimeUtc == DateTime.MinValue)
            {
                _lastCpuSampleTimeUtc = now;
                _lastProcessCpuTime = currentCpu;
                return 0;
            }

            var wallMs = (now - _lastCpuSampleTimeUtc).TotalMilliseconds;
            var cpuMs = (currentCpu - _lastProcessCpuTime).TotalMilliseconds;

            _lastCpuSampleTimeUtc = now;
            _lastProcessCpuTime = currentCpu;

            if (wallMs <= 0) return 0;
            var usage = cpuMs / (wallMs * Environment.ProcessorCount) * 100.0;
            return Math.Max(0, Math.Min(100, usage));
        }
    }

    private static string GetPlatformName()
    {
        if (OperatingSystem.IsWindows())
        {
            return $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor} ({RuntimeInformation.OSArchitecture})";
        }
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var osRelease = System.IO.File.ReadAllLines("/etc/os-release")
                    .ToDictionary(
                        line => line.Split('=')[0],
                        line => line.Split('=')[1].Trim('"')
                    );
                return $"{osRelease["PRETTY_NAME"]} ({RuntimeInformation.OSArchitecture})";
            }
            catch
            {
                return $"Linux ({RuntimeInformation.OSArchitecture})";
            }
        }
        if (OperatingSystem.IsMacOS())
        {
            return $"macOS {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture})";
        }
        return "Unknown";
    }

    private double ParseLinuxMemInfo(string line)
    {
        return double.Parse(line.Split([' '], StringSplitOptions.RemoveEmptyEntries)[1]);
    }

    private double GetMacMemoryInfo()
    {
        try
        {
            var output = ExecuteCommand("sysctl", "hw.memsize");
            var memSize = long.Parse(output.Split(':')[1].Trim());
            return memSize / 1024.0 / 1024.0; // 转换为 MB
        }
        catch
        {
            return 0;
        }
    }

    private static string ExecuteCommand(string command, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        return process.StandardOutput.ReadToEnd();
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start()
    {
        try
        {
            if (_server.IsRunning)
            {
                DicomLogger.Warning("Api",
                    "[API] 启动服务失败 - 原因: {Reason}", 
                    "服务器已在运行");
                return BadRequest(new
                {
                    Message = "服务器已在运行",
                    AeTitle = _settings.AeTitle,
                    StoreSCPPort = _settings.StoreSCPPort,
                    WorklistSCPPort = _settings.WorklistSCP.Port
                });
            }

            CStoreSCP.Configure(_settings, _persistence);

            await _server.StartAsync();
            DicomLogger.Information("Api",
                "[API] 启动服务成功");
            return Ok(new
            {
                Message = "服务器已启动",
                AeTitle = _settings.AeTitle,
                StoreSCPPort = _settings.StoreSCPPort,
                WorklistSCPPort = _settings.WorklistSCP.Port
            });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex,
                "[API] 启动服务异常");
            return StatusCode(500, "启动服务器失败");
        }
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        try
        {
            if (!_server.IsRunning)
            {
                DicomLogger.Warning("Api",
                    "[API] 停止服务失败 - 原因: {Reason}", 
                    "服务器未运行");
                return BadRequest("服务器未运行");
            }

            await _server.StopAsync();
            DicomLogger.Information("Api",
                "[API] 停止服务成功");
            return Ok("服务器已停止");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex,
                "[API] 停止服务异常");
            return StatusCode(500, "停止服务器失败");
        }
    }

    [HttpPost("restart")]
    public async Task<IActionResult> Restart()
    {
        try
        {
            DicomLogger.Information("Api", "[API] 正在重启DICOM服务...");
            await _server.RestartAllServices();
            DicomLogger.Information("Api", "[API] DICOM服务重启完成");

            return Ok(new
            {
                Message = "服务重成功",
                AeTitle = _settings.AeTitle,
                StoreSCPPort = _settings.StoreSCPPort,
                WorklistSCPPort = _settings.WorklistSCP.Port,
                QRSCPPort = _settings.QRSCP.Port
            });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 重启DICOM服务失败");
            return StatusCode(500, "重启服务失败");
        }
    }
}

// 添加 PerformanceInfo 结构体
[StructLayout(LayoutKind.Sequential)]
public struct PerformanceInfo
{
    public int Size;
    public IntPtr CommitTotal;
    public IntPtr CommitLimit;
    public IntPtr CommitPeak;
    public IntPtr PhysicalTotal;
    public IntPtr PhysicalAvailable;
    public IntPtr SystemCache;
    public IntPtr KernelTotal;
    public IntPtr KernelPaged;
    public IntPtr KernelNonpaged;
    public IntPtr PageSize;
    public int HandlesCount;
    public int ProcessCount;
    public int ThreadCount;

    public static PerformanceInfo GetPerformanceInfo()
    {
        var pi = new PerformanceInfo { Size = Marshal.SizeOf<PerformanceInfo>() };
        GetPerformanceInfo(out pi, pi.Size);
        return pi;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(out PerformanceInfo PerformanceInformation, int Size);
} 