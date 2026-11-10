namespace Orkeon.Studio.Tests.Shared;

/// <summary>
/// The root of an Orkeon source checkout, for the drift tests that pin Studio against files
/// Orkeon ships but does not package: example crews and settings, the installer's sample
/// settings, the tool inventory, the CLI sources. Studio left the Orkeon repository, so these
/// files are no longer beside it; the tests read them from a checkout instead.
/// </summary>
/// <remarks>
/// <c>ORKEON_SOURCE_ROOT</c> names the checkout; unset, a sibling <c>../orkeon</c> of this
/// repository is used. Neither holding <c>Orkeon.sln</c> fails the test with the way out: the
/// drift tests never pass on nothing. The checkout should match the Orkeon packages Studio
/// builds against (<c>OrkeonVersion</c> in <c>Directory.Packages.props</c>).
/// </remarks>
public static class OrkeonSources
{
    /// <summary>The environment variable naming an Orkeon checkout.</summary>
    public const string RootVariable = "ORKEON_SOURCE_ROOT";

    /// <summary>The Orkeon checkout's root directory.</summary>
    public static string Root()
    {
        var configured = Environment.GetEnvironmentVariable(RootVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (File.Exists(Path.Combine(full, "Orkeon.sln")))
                return full;
            throw new InvalidOperationException(
                $"{RootVariable}={configured} holds no Orkeon.sln: point it at a checkout of https://github.com/Orkeon/orkeon.");
        }

        var studioRoot = StudioRoot();
        if (studioRoot is not null)
        {
            var sibling = Path.GetFullPath(Path.Combine(studioRoot, "..", "orkeon"));
            if (File.Exists(Path.Combine(sibling, "Orkeon.sln")))
                return sibling;
        }

        throw new InvalidOperationException(
            $"No Orkeon checkout: set {RootVariable} to a clone of https://github.com/Orkeon/orkeon "
            + "(git clone --depth 1 --filter=blob:none), or clone it next to this repository as ../orkeon.");
    }

    private static string? StudioRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orkeon.Studio.sln")))
            directory = directory.Parent;
        return directory?.FullName;
    }
}
