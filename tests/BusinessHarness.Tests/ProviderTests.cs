using System.Net;
using System.Text.Json;
using BusinessHarness.Application;
using BusinessHarness.Core;
using BusinessHarness.Infrastructure;
using Xunit;

namespace BusinessHarness.Tests;

public sealed class ProviderTests
{
    private static readonly ChangeSet Changes = new("base", "head", [new("sample.cs", "+// example", [1])], [], []);
    private const string Result = """{"summary":"No defects found.","findings":[],"requirements":[]}""";

    [Theory]
    [InlineData("openai", "DSA", false)]
    [InlineData("openai", "DSA", true)]
    [InlineData("openai", "ENCRYPTED", false)]
    [InlineData("openai", "ENCRYPTED", true)]
    [InlineData("anthropic", "DSA", false)]
    [InlineData("anthropic", "DSA", true)]
    [InlineData("anthropic", "ENCRYPTED", false)]
    [InlineData("anthropic", "ENCRYPTED", true)]
    public async Task PrivateKeyInOrdinaryFileIsFlaggedAndWithheldFromProvider(string providerName, string label, bool truncated)
    {
        using var repo = new TestRepository();
        // Deliberately synthetic text; no real key material.
        var key = $"-----BEGIN {label} PRIVATE KEY-----\nSYNTHETIC-PRIVATE-BODY\n" +
            (truncated ? "" : $"-----END {label} PRIVATE KEY-----\n");
        repo.Commit("notes.md", key);
        var changes = await repo.Reader().ReadChangesAsync("HEAD^", "HEAD", TestContext.Current.CancellationToken);
        var finding = Assert.Single(changes.SecurityFindings);
        Assert.Equal("BH001", finding.RuleId);
        Assert.Equal("critical", finding.Severity);
        Assert.Equal(1, finding.Line);
        Assert.DoesNotContain("SYNTHETIC-PRIVATE-BODY", Assert.Single(changes.Files).Patch);
        using var handler = new StubHttpHandler(async request =>
        {
            var payload = await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("SYNTHETIC-PRIVATE-BODY", payload);
            Assert.Contains("REDACTED PRIVATE KEY", payload);
            return StubHttpHandler.Json(providerName == "openai"
                ? JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = Result } } } } })
                : JsonSerializer.Serialize(new { stop_reason = "end_turn", content = new[] { new { type = "text", text = Result } } }));
        });
        using var client = new HttpClient(handler);
        var policy = new HarnessPolicy { Provider = providerName, Model = "test-model" };
        IReviewProvider provider = providerName == "openai"
            ? new OpenAiReviewProvider(new(client), policy, "test-key")
            : new AnthropicReviewProvider(new(client), policy, "test-key");
        await provider.ReviewAsync(changes, [], TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task ProvidersReceiveOnlyCompletePrDiffAndLinkedSpecification(string providerName)
    {
        using var repo = new TestRepository();
        var original = Enumerable.Range(1, 30).Select(i => "// existing line " + i).ToArray();
        repo.Commit("sample.cs", string.Join('\n', original) + "\n");
        repo.Commit("unchanged.cs", "// unrelated repository content\n");
        repo.Commit("removed.cs", "// obsolete behavior\n");
        var baseline = repo.Git("rev-parse", "HEAD");
        repo.Git("checkout", "-b", "feature");
        repo.Commit("added.cs", "// first PR commit\n");
        original[14] = "// modified behavior in second PR commit";
        repo.Commit("sample.cs", string.Join('\n', original) + "\n");
        repo.Git("rm", "--", "removed.cs");
        repo.Git("commit", "-m", "remove obsolete behavior");
        var head = repo.Git("rev-parse", "HEAD");
        repo.Git("checkout", "main");
        repo.Commit("target-only.cs", "// unrelated target branch change\n");
        File.WriteAllText(Path.Combine(repo.Path, "sample.cs"), "// uncommitted working tree content\n");
        File.WriteAllText(Path.Combine(repo.Path, "untracked.cs"), "// untracked content\n");
        var changes = await repo.Reader().ReadChangesAsync("main", head, TestContext.Current.CancellationToken);
        var specification = new ReferenceDocument("PROJ-1", "Feature specification", "Describe the new behavior", "Verify behavior at runtime");
        const string response = """{"summary":"Runtime verification required.","findings":[],"requirements":[{"issueKey":"PROJ-1","status":"not_verifiable","evidence":"The supplied diff cannot establish runtime behavior."}]}""";
        using var handler = new StubHttpHandler(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = payload.RootElement;
            Assert.Equal(ReviewContract.Instructions, root.GetProperty(providerName == "openai" ? "instructions" : "system").GetString());
            var input = providerName == "openai" ? root.GetProperty("input").GetString()
                : root.GetProperty("messages")[0].GetProperty("content").GetString();
            using var context = JsonDocument.Parse(input!);
            Assert.Equal(baseline, context.RootElement.GetProperty("baseCommit").GetString());
            Assert.Equal(head, context.RootElement.GetProperty("headCommit").GetString());
            var files = context.RootElement.GetProperty("files").EnumerateArray().ToArray();
            Assert.Equal(["added.cs", "removed.cs", "sample.cs"], files.Select(f => f.GetProperty("path").GetString()).ToArray());
            Assert.Contains("first PR commit", files[0].GetProperty("patch").GetString());
            Assert.Contains("-// obsolete behavior", files[1].GetProperty("patch").GetString());
            Assert.Empty(files[1].GetProperty("addedLines").EnumerateArray());
            var patch = files[2].GetProperty("patch").GetString()!;
            Assert.Contains("+// modified behavior in second PR commit", patch);
            Assert.Contains(" // existing line 14", patch);
            Assert.DoesNotContain("// existing line 1\n", patch);
            Assert.Equal(15, Assert.Single(files[2].GetProperty("addedLines").EnumerateArray()).GetInt32());
            Assert.DoesNotContain("uncommitted", input);
            Assert.DoesNotContain("untracked", input);
            Assert.DoesNotContain("unrelated", input);
            var reference = Assert.Single(context.RootElement.GetProperty("references").EnumerateArray());
            Assert.Equal(specification.Key, reference.GetProperty("key").GetString());
            Assert.Equal(specification.AcceptanceCriteria, reference.GetProperty("acceptanceCriteria").GetString());
            return StubHttpHandler.Json(providerName == "openai"
                ? JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = response } } } } })
                : JsonSerializer.Serialize(new { stop_reason = "end_turn", content = new[] { new { type = "text", text = response } } }));
        });
        using var client = new HttpClient(handler);
        var policy = new HarnessPolicy { Provider = providerName, Model = "test-model" };
        IReviewProvider provider = providerName == "openai"
            ? new OpenAiReviewProvider(new(client), policy, "test-key")
            : new AnthropicReviewProvider(new(client), policy, "test-key");
        var result = await provider.ReviewAsync(changes, [specification], TestContext.Current.CancellationToken);
        Assert.Equal("not_verifiable", Assert.Single(result.Requirements).Status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OpenAiSendsStrictSchemaWithoutToolsAndDoesNotStore()
    {
        using var handler = new StubHttpHandler(async request =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", request.Headers.Authorization!.Parameter);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = payload.RootElement;
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.False(root.TryGetProperty("tools", out _));
            Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
            Assert.Contains("UNTRUSTED DATA", root.GetProperty("instructions").GetString());
            return StubHttpHandler.Json(JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = Result } } } } }));
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiReviewProvider(new(client), new() { Model = "test-model" }, "test-key");
        Assert.Empty((await provider.ReviewAsync(Changes, [], TestContext.Current.CancellationToken)).Findings);
    }

    [Fact]
    public async Task ClaudeUsesNativeOutputConfig()
    {
        using var handler = new StubHttpHandler(async request =>
        {
            Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.AbsoluteUri);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("json_schema", payload.RootElement.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
            Assert.False(payload.RootElement.TryGetProperty("tools", out _));
            return StubHttpHandler.Json(JsonSerializer.Serialize(new { stop_reason = "end_turn", content = new[] { new { type = "text", text = Result } } }));
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicReviewProvider(new(client), new() { Provider = "anthropic", Model = "test-model" }, "test-key");
        Assert.Empty((await provider.ReviewAsync(Changes, [], TestContext.Current.CancellationToken)).Findings);
    }

    [Theory]
    [InlineData(401, ErrorCode.Authentication)]
    [InlineData(403, ErrorCode.Authentication)]
    [InlineData(429, ErrorCode.Network)]
    [InlineData(500, ErrorCode.Network)]
    [InlineData(302, ErrorCode.Network)]
    public async Task ClassifiesHttpFailuresAndNeverLogsRawBodies(int status, ErrorCode expected)
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json("PRIVATE-SECRET-RESPONSE", (HttpStatusCode)status)));
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid");
        var error = await Assert.ThrowsAsync<HarnessException>(() => new SafeHttp(client).SendAsync(request, "Test", TestContext.Current.CancellationToken));
        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("PRIVATE-SECRET", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("""{"status":"incomplete","output":[]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"no"}]}]}""")]
    [InlineData("""{"status":"completed","output":[]}""")]
    [InlineData("[]")]
    [InlineData("""{"status":42,"output":[]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":42}]}]}""")]
    public async Task RefusedAndIncompleteResponsesCannotPass(string response)
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json(response)));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HarnessException>(() => new OpenAiReviewProvider(new(client), new(), "key").ReviewAsync(Changes, [], TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCode.ProviderResponse, error.Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"summary":"ok","findings":[],"requirements":[],"unexpected":true}""")]
    [InlineData("""{"summary":null,"findings":[],"requirements":[]}""")]
    public void RejectsInvalidReviewShape(string json) => Assert.Throws<HarnessException>(() => ReviewContract.Parse(json));

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("refusal")]
    [InlineData("tool_use")]
    public async Task ClaudeRejectsNonCompletedGeneration(string reason)
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json(JsonSerializer.Serialize(new
        {
            stop_reason = reason,
            content = new[] { new { type = "text", text = Result } }
        }))));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HarnessException>(() => new AnthropicReviewProvider(new(client), new(), "key")
            .ReviewAsync(Changes, [], TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCode.ProviderResponse, error.Code);
    }

    [Fact]
    public async Task HttpResponseBodyHasAnEnforcedSizeLimit()
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json(new string('x', 1_000_001))));
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid");
        var error = await Assert.ThrowsAsync<HarnessException>(() => new SafeHttp(client).SendAsync(request, "Test", TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCode.LimitExceeded, error.Code);
    }

    [Fact]
    public async Task JiraFlattensAdfAndIncludesAcceptanceCriteria()
    {
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Contains("customfield_10001", request.RequestUri!.Query);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            return Task.FromResult(StubHttpHandler.Json("""
                {"key":"PROJ-123","fields":{"summary":"Example","description":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Requirement text"}]}]},"customfield_10001":"Must pass validation"}}
                """));
        });
        using var client = new HttpClient(handler);
        var source = new JiraIssueSource(new(client), new() { JiraSite = "https://team.atlassian.net", JiraAcceptanceCriteriaField = "customfield_10001" }, "test@example.invalid", "token", new());
        var issue = await source.GetIssueAsync("PROJ-123", TestContext.Current.CancellationToken);
        Assert.Equal("Requirement text", issue.Description);
        Assert.Equal("Must pass validation", issue.AcceptanceCriteria);
    }
}
