using Orkeon.Compliance.Vfs;
using Orkeon.Constants.Configuration;

namespace Orkeon.Studio.Core.Process;

/// <summary>Where a located <c>orkeon</c> binary was found.</summary>
public enum BinarySource
{
    /// <summary>Not found anywhere.</summary>
    NotFound,

    /// <summary>In the directory the operator named (the <c>--cli-dir</c> argument).</summary>
    ExplicitDirectory,

    /// <summary>Next to Studio — a Studio installed inside the CLI's own tree.</summary>
    InstallDirectory,

    /// <summary>In the directory named by the <c>ORKEON_CLI_DIR</c> environment variable.</summary>
    EnvironmentVariable,

    /// <summary>On <c>PATH</c> — a separately installed CLI.</summary>
    SearchPath,

    /// <summary>
    /// In the repository checkout Studio itself runs from — the F5-from-the-IDE layout,
    /// where the CLI builds into <c>src/scripting/Orkeon.Scripting.Cli/bin/…</c> instead
    /// of sitting next to Studio.
    /// </summary>
    DevelopmentTree,

    /// <summary>
    /// In the layout of an Orkeon Windows install (its MSI, or its zip installed with
    /// <c>install.ps1</c>, wherever it was put): the <c>libexec\orkeon</c> folder behind a
    /// <c>bin\orkeon.cmd</c> launcher found on <c>PATH</c>, or the installers' default folder,
    /// <c>%LOCALAPPDATA%\Programs\Orkeon\libexec\orkeon</c>.
    /// </summary>
    OrkeonInstaller,
}

/// <summary>
/// The outcome of a binary lookup: a path, or an actionable message plus the list of
/// everything that was tried, so the UI can show the user where Studio looked.
/// </summary>
public sealed record BinaryLocation
{
    /// <summary>Full path of the binary, null when it was not found.</summary>
    public string? Path { get; init; }

    /// <summary>Which lookup step matched.</summary>
    public BinarySource Source { get; init; } = BinarySource.NotFound;

    /// <summary>User-facing explanation, set only when the binary was not found.</summary>
    public string? Error { get; init; }

    /// <summary>Every candidate path that was probed, in order.</summary>
    public IReadOnlyList<string> ProbedPaths { get; init; } = [];

    /// <summary>
    /// Variables the launch must add to the inherited environment, the ones the Orkeon
    /// launcher would have set had Studio gone through it: running <c>libexec/orkeon/orkeon</c>
    /// (<c>orkeon.exe</c> on Windows) directly skips the <c>bin/</c> launcher, which alone
    /// points the CLI at the esbuild the install ships in <c>libexec/esbuild-bin</c>
    /// (<c>ORKEON_ESBUILD_PATH</c>). Set on every platform for that layout, unless the
    /// variable is already set; empty in every other case.
    /// </summary>
    public IReadOnlyDictionary<string, string> LaunchEnvironment { get; init; } = NoVariables;

    /// <summary>True when <see cref="Path"/> holds a usable binary.</summary>
    public bool Found => Path is not null;

    private static readonly IReadOnlyDictionary<string, string> NoVariables =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The environment of a launch of the located binary: <see cref="LaunchEnvironment"/>, then
    /// <paramref name="overrides"/>, which win, as the launcher's own «if not defined» lets a
    /// variable set by the caller stand.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentWith(IReadOnlyDictionary<string, string>? overrides)
    {
        if (LaunchEnvironment.Count == 0)
            return overrides ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var merged = new Dictionary<string, string>(LaunchEnvironment, StringComparer.Ordinal);
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
                merged[key] = value;
        }

        return merged;
    }
}

/// <summary>
/// Finds the <c>orkeon</c> executable Studio drives.
/// <para>
/// In order: the <c>--cli-dir</c> argument, next to Studio, <c>ORKEON_CLI_DIR</c>, <c>PATH</c>,
/// the Orkeon Windows installers' default folder, then the development checkout. Studio's
/// own packages never bundle the CLI; the install directory still comes before
/// <c>ORKEON_CLI_DIR</c> and <c>PATH</c> for a Studio installed inside the CLI's tree
/// (<c>scripts/install-from-source.*</c>), which then drives the CLI it was installed with.
/// On Windows the lookup accepts <c>orkeon.exe</c> alone, and an Orkeon install puts only
/// <c>.cmd</c> launchers in its <c>bin\</c>: a <c>bin\orkeon.cmd</c> in the <c>--cli-dir</c>
/// directory, in <c>ORKEON_CLI_DIR</c> or on <c>PATH</c> leads to its
/// <c>..\libexec\orkeon\orkeon.exe</c>, at that directory's rank.
/// </para>
/// </summary>
[SuppressVfsCompliance(
    "OUT-OF-SCOPE: system binary discovery (the co-installed orkeon executable), the same " +
    "exception the CLI's esbuild resolution carries; no VFS mount exists for install directories.")]
public sealed class OrkeonBinaryLocator
{
    /// <summary>Name of the CLI executable, without extension.</summary>
    public const string ExecutableBaseName = "orkeon";

    /// <summary>Environment variable naming an Orkeon source checkout, the one the drift tests read.</summary>
    public const string SourceRootEnvironmentVariable = "ORKEON_SOURCE_ROOT";

    /// <summary>Environment variable naming the directory that holds the CLI.</summary>
    public const string DirectoryEnvironmentVariable = "ORKEON_CLI_DIR";

    /// <summary>Environment variable naming the per-user local application data folder (Windows).</summary>
    public const string LocalAppDataEnvironmentVariable = "LOCALAPPDATA";

    /// <summary>
    /// The launcher the Orkeon Windows installer puts in its <c>bin\</c>, the folder it adds to
    /// <c>PATH</c>. It is a <c>.cmd</c>, which the lookup never starts: it only tells where the
    /// real <c>orkeon.exe</c> sits, in <c>..\libexec\orkeon</c>.
    /// </summary>
    public const string WindowsLauncherName = ExecutableBaseName + ".cmd";

    /// <summary>Name of the Windows executable, the one name the Windows lookup accepts.</summary>
    private const string WindowsExecutableName = ExecutableBaseName + ".exe";

    /// <summary>
    /// Process-wide directory override, set once at startup from the front-end's
    /// <c>--cli-dir</c> argument. Ambient on purpose: the three CLI clients
    /// (runner, forge, run) all build their default locator through
    /// <see cref="ForCurrentMachine"/>, and the operator's choice must reach every one
    /// of them without re-plumbing each seam.
    /// </summary>
    public static string? DirectoryOverride { get; set; }

    /// <summary>
    /// The two path separators, as one array. Spelled as two char arguments the call also
    /// matches <c>Split(char separator, int count)</c>: the separator list does win that
    /// overload resolution, but only a reader who redoes it can be sure the second char is a
    /// separator and not a maximum count. The array says so outright, and is built once.
    /// </summary>
    private static readonly char[] DirectorySeparators =
        [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar];

    private readonly IExecutableProbe _probe;
    private readonly IReadOnlyList<string> _fileNames;
    private readonly string? _explicitDirectory;
    private readonly Func<string, string?> _environment;

    /// <summary>
    /// True when <see cref="_environment"/> is the process environment: only then may an unset
    /// <c>LOCALAPPDATA</c> fall back to the known folder, which a test reader must not reach.
    /// </summary>
    private readonly bool _readsProcessEnvironment;

    /// <summary>
    /// The first build for another platform walked past during the current <see cref="Locate"/>,
    /// if any: a file named exactly <c>orkeon</c> on a Windows machine, which cannot be started
    /// there. Kept so the failure can name it instead of claiming nothing was seen.
    /// </summary>
    private string? _foreignBuild;

    /// <summary>Creates a locator over <paramref name="probe"/>.</summary>
    /// <param name="probe">File-system and <c>PATH</c> access used for the lookup.</param>
    /// <param name="fileNames">
    /// Candidate file names, most specific first; defaults to the platform's
    /// (<c>orkeon.exe</c> on Windows, <c>orkeon</c> elsewhere).
    /// </param>
    /// <param name="explicitDirectory">The directory the caller was told the CLI lives in (<c>--cli-dir</c>); searched first.</param>
    /// <param name="environment">Environment-variable reader (<c>ORKEON_CLI_DIR</c>); defaults to the process environment.</param>
    public OrkeonBinaryLocator(
        IExecutableProbe probe,
        IReadOnlyList<string>? fileNames = null,
        string? explicitDirectory = null,
        Func<string, string?>? environment = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _fileNames = fileNames is { Count: > 0 } ? fileNames : DefaultFileNames();
        _explicitDirectory = explicitDirectory;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _readsProcessEnvironment = environment is null;
    }

    /// <summary>A locator over the real machine, honouring <see cref="DirectoryOverride"/>.</summary>
    public static OrkeonBinaryLocator ForCurrentMachine() =>
        new(PhysicalExecutableProbe.Instance, explicitDirectory: DirectoryOverride);

    /// <summary>
    /// The executable names to try on the current platform.
    /// <para>
    /// Windows takes <c>orkeon.exe</c> and ONLY that. An extension-less file cannot be
    /// started there at all — CreateProcess needs a PE with a recognised extension — so the
    /// one thing a bare <c>orkeon</c> can be on a Windows machine is a foreign build: a
    /// checkout shared with WSL or a container has the Linux apphost sitting in the very
    /// <c>bin/</c> directory the development-tree lookup probes. Accepting it turned «the CLI
    /// is not installed», which Studio knows how to say and how to disable features for, into
    /// «The specified executable is not a valid application for this OS platform» thrown at
    /// the user from the middle of a launch.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> DefaultFileNames() =>
        OperatingSystem.IsWindows()
            ? [WindowsExecutableName]
            : [ExecutableBaseName];

    /// <summary>
    /// Runs the lookup. Never throws: a missing binary is a <see cref="BinaryLocation"/>
    /// carrying the message to show, because "the CLI is not installed" is an ordinary state
    /// of a freshly opened Studio, not an exceptional one.
    /// </summary>
    public BinaryLocation Locate()
    {
        var probed = new List<string>();
        _foreignBuild = null;

        // 1. The directory the operator named beats everything: an explicit choice must
        //    never lose to whatever happens to sit next to Studio.
        if (Unquote(_explicitDirectory) is { Length: > 0 } explicitDirectory
            && TryDirectoryOrLauncher(explicitDirectory, probed, out var explicitFound))
        {
            return Located(explicitFound, BinarySource.ExplicitDirectory, probed);
        }

        // 2. Next to the executable — the layout every package guarantees.
        foreach (var directory in InstallDirectories())
        {
            if (TryDirectory(directory, probed, out var found))
                return Located(found, BinarySource.InstallDirectory, probed);
        }

        // 3. The environment variable — machine-wide configuration without a flag.
        if (Unquote(_environment(DirectoryEnvironmentVariable)) is { Length: > 0 } environmentDirectory
            && TryDirectoryOrLauncher(environmentDirectory, probed, out var environmentFound))
        {
            return Located(environmentFound, BinarySource.EnvironmentVariable, probed);
        }

        // 4. PATH. An Orkeon install's bin\ there holds orkeon.cmd, never orkeon.exe: the
        //    executable it launches sits in ..\libexec\orkeon, probed at the same PATH rank.
        foreach (var directory in _probe.SearchPathDirectories)
        {
            if (TryDirectory(directory, probed, out var found))
                return Located(found, BinarySource.SearchPath, probed);

            if (InstallerLibexecBehindLauncher(directory) is { } libexec
                && TryDirectory(libexec, probed, out var installerFound))
            {
                return Located(installerFound, BinarySource.OrkeonInstaller, probed);
            }
        }

        // 5. The Orkeon Windows installers' default folder, for a PATH this process inherited
        //    before the install (the zip's install.ps1 does not broadcast its PATH change), or
        //    one the user trimmed. Then 6. the development checkout.
        if (DefaultInstallerDirectory() is { } installerDirectory
            && !probed.Contains(System.IO.Path.Combine(installerDirectory, WindowsExecutableName), StringComparer.OrdinalIgnoreCase)
            && TryDirectory(installerDirectory, probed, out var defaultFound))
        {
            return Located(defaultFound, BinarySource.OrkeonInstaller, probed);
        }

        foreach (var directory in DevelopmentTreeDirectories())
        {
            if (TryDirectory(directory, probed, out var found))
                return Located(found, BinarySource.DevelopmentTree, probed);
        }

        // Telling someone «not found» while a file called orkeon sits in the directory they
        // are looking at is the least useful true sentence available. Name what was seen.
        if (_foreignBuild is { } foreign)
        {
            return new BinaryLocation
            {
                Source = BinarySource.NotFound,
                ProbedPaths = probed,
                Error =
                    $"The `{ExecutableBaseName}` command-line tool was not located on this machine. " +
                    $"There is a file called `{ExecutableBaseName}` at {foreign}, but it carries no " +
                    $"executable extension, so this system cannot run it — it is a build for another " +
                    $"platform, which happens when the same checkout is built from WSL or a " +
                    $"container. Build the CLI for Windows " +
                    $"(dotnet build src\\scripting\\Orkeon.Scripting.Cli), or point --cli-dir or " +
                    $"{DirectoryEnvironmentVariable} at a directory holding {ExecutableBaseName}.exe. " +
                    $"Until then, everything that runs a crew stays unavailable.",
            };
        }

        var installerSteps = LooksForWindowsExecutable
            ? $"PATH (with the libexec\\orkeon folder behind an {WindowsLauncherName} found there), " +
              $"the Orkeon installer's folder ({DefaultInstallerDirectory() ?? DefaultInstallerDirectoryDisplay}), "
            : "PATH, ";

        return new BinaryLocation
        {
            Source = BinarySource.NotFound,
            ProbedPaths = probed,
            Error =
                $"The `{ExecutableBaseName}` command-line tool was not located on this machine. " +
                $"Studio runs crews by " +
                $"invoking it, so nothing can be launched until it is installed. Looked, in order: " +
                $"the --cli-dir argument, next to Studio ({_probe.BaseDirectory}), the " +
                $"{DirectoryEnvironmentVariable} environment variable, {installerSteps}and the development " +
                $"checkout. Reinstall the Orkeon package, point --cli-dir or " +
                $"{DirectoryEnvironmentVariable} at the CLI's directory, or in a checkout build it " +
                $"first: dotnet build src/scripting/Orkeon.Scripting.Cli.",
        };
    }

    /// <summary>The installer's default folder as the user would type it, shown when <c>LOCALAPPDATA</c> is unset.</summary>
    private const string DefaultInstallerDirectoryDisplay = @"%LOCALAPPDATA%\Programs\Orkeon\libexec\orkeon";

    /// <summary>
    /// True when the lookup accepts <c>orkeon.exe</c>, the Windows lookup. The installer steps
    /// key on the configured names, not on the running OS, like the foreign-build diagnosis:
    /// the suite exercises them anywhere.
    /// </summary>
    private bool LooksForWindowsExecutable =>
        _fileNames.Contains(WindowsExecutableName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>libexec\orkeon</c> folder of an Orkeon install whose <c>bin\</c> is
    /// <paramref name="directory"/>, when that directory is named <c>bin</c> and holds the
    /// install's <c>orkeon.cmd</c> launcher; null otherwise. The name is checked first, so
    /// the other PATH entries cost no extra probe. The launcher only marks the layout and is
    /// not a candidate, so it stays out of the probed paths.
    /// </summary>
    private string? InstallerLibexecBehindLauncher(string directory)
    {
        if (!LooksForWindowsExecutable || string.IsNullOrWhiteSpace(directory))
            return null;

        // TrimEndingDirectorySeparator keeps a root whole, where TrimEnd would turn C:\ into
        // the drive-relative C: and probe against the current directory.
        var trimmed = System.IO.Path.TrimEndingDirectorySeparator(directory);
        if (!string.Equals(System.IO.Path.GetFileName(trimmed), "bin", StringComparison.OrdinalIgnoreCase)
            || System.IO.Path.GetDirectoryName(trimmed) is not { Length: > 0 } root
            || !_probe.FileExists(System.IO.Path.Combine(trimmed, WindowsLauncherName)))
        {
            return null;
        }

        return System.IO.Path.Combine(root, "libexec", ExecutableBaseName);
    }

    /// <summary>
    /// <see cref="TryDirectory"/>, then, for a directory the user named, the install behind
    /// the <c>bin\orkeon.cmd</c> it may hold: pointing <c>--cli-dir</c> or <c>ORKEON_CLI_DIR</c>
    /// at the folder where <c>orkeon</c> seems to live must not lose to a CLI on <c>PATH</c>.
    /// </summary>
    private bool TryDirectoryOrLauncher(string directory, List<string> probed, out string? found)
    {
        if (TryDirectory(directory, probed, out found))
            return true;

        if (InstallerLibexecBehindLauncher(directory) is { } libexec)
            return TryDirectory(libexec, probed, out found);

        return false;
    }

    /// <summary>
    /// A path read from a variable or an argument, without the quotes a shell would have
    /// stripped; empty for null, blank or <c>""</c>, so a caller never probes the current directory.
    /// </summary>
    private static string Unquote(string? value) => value is null ? "" : value.Trim().Trim('"').Trim();

    /// <summary>A hit, with the variables the launch of that binary needs.</summary>
    private BinaryLocation Located(string? path, BinarySource source, List<string> probed) => new()
    {
        Path = path,
        Source = source,
        ProbedPaths = probed,
        LaunchEnvironment = path is null ? new Dictionary<string, string>(StringComparer.Ordinal) : LaunchEnvironmentFor(path),
    };

    /// <summary>
    /// What the Orkeon launcher sets before it starts <c>libexec\orkeon\orkeon</c>: the esbuild
    /// its install ships in <c>libexec\esbuild-bin</c>, unless <c>ORKEON_ESBUILD_PATH</c> is
    /// already set (the launcher's «if not defined»). The CLI itself only looks for a bundled
    /// esbuild under its own folder, so without this a <c>.ork.ts</c> crew finds no compiler.
    /// </summary>
    private Dictionary<string, string> LaunchEnvironmentFor(string binary)
    {
        var none = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(_environment(EnvironmentVariableNames.EsbuildPath)))
            return none;

        var appDirectory = System.IO.Path.GetDirectoryName(binary);
        var libexec = appDirectory is null ? null : System.IO.Path.GetDirectoryName(appDirectory);
        if (appDirectory is null || libexec is null
            || !string.Equals(System.IO.Path.GetFileName(appDirectory), ExecutableBaseName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(System.IO.Path.GetFileName(libexec), "libexec", StringComparison.OrdinalIgnoreCase))
        {
            return none;
        }

        var esbuildName = binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "esbuild.exe" : "esbuild";
        var esbuild = System.IO.Path.Combine(libexec, "esbuild-bin", esbuildName);
        if (!_probe.FileExists(esbuild))
            return none;

        none[EnvironmentVariableNames.EsbuildPath] = esbuild;
        return none;
    }

    /// <summary>
    /// The Orkeon Windows installer's default CLI folder,
    /// <c>%LOCALAPPDATA%\Programs\Orkeon\libexec\orkeon</c> (its per-user MSI installs under
    /// <c>LocalAppDataFolder</c>); null outside the Windows lookup or when the variable is unset.
    /// </summary>
    private string? DefaultInstallerDirectory()
    {
        if (!LooksForWindowsExecutable)
            return null;

        // The MSI resolves LocalAppDataFolder from the known folder; the variable normally
        // matches it, and the known folder covers a parent process that dropped the variable.
        var localAppData = Unquote(_environment(LocalAppDataEnvironmentVariable)) is { Length: > 0 } variable
            ? variable
            : _readsProcessEnvironment
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : null;

        return string.IsNullOrWhiteSpace(localAppData)
            ? null
            : System.IO.Path.Combine(localAppData, "Programs", "Orkeon", "libexec", ExecutableBaseName);
    }

    /// <summary>
    /// The directories that may hold the co-installed binary, in order: next to Studio, then
    /// the <c>bin/</c> sibling and parent layouts the archives and the <c>.deb</c> produce.
    /// </summary>
    private IEnumerable<string> InstallDirectories()
    {
        var baseDirectory = _probe.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
            yield break;

        var trimmed = baseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var parent = System.IO.Path.GetDirectoryName(trimmed) ?? trimmed;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // The libexec/orkeon sibling is the layout of a Studio installed inside the CLI's tree
        // (scripts/install-from-source.*): Studio in libexec/<studio-app>/, the CLI in
        // libexec/orkeon/. On Windows bin\ there holds .cmd wrappers, never the orkeon.exe the
        // lookup accepts, so this sibling is what reaches it from Studio's own folder.
        foreach (var candidate in new[]
                 {
                     trimmed,
                     System.IO.Path.Combine(trimmed, "bin"),
                     parent,
                     System.IO.Path.Combine(parent, "bin"),
                     System.IO.Path.Combine(parent, ExecutableBaseName),
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
                yield return candidate;
        }
    }

    /// <summary>
    /// The dev-checkout fallback: when an Orkeon source checkout is at hand, the CLI built there
    /// sits in <c>src/scripting/Orkeon.Scripting.Cli/bin/&lt;Configuration&gt;/&lt;tfm&gt;/</c>.
    /// The checkout is the one <c>ORKEON_SOURCE_ROOT</c> names (the variable the drift tests
    /// read too), else the sibling <c>../orkeon</c> of Studio's own repository, found by walking
    /// up from Studio's base path to <c>Orkeon.Studio.sln</c>. Studio's base path names the
    /// configuration (the segment after <c>bin</c>) and the TFM (its own, minus the
    /// <c>-windows</c> suffix); the sibling configuration is probed second so a Debug Studio
    /// can still find a Release CLI and vice versa.
    /// </summary>
    private IEnumerable<string> DevelopmentTreeDirectories()
    {
        var baseDirectory = _probe.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
            yield break;

        var segments = baseDirectory
            .Split(DirectorySeparators)
            .Where(s => s.Length > 0)
            .ToArray();

        var binIndex = Array.FindLastIndex(segments, s => string.Equals(s, "bin", StringComparison.OrdinalIgnoreCase));
        var configuration = binIndex >= 0 && binIndex + 1 < segments.Length ? segments[binIndex + 1] : "Debug";
        var framework = segments.Length > 0 ? segments[^1] : "";
        var suffix = framework.IndexOf('-', StringComparison.Ordinal);
        if (suffix > 0)
            framework = framework[..suffix];
        if (framework.Length == 0 || !framework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            yield break;

        var configurations = new List<string> { configuration };
        foreach (var fallback in new[] { "Release", "Debug" })
        {
            if (!configurations.Contains(fallback, StringComparer.OrdinalIgnoreCase))
                configurations.Add(fallback);
        }

        foreach (var root in OrkeonSourceRoots(baseDirectory))
        {
            var cliBin = System.IO.Path.Combine(root, "src", "scripting", "Orkeon.Scripting.Cli", "bin");
            foreach (var candidate in configurations)
                yield return System.IO.Path.Combine(cliBin, candidate, framework);
        }
    }

    /// <summary>The Orkeon checkouts to look into: <c>ORKEON_SOURCE_ROOT</c> first, then the sibling <c>../orkeon</c>.</summary>
    private IEnumerable<string> OrkeonSourceRoots(string baseDirectory)
    {
        if (_environment(SourceRootEnvironmentVariable) is { Length: > 0 } configured
            && !string.IsNullOrWhiteSpace(configured))
        {
            yield return configured.Trim();
        }

        var studioRoot = FindStudioRoot(baseDirectory);
        if (studioRoot is not null
            && System.IO.Path.GetDirectoryName(studioRoot) is { Length: > 0 } parent)
        {
            yield return System.IO.Path.Combine(parent, "orkeon");
        }
    }

    private string? FindStudioRoot(string startDirectory)
    {
        var directory = startDirectory.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        for (var depth = 0; depth < 10 && !string.IsNullOrEmpty(directory); depth++)
        {
            if (_probe.FileExists(System.IO.Path.Combine(directory, "Orkeon.Studio.sln")))
                return directory;

            directory = System.IO.Path.GetDirectoryName(directory);
        }

        return null;
    }

    private bool TryDirectory(string directory, List<string> probed, out string? found)
    {
        foreach (var fileName in _fileNames)
        {
            var candidate = System.IO.Path.Combine(directory, fileName);
            probed.Add(candidate);
            if (_probe.FileExists(candidate))
            {
                found = candidate;
                return true;
            }
        }

        NoteForeignBuild(directory);
        found = null;
        return false;
    }

    /// <summary>
    /// Records a Linux/macOS apphost sitting where a Windows one was expected. The question is
    /// not «are we on Windows» but «is the bare name one we accept»: where it is, a hit would
    /// have returned above and we never arrive here holding a runnable file. That keeps the
    /// diagnosis a property of the configured names, so the suite can exercise it anywhere.
    /// </summary>
    private void NoteForeignBuild(string directory)
    {
        if (_foreignBuild is not null || _fileNames.Contains(ExecutableBaseName, StringComparer.OrdinalIgnoreCase))
            return;

        var bare = System.IO.Path.Combine(directory, ExecutableBaseName);
        if (_probe.FileExists(bare))
            _foreignBuild = bare;
    }
}
