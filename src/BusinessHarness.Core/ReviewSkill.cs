using System.Text;

namespace BusinessHarness.Core;

/// <summary>Supplemental review guidance supplied only by trusted pipeline policy.</summary>
public sealed record ReviewSkill(string Name, string Description, string Instructions)
{
    public const int MaxCount = 20;
    public const int MaxBankBytes = 32_000;

    public static bool IsName(string? name) => name is { Length: >= 1 and <= 64 } && name != "none" &&
        name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    public static void Validate(IReadOnlyList<ReviewSkill>? bank, IReadOnlyList<string>? enabled)
    {
        if (bank is null || enabled is null || bank.Count > MaxCount || enabled.Count > MaxCount ||
            bank.Any(s => s is null || !IsName(s.Name) || !IsText(s.Description, 500) || !IsText(s.Instructions, 8000)) ||
            bank.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != bank.Count ||
            enabled.Distinct(StringComparer.Ordinal).Count() != enabled.Count ||
            enabled.Any(name => !IsName(name) || !bank.Any(s => s.Name == name)) ||
            bank.Sum(s => Encoding.UTF8.GetByteCount(s.Name + s.Description + s.Instructions)) > MaxBankBytes)
            throw new HarnessException(ErrorCode.Configuration, "Invalid review skill bank or selection.",
                "Use at most 20 uniquely named trusted skills, at most 32,000 UTF-8 bytes in total, and enable only configured names. See docs/configuration.md.");
    }

    private static bool IsText(string? value, int limit) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= limit && !value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));
}
