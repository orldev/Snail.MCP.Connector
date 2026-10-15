namespace Snail.MCP.Connector.Application.Ports;

/// <summary>Where proposals to write are kept between the agent's proposal, the person's decision and the write.</summary>
public interface IProposals
{
    /// <summary>Keeps the proposal as it now stands.</summary>
    Task SaveAsync(WriteProposal proposal, CancellationToken cancellationToken);

    /// <summary>The proposal of that id; null when there is none.</summary>
    Task<WriteProposal?> FindAsync(string id, CancellationToken cancellationToken);
}
