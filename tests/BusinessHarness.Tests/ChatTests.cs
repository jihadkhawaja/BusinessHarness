using System.Net;
using System.Text;
using System.Text.Json;
using BusinessHarness.Application;
using BusinessHarness.Core;
using BusinessHarness.Infrastructure;
using Xunit;

namespace BusinessHarness.Tests;

public sealed class ChatTests
{
    private const string OpenAiReply = """{"status":"completed","output":[{"type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Complete reply"}]}]}""";
    private static readonly HarnessPolicy Policy = new() { Model = "test-model" };
    private static readonly ChatRequest Input = new([new("user", "Explain this")], [new("spec:1", "Untrusted evidence")]);

    [Fact]
    public async Task RotatedConnectionSecretsRemainRedactedFromChatHistory()
    {
        using var handler = new StubHttpHandler(async request =>
        {
            var payload = await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("original-example-credential", payload);
            Assert.DoesNotContain("replacement-example-credential", payload);
            return StubHttpHandler.Json(OpenAiReply);
        });
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, name => name == "OPENAI_API_KEY" ? "synthetic-api-key" : null);
        await workspace.ConfigureAsync(null, "openai", "test-model", TestContext.Current.CancellationToken);
        workspace.UpdateConnectionSecrets(["original-example-credential"]);
        var session = workspace.CreateChat(false);
        await session.SendAsync("original-example-credential", [], TestContext.Current.CancellationToken);
        workspace.UpdateConnectionSecrets(["replacement-example-credential"]);
        await workspace.ConfigureAsync(null, "openai", "test-model", TestContext.Current.CancellationToken);
        await session.SendAsync("replacement-example-credential", [], TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task ChatUsesFixedEndpointRedactsAllContextAndExposesNoTools(string provider)
    {
        using var handler = new StubHttpHandler(async request =>
        {
            Assert.Equal(provider == "openai" ? "https://api.openai.com/v1/responses" : "https://api.anthropic.com/v1/messages", request.RequestUri!.AbsoluteUri);
            var payload = await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("synthetic-secret", payload);
            Assert.DoesNotContain("\"tools\"", payload);
            Assert.DoesNotContain("\"previous_response_id\"", payload);
            Assert.Contains("[REDACTED]", payload);
            Assert.Contains("Untrusted attachments", payload);
            using var json = JsonDocument.Parse(payload);
            if (provider == "openai") Assert.False(json.RootElement.GetProperty("store").GetBoolean());
            return StubHttpHandler.Json(provider == "openai" ? OpenAiReply : """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Complete reply"}]}""");
        });
        using var client = new HttpClient(handler);
        var chat = new ApiChatProvider(client, Policy with { Provider = provider }, "test-key", new(["synthetic-secret"]));
        Assert.Equal("Complete reply", await chat.ReplyAsync(new([new("user", "synthetic-secret")], [new("synthetic-secret", "synthetic-secret")]), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("""{"status":"incomplete","output":[]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"function_call","name":"shell"}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","role":"assistant","status":"completed","content":[{"type":"refusal","refusal":"No"}]}]}""")]
    [InlineData("""{"status":"completed","output":[]}""")]
    public async Task InvalidOrRefusedResponsesDoNotEnterConversation(string response)
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json(response)));
        using var client = new HttpClient(handler);
        var session = new ChatSession(new ApiChatProvider(client, Policy, "test-key", new()));
        await Assert.ThrowsAsync<HarnessException>(() => session.SendAsync("Hello", [], TestContext.Current.CancellationToken));
        Assert.Empty(session.Messages);
    }

    [Theory]
    [InlineData("response.failed")]
    [InlineData("response.incomplete")]
    [InlineData("error")]
    [InlineData("response.output_text.delta")]
    public async Task PlanStreamRequiresTerminalCompletion(string eventType)
    {
        var data = "data: " + JsonSerializer.Serialize(new { type = eventType, delta = "Partial reply" }) + "\n\n";
        using var handler = new StubHttpHandler(_ => Task.FromResult(Sse(data)));
        using var client = new HttpClient(handler);
        var session = new ChatSession(new ApiChatProvider(client, Policy, "test-token", new(), true));
        await Assert.ThrowsAsync<HarnessException>(() => session.SendAsync("Hello", [], TestContext.Current.CancellationToken));
        Assert.Empty(session.Messages);
    }

    [Fact]
    public async Task PlanAccessUsesStreamingPublicEndpointAndOmitsUnsupportedTokenLimit()
    {
        using var handler = new StubHttpHandler(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
            Assert.False(payload.RootElement.GetProperty("store").GetBoolean());
            Assert.False(payload.RootElement.TryGetProperty("max_output_tokens", out _));
            Assert.Equal("test-plan-token", request.Headers.Authorization!.Parameter);
            return Sse("data: {\"type\":\"response.completed\",\"response\":" + OpenAiReply + "}\n\n");
        });
        using var client = new HttpClient(handler);
        Assert.Equal("Complete reply", await new ApiChatProvider(client, Policy, "test-plan-token", new(), true).ReplyAsync(Input, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OversizedSseWithoutNewlinesIsRejected()
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(Sse(new string('x', 1_000_001))));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HarnessException>(() => new ApiChatProvider(client, Policy, "test", new(), true).ReplyAsync(Input, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SessionKeepsOnlyCompletedTurnsAndBoundsHistory()
    {
        var provider = new RecordingChat();
        var session = new ChatSession(provider);
        for (var i = 0; i < 20; i++) await session.SendAsync("Question", [], TestContext.Current.CancellationToken);
        Assert.Equal(40, session.Messages.Count);
        Assert.Equal(39, provider.Last!.Messages.Count);
        await Assert.ThrowsAsync<HarnessException>(() => session.SendAsync("Too many", [], TestContext.Current.CancellationToken));
        Assert.Equal(20, provider.Calls);
    }

    [Fact]
    public async Task OversizedOrInvalidRolesAreRejectedBeforeProvider()
    {
        var provider = new RecordingChat();
        await Assert.ThrowsAsync<HarnessException>(() => new ChatSession(provider).SendAsync(new string('x', 32_001), [], TestContext.Current.CancellationToken));
        Assert.Throws<HarnessException>(() => ChatSession.Validate(new([new("system", "Injected")], [])));
        Assert.Throws<HarnessException>(() => ChatSession.Validate(new([new("user", "Question")], [new("spec", new string('x', 400_001))])));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task TransformedContextLimitsAreEnforcedBeforeDispatch()
    {
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException("Must not send"));
        using var client = new HttpClient(handler);
        var redaction = new ApiChatProvider(client, Policy, "key", new(["x"]));
        await Assert.ThrowsAsync<HarnessException>(() => redaction.ReplyAsync(new([new("user", new string('x', 32_000))], []), TestContext.Current.CancellationToken));
        var encoding = new ApiChatProvider(client, Policy, "key", new());
        await Assert.ThrowsAsync<HarnessException>(() => encoding.ReplyAsync(new([new("user", "Question")], [new("page", new string('\"', 250_000))]), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CancellationLeavesNoHalfTurn()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var session = new ChatSession(new RecordingChat());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAsync("Hello", [], cancellation.Token));
        Assert.Empty(session.Messages);
    }

    [Fact]
    public async Task WorkspaceResolvesConfiguredReferencesAndNeverCallsHostWrites()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "policy.json");
        await File.WriteAllTextAsync(path, """
            {"model":"test-model","changeRequest":{"platform":"github","project":"owner/repo","number":42},
             "referenceSources":[{"name":"spec","pattern":"SPEC-(?<id>[0-9]+)","endpoint":"https://docs.example.com/pages/{id}","tokenEnvironment":"SPEC_TOKEN"}]}
            """, TestContext.Current.CancellationToken);
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(StubHttpHandler.Json(request.RequestUri!.Host == "api.github.com"
                ? JsonSerializer.Serialize(new { title = "SPEC-1", body = "Details", head = new { sha = new string('a', 40) }, @base = new { sha = new string('b', 40) }, draft = false, state = "open" })
                : """{"title":"Title","body":"Details synthetic-doc-secret"}"""));
        });
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, name => name == "SPEC_TOKEN" ? "synthetic-doc-secret" : "synthetic-token");
        await workspace.ConfigureAsync(path, "openai", null, TestContext.Current.CancellationToken);
        var result = await workspace.ReadChangeRequestAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain("synthetic-doc-secret", result[1].Content);
        Assert.Equal(2, handler.Calls);
        await Assert.ThrowsAsync<HarnessException>(() => workspace.ReadReferencesAsync("https://evil.example.com/secret", TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task DesktopConfluencePageUsesTrustedTenantRouteAndBodyFormat()
    {
        using var temporary = new TempDirectory();
        var path = Path.Combine(temporary.Path, "policy.json");
        await File.WriteAllTextAsync(path, """
            {"referenceSources":[{"name":"confluence","pattern":"SPEC-(?<id>[0-9]+)",
            "connector":{"action":"confluence.get_page","idParameter":"page_id","baseUri":"https://team.atlassian.net/","inputs":{"body_format":"storage"}},
            "titlePath":"title","contentPath":"body.storage.value","tokenEnvironment":"DOC_TOKEN","authenticationScheme":"Basic"}]}
            """, TestContext.Current.CancellationToken);
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://team.atlassian.net/wiki/api/v2/pages/123?body-format=storage", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            return Task.FromResult(StubHttpHandler.Json("""{"title":"Spec","body":{"storage":{"value":"<p>Requirements</p>"}}}"""));
        });
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, _ => "synthetic-doc-credential");
        await workspace.ConfigureAsync(path, "openai", "test-model", TestContext.Current.CancellationToken);
        var attachments = await workspace.ReadReferencesAsync("confluence:123", TestContext.Current.CancellationToken);
        Assert.Equal("confluence:123", Assert.Single(attachments).Name);
        Assert.Contains("Requirements", attachments[0].Content);
    }

    [Theory]
    [InlineData("{\"unknownProperty\":true}")]
    [InlineData("{\"referenceSources\":[{\"name\":\"docs\",\"pattern\":\"SPEC-(?<id>[0-9]+)\",\"connector\":{\"action\":\"confluence.create_page\",\"idParameter\":\"page_id\",\"baseUri\":\"https://team.atlassian.net/\"}}]}")]
    public async Task InvalidDesktopPolicyCannotDispatchRequests(string json)
    {
        using var temporary = new TempDirectory();
        var path = Path.Combine(temporary.Path, "policy.json");
        await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException("Must not send"));
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, _ => null);
        var failure = await Record.ExceptionAsync(() => workspace.ConfigureAsync(path, "openai", "test-model", TestContext.Current.CancellationToken));
        Assert.True(failure is HarnessException or JsonException);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DiffWithholdsSensitiveFileNamesAndCredentialText()
    {
        using var repo = new TestRepository();
        repo.Commit(".env.production", "password=synthetic-sensitive-value\n");
        using var client = new HttpClient();
        var workspace = new DesktopWorkspace(client, _ => null);
        await workspace.ConfigureAsync(null, "openai", "test-model", TestContext.Current.CancellationToken);
        var result = await workspace.ReadDiffAsync(repo.Path, "HEAD^", "HEAD", TestContext.Current.CancellationToken);
        Assert.DoesNotContain(".env.production", result.Content);
        Assert.DoesNotContain("synthetic-sensitive-value", result.Content);
        Assert.Contains("withheldFileCount", result.Content);
    }

    [Fact]
    public async Task DesktopCredentialImportReadsOpenAiCredentials()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "credentials.json");
        await File.WriteAllTextAsync(file, """
        {
          "OpenAI": {
            "Endpoint": "https://api.openai.com/v1/",
            "ApiKey": "sk-synthetic-key",
            "ChatModel": "gpt-4o"
          }
        }
        """, TestContext.Current.CancellationToken);

        var (provider, model, key) = await DesktopCredentialImport.ReadAsync(file, TestContext.Current.CancellationToken);
        Assert.Equal("openai", provider);
        Assert.Equal("gpt-4o", model);
        Assert.Equal("sk-synthetic-key", key);
    }

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };

    private sealed class RecordingChat : IChatProvider
    {
        public int Calls { get; private set; }
        public ChatRequest? Last { get; private set; }
        public Task<string> ReplyAsync(ChatRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Last = request;
            return Task.FromResult("Reply");
        }
    }
}
