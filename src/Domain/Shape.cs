namespace Snail.MCP.Connector.Domain;

/// <summary>One field as the sampled documents hold it: where, of which types, and how often it is there.</summary>
/// <param name="Path">The field's dotted path, <c>customer.address.city</c>; an array's items are named <c>items[]</c>.</param>
/// <param name="Types">The types seen, most frequent first: string, int, double, decimal, bool, date, objectId, object, array, null.</param>
/// <param name="Share">How many of the sampled documents hold the field, from 0 to 1.</param>
/// <param name="Example">One value seen, shortened, so a model knows what the data looks like.</param>
public sealed record FieldShape(string Path, IReadOnlyList<string> Types, double Share, string? Example);

/// <summary>A collection as its sample shows it.</summary>
/// <param name="Name">The collection.</param>
/// <param name="Documents">The documents the server estimates the collection holds.</param>
/// <param name="Sampled">How many documents the shape was read from.</param>
/// <param name="Fields">The fields, in path order.</param>
public sealed record CollectionShape(string Name, long Documents, int Sampled, IReadOnlyList<FieldShape> Fields)
{
    /// <summary>The indexes of the collection by their keys, so a model can ask what is quick to ask.</summary>
    public IReadOnlyList<string> Indexes { get; init; } = [];
}

/// <summary>Rows an answer carries, and whether there were more than it carries.</summary>
/// <param name="Rows">The rows as relaxed Extended JSON objects, one per document.</param>
/// <param name="IsCut">Whether the limit or the size stopped the reading before the end.</param>
public sealed record RowPage(IReadOnlyList<string> Rows, bool IsCut);
