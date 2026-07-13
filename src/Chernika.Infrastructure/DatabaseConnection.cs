using Microsoft.Extensions.Configuration;

namespace Chernika.Infrastructure;

public static class DatabaseConnection
{
    public static string Build(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

        var password = configuration["POSTGRES_PASSWORD"]
            ?? Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");

        if (string.IsNullOrEmpty(password))
            return connectionString;

        if (connectionString.Contains("Password=", StringComparison.OrdinalIgnoreCase))
            return connectionString;

        return connectionString.TrimEnd(';') + $";Password={password}";
    }
}
