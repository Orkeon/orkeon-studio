using System.Reflection;
using Microsoft.Extensions.Configuration;
using Orkeon.Infrastructure.LLMs.Decision;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Tests.Configuration;

/// <summary>
/// The binder's refusal of a value it cannot convert is said without that value (Orkeon #465): in the
/// host's own words wherever the host's pattern reads the sentence, and still without the value where
/// it does not, a key path holding an apostrophe (a decision profile named <c>l'equipe</c>), which the
/// host's pattern takes for the end of the value and then says the binder's sentence as it is.
/// </summary>
public sealed class BinderRefusalWithoutValueTests
{
    private const string Secret = "sk-ant-api03-studio-oracle-secret";

    [Theory]
    [InlineData("Failed to convert configuration value 'x' at 'Orkeon:Compaction:TriggerShare' to type 'System.Double'.")]
    [InlineData("Failed to convert configuration value '' at 'Orkeon:Compaction:TriggerShare' to type 'System.Double'.")]
    [InlineData("Failed to convert configuration value 'it's' at 'Orkeon:Bridge:MaxSessions' to type 'System.Int32'.")]
    [InlineData("Failed to convert configuration value 'a' at 'b' at 'Orkeon:Bridge:MaxSessions' to type 'System.Int32'.")]
    [InlineData("Failed to convert configuration value 'two\nlines' at 'Orkeon:Bridge:MaxSessions' to type 'System.Int32'.")]
    [InlineData("Some other refusal, said as it is.")]
    public void A_sentence_the_hosts_pattern_reads_is_said_in_the_hosts_words(string message)
    {
        Assert.Equal(EngineBinderRefusal.WithoutValue(message), StudioWithoutValue(message));
    }

    [Theory]
    [InlineData("Orkeon:Decision:Profiles:l'equipe:Stages:0:MinConfidence")]
    [InlineData("Orkeon:Decision:Profiles:'quoted':Stages:0:MinConfidence")]
    [InlineData("Orkeon:Decision:Profiles:a' at 'b:Stages:0:MinConfidence")]
    public void A_path_holding_an_apostrophe_never_lets_the_value_through(string path)
    {
        var message = BinderMessage(path, Secret);
        Assert.Contains(Secret, message, StringComparison.Ordinal);

        var studio = StudioWithoutValue(message);

        Assert.DoesNotContain(Secret, studio, StringComparison.Ordinal);
        Assert.StartsWith("Failed to convert the configuration value at '", studio, StringComparison.Ordinal);
        Assert.EndsWith("' to type 'System.Double'.", studio, StringComparison.Ordinal);
        // The host says the binder's sentence as it is, the value included, while its pattern takes no
        // apostrophe in a path: once it says it without the value, it must be Studio's sentence.
        var engine = EngineBinderRefusal.WithoutValue(message);
        if (!engine.Contains(Secret, StringComparison.Ordinal))
            Assert.Equal(engine, studio);
    }

    /// <summary>
    /// The e-mail and drive binders carry their own copy of the host's rewording (Orkeon #465): where
    /// it reads the sentence, it says the host's words, which are Studio's.
    /// </summary>
    [Theory]
    [InlineData("Failed to convert configuration value 'x' at 'Orkeon:Tools:Email:Accounts:work:Incoming:Port' to type 'System.Int32'.")]
    [InlineData("Failed to convert configuration value 'it's' at 'Orkeon:Tools:Drive:Accounts:work:TimeoutSeconds' to type 'System.Int32'.")]
    [InlineData("Failed to convert configuration value 'two\nlines' at 'Orkeon:Tools:Email:Accounts:work:Send:MaxPerHour' to type 'System.Int32'.")]
    [InlineData("Some other refusal, said as it is.")]
    public void The_account_binders_copies_say_the_hosts_words(string message)
    {
        var host = EngineBinderRefusal.WithoutValue(message);

        Assert.Equal(host, Orkeon.Tools.Email.Configuration.EmailOptionsBinder.WithoutValue(message));
        Assert.Equal(host, Orkeon.Tools.Drive.Configuration.DriveOptionsBinder.WithoutValue(message));
        Assert.Equal(host, StudioWithoutValue(message));
    }

    /// <summary>
    /// An account named with an apostrophe: the copies in the e-mail and drive binders inherit the
    /// host's pattern, which then says the binder's sentence as it is; Studio never lets the value
    /// through, and says the copies' words once they leave it out.
    /// </summary>
    [Theory]
    [InlineData("Orkeon:Tools:Email:Accounts:l'equipe:Incoming:Port")]
    [InlineData("Orkeon:Tools:Drive:Accounts:l'equipe:TimeoutSeconds")]
    [InlineData("Orkeon:Tools:Drive:Accounts:a' at 'b:MaxUploadMegabytes")]
    public void An_account_named_with_an_apostrophe_never_lets_the_value_through(string path)
    {
        var message = BinderMessage(path, Secret, new WholeNumberProbe());
        Assert.Contains(Secret, message, StringComparison.Ordinal);

        var studio = StudioWithoutValue(message);

        Assert.DoesNotContain(Secret, studio, StringComparison.Ordinal);
        Assert.StartsWith("Failed to convert the configuration value at '", studio, StringComparison.Ordinal);
        Assert.EndsWith("' to type 'System.Int32'.", studio, StringComparison.Ordinal);
        foreach (var copy in new[]
                 {
                     Orkeon.Tools.Email.Configuration.EmailOptionsBinder.WithoutValue(message),
                     Orkeon.Tools.Drive.Configuration.DriveOptionsBinder.WithoutValue(message),
                 })
        {
            if (!copy.Contains(Secret, StringComparison.Ordinal))
                Assert.Equal(copy, studio);
        }
    }

    [Fact]
    public void A_value_holding_the_words_of_the_sentence_never_gets_through()
    {
        var message = BinderMessage("Orkeon:Decision:Profiles:l'equipe:Stages:0:MinConfidence", Secret + "' at 'x' to type 'y'.");

        Assert.DoesNotContain(Secret, StudioWithoutValue(message), StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_under_a_profile_named_with_an_apostrophe_is_filed_at_its_key_and_never_echoed()
    {
        var document = AppSettingsDocument.Parse($$"""
            { "Orkeon": { "Decision": { "Profiles": {
              "l'equipe": { "Target": "default", "Stages": [ { "Kind": "route", "Routes": { "fast": "x" }, "MinConfidence": "{{Secret}}", "Fallback": "slow" } ] },
              "a' at 'b": { "Target": "default", "Stages": [ { "Kind": "verify", "EscalateTo": "x", "MinProbability": "{{Secret}}" } ] }
            } } } }
            """);

        var problems = document.Decision.ProfileProblems();

        Assert.NotEmpty(problems);
        Assert.All(problems, problem => Assert.DoesNotContain(Secret, problem.Problem, StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Path == "Orkeon:Decision:Profiles:l'equipe:Stages:0:MinConfidence"
            && problem.Problem == "Failed to convert the configuration value at 'Orkeon:Decision:Profiles:l'equipe:Stages:0:MinConfidence' to type 'System.Double'.");
        Assert.Contains(problems, problem => problem.Path == "Orkeon:Decision:Profiles:a' at 'b:Stages:0:MinProbability");
        // Nothing is filed under the section: each refusal is read at its key.
        Assert.DoesNotContain(problems, problem => problem.Path == DecisionProfilesOptions.SectionName);
    }

    // Studio's rewording is internal to its Core: reached through reflection, as the engine's is.
    private static readonly MethodInfo StudioReword = typeof(AppSettingsDocument).Assembly
        .GetType("Orkeon.Studio.Core.Configuration.SettingsBinding", throwOnError: true)!
        .GetMethod("WithoutValue", BindingFlags.Public | BindingFlags.Static, [typeof(string)])!;

    private static string StudioWithoutValue(string message) => (string)StudioReword.Invoke(null, [message])!;

    /// <summary>The configuration binder's own refusal of <paramref name="value"/> at <paramref name="path"/>, bound as a double.</summary>
    private static string BinderMessage(string path, string value) => BinderMessage(path, value, new Probe());

    /// <summary>The configuration binder's own refusal of <paramref name="value"/> at <paramref name="path"/>, bound onto <paramref name="probe"/>.</summary>
    private static string BinderMessage(string path, string value, object probe)
    {
        var last = path.LastIndexOf(':');
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [path] = value })
            .Build();
        var ex = Assert.Throws<InvalidOperationException>(() => configuration.GetSection(path[..last]).Bind(probe));
        return ex.Message;
    }

    private sealed class Probe
    {
        public double MinConfidence { get; set; }

        public double MinProbability { get; set; }
    }

    // The whole-number settings of an account, as the e-mail and drive binders bind them.
    private sealed class WholeNumberProbe
    {
        public int Port { get; set; }

        public int TimeoutSeconds { get; set; }

        public int MaxUploadMegabytes { get; set; }
    }
}
