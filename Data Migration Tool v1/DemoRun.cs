using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using static System.Net.Mime.MediaTypeNames;
using static System.Runtime.InteropServices.JavaScript.JSType;

class Program
{
    static void Main()
    {
        string sourceConnString = "Data Source = 10.171.48.234; Initial Catalog = ExperienceAnalyzer_Luma; Persist Security Info = True; User ID = up_user; Password =up@321; Pooling = False; MultipleActiveResultSets = False; Encrypt = True; TrustServerCertificate = True; Application Name = \"SQL Server Management Studio\"; Command Timeout = 0";
        string destConnString = "Data Source = 10.171.48.234; Initial Catalog = ExperienceAnalyzer_Sample; Persist Security Info = True; User ID = up_user; Password =up@321; Pooling = False; MultipleActiveResultSets = False; Encrypt = True; TrustServerCertificate = True; Application Name = \"SQL Server Management Studio\"; Command Timeout = 0";

        string sourceTable = "ia.CallVolume";
        string destTable = "ia.callvolume_test";

        using (SqlConnection sourceConn = new SqlConnection(sourceConnString))
        using (SqlConnection destConn = new SqlConnection(destConnString))
        {
            try
            {
                sourceConn.Open();
                destConn.Open();

                // 1. Fetch all column names present in the destination table
                HashSet<string> destColumns = GetTableColumnNames(destConn, destTable);

                // 2. Query all columns from the source table
                using (SqlCommand cmd = new SqlCommand($"SELECT * FROM {sourceTable}", sourceConn))
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    using (SqlBulkCopy bulkCopy = new SqlBulkCopy(destConn, SqlBulkCopyOptions.Default, null))
                    {
                        bulkCopy.DestinationTableName = destTable;
                        bulkCopy.BatchSize = 5000;
                        bulkCopy.BulkCopyTimeout = 600;

                        // 3. Inspect reader schema and map matching columns
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            string sourceColumnName = reader.GetName(i);

                            if (destColumns.Contains(sourceColumnName))
                            {
                                // Match found: add to bulk copy mappings
                                bulkCopy.ColumnMappings.Add(sourceColumnName, sourceColumnName);
                            }
                            else
                            {
                                // Log and skip columns not present in the destination
                                Console.WriteLine($"[SKIPPED] Source column '{sourceColumnName}' does not exist in destination table '{destTable}'.");
                            }
                        }

                        // Ensure at least one column matched before attempting write
                        if (bulkCopy.ColumnMappings.Count == 0)
                        {
                            Console.WriteLine("No matching columns found between source and destination tables. Operation aborted.");
                            return;
                        }

                        // 4. Stream and write the data
                        bulkCopy.WriteToServer(reader);
                        Console.WriteLine("Data copied successfully with dynamic column mapping.");
                    }
                }
            }
            catch (SqlException ex)
            {
                Console.WriteLine("A SQL Server error occurred.");
                Console.WriteLine($"Error number: {ex.Number}, state: {ex.State}");
                Console.WriteLine($"Message: {ex.Message}");

                if (ex.Number == 18456)
                {
                    Console.WriteLine("The SQL login was rejected. Verify the server, username, password, and SQL authentication settings.");
                }
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine($"The database operation timed out: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"The database operation could not be completed: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An unexpected error occurred: {ex.Message}");
            }
        }
    }
    static HashSet<string> GetTableColumnNames(SqlConnection conn, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] parts = tableName.Split('.');
        string schema = parts.Length > 1 ? parts[0] : "dbo";
        string table = parts.Length > 1 ? parts[1] : parts[0];

        string query = @"
            SELECT COLUMN_NAME 
            FROM INFORMATION_SCHEMA.COLUMNS 
            WHERE TABLE_SCHEMA = @Schema AND TABLE_NAME = @Table";

        using (SqlCommand cmd = new SqlCommand(query, conn))
        {
            cmd.Parameters.AddWithValue("@Schema", schema);
            cmd.Parameters.AddWithValue("@Table", table);

            using (SqlDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    columns.Add(reader.GetString(0));
                }
            }
        }

        return columns;
    }
}