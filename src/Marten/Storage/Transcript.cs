using System.Text;
using System.Text.Json;

namespace Marten.Storage;

public static class EntryKind {
	/// <summary>Something woke the agent; Reason says what. Opens a turn.</summary>
	public const string Wake = "wake";
	/// <summary>Input to the model from Sera, another agent or the runtime. From says who.</summary>
	public const string Message = "message";
	/// <summary>Text the model wrote.</summary>
	public const string Assistant = "assistant";
	/// <summary>Provider-specific reasoning. Replayed only to the same provider.</summary>
	public const string Reasoning = "reasoning";
	public const string ToolCall = "tool_call";
	public const string ToolResult = "tool_result";
	/// <summary>A model call failed. Shown in the UI, never sent to the model.</summary>
	public const string Error = "error";
	/// <summary>The turn ended and the agent went idle.</summary>
	public const string Idle = "idle";
	/// <summary>Old tool results (Seqs) are replaced in the model's view by a pointer to their full text.</summary>
	public const string Elide = "elide";
	/// <summary>Everything up to UpTo is replaced in the model's view by Text, a summary. Instructions holds the rebuilt system prompt.</summary>
	public const string Compaction = "compaction";
	/// <summary>The system prompt was rebuilt because a hand-edited prompt changed. Instructions holds it; never sent to the model.</summary>
	public const string Reload = "reload";
}

/// <summary>
/// One line of an agent's append-only log. The log is backend-neutral: each backend converts
/// it to its own wire format, and keeps its raw items in Raw so they can be replayed verbatim
/// to the same provider.
/// </summary>
public sealed record Entry {
	public long Seq { get; init; }
	public DateTimeOffset Ts { get; init; }
	public required string Kind { get; init; }
	public string? From { get; init; }
	public string? Text { get; init; }
	public string? Reason { get; init; }
	public string? CallId { get; init; }
	public string? Name { get; init; }
	public string? Namespace { get; init; }
	public string? Arguments { get; init; }
	public bool? IsError { get; init; }
	/// <summary>Path, relative to the agent directory, of the full output when Text was truncated.</summary>
	public string? FullOutput { get; init; }
	public string? Provider { get; init; }
	public string? Model { get; init; }
	public JsonElement? Raw { get; init; }
	public JsonElement? Usage { get; init; }
	public List<long>? Seqs { get; init; }
	public long? UpTo { get; init; }
	public string? Instructions { get; init; }
	/// <summary>Images a tool result shows the model (MCP tools, View-Image): absolute paths of saved files.</summary>
	public List<ImageRef>? Images { get; init; }
}

public sealed record ImageRef(string Path, string MimeType);

public sealed record TranscriptPage(IReadOnlyList<Entry> Entries, bool HasOlder);

/// <summary>
/// Append-only JSONL log split into segments, so a long-lived agent never rewrites a big file.
/// A torn last line from a crash is dropped on load.
/// </summary>
public sealed class Transcript {
	const long SegmentBytes = 4 << 20;
	readonly string Dir;
	readonly List<Entry> entries = new();
	readonly object gate = new();
	string segmentPath;

	public Transcript(string dir) {
		Dir = dir;
		Directory.CreateDirectory(dir);
		var segments = Directory.GetFiles(dir, "*.jsonl").Order(StringComparer.Ordinal).ToList();
		foreach(var seg in segments)
			foreach(var line in File.ReadLines(seg)) {
				if(string.IsNullOrWhiteSpace(line)) continue;
				try {
					if(JsonIO.Deserialize<Entry>(line) is { } e) entries.Add(e);
				} catch(JsonException) when(seg == segments[^1]) {
					// A crash mid-append leaves a torn final line; everything before it is intact.
				}
			}
		segmentPath = segments.Count > 0 ? segments[^1] : SegmentName(1);
	}

	string SegmentName(int n) => Path.Combine(Dir, $"{n:D6}.jsonl");

	public IReadOnlyList<Entry> Entries {
		get { lock(gate) return entries.ToList(); }
	}

	public TranscriptPage ReadPage(int limit, long? before = null) {
		lock(gate) {
			var end = entries.Count;
			if(before is long cursor) {
				// Find the first entry at or beyond the exclusive cursor.
				var lo = 0;
				while(lo < end) {
					var mid = lo + (end - lo) / 2;
					if(entries[mid].Seq < cursor) lo = mid + 1;
					else end = mid;
				}
			}
			var count = Math.Min(end, Math.Clamp(limit, 1, 100));
			return new TranscriptPage(entries.GetRange(end - count, count), end > count);
		}
	}

	public Entry? Last {
		get { lock(gate) return entries.Count > 0 ? entries[^1] : null; }
	}

	public Entry Append(Entry entry) {
		lock(gate) {
			entry = entry with {
				Seq = entries.Count > 0 ? entries[^1].Seq + 1 : 1,
				Ts = entry.Ts == default ? DateTimeOffset.UtcNow : entry.Ts,
			};
			if(File.Exists(segmentPath) && new FileInfo(segmentPath).Length > SegmentBytes)
				segmentPath = SegmentName(int.Parse(Path.GetFileNameWithoutExtension(segmentPath)) + 1);
			using(var fs = new FileStream(segmentPath, FileMode.Append, FileAccess.Write, FileShare.Read)) {
				// Make sure a torn line from a crash doesn't glue itself onto this one.
				if(fs.Length > 0) {
					using var check = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
					check.Seek(-1, SeekOrigin.End);
					if(check.ReadByte() != '\n') fs.WriteByte((byte) '\n');
				}
				fs.Write(Encoding.UTF8.GetBytes(JsonIO.Serialize(entry) + "\n"));
				fs.Flush(flushToDisk: true);
			}
			entries.Add(entry);
			return entry;
		}
	}
}
