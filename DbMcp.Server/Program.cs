using DbMcp.Data.Configuration;
using DbMcp.Data.Services;
using DbMcp.Server;
using DbMcp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

// One-shot CLI: -register / -unregister self-install into Claude Desktop, then exit
// BEFORE any host / DI / stdio wiring runs. Must be the very first statement so the
// verbs never touch the JSON-RPC stdout channel. Any other invocation (incl. a stray
// arg) returns false and falls through to start the stdio server unchanged.
if (Installer.TryHandleCli(args, out var cliExitCode))
    return cliExitCode;

// Pin the exe directory as the FIRST executable statement, before the host,
// the bootstrap logger, or any config read runs. An MCP host (Claude Code)
// spawns this binary with an arbitrary working directory; without this every
// relative path below (bootstrap log file, appsettings.json, runtime logs)
// would resolve against that unknown CWD. AppContext.BaseDirectory is already
// the running assembly's directory (with a trailing separator) and is never
// null, so it is used directly — no Path.GetDirectoryName(...)! wrapper, which
// only worked by luck of the trailing separator. See mcp-dotnet KB §17.7.
var exeDir = AppContext.BaseDirectory;
Directory.SetCurrentDirectory(exeDir);

// stdio MCP: stdout carries the JSON-RPC frame. EVERY log byte must go to
// stderr (standardErrorFromLevel: Verbose routes ALL levels to stderr) or the
// file — NEVER stdout. A default Console sink writes stdout and corrupts the
// protocol handshake. The bootstrap logger fires BEFORE config is read, so the
// stderr routing must be set here in code — it cannot wait for appsettings.
// This is a true cold-start window; a stray stdout write here breaks the
// handshake before the server starts. See mcp-dotnet KB §2.2. #1 stdio footgun.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(
        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
        standardErrorFromLevel: LogEventLevel.Verbose)
    .WriteTo.File(Path.Combine(exeDir, "logs", "dbmcp-startup-.log"), rollingInterval: RollingInterval.Day)
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Belt-and-suspenders with Directory.SetCurrentDirectory above: the JSON
    // config provider resolves appsettings.json via ContentRootPath, which
    // defaults to the CWD. Pinning both covers config resolution AND any other
    // relative path, and survives a refactor that drops either one. See D-CWD.
    builder.Environment.ContentRootPath = exeDir;

    // Remove the default ConsoleLoggerProvider — Host.CreateApplicationBuilder
    // registers it and it writes to STDOUT, a second protocol-corruption vector
    // independent of Serilog's own Console sink. AddSerilog ADDS a provider; it
    // does not remove this one. Clearing first guarantees Serilog (stderr+file)
    // is the ONLY logging provider, so framework/Microsoft.* logs can never
    // reach stdout. Do NOT 'add back' console logging. See mcp-dotnet KB §2.2.
    builder.Logging.ClearProviders();

    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext());

    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = "dbmcp",
                Version = "1.0.0"
            };
            options.ServerInstructions = """
                Database MCP — multi-engine database introspection and query execution.

                IMPORTANT: Call list_connections first to discover available database connections.

                Tool overview:
                - list_connections: Show all configured database connections (alias, engine, database name)
                - list_schemas: List schemas in a database
                - list_tables: List tables and views with approximate row counts
                - describe_table: Get columns, primary keys, indexes, and foreign keys for a table
                - execute_query: Run a read-only SELECT query. Returns a JSON object { "rows": [...], "returned_rows": N } (an object, not a bare array). No row cap — bound with TOP/LIMIT; ~43s wall-clock timeout
                - execute_nonquery: Run DDL/DML statements (CREATE, INSERT, UPDATE, DELETE, ALTER, DROP); optional batchSeparator splits into batches and useTransaction controls atomic-vs-per-batch commit
                - execute_script: Execute a .sql file (transactional by default); optional batchSeparator splits into batches and useTransaction controls atomic-vs-per-batch commit
                """;
        })
        .WithStdioServerTransport()
        .WithTools<ConnectionTools>()
        .WithTools<SchemaTools>()
        .WithTools<QueryTools>();

    // Eager, fail-loud config binding (§19.1): a missing/empty connection
    // registry is a config error that must stop the server at boot — not yield
    // a server whose every tool call fails. Verbatim from the source host.
    var connections = builder.Configuration
        .GetSection("Connections")
        .Get<Dictionary<string, ConnectionEntry>>()
        ?? throw new InvalidOperationException("No 'Connections' section found in appsettings.json");

    if (connections.Count == 0)
        throw new InvalidOperationException("At least one connection must be configured in 'Connections'");

    var resilienceSettings = builder.Configuration
        .GetSection("Resilience")
        .Get<ResilienceSettings>() ?? new ResilienceSettings();

    var databaseSettings = builder.Configuration
        .GetSection("Database")
        .Get<DatabaseSettings>() ?? new DatabaseSettings();

    builder.Services.AddSingleton(sp => new DatabaseService(
        connections,
        sp.GetRequiredService<ILogger<DatabaseService>>(),
        resilienceSettings,
        databaseSettings
    ));

    var host = builder.Build();

    // Force-resolve DatabaseService at boot so a bad binding / unsupported
    // engine surfaces as a clean Log.Fatal on stderr here, not as a first-tool-
    // call surprise (§19.1). Verified safe: the DatabaseService ctor only stashes
    // config and builds engine + resilience-pipeline objects — it does NOT open a
    // DB connection, so a momentarily-down database does not make startup flaky.
    // See D-EAGER / R-EAGER.
    _ = host.Services.GetRequiredService<DatabaseService>();

    // Safe to log here — Serilog routes to stderr+file only. No app.Urls banner:
    // stdio has no bound address.
    Log.Information("dbmcp stdio server starting — {Count} connections", connections.Count);

    await host.RunAsync();
    // The -register/-unregister CLI path returns above; the synthesized entry point
    // is therefore Task<int>, so the server path must also return. 0 = clean shutdown.
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "dbmcp terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
