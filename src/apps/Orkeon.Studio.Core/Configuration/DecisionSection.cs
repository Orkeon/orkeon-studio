using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Infrastructure.LLMs.Decision;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// Typed view over the decision models (System One): the circuit's switch and provider name
/// (<c>Orkeon:Decision:Enabled</c>, <c>Orkeon:Decision:Provider</c>) and the section of each provider
/// (<c>Orkeon:TypeSafe</c>, <c>Orkeon:Liquid</c>, <c>Orkeon:Kev</c>). Every write lands in place, field by
/// field, so a key Studio does not model (the decision profiles under <c>Orkeon:Decision:Profiles</c>, a
/// provider's <c>ApiKey</c>) survives an edit. The providers, their sections and their defaults are the
/// engine's own (<see cref="DecisionProviderDescriptor"/>), read from the <c>Orkeon</c> package.
/// </summary>
public sealed class DecisionSection
{
    /// <summary>Configuration path of the circuit's section.</summary>
    public const string SectionPath = DecisionCircuitOptions.SectionName;

    /// <summary>Configuration path of the circuit's switch.</summary>
    public const string EnabledPath = SectionPath + ":Enabled";

    /// <summary>Configuration path of the provider's name.</summary>
    public const string ProviderPath = SectionPath + ":Provider";

    /// <summary>
    /// Configuration path of the runners' strict tool resolution (on by default): on, a crew that names a
    /// tool no registration gives is refused at load; off, the tool is dropped with a log line.
    /// </summary>
    public const string StrictToolsPath = "Orkeon:CrewFactory:StrictTools";

    /// <summary>The prefix of the environment variables a run reads as settings, above the file.</summary>
    public const string EnvironmentPrefix = Orkeon.Constants.Configuration.EnvironmentVariableNames.SettingsPrefix;

    // The fields of a provider's section (SystemOneProviderOptions), probed in the environment.
    private static readonly string[] ProviderFields = ["ApiKey", "ApiKeyEnvVar", "BaseUrl", "Model", "TimeoutSeconds", "MaxRetries"];

    // The engine's defaults (SystemOneProviderOptions), copied so the settings screen can show them
    // as watermarks; EngineDefaultsDriftTests pins each one against the original.

    /// <summary>Default of a provider's <c>TimeoutSeconds</c>.</summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>Default of a provider's <c>MaxRetries</c>.</summary>
    public const int DefaultMaxRetries = 3;

    private readonly AppSettingsDocument _document;

    internal DecisionSection(AppSettingsDocument document) => _document = document;

    /// <summary>The providers the engine knows, in the order its documentation lists them.</summary>
    public static IReadOnlyList<DecisionProviderDescriptor> Providers => DecisionProviderDescriptor.All;

    /// <summary>
    /// Whether the circuit's section is written, in any letter case: the keys are read and written
    /// where the file spells them (<see cref="AppSettingsDocument.PathAsWritten"/>), as the run binds them.
    /// </summary>
    public bool Exists => _document.GetNode(_document.PathAsWritten(SectionPath)) is not null;

    /// <summary>The switch as written; absent means on, which <see cref="IsEnabled"/> resolves.</summary>
    public bool? Enabled
    {
        get => _document.GetBoolean(_document.PathAsWritten(EnabledPath));
        set => _document.SetBoolean(_document.PathAsWritten(EnabledPath), value);
    }

    /// <summary>The path of the switch as the file spells it: where its text is read when it reads as no boolean.</summary>
    public string EnabledPathAsWritten => _document.PathAsWritten(EnabledPath);

    /// <summary>Whether decision tasks ask the decision model: the switch defaults to on.</summary>
    public bool IsEnabled => Enabled ?? true;

    /// <summary>The provider's name as written (<c>typesafe</c>, <c>liquid</c>, <c>kev</c>); null deduces it.</summary>
    public string? Provider
    {
        get => _document.GetString(_document.PathAsWritten(ProviderPath));
        set => _document.SetString(_document.PathAsWritten(ProviderPath), string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>The settings of the provider <paramref name="descriptor"/> names.</summary>
    public DecisionProviderSettings For(DecisionProviderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new DecisionProviderSettings(_document, descriptor);
    }

    /// <summary>
    /// Which provider a run of these settings uses, as the engine resolves it
    /// (<c>DecisionProviderChoice.Resolve</c>), whether the circuit is on, and whether the runner
    /// resolves tools strictly. The settings are composed as the runner composes them
    /// (<c>RunnerSettings.ComposeSources</c>): the environment variables without a prefix, then this
    /// file, then the <c>ORKEON_</c> variables, which win. The environment is probed through
    /// <paramref name="environment"/> for the names these settings take (<c>Orkeon__Decision__Provider</c>,
    /// <c>ORKEON_Orkeon__TypeSafe__Model</c>...), each spelt as here: a variable the run reads in another
    /// letter case (Linux tells them apart, the configuration does not) is not seen.
    /// </summary>
    /// <param name="environment">
    /// Reads an environment variable: where the providers' keys live, and the settings a run reads
    /// above or under the file. It should see what the spawned CLI inherits.
    /// </param>
    public DecisionStatus Resolve(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var configuration = ToConfiguration(_document.Root, environment);

        // The switches as the engine binds them: keys in any case, a value bool.Parse reads, else a refusal.
        if (!TryReadSwitch(configuration, EnabledPath, out var enabled, out var refusal)
            || !TryReadSwitch(configuration, StrictToolsPath, out var strictTools, out refusal))
        {
            return new DecisionStatus(true, Provider: null, DecisionProviderSelection.Deduced, [], refusal);
        }

        try
        {
            var choice = DecisionProviderChoice.Resolve(configuration, environment);
            return new DecisionStatus(
                enabled,
                choice.Provider,
                choice.How,
                choice.IgnoredSections,
                choice.Provider is { } chosen ? RefuseOptions(configuration, chosen) : null)
            {
                MissingKeyVariable = choice.Provider is { } provider ? MissingKey(configuration, provider, environment) : null,
                StrictTools = strictTools,
            };
        }
        catch (InvalidOperationException ex)
        {
            return new DecisionStatus(enabled, Provider: null, DecisionProviderSelection.Deduced, [], ex.Message)
            {
                StrictTools = strictTools,
            };
        }
    }

    /// <summary>
    /// A switch as the engine binds it (on when absent or blank), or the binder's refusal of a text
    /// <c>bool.Parse</c> does not read.
    /// </summary>
    private static bool TryReadSwitch(IConfiguration configuration, string path, out bool value, out string? refusal)
    {
        var written = configuration[path];
        value = true;
        refusal = null;
        if (string.IsNullOrWhiteSpace(written) || bool.TryParse(written.Trim(), out value))
            return true;

        value = true;
        refusal = $"Failed to convert configuration value '{written}' at '{path}' to type 'System.Boolean'.";
        return false;
    }

    /// <summary>
    /// What the host's start refuses in the section of the provider in use, as the engine binds and
    /// validates it (<c>AddOrkeonDecisionProvider</c>, validated at start): a value the binder cannot
    /// convert, a timeout under one second, retries under zero, an API root that is no absolute URL.
    /// The sections left aside are bound by nobody. Null when the section is accepted.
    /// </summary>
    private static string? RefuseOptions(IConfiguration configuration, DecisionProviderDescriptor provider)
    {
        var options = new SystemOneProviderOptions();
        provider.ApplyDefaults(options);
        try
        {
            configuration.GetSection(provider.Section).Bind(options);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        // FillBlanks, for the one default the rules below read.
        if (options.BaseUrl is null || string.IsNullOrWhiteSpace(options.BaseUrl.OriginalString))
            options.BaseUrl = provider.DefaultBaseUrl;

        // The engine's own wording, joined as OptionsValidationException joins its failures.
        var failures = new List<string>();
        if (options.TimeoutSeconds <= 0)
            failures.Add($"{provider.Section}:TimeoutSeconds: one second or more.");
        if (options.MaxRetries < 0)
            failures.Add($"{provider.Section}:MaxRetries: zero or more.");
        if (!options.BaseUrl.IsAbsoluteUri)
            failures.Add($"{provider.Section}:BaseUrl: an absolute URL.");
        return failures.Count > 0 ? string.Join("; ", failures) : null;
    }

    /// <summary>
    /// The variable a provider that needs a key reads it from, when no key is in place there nor in the
    /// file (<c>ApiKey</c>): every request it gets then fails. Null when the key is in place or optional.
    /// </summary>
    private static string? MissingKey(IConfiguration configuration, DecisionProviderDescriptor provider, Func<string, string?> environment)
    {
        if (!provider.KeyRequired || !string.IsNullOrWhiteSpace(configuration[$"{provider.Section}:ApiKey"]))
            return null;

        var written = configuration[$"{provider.Section}:ApiKeyEnvVar"];
        var variable = string.IsNullOrWhiteSpace(written) ? provider.DefaultApiKeyVariable : written.Trim();
        return string.IsNullOrWhiteSpace(environment(variable)) ? variable : null;
    }

    /// <summary>
    /// The document as the configuration a run binds, as the JSON provider flattens it: an empty
    /// object or array is a key without a value, so a section written empty still counts as present.
    /// With <paramref name="environment"/>, the settings of the decision circuit a run also reads from
    /// the environment are laid as the runner lays them: the variables without a prefix under the
    /// file, the <c>ORKEON_</c> ones above it.
    /// </summary>
    internal static IConfiguration ToConfiguration(JsonObject root, Func<string, string?>? environment = null)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (environment is not null)
            LayEnvironment(environment, prefix: "", data);
        Flatten(root, prefix: null, data);
        if (environment is not null)
            LayEnvironment(environment, EnvironmentPrefix, data);
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    /// <summary>The settings a run of the decision circuit reads, as an environment variable names each (<c>:</c> written <c>__</c>).</summary>
    internal static IEnumerable<string> EnvironmentSettings()
    {
        yield return EnabledPath;
        yield return ProviderPath;
        yield return StrictToolsPath;
        foreach (var provider in Providers)
        {
            foreach (var field in ProviderFields)
                yield return $"{provider.Section}:{field}";
        }
    }

    private static void LayEnvironment(Func<string, string?> environment, string prefix, Dictionary<string, string?> data)
    {
        foreach (var setting in EnvironmentSettings())
        {
            if (environment(prefix + setting.Replace(":", "__", StringComparison.Ordinal)) is { } value)
                data[setting] = value;
        }
    }

    private static void Flatten(JsonNode? node, string? prefix, Dictionary<string, string?> data)
    {
        switch (node)
        {
            case JsonObject obj when obj.Count > 0:
                foreach (var (key, child) in obj)
                    Flatten(child, prefix is null ? key : $"{prefix}:{key}", data);
                break;
            case JsonArray array when array.Count > 0:
                for (var i = 0; i < array.Count; i++)
                    Flatten(array[i], $"{prefix}:{i}", data);
                break;
            case JsonObject or JsonArray or null:
                if (prefix is not null)
                    data[prefix] = null;
                break;
            default:
                if (prefix is not null)
                    data[prefix] = AppSettingsDocument.ReadString(node);
                break;
        }
    }
}

/// <summary>The decision circuit a run of the settings gets, as the engine resolves it.</summary>
/// <param name="Enabled">The <c>Orkeon:Decision:Enabled</c> switch, on by default.</param>
/// <param name="Provider">The provider the run uses, or null when none is configured (or the choice is refused).</param>
/// <param name="How">Whether the provider was named or deduced.</param>
/// <param name="IgnoredSections">The provider sections present but not used.</param>
/// <param name="Refusal">
/// The engine's message when it refuses the settings at start (an unknown name, two providers that could
/// be deduced, a Kev address the key may not travel to, a switch that is neither true nor false, a section
/// of the provider in use its validation refuses); null otherwise.
/// </param>
public sealed record DecisionStatus(
    bool Enabled,
    DecisionProviderDescriptor? Provider,
    DecisionProviderSelection How,
    IReadOnlyList<string> IgnoredSections,
    string? Refusal)
{
    /// <summary>Whether a run asks the decision model: the switch is on and a provider is chosen.</summary>
    public bool CircuitOn => Enabled && Provider is not null && Refusal is null;

    /// <summary>
    /// The variable the chosen provider reads its key from, when it needs one and none is in place there
    /// nor in the file: each request then fails. Null when the key is in place, optional, or no provider is chosen.
    /// </summary>
    public string? MissingKeyVariable { get; init; }

    /// <summary>
    /// Whether the runner resolves tools strictly (<c>Orkeon:CrewFactory:StrictTools</c>, on by default):
    /// on, a holder of <c>system_one_decide</c> without a provider is refused at load, a check included;
    /// off, the tool is dropped and the crew runs without it.
    /// </summary>
    public bool StrictTools { get; init; } = true;
}

/// <summary>
/// The section of one decision provider (<c>Orkeon:TypeSafe</c>, <c>Orkeon:Liquid</c>, <c>Orkeon:Kev</c>),
/// as <c>SystemOneProviderOptions</c> binds it. A blank value removes its key: the engine then gives the
/// provider's default (<c>DecisionProviderDescriptor.FillBlanks</c>).
/// </summary>
public sealed class DecisionProviderSettings
{
    private readonly AppSettingsDocument _document;

    internal DecisionProviderSettings(AppSettingsDocument document, DecisionProviderDescriptor descriptor)
    {
        _document = document;
        Descriptor = descriptor;
    }

    /// <summary>The provider, as the engine describes it.</summary>
    public DecisionProviderDescriptor Descriptor { get; }

    /// <summary>The section's path, as the engine spells it.</summary>
    public string SectionPath => Descriptor.Section;

    /// <summary>The path of a field, as the file spells the keys it holds (<see cref="AppSettingsDocument.PathAsWritten"/>).</summary>
    private string Field(string name) => _document.PathAsWritten($"{SectionPath}:{name}");

    /// <summary>Whether the section is written, even empty or null: what the engine deduces a provider from.</summary>
    public bool Exists => DecisionProviderChoice.HasSection(DecisionSection.ToConfiguration(_document.Root), SectionPath);

    /// <summary>The API root; null gives the provider's (<see cref="DecisionProviderDescriptor.DefaultBaseUrl"/>).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056",
        Justification = "The JSON field as typed: an in-progress or malformed URL must survive an edit; the engine judges it at start.")]
    public string? BaseUrl
    {
        get => _document.GetString(Field("BaseUrl"));
        set => _document.SetString(Field("BaseUrl"), Blank(value));
    }

    /// <summary>The model; null gives the provider's (<see cref="DecisionProviderDescriptor.DefaultModel"/>).</summary>
    public string? Model
    {
        get => _document.GetString(Field("Model"));
        set => _document.SetString(Field("Model"), Blank(value));
    }

    /// <summary>The variable the key is read from; null gives the provider's (<see cref="DecisionProviderDescriptor.DefaultApiKeyVariable"/>).</summary>
    public string? ApiKeyEnvVar
    {
        get => _document.GetString(Field("ApiKeyEnvVar"));
        set => _document.SetString(Field("ApiKeyEnvVar"), Blank(value));
    }

    /// <summary>The variable a run reads the key from: the one written, trimmed, else the provider's.</summary>
    public string EffectiveApiKeyEnvVar =>
        ApiKeyEnvVar is { } written && !string.IsNullOrWhiteSpace(written) ? written.Trim() : Descriptor.DefaultApiKeyVariable;

    /// <summary>Whether a key is written in the file itself (<c>ApiKey</c>): Studio never writes one.</summary>
    public bool HasApiKeyInFile => !string.IsNullOrEmpty(_document.GetString(Field("ApiKey")));

    /// <summary>The time a request may take, in seconds; null gives <see cref="DecisionSection.DefaultTimeoutSeconds"/>.</summary>
    public int? TimeoutSeconds
    {
        get => _document.GetInt32(Field("TimeoutSeconds"));
        set => _document.SetInt32(Field("TimeoutSeconds"), value);
    }

    /// <summary>The retries of a failed request; null gives <see cref="DecisionSection.DefaultMaxRetries"/>.</summary>
    public int? MaxRetries
    {
        get => _document.GetInt32(Field("MaxRetries"));
        set => _document.SetInt32(Field("MaxRetries"), value);
    }

    /// <summary>Writes the section, empty when nothing is set: an empty section still makes the provider deducible.</summary>
    public void Create()
    {
        var path = _document.PathAsWritten(SectionPath);
        if (_document.GetNode(path) is not JsonObject)
            _document.SetNode(path, new JsonObject());
    }

    /// <summary>Removes the whole section, under every letter case the file spells it in.</summary>
    public void Remove()
    {
        var parentPath = ConfigurationPath.GetParentPath(SectionPath)!;
        var leaf = ConfigurationPath.GetSectionKey(SectionPath);
        if (_document.GetNode(_document.PathAsWritten(parentPath)) is not JsonObject parent)
            return;

        foreach (var key in parent.Select(property => property.Key)
                     .Where(key => string.Equals(key, leaf, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            parent.Remove(key);
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
