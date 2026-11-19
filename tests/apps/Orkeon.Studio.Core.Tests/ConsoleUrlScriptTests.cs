using System.ComponentModel;
using System.Diagnostics;
using SysProcess = System.Diagnostics.Process;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Presets;
using Orkeon.Tests.Shared.Produced;
using Orkeon.Tests.Shared.Timing;

namespace Orkeon.Studio.Core.Tests;

/// <summary>The manual script that lists the console links Studio opens.</summary>
public sealed class ConsoleUrlScriptTests
{
    [Fact]
    [Trait("BR", "BR-02")]
    [Trait("BR", "UC-05")]
    public async System.Threading.Tasks.Task ShouldGiveAnHttpsConsoleForEveryProviderOfTheCatalogue()
    {
        var root = ProducedFile.RepositoryRoot();
        var expected = LlmPresets.ProviderCatalogFor(EnglishStudioStrings.Instance)
            .Select(c => c.KeyConsoleUri)
            .OfType<Uri>()
            .ToList();
        Assert.All(expected, uri => Assert.Equal(Uri.UriSchemeHttps, uri.Scheme));

        var (exitCode, stdout, stderr) = await RunScript(root, "--list");
        Assert.True(exitCode == 0, $"exit {exitCode}: {stderr}");

        var printed = stdout.Split('\n')
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(t => t.StartsWith("https://", StringComparison.Ordinal)))
            .Where(token => token is not null)
            .Select(token => token!)
            .Order(StringComparer.Ordinal)
            .Distinct()
            .ToList();

        Assert.Equal(
            expected.Select(u => u.AbsoluteUri).Order(StringComparer.Ordinal).Distinct().ToList(),
            printed);
    }

    [Fact]
    [Trait("BR", "BR-12")]
    public void ShouldNotBeCalledByAnyWorkflow()
    {
        var folder = Path.Combine(ProducedFile.RepositoryRoot(), ".github", "workflows");
        var files = Directory.EnumerateFiles(folder, "*.yml")
            .Concat(Directory.EnumerateFiles(folder, "*.yaml"))
            .ToList();

        Assert.NotEmpty(files);
        foreach (var file in files)
            Assert.DoesNotContain("check-console-urls", File.ReadAllText(file), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--help</c> and a mistyped option end offline: a manual check that misread its
    /// arguments as « check every link » would reach the network on a metered connection.
    /// </summary>
    [Theory]
    [InlineData("--help", 0)]
    [InlineData("-h", 0)]
    [InlineData("--lsit", 2)]
    public async System.Threading.Tasks.Task ShouldAnswerHelpAndRefuseAnUnknownArgumentWithoutARequest(string argument, int expectedExit)
    {
        var (exitCode, stdout, stderr) = await RunScript(ProducedFile.RepositoryRoot(), argument);

        Assert.True(exitCode == expectedExit, $"exit {exitCode}: {stderr}");
        Assert.Contains("--list", expectedExit == 0 ? stdout : stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", stdout, StringComparison.Ordinal);
    }

    /// <summary>Runs the script under python3 with a hang guard; skips when python3 is missing.</summary>
    private static async System.Threading.Tasks.Task<(int ExitCode, string Stdout, string Stderr)> RunScript(string root, string argument)
    {
        var start = new ProcessStartInfo("python3")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // A request the script should not make fails at once on a dead proxy instead of
        // reaching the network, should a regression read an argument as « check every link ».
        foreach (var name in new[] { "https_proxy", "HTTPS_PROXY", "http_proxy", "HTTP_PROXY" })
            start.Environment[name] = "http://127.0.0.1:9";
        foreach (var name in new[] { "no_proxy", "NO_PROXY" })
            start.Environment.Remove(name);
        start.ArgumentList.Add(Path.Combine(root, "scripts", "check-console-urls.py"));
        start.ArgumentList.Add(argument);

        SysProcess process;
        try
        {
            process = SysProcess.Start(start)!;
        }
        catch (Win32Exception)
        {
            Assert.Skip("python3 is not on PATH");
            throw;
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(Polling.DefaultTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail($"check-console-urls.py {argument} did not end within the hang guard");
            }

            return (process.ExitCode, await output, await error);
        }
    }
}
