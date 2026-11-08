using System.Net;
using System.Text;
using System.Text.Json;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

public sealed class SafeHttp(HttpClient client)
{
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        MaxResponseHeadersLength = 32
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<JsonDocument> SendAsync(HttpRequestMessage request, string service, CancellationToken ct)
    {
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new HarnessException(ErrorCode.Authentication, $"{service} rejected authentication or access (HTTP {(int)response.StatusCode}).",
                    "Check the secret variable, token scope, project/model access, and whether this pipeline is allowed to receive secrets.");
            if (!response.IsSuccessStatusCode)
                throw new HarnessException(ErrorCode.Network, $"{service} returned HTTP {(int)response.StatusCode}.",
                    response.StatusCode == HttpStatusCode.TooManyRequests
                        ? "Rate limit reached. Check provider quota and Retry-After guidance, then rerun. Requests are not retried automatically."
                        : "Check service availability, model/issue identifier and network policy. Redirects are refused; response bodies are withheld to protect secrets.");
            const int maxBytes = 1_000_000;
            if (response.Content.Headers.ContentLength > maxBytes) throw TooLarge();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + read > maxBytes) throw TooLarge();
                body.Write(buffer, 0, read);
            }
            return JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new HarnessException(ErrorCode.Network, $"Could not reach {service} over HTTPS.",
                "Check DNS, outbound firewall, proxy and TLS certificate configuration. TLS validation is never disabled.");
        }
        catch (JsonException)
        {
            throw new HarnessException(ErrorCode.ProviderResponse, $"{service} returned malformed JSON.", "Check API compatibility and service status; raw responses are not logged.");
        }
    }

    public static StringContent JsonContent(object payload) => new(JsonSerializer.Serialize(payload, JsonDefaults.Options), Encoding.UTF8, "application/json");
    private static HarnessException TooLarge() => new(ErrorCode.LimitExceeded, "Service response exceeded 1 MB.", "Reduce the change or issue scope. No partial result is accepted.");
}
