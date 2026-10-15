# Tools

11 tools in 3 families.
The names, descriptions and schemas are generated from the server itself, so they are exactly what the model receives in `tools/list`.

| Tool | Family | What it does |
| --- | --- | --- |
| [`connector_sources`](databases.md#connector_sources) | Databases | List the company databases declared on the stand: each source's name, engine (mongodb, sqlserver or postgresql), database, the collections an agent may read … |
| [`connector_schema`](databases.md#connector_schema) | Databases | Read what a source holds before querying it. |
| [`connector_find`](databases.md#connector_find) | Databases | Read documents of a collection that match a filter, with the fields and order asked for. |
| [`connector_aggregate`](databases.md#connector_aggregate) | Databases | Run a MongoDB aggregation pipeline on a collection to group, sum, count and join — the way to analyse rather than list. |
| [`connector_count`](databases.md#connector_count) | Databases | Count the documents of a collection that match a filter, written as MongoDB Extended JSON, such as {"status": "paid", "total": {"$gt": 100}}; dates as {"$dat… |
| [`connector_query`](databases.md#connector_query) | Databases | Run one SELECT on a SQL source — PostgreSQL or SQL Server — to search, join, group and sum. |
| [`connector_export`](databases.md#connector_export) | Databases | Write rows into a csv or xlsx file of the task, under exports/ of the working copy, which the task's card offers a person to download. |
| [`connector_journal`](databases.md#connector_journal) | Databases | Read the queries this task already made — which source and collection, what was asked, how many rows came back, whether it failed — newest first, so a query … |
| [`connector_propose_write`](writes.md#connector_propose_write) | Writes | Propose a change to a company database — update, update_one, insert or delete — when the task needs one. |
| [`connector_apply`](writes.md#connector_apply) | Writes | Run a proposal the stand's owner allowed, once, as the source's writing user, and get back how many documents or rows it changed. |
| [`connector_diagnose`](diagnostics.md#connector_diagnose) | Diagnostics | Say whether the server can reach the stand's sources: its version, Vault, and how many sources are declared. |
