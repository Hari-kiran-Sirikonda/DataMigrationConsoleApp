using Microsoft.Data.SqlClient;
using Npgsql;
using log4net;
using log4net.Config;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Text;

class BaseVersion
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(BaseVersion));

    private enum ServerType
    {
        SqlServer,
        PostgreSql
    }

    private sealed record TableMapping(
        ServerType SourceServerType,
        string SourceHost,
        int SourcePort,
        string SourceDatabase,
        string SourceUsername,
        string SourcePassword,
        string SourceTable,
        ServerType DestinationServerType,
        string DestinationHost,
        int DestinationPort,
        string DestinationDatabase,
        string DestinationUsername,
        string DestinationPassword,
        string DestinationTable);

    private sealed record TableReference(string Schema, string Table);

    public record MissingColumnInfo(string MissingFrom, string ColumnName);

    static void Main()
    {
        string logConfigPath = Path.Combine(AppContext.BaseDirectory, "log4net.config");
        XmlConfigurator.Configure(new FileInfo(logConfigPath));

        try
        {
            List<TableMapping> mappings = LoadTableMappings();

            Log.Info($"Found {mappings.Count} table mapping(s).");

            foreach (TableMapping mapping in mappings)
            {
                MigrateTablePair(mapping);
            }
        }
        catch (FileNotFoundException ex)
        {
            Log.Error($"Required file was not found: {ex.FileName}", ex);
        }
        catch (InvalidDataException ex)
        {
            Log.Error("The table mapping file is invalid.", ex);
        }
        catch (InvalidOperationException ex)
        {
            Log.Error("Configuration error.", ex);
        }
        catch (Exception ex)
        {
            Log.Error("An unexpected error occurred.", ex);
        }
    }

    private static List<TableMapping> LoadTableMappings()
    {
        //Connection strings file
        string mappingsPath = Path.Combine(AppContext.BaseDirectory, "TableMappings.csv");

        if (!File.Exists(mappingsPath))
        {
            throw new FileNotFoundException("The table mapping CSV file was not found.", mappingsPath);
        }

        string[] lines = File.ReadAllLines(mappingsPath);
        string[] connections = ParseCsvLine(lines[1]);

        if (lines.Length == 0)
        {
            throw new InvalidDataException("The CSV file is empty.");
        }

        string[] headers = ParseCsvLine(lines[0]);
        string[] expectedHeaders =
        {
            "SourceServerType",
            "SourceHost",
            "SourcePort",
            "SourceDatabase",
            "SourceUsername",
            "DestinationServerType",
            "DestinationHost",
            "DestinationPort",
            "DestinationDatabase",
            "DestinationUsername",
        };

        string expectedHeaderText = string.Join(',', expectedHeaders);

        if (headers.Length != expectedHeaders.Length)
        {
            throw new InvalidDataException($"The first row must contain: {expectedHeaderText}.");
        }

        for (int i = 0; i < expectedHeaders.Length; i++)
        {
            if (!headers[i].Equals(expectedHeaders[i], StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The first row must contain: {expectedHeaderText}.");
            }
        }

        //Reading the Table names and validating the data
        string tableNamesPath = Path.Combine(AppContext.BaseDirectory, "Tables.csv");

        if (!File.Exists(tableNamesPath))
        {
            throw new FileNotFoundException("The table mapping CSV file was not found.", tableNamesPath);
        }

        string[] tables = File.ReadAllLines(tableNamesPath);

        if (tables.Length == 0)
        {
            throw new InvalidDataException("The CSV file is empty.");
        }

        string[] tableHeaders = ParseCsvLine(tables[0]);
        string[] expectedTableHeaders =
        {
            "SourceTables",
            "DestinationTables",
        };

        string expectedTableHeaderText = string.Join(',', expectedTableHeaders);

        if (tableHeaders.Length != expectedTableHeaders.Length)
        {
            throw new InvalidDataException($"The first row must contain: {expectedTableHeaderText}.");
        }

        for (int i = 0; i < expectedTableHeaders.Length; i++)
        {
            if (!tableHeaders[i].Equals(expectedTableHeaders[i], StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The first row must contain: {expectedTableHeaderText}.");
            }
        }

        var mappings = new List<TableMapping>();

        for (int lineNumber = 1; lineNumber < tables.Length; lineNumber++)
        {
            if (string.IsNullOrWhiteSpace(tables[lineNumber]))
            {
                continue;
            }

            string[] tablesPair = ParseCsvLine(tables[lineNumber]);

            if (tablesPair.Length != expectedTableHeaders.Length || Array.Exists(tablesPair, string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException($"Line {lineNumber + 1} must contain fourteen values: SourceDatabase, SourceTables, DestinationDatabase, DestinationTables.");
            }

            //Read the the source Password from the console.
            Console.WriteLine($"Enter the Password for {connections[1]} : ");
            String sourceServerPassword = Console.ReadLine();

            //Read the the destination Password from the console.
            Console.WriteLine($"Enter the Password for {connections[6]} : ");
            String destinationServerPassword = Console.ReadLine();

            mappings.Add(new TableMapping(
                ParseServerType(connections[0], lineNumber + 1),
                connections[1],
                ParsePort(connections[2], lineNumber + 1),
                connections[3],
                connections[4],
                sourceServerPassword,
                tablesPair[0],
                ParseServerType(connections[5], lineNumber + 1),
                connections[6],
                ParsePort(connections[7], lineNumber + 1),
                connections[8],
                connections[9],
                destinationServerPassword,
                tablesPair[1]));
        }

        if (mappings.Count == 0)
        {
            throw new InvalidDataException("The CSV file does not contain any table mappings.");
        }

        return mappings;
    }

    private static void checkHeaders(string[] expected, string[] headers)
    {

    }

    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        bool insideQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char current = line[i];

            if (current == '"')
            {
                if (insideQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else
                {
                    insideQuotes = !insideQuotes;
                }
            }
            else if (current == ',' && !insideQuotes)
            {
                values.Add(value.ToString().Trim());
                value.Clear();
            }
            else
            {
                value.Append(current);
            }
        }

        if (insideQuotes)
        {
            throw new InvalidDataException("A CSV field contains an unmatched quote.");
        }

        values.Add(value.ToString().Trim());
        return values.ToArray();
    }

    private static int ParsePort(string value, int lineNumber)
    {
        if (!int.TryParse(value, out int port) || port is < 1 or > 65535)
        {
            throw new InvalidDataException($"Line {lineNumber} contains an invalid port '{value}'.");
        }

        return port;
    }

    private static ServerType ParseServerType(string value, int lineNumber)
    {
        if (value.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) || value.Equals("Sql", StringComparison.OrdinalIgnoreCase))
        {
            return ServerType.SqlServer;
        }

        if (value.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase) || value.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) || value.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            return ServerType.PostgreSql;
        }

        throw new InvalidDataException($"Line {lineNumber} contains unsupported server type '{value}'. Use SqlServer or PostgreSql.");
    }

    private static DbConnection CreateConnection(ServerType serverType, string host, int port, string database, string username, string password)
    {
        if (serverType == ServerType.SqlServer)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = port == 0 ? host : $"{host},{port}",
                InitialCatalog = database,
                UserID = username,
                Password = password,
                PersistSecurityInfo = false,
                Pooling = false,
                MultipleActiveResultSets = false,
                Encrypt = true,
                TrustServerCertificate = true,
                ApplicationName = "Data Migration Tool"
            };

            return new SqlConnection(builder.ConnectionString);
        }

        var postgresBuilder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = password
        };

        return new NpgsqlConnection(postgresBuilder.ConnectionString);
    }

    private static void MigrateTablePair(TableMapping mapping)
    {
        string sourceLocation = $"{mapping.SourceServerType}:{mapping.SourceTable}";
        string destinationLocation = $"{mapping.DestinationServerType}:{mapping.DestinationTable}";
        Log.Info($"Starting migration: {sourceLocation} -> {destinationLocation}");

        try
        {
            using DbConnection sourceConnection = CreateConnection(mapping.SourceServerType, mapping.SourceHost, mapping.SourcePort, mapping.SourceDatabase, mapping.SourceUsername, mapping.SourcePassword);
            Console.WriteLine($"--Conection Successful for the source server {mapping.SourceHost}.");
            using DbConnection destinationConnection = CreateConnection(mapping.DestinationServerType, mapping.DestinationHost, mapping.DestinationPort, mapping.DestinationDatabase, mapping.DestinationUsername, mapping.DestinationPassword);
            Console.WriteLine($"--Conection Successful for the source server {mapping.DestinationHost}.");

            sourceConnection.Open();
            destinationConnection.Open();

            if (mapping.SourceServerType == ServerType.SqlServer && mapping.DestinationServerType == ServerType.SqlServer)
            {
                MigrateSqlServerPair((SqlConnection)sourceConnection, (SqlConnection)destinationConnection, mapping);
            }
            else if (mapping.SourceServerType == ServerType.PostgreSql && mapping.DestinationServerType == ServerType.PostgreSql)
            {
                MigratePostgreSqlPair((NpgsqlConnection)sourceConnection, (NpgsqlConnection)destinationConnection, mapping);
            }
            else
            {
                MigrateCrossProviderPair(sourceConnection, destinationConnection, mapping);
            }
        }
        catch (DbException ex)
        {
            Log.Error($"Database error for {sourceLocation} -> {destinationLocation}.", ex);
        }
        catch (TimeoutException ex)
        {
            Log.Error($"Timeout for {sourceLocation} -> {destinationLocation}.", ex);
        }
        catch (InvalidOperationException ex)
        {
            Log.Error($"Operation error for {sourceLocation} -> {destinationLocation}.", ex);
        }
        catch (Exception ex)
        {
            Log.Error($"Unexpected error for {sourceLocation} -> {destinationLocation}.", ex);
        }
    }

    private static void MigrateCrossProviderPair(DbConnection sourceConnection, DbConnection destinationConnection, TableMapping mapping)
    {
        HashSet<string> sourceColumns = GetTableColumnNames(sourceConnection, mapping.SourceTable, mapping.SourceServerType);
        HashSet<string> destinationColumns = GetTableColumnNames(destinationConnection, mapping.DestinationTable, mapping.DestinationServerType);
        ValidateColumns(sourceColumns, destinationColumns, mapping);
        LogColumnComparison(sourceColumns, destinationColumns, mapping);

        TableReference sourceTable = ParseTableName(mapping.SourceTable, mapping.SourceServerType);
        TableReference destinationTable = ParseTableName(mapping.DestinationTable, mapping.DestinationServerType);

        using DbCommand sourceCommand = sourceConnection.CreateCommand();
        sourceCommand.CommandText = $"SELECT * FROM {QuoteTable(sourceTable, mapping.SourceServerType)}";
        using DbDataReader reader = sourceCommand.ExecuteReader();

        var matchingColumns = new List<(int SourceOrdinal, string Name)>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            string sourceColumnName = reader.GetName(i);

            if (destinationColumns.Contains(sourceColumnName))
            {
                matchingColumns.Add((i, sourceColumnName));
                Log.Info($"[MATCH FOUND] Source column '{sourceColumnName}' exists in destination table '{mapping.DestinationTable}'.");
            }
            else
            {
                Log.Warn($"[SKIPPED] Source column '{sourceColumnName}' does not exist in destination table '{mapping.DestinationTable}'.");
            }
        }

        if (matchingColumns.Count == 0)
        {
            Log.Warn("No matching columns found. Operation skipped for this pair.");
            return;
        }

        string columnList = string.Join(", ", matchingColumns.Select(column => QuoteIdentifier(column.Name, mapping.DestinationServerType)));
        string parameterList = string.Join(", ", matchingColumns.Select((_, index) => $"@p{index}"));

        using DbCommand insertCommand = destinationConnection.CreateCommand();
        insertCommand.CommandText = $"INSERT INTO {QuoteTable(destinationTable, mapping.DestinationServerType)} ({columnList}) VALUES ({parameterList})";

        for (int i = 0; i < matchingColumns.Count; i++)
        {
            DbParameter parameter = insertCommand.CreateParameter();
            parameter.ParameterName = $"@p{i}";
            insertCommand.Parameters.Add(parameter);
        }

        long rowsCopied = 0;
        while (reader.Read())
        {
            for (int i = 0; i < matchingColumns.Count; i++)
            {
                object value = reader.GetValue(matchingColumns[i].SourceOrdinal);
                insertCommand.Parameters[i].Value = value is DBNull ? DBNull.Value : value;
            }

            insertCommand.ExecuteNonQuery();
            rowsCopied++;
        }

        Log.Info($"Data copied successfully: {mapping.SourceTable} -> {mapping.DestinationTable}. Rows copied: {rowsCopied}");
    }

    private static void MigrateSqlServerPair(SqlConnection sourceConnection, SqlConnection destinationConnection, TableMapping mapping)
    {
        HashSet<string> sourceColumns = GetTableColumnNames(sourceConnection, mapping.SourceTable, ServerType.SqlServer);
        HashSet<string> destinationColumns = GetTableColumnNames(destinationConnection, mapping.DestinationTable, ServerType.SqlServer);
        ValidateColumns(sourceColumns, destinationColumns, mapping);
        LogColumnComparison(sourceColumns, destinationColumns, mapping);

        TableReference sourceTable = ParseTableName(mapping.SourceTable, ServerType.SqlServer);
        using SqlCommand command = new($"SELECT * FROM {QuoteTable(sourceTable, ServerType.SqlServer)}", sourceConnection);
        using SqlDataReader reader = command.ExecuteReader();
        using SqlBulkCopy bulkCopy = new(destinationConnection, SqlBulkCopyOptions.Default, null)
        {
            DestinationTableName = QuoteTable(ParseTableName(mapping.DestinationTable, ServerType.SqlServer), ServerType.SqlServer),
            BatchSize = 5000,
            BulkCopyTimeout = 600
        };

        for (int i = 0; i < reader.FieldCount; i++)
        {
            string sourceColumnName = reader.GetName(i);

            if (destinationColumns.Contains(sourceColumnName))
            {
                bulkCopy.ColumnMappings.Add(sourceColumnName, sourceColumnName);
                Log.Info($"[MATCH FOUND] Source column '{sourceColumnName}' exists in destination table '{mapping.DestinationTable}'.");
            }
            else
            {
                Log.Warn($"[SKIPPED] Source column '{sourceColumnName}' does not exist in destination table '{mapping.DestinationTable}'.");
            }
        }

        if (bulkCopy.ColumnMappings.Count == 0)
        {
            Log.Warn("No matching columns found. Operation skipped for this pair.");
            return;
        }

        bulkCopy.WriteToServer(reader);
        Log.Info($"Data copied successfully: {mapping.SourceTable} -> {mapping.DestinationTable}");
    }

    private static void MigratePostgreSqlPair(NpgsqlConnection sourceConnection, NpgsqlConnection destinationConnection, TableMapping mapping)
    {
        HashSet<string> sourceColumns = GetTableColumnNames(sourceConnection, mapping.SourceTable, ServerType.PostgreSql);
        HashSet<string> destinationColumns = GetTableColumnNames(destinationConnection, mapping.DestinationTable, ServerType.PostgreSql);
        ValidateColumns(sourceColumns, destinationColumns, mapping);
        LogColumnComparison(sourceColumns, destinationColumns, mapping);

        TableReference sourceTable = ParseTableName(mapping.SourceTable, ServerType.PostgreSql);
        TableReference destinationTable = ParseTableName(mapping.DestinationTable, ServerType.PostgreSql);
        using NpgsqlCommand command = new($"SELECT * FROM {QuoteTable(sourceTable, ServerType.PostgreSql)}", sourceConnection);
        using NpgsqlDataReader reader = command.ExecuteReader();

        var matchingColumns = new List<(int SourceOrdinal, string Name)>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            string sourceColumnName = reader.GetName(i);

            if (destinationColumns.Contains(sourceColumnName))
            {
                matchingColumns.Add((i, sourceColumnName));
                Log.Info($"[MATCH FOUND] Source column '{sourceColumnName}' exists in destination table '{mapping.DestinationTable}'.");
            }
            else
            {
                Log.Warn($"[SKIPPED] Source column '{sourceColumnName}' does not exist in destination table '{mapping.DestinationTable}'.");
            }
        }

        if (matchingColumns.Count == 0)
        {
            Log.Warn("No matching columns found. Operation skipped for this pair.");
            return;
        }

        string columnList = string.Join(", ", matchingColumns.Select(column => QuoteIdentifier(column.Name, ServerType.PostgreSql)));
        string copyCommand = $"COPY {QuoteTable(destinationTable, ServerType.PostgreSql)} ({columnList}) FROM STDIN (FORMAT BINARY)";

        using NpgsqlBinaryImporter importer = destinationConnection.BeginBinaryImport(copyCommand);
        while (reader.Read())
        {
            importer.StartRow();

            foreach ((int sourceOrdinal, string _) in matchingColumns)
            {
                object value = reader.GetValue(sourceOrdinal);

                if (value is DBNull)
                {
                    importer.WriteNull();
                }
                else
                {
                    importer.Write(value);
                }
            }
        }

        importer.Complete();
        Log.Info($"Data copied successfully: {mapping.SourceTable} -> {mapping.DestinationTable}");
    }

    private static HashSet<string> GetTableColumnNames(DbConnection connection, string tableName, ServerType serverType)
    {
        TableReference table = ParseTableName(tableName, serverType);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string query = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @Schema AND TABLE_NAME = @Table ORDER BY ORDINAL_POSITION";

        using DbCommand command = connection.CreateCommand();
        command.CommandText = query;

        DbParameter schemaParameter = command.CreateParameter();
        schemaParameter.ParameterName = "@Schema";
        schemaParameter.Value = table.Schema;
        command.Parameters.Add(schemaParameter);

        DbParameter tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "@Table";
        tableParameter.Value = table.Table;
        command.Parameters.Add(tableParameter);

        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static TableReference ParseTableName(string tableName, ServerType serverType)
    {
        string[] parts = tableName.Split('.', StringSplitOptions.TrimEntries);

        if (parts.Length > 2 || parts.Length == 0 || Array.Exists(parts, string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException($"Table name '{tableName}' is invalid.");
        }

        string defaultSchema = serverType == ServerType.PostgreSql ? "public" : "dbo";
        return parts.Length == 2 ? new TableReference(parts[0], parts[1]) : new TableReference(defaultSchema, parts[0]);
    }

    private static string QuoteTable(TableReference table, ServerType serverType)
    {
        return $"{QuoteIdentifier(table.Schema, serverType)}.{QuoteIdentifier(table.Table, serverType)}";
    }

    private static string QuoteIdentifier(string identifier, ServerType serverType)
    {
        return serverType == ServerType.PostgreSql
            ? $"\"{identifier.Replace("\"", "\"\"")}\""
            : $"[{identifier.Replace("]", "]]" )}]";
    }

    private static void ValidateColumns(HashSet<string> sourceColumns, HashSet<string> destinationColumns, TableMapping mapping)
    {
        if (sourceColumns.Count == 0)
        {
            throw new InvalidOperationException($"Source table '{mapping.SourceTable}' was not found or contains no columns.");
        }

        if (destinationColumns.Count == 0)
        {
            throw new InvalidOperationException($"Destination table '{mapping.DestinationTable}' was not found or contains no columns in database '{mapping.DestinationDatabase}'.");
        }
    }

    private static void LogColumnComparison(HashSet<string> sourceColumns, HashSet<string> destinationColumns, TableMapping mapping)
    {
        Log.Info($"Count of Source Columns: {sourceColumns.Count}");
        Log.Info($"Count of Destination Columns: {destinationColumns.Count}");

        var missingColumns = FindMissingColumns(sourceColumns, destinationColumns, mapping.SourceTable, mapping.DestinationTable);
        if (missingColumns.Count == 0)
        {
            Log.Info("No columns are missing.");
            return;
        }

        Log.Warn("The following columns are missing:");
        foreach (MissingColumnInfo item in missingColumns)
        {
            Log.Warn($"Missing from {item.MissingFrom}: {item.ColumnName}");
        }
    }

    public static List<MissingColumnInfo> FindMissingColumns(HashSet<string> tableAColumns, HashSet<string> tableBColumns, string tableAName, string tableBName)
    {
        ArgumentNullException.ThrowIfNull(tableAColumns);
        ArgumentNullException.ThrowIfNull(tableBColumns);

        var missingColumns = new List<MissingColumnInfo>();

        foreach (string column in tableAColumns)
        {
            if (!tableBColumns.Contains(column))
            {
                missingColumns.Add(new MissingColumnInfo(tableBName, column));
            }
        }

        foreach (string column in tableBColumns)
        {
            if (!tableAColumns.Contains(column))
            {
                missingColumns.Add(new MissingColumnInfo(tableAName, column));
            }
        }

        return missingColumns;
    }
}