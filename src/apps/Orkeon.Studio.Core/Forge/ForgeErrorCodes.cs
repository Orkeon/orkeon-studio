namespace Orkeon.Studio.Core.Forge;

/// <summary>
/// The <c>code</c>s of the engine's <c>error</c> events (<see cref="ForgeEventKinds.Error"/>) a screen
/// reads beyond <c>recoverable</c> and <c>terminal</c> — re-declared like the warning codes
/// (<see cref="ForgeWarningCodes"/>), Studio.Core never referencing the CLI; a drift test pins the literal
/// against the engine's own. A code not listed here is said as the engine wrote it.
/// </summary>
public static class ForgeErrorCodes
{
    /// <summary>
    /// The engine host refused the settings file the forge was given (Orkeon #473, #475): a cycle or a
    /// rephrase. A new session is deleted with it (<c>recoverable: false</c>); a resumed one is kept and
    /// resumable once the settings are fixed (<c>recoverable: true</c>) — either way the way out is the
    /// settings, never the diagnostic.
    /// </summary>
    public const string SettingsRefused = "FORGE-SETTINGS-REFUSED";
}
