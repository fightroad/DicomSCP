using Serilog;
using Serilog.Events;
using Serilog.Core;
using DicomSCP.Configuration;
using ILogger = Serilog.ILogger;

namespace DicomSCP.Services;

/// <summary>
/// 统一的DICOM日志服务
/// </summary>
public static class DicomLogger
{
    private static ILogger? _logger;
    private static readonly Dictionary<string, ILogger> _loggers = new();
    private static LogSettings? _settings;

    private const string DefaultConsoleTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";
    private const string DefaultFileTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// 初始化日志服务
    /// </summary>
    public static void Initialize(LogSettings settings)
    {
        _settings = settings;

        // 配置DicomServer的日志（只输出到控制台）
        var serverLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _loggers["DicomServer"] = serverLogger;

        // 配置QueryRetrieveSCU的日志
        if (settings.Services.QueryRetrieveSCU.Enabled)
        {
            var qrConfig = settings.Services.QueryRetrieveSCU;
            var qrLogger = CreateLogger("QueryRetrieveSCU", qrConfig);
            _loggers["QueryRetrieveSCU"] = qrLogger;
        }

        // 配置WorklistSCP的日志
        if (settings.Services.WorklistSCP?.Enabled == true)
        {
            var worklistConfig = settings.Services.WorklistSCP;
            var worklistLogger = CreateLogger("WorklistSCP", worklistConfig);
            _loggers["WorklistSCP"] = worklistLogger;
        }

        // 配置StoreSCP的日志
        if (settings.Services.StoreSCP?.Enabled == true)
        {
            var storeConfig = settings.Services.StoreSCP;
            var storeLogger = CreateLogger("StoreSCP", storeConfig);
            _loggers["StoreSCP"] = storeLogger;
        }

        // 配置数据库日志
        if (settings.Database.Enabled)
        {
            var dbConfig = settings.Database;
            var dbLogger = new LoggerConfiguration()
                .MinimumLevel.Is(dbConfig.MinimumLevel);

            if (dbConfig.EnableConsoleLog)
            {
                dbLogger.WriteTo.Console(
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}");
            }

            if (dbConfig.EnableFileLog)
            {
                Directory.CreateDirectory(dbConfig.LogPath);
                dbLogger.WriteTo.File(
                    path: Path.Combine(dbConfig.LogPath, "database-.log"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                    retainedFileCountLimit: settings.RetainedDays);
            }

            _loggers["Database"] = dbLogger.CreateLogger();
        }

        // 配置API日志
        if (settings.Api.Enabled)
        {
            var apiConfig = settings.Api;
            var apiLogger = new LoggerConfiguration()
                .MinimumLevel.Is(apiConfig.MinimumLevel);

            if (apiConfig.EnableConsoleLog)
            {
                apiLogger.WriteTo.Console(
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}");
            }

            if (apiConfig.EnableFileLog)
            {
                Directory.CreateDirectory(apiConfig.LogPath);
                apiLogger.WriteTo.File(
                    path: Path.Combine(apiConfig.LogPath, "api-.log"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                    retainedFileCountLimit: settings.RetainedDays);
            }

            _loggers["Api"] = apiLogger.CreateLogger();
        }

        // 设置默认日志记录器
        _logger = serverLogger;
        Log.Logger = serverLogger;
    }

    private static ILogger CreateLogger(string serviceName, ServiceLogConfig config)
    {
        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Is(config.MinimumLevel);

        if (config.EnableConsoleLog)
        {
            logConfig.WriteTo.Console(
                outputTemplate: string.IsNullOrEmpty(config.OutputTemplate) 
                    ? (_settings?.OutputTemplate ?? DefaultConsoleTemplate)
                    : config.OutputTemplate);
        }

        if (config.EnableFileLog)
        {
            var logPath = string.IsNullOrEmpty(config.LogPath)
                ? Path.Combine(_settings?.LogPath ?? "logs", serviceName.ToLower())
                : config.LogPath;

            // 确保日志目录存在
            Directory.CreateDirectory(logPath);

            logConfig.WriteTo.File(
                path: Path.Combine(logPath, $"{serviceName.ToLower()}-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: string.IsNullOrEmpty(config.OutputTemplate)
                    ? (_settings?.OutputTemplate ?? DefaultFileTemplate)
                    : config.OutputTemplate,
                retainedFileCountLimit: _settings?.RetainedDays ?? 31);
        }

        return logConfig.CreateLogger();
    }

    private static ILogger GetLogger(string service = "DicomServer")
    {
        if (_loggers.TryGetValue(service, out var logger))
        {
            return logger;
        }
        return _logger ?? throw new InvalidOperationException("Logger not initialized");
    }

    public static void Debug(string messageTemplate, params object[] propertyValues)
        => GetLogger().Debug(messageTemplate, propertyValues);

    public static void Information(string messageTemplate, params object[] propertyValues)
        => GetLogger().Information(messageTemplate, propertyValues);

    public static void Warning(string messageTemplate, params object[] propertyValues)
        => GetLogger().Warning(messageTemplate, propertyValues);

    public static void Error(Exception? exception, string messageTemplate, params object[] propertyValues)
        => GetLogger().Error(exception, messageTemplate, propertyValues);

    public static void Debug(string service, string messageTemplate, params object[] propertyValues)
        => GetLogger(service).Debug(messageTemplate, propertyValues);

    public static void Information(string service, string messageTemplate, params object[] propertyValues)
        => GetLogger(service).Information(messageTemplate, propertyValues);

    public static void Warning(string service, string messageTemplate, params object[] propertyValues)
        => GetLogger(service).Warning(messageTemplate, propertyValues);

    public static void Error(string service, Exception? exception, string messageTemplate, params object[] propertyValues)
        => GetLogger(service).Error(exception, messageTemplate, propertyValues);

    /// <summary>
    /// 关闭日志服务
    /// </summary>
    public static void CloseAndFlush()
    {
        foreach (var logger in _loggers.Values)
        {
            (logger as IDisposable)?.Dispose();
        }
        _loggers.Clear();
        Log.CloseAndFlush();
    }
} 