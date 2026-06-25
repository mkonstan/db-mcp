namespace DbMcp.Data.Configuration;

public sealed record DatabaseSettings
{
    public int CommandTimeoutSeconds { get; init; } = 43;
}
