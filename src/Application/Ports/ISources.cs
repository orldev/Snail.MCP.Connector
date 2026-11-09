namespace Snail.MCP.Connector.Application.Ports;

/// <summary>The company databases declared on the stand.</summary>
public interface ISources
{
    /// <summary>Every source, without its credentials, and the ones left out; or why none could be read.</summary>
    Task<Answer<DeclaredSources>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One source with how to reach it; or why there is none of that name.</summary>
    Task<Answer<ReachableSource>> FindAsync(string name, CancellationToken cancellationToken);
}
