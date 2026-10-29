using System.Text;
using BusinessHarness.Application;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

/// <summary>Claude Messages API adapter using native structured outputs and no tools.</summary>
public sealed class AnthropicReviewProvider(SafeHttp http, HarnessPolicy policy, string apiKey, SecretRedactor? redactor = null) : IReviewProvider
{
    public async Task<ReviewResult> ReviewAsync(ChangeSet changes, IReadOnlyList<ReferenceDocument> issues, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = SafeHttp.JsonContent(new
        {
            model = policy.Model,
            max_tokens = policy.MaxOutputTokens,
            system = ReviewContract.BuildInstructions(policy, redactor ?? new([apiKey])),
            messages = new[] { new { role = "user", content = ReviewContract.BuildInput(changes, issues) } },
            output_config = new { format = new { type = "json_schema", schema = ReviewContract.Schema } }
        });
        using var body = await http.SendAsync(request, "Anthropic", cancellationToken);
        var root = body.RootElement;
        if (ApiJson.String(root, "stop_reason") != "end_turn") throw ReviewContract.InvalidResponse();
        var content = ApiJson.Array(root, "content");
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (ApiJson.String(block, "type") != "text" || ApiJson.String(block, "text") is not { } value)
                throw ReviewContract.InvalidResponse();
            text.Append(value);
        }
        return ReviewContract.Parse(text.ToString());
    }
}
