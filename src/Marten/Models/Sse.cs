using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Marten.Models;

public static class Sse {
	/// <summary>Parse server-sent events into their JSON data payloads. Handles CRLF and multi-line data.</summary>
	public static async IAsyncEnumerable<JsonObject> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct) {
		using var reader = new StreamReader(stream, Encoding.UTF8);
		var data = new StringBuilder();
		while(true) {
			var line = await reader.ReadLineAsync(ct);
			if(line == null || line.Length == 0) {
				if(data.Length > 0) {
					var text = data.ToString();
					data.Clear();
					if(text != "[DONE]" && JsonNode.Parse(text) is JsonObject obj) yield return obj;
				}
				if(line == null) yield break;
				continue;
			}
			if(line.StartsWith("data:")) {
				if(data.Length > 0) data.Append('\n');
				data.Append(line.AsSpan(5).TrimStart(' '));
			}
		}
	}
}
