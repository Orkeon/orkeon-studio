using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Validation;

namespace Orkeon.Studio.Wpf.ViewModels.Launch;

/// <summary>
/// The <c>Provider</c> of the <c>Llm</c> section and of its profiles in the settings file the run reads
/// (<see cref="LlmProviderRules"/>, Orkeon #457), and the agent-cli module's <c>Llm:AgentCli</c> (#470):
/// what the host's start refuses there blocks « Launch now » and the check, as a refused bridge does. Judged on a pinned or team file alone: while the CLI picks the
/// file itself, the machine's settings are judged by the Settings tab.
/// </summary>
public sealed partial class LaunchTabViewModel
{
    private AppSettingsDocument? _llmProviderJudged;
    private IReadOnlyList<ValidationMessage> _llmProviderRefusals = [];

    /// <summary>The refusals of the file the run reads, judged once per document read.</summary>
    private IReadOnlyList<ValidationMessage> LlmProviderRefusals
    {
        get
        {
            var document = _settingsDecisionKnown ? _settingsDecisionDocument : null;
            if (!ReferenceEquals(document, _llmProviderJudged))
            {
                _llmProviderJudged = document;
                _llmProviderRefusals = document is null ? [] : LlmProviderRules.StartRefusals(document);
            }

            return _llmProviderRefusals;
        }
    }

    /// <summary>Whether a <c>Provider</c> of the file the run reads stops it at start, a check included.</summary>
    public bool IsBlockedByLlmProvider => LlmProviderRefusals.Count > 0;

    /// <summary>Why the model settings stop the run, in the engine's words and naming the file; empty when they do not.</summary>
    public string LlmProviderMessage => IsBlockedByLlmProvider
        ? _strings.Format(
            StudioStringKeys.RunBlockedLlmProvider,
            string.Join(" ", LlmProviderRefusals.Select(refusal => refusal.Text)),
            ResolvedSettingsPath ?? "")
        : "";

    /// <summary>Tells the alerts and the commands that the file the run reads was judged again.</summary>
    private void RaiseLlmProviderChanged()
    {
        OnPropertiesChanged(nameof(IsBlockedByLlmProvider), nameof(LlmProviderMessage));
        RunCommand.RaiseCanExecuteChanged();
        ValidateCommand.RaiseCanExecuteChanged();
    }
}
