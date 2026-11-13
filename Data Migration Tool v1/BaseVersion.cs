using log4net;
using log4net.Config;
using Microsoft.Data.SqlClient;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

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
        string DestinationTable,
        string WhereConditionString);

    private sealed record TableReference(string Schema, string Table);

    private sealed record ColumnMapping(int SourceOrdinal, string SourceName, string DestinationName);

    private sealed record DestinationConnectionKey(
        ServerType ServerType,
        string Host,
        int Port,
        string Database,
        string Username,
        string Password);

    private sealed record ForeignKeyDependency(TableReference Parent, TableReference Child);

    public record MissingColumnInfo(string MissingFrom, string ColumnName);

    static void Main()
    {
        string logConfigPath = Path.Combine(AppContext.BaseDirectory, "log4net.config");
        XmlConfigurator.Configure(new FileInfo(logConfigPath));

        try
        {
                List<TableMapping> mappings = LoadTableMappings();
                mappings = OrderMappingsByDestinationDependencies(mappings);

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

        string[] expectedConnectionHeaders =
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
        string[] connectionHeaders = ParseCsvLine(lines[0]);
        validateHeaders(expectedConnectionHeaders, connectionHeaders);

        //Reading the Table names and validating the data
        string tableNamesPath = Path.Combine(AppContext.BaseDirectory, "Tables.csv");

        string[] tablePairs = File.ReadAllLines(tableNamesPath);

        string[] tableHeaders = ParseCsvLine(tablePairs[0]);
        string[] expectedTableHeaders =
        {
            "SourceTables",
            "DestinationTables",
            "WhereConditionString",
        };
        validateHeaders(expectedTableHeaders, tableHeaders);

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

        //Read the the source Password from the console.
        Console.Write($"Enter the Password for {connections[1]} : ");
        String sourceServerPassword = Console.ReadLine();

        //Read the the destination Password from the console.
        Console.Write($"Enter the Password for {connections[6]} : ");
        String destinationServerPassword = Console.ReadLine();

        //Read wether the source and destination table names are same
        Console.Write($"Are the source and destination table names are same (y/n) : ");
        String inputSameTableNames = Console.ReadLine();
        String sameTableNames = inputSameTableNames.ToLower();

        for (int lineNumber = 1; lineNumber < tablePairs.Length; lineNumber++)
        {
            if (string.IsNullOrWhiteSpace(tablePairs[lineNumber]))
            {
                continue;
            }

            string[] pair = ParseCsvLine(tablePairs[lineNumber]);

            if (pair.Length != expectedTableHeaders.Length || Array.Exists(pair, string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException($"Line {lineNumber + 1} must contain fourteen values: SourceDatabase, SourceTables, DestinationDatabase, DestinationTables.");
            }

            if(sameTableNames == "y")
            {
                mappings.Add(new TableMapping(
                ParseServerType(connections[0], lineNumber + 1),
                connections[1],
                ParsePort(connections[2], lineNumber + 1),
                connections[3],
                connections[4],
                sourceServerPassword,
                pair[0],
                ParseServerType(connections[5], lineNumber + 1),
                connections[6],
                ParsePort(connections[7], lineNumber + 1),
                connections[8],
                connections[9],
                destinationServerPassword,
                pair[0],
                pair[2]));
            }
            else if (sameTableNames == "n")
            {
                mappings.Add(new TableMapping(
                ParseServerType(connections[0], lineNumber + 1),
                connections[1],
                ParsePort(connections[2], lineNumber + 1),
                connections[3],
                connections[4],
                sourceServerPassword,
                pair[0],
                ParseServerType(connections[5], lineNumber + 1),
                connections[6],
                ParsePort(connections[7], lineNumber + 1),
                connections[8],
                connections[9],
                destinationServerPassword,
                pair[1],
                pair[2]));
            }
            else
            {
                throw new InvalidDataException("The Option should be either 'y' or 'n'");
            }
        }

        if (mappings.Count == 0)
        {
            throw new InvalidDataException("The CSV file does not contain any table mappings.");
        }

        return mappings;
    }

    public static string ToSnakeCase(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        // 1. Insert underscore between an acronym and the start of a new word (e.g., "HTTPRequest" -> "HTTP_Request")
        string step1 = Regex.Replace(input, @"([A-Z]+)([A-Z][a-z])", "$1_$2");

        // 2. Insert underscore between a lowercase letter/digit and an uppercase letter (e.g., "BusinessUnit" -> "Business_Unit")
        string step2 = Regex.Replace(step1, @"([a-z\d])([A-Z])", "$1_$2");

        return step2.ToLowerInvariant();
    }

    private static List<TableMapping> OrderMappingsByDestinationDependencies(List<TableMapping> mappings)
    {
        var orderedMappings = new List<TableMapping>(mappings.Count);

        foreach (IGrouping<DestinationConnectionKey, TableMapping> group in mappings.GroupBy(mapping => new DestinationConnectionKey(
            mapping.DestinationServerType,
            mapping.DestinationHost,
            mapping.DestinationPort,
            mapping.DestinationDatabase,
            mapping.DestinationUsername,
            mapping.DestinationPassword)))
        {
            using DbConnection connection = CreateConnection(
                group.Key.ServerType,
                group.Key.Host,
                group.Key.Port,
                group.Key.Database,
                group.Key.Username,
                group.Key.Password);
            connection.Open();

            List<TableMapping> groupMappings = group.ToList();
            List<ForeignKeyDependency> dependencies = GetForeignKeyDependencies(connection, group.Key.ServerType);
            orderedMappings.AddRange(TopologicallySortMappings(groupMappings, dependencies));
        }

        return orderedMappings;
    }

    private static List<ForeignKeyDependency> GetForeignKeyDependencies(DbConnection connection, ServerType serverType)
    {
        const string sqlServerQuery =
            "SELECT parentSchema.name, parentTable.name, childSchema.name, childTable.name " +
            "FROM sys.foreign_keys AS fk " +
            "INNER JOIN sys.tables AS parentTable ON parentTable.object_id = fk.referenced_object_id " +
            "INNER JOIN sys.schemas AS parentSchema ON parentSchema.schema_id = parentTable.schema_id " +
            "INNER JOIN sys.tables AS childTable ON childTable.object_id = fk.parent_object_id " +
            "INNER JOIN sys.schemas AS childSchema ON childSchema.schema_id = childTable.schema_id";

        const string postgresQuery =
            "SELECT parentNamespace.nspname, parentTable.relname, childNamespace.nspname, childTable.relname " +
            "FROM pg_constraint AS constraintInfo " +
            "INNER JOIN pg_class AS parentTable ON parentTable.oid = constraintInfo.confrelid " +
            "INNER JOIN pg_namespace AS parentNamespace ON parentNamespace.oid = parentTable.relnamespace " +
            "INNER JOIN pg_class AS childTable ON childTable.oid = constraintInfo.conrelid " +
            "INNER JOIN pg_namespace AS childNamespace ON childNamespace.oid = childTable.relnamespace " +
            "WHERE constraintInfo.contype = 'f'";

        using DbCommand command = connection.CreateCommand();
        command.CommandText = serverType == ServerType.SqlServer ? sqlServerQuery : postgresQuery;

        using DbDataReader reader = command.ExecuteReader();
        var dependencies = new List<ForeignKeyDependency>();

        while (reader.Read())
        {
            dependencies.Add(new ForeignKeyDependency(
                new TableReference(reader.GetString(0), reader.GetString(1)),
                new TableReference(reader.GetString(2), reader.GetString(3))));
        }

        return dependencies;
    }

    private static List<TableMapping> TopologicallySortMappings(List<TableMapping> mappings,List<ForeignKeyDependency> dependencies)
    {
        var mappingByTable = new Dictionary<string, TableMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (TableMapping mapping in mappings)
        {
            string tableKey = GetTableKey(ParseTableName(mapping.DestinationTable, mapping.DestinationServerType));
            if (!mappingByTable.TryAdd(tableKey, mapping))
            {
                throw new InvalidOperationException($"Destination table '{mapping.DestinationTable}' is mapped more than once.");
            }
        }

        var childTablesByParent = mappingByTable.Keys.ToDictionary(key => key, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var dependencyCounts = mappingByTable.Keys.ToDictionary(key => key, _ => 0, StringComparer.OrdinalIgnoreCase);

        foreach (ForeignKeyDependency dependency in dependencies)
        {
            string parentKey = GetTableKey(dependency.Parent);
            string childKey = GetTableKey(dependency.Child);

            if (!mappingByTable.ContainsKey(parentKey) || !mappingByTable.ContainsKey(childKey))
            {
                continue;
            }

            if (parentKey.Equals(childKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Self-referencing foreign-key dependency detected on destination table '{dependency.Child.Schema}.{dependency.Child.Table}'. This table requires a two-phase migration or a deferrable constraint.");
            }

            if (!childTablesByParent[parentKey].Contains(childKey, StringComparer.OrdinalIgnoreCase))
            {
                childTablesByParent[parentKey].Add(childKey);
                dependencyCounts[childKey]++;
            }
        }

        var remainingKeys = new HashSet<string>(mappingByTable.Keys, StringComparer.OrdinalIgnoreCase);
        var result = new List<TableMapping>(mappings.Count);

        while (remainingKeys.Count > 0)
        {
            string nextKey = mappings
                .Select(mapping => GetTableKey(ParseTableName(mapping.DestinationTable, mapping.DestinationServerType)))
                .FirstOrDefault(key => remainingKeys.Contains(key) && dependencyCounts[key] == 0);

            if (nextKey is null)
            {
                string cycle = string.Join(", ", remainingKeys.Select(key => mappingByTable[key].DestinationTable));
                throw new InvalidOperationException($"Circular foreign-key dependency detected among destination tables: {cycle}. These tables require a two-phase migration or deferrable constraints.");
            }

            remainingKeys.Remove(nextKey);
            result.Add(mappingByTable[nextKey]);

            foreach (string childKey in childTablesByParent[nextKey])
            {
                dependencyCounts[childKey]--;
            }
        }

        return result;
    }

    private static string GetTableKey(TableReference table)
    {
        return $"{table.Schema.ToLowerInvariant()}.{table.Table.ToLowerInvariant()}";
    }

    private static void validateHeaders(string[] expected, string[] headers)
    {
        string expectedHeaderText = string.Join(',', expected);

        if (headers.Length != expected.Length)
        {
            throw new InvalidDataException($"The first row must contain: {expectedHeaderText}.");
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (!headers[i].Equals(expected[i], StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The first row must contain: {expectedHeaderText}.");
            }
        }
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

            if (mapping.SourceServerType == ServerType.SqlServer && mapping.DestinationServerType == ServerType.SqlServer && !(string.IsNullOrWhiteSpace(mapping.WhereConditionString)))
            {
                if (!(string.IsNullOrWhiteSpace(mapping.WhereConditionString)))
                {
                    MigrateSqlServerPair((SqlConnection)sourceConnection, (SqlConnection)destinationConnection, mapping);
                }
                else
                {
                    Log.Warn($"[SKIPPED] Table Migration from '{mapping.SourceDatabase}.{mapping.SourceTable}' --> '{mapping.DestinationDatabase}.{mapping.DestinationTable}'.");
                }
            }
            else if (mapping.SourceServerType == ServerType.PostgreSql && mapping.DestinationServerType == ServerType.PostgreSql)
            {
                if (!(string.IsNullOrWhiteSpace(mapping.WhereConditionString)))
                {
                    MigratePostgreSqlPair((NpgsqlConnection)sourceConnection, (NpgsqlConnection)destinationConnection, mapping);
                }
                else
                {
                    Log.Warn($"[SKIPPED] Table Migration from '{mapping.SourceDatabase}.{mapping.SourceTable}' --> '{mapping.DestinationDatabase}.{mapping.DestinationTable}'.");
                }
            }
            else
            {
                if (!(string.IsNullOrWhiteSpace(mapping.WhereConditionString))){
                    MigrateCrossProviderPair(sourceConnection, destinationConnection, mapping);
                }
                else
                {
                    Log.Warn($"[SKIPPED] Table Migration from '{mapping.SourceDatabase}.{mapping.SourceTable}' --> '{mapping.DestinationDatabase}.{mapping.DestinationTable}'.");
                }
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
        sourceCommand.CommandText = $"SELECT * FROM {QuoteTable(sourceTable, mapping.SourceServerType)} where {mapping.WhereConditionString}";
        using DbDataReader reader = sourceCommand.ExecuteReader();

        Dictionary<string, string> destinationColumnsByNormalizedName = BuildNormalizedColumnMap(destinationColumns, mapping.DestinationTable);
        var matchingColumns = new List<ColumnMapping>();
        var sourceColumnsByNormalizedName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            string sourceColumnName = reader.GetName(i);
            string normalizedSourceName = ToSnakeCase(sourceColumnName);

            if (destinationColumnsByNormalizedName.TryGetValue(normalizedSourceName, out string destinationColumnName))
            {
                if (!sourceColumnsByNormalizedName.Add(normalizedSourceName))
                {
                    throw new InvalidOperationException($"Source table '{mapping.SourceTable}' contains multiple columns that normalize to '{normalizedSourceName}'.");
                }

                matchingColumns.Add(new ColumnMapping(i, sourceColumnName, destinationColumnName));
                Log.Info($"[MATCH FOUND] Source column '{sourceColumnName}' maps to destination column '{destinationColumnName}' in table '{mapping.DestinationTable}'.");
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

        string columnList = string.Join(", ", matchingColumns.Select(column => QuoteIdentifier(column.DestinationName, mapping.DestinationServerType)));
        string parameterList = string.Join(", ", matchingColumns.Select((_, index) => $"@p{index}"));

        //This is the command used to insert the rows from source to destination.
        using DbCommand insertCommand = destinationConnection.CreateCommand();
        insertCommand.CommandText = $"INSERT INTO {QuoteTable(destinationTable, mapping.DestinationServerType)} ({columnList}) VALUES ({parameterList})";

        for (int i = 0; i < matchingColumns.Count; i++)
        {
            DbParameter parameter = insertCommand.CreateParameter();
            parameter.ParameterName = $"@p{i}";
            insertCommand.Parameters.Add(parameter);
        }

        bool identityInsertEnabled = false;

        try
        {
            if (mapping.DestinationServerType == ServerType.SqlServer &&
                matchingColumns.Any(column => IsSqlServerIdentityColumn(
                    (SqlConnection)destinationConnection,
                    destinationTable,
                    column.DestinationName)))
            {
                using SqlCommand identityInsertCommand = new(
                    $"SET IDENTITY_INSERT {QuoteTable(destinationTable, ServerType.SqlServer)} ON",
                    (SqlConnection)destinationConnection);
                identityInsertCommand.ExecuteNonQuery();
                identityInsertEnabled = true;
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

            Log.Info($"Data Migrated successfully: {mapping.SourceTable} -> {mapping.DestinationTable}. Rows copied: {rowsCopied}");
        }
        finally
        {
            if (identityInsertEnabled)
            {
                using SqlCommand identityInsertCommand = new(
                    $"SET IDENTITY_INSERT {QuoteTable(destinationTable, ServerType.SqlServer)} OFF",
                    (SqlConnection)destinationConnection);
                identityInsertCommand.ExecuteNonQuery();
            }

        }
    }

    private static bool IsSqlServerIdentityColumn(SqlConnection connection, TableReference table, string columnName)
    {
        using SqlCommand command = new(
            "SELECT COUNT(*) FROM sys.identity_columns AS ic " +
            "INNER JOIN sys.tables AS t ON t.object_id = ic.object_id " +
            "INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id " +
            "INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
            "WHERE s.name = @schema AND t.name = @table AND c.name = @column",
            connection);
        command.Parameters.AddWithValue("@schema", table.Schema);
        command.Parameters.AddWithValue("@table", table.Table);
        command.Parameters.AddWithValue("@column", columnName);

        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static void MigrateSqlServerPair(SqlConnection sourceConnection, SqlConnection destinationConnection, TableMapping mapping)
    {
        HashSet<string> sourceColumns = GetTableColumnNames(sourceConnection, mapping.SourceTable, ServerType.SqlServer);
        HashSet<string> destinationColumns = GetTableColumnNames(destinationConnection, mapping.DestinationTable, ServerType.SqlServer);
        ValidateColumns(sourceColumns, destinationColumns, mapping);
        LogColumnComparison(sourceColumns, destinationColumns, mapping);

        TableReference sourceTable = ParseTableName(mapping.SourceTable, ServerType.SqlServer);
        using SqlCommand command = new($"SELECT * FROM {QuoteTable(sourceTable, ServerType.SqlServer)} where {mapping.WhereConditionString}", sourceConnection);
        using SqlDataReader reader = command.ExecuteReader();
        using SqlBulkCopy bulkCopy = new(destinationConnection, SqlBulkCopyOptions.KeepIdentity, null)
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
        Log.Info($"Data Migrated successfully: {mapping.SourceTable} -> {mapping.DestinationTable}");
    }

    private static void MigratePostgreSqlPair(NpgsqlConnection sourceConnection, NpgsqlConnection destinationConnection, TableMapping mapping)
    {
        HashSet<string> sourceColumns = GetTableColumnNames(sourceConnection, mapping.SourceTable, ServerType.PostgreSql);
        HashSet<string> destinationColumns = GetTableColumnNames(destinationConnection, mapping.DestinationTable, ServerType.PostgreSql);
        ValidateColumns(sourceColumns, destinationColumns, mapping);
        LogColumnComparison(sourceColumns, destinationColumns, mapping);

        TableReference sourceTable = ParseTableName(mapping.SourceTable, ServerType.PostgreSql);
        TableReference destinationTable = ParseTableName(mapping.DestinationTable, ServerType.PostgreSql);
        using NpgsqlCommand command = new($"SELECT * FROM {QuoteTable(sourceTable, ServerType.PostgreSql)} where {mapping.WhereConditionString}", sourceConnection);
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
        Log.Info($"Data Migrated successfully: {mapping.SourceTable} -> {mapping.DestinationTable}");
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

    private static Dictionary<string, string> BuildNormalizedColumnMap(IEnumerable<string> columnNames, string tableName)
    {
        var normalizedColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string columnName in columnNames)
        {
            string normalizedName = ToSnakeCase(columnName);

            if (normalizedColumns.TryGetValue(normalizedName, out string existingColumnName))
            {
                throw new InvalidOperationException($"Destination table '{tableName}' contains columns '{existingColumnName}' and '{columnName}' that both normalize to '{normalizedName}'.");
            }

            normalizedColumns.Add(normalizedName, columnName);
        }

        return normalizedColumns;
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

        List<MissingColumnInfo> missingColumns = mapping.SourceServerType == mapping.DestinationServerType
            ? FindMissingColumns(sourceColumns, destinationColumns, mapping.SourceTable, mapping.DestinationTable)
            : FindMissingColumnsByNormalizedName(sourceColumns, destinationColumns, mapping.SourceTable, mapping.DestinationTable);
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

    private static List<MissingColumnInfo> FindMissingColumnsByNormalizedName(
        HashSet<string> sourceColumns,
        HashSet<string> destinationColumns,
        string sourceTableName,
        string destinationTableName)
    {
        Dictionary<string, string> sourceColumnsByNormalizedName = BuildNormalizedColumnMap(sourceColumns, sourceTableName);
        Dictionary<string, string> destinationColumnsByNormalizedName = BuildNormalizedColumnMap(destinationColumns, destinationTableName);
        var missingColumns = new List<MissingColumnInfo>();

        foreach (KeyValuePair<string, string> sourceColumn in sourceColumnsByNormalizedName)
        {
            if (!destinationColumnsByNormalizedName.ContainsKey(sourceColumn.Key))
            {
                missingColumns.Add(new MissingColumnInfo(destinationTableName, sourceColumn.Value));
            }
        }

        foreach (KeyValuePair<string, string> destinationColumn in destinationColumnsByNormalizedName)
        {
            if (!sourceColumnsByNormalizedName.ContainsKey(destinationColumn.Key))
            {
                missingColumns.Add(new MissingColumnInfo(sourceTableName, destinationColumn.Value));
            }
        }

        return missingColumns;
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