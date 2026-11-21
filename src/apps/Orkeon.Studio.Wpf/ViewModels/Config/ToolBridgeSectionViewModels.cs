using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Llm;
using Orkeon.Studio.Core.Localization;

namespace Orkeon.Studio.Wpf.ViewModels.Config;

/// <summary>
/// The caller's side of the tool bridge (<c>Orkeon:Tools:Bridge</c>): the address of the bridge, the
/// tools a run executes there, the variable that holds the token and the row that stores it in the
/// user environment, never in the file. What the engine's start refuses is said under the fields, in
/// the engine's words. The timeouts and sizes keep the engine's defaults and are edited in the JSON tab.
/// </summary>
public sealed class ToolBridgeSectionViewModel : DocumentSectionViewModel
{
    private readonly IStudioStrings _strings;
    private readonly IApiKeyStore _keyStore;
    private SecretRowViewModel _token;

    /// <summary>Binds the form to the <c>Orkeon:Tools:Bridge</c> section of the document.</summary>
    public ToolBridgeSectionViewModel(
        Func<AppSettingsDocument> document, Action onChanged, IStudioStrings? strings = null, IApiKeyStore? keyStore = null)
        : base(document, onChanged)
    {
        _strings = strings ?? EnglishStudioStrings.Instance;
        _keyStore = keyStore ?? new EnvironmentApiKeyStore();
        _token = NewTokenRow();
        _strings.CultureChanged += (_, _) => _token.RefreshTexts();
    }

    private ToolBridgeSection Section => Document.ToolBridge;

    /// <inheritdoc />
    public override bool Exists => Section.Exists;

    /// <summary>The bridge's base address; empty, nothing is bridged.</summary>
    public string Address
    {
        get => Section.Address ?? string.Empty;
        set => SetValue(Address, value?.Trim() ?? string.Empty, v => Section.Address = v);
    }

    /// <summary>The tools to bridge, comma-separated, or the lone <c>*</c>; emptied, the key is removed.</summary>
    public string AllowText
    {
        get => string.Join(", ", Section.Allow);
        set => SetValue(AllowText, Joined(value), v => Section.Allow = DecisionSection.SplitList(v));
    }

    /// <summary>The tools never bridged, comma-separated; emptied, the key is removed.</summary>
    public string DenyText
    {
        get => string.Join(", ", Section.Deny);
        set => SetValue(DenyText, Joined(value), v => Section.Deny = DecisionSection.SplitList(v));
    }

    /// <summary>The variable that holds the token; empty, the engine's.</summary>
    public string TokenEnvVar
    {
        get => Section.TokenEnvVar ?? string.Empty;
        set => SetValue(TokenEnvVar, value?.Trim() ?? string.Empty, v => Section.TokenEnvVar = v);
    }

    /// <summary>The watermark of <see cref="TokenEnvVar"/>.</summary>
    public static string TokenEnvVarDefault => ToolBridgeSection.DefaultTokenEnvVar;

    /// <summary>The <c>Host</c> header sent to the bridge, when it differs from the address's.</summary>
    public string HostHeader
    {
        get => Section.HostHeader ?? string.Empty;
        set => SetValue(HostHeader, value?.Trim() ?? string.Empty, v => Section.HostHeader = v);
    }

    /// <summary>The row that stores the token in the user environment, under the variable the section names.</summary>
    public SecretRowViewModel Token => _token;

    /// <summary>What the engine's start refuses in the section, one line each; empty when it is accepted.</summary>
    public string ProblemsText => string.Join("\n", Section.Problems().Select(problem => problem.Problem));

    /// <inheritdoc />
    public override void Refresh()
    {
        base.Refresh();
        FollowTokenVariable();
    }

    /// <inheritdoc />
    protected override void OnSectionChanged()
    {
        FollowTokenVariable();
        OnPropertiesChanged(
            nameof(Exists), nameof(Address), nameof(AllowText), nameof(DenyText), nameof(TokenEnvVar), nameof(HostHeader),
            nameof(ProblemsText));
    }

    // The row stores under the variable the section names: another name, another row.
    private void FollowTokenVariable()
    {
        if (string.Equals(_token.EnvName, Section.EffectiveTokenEnvVar, StringComparison.Ordinal))
            return;

        _token = NewTokenRow();
        OnPropertyChanged(nameof(Token));
    }

    private SecretRowViewModel NewTokenRow() =>
        new(Section.EffectiveTokenEnvVar, () => _strings[StudioStringKeys.SettingsBridgeTokenUsedBy], _keyStore, _strings);

    private static string Joined(string? text) => string.Join(", ", DecisionSection.SplitList(text));
}

/// <summary>
/// The serving side of the tool bridge (<c>Orkeon:Bridge</c>): the address <c>orkeon bridge serve</c>
/// listens on, the tools it runs for a caller and the variable that holds the token. Studio starts no
/// bridge: the card sets what the command reads. What the engine's start refuses is said under the
/// fields; every host validates this section, the serving command or not.
/// </summary>
public sealed class BridgeServerSectionViewModel : DocumentSectionViewModel
{
    /// <summary>Binds the form to the <c>Orkeon:Bridge</c> section of the document.</summary>
    public BridgeServerSectionViewModel(Func<AppSettingsDocument> document, Action onChanged)
        : base(document, onChanged)
    {
    }

    private BridgeServerSection Section => Document.BridgeServer;

    /// <inheritdoc />
    public override bool Exists => Section.Exists;

    /// <summary>The listener's prefix; empty, the engine's for this machine.</summary>
    public string Prefix
    {
        get => Section.Prefix ?? string.Empty;
        set => SetValue(Prefix, value?.Trim() ?? string.Empty, v => Section.Prefix = v);
    }

    /// <summary>The watermark of <see cref="Prefix"/>.</summary>
    public static string PrefixDefault => BridgeServerSection.DefaultPrefix;

    /// <summary>The tools served, comma-separated, or the lone <c>*</c>; emptied, the key is removed.</summary>
    public string AllowText
    {
        get => string.Join(", ", Section.Allow);
        set => SetValue(AllowText, Joined(value), v => Section.Allow = DecisionSection.SplitList(v));
    }

    /// <summary>The tools never served, comma-separated; emptied, the key is removed.</summary>
    public string DenyText
    {
        get => string.Join(", ", Section.Deny);
        set => SetValue(DenyText, Joined(value), v => Section.Deny = DecisionSection.SplitList(v));
    }

    /// <summary>The variable that holds the token; empty, the engine's.</summary>
    public string TokenEnvVar
    {
        get => Section.TokenEnvVar ?? string.Empty;
        set => SetValue(TokenEnvVar, value?.Trim() ?? string.Empty, v => Section.TokenEnvVar = v);
    }

    /// <summary>What the engine's start refuses in the section, one line each; empty when it is accepted.</summary>
    public string ProblemsText => string.Join("\n", Section.Problems().Select(problem => problem.Problem));

    /// <inheritdoc />
    protected override void OnSectionChanged() => OnPropertiesChanged(
        nameof(Exists), nameof(Prefix), nameof(AllowText), nameof(DenyText), nameof(TokenEnvVar), nameof(ProblemsText));

    private static string Joined(string? text) => string.Join(", ", DecisionSection.SplitList(text));
}
