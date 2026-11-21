using System.Globalization;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// The rules of the tool bridge's two sections, as the engine's host judges them at start
/// (<c>ToolBridgeClientOptions.Problems</c>, <c>ToolBridgeServerOptions.Problems</c> and their helpers in
/// <c>Orkeon.Hosting</c>). Those classes are internal to an assembly Studio does not reference, so the
/// rules are restated here, sentence for sentence, and pinned against the originals by
/// <c>ToolBridgeRulesOracleTests</c>, which runs both over the same settings.
/// </summary>
public static class ToolBridgeRules
{
    /// <summary>The host name a container of Docker Desktop reaches its host's loopback by.</summary>
    public const string DockerHost = "host.docker.internal";

    /// <summary>The entry of <c>Allow</c> that selects every bridgeable Orkeon tool.</summary>
    public const string All = "*";

    /// <summary>The least length of the token, trimmed.</summary>
    public const int MinTokenLength = 16;

    /// <summary>The largest value of a count of the serving side (sessions, calls, runs).</summary>
    public const int MaxCountLimit = 1024;

    /// <summary>The longest timeout and heartbeat window of the serving side.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// What is wrong with an address of the bridge, or null: an absolute http or https address, with no
    /// user or password, that ends with <c>/</c>, over https unless its host is the loopback (or, for a
    /// caller, <c>host.docker.internal</c>).
    /// </summary>
    public static string? AddressProblem(string? value, string key, bool allowDockerHost = false)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"{key} '{value}' is not an absolute http or https address.";
        }

        if (uri.UserInfo.Length > 0)
            return $"{key} names a user and a password before its host: the token travels in its environment variable, never in the address.";
        if (!value.EndsWith('/'))
            return $"{key} '{value}' must end with '/'.";
        if (uri.Scheme != Uri.UriSchemeHttps && !IsLoopback(uri) && !(allowDockerHost && IsDockerHost(uri)))
            return $"{key} '{value}' names a host other than loopback over http: such an address requires https.";
        return null;
    }

    /// <summary>Whether the address names the loopback: an IP of it, or the name <c>localhost</c> as written.</summary>
    public static bool IsLoopback(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            return System.Net.IPAddress.TryParse(uri.IdnHost, out var address) && System.Net.IPAddress.IsLoopback(address);

        // Uri reports the name loopback as localhost: the host is read as written.
        return string.Equals(HostAsWritten(uri), "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static string HostAsWritten(Uri uri)
    {
        var text = uri.OriginalString;
        var start = text.IndexOf("://", StringComparison.Ordinal);
        if (start < 0)
            return uri.IdnHost;
        start += 3;
        var authorityEnd = text.IndexOfAny(['/', '?', '#'], start);
        var at = text.IndexOf('@', start);
        if (at >= 0 && (authorityEnd < 0 || at < authorityEnd))
            start = at + 1;
        var end = text.IndexOfAny([':', '/', '?', '#'], start);
        return end < 0 ? text[start..] : text[start..end];
    }

    /// <summary>Whether the address names <c>host.docker.internal</c>.</summary>
    public static bool IsDockerHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return string.Equals(uri.IdnHost, DockerHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a text is a host with an optional port, as a <c>Host</c> header carries it.</summary>
    public static bool IsAuthority(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '@' or '?' or '#' or '\\'))
            return false;
        return Uri.TryCreate("http://" + value + "/", UriKind.Absolute, out var uri)
            && uri.UserInfo.Length == 0 && uri.PathAndQuery == "/" && uri.Host.Length > 0;
    }

    /// <summary>
    /// What is wrong with the names of a selection: an empty name, or a pattern, only a tool's name or
    /// the lone <c>*</c> being accepted.
    /// </summary>
    public static IEnumerable<string> SelectionProblems(string section, IEnumerable<string> allow, IEnumerable<string> deny)
    {
        foreach (var (key, entries) in new[] { ("Allow", allow), ("Deny", deny) })
        {
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry))
                    yield return $"{section}:{key} holds an empty name.";
                else if (entry != All && entry.Contains('*', StringComparison.Ordinal))
                    yield return $"{section}:{key} '{entry}': only a tool's name or the lone '*' is accepted, not a pattern.";
            }
        }
    }

    /// <summary>
    /// What is wrong with the token an environment variable holds, or null: set, and
    /// <see cref="MinTokenLength"/> characters or more once trimmed. The sentence completes the engine's
    /// « the tool bridge refuses the start: ».
    /// </summary>
    public static string? TokenProblem(string? value, string variable)
    {
        var token = value?.Trim();
        if (string.IsNullOrEmpty(token))
            return $"it reads its token from the environment variable {variable}, which is not set.";
        if (token.Length < MinTokenLength)
            return $"the token in the environment variable {variable} is shorter than {MinTokenLength} characters: use a long random value (openssl rand -hex 32).";
        return null;
    }

    /// <summary>The binder's refusal of a text it cannot convert, as the host's start words it.</summary>
    public static string ConversionProblem(string written, string path, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return $"Failed to convert configuration value '{written}' at '{path}' to type '{type.FullName}'.";
    }

    /// <summary>Reads a duration as the binder does: the invariant <c>[d.]hh:mm:ss</c>.</summary>
    public static bool TryReadDuration(string text, out TimeSpan value) =>
        TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value);
}
