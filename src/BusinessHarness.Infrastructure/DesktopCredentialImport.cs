using System.Text.Json;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

public static class DesktopCredentialImport
{
    public static async Task<(string Provider, string Model, string Credential)> ReadAsync(string path, CancellationToken ct)
    {
        SafePaths.RejectLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 64_000) throw Invalid();
        using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 8 }, ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() is < 1 or > 2 ||
            root.EnumerateObject().Any(p => p.Name is not ("OpenAI" or "Anthropic" or "Atlassian"))) throw Invalid();
        var providers = root.EnumerateObject().Where(p => p.Name is "OpenAI" or "Anthropic").ToArray();
        if (providers.Length != 1 || root.EnumerateObject().Count(p => p.Name == "Atlassian") > 1) throw Invalid();
        var entry = providers[0];
        var provider = entry.Name switch { "OpenAI" => "openai", "Anthropic" => "anthropic", _ => throw Invalid() };
        if (entry.Value.ValueKind != JsonValueKind.Object || entry.Value.EnumerateObject().Count() != 3 ||
            entry.Value.EnumerateObject().Any(p => p.Name is not ("Endpoint" or "ApiKey" or "ChatModel"))) throw Invalid();
        var endpoint = ApiJson.String(entry.Value, "Endpoint");
        var key = ApiJson.String(entry.Value, "ApiKey");
        var model = ApiJson.String(entry.Value, "ChatModel");
        if (endpoint != (provider == "openai" ? "https://api.openai.com/v1/" : "https://api.anthropic.com/") ||
            key is not { Length: > 0 and <= 16_000 } || key.Any(char.IsControl) || model is not { Length: > 0 and <= 120 } || model.Any(char.IsControl)) throw Invalid();
        return (provider, model, key);
    }

    private static HarnessException Invalid() => new(ErrorCode.Configuration, "This credentials file is not supported.",
        "Use one OpenAI or Anthropic section with Endpoint, ApiKey and ChatModel. Only official endpoints are accepted.");
}
