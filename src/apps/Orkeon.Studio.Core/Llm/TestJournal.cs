using Orkeon.Compliance.Vfs;
using System.Globalization;
using System.Text;
using Orkeon.Studio.Core.Decision;

namespace Orkeon.Studio.Core.Llm;

/// <summary>
/// What the tests sent and got back, as plain text to copy into a report: in English like every
/// log, and without a key — the probes' results never carry one.
/// </summary>
public sealed class TestJournal(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly StringBuilder _text = new();

    /// <summary>The whole journal.</summary>
    public string Text => _text.ToString();

    /// <summary>Empties it.</summary>
    public void Clear() => _text.Clear();

    /// <summary>Something said about <paramref name="name"/> without an answer: a refusal, a stop.</summary>
    public void Note(string name, string message) => Entry(name, message);

    /// <summary>A model's answer to <paramref name="request"/>.</summary>
    public void Add(string name, LlmProbeRequest request, LlmProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        Entry(name, result.Succeeded ? "passed" : "FAILED");
        Line("model", request.Model);
        Line("url", result.Url);
        Line("elapsed", Seconds(result.Elapsed));
        Line("status", result.StatusCode is { } code ? $"{code} {result.ReasonPhrase}".Trim() : null);
        Line("prompt", request.Prompt);
        Line("reply", result.ReplyText is { Length: 0 } ? "(empty)" : result.ReplyText);
        Line("tokens", result.Usage is { } usage ? Tokens(usage.InputTokens, usage.OutputTokens) : null);
        Line("verdict", result.Succeeded ? null : result.Message);
        Line("detail", result.FullDetail ?? result.Detail);
    }

    /// <summary>A decision provider's answers.</summary>
    public void Add(string name, DecisionProbeRequest request, DecisionProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        Entry(name, result.Succeeded ? "passed" : result.Failure == DecisionProbeFailure.None ? "FAILED (unexpected answer)" : $"FAILED ({result.Failure})");
        Line("model", result.Model);
        Line("url", result.Address);
        Line("elapsed", Seconds(result.Elapsed));
        Line("state", request.Case?.State ?? SystemOneDecisionProbe.SampleState);
        foreach (var check in result.Checks)
            Line(check.Name, check.Answer + Judgement(check.Passed));
        Line("tokens", result.InputTokens is { } tokens ? Tokens(tokens, null) : null);
        Line("detail", result.Detail);
    }

    /// <summary>Writes <paramref name="text"/> to the file the user picked.</summary>
    [SuppressVfsCompliance("EXCEPTION-BOOTSTRAP: a file the user picked in a save dialog, outside any workspace.")]
    public static Task SaveAsync(string path, string text) => File.WriteAllTextAsync(path, text);

    private static string Judgement(bool? passed)
    {
        if (passed is not { } right)
            return "";
        return right ? " - right" : " - WRONG";
    }

    private void Entry(string name, string verdict)
    {
        if (_text.Length > 0)
            _text.AppendLine();
        _text.Append('[').Append(_time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("] ")
            .Append(name).Append(": ").AppendLine(verdict);
    }

    private void Line(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        _text.Append("  ").Append(label).Append(": ").AppendLine(value.Trim().ReplaceLineEndings(Environment.NewLine + "    "));
    }

    private static string? Seconds(TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero ? elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s" : null;

    private static string Tokens(int? input, int? output) =>
        string.Join(", ", new[] { input is { } i ? $"{i.ToString(CultureInfo.InvariantCulture)} in" : null, output is { } o ? $"{o.ToString(CultureInfo.InvariantCulture)} out" : null }.OfType<string>());
}
