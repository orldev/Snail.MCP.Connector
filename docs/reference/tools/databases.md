# Databases

## <code>connector_<wbr>sources</code>

List the company databases declared on the stand: each source's name, engine (mongodb, sqlserver or postgresql), database, the collections
an agent may read and what a person wrote about it. Start here, then read a source's shape with connector_schema.

*read-only · repeat-safe · stays on this machine.*

Takes no parameters.

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {}
}
```

</details>

## <code>connector_<wbr>schema</code>

Read what a source holds before querying it. Without a collection: the collections or tables an agent may read.
With one: for MongoDB, its fields read from a sample of 200 documents — each field's dotted path, its types, how
often it is present and an example value; for SQL, its columns with their types and an example value. Both answer
how many rows it holds and its indexes, which are what a query is quick on.

*read-only · repeat-safe · stays on this machine.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `source` | `string` | yes | — | The source's name, as connector_sources lists it. |
| `collection` | `string?` | — | `null` | The collection whose fields to read; without one, the collections an agent may read. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "source": {
      "description": "The source\u0027s name, as connector_sources lists it.",
      "type": "string"
    },
    "collection": {
      "description": "The collection whose fields to read; without one, the collections an agent may read.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    }
  },
  "required": [
    "source"
  ]
}
```

</details>

## <code>connector_<wbr>find</code>

Read documents of a collection that match a filter, with the fields and order asked for. Write the filter,
projection and sort as MongoDB Extended JSON, such as {"status": "paid", "total": {"$gt": 100}}; dates as {"$date": "2026-01-31T00:00:00Z"}, ids as {"$oid": "…"}. An answer carries 50 documents unless asked for more, at most 200, and at
most 65536 bytes of them; when it was cut it says so — narrow the filter or count first. A query gets 30 seconds.
Only reads: operators that write or run code are refused.

*read-only · repeat-safe · stays on this machine.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `source` | `string` | yes | — | The source's name, as connector_sources lists it. |
| `collection` | `string` | yes | — | The collection, as connector_schema lists it. |
| `filter` | `string?` | — | `null` | Which documents, as MongoDB Extended JSON, such as {"status": "paid", "total": {"$gt": 100}}; dates as {"$date": "2026-01-31T00:00:00Z"}, ids as {"$oid": "…"}; empty matches every one. |
| `projection` | `string?` | — | `null` | Which fields to return, such as {"number": 1, "total": 1, "_id": 0}; empty returns them all. |
| `sort` | `string?` | — | `null` | The order, such as {"date": -1}; empty leaves the database's order. |
| `limit` | `integer?` | — | `null` | How many rows to return: 50 when not given, at most 200. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "source": {
      "description": "The source\u0027s name, as connector_sources lists it.",
      "type": "string"
    },
    "collection": {
      "description": "The collection, as connector_schema lists it.",
      "type": "string"
    },
    "filter": {
      "description": "Which documents, as MongoDB Extended JSON, such as {\u0022status\u0022: \u0022paid\u0022, \u0022total\u0022: {\u0022$gt\u0022: 100}}; dates as {\u0022$date\u0022: \u00222026-01-31T00:00:00Z\u0022}, ids as {\u0022$oid\u0022: \u0022\u2026\u0022}; empty matches every one.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    },
    "projection": {
      "description": "Which fields to return, such as {\u0022number\u0022: 1, \u0022total\u0022: 1, \u0022_id\u0022: 0}; empty returns them all.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    },
    "sort": {
      "description": "The order, such as {\u0022date\u0022: -1}; empty leaves the database\u0027s order.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    },
    "limit": {
      "description": "How many rows to return: 50 when not given, at most 200.",
      "type": [
        "integer",
        "null"
      ],
      "default": null
    }
  },
  "required": [
    "source",
    "collection"
  ]
}
```

</details>

## <code>connector_<wbr>aggregate</code>

Run a MongoDB aggregation pipeline on a collection to group, sum, count and join — the way to analyse rather than
list. Write the pipeline as a JSON array of stages in MongoDB Extended JSON, such as {"status": "paid", "total": {"$gt": 100}}; dates as {"$date": "2026-01-31T00:00:00Z"}, ids as {"$oid": "…"}. $lookup, $graphLookup and $unionWith may
read only collections the source allows; $out, $merge, $function, $accumulator and $where are refused. The answer
carries 50 rows unless asked for more, at most 200, at most 65536 bytes; a query gets 30 seconds.

*read-only · repeat-safe · stays on this machine.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `source` | `string` | yes | — | The source's name, as connector_sources lists it. |
| `collection` | `string` | yes | — | The collection, as connector_schema lists it. |
| `pipeline` | `string` | yes | — | The stages as a JSON array, such as [{"$match": {"status": "paid"}}, {"$group": {"_id": "$customer", "total": {"$sum": "$total"}}}]. |
| `limit` | `integer?` | — | `null` | How many rows to return: 50 when not given, at most 200. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "source": {
      "description": "The source\u0027s name, as connector_sources lists it.",
      "type": "string"
    },
    "collection": {
      "description": "The collection, as connector_schema lists it.",
      "type": "string"
    },
    "pipeline": {
      "description": "The stages as a JSON array, such as [{\u0022$match\u0022: {\u0022status\u0022: \u0022paid\u0022}}, {\u0022$group\u0022: {\u0022_id\u0022: \u0022$customer\u0022, \u0022total\u0022: {\u0022$sum\u0022: \u0022$total\u0022}}}].",
      "type": "string"
    },
    "limit": {
      "description": "How many rows to return: 50 when not given, at most 200.",
      "type": [
        "integer",
        "null"
      ],
      "default": null
    }
  },
  "required": [
    "source",
    "collection",
    "pipeline"
  ]
}
```

</details>

## <code>connector_<wbr>count</code>

Count the documents of a collection that match a filter, written as MongoDB Extended JSON, such as {"status": "paid", "total": {"$gt": 100}}; dates as {"$date": "2026-01-31T00:00:00Z"}, ids as {"$oid": "…"}; an empty filter counts them
all. Cheaper than reading them: count before reading a large collection.

*read-only · repeat-safe · stays on this machine.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `source` | `string` | yes | — | The source's name, as connector_sources lists it. |
| `collection` | `string` | yes | — | The collection, as connector_schema lists it. |
| `filter` | `string?` | — | `null` | Which documents, as MongoDB Extended JSON, such as {"status": "paid", "total": {"$gt": 100}}; dates as {"$date": "2026-01-31T00:00:00Z"}, ids as {"$oid": "…"}; empty matches every one. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "source": {
      "description": "The source\u0027s name, as connector_sources lists it.",
      "type": "string"
    },
    "collection": {
      "description": "The collection, as connector_schema lists it.",
      "type": "string"
    },
    "filter": {
      "description": "Which documents, as MongoDB Extended JSON, such as {\u0022status\u0022: \u0022paid\u0022, \u0022total\u0022: {\u0022$gt\u0022: 100}}; dates as {\u0022$date\u0022: \u00222026-01-31T00:00:00Z\u0022}, ids as {\u0022$oid\u0022: \u0022\u2026\u0022}; empty matches every one.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    }
  },
  "required": [
    "source",
    "collection"
  ]
}
```

</details>

## <code>connector_<wbr>query</code>

Run one SELECT on a SQL source — PostgreSQL or SQL Server — to search, join, group and sum. The statement is a
SELECT or a WITH ending in one; anything that writes or changes the database is refused, and it runs as the
read-only user in a transaction that is rolled back. Tables after FROM and JOIN must be ones the source allows.
The answer carries 50 rows unless asked for more, at most 200, at most 65536 bytes; a query gets 30 seconds.
For MongoDB sources use connector_find and connector_aggregate.

*read-only · repeat-safe · stays on this machine.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `source` | `string` | yes | — | The source's name, as connector_sources lists it. |
| `select` | `string` | yes | — | The SELECT, such as SELECT c.city, sum(o.total) FROM orders o JOIN customers c ON c.id = o.customer_id GROUP BY c.city. |
| `limit` | `integer?` | — | `null` | How many rows to return: 50 when not given, at most 200. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "source": {
      "description": "The source\u0027s name, as connector_sources lists it.",
      "type": "string"
    },
    "select": {
      "description": "The SELECT, such as SELECT c.city, sum(o.total) FROM orders o JOIN customers c ON c.id = o.customer_id GROUP BY c.city.",
      "type": "string"
    },
    "limit": {
      "description": "How many rows to return: 50 when not given, at most 200.",
      "type": [
        "integer",
        "null"
      ],
      "default": null
    }
  },
  "required": [
    "source",
    "select"
  ]
}
```

</details>

## <code>connector_<wbr>export</code>

Write rows into a csv or xlsx file of the task, under exports/ of the working copy, which the task's card offers a
person to download. For MongoDB, give a collection and either a filter or a pipeline; nested fields become columns
of their dotted paths. For SQL, give a select. An export writes at most 100000 rows and says when it stopped there,
and at most 1000 columns: the fields past them go into one column, _more, as JSON.

*only adds · repeat-safe · stays on this machine.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `source` | `string` | yes | — | The source's name, as connector_sources lists it. |
| `format` | `string` | yes | — | The file's format: csv or xlsx. |
| `file` | `string` | yes | — | The file's name, letters, digits, dots, dashes and underscores; the format's extension is added when missing. |
| `collection` | `string?` | — | `null` | For MongoDB, the collection, as connector_schema lists it; not used for SQL. |
| `filter` | `string?` | — | `null` | Which documents, as MongoDB Extended JSON, such as {"status": "paid", "total": {"$gt": 100}}; dates as {"$date": "2026-01-31T00:00:00Z"}, ids as {"$oid": "…"}; empty matches every one. |
| `pipeline` | `string?` | — | `null` | For MongoDB, the stages as a JSON array, when the rows come from a pipeline rather than a filter. |
| `select` | `string?` | — | `null` | For SQL, the SELECT whose rows the file holds; not used for MongoDB. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "source": {
      "description": "The source\u0027s name, as connector_sources lists it.",
      "type": "string"
    },
    "format": {
      "description": "The file\u0027s format: csv or xlsx.",
      "type": "string"
    },
    "file": {
      "description": "The file\u0027s name, letters, digits, dots, dashes and underscores; the format\u0027s extension is added when missing.",
      "type": "string"
    },
    "collection": {
      "description": "For MongoDB, the collection, as connector_schema lists it; not used for SQL.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    },
    "filter": {
      "description": "Which documents, as MongoDB Extended JSON, such as {\u0022status\u0022: \u0022paid\u0022, \u0022total\u0022: {\u0022$gt\u0022: 100}}; dates as {\u0022$date\u0022: \u00222026-01-31T00:00:00Z\u0022}, ids as {\u0022$oid\u0022: \u0022\u2026\u0022}; empty matches every one.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    },
    "pipeline": {
      "description": "For MongoDB, the stages as a JSON array, when the rows come from a pipeline rather than a filter.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    },
    "select": {
      "description": "For SQL, the SELECT whose rows the file holds; not used for MongoDB.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    }
  },
  "required": [
    "source",
    "format",
    "file"
  ]
}
```

</details>

## <code>connector_<wbr>journal</code>

Read the queries this task already made — which source and collection, what was asked, how many rows came back,
whether it failed — newest first, so a query is not repeated. Every query is written there, for the company to see.

*read-only · repeat-safe · stays on this machine.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `most` | `integer` | — | `20` | How many queries to return, newest first: at most 200. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "most": {
      "description": "How many queries to return, newest first: at most 200.",
      "type": "integer",
      "default": 20
    }
  }
}
```

</details>

