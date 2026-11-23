using Orkeon.Studio.Core.Launch;

namespace Orkeon.Studio.Core.Tests.Launch;

/// <summary>
/// The LLM profiles a spawned run is offered besides its settings file's: one rule for the window and
/// the terminal, the host profiles a front-end lays and those the inherited environment lays.
/// </summary>
public sealed class LaunchLlmProfilesTests
{
    [Fact]
    public void The_profiles_an_environment_lays_are_read_from_its_names_alone()
    {
        var laid = LaunchLlmProfiles.LaidIn(
            ["HOME", "ORKEON_CLI_DIR", "ORKEON_Llm__Profiles__a__Model", "llm__profiles__B__provider", "ORKEON_ORKEON_Llm__Profiles__c__Model", "Llm__Profiles____Model"]);

        Assert.Equal(["B", "a"], laid.Order(StringComparer.Ordinal));
        Assert.Contains("b", laid);
    }

    [Fact]
    public void A_run_is_offered_the_host_profiles_and_the_environments_each_once_in_any_letter_case()
    {
        var offered = LaunchLlmProfiles.Offered(
            ["claude", "Strong"],
            ["PATH", "ORKEON_Llm__Profiles__strong__Model", "Llm__Profiles__local__BaseUrl"]);

        Assert.Equal(3, offered.Count);
        Assert.Contains("claude", offered, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("local", offered, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("STRONG", offered, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Without_host_profiles_the_environment_alone_is_offered()
    {
        Assert.Equal(["x"], LaunchLlmProfiles.Offered([], ["ORKEON_Llm__Profiles__x__Model"]));
    }
}
