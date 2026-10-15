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
refused, and a join may read only collections the source allows.

A SQL Server or PostgreSQL source is read with `connector_query` and one `SELECT` (or `WITH … SELECT`). The statement is
read with its comments and strings taken out: a second statement, a word that writes or runs code (`INSERT`, `UPDATE`,
`DELETE`, `INTO`, `EXEC`, `COPY`, `SET`…) or a table after `FROM`/`JOIN` the source does not allow is refused before the
database sees it. It then runs in a transaction that is always rolled back — on PostgreSQL also `READ ONLY`, so the
database refuses a write itself — with the query's seconds as its command timeout. On SQL Server the read-only user is
the wall that counts: give it `db_datareader` and nothing else. `connector_schema` reads the columns from
`information_schema`, three rows as examples, the planner's row estimate and the indexes; `connector_export` writes the
rows of a `SELECT` as it does a collection's.

```sql
-- PostgreSQL
CREATE ROLE reader LOGIN PASSWORD '…';
GRANT CONNECT ON DATABASE ledger TO reader;
GRANT USAGE ON SCHEMA public TO reader;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO reader;
ALTER ROLE reader SET default_transaction_read_only = on;

-- SQL Server, in the database
CREATE LOGIN reader WITH PASSWORD = '…';
CREATE USER reader FOR LOGIN reader;
ALTER ROLE db_datareader ADD MEMBER reader;

-- the writing user, only when writes are wanted, and only on the tables an agent may change
CREATE ROLE writer LOGIN PASSWORD '…';                                   -- PostgreSQL
GRANT CONNECT ON DATABASE ledger TO writer;
GRANT USAGE ON SCHEMA public TO writer;
GRANT SELECT, INSERT, UPDATE, DELETE ON invoices, payments TO writer;
```

## Writes

A write is proposed, never made at once. `connector_propose_write` dry-runs it as the read-only user — how many
documents it touches, three of them as they are — and keeps it in the data folder; the agent then calls the platform's
`request_write_approval`, and the task waits. The stand's owner reads the proposal in the dashboard, which takes it from
this server at `GET /proposals/{id}` and brings the decision to `POST /proposals/{id}/decision`, both with the
`X-Approval-Key` header — a key the stand gives the dashboard and no agent. `connector_apply` then runs an allowed
proposal once, as the writing user, for the task that proposed it, within 7 days. An update or a delete needs a filter;
an update is field operators; one proposal touches at most 1000 documents or rows. Without `APPROVAL_KEY` writes are
off.

A SQL table is written the same way, and no SQL an agent wrote is ever run: the filter is columns and values —
`{"id": 42}`, or `$eq`, `$ne`, `$gt`, `$gte`, `$lt`, `$lte`, `$in`, `$null`, all of which must hold — an update is `$set`
and `$inc`, an insert is an array of rows, and `update_one` needs a filter that matches exactly one row. The server
writes the statement itself, every name checked and quoted and every value a parameter (PostgreSQL takes text untyped,
so a date written as `"2026-06-01"` is read as the column's type). The dry run counts and shows the rows as the
read-only user; the write runs as the writing user in one transaction, rolled back when it would touch more rows than
the person allowed — the data changed since, and it has to be proposed again.

## Sources

A source is one KV v2 secret in the stand's Vault, under `secret/connectors/<name>`:

| Key | What |
|---|---|
| `kind` | `mongodb`, `sqlserver` or `postgresql` |
| `database` | the database read |
| `collections` | the collections an agent may read, comma-separated; `*` for every one |
| `description` | what a person wrote about the data, shown to the agent |
| `readUri` | the connection string of the user that may only read: a `mongodb://` URI, or for SQL the driver's own form (`Host=…;Username=…;Password=…` for PostgreSQL, `Server=…;User Id=…;Password=…;TrustServerCertificate=true` for SQL Server); the database is taken from `database` |
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
