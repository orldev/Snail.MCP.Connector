# Snail.MCP.Connector

Company databases for agents: MongoDB, SQL Server, PostgreSQL; read-only, writes on approval.

Its tools: [docs/reference/tools](docs/reference/tools/index.md), written from the server itself. Built on
[Snail.Sdk](https://github.com/orldev/Snail.Sdk): the rules, the dotnet tool package, publishing into `~/.s3l-mcp`, the
shared workflows.

```bash
dotnet publish src -c Release -r osx-arm64 -p:SnailMcpInstall=true    # ~/.s3l-mcp/Snail.MCP.Connector/current
```

## How it reads

The server reads the company's databases for the platform's agents, inside the company's own network. Every read goes
through a database user that can only read; every query — task, source, collection, what was asked, how many rows — is
written into a journal of one JSON-lines file a day in the server's data folder, and an answer whose query could not be
written down is withheld. Pipelines that write or run code (`$out`, `$merge`, `$function`, `$accumulator`, `$where`) are
refused, and a join may read only collections the source allows. MongoDB first; SQL Server and PostgreSQL follow.

## Writes

A write is proposed, never made at once. `connector_propose_write` dry-runs it as the read-only user — how many
documents it touches, three of them as they are — and keeps it in the data folder; the agent then calls the platform's
`request_write_approval`, and the task waits. The stand's owner reads the proposal in the dashboard, which takes it from
this server at `GET /proposals/{id}` and brings the decision to `POST /proposals/{id}/decision`, both with the
`X-Approval-Key` header — a key the stand gives the dashboard and no agent. `connector_apply` then runs an allowed
proposal once, as the writing user, for the task that proposed it, within 7 days. An update or a delete needs a filter;
an update is field operators; one proposal touches at most 1000 documents. Without `APPROVAL_KEY` writes are off.

## Sources

A source is one KV v2 secret in the stand's Vault, under `secret/connectors/<name>`:

| Key | What |
|---|---|
| `kind` | `mongodb`, `sqlserver` or `postgresql` |
| `database` | the database read |
| `collections` | the collections an agent may read, comma-separated; `*` for every one |
| `description` | what a person wrote about the data, shown to the agent |
| `readUri` | the connection string of the user that may only read |
| `writeUri` | the connection string of the user that may write — used only after a person approves a write |

## Settings

The settings file `connector`, then the `SNAIL_MCP_CONNECTOR_` variables, which win: `VAULT_ADDRESS`, `VAULT_TOKEN` (read
and list under the path and nothing else), `VAULT_MOUNT` (`secret`), `VAULT_PATH` (`connectors`), `WORKSPACES_ROOT`
(`/workspaces`, where exports land), `ROWS` (50), `MOST_ROWS` (200), `MOST_BYTES` (65536), `MOST_EXPORT_ROWS` (100000),
`SAMPLE` (200), `QUERY_SECONDS` (30), `JOURNAL_DAYS` (365), `APPROVAL_KEY` (empty: writes off), `MOST_WRITTEN` (1000),
`PROPOSAL_SAMPLE` (3), `PROPOSAL_DAYS` (7).

```bash
docker build --build-context feed=../../nuget -t snail/s3l-connector-mcp .
```
