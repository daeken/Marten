using System.Text.Json;
using Marten.Agents;
using Marten.Storage;

namespace Marten.Web;

internal static class ArtifactTranscriptProjection {
	public static Entry Outside(Entry entry, Transcript log) {
		if(entry.Artifacts == null && !PrivateCall(entry) && !log.IsArtifactCall(entry.CallId)) return entry;
		return entry with { Artifacts = null, Arguments = null, Raw = null, FullOutput = null,
			Text = entry.Kind == EntryKind.ToolResult ? "Operator artifact result." : null };
	}

	internal static bool PrivateCall(Entry entry) => entry.Name == PublishArtifactTool.ToolName ||
		(entry.Kind == EntryKind.ToolCall && entry.Name == "call_tool" && TargetsArtifact(entry.Arguments));

	static bool TargetsArtifact(string? text) {
		if(text == null) return false;
		try {
			using var doc = JsonDocument.Parse(text);
			return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("name", out var target) &&
				target.ValueKind == JsonValueKind.String && target.GetString() == PublishArtifactTool.ToolName;
		} catch(JsonException) { return false; }
	}
}
