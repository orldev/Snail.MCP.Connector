namespace Snail.MCP.Connector.Domain;

/// <summary>What a write does to a collection.</summary>
public enum WriteOperation
{
    /// <summary>Changes every document the filter matches.</summary>
    Update,

    /// <summary>Changes the first document the filter matches.</summary>
    UpdateOne,

    /// <summary>Adds documents.</summary>
    Insert,

    /// <summary>Removes every document the filter matches.</summary>
    Delete
}

/// <summary>Where a proposal to write stands.</summary>
public enum ProposalState
{
    /// <summary>It waits for a person.</summary>
    Pending,

    /// <summary>A person allowed it; it has not run yet.</summary>
    Approved,

    /// <summary>A person refused it.</summary>
    Rejected,

    /// <summary>It ran.</summary>
    Applied,

    /// <summary>It was taken to run and its end is not known yet: no second call may take it.</summary>
    Applying
}

/// <summary>
/// A write an agent proposed, as the server dry-ran it with the read-only user: what it changes, how many documents it
/// matched and what they look like — what a person reads before allowing it.
/// </summary>
/// <remarks>
/// The owner's decision of 15.10.2026: a company's data is written only after the owner of the stand allows the very
/// write. The proposal is kept here, by the server that will run it, so what a person reads is what runs — not the
/// agent's account of it — and it runs once, for the task that proposed it.
/// </remarks>
public sealed record WriteProposal
{
    /// <summary>The proposal's id, which the agent and the person name it by.</summary>
    public required string Id { get; init; }

    /// <summary>The task that proposed it; only that task may apply it.</summary>
    public required string Task { get; init; }

    /// <summary>The source written.</summary>
    public required string Source { get; init; }

    /// <summary>The collection written.</summary>
    public required string Collection { get; init; }

    /// <summary>What the write does.</summary>
    public required WriteOperation Operation { get; init; }

    /// <summary>Which documents an update or a delete touches, as Extended JSON; <c>{}</c> for an insert.</summary>
    public required string Filter { get; init; }

    /// <summary>The update's operators, or the documents an insert adds, as Extended JSON.</summary>
    public required string Change { get; init; }

    /// <summary>Why the agent proposes it, in its own words.</summary>
    public required string Reason { get; init; }

    /// <summary>How many documents the dry run matched; for an insert, how many it adds.</summary>
    public required long Matched { get; init; }

    /// <summary>A few of the matched documents as they are now, so a person sees what is about to change.</summary>
    public IReadOnlyList<string> Sample { get; init; } = [];

    /// <summary>When it was proposed.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Where it stands.</summary>
    public ProposalState State { get; init; } = ProposalState.Pending;

    /// <summary>Who allowed or refused it.</summary>
    public string? DecidedBy { get; init; }

    /// <summary>When it was allowed or refused.</summary>
    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>What the person said with the decision.</summary>
    public string? Comment { get; init; }

    /// <summary>How many documents the write changed, once it ran.</summary>
    public long? Written { get; init; }
}
