namespace DbMcp.Data.Configuration;

public sealed record ResilienceSettings
{
    public RetrySettings Retry { get; init; } = new();
    public TimeoutSettings Timeout { get; init; } = new();
}

public sealed record RetrySettings
{
    public bool Enabled { get; init; } = false;
    public int MaxAttempts { get; init; } = 3;
    public int BaseDelayMs { get; init; } = 200;
}

public sealed record TimeoutSettings
{
    public bool Enabled { get; init; } = true;
}
