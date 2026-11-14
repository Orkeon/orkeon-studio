using Orkeon.Application.Agent;
using Orkeon.Constants.FileSystem;
using Orkeon.Domain.FileSystem;
using Orkeon.Studio.Core.Teams;

namespace Orkeon.Studio.Core.Launch;

/// <summary>An agent whose file-system declaration the runtime will refuse when it starts it.</summary>
/// <param name="Agent">The agent as the refusal names it.</param>
/// <param name="Reason">The runtime's own refusal.</param>
public sealed record FileSystemScopeRefusal(string Agent, string Reason);

/// <summary>
/// Tells, before a run, which agents of a YAML crew the runtime will refuse to start because of
/// their <c>filesystem:</c> scope (#349) or their working root: an entry under no mount of the run,
/// asking more than the mount grants, a duplicate, a malformed list, or a working root outside the
/// mounts or outside the scope. A launch mount can provoke it — a <c>--mount</c> that lowers a root's
/// rights, a settings entry the run does not mount.
/// <para>
/// The rule is never copied: the mounts the run will see are parsed by the engine's own
/// <see cref="FileSystemMount.Parse"/>, and each agent goes through the check the runtime makes at
/// start (<see cref="AgentWorkingRoot.NormalizeMounted"/> then <see cref="AgentWorkingRoot.ResolveScope"/>).
/// What Studio cannot see is left to the runtime: no verdict at all when a root has several settings
/// entries and nothing picks one (the crew's <c>mounts:</c> block may), and none for an agent whose
/// declaration reaches a root the runner mounts for itself beyond <c>/crew</c> and <c>/script</c>.
/// </para>
/// </summary>
public static class FileSystemScopePreflight
{
    /// <summary>The refusals the runtime will make, in the agents' order; empty when it will start them all.</summary>
    /// <param name="agents">The crew's declarations (<see cref="CrewFileSystemScopes.In"/>).</param>
    /// <param name="effective">The mount list the run will see (<c>ComputeEffectiveMounts</c>).</param>
    public static IReadOnlyList<FileSystemScopeRefusal> Check(
        IReadOnlyList<CrewAgentFileSystem> agents,
        IReadOnlyList<EffectiveMount> effective)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(effective);

        if (agents.Count == 0 || effective.Any(mount => mount.Selection == EffectiveMountSelection.Conflict))
            return [];

        var mounts = MountsOf(effective);
        if (mounts is null)
            return [];

        var refusals = new List<FileSystemScopeRefusal>();
        foreach (var agent in agents)
        {
            if (agent.Refusal is { } shape)
            {
                refusals.Add(new FileSystemScopeRefusal(agent.Key, shape));
                continue;
            }

            if (ReachesAnUnseenRunnerRoot(agent, mounts))
                continue;

            try
            {
                var root = AgentWorkingRoot.NormalizeMounted(agent.WorkingRoot, mounts);
                AgentWorkingRoot.ResolveScope(agent.Name, agent.Entries, mounts, parent: null, root);
            }
            catch (InvalidOperationException ex)
            {
                refusals.Add(new FileSystemScopeRefusal(agent.Name, ex.Message));
            }
        }

        return refusals;
    }

    /// <summary>
    /// The agent-facing mounts of the run, as the engine reads them; null when one of them does not
    /// parse — the runner then refuses the launch with its own message, and the scopes are moot.
    /// </summary>
    private static List<MountInfo>? MountsOf(IReadOnlyList<EffectiveMount> effective)
    {
        var mounts = new List<MountInfo>(effective.Count);
        foreach (var entry in effective.Where(entry => entry.IsMounted))
        {
            try
            {
                var mount = FileSystemMount.Parse(entry.Value);
                if (mount.Visibility == MountVisibility.AgentFacing)
                    mounts.Add(new MountInfo(mount.VirtualPath, mount.DefaultRights, mount.Overrides, mount.Visibility));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                return null;
            }
        }

        return mounts;
    }

    /// <summary>
    /// Whether the agent's entries or working root lie under a root the runner reserves for itself and
    /// may mount for the agents (<c>/sandbox</c>…) without Studio's prediction listing it.
    /// </summary>
    private static bool ReachesAnUnseenRunnerRoot(CrewAgentFileSystem agent, IReadOnlyList<MountInfo> mounts)
    {
        var unseen = RunnerVirtualRoots.All
            .Where(root => !mounts.Any(mount => IsUnder(mount.VirtualPath, root)))
            .ToList();

        return agent.Entries.Select(entry => entry.Path).Append(agent.WorkingRoot)
            .OfType<string>()
            .Any(path => unseen.Any(root => IsUnder(path, root)));
    }

    private static bool IsUnder(string path, string root)
    {
        var normalized = "/" + string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        var prefix = root.TrimEnd('/');
        return normalized.Equals(prefix, StringComparison.Ordinal)
            || normalized.StartsWith(prefix + "/", StringComparison.Ordinal);
    }
}
