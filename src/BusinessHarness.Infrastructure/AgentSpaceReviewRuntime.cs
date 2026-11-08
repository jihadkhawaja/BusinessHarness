using System.Text.Json;
using AgentSpace;
using BusinessHarness.Application;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

/// <summary>Binds the CLI review agent to one host-registered, policy-checked operation.</summary>
public sealed class AgentSpaceReviewRuntime
{
    private const string Generate = "review.generate";
    private readonly string directory;
    private readonly WorkspaceIdentity identity;
    private readonly DelegateWorkspacePolicyProvider policies;
    private readonly string provider;

    public AgentSpaceReviewRuntime(AgentWorkspacePolicy policy, string provider, int timeoutSeconds,
        string repository, string outputDirectory)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw Configuration("AgentSpace review workspaces require Windows or Linux.");

        try { identity = new WorkspaceIdentity(policy.Tenant, policy.Agent); }
        catch (ArgumentException) { throw Configuration("AgentSpace tenant and agent must be portable identifiers."); }

        if (policy.AllowedProviders is null || policy.AllowedProviders.Count is < 1 or > 2 ||
            policy.AllowedProviders.Any(name => name is not ("openai" or "anthropic")) ||
            policy.AllowedProviders.Distinct(StringComparer.Ordinal).Count() != policy.AllowedProviders.Count ||
            policy.MaxConcurrentRuns is < 1 or > 64)
            throw Configuration("AgentSpace provider grants or concurrency limit are invalid.");
        if (!policy.AllowedProviders.Contains(provider, StringComparer.Ordinal))
            throw Configuration("The configured review provider is denied by AgentSpace policy.");

        directory = ResolveDirectory(policy.Directory, repository, outputDirectory);
        this.provider = provider;
        var tenantPolicy = new WorkspacePolicy { AllowScripts = true, Commands = [new(Generate)] };
        var agentPolicy = new WorkspacePolicy
        {
            AllowScripts = true,
            Commands = [.. policy.AllowedProviders.Select(name => new CommandRule(Generate,
                [name, policy.AllowReferences ? "*" : "empty", policy.AllowRecalledMemory ? "*" : "empty"]))],
            Limits = new WorkspaceLimits
            {
                MaxConcurrentRuns = policy.MaxConcurrentRuns,
                MaxRunTime = TimeSpan.FromSeconds(timeoutSeconds),
                MaxScriptSteps = 2
            }
        };
        var grants = new WorkspacePolicies(tenantPolicy with { Limits = agentPolicy.Limits }, agentPolicy);
        var denied = new WorkspacePolicies(new WorkspacePolicy());
        policies = new DelegateWorkspacePolicyProvider(requested => requested == identity ? grants : denied);
    }

    public IReviewProvider Bind(IReviewProvider inner) => new ReviewProvider(this, inner);

    private static string ResolveDirectory(string? configured, string repository, string outputDirectory)
    {
        if (configured is null)
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
            configured = Path.Combine(localData, "BusinessHarness", "agent-workspaces");
        }
        if (!Path.IsPathFullyQualified(configured) || (OperatingSystem.IsWindows() && configured.StartsWith(@"\\", StringComparison.Ordinal)))
            throw Configuration("AgentSpace requires an absolute path on a local filesystem.");

        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        SafePaths.RejectLinks(repository);
        repository = FindCheckoutRoot(repository);
        if (Contains(path, repository) || Contains(repository, path) ||
            Contains(path, outputDirectory) || Contains(outputDirectory, path))
            throw Configuration("AgentSpace storage overlaps the reviewed checkout or report directory.");

        SafePaths.RejectLinks(path);
        if (File.Exists(path))
            throw new HarnessException(ErrorCode.FileSystem, "AgentSpace storage points to a file.",
                "Use a dedicated writable directory for agentWorkspace.directory.");
        return path;
    }

    private static string FindCheckoutRoot(string repository)
    {
        // Git accepts a working directory below the checkout root.
        for (var current = new DirectoryInfo(repository); current is not null; current = current.Parent)
        {
            var marker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker)) return current.FullName;
        }
        return repository;
    }

    private static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static HarnessException Configuration(string message) => new(ErrorCode.Configuration, message,
        "Check agentWorkspace in trusted policy. Use only the required provider and context grants.");

    private sealed class ReviewProvider(AgentSpaceReviewRuntime runtime, IReviewProvider inner) : IReviewProvider
    {
        public async Task<ReviewResult> ReviewAsync(ChangeSet changes, IReadOnlyList<ReferenceDocument> issues,
            CancellationToken cancellationToken)
        {
            string[] arguments = [runtime.provider, issues.Count == 0 ? "empty" : "present",
                changes.Memories.Count == 0 ? "empty" : "present"];
            ReviewResult? result = null;
            var commands = new Dictionary<string, AgentCommand>(StringComparer.Ordinal)
            {
                // Evidence and credentials stay in the existing provider path and out of workspace storage.
                [Generate] = new(args => args.SequenceEqual(arguments, StringComparer.Ordinal), async (_, _, ct) =>
                {
                    result = await inner.ReviewAsync(changes, issues, ct);
                    return "";
                })
            };
            var host = new AgentSpaceHost(runtime.directory, runtime.policies, commands);
            try
            {
                var workspace = host.Open(runtime.identity);
                // Check the journal before dispatch so known storage failures cannot incur a provider charge.
                await workspace.Files.ListAsync(limit: 1, cancellationToken: cancellationToken);
                await workspace.RunCommandAsync(Generate, arguments, cancellationToken: cancellationToken);
            }
            catch (WorkspaceAccessException)
            {
                throw Configuration("The review's requested context is denied by AgentSpace policy.");
            }
            catch (WorkspaceLimitException)
            {
                throw new HarnessException(ErrorCode.LimitExceeded, "AgentSpace review execution exceeded a workspace limit.",
                    "Check the concurrent run limit and review timeout, then retry.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException or TimeoutException)
            {
                throw new HarnessException(ErrorCode.FileSystem, "Could not access AgentSpace workspace storage.",
                    "Use a writable local directory owned by the trusted runner.");
            }
            return result ?? throw ReviewContract.InvalidResponse();
        }
    }
}
