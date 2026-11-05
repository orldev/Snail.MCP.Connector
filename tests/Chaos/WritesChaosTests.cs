using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Adapters;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Tests.Application;

namespace Snail.MCP.Connector.Tests.Chaos;

/// <summary>
/// The promise of a write — "run once, for the task that proposed it" — against what a model and a network really do:
/// call a tool twice at once, and drop a call halfway. Proposals are kept by the real <see cref="FileProposals"/>.
/// </summary>
public sealed class WritesChaosTests : IDisposable
{
    private const string Key = "key-of-the-stand";

    private readonly string _folder = TestFolder.Create();

    private readonly Fakes _fakes = new();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>
    /// A model that calls tools in parallel sends connector_apply twice. Both calls read the proposal as approved before
    /// either saves it as applied: an <c>$inc</c> is added twice, a person allowed it once.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_TwoCallsAtOnce_WritesOnce()
    {
        var writer = new CountingWriter(_fakes.Database) { Delay = TimeSpan.FromMilliseconds(200) };
        var writes = Writes(writer);
        string id = await ApprovedAsync(writes);

        await Task.WhenAll(writes.ApplyAsync(id, CancellationToken.None), writes.ApplyAsync(id, CancellationToken.None));

        Assert.Equal(1, writer.Writes);
    }

    /// <summary>
    /// The call is cancelled — the platform's step timed out, the HTTP connection dropped — after the database wrote and
    /// before the proposal was saved as applied. The proposal is still approved, and the next call writes it again.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_CancelledAfterTheDatabaseWrote_DoesNotWriteAgain()
    {
        using var dropped = new CancellationTokenSource();
        var writer = new CountingWriter(_fakes.Database) { AfterWrite = dropped };
        var writes = Writes(writer);
        string id = await ApprovedAsync(writes);

        await Record.ExceptionAsync(() => writes.ApplyAsync(id, dropped.Token));
        await writes.ApplyAsync(id, CancellationToken.None);

        Assert.Equal(1, writer.Writes);
    }

    /// <summary>
    /// Two people on the dashboard — the owner allows, an administrator refuses — at the same moment. Both read the
    /// proposal as pending, both decisions are journaled and answered as taken; the file keeps whichever came last.
    /// </summary>
    [Fact]
    public async Task DecideAsync_AllowAndRefuseAtOnce_TakesOneDecision()
    {
        var journal = new SlowJournal(TimeSpan.FromMilliseconds(200));
        var writes = Writes(_fakes.Database, journal);
        var proposed = await writes.ProposeAsync("crm", "deals", "update", """{"stage": "lead"}""", """{"$inc": {"touches": 1}}""", "follow up", CancellationToken.None);
        string id = proposed.Value!.Id;

        var decided = await Task.WhenAll(
            writes.DecideAsync(id, Key, isAllowed: true, "owner", null, CancellationToken.None),
            writes.DecideAsync(id, Key, isAllowed: false, "admin", "not now", CancellationToken.None));

        Assert.Single(decided, answer => answer?.Value is not null);
    }

    private Writes Writes(IDocuments documents, IQueryJournal? journal = null)
    {
        var options = Options.Create(_fakes.Settings);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?root={_fakes.Root}");
        var copy = new SessionCopy(new HttpContextAccessor { HttpContext = context }, options);

        return new Writes(new Fakes.SourceList(_fakes.Sources), documents, _fakes.Tables, new FileProposals(_folder), journal ?? new SlowJournal(TimeSpan.Zero), copy, TimeProvider.System, options);
    }

    private static async Task<string> ApprovedAsync(Writes writes)
    {
        var proposed = await writes.ProposeAsync("crm", "deals", "update", """{"stage": "lead"}""", """{"$inc": {"touches": 1}}""", "follow up", CancellationToken.None);
        string id = proposed.Value!.Id;
        await writes.DecideAsync(id, Key, isAllowed: true, "owner", null, CancellationToken.None);

        return id;
    }

    /// <summary>The document database of <see cref="Fakes"/>, with writes counted, slowed or followed by a dropped call.</summary>
    private sealed class CountingWriter(Fakes.Documents inner) : IDocuments
    {
        private int _writes;

        public int Writes => _writes;

        public TimeSpan Delay { get; init; }

        public CancellationTokenSource? AfterWrite { get; init; }

        public async Task<Answer<long>> WriteAsync(ReachableSource source, WriteProposal proposal, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _writes);
            await Task.Delay(Delay, CancellationToken.None);
            await (AfterWrite?.CancelAsync() ?? Task.CompletedTask);

            return Answer.Ok(proposal.Matched);
        }

        public Task<Answer<IReadOnlyList<string>>> CollectionsAsync(ReachableSource source, CancellationToken cancellationToken) => inner.CollectionsAsync(source, cancellationToken);

        public Task<Answer<CollectionShape>> ShapeAsync(ReachableSource source, string collection, int sample, CancellationToken cancellationToken) => inner.ShapeAsync(source, collection, sample, cancellationToken);

        public Task<Answer<RowPage>> FindAsync(ReachableSource source, DocumentQuery query, CancellationToken cancellationToken) => inner.FindAsync(source, query, cancellationToken);

        public Task<Answer<RowPage>> AggregateAsync(ReachableSource source, string collection, string pipeline, RowLimit limit, CancellationToken cancellationToken) => inner.AggregateAsync(source, collection, pipeline, limit, cancellationToken);

        public Task<Answer<long>> CountAsync(ReachableSource source, string collection, string filter, CancellationToken cancellationToken) => inner.CountAsync(source, collection, filter, cancellationToken);

        public Task<Answer<WritePreview>> PreviewAsync(ReachableSource source, string collection, string filter, int sample, CancellationToken cancellationToken) => inner.PreviewAsync(source, collection, filter, sample, cancellationToken);

        public IAsyncEnumerable<string> StreamAsync(ReachableSource source, DocumentQuery query, string? pipeline, int most, CancellationToken cancellationToken) => inner.StreamAsync(source, query, pipeline, most, cancellationToken);
    }

    /// <summary>A journal that takes its time over each line, as a disk under load does, and is safe to call at once.</summary>
    private sealed class SlowJournal(TimeSpan delay) : IQueryJournal
    {
        public ConcurrentQueue<QueryRecord> Records { get; } = new();

        public async Task<Answer<QueryRecord>> WriteAsync(QueryRecord query, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, CancellationToken.None);
            Records.Enqueue(query);

            return Answer.Ok(query);
        }

        public Task<IReadOnlyList<QueryRecord>> ReadAsync(string task, int most, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QueryRecord>>([.. Records.Where(record => record.Task == task).Take(most)]);
    }
}
