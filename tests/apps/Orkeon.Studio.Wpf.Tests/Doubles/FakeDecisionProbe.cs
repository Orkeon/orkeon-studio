using Orkeon.Studio.Core.Decision;

namespace Orkeon.Studio.Wpf.Tests.Doubles;

/// <summary>An <see cref="IDecisionProbe"/> that replays a scripted verdict and records what it was asked.</summary>
public sealed class FakeDecisionProbe : IDecisionProbe
{
    public List<DecisionProbeRequest> Requests { get; } = [];

    public Func<DecisionProbeRequest, DecisionProbeResult> Answer { get; set; } = static request => new DecisionProbeResult
    {
        Provider = request.Provider,
        Elapsed = TimeSpan.FromSeconds(1),
        Checks = [new DecisionProbeCheck("yes_no", true, "yes (0.98)")],
    };

    public Task<DecisionProbeResult> ProbeAsync(DecisionProbeRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(Answer(request));
    }
}
