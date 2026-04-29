using DicomSCP.Services;
using Microsoft.Data.Sqlite;

namespace DicomSCP.Repository;

/// <summary>
/// 数据仓储基类，提供基础的日志功能
/// </summary>
public abstract class BaseRepository(string connectionString)
{
    protected readonly string _connectionString = connectionString;

    protected SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    public static void ConfigureLogging()
    {
        // 不再需要独立的日志配置，使用 DicomLogger
    }

    protected void LogInformation(string message, params object[] args)
    {
        DicomLogger.Information("Database", "[DB] " + message, args);
    }

    protected void LogWarning(string message, params object[] args)
    {
        DicomLogger.Warning("Database", "[DB] " + message, args);
    }

    protected void LogError(Exception exception, string message, params object[] args)
    {
        DicomLogger.Error("Database", exception, "[DB] " + message, args);
    }

    protected void LogDebug(string message, params object[] args)
    {
        DicomLogger.Debug("Database", "[DB] " + message, args);
    }
} 