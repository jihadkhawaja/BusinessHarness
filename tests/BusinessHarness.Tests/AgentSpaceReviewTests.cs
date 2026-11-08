using System.Net;
using System.Text;
using System.Text.Json;
using BusinessHarness.Cli;
using BusinessHarness.Core;
using BusinessHarness.Infrastructure;
using Xunit;

namespace BusinessHarness.Tests;

public sealed class AgentSpaceReviewTests
{
    private static readonly ChangeSet Changes = new("base", "head", [], [], []);
    private static readonly ReferenceDocument Reference = new("PROJ-1", "Spec", "Reference evidence");

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DeniedContextNeverReachesProvider(bool hasReference, bool hasMemory)
    {
        using var temp = new TempDirectory();
        using var client = new HttpClient(new StubHttpHandler(_ => throw new InvalidOperationException("Unexpected provider request.")));
        var provider = CreateRuntime(temp.Path, new()).Bind(new OpenAiReviewProvider(new SafeHttp(client), new(), "test-token"));
        var changes = hasMemory ? Changes with { Memories = [new("id", "History", "test", "test", "review", DateTimeOffset.UnixEpoch)] } : Changes;

        var failure = await Assert.ThrowsAsync<HarnessException>(() => provider.ReviewAsync(changes,
            hasReference ? [Reference] : [], TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCode.Configuration, failure.Code);
        Assert.Contains("denied by AgentSpace", failure.Message);
    }

    [Fact]
    public async Task GrantedReviewUsesExistingProviderAndKeepsEvidenceOutOfStorage()
    {
        using var temp = new TempDirectory();
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        using var client = new HttpClient(new StubHttpHandler(async request =>
        {
            calls++;
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.False(body.RootElement.TryGetProperty("tools", out _));
            Assert.Contains("Reference evidence", body.RootElement.GetProperty("input").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"{\"summary\":\"Reviewed\",\"findings\":[],\"requirements\":[]}"}]}]}
                    """, Encoding.UTF8, "application/json")
            };
        }));
        var provider = CreateRuntime(temp.Path, new() { AllowReferences = true }).Bind(
            new OpenAiReviewProvider(new SafeHttp(client), new() { Model = "test-model" }, "test-token"));

        var result = await provider.ReviewAsync(Changes, [Reference], ct);

        Assert.Equal("Reviewed", result.Summary);
        Assert.Equal(1, calls);
        Assert.NotEmpty(Directory.EnumerateDirectories(Path.Combine(temp.Path, "agents")));
        foreach (var path in Directory.EnumerateFiles(Path.Combine(temp.Path, "agents"), "*", SearchOption.AllDirectories))
        {
            var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, ct));
            Assert.DoesNotContain("Reference evidence", text);
            Assert.DoesNotContain("test-token", text);
        }
    }

    [Fact]
    public async Task ValidateChecksEffectiveProviderAndDoesNotCreateWorkspace()
    {
        using var temp = new TempDirectory();
        var ct = TestContext.Current.CancellationToken;
        var config = Path.Combine(temp.Path, "policy.json");
        var storage = Path.Combine(temp.Path, "agents");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            model = "test-model",
            agentWorkspace = new { tenant = "test", agent = "review", allowedProviders = new[] { "openai" }, directory = storage }
        }), ct);
        var args = new[] { "validate", "--repo", Path.Combine(temp.Path, "checkout"),
            "--config", config, "--output", Path.Combine(temp.Path, "reports") };
        var stderr = new StringWriter();
        var allowed = new CliApplication(name => name == "OPENAI_API_KEY" ? "test-token" : null);
        var denied = new CliApplication(name => name == "HARNESS_PROVIDER" ? "anthropic" : null);

        Assert.Equal(0, await allowed.RunAsync(args, new StringWriter(), stderr, ct));
        Assert.Equal(2, await denied.RunAsync(args, new StringWriter(), stderr, ct));
        Assert.Contains("provider is denied by AgentSpace", stderr.ToString());
        Assert.False(Directory.Exists(storage));
    }

    [Fact]
    public async Task CorruptWorkspaceStopsBeforeDispatch()
    {
        using var temp = new TempDirectory();
        var ct = TestContext.Current.CancellationToken;
        var runtime = CreateRuntime(temp.Path, new());
        using var client = new HttpClient(new StubHttpHandler(_ => throw new InvalidOperationException("Unexpected provider request.")));
        var provider = runtime.Bind(new OpenAiReviewProvider(new SafeHttp(client), new(), "test-token"));
        // A rejected command initializes the fixture without contacting the provider.
        await Assert.ThrowsAsync<HarnessException>(() => provider.ReviewAsync(Changes, [Reference], ct));
        var journal = Assert.Single(Directory.EnumerateFiles(Path.Combine(temp.Path, "agents"), "journal.bin", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(journal, "damaged journal", ct);

        var failure = await Assert.ThrowsAsync<HarnessException>(() => provider.ReviewAsync(Changes, [], ct));

        Assert.Equal(ErrorCode.FileSystem, failure.Code);
        Assert.Equal("damaged journal", await File.ReadAllTextAsync(journal, ct));
    }

    [Fact]
    public void RejectsStorageInCheckoutWhenReviewStartsFromSubdirectory()
    {
        using var repo = new TestRepository();
        using var reports = new TempDirectory();
        var policy = new AgentWorkspacePolicy
        {
            Tenant = "test",
            Agent = "review",
            Directory = Path.Combine(repo.Path, "agents"),
            AllowedProviders = ["openai"]
        };
        var nested = Directory.CreateDirectory(Path.Combine(repo.Path, "nested")).FullName;

        var failure = Assert.Throws<HarnessException>(() => new AgentSpaceReviewRuntime(policy, "openai", 30, nested, reports.Path));

        Assert.Equal(ErrorCode.Configuration, failure.Code);
        Assert.Contains("overlaps", failure.Message);
        Assert.False(Directory.Exists(policy.Directory));
    }

    private static AgentSpaceReviewRuntime CreateRuntime(string root, AgentWorkspacePolicy policy) =>
        new(policy with { Tenant = "test", Agent = "review", Directory = Path.Combine(root, "agents"), AllowedProviders = ["openai"] },
            "openai", 30, Path.Combine(root, "checkout"), Path.Combine(root, "reports"));
}
