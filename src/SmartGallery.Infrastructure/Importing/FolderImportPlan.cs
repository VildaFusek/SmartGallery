using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using SmartGallery.Application.Importing;
using SmartGallery.Domain;

namespace SmartGallery.Infrastructure.Importing;

internal sealed class FolderImportPlan(SqliteConnection connection, string root)
{
    private const int BatchSize = 256;

    internal async Task<(long Media, string? Warning)> BuildAsync(
        IImportPreviewService planner, Library library, string sourceDirectory,
        Action<long, long, long> report, CancellationToken cancellationToken)
    {
        // An empty filename creates a private SQLite database deleted when the connection closes.
        connection.Open();
        connection.CreateCollation("PATH_NOCASE", StringComparer.OrdinalIgnoreCase.Compare);
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                PRAGMA temp_store = FILE;
                CREATE TABLE Items (Id INTEGER PRIMARY KEY, Item TEXT NOT NULL);
                CREATE TABLE Directories (Path TEXT COLLATE PATH_NOCASE PRIMARY KEY) WITHOUT ROWID;
                """;
            schema.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO Items (Item) VALUES ($item);";
        var value = insert.Parameters.Add("$item", SqliteType.Text);
        insert.Prepare();
        using var reserve = connection.CreateCommand();
        reserve.Transaction = transaction;
        reserve.CommandText = "INSERT OR IGNORE INTO Directories (Path) VALUES ($path);";
        var directory = reserve.Parameters.Add("$path", SqliteType.Text);
        reserve.Prepare();

        void AddItem(ImportPreviewItem item)
        {
            value.Value = JsonSerializer.Serialize(item, FolderImportPlanJsonContext.Default.ImportPreviewItem);
            insert.ExecuteNonQuery();
            if (item.State != ImportPreviewState.Blocked)
            {
                FolderImportService.RequireWithin(item.DestinationPath, root);
                for (var parent = Path.GetDirectoryName(item.DestinationPath);
                     parent is not null && !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase);
                     parent = Path.GetDirectoryName(parent))
                {
                    directory.Value = parent;
                    if (reserve.ExecuteNonQuery() == 0)
                    {
                        break; // This directory and all of its parents were already reserved.
                    }
                }
            }
        }

        if (planner is FolderImportPreviewService scanner)
        {
            var (_, media, _, warning) = await scanner.ScanAsync(
                library, sourceDirectory, previewOnly: false, AddItem, report, cancellationToken);
            if (warning is null)
            {
                transaction.Commit();
            }
            return (media, warning);
        }

        // Preserve support for callers supplying their own preview service.
        var preview = await planner.PreviewFolderAsync(library.Id, sourceDirectory, cancellationToken);
        if (!preview.IsComplete)
        {
            return (preview.Items.Count, preview.Warning ?? "Analýza je neúplná; kopírování se nespustí.");
        }
        if (library.FolderLayout != preview.FolderLayout || library.FileNaming != preview.FileNaming)
        {
            throw new InvalidOperationException("Během analýzy se změnila pravidla. Spusť import znovu.");
        }
        foreach (var item in preview.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddItem(item);
        }
        transaction.Commit();
        return (preview.Items.Count, null);
    }

    internal IEnumerable<ImportPreviewItem> ReadItems(CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id, Item FROM Items WHERE Id > $last ORDER BY Id LIMIT {BatchSize};";
        var last = command.Parameters.Add("$last", SqliteType.Integer);
        last.Value = 0L;
        command.Prepare();
        var batch = new List<ImportPreviewItem>(BatchSize);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch.Clear();
            // Close the reader before yielding: copying also queries directory reservations.
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    last.Value = reader.GetInt64(0);
                    batch.Add(JsonSerializer.Deserialize(reader.GetString(1), FolderImportPlanJsonContext.Default.ImportPreviewItem)
                        ?? throw new InvalidDataException("Nelze načíst položku plánu importu."));
                }
            }
            foreach (var item in batch)
            {
                yield return item;
            }
            if (batch.Count < BatchSize)
            {
                yield break;
            }
        }
    }

    internal bool IsReservedDirectory(string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM Directories WHERE Path = $path;";
        command.Parameters.AddWithValue("$path", path);
        return command.ExecuteScalar() is not null;
    }
}

[JsonSerializable(typeof(ImportPreviewItem))]
internal partial class FolderImportPlanJsonContext : JsonSerializerContext
{
}
