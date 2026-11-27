using System.Text.Json;
using Marten.Agents;
using Marten.Storage;

namespace Marten.Web;

internal static class ArtifactTranscriptProjection {
	public static Entry Outside(Entry entry) {
		if(entry.Artifacts == null && entry.Name != PublishArtifactTool.ToolName &&
			!(entry.Name == "call_tool" && (ContainsName(entry.Arguments) || ContainsName(entry.Raw)))) return entry;
		return entry with { Artifacts = null, Arguments = null, Raw = null, FullOutput = null,
			Text = entry.Kind == EntryKind.ToolResult ? "Operator artifact result." : null };
	}

	static bool ContainsName(string? text) {
		if(text == null) return false;
		// Normal JSON arguments, plus serialized arguments nested in provider replay payloads.
		if(text.Contains(PublishArtifactTool.ToolName, StringComparison.Ordinal)) return true;
		try { using var doc = JsonDocument.Parse(text); return ContainsName(doc.RootElement, 0); }
		catch(JsonException) { return false; }
	}

	static bool ContainsName(JsonElement? value) => value is { } node && ContainsName(node, 0);
	static bool ContainsName(JsonElement node, int depth) {
		if(depth >= 8) return false;
		return node.ValueKind switch {
			JsonValueKind.Object => node.EnumerateObject().Any(p => ContainsName(p.Value, depth + 1)),
			JsonValueKind.Array => node.EnumerateArray().Any(v => ContainsName(v, depth + 1)),
			JsonValueKind.String => node.GetString()?.Contains(PublishArtifactTool.ToolName, StringComparison.Ordinal) == true,
			_ => false,
		};
	}
}
