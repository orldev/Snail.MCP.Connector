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
read with its comments and strings taken out, in the engine's own quoting — a quote inside a quoted name (`"a'"`, and
`[a']` on SQL Server), PostgreSQL's `E'…'` and `$$…$$` are read as the database reads them: a second statement, a word
that writes, runs code or ends the transaction (`INSERT`, `UPDATE`, `DELETE`, `INTO`, `EXEC`, `COPY`, `SET`, `COMMIT`…),
a PostgreSQL function that reads a table or a query named in a string (`table_to_xml`, `query_to_xml`, `dblink`…) or a
table after `FROM`/`JOIN` the source does not allow is refused before the database sees it. On PostgreSQL the statement
then runs as a subquery cut at the rows asked — one statement whatever its text holds, and the database stops at the
limit itself — in a transaction that is `READ ONLY` and always rolled back; on SQL Server in a transaction that is always
rolled back, the command cancelled once the page is full. The query's seconds are its command timeout.
`connector_schema` reads the columns from `information_schema`, three rows as examples, the planner's row estimate and
the indexes; `connector_export` writes the rows of a `SELECT` as it does a collection's.

**The wall that counts is the read-only user's grants.** The rules are a door that answers a model in words it can act
on; no list of words names every function that reads a table by its name, and a statement the rules let through still
reads whatever the user may read. Give the reader `SELECT` on the tables the source allows, and nothing more — the list
of a source's tables is kept by the database only when its grants say the same:

```sql
-- PostgreSQL: the tables the source allows, not ALL TABLES
CREATE ROLE reader LOGIN PASSWORD '…';
GRANT CONNECT ON DATABASE ledger TO reader;
GRANT USAGE ON SCHEMA public TO reader;
GRANT SELECT ON clients, invoices, payments TO reader;
ALTER ROLE reader SET default_transaction_read_only = on;

-- SQL Server, in the database: the same tables; db_datareader only when the source allows every one (*)
CREATE LOGIN reader WITH PASSWORD = '…';
CREATE USER reader FOR LOGIN reader;
GRANT SELECT ON dbo.clients TO reader;
GRANT SELECT ON dbo.invoices TO reader;
GRANT SELECT ON dbo.payments TO reader;

-- the writing user, only when writes are wanted, and only on the tables an agent may change
CREATE ROLE writer LOGIN PASSWORD '…';                                   -- PostgreSQL
GRANT CONNECT ON DATABASE ledger TO writer;
GRANT USAGE ON SCHEMA public TO writer;
GRANT SELECT, INSERT, UPDATE, DELETE ON invoices, payments TO writer;
```

## Writes

A write is proposed, never made at once. `connector_propose_write` dry-runs it as the read-only user — how many
documents it touches, three of them as they are — and keeps it in the data folder; the agent then calls the platform's
`request_write_approval`, and the task waits. The stand's owner or an administrator (the right to manage agents) reads the proposal in the dashboard, which takes it from
this server at `GET /proposals/{id}` and brings the decision to `POST /proposals/{id}/decision`, both with the
`X-Approval-Key` header — a key the stand gives the dashboard and no agent. `connector_apply` then runs an allowed
proposal once, as the writing user, for the task that proposed it, within 7 days. An update or a delete needs a filter;
an update is field operators; one proposal touches at most 1000 documents or rows. Applied to MongoDB, the writing
user reads the ids the filter matches now and writes only those: more than the person allowed, and nothing is written —
the data changed, propose again. Every step — propose, decision, applying, apply — goes into the journal first; a step
the journal cannot hold is not taken. Without `APPROVAL_KEY` writes are off.

A proposal is `pending`, then `approved` or `rejected`, then `applying` and `applied`. It is taken — saved as
`applying` — under one lock before the database is asked, so two calls at once write it once and two decisions at once
take one; what the write ended in is saved whatever became of the call. A write that failed goes back to `approved`;
a call cut off during the write leaves the proposal `applying`, and nothing runs it again until a person looks at the
data.

A SQL table is written the same way, and no SQL an agent wrote is ever run: the filter is columns and values —
`{"id": 42}`, or `$eq`, `$ne`, `$gt`, `$gte`, `$lt`, `$lte`, `$in`, `$null`, all of which must hold — an update is `$set`
and `$inc`, an insert is an array of rows, and `update_one` needs a filter that matches exactly one row. The server
writes the statement itself, every name checked and quoted and every value a parameter (PostgreSQL takes text untyped,
so a date written as `"2026-06-01"` is read as the column's type). The dry run counts and shows the rows as the
read-only user; the write runs as the writing user in one transaction, rolled back when it would touch more rows than
the person allowed — the data changed since, and it has to be proposed again.

## Exports and the journal

`connector_export` writes the rows of a filter, a pipeline or a `SELECT` into the task's working copy under `exports/`,
as CSV (UTF-8 with a byte order mark) or an Excel workbook. Its start goes into the journal before any row is read, so
no file of the company's rows reaches the copy unrecorded. The rows are read twice — flattened into a spool file of the
server's temporary folder, then written from it a row at a time — so memory holds the columns, not the rows; the file
appears whole, moved into place. A nested field is a column of its dotted path; past `MOST_EXPORT_COLUMNS` the rest of a
row's fields go into one column, `_more`, as JSON. A workbook cell is a number only when the data held a number — the
postal code `08001` stays text — and a CSV cell of text that a spreadsheet would run as a formula (`=`, `+`, `-`, `@`)
starts with an apostrophe.

An export is a job of its task (`Snail.Sdk.Mcp.Hosting` jobs): it is checked and begun in the journal while the call is
served, and its rows are written by the job. The call takes `wait` (60 seconds unless given, 0 to 900) and answers with
the file when it is written within it, or with the job, `job://connector/export/<id>`, whose resource carries the same
answer once the file is there. The server offers `resources.subscribe`, so a client that can wait — the platform's
worker through `Snail.Sdk.Mcp.JobClient` — is told of the end, and no model is asked to call again. The job belongs to
the task the address names, not to the session: a step that connects again still reads it, a step of another task
does not. A call given up stops the waiting, not the export; a server stopping cancels it, and no file is left.

The journal is one JSON-lines file a day, and the same line in a file of the task's own, which `connector_journal`
reads; both go after `JOURNAL_DAYS`. A query whose answer the server could not read — a value .NET has no type for, a
document too deep — is answered as such and journaled like any other; a value a SQL driver cannot carry into .NET (an
interval of months, `NaN`) is written as the database spells it.

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
(`/workspaces`, where exports land), `URL` (`http://0.0.0.0:8830/`, its port on every interface of the container),
`ROWS` (50), `MOST_ROWS` (200), `MOST_BYTES` (65536), `MOST_EXPORT_ROWS` (100000), `MOST_EXPORT_COLUMNS` (1000),
`EXPORT_SECONDS` (600), `SAMPLE` (200), `QUERY_SECONDS` (30), `JOURNAL_DAYS` (365: the journal, and the proposals with
their samples of rows), `APPROVAL_KEY` (empty: writes off), `MOST_WRITTEN` (1000), `PROPOSAL_SAMPLE` (3),
`PROPOSAL_DAYS` (7).

The server is the standard's container host: a session for every client, a request a browser makes refused, the heap
trimmed when idle. The address a session opens names its task (`?root=`) and the tools it may use (`?tool=`); nothing
else checks who calls, so the server belongs on the stand's own network, reached by the platform's workers alone.

```bash
docker build --build-context feed=../../nuget -t s3l-connector-mcp:local .
```
