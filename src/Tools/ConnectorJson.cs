using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Snail.MCP.Connector.Tools;

/// <summary>The types a job of this server keeps as its result, written by generated code.</summary>
[JsonSerializable(typeof(JsonObject))]
public sealed partial class ConnectorJson : JsonSerializerContext
{
}
