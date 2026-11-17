using Orkeon.Domain.Tools;
using Orkeon.Domain.Tools.Protocol;

namespace Orkeon.Studio.Core.Tests.Doubles;

/// <summary>
/// A tool that only carries a name, registered where a run would register Orkeon's own tool of that
/// name under a setting the test host does not hold. Its assembly is named <c>Orkeon.*</c>, so the
/// engine's deliverable check counts it as one of Orkeon's own, as it counts the real one. Never called.
/// </summary>
public sealed class StubBaseTool(string name) : IBaseTool
{
    public string Name { get; } = name;

    public string Description => "A name only, for the deliverable check.";

    public ToolSchema Schema => new(Name, Description, []);

    public Task<ToolCallResponse> CallAsync(ToolCallRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A stub tool is never called.");

    public Task<ToolResult> ExecuteAsync(string input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A stub tool is never called.");

    public bool ValidateInput(string input) => true;
}
