using System.Text.RegularExpressions;

namespace BusinessHarness.Infrastructure;

public sealed partial class SecretRedactor(IEnumerable<string>? secrets = null)
{
    private readonly Regex? knownSecretPattern = CreateKnownSecretPattern(secrets ?? []);

    [GeneratedRegex(@"(?i)(?:sk-(?:proj-|ant-)?[a-z0-9_-]{16,}|gh[pousr]_[a-z0-9]{20,}|github_pat_[a-z0-9_]{20,}|AKIA[0-9A-Z]{16}|-----BEGIN (?:RSA |DSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"-----BEGIN (?:RSA |DSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----[\s\S]*?(?:-----END (?:RSA |DSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----|\z)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex("(?im)([\"']?(?:api[_-]?key|password|passwd|secret|access[_-]?token|client[_-]?secret)[\"']?\\s*[:=]\\s*[\"']?)([^\\s\"'`,;}{]{8,})", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AssignmentPattern();

    public string Redact(string text)
    {
        text = PrivateKeyPattern().Replace(text, match => string.Join('\n', match.Value.Split('\n').Select(_ => "[REDACTED PRIVATE KEY]")));
        text = TokenPattern().Replace(text, "[REDACTED]");
        text = AssignmentPattern().Replace(text, "$1[REDACTED]");
        // Mask structural secrets first so a short literal cannot break a PEM header.
        // One literal-matching pass avoids reprocessing replacement text as another secret.
        return knownSecretPattern?.Replace(text, "[REDACTED]") ?? text;
    }

    public bool ContainsSecret(string text) => Redact(text) != text;

    private static Regex? CreateKnownSecretPattern(IEnumerable<string> secrets)
    {
        var literals = secrets.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length).Select(Regex.Escape).ToArray();
        return literals.Length == 0 ? null : new Regex(string.Join('|', literals), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
