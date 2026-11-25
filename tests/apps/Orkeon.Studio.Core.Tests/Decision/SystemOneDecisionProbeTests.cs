using System.Net;
using System.Text.Json.Nodes;
using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Decision;
using Orkeon.Studio.Core.Tests.Doubles;

namespace Orkeon.Studio.Core.Tests.Decision;

/// <summary>The test of a decision provider (System One), over a scripted handler.</summary>
public sealed class SystemOneDecisionProbeTests
{
    private const string Key = "liquid-secret-key";

    private const string GoodAnswer =
        """
        {"model":"d1","answers":{
          "yes_no":{"type":"noul","noul":0.97},
          "choice":{"type":"choice","choice":"delivery","confidence":0.8,"probabilities":{"delivery":0.8,"billing":0.15,"account":0.05}},
          "score":{"type":"score","score":1.8,"confidence":0.9,"probabilities":{"0":0.0,"1":0.2,"2":0.8}}},
         "usage":{"input_tokens":120}}
        """;

    private static async Task<DecisionProbeResult> ProbeAsync(StubHttpMessageHandler handler, DecisionProbeRequest? request = null)
    {
        using var client = new HttpClient(handler, disposeHandler: false);
        using var probe = new SystemOneDecisionProbe(client);
        return await probe.ProbeAsync(
            request ?? new DecisionProbeRequest { Provider = DecisionProviderDescriptor.Liquid, ApiKey = Key },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_right_answers_pass_in_one_request()
    {
        using var handler = new StubHttpMessageHandler { Body = GoodAnswer };

        var result = await ProbeAsync(handler);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(3, result.Checks.Count);
        Assert.Equal("d1", result.Model);
        Assert.Equal(120, result.InputTokens);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("Bearer " + Key, sent.Authorization);
        Assert.EndsWith("/systemone", result.Address, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_answer_of_the_right_shape_fails()
    {
        using var handler = new StubHttpMessageHandler { Body = GoodAnswer.Replace("0.97", "0.02", StringComparison.Ordinal) };

        var result = await ProbeAsync(handler);

        Assert.False(result.Succeeded);
        Assert.Equal(DecisionProbeFailure.None, result.Failure);
        Assert.Equal("yes_no", Assert.Single(result.Checks, check => check.Passed == false).Name);
    }

    [Fact]
    public async Task A_missing_key_sends_nothing_and_names_the_variable()
    {
        using var handler = new StubHttpMessageHandler();

        var result = await ProbeAsync(handler, new DecisionProbeRequest { Provider = DecisionProviderDescriptor.Liquid });

        Assert.Equal(DecisionProbeFailure.KeyMissing, result.Failure);
        Assert.Equal("LIQUID_API_KEY", result.Detail);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_refusal_is_told_without_the_key_nor_a_retry(HttpStatusCode status)
    {
        using var handler = new StubHttpMessageHandler { StatusCode = status, Body = $$"""{"detail":"bad {{Key}}"}""" };

        var result = await ProbeAsync(handler);

        Assert.Equal(DecisionProbeFailure.Refused, result.Failure);
        Assert.NotEmpty(result.Detail!);
        Assert.DoesNotContain(Key, result.Detail, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_transport_failure_is_a_result()
    {
        using var handler = new StubHttpMessageHandler { FailWith = new HttpRequestException("no such host") };

        var result = await ProbeAsync(handler);

        Assert.Equal(DecisionProbeFailure.Refused, result.Failure);
        Assert.Contains("no such host", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_case_of_the_user_is_asked_as_written_and_not_judged()
    {
        using var handler = new StubHttpMessageHandler { Body = GoodAnswer };

        var result = await ProbeAsync(handler, new DecisionProbeRequest
        {
            Provider = DecisionProviderDescriptor.Liquid,
            ApiKey = Key,
            Case = new DecisionProbeCase("The sky is blue.", "Is it about the sky?", ["weather", " ", "food"]),
        });

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Checks.Count);
        Assert.All(result.Checks, check => Assert.Null(check.Passed));
        var body = JsonNode.Parse(handler.LastRequest.Body!)!;
        Assert.Equal("The sky is blue.", (string?)body["state"]);
        Assert.Equal(["food", "weather"], body["questions"]!["choice"]!["criteria"]!.AsObject().Select(o => o.Key).Order());
    }

    [Fact]
    public async Task A_case_without_a_question_sends_nothing()
    {
        using var handler = new StubHttpMessageHandler();

        var result = await ProbeAsync(handler, new DecisionProbeRequest
        {
            Provider = DecisionProviderDescriptor.Kev,
            Case = new DecisionProbeCase("text", "", ["one"]),
        });

        Assert.Equal(DecisionProbeFailure.NothingAsked, result.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_address_that_is_no_url_sends_nothing()
    {
        using var handler = new StubHttpMessageHandler();

        var result = await ProbeAsync(handler, new DecisionProbeRequest
        {
            Provider = DecisionProviderDescriptor.Kev,
            BaseAddress = "localhost:8080",
        });

        Assert.Equal(DecisionProbeFailure.AddressInvalid, result.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void A_request_reads_the_settings_of_the_file_and_the_key_of_their_variable()
    {
        var document = AppSettingsDocument.Parse(
            """{"Orkeon":{"Liquid":{"Model":"m","BaseUrl":"https://example.test/v1","ApiKeyEnvVar":"MY_KEY","TimeoutSeconds":5}}}""");

        var request = DecisionProbeRequest.For(
            document.Decision, DecisionProviderDescriptor.Liquid, name => name == "MY_KEY" ? "value" : null);

        Assert.Equal(("m", "https://example.test/v1", "value", 5), (request.Model, request.BaseAddress, request.ApiKey, request.TimeoutSeconds));
    }
}
