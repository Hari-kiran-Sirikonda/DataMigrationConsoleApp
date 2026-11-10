# Prompt: Modular RDBMS Data Migration Tool (Console to Windows Service Ready)

## Context & Objective
Build a modular, configurable database migration engine in **C# (.NET 8 / .NET 9)**.
- **Current Milestone:** An interactive Console Application demonstrating the end-to-end migration process.
- **Future Milestone:** The core engine must be completely isolated in a standalone Class Library (`Migration.Core`) so it can be plugged directly into a Windows Service (`Microsoft.Extensions.Hosting.WindowsServices` / `BackgroundService`) to run periodically in the background without modifying any database or migration logic.

---

## Architectural Principles & Design Patterns
1. **Separation of Concerns:**
   - `Migration.Core`: Interfaces, abstractions, batch pagination engine, schema validation, logging models, config models.
   - `Migration.Providers`: Database adapters implementing `IDatabaseProvider` for SQL Server (`Microsoft.Data.SqlClient`) and PostgreSQL (`Npgsql`).
   - `Migration.ConsoleApp`: CLI interface that handles interactive inputs, builds the DI container, and triggers `IMigrationOrchestrator`.
2. **Provider-Agnostic Matrix:**
   - The user selects the source and destination database engines dynamically:
     - SQL Server $\rightarrow$ SQL Server
     - SQL Server $\rightarrow$ PostgreSQL
     - PostgreSQL $\rightarrow$ SQL Server
     - PostgreSQL $\rightarrow$ PostgreSQL
   - **Strict Query Rule:** No stored procedures. All data reads, counts, schema checks, and writes must use parameterized dynamic SQL queries.
3. **Chunking / Pagination Mechanism:**
   - Do NOT load entire tables into memory.
   - Read data in configured batch sizes ($N$ records per chunk).
   - Use deterministic pagination (e.g., `ORDER BY [PrimaryKey/FirstColumn] OFFSET @Offset ROWS FETCH NEXT @BatchSize ROWS ONLY` for SQL Server; `ORDER BY "PrimaryKey/FirstColumn" LIMIT @BatchSize OFFSET @Offset` for PostgreSQL).
4. **Resilience & Production Readiness:**
   - Structured logging via `Microsoft.Extensions.Logging` (console + rolling file log via Serilog).
   - Transactional safety: Each chunk/batch runs within a transaction (`DbTransaction`). If a chunk fails, rollback that chunk and log the failure.
   - Graceful cancellation support via `CancellationToken`.

---

## Detailed Functional Requirements

### 1. Database Selection & Input Gathering
The application should first prompt for (or read from `appsettings.json`):
1. **Source & Destination Providers:** Select provider type (`SqlServer` or `PostgreSql`) and enter respective connection strings.
2. **Batch / Unit Size:** Prompt for the number of records to migrate per chunk (e.g., `10`, `500`, `1000`).
3. **Bulk Table Selection:**
   - The user inputs all target table names at once (e.g., comma-separated: `Users, Orders, Products, Invoices`).
   - **Symmetric Table Names:** The destination table names are identical to the source table names (e.g., source `Orders` copies directly to destination `Orders`). Destination tables already exist with matching schemas and are empty.
4. **Per-Table Filter Configuration:**
   - For each table in the list, prompt the user:
     - Option A: Migrate the entire table.
     - Option B: Apply a custom conditional query/filter (e.g., `CreatedAt >= '2026-01-01' AND Status = 'Active'`).
   - If a filter condition is provided, sanitize and append it as a `WHERE` predicate to the extraction query.

### 2. Pre-Flight Destination Existence Check
Before transferring records for any table:
- Check whether the table exists in the destination database:
  - **SQL Server:** Query `INFORMATION_SCHEMA.TABLES` or `sys.tables`.
  - **PostgreSQL:** Query `information_schema.tables` (accounting for schema, e.g., `public`).
- **Conditional Branching:**
  - **If the table exists:** Proceed to data transfer and write a log entry: `[INFO] Destination table '{TableName}' verified. Starting migration...`
  - **If the table does NOT exist:** Do not crash. Log a clear warning/error: `[WARNING] Destination table '{TableName}' does not exist. Skipping table.` Continue to the next table in the queue.

### 3. Chunked Data Migration Flow
For each verified table:
1. **Fetch Total Count:** Run `SELECT COUNT(*) FROM {TableName} [WHERE {Filter}]` on the source.
2. **Calculate Batches:** Total Batches = $\lceil \text{TotalRecords} / \text{BatchSize} \rceil$.
3. **Transfer in Units:**
   - Loop from batch $1$ to $M$:
     - Read the chunk from the source using a streaming reader (`DbDataReader`).
     - Dynamically construct a parameterized multi-row `INSERT INTO {DestinationTable} (...) VALUES (...)` statement matching the columns returned by the reader.
     - Execute the insert on the destination inside a transaction and commit.
     - Emit progress log:
       `[INFO] [{TableName}] Chunk {CurrentBatch}/{TotalBatches} transferred ({TransferredCount}/{TotalCount} records). Elapsed: {Duration}ms.`

### 4. Summary & Audit Reporting
At the end of the entire migration run, print a formatted console table summarizing:
- Table Name
- Status (`Completed`, `Skipped - Table Missing`, `Failed`)
- Total Records Processed
- Total Batches Executed
- Time Taken (seconds / milliseconds)

---

## Deliverables Expected from the AI Agent
1. **Clean Solution Structure:**
   - `MigrationTool.Core`: Interfaces (`IDatabaseProvider`, `IMigrationOrchestrator`, `ISchemaValidator`), models, and batch pagination engine.
   - `MigrationTool.Providers.SqlServer`: SQL Server dialect, connection handling, pagination, and insertion logic.
   - `MigrationTool.Providers.PostgreSql`: PostgreSQL dialect, connection handling, pagination, and insertion logic.
   - `MigrationTool.ConsoleApp`: Interactive CLI inputs, DI setup, and orchestration trigger.
2. **Complete, Working C# Code:** Complete implementations of all core classes without placeholders or truncated code.
3. **Windows Service Extensibility Note:** A clean example showing how `IMigrationOrchestrator` can be called inside a `BackgroundService` (`ExecuteAsync`) for continuous or scheduled background runs.


================================================================================
                    MODULAR RDBMS DATA MIGRATION ENGINE                         
================================================================================

[1/4] Provider Configuration:
-----------------------------
Select Source Database:
  1) Microsoft SQL Server
  2) PostgreSQL
> 1

Enter Source Connection String:
> Server=127.0.0.1;Database=SourceInventory;User Id=sa;Password=••••••••;TrustServerCertificate=True;

Select Destination Database:
  1) Microsoft SQL Server
  2) PostgreSQL
> 2

Enter Destination Connection String:
> Host=127.0.0.1;Port=5432;Database=TargetInventory;Username=postgres;Password=••••••••;

[2/4] Batch Chunk Configuration:
--------------------------------
Enter unit/batch size (Records per transfer cycle) [Default: 500]:
> 100

[3/4] Table Selection:
----------------------
Enter table names to migrate (comma-separated):
> Users, Orders, InventoryItems, AuditLogs

[4/4] Per-Table Filter Setup:
-----------------------------
- Table [Users]:
  Apply a custom WHERE filter? (y/N): n
  -> Mode: Full table transfer

- Table [Orders]:
  Apply a custom WHERE filter? (y/N): y
  Enter WHERE predicate (without 'WHERE'): OrderDate >= '2026-01-01' AND Status = 'Completed'
  -> Mode: Filtered transfer

- Table [InventoryItems]:
  Apply a custom WHERE filter? (y/N): n
  -> Mode: Full table transfer

- Table [AuditLogs]:
  Apply a custom WHERE filter? (y/N): y
  Enter WHERE predicate (without 'WHERE'): LogLevel IN ('Error', 'Critical')
  -> Mode: Filtered transfer

================================================================================
Configuration validated. Starting migration pipeline...
================================================================================