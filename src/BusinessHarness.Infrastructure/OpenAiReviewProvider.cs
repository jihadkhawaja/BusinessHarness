using System.Net.Http.Headers;
using System.Text;
using BusinessHarness.Application;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

/// <summary>Responses API adapter. Uses no tools and disables response storage.</summary>
public sealed class OpenAiReviewProvider(SafeHttp http, HarnessPolicy policy, string apiKey, SecretRedactor? redactor = null) : IReviewProvider
{
    public async Task<ReviewResult> ReviewAsync(ChangeSet changes, IReadOnlyList<ReferenceDocument> issues, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = SafeHttp.JsonContent(new
        {
            model = policy.Model,
            store = false,
            instructions = ReviewContract.BuildInstructions(policy, redactor ?? new([apiKey])),
            input = ReviewContract.BuildInput(changes, issues),
            max_output_tokens = policy.MaxOutputTokens,
            text = new { format = new { type = "json_schema", name = "repository_review", strict = true, schema = ReviewContract.Schema } }
        });
        using var body = await http.SendAsync(request, "OpenAI", cancellationToken);
        var root = body.RootElement;
        if (ApiJson.String(root, "status") != "completed") throw ReviewContract.InvalidResponse();
        var output = ApiJson.Array(root, "output");
        var text = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            var type = ApiJson.String(item, "type");
            if (type == "reasoning") continue;
            if (type != "message") throw ReviewContract.InvalidResponse();
            var content = ApiJson.Array(item, "content");
            foreach (var block in content.EnumerateArray())
            {
                if (ApiJson.String(block, "type") != "output_text" || ApiJson.String(block, "text") is not { } value)
                    throw ReviewContract.InvalidResponse();
                text.Append(value);
            }
        }
        return ReviewContract.Parse(text.ToString());
    }
}
