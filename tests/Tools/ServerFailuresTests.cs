using Microsoft.Extensions.DependencyInjection;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Extensions;
using Snail.MCP.Connector.Tests.Application;

namespace Snail.MCP.Connector.Tests.Tools;

/// <summary>A failure nobody foresaw, as the server whole answers it: in the server's own words, never the connection string a driver put in its message.</summary>
public sealed class ServerFailuresTests
{
    private const string Proposal = "w0000000000000001";

    private const string Password = "s3cret";

    /// <summary>
    /// A database driver throws past the adapter with the connection string in its message, as MongoDB's does for a
    /// server it cannot select. The server answered the message as it was, and the writing user's password with it.
    /// </summary>
    [Fact]
    public async Task CallTool_ApplyWhoseWriterThrowsWithItsConnectionString_AnswersWithoutThePassword()
    {
        var fakes = new Fakes();
        fakes.Kept.All[Proposal] = new WriteProposal
        {
            Id = Proposal,
            Task = Fakes.TaskId,
            Source = "crm",
            Collection = "deals",
            Operation = WriteOperation.Delete,
            Filter = """{"stage": "lost"}""",
            Change = "{}",
            Reason = "a test",
            Matched = 1,
            At = DateTimeOffset.UtcNow,
            State = ProposalState.Approved
        };
        await using var server = await InProcessServer.StartAsync(
            builder =>
            {
                builder.ConfigureConnectorServer();
                builder.Services.AddSingleton<ISources>(new Fakes.SourceList(fakes.Sources));
                builder.Services.AddSingleton<IDocuments>(new ThrowingWriter());
                builder.Services.AddSingleton<IQueryJournal>(fakes.Written);
                builder.Services.AddSingleton<IProposals>(fakes.Kept);
            },
            app => app.UseConnectorEndpoints());
        await using var session = await server.SessionAsync($"root=/workspaces/{Fakes.TaskId}/step-0");

        var result = await session.CallToolAsync("connector_apply", new Dictionary<string, object?> { ["proposal"] = Proposal });

        Assert.True(result.IsError);
        Assert.False(result.IsOk());
        Assert.DoesNotContain(Password, result.Text(), StringComparison.Ordinal);
    }

    /// <summary>A database whose driver throws on a write with the writing user's connection string in its message.</summary>
    private sealed class ThrowingWriter : IDocuments
    {
        public Task<Answer<long>> WriteAsync(ReachableSource source, WriteProposal proposal, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"No server answered mongodb://writer:{Password}@db:27017/crm within 10 s.");

        public Task<Answer<IReadOnlyList<string>>> CollectionsAsync(ReachableSource source, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Answer<CollectionShape>> ShapeAsync(ReachableSource source, string collection, int sample, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Answer<RowPage>> FindAsync(ReachableSource source, DocumentQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Answer<RowPage>> AggregateAsync(ReachableSource source, string collection, string pipeline, RowLimit limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Answer<long>> CountAsync(ReachableSource source, string collection, string filter, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Answer<WritePreview>> PreviewAsync(ReachableSource source, string collection, string filter, int sample, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamAsync(ReachableSource source, DocumentQuery query, string? pipeline, int most, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
