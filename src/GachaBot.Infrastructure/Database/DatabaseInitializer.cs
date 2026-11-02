using GachaBot.Application.Publishing;
using Microsoft.EntityFrameworkCore;

namespace GachaBot.Infrastructure.Database;

public sealed class DatabaseInitializer(
    ISourceDatabaseFactory databaseFactory,
    IGuildDestinationStore guildDestinations)
{
    public DatabaseInitializer(ISourceDatabaseFactory databaseFactory)
        : this(databaseFactory, new LegacyGuildDestinationStore())
    {
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await guildDestinations.InitializeAsync(cancellationToken).ConfigureAwait(false);
        foreach (var key in databaseFactory.DatabaseKeys)
        {
            await EnsureSourceAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Makes a source schema compatible with the current model before it is read or written.
    /// This is deliberately idempotent: a worker may call it immediately before ingestion,
    /// protecting deployments that start source polling without going through the web host.
    /// </summary>
    public async Task EnsureSourceAsync(string sourceKey, CancellationToken cancellationToken = default)
    {
        await using var dbContext = databaseFactory.CreateDbContext(sourceKey);
        await EnsurePostgresTablesAsync(dbContext, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetDateColumnDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = databaseFactory.CreateDbContext(databaseFactory.DatabaseKeys[0]);
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT table_schema || '.' || table_name || '.' || column_name || ': ' || data_type
                FROM information_schema.columns
                WHERE table_catalog = current_database()
                  AND (column_name LIKE '%AtUtc' OR table_name = 'SourceStates')
                ORDER BY table_schema, table_name, column_name;
                """;
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(0));
            }

            return columns;
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task EnsurePostgresTablesAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsNpgsql())
        {
            await dbContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var schema = dbContext.Schema ?? throw new InvalidOperationException("PostgreSQL source schema is missing.");
        var escapedSchema = schema.Replace("\"", "\"\"", StringComparison.Ordinal);
        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State == System.Data.ConnectionState.Closed;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using (var schemaCommand = connection.CreateCommand())
            {
                schemaCommand.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{escapedSchema}\";";
                await schemaCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var tableCommand = connection.CreateCommand();
            tableCommand.CommandText = "SELECT to_regclass(@tableName)::text;";
            var parameter = tableCommand.CreateParameter();
            parameter.ParameterName = "@tableName";
            parameter.Value = $"\"{escapedSchema}\".\"ContentItems\"";
            tableCommand.Parameters.Add(parameter);
            var table = await tableCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (table is null or DBNull)
            {
                var schemaStatement = $"CREATE SCHEMA \"{escapedSchema}\";";
                var createScript = dbContext.Database.GenerateCreateScript()
                    .Replace(schemaStatement, string.Empty, StringComparison.Ordinal);
                await using var createCommand = connection.CreateCommand();
                createCommand.CommandText = createScript;
                await createCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await NormalizeLegacyDateColumnsAsync(connection, schema, cancellationToken).ConfigureAwait(false);
            await AddPublicationPurposeAsync(connection, schema, cancellationToken).ConfigureAwait(false);
            await AssertNoLegacyDateColumnsAsync(connection, schema, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task NormalizeLegacyDateColumnsAsync(
        System.Data.Common.DbConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        var legacyColumns = new List<(string Table, string Column)>();
        await using (var findCommand = connection.CreateCommand())
        {
            findCommand.CommandText = """
                SELECT table_name, column_name
                FROM information_schema.columns
                WHERE table_schema = @schema
                  AND data_type = 'bigint'
                  AND (
                      (table_name = 'SourceStates' AND column_name IN (
                          'LastAttemptUtc',
                          'LastModifiedUtc',
                          'LastSuccessfulRunUtc'))
                      OR column_name LIKE '%AtUtc'
                  );
                """;
            var schemaParameter = findCommand.CreateParameter();
            schemaParameter.ParameterName = "@schema";
            schemaParameter.Value = schema;
            findCommand.Parameters.Add(schemaParameter);
            await using var reader = await findCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                legacyColumns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (table, column) in legacyColumns)
        {
            var quotedSchema = QuoteIdentifier(schema);
            var quotedTable = QuoteIdentifier(table);
            var quotedColumn = QuoteIdentifier(column);
            await using var convertCommand = connection.CreateCommand();
            convertCommand.CommandText = $"""
                ALTER TABLE {quotedSchema}.{quotedTable}
                ALTER COLUMN {quotedColumn} TYPE timestamp with time zone
                USING CASE
                    WHEN {quotedColumn} IS NULL THEN NULL
                    ELSE to_timestamp(
                        (((({quotedColumn} >> 11) * 1000)::numeric
                           - ((({quotedColumn} & 2047) - 1024) * 600000000)
                           - 621355968000000000)
                          / 10000000)::double precision)
                END;
                """;
            await convertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AddPublicationPurposeAsync(
        System.Data.Common.DbConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        var publications = $"{QuoteIdentifier(schema)}.{QuoteIdentifier("Publications")}";
        var contentItems = $"{QuoteIdentifier(schema)}.{QuoteIdentifier("ContentItems")}";
        await using (var findCommand = connection.CreateCommand())
        {
            findCommand.CommandText = """
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = @schema AND table_name = 'Publications' AND column_name = 'Purpose';
                """;
            var schemaParameter = findCommand.CreateParameter();
            schemaParameter.ParameterName = "@schema";
            schemaParameter.Value = schema;
            findCommand.Parameters.Add(schemaParameter);
            // The backfill below is a one-time legacy migration; rerunning it on every start turned
            // regular posts about events into "Event starting" posts, ignoring guild settings.
            if (await findCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return;
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            ALTER TABLE {publications}
            ADD COLUMN IF NOT EXISTS "Purpose" character varying(32) NOT NULL DEFAULT 'Standard';

            UPDATE {publications} AS publication
            SET "Purpose" = 'EventStart'
            FROM {contentItems} AS content
            WHERE publication."ContentId" = content."Id"
              AND content."Kind" = 'Event'
              AND publication."Purpose" = 'Standard';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AssertNoLegacyDateColumnsAsync(
        System.Data.Common.DbConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = @schema
              AND data_type = 'bigint'
              AND column_name LIKE '%AtUtc'
            ORDER BY table_name, column_name;
            """;
        var schemaParameter = command.CreateParameter();
        schemaParameter.ParameterName = "@schema";
        schemaParameter.Value = schema;
        command.Parameters.Add(schemaParameter);
        var legacyColumn = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (legacyColumn is not null and not DBNull)
        {
            throw new InvalidOperationException(
                $"PostgreSQL schema '{schema}' still has a legacy bigint date column: {legacyColumn}.");
        }
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
