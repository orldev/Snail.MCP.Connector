using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Tools;
using Snail.Sdk.Mcp.Http;

namespace Snail.MCP.Connector.Extensions;

/// <summary>The dashboard's door: a proposal shown to a person, and the person's decision brought back.</summary>
/// <remarks>
/// Not a tool: an agent reaches this server through its MCP endpoint only, and the door answers only a request carrying
/// the approval key, which the stand gives the dashboard and no agent. A wrong key and a missing proposal answer the same
/// 404, so the door says nothing to whoever guesses. A request that carries an <c>Origin</c> is refused 403 before the
/// key is read, as at the MCP endpoint: the dashboard asks from the stand's backend, and a page in a browser never has a
/// reason to. Since Snail.Sdk 0.5 the refusal of <c>MapLocalMcp</c> holds for the MCP endpoint alone.
/// </remarks>
public static class ApprovalEndpointExtensions
{
    /// <summary>The header the key travels in.</summary>
    public const string KeyHeader = "X-Approval-Key";

    /// <summary>The path the door's addresses live under.</summary>
    public const string Door = "/proposals";

    extension(WebApplication app)
    {
        /// <summary>GET /proposals/{id} and POST /proposals/{id}/decision.</summary>
        public WebApplication MapApprovals()
        {
            app.UseBrowserRefusal(Door);

            app.MapGet("/proposals/{id}", async (string id, HttpRequest request, Writes writes, CancellationToken cancellationToken) =>
                await writes.ShowAsync(id, request.Headers[KeyHeader].FirstOrDefault(), cancellationToken) is { } proposal
                    ? Results.Text(AnswerFields.Of(proposal).ToJsonString(ToolAnswer.Wire), "application/json")
                    : Results.NotFound());

            app.MapPost("/proposals/{id}/decision", async (string id, ProposalDecision decision, HttpRequest request, Writes writes, CancellationToken cancellationToken) =>
                await writes.DecideAsync(id, request.Headers[KeyHeader].FirstOrDefault(), decision.Allow, decision.By, decision.Comment, cancellationToken) switch
                {
                    null => Results.NotFound(),
                    { IsSuccess: true, Value: { } proposal } => Results.Text(AnswerFields.Of(proposal).ToJsonString(ToolAnswer.Wire), "application/json"),
                    var refused => Results.Text(ToolAnswer.Text(ToolAnswer.Failed(refused)), "application/json", statusCode: StatusCodes.Status503ServiceUnavailable)
                });

            return app;
        }
    }
}

/// <summary>A person's decision on a proposal, as the dashboard brings it.</summary>
/// <param name="Allow">Whether the write may run.</param>
/// <param name="By">Who decided, by their login on the stand.</param>
/// <param name="Comment">What they said with it.</param>
public sealed record ProposalDecision(bool Allow, string By, string? Comment);
