namespace BusinessHarness.Core;

public sealed record AgentWorkspacePolicy
{
    public string Tenant { get; init; } = "";
    public string Agent { get; init; } = "";
    public string? Directory { get; init; }
    public List<string> AllowedProviders { get; init; } = [];
    public bool AllowReferences { get; init; }
    public bool AllowRecalledMemory { get; init; }
    public int MaxConcurrentRuns { get; init; } = 2;
}
