using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ClickTracker.Clicks;

/// <summary>Stores clicks in a local SQLite database.</summary>
public sealed class SqliteClickStore : IClickStore
{
    private const string CreateSchema = """
        CREATE TABLE IF NOT EXISTS clicks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_at_utc TEXT NOT NULL,
            link_id TEXT NOT NULL,
            target_url TEXT NOT NULL,
            client_address TEXT NULL,
            method TEXT NOT NULL,
            scheme TEXT NOT NULL,
            host TEXT NOT NULL,
            headers_json TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_clicks_link_id ON clicks(link_id);
        CREATE INDEX IF NOT EXISTS ix_clicks_occurred_at ON clicks(occurred_at_utc);
        """;

    private const string InsertClick = """
        INSERT INTO clicks (occurred_at_utc, link_id, target_url, client_address, method, scheme, host, headers_json)
        VALUES ($occurredAt, $linkId, $targetUrl, $clientAddress, $method, $scheme, $host, $headers);
        """;

    private readonly string _databasePath;

    public SqliteClickStore(IOptions<ClicksOptions> options, IHostEnvironment environment)
    {
        var path = options.Value.DatabasePath;
        _databasePath = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
    }

    /// <summary>The full path of the database file.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>Creates the database and the table if they do not exist yet.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; " + CreateSchema;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task WriteAsync(IReadOnlyList<ClickRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = InsertClick;

        var occurredAt = command.Parameters.Add("$occurredAt", SqliteType.Text);
        var linkId = command.Parameters.Add("$linkId", SqliteType.Text);
        var targetUrl = command.Parameters.Add("$targetUrl", SqliteType.Text);
        var clientAddress = command.Parameters.Add("$clientAddress", SqliteType.Text);
        var method = command.Parameters.Add("$method", SqliteType.Text);
        var scheme = command.Parameters.Add("$scheme", SqliteType.Text);
        var host = command.Parameters.Add("$host", SqliteType.Text);
        var headers = command.Parameters.Add("$headers", SqliteType.Text);

        foreach (var record in records)
        {
            occurredAt.Value = record.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture);
            linkId.Value = record.LinkId;
            targetUrl.Value = record.TargetUrl;
            clientAddress.Value = (object?)record.ClientAddress ?? DBNull.Value;
            method.Value = record.Method;
            scheme.Value = record.Scheme;
            host.Value = record.Host;
            headers.Value = JsonSerializer.Serialize(
                record.Headers,
                AppJsonSerializerContext.Default.IReadOnlyDictionaryStringStringArray);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        await connection.OpenAsync(cancellationToken);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }
}
