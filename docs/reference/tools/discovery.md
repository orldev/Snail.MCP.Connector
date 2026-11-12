# Finding and running tools

## <code>find_<wbr>tool</code>

Finds tools of this server for a task in words, best first, with the parameters of each. Ask it before deciding that no tool exists.

*read-only · repeat-safe · stays on this machine.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `task` | `string?` | — | `null` | What you are trying to do, in words. |
| `limit` | `integer` | — | `5` | How many tools to name, 1 to 20. |
| `intent` | `string?` | — | `null` | Same as task. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "task": {
      "description": "What you are trying to do, in words.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    },
    "limit": {
      "description": "How many tools to name, 1 to 20.",
      "type": "integer",
      "default": 5
    },
    "intent": {
      "description": "Same as task.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    }
  }
}
```

</details>

## <code>run_<wbr>tools</code>

Runs tools of this server in order in one call, stopping at the first failure; no step sees an earlier answer. An answer past the budget is flagged isOmitted: call that tool alone.

*may overwrite or delete · every call adds · stays on this machine.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `steps` | `array` | yes | — | The steps, in order. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "steps": {
      "description": "The steps, in order.",
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "tool": {
            "description": "The tool to call.",
            "type": [
              "string",
              "null"
            ]
          },
          "arguments": {
            "description": "The arguments of a direct call.",
            "type": [
              "object",
              "null"
            ],
            "default": null
          }
        },
        "required": [
          "tool"
        ]
      }
    }
  },
  "required": [
    "steps"
  ]
}
```

</details>

## <code>run_<wbr>program</code>

Run a JavaScript program whose functions are this server's commands, connector.&lt;command&gt;({ … }), with loops,
conditions and the results in hand. Use it when the work decides as it goes or takes many steps: the steps run
here, and only what the program returns comes back, instead of a call and a reply per step.
What the program returns comes back as result, log(…) writes lines that come back with it, and input is the object
passed alongside. A failed or refused command throws { error, message, hint, type, details }, which the program may
catch and act on; a command the server does not have stops the program with the names nearest to it. The program
reaches nothing but the commands: no files, no network, no clock but its own. The reply lists every command it sent.
Limits: 200 commands, 1000000 statements, calls nested 256 deep, 32 MB of memory, and 120 seconds unless timeoutSeconds gives up to 600.

*may overwrite or delete · every call adds · may reach outside services.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `code` | `string` | yes | — | The body of the program: JavaScript in strict mode, with return for whatever should come back as result. |
| `input` | `object?` | — | `null` | Values the program reads as input, so that the code itself can stay the same between calls. |
| `timeoutSeconds` | `integer?` | — | `null` | Seconds the program may run; the description of run_program states the default and the most it may be given. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "code": {
      "description": "The body of the program: JavaScript in strict mode, with return for whatever should come back as result.",
      "type": "string"
    },
    "input": {
      "description": "Values the program reads as input, so that the code itself can stay the same between calls.",
      "type": [
        "object",
        "null"
      ],
      "default": null
    },
    "timeoutSeconds": {
      "description": "Seconds the program may run; the description of run_program states the default and the most it may be given.",
      "type": [
        "integer",
        "null"
      ],
      "default": null
    }
  },
  "required": [
    "code"
  ]
}
```

</details>

