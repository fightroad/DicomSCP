using Serilog.Events;

namespace DicomSCP.Configuration;

public class LogSettings
{
    public bool EnableConsoleLog { get; set; } = true;
    public bool EnableFileLog { get; set; } = true;
    public LogEventLevel MinimumLevel { get; set; } = LogEventLevel.Information;
    public string LogPath { get; set; } = "logs";
    public int RetainedDays { get; set; } = 31;
    public string OutputTemplate { get; set; } = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";
    public ServiceLoggingConfig Services { get; set; } = new();
    public DatabaseLogConfig Database { get; set; } = new();
    public ApiLogConfig Api { get; set; } = new();
}

public class ServiceLoggingConfig
{
    public ServiceLogConfig QueryRetrieveSCU { get; set; } = new ServiceLogConfig();
    public ServiceLogConfig StoreSCP { get; set; } = new ServiceLogConfig
    {
        Enabled = true,
        EnableConsoleLog = true,
        EnableFileLog = true,
        MinimumLevel = Serilog.Events.LogEventLevel.Debug,
        LogPath = "logs/store",
        OutputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}"
    };
    public ServiceLogConfig WorklistSCP { get; set; } = new ServiceLogConfig
    {
        Enabled = true,
        EnableConsoleLog = true,
        EnableFileLog = true,
        MinimumLevel = Serilog.Events.LogEventLevel.Debug,
        LogPath = "logs/worklist",
        OutputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}"
    };
}

public class ServiceLogConfig
{
    public bool Enabled { get; set; } = true;
    public LogEventLevel MinimumLevel { get; set; } = LogEventLevel.Debug;
    public bool EnableConsoleLog { get; set; } = true;
    public bool EnableFileLog { get; set; } = true;
    public string LogPath { get; set; } = string.Empty;
    public string OutputTemplate { get; set; } = string.Empty;
}

/// <summary>
/// 数据库操作日志配置
/// </summary>
public class DatabaseLogConfig
{
    public bool Enabled { get; set; } = true;
    public LogEventLevel MinimumLevel { get; set; } = LogEventLevel.Debug;
    public bool EnableConsoleLog { get; set; } = true;
    public bool EnableFileLog { get; set; } = true;
    public string LogPath { get; set; } = "logs/database";
    public string OutputTemplate { get; set; } = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [DB] [{SourceContext}] {Message:lj}{NewLine}{Exception}";
}

/// <summary>
/// API服务日志配置
/// </summary>
public class ApiLogConfig
{
    public bool Enabled { get; set; } = true;
    public LogEventLevel MinimumLevel { get; set; } = LogEventLevel.Information;
    public bool EnableConsoleLog { get; set; } = true;
    public bool EnableFileLog { get; set; } = true;
    public string LogPath { get; set; } = "logs/api";
    public string OutputTemplate { get; set; } = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [API] [{RequestId}] {RequestMethod} {RequestPath} - {StatusCode} - {Elapsed:0.0000}ms{NewLine}{Message:lj}{NewLine}{Exception}";
} 