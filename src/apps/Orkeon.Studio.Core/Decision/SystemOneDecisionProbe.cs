using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orkeon.Application.Decision.DTOs;
using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Studio.Core.Llm;

namespace Orkeon.Studio.Core.Decision;

/// <summary>
/// Asks through <see cref="SystemOneDecisionModel"/>, the engine's own client, in one request without
/// a retry. The built-in questions are those of <c>orkeon llm probe</c> (D1 to D3): each has an
/// obvious answer, so an answer of the right shape and the wrong value is a failure too.
/// </summary>
public sealed class SystemOneDecisionProbe : IDecisionProbe, IDisposable
{
    /// <summary>The text the built-in questions are about.</summary>
    public const string SampleState = "Customer: my parcel never arrived and I want my money back.";

    /// <summary>The built-in yes/no question.</summary>
    public const string SampleQuestion = "Does the customer ask for a refund?";

    /// <summary>The options of the built-in choice.</summary>
    public static IReadOnlyList<string> SampleOptions { get; } = ["delivery", "billing", "account"];

    private const string NoulName = "yes_no";
    private const string ChoiceName = "choice";
    private const string ScoreName = "score";

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeProvider _time;

    /// <summary>Asks through <paramref name="client"/>, which the caller keeps ownership of.</summary>
    public SystemOneDecisionProbe(HttpClient client, TimeProvider? time = null)
        : this(client, ownsClient: false, time)
    {
    }

    private SystemOneDecisionProbe(HttpClient client, bool ownsClient, TimeProvider? time)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownsClient = ownsClient;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Asks over the real network, through a client this probe owns.</summary>
    public static SystemOneDecisionProbe ForCurrentMachine() => new(new HttpClient(), ownsClient: true, time: null);

    /// <inheritdoc />
    public async Task<DecisionProbeResult> ProbeAsync(DecisionProbeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var provider = request.Provider;
        var model = string.IsNullOrWhiteSpace(request.Model) ? provider.DefaultModel : request.Model.Trim();
        var result = new DecisionProbeResult { Provider = provider, Model = model };

        // An empty key is not passed on: the client would look for one of its own in the environment.
        var key = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim();
        if (provider.KeyRequired && key is null)
            return result with { Failure = DecisionProbeFailure.KeyMissing, Detail = request.KeyVariable ?? provider.DefaultApiKeyVariable };

        if (AddressOf(request) is not { } address)
            return result with { Failure = DecisionProbeFailure.AddressInvalid, Detail = SecretMasker.Apply(request.BaseAddress ?? "", [key]) };

        result = result with
        {
            Address = address.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/') + "/systemone",
        };
        var questions = request.Case is { } own ? QuestionsOf(own) : BuiltIn();
        if (questions.Count == 0)
            return result with { Failure = DecisionProbeFailure.NothingAsked };

        var asked = new SystemOneDecisionModel(
            provider,
            new OneClientFactory(_client),
            Options.Create(new SystemOneProviderOptions
            {
                ApiKey = key,
                ApiKeyEnvVar = request.KeyVariable ?? provider.DefaultApiKeyVariable,
                BaseUrl = address,
                Model = model,
                TimeoutSeconds = Math.Max(1, request.TimeoutSeconds),
                MaxRetries = 0,
            }),
            NullLogger<SystemOneDecisionModel>.Instance);

        var started = _time.GetTimestamp();
        try
        {
            var response = await asked.DecideAsync(
                new DecisionRequest
                {
                    State = JsonValue.Create(request.Case?.State ?? SampleState),
                    Questions = questions,
                },
                cancellationToken).ConfigureAwait(false);

            return result with
            {
                Model = response.Model,
                Elapsed = _time.GetElapsedTime(started),
                InputTokens = response.Usage.InputTokens,
                Checks = [.. new[] { NoulName, ChoiceName, ScoreName }
                    .Where(questions.ContainsKey)
                    .Select(name => Check(name, response, judged: request.Case is null))],
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return result with
            {
                Failure = DecisionProbeFailure.Refused,
                Detail = SecretMasker.Apply(ex.Message, [key]),
                Elapsed = _time.GetElapsedTime(started),
            };
        }
    }

    private static Uri? AddressOf(DecisionProbeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BaseAddress))
            return request.Provider.DefaultBaseUrl;

        return Uri.TryCreate(request.BaseAddress.Trim(), UriKind.Absolute, out var address)
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
                ? address
                : null;
    }

    private static ImmutableDictionary<string, DecisionQuestion> BuiltIn() =>
        ImmutableDictionary.CreateRange<string, DecisionQuestion>(
        [
            KeyValuePair.Create<string, DecisionQuestion>(NoulName, new NoulQuestion { Instructions = JsonValue.Create(SampleQuestion) }),
            KeyValuePair.Create<string, DecisionQuestion>(ChoiceName, Choice(
                ("delivery", "A delivery problem"), ("billing", "A billing question"), ("account", "An account or login issue"))),
            KeyValuePair.Create<string, DecisionQuestion>(ScoreName, new ScoreQuestion
            {
                Instructions = JsonValue.Create("How urgent is the customer's request?"),
                Criteria =
                [
                    JsonValue.Create("Not urgent: a general question.")!,
                    JsonValue.Create("Somewhat urgent: an inconvenience.")!,
                    JsonValue.Create("Urgent: money or goods were lost.")!,
                ],
            }),
        ]);

    private static ImmutableDictionary<string, DecisionQuestion> QuestionsOf(DecisionProbeCase own)
    {
        var questions = ImmutableDictionary.CreateBuilder<string, DecisionQuestion>();
        if (string.IsNullOrWhiteSpace(own.State))
            return questions.ToImmutable();

        if (!string.IsNullOrWhiteSpace(own.Question))
            questions.Add(NoulName, new NoulQuestion { Instructions = JsonValue.Create(own.Question.Trim()) });

        var options = own.Options.Select(option => option.Trim()).Where(option => option.Length > 0)
            .Distinct(StringComparer.Ordinal).ToList();
        if (options.Count >= 2)
            questions.Add(ChoiceName, Choice([.. options.Select(option => (option, default(string)))]));

        return questions.ToImmutable();
    }

    private static ChoiceQuestion Choice(params (string Name, string? When)[] options) => new()
    {
        Instructions = JsonValue.Create("Which option fits the message best?"),
        Criteria = ImmutableDictionary.CreateRange(
            options.Select(option => KeyValuePair.Create(option.Name, option.When is { } when ? (JsonNode?)JsonValue.Create(when) : null))),
    };

    private static DecisionProbeCheck Check(string name, DecisionResponse response, bool judged)
    {
        response.Answers.TryGetValue(name, out var answer);
        return answer switch
        {
            NoulAnswer noul => new(name, judged ? noul.Noul is >= 0.5 and <= 1.0 : null,
                $"yes = {Format(noul.Noul)}" + (judged ? " (expected at least 0.5)" : "")),
            ChoiceAnswer choice => new(name, judged ? choice.Choice == "delivery" : null,
                $"{choice.Choice}, confidence {Format(choice.Confidence)}"
                + $" ({string.Join(", ", choice.Probabilities.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {Format(p.Value)}"))})"
                + (judged ? " (expected delivery)" : "")),
            ScoreAnswer score => new(name, judged ? score.Score is >= 1.0 and <= 2.0 : null,
                $"{Format(score.Score)} on 0-2, confidence {Format(score.Confidence)}" + (judged ? " (expected at least 1)" : "")),
            null => new(name, false, "no answer"),
            _ => new(name, false, $"unexpected {answer.GetType().Name}"),
        };
    }

    private static string Format(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    /// <summary>The factory the engine's client asks: always the probe's own client.</summary>
    private sealed class OneClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
