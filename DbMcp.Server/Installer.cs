using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbMcp.Server;

/// <summary>
/// Self-registration of this stdio server into Claude Desktop's
/// <c>claude_desktop_config.json</c> via the <c>-register</c> / <c>-unregister</c> CLI verbs.
/// </summary>
/// <remarks>
/// <para>
/// SHARED PHILOSOPHY — every write merges into a <see cref="JsonObject"/> rather than
/// re-serializing a typed config, so unrelated <c>mcpServers</c> entries and any sibling
/// keys (including live DB credentials) survive a round-trip untouched. An unparseable
/// config is refused, never clobbered. The pure-core members (<see cref="ApplyToConfig"/>,
/// <see cref="RemoveFromConfig"/>, <see cref="ExistingCommand"/>, <see cref="IsRuntimeHost"/>)
/// take their inputs by parameter so they can be unit-tested with no real AppData; the
/// path-discovery and file-IO seams are <c>internal</c> and accept injected target lists.
/// </para>
/// <para>
/// WHY a CLI lives in a stdio server: <see cref="TryHandleCli"/> runs as the very first
/// thing in the process so <c>-register</c> prints and exits without ever touching the
/// host build or the JSON-RPC stdout channel. Any other invocation (including a stray arg)
/// falls through and starts the server unchanged.
/// </para>
/// </remarks>
public static class Installer
{
    /// <summary>The <c>mcpServers</c> key written into the config. Matches the <c>mcp__dbmcp__</c> tool prefix.</summary>
    public const string ServerKey = "dbmcp";

    private const string ConfigFileName = "claude_desktop_config.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly Dictionary<string, Func<int>> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["register"] = Register,
        ["unregister"] = Unregister
    };

    /// <summary>
    /// Dispatches a leading CLI verb (<c>-register</c> / <c>-unregister</c>); returns
    /// <see langword="false"/> when there is no verb so the caller proceeds to start the server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Leading dashes are stripped so <c>-register</c> and <c>--register</c> both match. An
    /// unrecognized <em>dash-prefixed</em> token is rejected (rather than silently falling
    /// through to the server) so a typo'd verb like <c>-regsiter</c> doesn't quietly launch a
    /// long-lived stdio process the user can't see.
    /// </para>
    /// <para>
    /// DEVIATION from the reference (which rejects any unrecognized first token): a token with
    /// no leading dash is NOT treated as a verb — it falls through to start the server. WHY: an
    /// MCP host may pass the server a bare positional arg, and the task requires a stray arg to
    /// still start the server. The dash is the signal that the user meant a CLI verb.
    /// </para>
    /// </remarks>
    public static bool TryHandleCli(string[] args, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0) { exitCode = 0; return false; }   // no verb -> run the server
        var first = args[0];
        if (!first.StartsWith('-')) { exitCode = 0; return false; }   // bare arg -> run the server
        var verb = first.TrimStart('-');
        if (Verbs.TryGetValue(verb, out var handler)) { exitCode = handler(); return true; }
        Console.WriteLine($"Unknown command '{first}'. Valid: {string.Join(", ", Verbs.Keys.Select(v => "-" + v))}.\nRun with no arguments to start the MCP server.");
        exitCode = 1; return true;
    }

    /// <summary>Registers this exe into every discovered Claude Desktop config.</summary>
    /// <remarks>
    /// Refuses when launched via the <c>dotnet</c> runtime host: the registered command must be
    /// the standalone apphost exe, not <c>dotnet</c> (which would need the dll path as an arg the
    /// host won't supply). See <see cref="IsRuntimeHost"/>.
    /// </remarks>
    public static int Register()
    {
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath null — cannot self-register.");
        if (IsRuntimeHost(exePath)) { Console.WriteLine("Refusing: launched via 'dotnet' host; run from the apphost exe instead."); return 1; }
        return RegisterInto(ConfigPaths(), exePath, ClassicConfigPath());
    }

    internal static int RegisterInto(IReadOnlyList<string> targets, string exePath, string fallbackConfigPath)
    {
        if (targets.Count == 0) { Console.WriteLine("WARNING: no Claude dir found; creating classic path."); targets = new[] { fallbackConfigPath }; }
        var succeeded = 0;
        foreach (var configPath in targets)
        {
            if (!TryLoad(configPath, out var root, out var loadError)) { Console.WriteLine(loadError); continue; }
            var previous = ExistingCommand(root); ApplyToConfig(root, exePath); Save(configPath, root); succeeded++;
            Console.WriteLine(previous is null ? $"Registered '{ServerKey}': {exePath} -> {configPath}" : $"Updated '{ServerKey}': {previous} -> {exePath} @ {configPath}");
        }
        if (succeeded == 0) { Console.WriteLine("No config written (all failed to parse)."); return 1; }
        Console.WriteLine($"Wrote {succeeded} config(s). Restart Claude Desktop."); return 0;
    }

    /// <summary>Removes this server from every discovered Claude Desktop config.</summary>
    public static int Unregister() => UnregisterFrom(ConfigPaths());

    internal static int UnregisterFrom(IReadOnlyList<string> targets)
    {
        if (targets.Count == 0) { Console.WriteLine("Nothing to remove."); return 0; }
        var hadParseFailure = false; var removedSomewhere = false;
        foreach (var configPath in targets)
        {
            if (!File.Exists(configPath)) continue;
            if (!TryLoad(configPath, out var root, out var loadError)) { Console.WriteLine(loadError); hadParseFailure = true; continue; }
            if (RemoveFromConfig(root)) { Save(configPath, root); removedSomewhere = true; Console.WriteLine($"Removed from {configPath}."); }
            else Console.WriteLine($"Not present in {configPath}.");
        }
        if (removedSomewhere) Console.WriteLine("Restart Claude Desktop.");
        return hadParseFailure ? 1 : 0;
    }

    /// <summary>True when the path is the shared <c>dotnet</c> runtime host rather than a self-contained apphost.</summary>
    public static bool IsRuntimeHost(string exePath) => string.Equals(Path.GetFileNameWithoutExtension(exePath), "dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sets (or overwrites) the <see cref="ServerKey"/> entry; creates <c>mcpServers</c> if absent. Other keys untouched.</summary>
    public static void ApplyToConfig(JsonObject root, string exePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root["mcpServers"] is not JsonObject servers) { servers = new JsonObject(); root["mcpServers"] = servers; }
        servers[ServerKey] = new JsonObject { ["command"] = exePath, ["args"] = new JsonArray() };
    }

    /// <summary>Removes the <see cref="ServerKey"/> entry; returns whether it was present.</summary>
    public static bool RemoveFromConfig(JsonObject root) { ArgumentNullException.ThrowIfNull(root); return root["mcpServers"] is JsonObject servers && servers.Remove(ServerKey); }

    /// <summary>The command already registered under <see cref="ServerKey"/>, or null if not present.</summary>
    public static string? ExistingCommand(JsonObject root) { ArgumentNullException.ThrowIfNull(root); return root["mcpServers"] is JsonObject s && s[ServerKey] is JsonObject e ? e["command"]?.GetValue<string>() : null; }

    internal static IReadOnlyList<string> ConfigPaths() => ConfigPaths(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static IReadOnlyList<string> ConfigPaths(string appData, string localAppData)
    {
        var paths = new List<string>();
        var classicDir = Path.Combine(appData, "Claude");
        if (Directory.Exists(classicDir)) paths.Add(Path.Combine(classicDir, ConfigFileName));
        var packagesRoot = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packagesRoot))
            foreach (var pkgDir in Directory.EnumerateDirectories(packagesRoot, "Claude_*"))
            { var d = Path.Combine(pkgDir, "LocalCache", "Roaming", "Claude"); if (Directory.Exists(d)) paths.Add(Path.Combine(d, ConfigFileName)); }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string ClassicConfigPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", ConfigFileName);

    private static bool TryLoad(string configPath, out JsonObject root, out string error)
    {
        root = new JsonObject(); error = "";
        if (!File.Exists(configPath)) return true;
        try
        {
            var text = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(text)) return true;
            if (JsonNode.Parse(text) is JsonObject parsed) { root = parsed; return true; }
            error = $"Refusing to modify {configPath}: top-level JSON is not an object."; return false;
        }
        catch (JsonException ex) { error = $"Refusing to modify {configPath}: not valid JSON ({ex.Message})."; return false; }
    }

    /// <summary>
    /// Writes the merged config back, creating the directory if needed.
    /// </summary>
    /// <remarks>
    /// WHY the backup (the one deviation from the reference): the real config holds live DB
    /// credentials in sibling keys. Before overwriting an existing file, copy it to a
    /// timestamped <c>.&lt;yyyyMMddHHmmss&gt;.bak</c> sibling — cheap insurance against a
    /// future round-trip bug ever dropping a secret. <see cref="DateTime.Now"/> (not UTC):
    /// the stamp is a human-facing recovery aid, not a workflow key.
    /// </remarks>
    private static void Save(string configPath, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        if (File.Exists(configPath))
            File.Copy(configPath, $"{configPath}.{DateTime.Now:yyyyMMddHHmmss}.bak");
        File.WriteAllText(configPath, root.ToJsonString(WriteOptions));
    }
}
