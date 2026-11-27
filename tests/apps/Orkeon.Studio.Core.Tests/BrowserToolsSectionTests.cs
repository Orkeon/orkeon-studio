using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Tests.Doubles;
using Orkeon.Studio.Core.Validation;
using Orkeon.Studio.Tests.Shared;
using Orkeon.Tools.Web;

namespace Orkeon.Studio.Core.Tests;

/// <summary>
/// Orkeon #440 — the <c>Orkeon:Tools:Browser</c> view: the engine and the window of the browser tool,
/// the default written as no key, and a lone former <c>Orkeon:Tools:Firefox</c> section moved under the
/// new name before a write, so the file never holds the two the run refuses together.
/// </summary>
public sealed class BrowserToolsSectionTests
{
    [Fact]
    public void The_engine_and_the_window_round_trip_on_the_documented_keys()
    {
        var document = AppSettingsDocument.CreateEmpty();

        document.BrowserTools.Engine = "chrome";
        document.BrowserTools.Headless = true;

        var reparsed = AppSettingsDocument.Parse(document.ToJson());
        Assert.Equal("chrome", reparsed.GetString("Orkeon:Tools:Browser:Engine"));
        Assert.True(reparsed.GetBoolean("Orkeon:Tools:Browser:Headless"));
        Assert.True(reparsed.BrowserTools.Exists);
        Assert.Equal("chrome", reparsed.BrowserTools.EffectiveEngine);
    }

    [Fact]
    public void The_default_engine_and_a_window_are_written_as_no_key_and_no_empty_section()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "edge", "Headless": true } } } }""");

        document.BrowserTools.Engine = "Firefox";
        document.BrowserTools.Headless = false;

        Assert.Null(document.BrowserTools.Engine);
        Assert.Null(document.BrowserTools.Headless);
        Assert.Equal(BrowserToolsSection.DefaultEngine, document.BrowserTools.EffectiveEngine);
        Assert.False(document.BrowserTools.Exists);
        Assert.Null(document.GetNode("Orkeon:Tools:Browser"));
    }

    [Fact]
    public void A_write_moves_a_lone_former_section_keys_and_all()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Firefox": { "Headless": true, "ProfilePath": "/p" } } } }""");
        Assert.True(document.BrowserTools.LegacyExists);
        Assert.False(document.BrowserTools.Exists);

        document.BrowserTools.Engine = "brave";

        Assert.False(document.BrowserTools.LegacyExists);
        Assert.Equal("brave", document.GetString("Orkeon:Tools:Browser:Engine"));
        Assert.True(document.GetBoolean("Orkeon:Tools:Browser:Headless"));
        Assert.Equal("/p", document.GetString("Orkeon:Tools:Browser:ProfilePath"));
    }

    [Fact]
    public void Removing_a_key_absent_from_a_former_section_leaves_it_where_it_is()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Firefox": { "ProfilePath": "/p" } } } }""");

        document.BrowserTools.Engine = null;
        document.BrowserTools.Headless = false;

        Assert.True(document.BrowserTools.LegacyExists);
        Assert.False(document.BrowserTools.Exists);
    }

    /// <summary>The path, the keys and the engines are the runtime's, read from its options and its reference.</summary>
    [Fact]
    public void The_section_keys_and_engines_are_the_ones_the_runtime_reads()
    {
        Assert.Equal(FirefoxToolOptions.BrowserSectionName, BrowserToolsSection.SectionPath);
        Assert.Equal(FirefoxToolOptions.SectionName, BrowserToolsSection.LegacySectionPath);
        Assert.NotNull(typeof(FirefoxToolOptions).GetProperty("Engine"));
        Assert.NotNull(typeof(FirefoxToolOptions).GetProperty("Headless"));
        Assert.Equal(typeof(string), typeof(FirefoxToolOptions).GetProperty("Profile")?.PropertyType);
        Assert.Equal(typeof(FirefoxBrowserLifetime), typeof(FirefoxToolOptions).GetProperty("Lifetime")?.PropertyType);
        Assert.Equal(typeof(bool), typeof(FirefoxToolOptions).GetProperty("SharedInstance")?.PropertyType);
        Assert.NotNull(typeof(FirefoxToolOptions).GetProperty("DeniedProfiles"));
        Assert.Equal(BrowserToolsSection.DefaultEngine, new FirefoxToolOptions().Engine);

        var reference = File.ReadAllLines(Path.Combine(OrkeonSources.Root(), "docs", "reference", "configuration.md"));
        var rows = reference.Where(line => line.StartsWith("| `Engine` | string | `\"firefox\"`", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.All(BrowserToolsSection.Engines, engine => Assert.Contains($"`{engine}`", row, StringComparison.Ordinal)));

        // The three keys the tab edits, as the reference documents them: the lifetimes and their
        // default, a profile disposable by default, a sharing switch off by default.
        var lifetimes = reference.Where(line => line.StartsWith("| `Lifetime` | enum | `\"Run\"` |", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(lifetimes);
        Assert.All(lifetimes, row => Assert.Contains(string.Join(", ", BrowserToolsSection.Lifetimes.Select(l => $"`{l}`")), row, StringComparison.Ordinal));
        var profiles = reference.Where(line => line.StartsWith("| `Profile` | string | — |  | The profile a browser starts with", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(profiles);
        Assert.All(profiles, row => Assert.Contains($"`{BrowserToolsSection.DisposableProfile}` (the default", row, StringComparison.Ordinal));
        Assert.Contains(reference, line => line.StartsWith("| `SharedInstance` | boolean | `false` |", StringComparison.Ordinal));
    }
}

/// <summary>Orkeon #440 — the browser rules the run applies at start, said before the save.</summary>
public sealed class BrowserValidationTests
{
    private static IReadOnlyList<ValidationMessage> Validate(string json) =>
        new AppSettingsValidator(new FakeDirectoryProbe()).Validate(AppSettingsDocument.Parse(json));

    private static ValidationMessage? Find(IEnumerable<ValidationMessage> messages, string code) =>
        messages.FirstOrDefault(m => m.Code == code);

    [Fact]
    public void An_unknown_engine_is_an_error_and_the_case_is_ignored()
    {
        var bad = Find(Validate("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "opera" } } } }"""), ValidationCodes.BrowserEngineUnknown);
        var good = Validate("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "Chrome" } } } }""");

        Assert.NotNull(bad);
        Assert.Equal(ValidationSeverity.Error, bad.Severity);
        Assert.Equal("Orkeon:Tools:Browser:Engine", bad.Path);
        Assert.Contains("opera", bad.Text, StringComparison.Ordinal);
        Assert.Null(Find(good, ValidationCodes.BrowserEngineUnknown));
    }

    [Fact]
    public void Safari_without_a_window_is_an_error_on_its_headless_key()
    {
        var message = Find(Validate("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "safari", "Headless": true } } } }"""), ValidationCodes.BrowserSafariHeadless);
        var windowed = Validate("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "safari" } } } }""");

        Assert.NotNull(message);
        Assert.Equal(ValidationSeverity.Error, message.Severity);
        Assert.Equal("Orkeon:Tools:Browser:Headless", message.Path);
        Assert.Null(Find(windowed, ValidationCodes.BrowserSafariHeadless));
    }

    [Fact]
    public void Both_sections_are_an_error_and_the_former_alone_a_warning()
    {
        var both = Validate("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "edge" }, "Firefox": { "Headless": true } } } }""");
        var legacy = Validate("""{ "Orkeon": { "Tools": { "Firefox": { "Headless": true } } } }""");

        var refused = Find(both, ValidationCodes.BrowserSectionsBoth);
        Assert.NotNull(refused);
        Assert.Equal(ValidationSeverity.Error, refused.Severity);
        Assert.Equal("Orkeon:Tools:Firefox", refused.Path);
        Assert.Null(Find(both, ValidationCodes.BrowserSectionLegacy));

        var deprecated = Find(legacy, ValidationCodes.BrowserSectionLegacy);
        Assert.NotNull(deprecated);
        Assert.Equal(ValidationSeverity.Warning, deprecated.Severity);
        Assert.Null(Find(legacy, ValidationCodes.BrowserSectionsBoth));
    }

    [Fact]
    public void A_headless_switch_that_reads_as_no_boolean_is_a_warning_since_the_run_ignores_it()
    {
        var messages = Validate("""{ "Orkeon": { "Tools": { "Browser": { "Headless": "yes" } } } }""");
        var legacy = Validate("""{ "Orkeon": { "Tools": { "Firefox": { "Headless": "yes" } } } }""");

        var message = Assert.Single(messages, m => m.Path == "Orkeon:Tools:Browser:Headless");
        Assert.Equal(ValidationCodes.InvalidFieldType, message.Code);
        Assert.Equal(ValidationSeverity.Warning, message.Severity);
        Assert.Contains(legacy, m => m.Path == "Orkeon:Tools:Firefox:Headless" && m.Severity == ValidationSeverity.Warning);
        Assert.DoesNotContain(messages, m => m.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void The_engine_of_a_lone_former_section_is_not_judged_since_the_run_never_reads_it()
    {
        var messages = Validate("""{ "Orkeon": { "Tools": { "Firefox": { "Engine": "opera", "Headless": true } } } }""");

        Assert.Null(Find(messages, ValidationCodes.BrowserEngineUnknown));
        Assert.Null(Find(messages, ValidationCodes.BrowserSafariHeadless));
        Assert.NotNull(Find(messages, ValidationCodes.BrowserSectionLegacy));
    }
}

/// <summary>Orkeon #440 — a lone former section is read as the run reads it, and changed by renaming it.</summary>
public sealed class BrowserToolsLegacySectionTests
{
    [Fact]
    public void A_lone_former_section_gives_its_window_and_switching_it_off_renames_it()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Firefox": { "Headless": true, "ProfilePath": "/p" } } } }""");
        Assert.True(document.BrowserTools.Headless);
        Assert.Equal("Orkeon:Tools:Firefox:Headless", document.BrowserTools.HeadlessPath);

        document.BrowserTools.Headless = false;

        Assert.Null(document.BrowserTools.Headless);
        Assert.Null(document.GetNode("Orkeon:Tools:Firefox"));
        Assert.Equal("/p", document.GetString("Orkeon:Tools:Browser:ProfilePath"));
        Assert.Null(document.GetNode("Orkeon:Tools:Browser:Headless"));
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("write")]
    public void The_engine_of_a_former_section_is_left_behind_since_the_run_ignored_it(string how)
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Firefox": { "engine": "safari", "Headless": true } } } }""");

        if (how == "rename")
            document.BrowserTools.MoveLegacySection();
        else
            document.BrowserTools.Headless = true;

        Assert.Equal(BrowserToolsSection.DefaultEngine, document.BrowserTools.EffectiveEngine);
        Assert.Null(document.GetNode("Orkeon:Tools:Browser:engine"));
        Assert.Null(document.GetNode("Orkeon:Tools:Firefox"));
        Assert.True(document.BrowserTools.Headless);
    }

    [Fact]
    public void A_former_section_holding_only_an_engine_leaves_no_empty_section_when_renamed()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Firefox": { "Engine": "chrome" } } } }""");

        document.BrowserTools.MoveLegacySection();

        Assert.Null(document.GetNode("Orkeon:Tools:Firefox"));
        Assert.Null(document.GetNode("Orkeon:Tools:Browser"));
    }

    [Fact]
    public void Removing_the_former_section_beside_the_new_one_keeps_the_new_one()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "edge" }, "Firefox": { "Headless": true } } } }""");
        Assert.True(document.BrowserTools.BothSet);

        document.BrowserTools.RemoveLegacySection();

        Assert.False(document.BrowserTools.BothSet);
        Assert.Null(document.GetNode("Orkeon:Tools:Firefox"));
        Assert.Equal("edge", document.BrowserTools.Engine);
    }
}

/// <summary>
/// Orkeon #440 — Studio's browser rules against the runtime's own: <c>FirefoxToolOptions.From</c> reads the
/// file the run would, and the engine table of <c>BrowserEngines</c> is the list the picker offers.
/// </summary>
public sealed class BrowserToolsRuntimeOracleTests
{
    private static FirefoxToolOptions Read(AppSettingsDocument document)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJson()));
        return FirefoxToolOptions.From(new ConfigurationBuilder().AddJsonStream(stream).Build());
    }

    [Fact]
    public void The_engines_offered_are_exactly_the_ones_the_runtime_knows()
    {
        var table = typeof(FirefoxToolOptions).Assembly.GetType("Orkeon.Tools.Web.Browser.BrowserEngines", throwOnError: true)!
            .GetField("s_engines", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null) as IDictionary;

        Assert.NotNull(table);
        Assert.Equal(
            table.Keys.Cast<string>().Order(StringComparer.Ordinal),
            BrowserToolsSection.Engines.Order(StringComparer.Ordinal));
    }

    public static TheoryData<string> Files => new()
    {
        """{ "Orkeon": { "Tools": { "Browser": { "Engine": "edge" }, "Firefox": { "Headless": true } } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Engine": "edge" }, "Firefox": "x" } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Engine": "edge" }, "Firefox": [1] } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Engine": "edge" }, "Firefox": {} } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Engine": "edge" }, "Firefox": [] } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Engine": "edge" }, "Firefox": null } } }""",
        """{ "Orkeon": { "Tools": { "Firefox": { "Engine": "chrome", "Headless": true } } } }""",
        """{ "Orkeon": { "Tools": { "Firefox": { "Headless": "yes" } } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Headless": true } } } }""",
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void Both_sections_are_refused_exactly_when_the_run_refuses_them(string json)
    {
        var document = AppSettingsDocument.Parse(json);
        var runRefuses = false;
        try
        {
            Read(document);
        }
        catch (InvalidOperationException)
        {
            runRefuses = true;
        }

        Assert.True(runRefuses == document.BrowserTools.BothSet, $"run refuses: {runRefuses}, Studio: {document.BrowserTools.BothSet}, file: {json}");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Studio_reads_the_engine_and_the_window_the_run_reads_before_and_after_a_rename(string json)
    {
        var document = AppSettingsDocument.Parse(json);
        if (document.BrowserTools.BothSet)
            return;

        var before = Read(document);
        Assert.Equal(before.Engine, document.BrowserTools.EffectiveEngine);
        Assert.Equal(before.Headless, document.BrowserTools.Headless ?? false);

        document.BrowserTools.MoveLegacySection();
        var after = Read(document);
        Assert.Equal(before.Engine, after.Engine);
        Assert.Equal(before.Headless, after.Headless);
    }
}

/// <summary>
/// The profile a browser starts on, how long it lives and whether the runs of a host share it: the
/// runtime's defaults written as no key, under the section the run reads, and judged as the run judges them.
/// </summary>
public sealed class BrowserToolsProfileLifetimeTests
{
    private static IReadOnlyList<ValidationMessage> Validate(string json) =>
        new AppSettingsValidator(new FakeDirectoryProbe()).Validate(AppSettingsDocument.Parse(json));

    [Fact]
    public void The_three_keys_round_trip_and_their_defaults_are_written_as_no_key()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var browser = document.BrowserTools;

        browser.Profile = "  agents  ";
        browser.Lifetime = "Host";
        browser.SharedInstance = true;

        Assert.Equal("agents", document.GetString("Orkeon:Tools:Browser:Profile"));
        Assert.Equal("Host", document.GetString("Orkeon:Tools:Browser:Lifetime"));
        Assert.True(document.GetBoolean("Orkeon:Tools:Browser:SharedInstance"));
        Assert.Equal("Host", browser.EffectiveLifetime);

        browser.Profile = "Disposable";
        browser.Lifetime = "run";
        browser.SharedInstance = false;

        Assert.Null(document.GetNode("Orkeon:Tools:Browser"));
        Assert.Null(browser.Profile);
        Assert.Equal("Run", browser.EffectiveLifetime);
    }

    [Fact]
    public void Under_a_lone_former_section_the_keys_are_read_there_and_a_write_renames_it()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Firefox": { "Profile": "agents", "Lifetime": "host" } } } }""");
        var browser = document.BrowserTools;

        Assert.Equal("Orkeon:Tools:Firefox:Lifetime", browser.KeyPath("Lifetime"));
        Assert.Equal("agents", browser.Profile);
        Assert.Equal("host", browser.Lifetime);

        browser.SharedInstance = true;

        Assert.Null(document.GetNode("Orkeon:Tools:Firefox"));
        Assert.Equal("agents", document.GetString("Orkeon:Tools:Browser:Profile"));
        Assert.Equal("host", document.GetString("Orkeon:Tools:Browser:Lifetime"));
        Assert.True(document.GetBoolean("Orkeon:Tools:Browser:SharedInstance"));
    }

    [Theory]
    [InlineData("""{ "Orkeon": { "Tools": { "Browser": { "Lifetime": "1" } } } }""", "Orkeon:Tools:Browser:Lifetime")]
    [InlineData("""{ "Orkeon": { "Tools": { "Firefox": { "Lifetime": "forever" } } } }""", "Orkeon:Tools:Firefox:Lifetime")]
    public void An_unknown_lifetime_is_an_error_under_either_section(string json, string path)
    {
        var message = Assert.Single(Validate(json), m => m.Code == ValidationCodes.BrowserLifetimeUnknown);

        Assert.Equal(ValidationSeverity.Error, message.Severity);
        Assert.Equal(path, message.Path);
        Assert.DoesNotContain(Validate("""{ "Orkeon": { "Tools": { "Browser": { "Lifetime": "HOST" } } } }"""),
            m => m.Code == ValidationCodes.BrowserLifetimeUnknown);
    }

    [Fact]
    public void A_shared_switch_that_reads_as_no_boolean_is_a_warning()
    {
        var message = Assert.Single(Validate("""{ "Orkeon": { "Tools": { "Browser": { "SharedInstance": "yes" } } } }"""),
            m => m.Path == "Orkeon:Tools:Browser:SharedInstance");

        Assert.Equal(ValidationCodes.InvalidFieldType, message.Code);
        Assert.Equal(ValidationSeverity.Warning, message.Severity);
    }

    [Theory]
    [InlineData("chrome", "agents", true)]
    [InlineData("edge", "relative/folder", true)]
    [InlineData("chrome", "~/agents", true)]
    [InlineData("safari", "agents", true)]
    [InlineData("safari", "DISPOSABLE", false)]
    [InlineData("firefox", "agents", false)]
    [InlineData("brave", "disposable", false)]
    [InlineData("opera", "agents", false)]
    public void A_profile_the_engine_refuses_is_a_warning_on_the_profile(string engine, string profile, bool refused)
    {
        var json = $$"""{ "Orkeon": { "Tools": { "Browser": { "Engine": "{{engine}}", "Profile": "{{profile}}" } } } }""";
        var messages = Validate(json).Where(m => m.Code == ValidationCodes.BrowserProfileRefused).ToList();

        if (!refused)
        {
            Assert.Empty(messages);
            return;
        }

        var message = Assert.Single(messages);
        Assert.Equal(ValidationSeverity.Warning, message.Severity);
        Assert.Equal("Orkeon:Tools:Browser:Profile", message.Path);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_full_folder_path_is_a_chromium_profile_when_the_folder_exists(bool exists, bool refused)
    {
        var folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "agents-profile"));
        var json = $$"""{ "Orkeon": { "Tools": { "Browser": { "Engine": "chrome", "Profile": "{{folder.Replace("\\", "\\\\", StringComparison.Ordinal)}}" } } } }""";
        var probe = exists ? new FakeDirectoryProbe(folder) : new FakeDirectoryProbe();

        var messages = new AppSettingsValidator(probe).Validate(AppSettingsDocument.Parse(json))
            .Where(m => m.Code == ValidationCodes.BrowserProfileRefused).ToList();

        Assert.Equal(refused, messages.Count == 1);
        if (refused)
            Assert.Contains("does not exist", messages[0].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "Orkeon": { "Tools": { "Browser": { "Profile": "Work", "DeniedProfiles": [ "work" ] } } } }""", true)]
    [InlineData("""{ "Orkeon": { "Tools": { "Browser": { "Profile": "work", "DeniedProfiles": "home; work " } } } }""", true)]
    [InlineData("""{ "Orkeon": { "Tools": { "Firefox": { "Profile": "work", "DeniedProfiles": "work" } } } }""", true)]
    [InlineData("""{ "Orkeon": { "Tools": { "Browser": { "Profile": "work", "DeniedProfiles": [ "home" ] } } } }""", false)]
    [InlineData("""{ "Orkeon": { "Tools": { "Browser": { "DeniedProfiles": [ "disposable" ] } } } }""", false)]
    public void A_default_profile_the_host_forbids_is_a_warning_whatever_the_engine(string json, bool refused)
    {
        var messages = Validate(json).Where(m => m.Code == ValidationCodes.BrowserProfileRefused).ToList();

        Assert.Equal(refused, messages.Count == 1);
        if (refused)
            Assert.Contains("DeniedProfiles", messages[0].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "Orkeon": { "Tools": { "Browser": { "Profile": { "Name": "x" } } } } }""")]
    [InlineData("""{ "Orkeon": { "Tools": { "Browser": { "Profile": [ "x" ] } } } }""")]
    public void A_profile_that_is_no_text_is_a_warning_since_the_run_starts_on_a_disposable_one(string json)
    {
        var message = Assert.Single(Validate(json), m => m.Path == "Orkeon:Tools:Browser:Profile");

        Assert.Equal(ValidationCodes.InvalidFieldType, message.Code);
        Assert.Equal(ValidationSeverity.Warning, message.Severity);
        Assert.Null(AppSettingsDocument.Parse(json).BrowserTools.Profile);
    }

    [Fact]
    public void The_lifetimes_are_the_runtimes()
    {
        Assert.Equal(Enum.GetNames<FirefoxBrowserLifetime>(), BrowserToolsSection.Lifetimes);
        Assert.Equal(BrowserToolsSection.DefaultLifetime, nameof(FirefoxBrowserLifetime.Run));
        Assert.Equal(BrowserToolsSection.DefaultLifetime, new FirefoxToolOptions().Lifetime.ToString());
    }

    public static TheoryData<string> Files => new()
    {
        """{ "Orkeon": { "Tools": { "Browser": { "Profile": " agents ", "Lifetime": "host", "SharedInstance": true } } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Profile": "", "Lifetime": " ", "SharedInstance": "yes" } } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Lifetime": "1" } } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Lifetime": "bogus" } } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Lifetime": "RUN", "SharedInstance": "False" } } } }""",
        """{ "Orkeon": { "Tools": { "Firefox": { "Profile": "/p", "Lifetime": "Host", "SharedInstance": "true" } } } }""",
        """{ "Orkeon": { "Tools": { "Firefox": { "Lifetime": "x" } } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Profile": "DISPOSABLE", "Lifetime": true, "SharedInstance": 1 } } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Lifetime": 0 } } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Profile": {}, "Lifetime": {}, "SharedInstance": {} } } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Profile": [ "p" ], "Lifetime": [ "Host" ], "SharedInstance": [ true ] } } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "DeniedProfiles": "a, b;c" } } } }""",
        """{ "Orkeon": { "Tools": { "Browser": { "Lifetime": "Host" }, "Firefox": { "Lifetime": "Run" } } } }""",
    };

    /// <summary>What the run reads, and refuses, is what Studio reads and calls an error.</summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void Studio_reads_the_profile_the_lifetime_and_the_sharing_the_run_reads(string json)
    {
        var document = AppSettingsDocument.Parse(json);
        var browser = document.BrowserTools;
        FirefoxToolOptions? read;
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJson())))
        {
            try
            {
                read = FirefoxToolOptions.From(new ConfigurationBuilder().AddJsonStream(stream).Build());
            }
            catch (InvalidOperationException)
            {
                read = null;
            }
        }

        var studioRefuses = Validate(json).Any(m => m.Code is ValidationCodes.BrowserLifetimeUnknown or ValidationCodes.BrowserSectionsBoth);
        Assert.True((read is null) == studioRefuses, $"run refuses: {read is null}, Studio: {studioRefuses}, file: {json}");
        if (read is null)
            return;

        Assert.Equal(read.Profile, browser.Profile);
        Assert.Equal(read.Lifetime.ToString(), browser.EffectiveLifetime, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(read.SharedInstance, browser.SharedInstance ?? false);
        Assert.Equal(read.DeniedProfiles.Order(StringComparer.Ordinal), browser.DeniedProfiles.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal));
    }
}
