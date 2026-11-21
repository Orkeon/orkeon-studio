using Orkeon.Constants.Protocol;
using Orkeon.Studio.Core.Localization;

namespace Orkeon.Studio.Core.Run;

/// <summary>
/// The sentence of each cause the engine gives for a decision circuit that is off
/// (<see cref="DecisionCircuitCauses"/>): one catalogue key per cause, for the run view's circuit
/// line and the wizard's line on its assistant. A test holds the list against the engine's
/// constants, so a cause the engine adds is given its sentence, never shown as a raw word.
/// </summary>
public static class DecisionCircuitCauseKeys
{
    /// <summary>
    /// The catalogue key of <paramref name="cause"/>'s sentence; null for a cause this build does not
    /// know, which a screen shows as the engine wrote it, and for none.
    /// </summary>
    public static string? KeyOf(string? cause) => cause switch
    {
        DecisionCircuitCauses.Switch => StudioStringKeys.RunNoteCauseSwitch,
        DecisionCircuitCauses.Settings => StudioStringKeys.RunNoteCauseSettings,
        DecisionCircuitCauses.NoProvider => StudioStringKeys.RunNoteCauseNoProvider,
        DecisionCircuitCauses.Trial => StudioStringKeys.RunNoteCauseTrial,
        _ => null,
    };
}
