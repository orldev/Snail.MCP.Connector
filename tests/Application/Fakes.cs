using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;

namespace Snail.MCP.Connector.Tests.Application;

/// <summary>The ports of <see cref="Queries"/> as plain objects a test sets up and reads back.</summary>
internal sealed class Fakes
{
    public const string TaskId = "4f1c2b7a9d3e4a6b8c0d1e2f3a4b5c6d";

    public Dictionary<string, ReachableSource> Sources { get; } = new(StringComparer.Ordinal)
    {
        ["sales"] = new(new Source("sales", SourceKind.MongoDb, "sales", ["orders", "customers"]), new SourceAccess("mongodb://reader@db/sales", null)),
        ["ledger"] = new(new Source("ledger", SourceKind.SqlServer, "ledger", [Source.Every]), new SourceAccess("Server=db;User=reader", null)),
        ["crm"] = new(new Source("crm", SourceKind.MongoDb, "crm", ["deals"]) { CanWrite = true }, new SourceAccess("mongodb://reader@db/crm", "mongodb://writer@db/crm"))
    };

    public Proposals Kept { get; } = new();

    public ConnectorOptions Settings { get; } = new() { ApprovalKey = "key-of-the-stand" };

    public Writes Writes(TimeProvider? clock = null)
    {
        var options = Options.Create(Settings);
        var context = new DefaultHttpContext();
        if (Root is not null)
            context.Request.QueryString = new QueryString($"?root={Root}");

        var copy = new SessionCopy(new HttpContextAccessor { HttpContext = context }, options);

        return new Writes(new SourceList(Sources), Database, Kept, Written, copy, clock ?? TimeProvider.System, options);
    }

    internal sealed class Proposals : IProposals
    {
        public Dictionary<string, WriteProposal> All { get; } = new(StringComparer.Ordinal);

        public Task SaveAsync(WriteProposal proposal, CancellationToken cancellationToken)
        {
            All[proposal.Id] = proposal;

            return Task.CompletedTask;
        }

        public Task<WriteProposal?> FindAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(All.GetValueOrDefault(id));
    }

    public Documents Database { get; } = new();

    public SqlFake Tables { get; } = new();

    public Journal Written { get; } = new();

    public string? Root { get; set; } = $"/workspaces/{TaskId}/step-0";

    public Queries Queries()
    {
        var options = Options.Create(new ConnectorOptions());
        var context = new DefaultHttpContext();
        if (Root is not null)
            context.Request.QueryString = new QueryString($"?root={Root}");

        var copy = new SessionCopy(new HttpContextAccessor { HttpContext = context }, options);

        return new Queries(new SourceList(Sources), Database, Tables, Written, new NoFiles(), copy, TimeProvider.System, options);
    }

    internal sealed class SourceList(Dictionary<string, ReachableSource> sources) : ISources
    {
        public Task<Answer<IReadOnlyList<Source>>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Answer.Ok<IReadOnlyList<Source>>([.. sources.Values.Select(source => source.Source)]));

        public Task<Answer<ReachableSource>> FindAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(sources.TryGetValue(name, out var found) ? Answer.Ok(found) : Answer.Failed<ReachableSource>(Messages.NoSource(name), Messages.NoSourceHint));
    }

    internal sealed class Documents : IDocuments
    {
        public List<string> Asked { get; } = [];

        public RowPage Page { get; set; } = new(["""{"number": 1}""", """{"number": 2}"""], IsCut: false);

        public DocumentQuery? LastQuery { get; private set; }

        public Task<Answer<IReadOnlyList<string>>> CollectionsAsync(ReachableSource source, CancellationToken cancellationToken)
        {
            Asked.Add("collections");

            return Task.FromResult(Answer.Ok<IReadOnlyList<string>>(["salaries", "orders", "customers"]));
        }

        public Task<Answer<CollectionShape>> ShapeAsync(ReachableSource source, string collection, int sample, CancellationToken cancellationToken)
        {
            Asked.Add($"shape {collection}");

            return Task.FromResult(Answer.Ok(new CollectionShape(collection, 10, 10, [])));
        }

        public Task<Answer<RowPage>> FindAsync(ReachableSource source, DocumentQuery query, CancellationToken cancellationToken)
        {
            Asked.Add($"find {query.Collection}");
            LastQuery = query;

            return Task.FromResult(Answer.Ok(Page));
        }

        public Task<Answer<RowPage>> AggregateAsync(ReachableSource source, string collection, string pipeline, RowLimit limit, CancellationToken cancellationToken)
        {
            Asked.Add($"aggregate {collection}");

            return Task.FromResult(Answer.Ok(Page));
        }

        public Task<Answer<long>> CountAsync(ReachableSource source, string collection, string filter, CancellationToken cancellationToken)
        {
            Asked.Add($"count {collection}");

            return Task.FromResult(Answer.Ok(42L));
        }

        public long Matched { get; set; } = 3;

        public List<WriteProposal> WrittenProposals { get; } = [];

        public Task<Answer<WritePreview>> PreviewAsync(ReachableSource source, string collection, string filter, int sample, CancellationToken cancellationToken)
        {
            Asked.Add($"preview {collection}");

            return Task.FromResult(Answer.Ok(new WritePreview(Matched, ["""{"stage": "lead"}"""])));
        }

        public Task<Answer<long>> WriteAsync(ReachableSource source, WriteProposal proposal, CancellationToken cancellationToken)
        {
            WrittenProposals.Add(proposal);

            return Task.FromResult(Answer.Ok(proposal.Matched));
        }

        public async IAsyncEnumerable<string> StreamAsync(ReachableSource source, DocumentQuery query, string? pipeline, int most, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Asked.Add($"stream {query.Collection}");

            foreach (string row in Page.Rows)
            {
                await Task.Yield();
                yield return row;
            }
        }
    }

    internal sealed class SqlFake : ITables
    {
        public List<string> Asked { get; } = [];

        public Task<Answer<IReadOnlyList<string>>> TablesAsync(ReachableSource source, CancellationToken cancellationToken) =>
            Task.FromResult(Answer.Ok<IReadOnlyList<string>>(["public.accounts", "hr.salaries"]));

        public Task<Answer<CollectionShape>> ShapeAsync(ReachableSource source, string table, CancellationToken cancellationToken) =>
            Task.FromResult(Answer.Ok(new CollectionShape(table, 10, 3, [])));

        public Task<Answer<RowPage>> QueryAsync(ReachableSource source, string select, RowLimit limit, CancellationToken cancellationToken)
        {
            Asked.Add(select);

            return Task.FromResult(Answer.Ok(new RowPage(["""{"n": 1}"""], IsCut: false)));
        }

        public async IAsyncEnumerable<string> StreamAsync(ReachableSource source, string select, int most, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Asked.Add(select);
            await Task.Yield();
            yield return """{"n": 1}""";
        }
    }

    internal sealed class Journal : IQueryJournal
    {
        public List<QueryRecord> Records { get; } = [];

        public bool IsDown { get; set; }

        public Task<Answer<QueryRecord>> WriteAsync(QueryRecord query, CancellationToken cancellationToken)
        {
            if (IsDown)
                return Task.FromResult(Answer.Failed<QueryRecord>(Messages.JournalDown));

            Records.Add(query);

            return Task.FromResult(Answer.Ok(query));
        }

        public Task<IReadOnlyList<QueryRecord>> ReadAsync(string task, int most, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QueryRecord>>([.. Records.Where(record => record.Task == task).Reverse().Take(most)]);
    }

    private sealed class NoFiles : ITableFiles
    {
        public async Task<Answer<ExportedFile>> WriteAsync(string copy, string file, TableFormat format, IAsyncEnumerable<string> rows, int most, CancellationToken cancellationToken)
        {
            int count = 0;
            await foreach (string row in rows.WithCancellation(cancellationToken))
                count++;

            return Answer.Ok(new ExportedFile($"exports/{file}", count, ["number"], IsCut: false));
        }
    }
}
