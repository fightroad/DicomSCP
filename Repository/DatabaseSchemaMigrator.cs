using Dapper;
using Microsoft.Data.Sqlite;
using System.Data;

namespace DicomSCP.Repository;

/// <summary>
/// 数据库结构迁移器：集中管理历史数据库字段升级。
/// </summary>
public static class DatabaseSchemaMigrator
{
    /// <summary>
    /// 执行所有已定义的数据库结构迁移。
    /// </summary>
    public static async Task MigrateAsync(SqliteConnection connection, IDbTransaction? transaction = null)
    {
        await EnsureStudyRemarkColumnAsync(connection, transaction);
    }

    private static async Task EnsureStudyRemarkColumnAsync(SqliteConnection connection, IDbTransaction? transaction)
    {
        var studyRemarkColumnExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM pragma_table_info('Studies') WHERE name = 'Remark'",
            transaction: transaction);

        if (studyRemarkColumnExists == 0)
        {
            await connection.ExecuteAsync("ALTER TABLE Studies ADD COLUMN Remark TEXT", transaction: transaction);
        }
    }
}
