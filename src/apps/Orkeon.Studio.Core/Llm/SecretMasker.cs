namespace Orkeon.Studio.Core.Llm;

/// <summary>
/// Takes the keys Studio knows out of a text about to be shown as evidence or copied: an engine's
/// output, an error body or a test journal may quote the key it was given.
/// </summary>
public static class SecretMasker
{
    /// <summary>What stands where a key was.</summary>
    public const string Mask = "***";

    /// <summary>A value shorter than this is no key: masking it would eat ordinary words.</summary>
    private const int ShortestSecret = 6;

    /// <summary>Replaces every occurrence of each of <paramref name="secrets"/> in <paramref name="text"/>.</summary>
    /// <param name="text">The text to clean.</param>
    /// <param name="secrets">The keys; blank and very short ones are skipped.</param>
    /// <returns>The text without any of the keys.</returns>
    public static string Apply(string text, IEnumerable<string?> secrets)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(secrets);

        // Longest first: a key that contains another leaves no tail behind.
        foreach (var secret in secrets.OfType<string>().Select(s => s.Trim())
                     .Where(s => s.Length >= ShortestSecret)
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return text;
    }
}
