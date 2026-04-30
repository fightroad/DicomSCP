using Dapper;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace DicomSCP.Repository;

public sealed class UserRepository(IConfiguration configuration)
    : BaseRepository(configuration.GetConnectionString("DicomDb") ?? throw new ArgumentException("Missing DicomDb connection string"))
{
    public async Task<bool> ValidateUserAsync(string username, string password)
    {
        await using var connection = new SqliteConnection(_connectionString);
        var hashedPassword = HashPassword(password);
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Users WHERE Username = @Username AND Password = @Password",
            new { Username = username, Password = hashedPassword }
        );
        return count > 0;
    }

    public async Task<bool> ChangePasswordAsync(string username, string newPassword)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var hashedPassword = HashPassword(newPassword);
        var sql = @"
            UPDATE Users
            SET Password = @Password
            WHERE Username = @Username";

        var result = await connection.ExecuteAsync(sql, new
        {
            Username = username,
            Password = hashedPassword
        });

        return result > 0;
    }

    private static string HashPassword(string password)
    {
        var hashedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(hashedBytes);
    }
}
