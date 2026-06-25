using System.Text.Json.Nodes;
using DbMcp.Server;

namespace DbMcp.Data.Tests;

public class InstallerTests : IDisposable
{
    private readonly string _tempDir;

    public InstallerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dbmcp-installer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void ApplyToConfig_AddsServerKey()
    {
        var root = new JsonObject();

        Installer.ApplyToConfig(root, @"C:\dbmcp\DbMcp.Server.exe");

        var entry = root["mcpServers"]!["dbmcp"]!;
        Assert.Equal(@"C:\dbmcp\DbMcp.Server.exe", entry["command"]!.GetValue<string>());
        Assert.Empty(entry["args"]!.AsArray());
    }

    [Fact]
    public void ApplyToConfig_PreservesUnrelatedServersAndSecretSibling()
    {
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["other-server"] = new JsonObject { ["command"] = "other.exe" }
            },
            ["apiKey"] = "super-secret-credential"
        };

        Installer.ApplyToConfig(root, "dbmcp.exe");

        Assert.Equal("other.exe", root["mcpServers"]!["other-server"]!["command"]!.GetValue<string>());
        Assert.Equal("dbmcp.exe", root["mcpServers"]!["dbmcp"]!["command"]!.GetValue<string>());
        Assert.Equal("super-secret-credential", root["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public void ApplyToConfig_OverwritesExistingDbmcpEntry()
    {
        var root = new JsonObject();
        Installer.ApplyToConfig(root, "old.exe");

        Installer.ApplyToConfig(root, "new.exe");

        Assert.Equal("new.exe", root["mcpServers"]!["dbmcp"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void RemoveFromConfig_RemovesPresentKey()
    {
        var root = new JsonObject();
        Installer.ApplyToConfig(root, "dbmcp.exe");

        var removed = Installer.RemoveFromConfig(root);

        Assert.True(removed);
        Assert.Null(Installer.ExistingCommand(root));
    }

    [Fact]
    public void RemoveFromConfig_AbsentKeyReturnsFalse()
    {
        var root = new JsonObject { ["mcpServers"] = new JsonObject() };

        Assert.False(Installer.RemoveFromConfig(root));
    }

    [Fact]
    public void ExistingCommand_ReturnsNullWhenAbsent()
    {
        Assert.Null(Installer.ExistingCommand(new JsonObject()));
    }

    [Fact]
    public void ExistingCommand_ReturnsRegisteredCommand()
    {
        var root = new JsonObject();
        Installer.ApplyToConfig(root, "dbmcp.exe");

        Assert.Equal("dbmcp.exe", Installer.ExistingCommand(root));
    }

    [Fact]
    public void IsRuntimeHost_TrueForDotnetHost()
    {
        Assert.True(Installer.IsRuntimeHost(@"C:\Program Files\dotnet\dotnet.exe"));
    }

    [Fact]
    public void IsRuntimeHost_FalseForApphost()
    {
        Assert.False(Installer.IsRuntimeHost(@"C:\dbmcp\DbMcp.Server.exe"));
    }

    [Fact]
    public void RegisterInto_PreservesUnrelatedServerAndSecret_AddsDbmcp()
    {
        var configPath = Path.Combine(_tempDir, "claude_desktop_config.json");
        var original = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["other-server"] = new JsonObject { ["command"] = "other.exe" }
            },
            ["apiKey"] = "super-secret-credential"
        };
        File.WriteAllText(configPath, original.ToJsonString());

        var exit = Installer.RegisterInto(new[] { configPath }, "dbmcp.exe", configPath);

        Assert.Equal(0, exit);
        var written = (JsonObject)JsonNode.Parse(File.ReadAllText(configPath))!;
        Assert.Equal("other.exe", written["mcpServers"]!["other-server"]!["command"]!.GetValue<string>());
        Assert.Equal("dbmcp.exe", written["mcpServers"]!["dbmcp"]!["command"]!.GetValue<string>());
        Assert.Equal("super-secret-credential", written["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public void RegisterInto_CreatesBackupOfExistingFile()
    {
        var configPath = Path.Combine(_tempDir, "claude_desktop_config.json");
        File.WriteAllText(configPath, "{}");

        Installer.RegisterInto(new[] { configPath }, "dbmcp.exe", configPath);

        var backups = Directory.GetFiles(_tempDir, "claude_desktop_config.json.*.bak");
        Assert.Single(backups);
    }

    [Fact]
    public void RegisterInto_NoBackupWhenFileDidNotExist()
    {
        var configPath = Path.Combine(_tempDir, "claude_desktop_config.json");

        Installer.RegisterInto(new[] { configPath }, "dbmcp.exe", configPath);

        Assert.True(File.Exists(configPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.bak"));
    }

    [Fact]
    public void RegisterInto_EmptyTargets_WritesFallback()
    {
        var fallback = Path.Combine(_tempDir, "fallback", "claude_desktop_config.json");

        var exit = Installer.RegisterInto(Array.Empty<string>(), "dbmcp.exe", fallback);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(fallback));
        var written = (JsonObject)JsonNode.Parse(File.ReadAllText(fallback))!;
        Assert.Equal("dbmcp.exe", written["mcpServers"]!["dbmcp"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void RegisterInto_RefusesUnparseableFile_LeavesItUnchanged()
    {
        var configPath = Path.Combine(_tempDir, "claude_desktop_config.json");
        const string garbage = "{ this is not valid json";
        File.WriteAllText(configPath, garbage);

        var exit = Installer.RegisterInto(new[] { configPath }, "dbmcp.exe", configPath);

        Assert.Equal(1, exit);
        Assert.Equal(garbage, File.ReadAllText(configPath));
    }

    [Fact]
    public void UnregisterFrom_RemovesDbmcp_KeepsOtherServerAndSecret()
    {
        var configPath = Path.Combine(_tempDir, "claude_desktop_config.json");
        var original = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["other-server"] = new JsonObject { ["command"] = "other.exe" },
                ["dbmcp"] = new JsonObject { ["command"] = "dbmcp.exe" }
            },
            ["apiKey"] = "super-secret-credential"
        };
        File.WriteAllText(configPath, original.ToJsonString());

        var exit = Installer.UnregisterFrom(new[] { configPath });

        Assert.Equal(0, exit);
        var written = (JsonObject)JsonNode.Parse(File.ReadAllText(configPath))!;
        Assert.Null(written["mcpServers"]!["dbmcp"]);
        Assert.Equal("other.exe", written["mcpServers"]!["other-server"]!["command"]!.GetValue<string>());
        Assert.Equal("super-secret-credential", written["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public void UnregisterFrom_RefusesUnparseable_ReturnsFailure()
    {
        var configPath = Path.Combine(_tempDir, "claude_desktop_config.json");
        const string garbage = "{ not json";
        File.WriteAllText(configPath, garbage);

        var exit = Installer.UnregisterFrom(new[] { configPath });

        Assert.Equal(1, exit);
        Assert.Equal(garbage, File.ReadAllText(configPath));
    }

    [Fact]
    public void TryHandleCli_NoArgs_FallsThroughToServer()
    {
        var handled = Installer.TryHandleCli(Array.Empty<string>(), out var exit);

        Assert.False(handled);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void TryHandleCli_UnknownVerb_HandledAsError()
    {
        var handled = Installer.TryHandleCli(new[] { "-bogus" }, out var exit);

        Assert.True(handled);
        Assert.Equal(1, exit);
    }

    [Fact]
    public void TryHandleCli_StrayArg_FallsThroughToServer()
    {
        var handled = Installer.TryHandleCli(new[] { "somefile.sql" }, out var exit);

        Assert.False(handled);
        Assert.Equal(0, exit);
    }
}
