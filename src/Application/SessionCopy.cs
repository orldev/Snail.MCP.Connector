using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Configuration;
using Snail.Sdk.Mcp.Hosting.Sessions;

namespace Snail.MCP.Connector.Application;

/// <summary>The task this session reads for, and the working copy its exports are written into, read from the address it opened.</summary>
/// <remarks>
/// The platform names a step's copy in the address — <c>/mcp?root=/workspaces/&lt;task&gt;/step-3</c> — as it does for its
/// file and git servers. The task is the copy's first folder under the workspaces root (<see cref="WorkspaceCopy"/>), and
/// every query is written into the journal under it. A session without a copy is refused: a query nobody can trace back
/// to a task is the one the journal exists to rule out. What stays here is this server's words for each reason the SDK
/// gives: a session that names no copy and one that names a copy outside the root are told different things.
/// </remarks>
public sealed partial class SessionCopy(ISessionAddress address, IOptions<ConnectorOptions> options)
{
    /// <summary>What the address calls the working copy.</summary>
    public const string Parameter = SessionRoots.Parameter;

    /// <summary>The copy and its task, or why this session has none.</summary>
    public Answer<WorkspacePlace<string>> Current() => Of(address.Values(Parameter).FirstOrDefault(), options.Value.WorkspacesRoot);

    /// <summary>The copy a root names under the workspaces folder, or why it names none.</summary>
    /// <remarks>The refusal is typed by the SDK's reason — <c>noRoot</c>, <c>outside</c>, <c>malformed</c>, <c>notATask</c> — so a program tells a missing copy from a wrong one.</remarks>
    public static Answer<WorkspacePlace<string>> Of(string? root, string workspaces) =>
        WorkspaceCopy.Of(root, workspaces, IsTaskId).ToAnswer(refusal => refusal is CopyRefusal.NoRoot
            ? Answer.Failed(Messages.NoCopy, Messages.NoCopyHint)
            : Answer.Failed(Messages.CopyOutside(root ?? string.Empty, workspaces)));

    /// <summary>Whether a folder under the workspaces root names a task: the platform's task ids.</summary>
    public static bool IsTaskId(string folder) => TaskId().IsMatch(folder);

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial System.Text.RegularExpressions.Regex TaskId();
}

