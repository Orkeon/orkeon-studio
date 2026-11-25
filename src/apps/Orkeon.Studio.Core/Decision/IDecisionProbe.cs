using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Decision;

/// <summary>What to ask a decision provider, and how to reach it.</summary>
public sealed record DecisionProbeRequest
{
    /// <summary>The provider asked.</summary>
    public required DecisionProviderDescriptor Provider { get; init; }

    /// <summary>The API root; blank gives the provider's.</summary>
    public string? BaseAddress { get; init; }

    /// <summary>The model; blank gives the provider's.</summary>
    public string? Model { get; init; }

    /// <summary>The key; null when none is in place. Never shown, never written.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The variable the key is read from, named when it is missing.</summary>
    public string? KeyVariable { get; init; }

    /// <summary>How long the request may take, in seconds.</summary>
    public int TimeoutSeconds { get; init; } = DecisionSection.DefaultTimeoutSeconds;

    /// <summary>The case to judge; null asks the built-in questions, whose answers are known.</summary>
    public DecisionProbeCase? Case { get; init; }

    /// <summary>
    /// The request a run of <paramref name="section"/> would make of <paramref name="provider"/>, its key
    /// read as the engine reads it: the file, then the variable through <paramref name="environment"/>.
    /// </summary>
    public static DecisionProbeRequest For(
        DecisionSection section, DecisionProviderDescriptor provider, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(environment);

        var settings = section.For(provider);
        return new DecisionProbeRequest
        {
            Provider = provider,
            BaseAddress = settings.BaseUrl,
            Model = settings.Model,
            ApiKey = settings.ApiKeyInFile ?? environment(settings.EffectiveApiKeyEnvVar),
            KeyVariable = settings.EffectiveApiKeyEnvVar,
            TimeoutSeconds = Math.Min(
                settings.TimeoutSeconds ?? DecisionSection.DefaultTimeoutSeconds, DecisionSection.DefaultTimeoutSeconds),
        };
    }
}

/// <summary>A case of the user's own: a text, a yes/no question about it and the options to choose among.</summary>
/// <param name="State">The text judged.</param>
/// <param name="Question">The yes/no question; blank asks none.</param>
/// <param name="Options">The options of the choice; fewer than two asks none.</param>
public sealed record DecisionProbeCase(string State, string Question, IReadOnlyList<string> Options);

/// <summary>Why a decision probe failed as a whole; <see cref="None"/> when the provider answered.</summary>
public enum DecisionProbeFailure
{
    /// <summary>The provider answered.</summary>
    None,

    /// <summary>The provider needs a key and none is in place: nothing was sent.</summary>
    KeyMissing,

    /// <summary>The address is no absolute http(s) URL: nothing was sent.</summary>
    AddressInvalid,

    /// <summary>The case asks nothing: nothing was sent.</summary>
    NothingAsked,

    /// <summary>The call failed; <see cref="DecisionProbeResult.Detail"/> says how.</summary>
    Refused,
}

/// <summary>One question's answer.</summary>
/// <param name="Name">The question's name.</param>
/// <param name="Passed">Whether the answer is the expected one; null for a case of the user's own.</param>
/// <param name="Answer">The answer with its probabilities, in plain English: it is evidence, not copy.</param>
public sealed record DecisionProbeCheck(string Name, bool? Passed, string Answer);

/// <summary>What a decision provider answered. Nothing here ever carries the key.</summary>
public sealed record DecisionProbeResult
{
    /// <summary>The provider asked.</summary>
    public required DecisionProviderDescriptor Provider { get; init; }

    /// <summary>Why the probe failed as a whole.</summary>
    public DecisionProbeFailure Failure { get; init; }

    /// <summary>The untranslated cause of <see cref="Failure"/>.</summary>
    public string? Detail { get; init; }

    /// <summary>The address called, without credentials nor query string.</summary>
    public string? Address { get; init; }

    /// <summary>The model that answered, else the one asked.</summary>
    public string? Model { get; init; }

    /// <summary>The time to the verdict.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>The answers, in the order of the questions.</summary>
    public IReadOnlyList<DecisionProbeCheck> Checks { get; init; } = [];

    /// <summary>The tokens read, when the provider counts them.</summary>
    public int? InputTokens { get; init; }

    /// <summary>Whether the provider answered, and no answer is a wrong one.</summary>
    public bool Succeeded => Failure == DecisionProbeFailure.None && Checks.All(check => check.Passed != false);
}

/// <summary>Asks a decision provider (System One) a few questions, to learn that it answers and answers right.</summary>
public interface IDecisionProbe
{
    /// <summary>Asks, and describes what happened; only the caller's own cancellation throws.</summary>
    Task<DecisionProbeResult> ProbeAsync(DecisionProbeRequest request, CancellationToken cancellationToken = default);
}
