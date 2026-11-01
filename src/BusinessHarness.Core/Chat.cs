namespace BusinessHarness.Core;

public sealed record ChatMessage(string Role, string Content);
public sealed record ChatAttachment(string Name, string Content);
public sealed record ChatRequest(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ChatAttachment> Attachments);
