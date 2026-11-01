using BusinessHarness.Core;

namespace BusinessHarness.Application;

public interface IChatProvider
{
    Task<string> ReplyAsync(ChatRequest request, CancellationToken cancellationToken);
}

/// <summary>A bounded conversation. Only completed turns enter history.</summary>
public sealed class ChatSession(IChatProvider provider)
{
    public const int MaxMessages = 40;
    public const int MaxMessageCharacters = 32_000;
    public const int MaxContextCharacters = 400_000;
    private readonly List<ChatMessage> messages = [];
    private readonly SemaphoreSlim gate = new(1, 1);

    public IReadOnlyList<ChatMessage> Messages => messages.AsReadOnly();

    public static void Validate(ChatRequest request)
    {
        if (request.Messages.Count is < 1 or > MaxMessages || request.Messages.Count % 2 != 1 ||
            request.Messages.Where((m, i) => m.Role != (i % 2 == 0 ? "user" : "assistant")).Any() ||
            request.Messages.Any(m => string.IsNullOrWhiteSpace(m.Content) || m.Content.Length > MaxMessageCharacters) ||
            request.Attachments.Count > 20 ||
            request.Attachments.Any(a => string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > 300 || string.IsNullOrWhiteSpace(a.Content)) ||
            request.Messages.Sum(m => (long)m.Content.Length) + request.Attachments.Sum(a => (long)a.Name.Length + a.Content.Length) > MaxContextCharacters)
            throw new HarnessException(ErrorCode.LimitExceeded, "Chat context is invalid or exceeds its limits.",
                "Start a new chat or remove attachments. Use at most 40 messages, 32,000 characters per message, 20 attachments and 400,000 characters total.");
    }

    public async Task<string> SendAsync(string text, IReadOnlyList<ChatAttachment> attachments, CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken))
            throw new HarnessException(ErrorCode.Configuration, "A reply is already in progress.", "Wait for the reply or cancel it first.");
        try
        {
            var next = new ChatMessage("user", text);
            var request = new ChatRequest([.. messages, next], attachments.ToArray());
            Validate(request);
            var reply = await provider.ReplyAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(reply) || reply.Length > MaxMessageCharacters)
                throw new HarnessException(ErrorCode.ProviderResponse, "Chat returned an empty or oversized reply.", "Reduce the scope or choose a compatible text model.");
            messages.Add(next);
            messages.Add(new("assistant", reply));
            return reply;
        }
        finally { gate.Release(); }
    }
}
