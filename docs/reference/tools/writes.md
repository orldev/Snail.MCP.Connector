# Writes

## <code>connector_<wbr>propose_<wbr>write</code>

Propose a change to a company database — update, update_one, insert or delete — when the task needs one. Nothing is written: the server
dry-runs it as the read-only user, counts the documents it touches and keeps a few of them as they are now, and
answers a proposal id. Then call request_write_approval with server "connector" and that proposal: the task waits
until the stand's owner reads the proposal and allows or refuses it. An update or a delete needs a filter naming
the documents; an update is field operators such as $set; one proposal touches at most 1000 documents. The source
must have a writing user.

*only adds · every call adds · stays on this machine.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `source` | `string` | yes | — | The source's name, as connector_sources lists it. |
| `collection` | `string` | yes | — | The collection, as connector_schema lists it. |
| `operation` | `string` | yes | — | What the write does: update, update_one, insert or delete. |
| `reason` | `string` | yes | — | Why the task needs this write, in one or two sentences for the person who decides. |
| `filter` | `string?` | — | `null` | Which documents an update or a delete touches, such as {"number": "2026-0042"}; it may not be empty. Not used by an insert. |
| `change` | `string?` | — | `null` | For an update, the field operators, such as {"$set": {"status": "paid"}}; for an insert, a JSON array of the documents; not used by a delete. |

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
    "operation": {
      "description": "What the write does: update, update_one, insert or delete.",
      "type": "string"
    },
    "reason": {
      "description": "Why the task needs this write, in one or two sentences for the person who decides.",
      "type": "string"
    },
    "filter": {
      "description": "Which documents an update or a delete touches, such as {\u0022number\u0022: \u00222026-0042\u0022}; it may not be empty. Not used by an insert.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    },
    "change": {
      "description": "For an update, the field operators, such as {\u0022$set\u0022: {\u0022status\u0022: \u0022paid\u0022}}; for an insert, a JSON array of the documents; not used by a delete.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    }
  },
  "required": [
    "source",
    "collection",
    "operation",
    "reason"
  ]
}
```

</details>

## <code>connector_<wbr>apply</code>

Run a proposal the stand's owner allowed, once, as the source's writing user, and get back how many documents it
changed. Only the task that proposed it may run it, and only within 7 days; a proposal nobody allowed yet, refused,
or already run is refused with what to do.

*may overwrite or delete · every call adds · stays on this machine.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `proposal` | `string` | yes | — | The proposal id connector_propose_write answered. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "proposal": {
      "description": "The proposal id connector_propose_write answered.",
      "type": "string"
    }
  },
  "required": [
    "proposal"
  ]
}
```

</details>

