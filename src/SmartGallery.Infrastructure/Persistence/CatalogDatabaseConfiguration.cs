using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SmartGallery.Infrastructure.Persistence;

internal static class CatalogDatabaseConfiguration
{
    internal static string DatabasePath
    {
        get
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData))
            {
                throw new InvalidOperationException("Nelze určit složku místních dat aplikace.");
            }

            return Path.Combine(localData, "SmartGallery", "catalog.db");
        }
    }

    internal static void Configure(
        DbContextOptionsBuilder options, bool allowCreate, string? databasePath = null, bool pooling = true)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath ?? DatabasePath,
            Mode = allowCreate ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = pooling,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();

        options.UseSqlite(connectionString);
    }
}
