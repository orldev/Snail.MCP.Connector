using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Tools;

namespace Snail.MCP.Connector.Extensions;

/// <summary>The dashboard's door: a proposal shown to a person, and the person's decision brought back.</summary>
/// <remarks>
/// Not a tool: an agent reaches this server through its MCP endpoint only, and the door answers only a request carrying
/// the approval key, which the stand gives the dashboard and no agent. A wrong key and a missing proposal answer the same
/// 404, so the door says nothing to whoever guesses.
/// </remarks>
public static class ApprovalEndpointExtensions
{
    /// <summary>The header the key travels in.</summary>
    public const string KeyHeader = "X-Approval-Key";

    extension(WebApplication app)
    {
        /// <summary>GET /proposals/{id} and POST /proposals/{id}/decision.</summary>
        public WebApplication MapApprovals()
        {
            app.MapGet("/proposals/{id}", async (string id, HttpRequest request, Writes writes, CancellationToken cancellationToken) =>
                await writes.ShowAsync(id, request.Headers[KeyHeader].FirstOrDefault(), cancellationToken) is { } proposal
                    ? Results.Text(ToolResponse.Data(proposal).ToJsonString(ToolResponse.Options), "application/json")
                    : Results.NotFound());

            app.MapPost("/proposals/{id}/decision", async (string id, ProposalDecision decision, HttpRequest request, Writes writes, CancellationToken cancellationToken) =>
                await writes.DecideAsync(id, request.Headers[KeyHeader].FirstOrDefault(), decision.Allow, decision.By, decision.Comment, cancellationToken) is { } proposal
                    ? Results.Text(ToolResponse.Data(proposal).ToJsonString(ToolResponse.Options), "application/json")
                    : Results.NotFound());

            return app;
        }
    }
}

/// <summary>A person's decision on a proposal, as the dashboard brings it.</summary>
/// <param name="Allow">Whether the write may run.</param>
/// <param name="By">Who decided, by their login on the stand.</param>
/// <param name="Comment">What they said with it.</param>
public sealed record ProposalDecision(bool Allow, string By, string? Comment);
