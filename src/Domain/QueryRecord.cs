namespace Snail.MCP.Connector.Domain;

/// <summary>What an agent did to a company database, for the journal every query lands in.</summary>
/// <remarks>
/// The owner's decision of 15.10.2026: every query is written down — which task, which source, what was asked, how many
/// rows came back — so a company can answer who read its data and why, as the GDPR asks of it.
/// </remarks>
/// <param name="At">When the query ended.</param>
/// <param name="Task">The task the query was made for, read from the session's working copy.</param>
/// <param name="Source">The source asked.</param>
/// <param name="Collection">The collection or table asked.</param>
/// <param name="Operation">What was done: schema, find, aggregate, count, export.</param>
/// <param name="Query">The query as it was sent, as JSON.</param>
/// <param name="Rows">How many rows came back.</param>
/// <param name="Milliseconds">How long it took.</param>
/// <param name="Error">Why it failed; null when it did not.</param>
public sealed record QueryRecord(
    DateTimeOffset At,
    string Task,
    string Source,
    string Collection,
    string Operation,
    string Query,
    long Rows,
    long Milliseconds,
    string? Error);
