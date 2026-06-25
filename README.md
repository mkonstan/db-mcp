# dbmcp

A self-contained **stdio** MCP server for multi-engine database introspection and query execution. Supports **PostgreSQL** and **SQL Server** via named connections. stdio transport only (`WithStdioServerTransport()`) — no HTTP, REST, or web surface.

## Tools

| Tool | Purpose |
|------|---------|
| `list_connections` | Show configured connections (alias, engine, database). Call first. |
| `list_schemas` | List schemas in a database |
| `list_tables` | List tables and views with approximate row counts |
| `describe_table` | Columns, primary keys, indexes, foreign keys for a table |
| `execute_query` | Read-only SELECT (no row cap — bound with `TOP`/`LIMIT`; ~43s timeout) |
| `execute_nonquery` | DDL/DML (CREATE/INSERT/UPDATE/DELETE/ALTER/DROP); optional `batchSeparator` + `useTransaction` |
| `execute_script` | Execute a `.sql` file (transactional by default); optional `batchSeparator` + `useTransaction` |

`batchSeparator` splits the input on lines equal to a token (e.g. `GO`) and runs each batch in order; `useTransaction` (default `true`) wraps all batches in one transaction (atomic) versus committing per batch.

## Configure connections

Connections live under `Connections` in `DbMcp.Server/appsettings.json`. The repo ships a single placeholder (`tempdb-sql`) — replace it with your own. Each entry needs an `Engine` (`Postgres` or `SqlServer`) and a `ConnectionString`:

```json
"Connections": {
  "my-pg":  { "Engine": "Postgres",  "ConnectionString": "Host=localhost;Database=mydb;Username=user;Password=..." },
  "my-sql": { "Engine": "SqlServer", "ConnectionString": "Server=localhost;Database=mydb;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;" }
}
```

Keep real credentials out of source control — use `appsettings.Development.json` (gitignored) or environment-specific overrides for anything sensitive.

## Build & publish

```
dotnet build db-mcp.sln
dotnet publish DbMcp.Server -c Release    # → publish/
```

The publish output is a self-contained Windows x64 folder (deliberately not single-file — Serilog discovers sinks by scanning on-disk assemblies). Run `publish/DbMcp.Server.exe`.

## Register with a client

The published exe self-registers into Claude Desktop's config(s) — backs up each config first, merges without clobbering other servers:

```
publish\register.bat      REM add dbmcp to every Claude Desktop config on this machine
publish\unregister.bat    REM remove it
```

Or register manually by pointing the client at the absolute path of the published exe:

```json
{ "mcpServers": { "dbmcp": { "command": "C:/path/to/publish/DbMcp.Server.exe" } } }
```

Prefer the published exe over `dotnet run` — `dotnet run` build output can pollute stdout and break the JSON-RPC handshake.

## stdout is the protocol

In a stdio MCP server, **stdout carries the JSON-RPC frame**. ALL logging goes to **stderr** and `publish/logs/` — never stdout. Two stdout-pollution vectors are closed in `Program.cs`: Serilog's Console sink is routed to stderr (`standardErrorFromLevel: Verbose`), and the default `ConsoleLoggerProvider` is removed via `builder.Logging.ClearProviders()`. Do not re-add a stdout log sink or the default console logger.

## Design notes

- **Magic-free:** no row cap, no query rewriting, no result truncation. `execute_query` returns whatever the query produced, bounded only by a wall-clock command timeout (default 43s). Bound large queries yourself with `TOP`/`LIMIT`.
- **Resilience:** retry is off by default; a Polly timeout is staggered just outside the command timeout so the database engine cancels the query first and raises a normal timeout error.
- **SQL Server local dev:** connection strings use `Encrypt=False` by default — adjust for your environment.
