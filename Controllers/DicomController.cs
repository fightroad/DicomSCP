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
    private static (ulong User, ulong System, ulong Idle, ulong Nice)? _lastMacCpuStat;
    private static float _windowsCpuUsage;
    private static PerformanceCounter? _windowsCpuCounter;

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var serverStatus = _server.GetServicesStatus();
        var process = Process.GetCurrentProcess();
        process.Refresh();

        // Windows 用私有内存；macOS/Linux 用工作集(RSS)，更接近活动监视器/系统监视器
        var processMemory = (OperatingSystem.IsWindows()
            ? process.PrivateMemorySize64
            : process.WorkingSet64) / 1024.0 / 1024.0;
        
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
                // macOS: sysctl 获取总量，vm_stat 估算可用内存
                (totalPhysicalMemory, availablePhysicalMemory) = GetMacMemoryInfo();
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
                // Intel 有 brand_string；Apple Silicon 常为空，回退 hw.model
                _cachedCpuModel = ExecuteCommand("sysctl", "-n machdep.cpu.brand_string").Trim();
                if (string.IsNullOrWhiteSpace(_cachedCpuModel))
                {
                    var model = ExecuteCommand("sysctl", "-n hw.model").Trim();
                    _cachedCpuModel = string.IsNullOrWhiteSpace(model) ? null : model;
                }
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

        if (OperatingSystem.IsMacOS())
        {
            lock (_cpuLock)
            {
                try
                {
                    if (TryGetMacCpuLoad(out var current))
                    {
                        if (_lastMacCpuStat is { } last)
                        {
                            var userDiff = current.User - last.User;
                            var sysDiff = current.System - last.System;
                            var idleDiff = current.Idle - last.Idle;
                            var niceDiff = current.Nice - last.Nice;
                            var totalDiff = userDiff + sysDiff + idleDiff + niceDiff;
                            _lastMacCpuStat = current;
                            if (totalDiff > 0)
                            {
                                return (userDiff + sysDiff + niceDiff) * 100.0 / totalDiff;
                            }
                        }
                        else
                        {
                            _lastMacCpuStat = current;
                        }
                    }
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

    /// <summary>
    /// 返回 macOS 物理内存总量与可用内存（MB）。
    /// 可用内存约等于 free + inactive + speculative + purgeable 页。
    /// </summary>
    private static (double TotalMb, double AvailableMb) GetMacMemoryInfo()
    {
        try
        {
            var memSizeOutput = ExecuteCommand("sysctl", "-n hw.memsize").Trim();
            var totalBytes = long.Parse(memSizeOutput);
            var totalMb = totalBytes / 1024.0 / 1024.0;

            var pageSizeOutput = ExecuteCommand("sysctl", "-n hw.pagesize").Trim();
            var pageSize = long.Parse(pageSizeOutput);

            var vmStat = ExecuteCommand("vm_stat", "");
            long free = 0, inactive = 0, speculative = 0, purgeable = 0;
            foreach (var rawLine in vmStat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("Pages free:", StringComparison.Ordinal))
                    free = ParseMacVmStatPages(line);
                else if (line.StartsWith("Pages inactive:", StringComparison.Ordinal))
                    inactive = ParseMacVmStatPages(line);
                else if (line.StartsWith("Pages speculative:", StringComparison.Ordinal))
                    speculative = ParseMacVmStatPages(line);
                else if (line.StartsWith("Pages purgeable:", StringComparison.Ordinal))
                    purgeable = ParseMacVmStatPages(line);
            }

            var availableBytes = (free + inactive + speculative + purgeable) * pageSize;
            var availableMb = Math.Min(availableBytes / 1024.0 / 1024.0, totalMb);
            return (totalMb, availableMb);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static long ParseMacVmStatPages(string line)
    {
        // 例: "Pages free:                               41966."
        var valuePart = line.Split(':', 2)[1].Trim().TrimEnd('.');
        return long.Parse(valuePart);
    }

    private static bool TryGetMacCpuLoad(out (ulong User, ulong System, ulong Idle, ulong Nice) load)
    {
        load = default;
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        // host_cpu_load_info.cpu_ticks: USER, SYSTEM, IDLE, NICE
        var ticks = new uint[4];
        var count = 4;
        var result = host_statistics(mach_host_self(), HostCpuLoadInfo, ticks, ref count);
        if (result != 0 || count < 4)
        {
            return false;
        }

        load = (ticks[0], ticks[1], ticks[2], ticks[3]);
        return true;
    }

    private const int HostCpuLoadInfo = 3;

    [DllImport("libSystem.dylib")]
    private static extern IntPtr mach_host_self();

    [DllImport("libSystem.dylib")]
    private static extern int host_statistics(IntPtr host, int flavor, uint[] hostInfo, ref int hostInfoCount);

    private static string ExecuteCommand(string command, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(3000);
        return output;
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