using Orkeon.Studio.Core.Process;
using Orkeon.Studio.Core.Tests.Doubles;

namespace Orkeon.Studio.Core.Tests.Process;

/// <summary>
/// Resolution of the co-installed <c>orkeon</c> binary. Every case runs against
/// <see cref="FakeExecutableProbe"/>: the suite must pass on a machine where the CLI is
/// installed and on one where it is not.
/// </summary>
public sealed class OrkeonBinaryLocatorTests
{
    private static readonly string[] UnixNames = ["orkeon"];

    /// <summary>What <see cref="OrkeonBinaryLocator.DefaultFileNames"/> yields on Windows.</summary>
    private static readonly string[] WindowsNames = ["orkeon.exe"];

    [Fact]
    public void The_binary_next_to_studio_is_found_first()
    {
        var installDir = Path.Combine("/", "opt", "orkeon");
        var probe = new FakeExecutableProbe { BaseDirectory = installDir }
            .WithFile(Path.Combine(installDir, "orkeon"))
            .WithPathDirectories(Path.Combine("/", "usr", "bin"))
            .WithFile(Path.Combine("/", "usr", "bin", "orkeon"));

        var location = new OrkeonBinaryLocator(probe, UnixNames).Locate();

        Assert.True(location.Found);
        Assert.Equal(Path.Combine(installDir, "orkeon"), location.Path);
        Assert.Equal(BinarySource.InstallDirectory, location.Source);
        Assert.Null(location.Error);
    }

    [Fact]
    public void The_bin_sibling_of_the_install_directory_is_probed()
    {
        // Archive layout: the apps sit in libexec/<app>/ and every launcher in bin/.
        var root = Path.Combine("/", "opt", "orkeon");
        var probe = new FakeExecutableProbe { BaseDirectory = Path.Combine(root, "libexec") }
            .WithFile(Path.Combine(root, "bin", "orkeon"));

        var location = new OrkeonBinaryLocator(probe, UnixNames).Locate();

        Assert.True(location.Found);
        Assert.Equal(Path.Combine(root, "bin", "orkeon"), location.Path);
        Assert.Equal(BinarySource.InstallDirectory, location.Source);
    }

    [Fact]
    public void The_cli_sibling_under_libexec_is_found_from_the_packaged_studio()
    {
        // Windows zip/MSI layout: Studio in libexec\orkeon-studio\, the CLI in
        // libexec\orkeon\orkeon.exe, and bin\ holds only .cmd wrappers — which the
        // Windows lookup (orkeon.exe only) can never accept from PATH.
        var root = Path.Combine("C:", "Users", "me", "AppData", "Local", "Programs", "Orkeon");
        var cli = Path.Combine(root, "libexec", "orkeon", "orkeon.exe");
        var probe = new FakeExecutableProbe { BaseDirectory = Path.Combine(root, "libexec", "orkeon-studio") }
            .WithPathDirectories(Path.Combine(root, "bin"))
            .WithFile(Path.Combine(root, "bin", "orkeon.cmd"))
            .WithFile(cli);

        var location = new OrkeonBinaryLocator(probe, WindowsNames).Locate();

        Assert.True(location.Found);
        Assert.Equal(cli, location.Path);
        Assert.Equal(BinarySource.InstallDirectory, location.Source);
    }

    [Fact]
    public void The_search_path_is_the_fallback()
    {
        var probe = new FakeExecutableProbe { BaseDirectory = Path.Combine("/", "opt", "studio") }
            .WithPathDirectories(Path.Combine("/", "nowhere"), Path.Combine("/", "usr", "local", "bin"))
            .WithFile(Path.Combine("/", "usr", "local", "bin", "orkeon"));

        var location = new OrkeonBinaryLocator(probe, UnixNames).Locate();

        Assert.True(location.Found);
        Assert.Equal(Path.Combine("/", "usr", "local", "bin", "orkeon"), location.Path);
        Assert.Equal(BinarySource.SearchPath, location.Source);
    }

    [Fact]
    public void A_missing_binary_is_an_actionable_result_not_an_exception()
    {
        var probe = new FakeExecutableProbe { BaseDirectory = Path.Combine("/", "opt", "studio") }
            .WithPathDirectories(Path.Combine("/", "usr", "bin"));

        var location = new OrkeonBinaryLocator(probe, UnixNames).Locate();

        Assert.False(location.Found);
        Assert.Null(location.Path);
        Assert.Equal(BinarySource.NotFound, location.Source);
        Assert.NotNull(location.Error);

        // The message must name the missing tool, say what breaks, and say what to do. «Not
        // located on this machine» over «not found»: the reader is being told the search ended,
        // not that some particular path was empty.
        Assert.Contains("orkeon", location.Error, StringComparison.Ordinal);
        Assert.Contains("was not located on this machine", location.Error, StringComparison.Ordinal);
        Assert.Contains("reinstall", location.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PATH", location.Error, StringComparison.Ordinal);
        Assert.Contains(probe.BaseDirectory, location.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_probed_path_is_reported_so_the_ui_can_show_where_it_looked()
    {
        var probe = new FakeExecutableProbe { BaseDirectory = Path.Combine("/", "opt", "studio") }
            .WithPathDirectories(Path.Combine("/", "usr", "bin"));

        var location = new OrkeonBinaryLocator(probe, UnixNames).Locate();

        Assert.Contains(Path.Combine("/", "opt", "studio", "orkeon"), location.ProbedPaths);
        Assert.Contains(Path.Combine("/", "usr", "bin", "orkeon"), location.ProbedPaths);
        Assert.Equal(probe.ProbedPaths, location.ProbedPaths);
    }

    [Fact]
    public void The_windows_executable_name_is_tried_before_the_bare_one()
    {
        var installDir = Path.Combine("C:", "Program Files", "Orkeon");
        var probe = new FakeExecutableProbe { BaseDirectory = installDir }
            .WithFile(Path.Combine(installDir, "orkeon.exe"));

        var location = new OrkeonBinaryLocator(probe, ["orkeon.exe", "orkeon"]).Locate();

        Assert.True(location.Found);
        Assert.Equal(Path.Combine(installDir, "orkeon.exe"), location.Path);
    }

    [Fact]
    public void The_default_executable_names_match_the_platform()
    {
        var names = OrkeonBinaryLocator.DefaultFileNames();

        if (OperatingSystem.IsWindows())
            Assert.Equal(["orkeon.exe"], names);
        else
            Assert.Equal(["orkeon"], names);
    }

    /// <summary>
    /// A checkout shared between Windows and WSL leaves the Linux apphost — plain <c>orkeon</c>,
    /// no extension — in the very <c>bin/</c> directory the development-tree lookup walks. Windows
    /// cannot start that file, so claiming it was found turned «the CLI is not installed», a state
    /// Studio knows how to present and how to disable its launch features for, into a Win32
    /// «The specified executable is not a valid application for this OS platform» thrown at the
    /// user mid-launch. It must read as not found.
    /// </summary>
    [Fact]
    public void A_build_for_another_platform_does_not_count_as_finding_the_binary()
    {
        var installDir = Path.Combine("C:", "Program Files", "Orkeon");
        var probe = new FakeExecutableProbe { BaseDirectory = installDir }
            .WithFile(Path.Combine(installDir, "orkeon"));

        var location = new OrkeonBinaryLocator(probe, WindowsNames).Locate();

        Assert.False(location.Found);
        Assert.Equal(BinarySource.NotFound, location.Source);
        Assert.Null(location.Path);
    }

    [Fact]
    public void The_failure_names_the_build_for_another_platform_it_walked_past()
    {
        var installDir = Path.Combine("C:", "Program Files", "Orkeon");
        var probe = new FakeExecutableProbe { BaseDirectory = installDir }
            .WithFile(Path.Combine(installDir, "orkeon"));

        var error = new OrkeonBinaryLocator(probe, WindowsNames).Locate().Error;

        // Telling someone «not found» while a file called orkeon sits in the folder they are
        // staring at is the least useful true sentence available.
        Assert.NotNull(error);
        Assert.Contains("was not located on this machine", error, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(installDir, "orkeon"), error, StringComparison.Ordinal);
        Assert.Contains("another", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_windows_executable_still_wins_over_a_neighbouring_foreign_build()
    {
        var installDir = Path.Combine("C:", "Program Files", "Orkeon");
        var probe = new FakeExecutableProbe { BaseDirectory = installDir }
            .WithFile(Path.Combine(installDir, "orkeon"))
            .WithFile(Path.Combine(installDir, "orkeon.exe"));

        var location = new OrkeonBinaryLocator(probe, WindowsNames).Locate();

        Assert.True(location.Found);
        Assert.Equal(Path.Combine(installDir, "orkeon.exe"), location.Path);
        Assert.Null(location.Error);
    }

    /// <summary>The unix lookup accepts the bare name, so it must not diagnose one as foreign.</summary>
    [Fact]
    public void The_unix_lookup_is_not_confused_by_the_extension_less_name()
    {
        var installDir = Path.Combine("/", "opt", "orkeon");
        var probe = new FakeExecutableProbe { BaseDirectory = installDir }
            .WithFile(Path.Combine(installDir, "orkeon"));

        var location = new OrkeonBinaryLocator(probe, UnixNames).Locate();

        Assert.True(location.Found);
        Assert.Null(location.Error);
    }

    [Fact]
    public void An_empty_base_directory_falls_through_to_the_search_path()
    {
        var probe = new FakeExecutableProbe { BaseDirectory = "" }
            .WithPathDirectories(Path.Combine("/", "usr", "bin"))
            .WithFile(Path.Combine("/", "usr", "bin", "orkeon"));

        var location = new OrkeonBinaryLocator(probe, UnixNames).Locate();

        Assert.True(location.Found);
        Assert.Equal(BinarySource.SearchPath, location.Source);
    }
    [Fact]
    public void A_sibling_orkeon_checkout_provides_the_cli()
    {
        // Studio launched from the IDE: its base dir is deep inside its own repo's bin tree,
        // nothing next to it, nothing on PATH, but ../orkeon holds a built CLI.
        var studio = Path.Combine("/", "home", "me", "orkeon-studio");
        var orkeon = Path.Combine("/", "home", "me", "orkeon");
        var studioBin = Path.Combine(studio, "src", "apps", "Orkeon.Studio.Wpf", "bin", "Debug", "net10.0-windows");
        var cli = Path.Combine(orkeon, "src", "scripting", "Orkeon.Scripting.Cli", "bin", "Debug", "net10.0", "orkeon");

        var probe = new FakeExecutableProbe { BaseDirectory = studioBin }
            .WithFile(Path.Combine(studio, "Orkeon.Studio.sln"))
            .WithFile(cli);

        var location = new OrkeonBinaryLocator(probe, UnixNames, environment: _ => null).Locate();

        Assert.True(location.Found);
        Assert.Equal(cli, location.Path);
        Assert.Equal(BinarySource.DevelopmentTree, location.Source);
    }

    [Fact]
    public void The_source_root_variable_beats_the_sibling_checkout()
    {
        var studio = Path.Combine("/", "home", "me", "orkeon-studio");
        var named = Path.Combine("/", "srv", "orkeon-src");
        var sibling = Path.Combine("/", "home", "me", "orkeon");
        var studioBin = Path.Combine(studio, "bin", "Debug", "net10.0");
        var namedCli = Path.Combine(named, "src", "scripting", "Orkeon.Scripting.Cli", "bin", "Debug", "net10.0", "orkeon");
        var siblingCli = Path.Combine(sibling, "src", "scripting", "Orkeon.Scripting.Cli", "bin", "Debug", "net10.0", "orkeon");

        var probe = new FakeExecutableProbe { BaseDirectory = studioBin }
            .WithFile(Path.Combine(studio, "Orkeon.Studio.sln"))
            .WithFile(namedCli)
            .WithFile(siblingCli);

        var location = new OrkeonBinaryLocator(
            probe, UnixNames, environment: name => name == "ORKEON_SOURCE_ROOT" ? named : null).Locate();

        Assert.Equal(namedCli, location.Path);
        Assert.Equal(BinarySource.DevelopmentTree, location.Source);
    }

    [Fact]
    public void The_source_root_variable_works_outside_a_studio_checkout()
    {
        var named = Path.Combine("/", "srv", "orkeon-src");
        var cli = Path.Combine(named, "src", "scripting", "Orkeon.Scripting.Cli", "bin", "Release", "net10.0", "orkeon");
        var studioBin = Path.Combine("/", "tmp", "x", "bin", "Release", "net10.0");

        var probe = new FakeExecutableProbe { BaseDirectory = studioBin }.WithFile(cli);

        var location = new OrkeonBinaryLocator(
            probe, UnixNames, environment: name => name == "ORKEON_SOURCE_ROOT" ? named : null).Locate();

        Assert.Equal(cli, location.Path);
        Assert.Equal(BinarySource.DevelopmentTree, location.Source);
    }

    [Fact]
    public void A_debug_studio_falls_back_to_a_release_cli_and_never_escapes_the_checkout()
    {
        var studio = Path.Combine("/", "home", "me", "orkeon-studio");
        var orkeon = Path.Combine("/", "home", "me", "orkeon");
        var studioBin = Path.Combine(studio, "src", "apps", "Orkeon.Studio.Wpf", "bin", "Debug", "net10.0-windows");
        var releaseCli = Path.Combine(orkeon, "src", "scripting", "Orkeon.Scripting.Cli", "bin", "Release", "net10.0", "orkeon");

        var probe = new FakeExecutableProbe { BaseDirectory = studioBin }
            .WithFile(Path.Combine(studio, "Orkeon.Studio.sln"))
            .WithFile(releaseCli);

        var location = new OrkeonBinaryLocator(probe, UnixNames, environment: _ => null).Locate();

        Assert.Equal(releaseCli, location.Path);
        Assert.Equal(BinarySource.DevelopmentTree, location.Source);

        // Outside a Studio checkout (no Orkeon.Studio.sln above) and without the variable,
        // the fallback stays silent.
        var installed = new FakeExecutableProbe { BaseDirectory = studioBin }.WithFile(releaseCli);
        Assert.False(new OrkeonBinaryLocator(installed, UnixNames, environment: _ => null).Locate().Found);
    }

    [Fact]
    public void The_operators_directory_beats_the_binary_next_to_studio()
    {
        var installDir = Path.Combine("/", "opt", "orkeon");
        var chosenDir = Path.Combine("/", "srv", "cli");
        var probe = new FakeExecutableProbe { BaseDirectory = installDir }
            .WithFile(Path.Combine(installDir, "orkeon"))
            .WithFile(Path.Combine(chosenDir, "orkeon"));

        var location = new OrkeonBinaryLocator(probe, UnixNames, explicitDirectory: chosenDir).Locate();

        Assert.Equal(Path.Combine(chosenDir, "orkeon"), location.Path);
        Assert.Equal(BinarySource.ExplicitDirectory, location.Source);
    }

    [Fact]
    public void The_environment_variable_is_read_after_the_install_directory_and_before_path()
    {
        var envDir = Path.Combine("/", "srv", "env-cli");
        var pathDir = Path.Combine("/", "usr", "bin");
        var probe = new FakeExecutableProbe { BaseDirectory = Path.Combine("/", "opt", "orkeon") }
            .WithPathDirectories(pathDir)
            .WithFile(Path.Combine(envDir, "orkeon"))
            .WithFile(Path.Combine(pathDir, "orkeon"));

        var location = new OrkeonBinaryLocator(
            probe, UnixNames,
            environment: name => name == OrkeonBinaryLocator.DirectoryEnvironmentVariable ? envDir : null).Locate();

        Assert.Equal(Path.Combine(envDir, "orkeon"), location.Path);
        Assert.Equal(BinarySource.EnvironmentVariable, location.Source);

        // But the install directory still wins over the variable.
        var installed = new FakeExecutableProbe { BaseDirectory = Path.Combine("/", "opt", "orkeon") }
            .WithFile(Path.Combine("/", "opt", "orkeon", "orkeon"))
            .WithFile(Path.Combine(envDir, "orkeon"));
        var installedLocation = new OrkeonBinaryLocator(
            installed, UnixNames,
            environment: _ => envDir).Locate();
        Assert.Equal(BinarySource.InstallDirectory, installedLocation.Source);
    }

    [Fact]
    public void The_not_found_message_names_every_lookup_in_order()
    {
        var location = new OrkeonBinaryLocator(
            new FakeExecutableProbe { BaseDirectory = "/opt/orkeon" }, UnixNames, environment: _ => null).Locate();

        Assert.Contains("--cli-dir", location.Error, StringComparison.Ordinal);
        Assert.Contains(OrkeonBinaryLocator.DirectoryEnvironmentVariable, location.Error, StringComparison.Ordinal);
        Assert.Contains("PATH", location.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Orkeon Windows installer lays out <c>%LOCALAPPDATA%\Programs\Orkeon</c>, puts its
    /// <c>bin\</c> (only <c>.cmd</c> launchers) on PATH and the real executable in
    /// <c>libexec\orkeon</c>; Studio's own MSI installs to <c>Programs\Orkeon Studio</c>, out of
    /// reach of the sibling lookups.
    /// </summary>
    private static readonly string LocalAppData = Path.Combine("C:", "Users", "me", "AppData", "Local");

    private static readonly string InstallerRoot = Path.Combine(LocalAppData, "Programs", "Orkeon");

    private static readonly string InstallerCli = Path.Combine(InstallerRoot, "libexec", "orkeon", "orkeon.exe");

    private static FakeExecutableProbe StudioFromItsOwnMsi() =>
        new() { BaseDirectory = Path.Combine(LocalAppData, "Programs", "Orkeon Studio", "libexec", "orkeon-studio") };

    [Fact]
    public void An_orkeon_launcher_on_path_leads_to_the_installers_executable()
    {
        var probe = StudioFromItsOwnMsi()
            .WithPathDirectories(Path.Combine("C:", "Windows"), Path.Combine(InstallerRoot, "bin"))
            .WithFile(Path.Combine(InstallerRoot, "bin", "orkeon.cmd"))
            .WithFile(InstallerCli);

        var location = new OrkeonBinaryLocator(probe, WindowsNames, environment: _ => null).Locate();

        Assert.True(location.Found);
        Assert.Equal(InstallerCli, location.Path);
        Assert.Equal(BinarySource.OrkeonInstaller, location.Source);
        Assert.Null(location.Error);

        // The launcher is never what Studio starts, and never reported as a candidate.
        Assert.DoesNotContain(Path.Combine(InstallerRoot, "bin", "orkeon.cmd"), location.ProbedPaths);
        Assert.Contains(Path.Combine(InstallerRoot, "bin", "orkeon.exe"), location.ProbedPaths);
    }

    [Fact]
    public void The_launcher_keeps_its_rank_on_path()
    {
        var toolDir = Path.Combine("C:", "Users", "me", ".dotnet", "tools");
        var toolCli = Path.Combine(toolDir, "orkeon.exe");

        // The .NET tool listed first wins...
        var toolFirst = StudioFromItsOwnMsi()
            .WithPathDirectories(toolDir, Path.Combine(InstallerRoot, "bin"))
            .WithFile(toolCli)
            .WithFile(Path.Combine(InstallerRoot, "bin", "orkeon.cmd"))
            .WithFile(InstallerCli);
        var first = new OrkeonBinaryLocator(toolFirst, WindowsNames, environment: _ => null).Locate();
        Assert.Equal(toolCli, first.Path);
        Assert.Equal(BinarySource.SearchPath, first.Source);

        // ...and loses to the installer listed before it, as `orkeon` typed in a shell would.
        var installerFirst = StudioFromItsOwnMsi()
            .WithPathDirectories(Path.Combine(InstallerRoot, "bin"), toolDir)
            .WithFile(toolCli)
            .WithFile(Path.Combine(InstallerRoot, "bin", "orkeon.cmd"))
            .WithFile(InstallerCli);
        var second = new OrkeonBinaryLocator(installerFirst, WindowsNames, environment: _ => null).Locate();
        Assert.Equal(InstallerCli, second.Path);
        Assert.Equal(BinarySource.OrkeonInstaller, second.Source);
    }

    [Fact]
    public void The_installers_default_folder_is_found_without_path()
    {
        // A PATH inherited before the installer ran, or trimmed by the user.
        var probe = StudioFromItsOwnMsi()
            .WithPathDirectories(Path.Combine("C:", "Windows"))
            .WithFile(InstallerCli);

        var location = new OrkeonBinaryLocator(
            probe, WindowsNames,
            environment: name => name == OrkeonBinaryLocator.LocalAppDataEnvironmentVariable ? LocalAppData : null).Locate();

        Assert.True(location.Found);
        Assert.Equal(InstallerCli, location.Path);
        Assert.Equal(BinarySource.OrkeonInstaller, location.Source);
    }

    [Fact]
    public void The_installers_default_folder_comes_after_the_environment_variable_and_path()
    {
        var envDir = Path.Combine("D:", "tools", "orkeon");
        var pathDir = Path.Combine("C:", "Users", "me", ".dotnet", "tools");
        var probe = StudioFromItsOwnMsi()
            .WithPathDirectories(pathDir)
            .WithFile(Path.Combine(envDir, "orkeon.exe"))
            .WithFile(Path.Combine(pathDir, "orkeon.exe"))
            .WithFile(InstallerCli);

        var fromVariable = new OrkeonBinaryLocator(
            probe, WindowsNames,
            environment: name => name switch
            {
                OrkeonBinaryLocator.DirectoryEnvironmentVariable => envDir,
                OrkeonBinaryLocator.LocalAppDataEnvironmentVariable => LocalAppData,
                _ => null,
            }).Locate();
        Assert.Equal(BinarySource.EnvironmentVariable, fromVariable.Source);

        var fromPath = new OrkeonBinaryLocator(
            probe, WindowsNames,
            environment: name => name == OrkeonBinaryLocator.LocalAppDataEnvironmentVariable ? LocalAppData : null).Locate();
        Assert.Equal(Path.Combine(pathDir, "orkeon.exe"), fromPath.Path);
        Assert.Equal(BinarySource.SearchPath, fromPath.Source);
    }

    [Fact]
    public void The_installers_folder_is_probed_once_when_path_already_led_there()
    {
        var probe = StudioFromItsOwnMsi()
            .WithPathDirectories(Path.Combine(InstallerRoot, "bin"))
            .WithFile(Path.Combine(InstallerRoot, "bin", "orkeon.cmd"));

        var location = new OrkeonBinaryLocator(
            probe, WindowsNames,
            environment: name => name == OrkeonBinaryLocator.LocalAppDataEnvironmentVariable ? LocalAppData : null).Locate();

        Assert.False(location.Found);
        Assert.Single(location.ProbedPaths, p => p == InstallerCli);
    }

    [Fact]
    public void The_installer_steps_belong_to_the_windows_lookup_only()
    {
        var root = Path.Combine("/", "opt", "orkeon");
        var probe = new FakeExecutableProbe { BaseDirectory = Path.Combine("/", "opt", "studio") }
            .WithPathDirectories(Path.Combine(root, "bin"))
            .WithFile(Path.Combine(root, "bin", "orkeon.cmd"))
            .WithFile(Path.Combine(root, "libexec", "orkeon", "orkeon"))
            .WithFile(Path.Combine(LocalAppData, "Programs", "Orkeon", "libexec", "orkeon", "orkeon"));

        var location = new OrkeonBinaryLocator(probe, UnixNames, environment: _ => LocalAppData).Locate();

        Assert.False(location.Found);
        Assert.Equal(probe.ProbedPaths, location.ProbedPaths);
        Assert.DoesNotContain("installer", location.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_windows_not_found_message_names_the_installers_folder()
    {
        var withVariable = new OrkeonBinaryLocator(
            StudioFromItsOwnMsi(), WindowsNames,
            environment: name => name == OrkeonBinaryLocator.LocalAppDataEnvironmentVariable ? LocalAppData : null).Locate();
        Assert.Contains(Path.GetDirectoryName(InstallerCli)!, withVariable.Error, StringComparison.Ordinal);
        Assert.Contains("orkeon.cmd", withVariable.Error, StringComparison.Ordinal);
        Assert.Contains(InstallerCli, withVariable.ProbedPaths);

        var withoutVariable = new OrkeonBinaryLocator(StudioFromItsOwnMsi(), WindowsNames, environment: _ => null).Locate();
        Assert.Contains(@"%LOCALAPPDATA%\Programs\Orkeon\libexec\orkeon", withoutVariable.Error, StringComparison.Ordinal);
    }

    private static readonly string InstallerEsbuild = Path.Combine(InstallerRoot, "libexec", "esbuild-bin", "esbuild.exe");

    private static Func<string, string?> LocalAppDataOnly =>
        name => name == OrkeonBinaryLocator.LocalAppDataEnvironmentVariable ? LocalAppData : null;

    /// <summary>
    /// bin\orkeon.cmd alone points the CLI at the esbuild the install ships in
    /// libexec\esbuild-bin; the CLI only looks under its own folder. Running orkeon.exe
    /// directly must carry that variable, or a .ork.ts crew finds no compiler.
    /// </summary>
    [Fact]
    public void An_installer_hit_carries_the_esbuild_the_launcher_would_have_named()
    {
        var probe = StudioFromItsOwnMsi()
            .WithPathDirectories(Path.Combine(InstallerRoot, "bin"))
            .WithFile(Path.Combine(InstallerRoot, "bin", "orkeon.cmd"))
            .WithFile(InstallerCli)
            .WithFile(InstallerEsbuild);

        var location = new OrkeonBinaryLocator(probe, WindowsNames, environment: _ => null).Locate();

        Assert.Equal(InstallerCli, location.Path);
        var variable = Assert.Single(location.LaunchEnvironment);
        Assert.Equal("ORKEON_ESBUILD_PATH", variable.Key);
        Assert.Equal(InstallerEsbuild, variable.Value);
    }

    [Fact]
    public void The_esbuild_variable_is_left_alone_when_set_or_when_nothing_ships_it()
    {
        // Already set: the launcher's «if not defined» keeps the user's choice.
        var probe = StudioFromItsOwnMsi().WithFile(InstallerCli).WithFile(InstallerEsbuild);
        var preset = new OrkeonBinaryLocator(
            probe, WindowsNames,
            environment: name => name switch
            {
                OrkeonBinaryLocator.LocalAppDataEnvironmentVariable => LocalAppData,
                "ORKEON_ESBUILD_PATH" => Path.Combine("D:", "esbuild.exe"),
                _ => null,
            }).Locate();
        Assert.Equal(InstallerCli, preset.Path);
        Assert.Empty(preset.LaunchEnvironment);

        // No esbuild-bin beside it: nothing to point at.
        var bare = new OrkeonBinaryLocator(StudioFromItsOwnMsi().WithFile(InstallerCli), WindowsNames, environment: LocalAppDataOnly).Locate();
        Assert.Equal(InstallerCli, bare.Path);
        Assert.Empty(bare.LaunchEnvironment);

        // A CLI outside a libexec\orkeon layout (the .NET tool) needs nothing either.
        var toolDir = Path.Combine("C:", "Users", "me", ".dotnet", "tools");
        var tool = new OrkeonBinaryLocator(
            StudioFromItsOwnMsi().WithPathDirectories(toolDir).WithFile(Path.Combine(toolDir, "orkeon.exe"))
                .WithFile(Path.Combine("C:", "Users", "me", ".dotnet", "esbuild-bin", "esbuild.exe")),
            WindowsNames, environment: _ => null).Locate();
        Assert.Equal(BinarySource.SearchPath, tool.Source);
        Assert.Empty(tool.LaunchEnvironment);
    }

    [Fact]
    public void The_callers_variables_win_over_the_launch_environment()
    {
        var location = new BinaryLocation
        {
            Path = InstallerCli,
            LaunchEnvironment = new Dictionary<string, string> { ["ORKEON_ESBUILD_PATH"] = InstallerEsbuild },
        };

        var merged = location.EnvironmentWith(new Dictionary<string, string>
        {
            ["ORKEON_ESBUILD_PATH"] = "mine",
            ["OTHER"] = "kept",
        });

        Assert.Equal("mine", merged["ORKEON_ESBUILD_PATH"]);
        Assert.Equal("kept", merged["OTHER"]);
        Assert.Equal(InstallerEsbuild, location.EnvironmentWith(null)["ORKEON_ESBUILD_PATH"]);
        Assert.Empty(new BinaryLocation().EnvironmentWith(null));
    }

    [Fact]
    public void Only_a_launcher_in_a_bin_folder_marks_an_install()
    {
        // An unrelated orkeon.cmd elsewhere on PATH says nothing about a libexec beside it.
        var scripts = Path.Combine("C:", "Users", "me", "scripts");
        var probe = StudioFromItsOwnMsi()
            .WithPathDirectories(scripts)
            .WithFile(Path.Combine(scripts, "orkeon.cmd"))
            .WithFile(Path.Combine("C:", "Users", "me", "libexec", "orkeon", "orkeon.exe"));

        var location = new OrkeonBinaryLocator(probe, WindowsNames, environment: _ => null).Locate();

        Assert.False(location.Found);
        Assert.DoesNotContain(Path.Combine("C:", "Users", "me", "libexec", "orkeon", "orkeon.exe"), location.ProbedPaths);
    }

    [Fact]
    public void A_path_entry_with_a_trailing_separator_still_leads_to_the_install()
    {
        var probe = StudioFromItsOwnMsi()
            .WithPathDirectories(Path.Combine(InstallerRoot, "bin") + Path.DirectorySeparatorChar)
            .WithFile(Path.Combine(InstallerRoot, "bin", "orkeon.cmd"))
            .WithFile(InstallerCli);

        var location = new OrkeonBinaryLocator(probe, WindowsNames, environment: _ => null).Locate();

        Assert.Equal(InstallerCli, location.Path);
        Assert.Equal(BinarySource.OrkeonInstaller, location.Source);
    }

    [Fact]
    public void A_launcher_without_its_executable_falls_through_to_the_rest_of_path()
    {
        var toolDir = Path.Combine("C:", "Users", "me", ".dotnet", "tools");
        var probe = StudioFromItsOwnMsi()
            .WithPathDirectories(Path.Combine(InstallerRoot, "bin"), toolDir)
            .WithFile(Path.Combine(InstallerRoot, "bin", "orkeon.cmd"))
            .WithFile(Path.Combine(toolDir, "orkeon.exe"));

        var location = new OrkeonBinaryLocator(probe, WindowsNames, environment: _ => null).Locate();

        Assert.Equal(Path.Combine(toolDir, "orkeon.exe"), location.Path);
        Assert.Equal(BinarySource.SearchPath, location.Source);
    }

    [Fact]
    public void A_bin_folder_the_user_named_leads_to_its_install_before_path()
    {
        // The user points at the folder where `orkeon` seems to live; a .NET tool on PATH
        // must not win over that choice.
        var custom = Path.Combine("D:", "Orkeon");
        var customCli = Path.Combine(custom, "libexec", "orkeon", "orkeon.exe");
        var toolDir = Path.Combine("C:", "Users", "me", ".dotnet", "tools");
        FakeExecutableProbe Probe() => StudioFromItsOwnMsi()
            .WithPathDirectories(toolDir)
            .WithFile(Path.Combine(toolDir, "orkeon.exe"))
            .WithFile(Path.Combine(custom, "bin", "orkeon.cmd"))
            .WithFile(customCli);

        var explicitHit = new OrkeonBinaryLocator(
            Probe(), WindowsNames, explicitDirectory: Path.Combine(custom, "bin"), environment: _ => null).Locate();
        Assert.Equal(customCli, explicitHit.Path);
        Assert.Equal(BinarySource.ExplicitDirectory, explicitHit.Source);

        var variableHit = new OrkeonBinaryLocator(
            Probe(), WindowsNames,
            environment: name => name == OrkeonBinaryLocator.DirectoryEnvironmentVariable ? "\"" + Path.Combine(custom, "bin") + "\" " : null).Locate();
        Assert.Equal(customCli, variableHit.Path);
        Assert.Equal(BinarySource.EnvironmentVariable, variableHit.Source);
    }

    [Fact]
    public void The_installers_default_folder_comes_before_the_development_checkout()
    {
        var studio = Path.Combine("C:", "src", "orkeon-studio");
        var studioBin = Path.Combine(studio, "src", "apps", "Orkeon.Studio.Wpf", "bin", "Debug", "net10.0-windows");
        var devCli = Path.Combine("C:", "src", "orkeon", "src", "scripting", "Orkeon.Scripting.Cli", "bin", "Debug", "net10.0", "orkeon.exe");
        var probe = new FakeExecutableProbe { BaseDirectory = studioBin }
            .WithFile(Path.Combine(studio, "Orkeon.Studio.sln"))
            .WithFile(devCli)
            .WithFile(InstallerCli);

        var location = new OrkeonBinaryLocator(probe, WindowsNames, environment: LocalAppDataOnly).Locate();

        Assert.Equal(InstallerCli, location.Path);
        Assert.Equal(BinarySource.OrkeonInstaller, location.Source);
    }

    [Fact]
    public void An_empty_quoted_directory_variable_is_no_directory()
    {
        // ORKEON_CLI_DIR="" must not turn into a probe of the current directory.
        var probe = StudioFromItsOwnMsi();

        var location = new OrkeonBinaryLocator(
            probe, WindowsNames, explicitDirectory: "\"\"",
            environment: name => name == OrkeonBinaryLocator.DirectoryEnvironmentVariable ? "\"\"" : null).Locate();

        Assert.False(location.Found);
        Assert.DoesNotContain("orkeon.exe", location.ProbedPaths);
    }

    [Fact]
    public void A_quoted_local_app_data_still_names_the_installers_folder()
    {
        var probe = StudioFromItsOwnMsi().WithFile(InstallerCli);

        var location = new OrkeonBinaryLocator(
            probe, WindowsNames,
            environment: name => name == OrkeonBinaryLocator.LocalAppDataEnvironmentVariable ? " \"" + LocalAppData + "\"" : null).Locate();

        Assert.Equal(InstallerCli, location.Path);
    }
}
