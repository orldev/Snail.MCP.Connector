using Snail.MCP.Connector.Adapters;

namespace Snail.MCP.Connector.Tests.Adapters;

/// <summary>Proposals kept by the server: one file each, read back as written, gone with the journal's days.</summary>
public sealed class FileProposalsTests : IDisposable
{
    private readonly string _folder = TestFolder.Create();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task FindAsync_SavedProposal_IsReadBackAsWritten()
    {
        var proposals = new FileProposals(_folder);
        var proposal = Proposal("w0123456789abcdef");

        await proposals.SaveAsync(proposal, CancellationToken.None);

        var read = await proposals.FindAsync(proposal.Id, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(proposal, read with { Sample = proposal.Sample });
        Assert.Equal(proposal.Sample, read.Sample);
    }

    /// <summary>A proposal carries a sample of the company's rows, so it goes when the journal's days have passed.</summary>
    [Fact]
    public async Task SaveAsync_ProposalOlderThanTheDays_DeletesIt()
    {
        var proposals = new FileProposals(_folder, days: 30);
        await proposals.SaveAsync(Proposal("w0000000000000001"), CancellationToken.None);
        File.SetLastWriteTimeUtc(Path.Combine(_folder, "w0000000000000001.json"), DateTime.UtcNow.AddDays(-40));

        await new FileProposals(_folder, days: 30).SaveAsync(Proposal("w0000000000000002"), CancellationToken.None);

        Assert.Null(await proposals.FindAsync("w0000000000000001", CancellationToken.None));
        Assert.NotNull(await proposals.FindAsync("w0000000000000002", CancellationToken.None));
    }

    [Fact]
    public async Task FindAsync_IdThatIsAPath_IsNone() =>
        Assert.Null(await new FileProposals(_folder).FindAsync("../journal/2026-10-30", CancellationToken.None));

    private static WriteProposal Proposal(string id) => new()
    {
        Id = id,
        Task = "t1",
        Source = "crm",
        Collection = "deals",
        Operation = WriteOperation.Update,
        Filter = """{"stage":"lead"}""",
        Change = """{"$set":{"stage":"won"}}""",
        Reason = "signed",
        Matched = 1,
        Sample = ["""{"stage":"lead"}"""],
        At = DateTimeOffset.UnixEpoch
    };
}
