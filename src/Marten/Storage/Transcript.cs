using System.Buffers;
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
	/// <summary>Start a new model context after this entry; Instructions holds its rebuilt system prompt.</summary>
	public const string ContextReset = "context_reset";
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

[Flags]
internal enum RowKind {
	Other = 1, Wake = 2, Idle = 4, Error = 8, Call = 16, Result = 32, Reasoning = 64,
	Elide = 128, Compaction = 256, Reload = 512, Reset = 1024, Assistant = 2048,
	Instructions = Compaction | Reload | Reset,
	ViewChange = Elide | Instructions,
	All = -1,
}

internal sealed record CallRecord(string? CallId, string? Name);

/// <summary>Append-only JSONL. Only row locations and kind tags are resident, not the entries.</summary>
public sealed class Transcript {
	const long SegmentBytes = 4 << 20;
	readonly List<string> segments;
	readonly List<Row> rows = new();
	readonly object gate = new();
	bool damagedTail;
	bool appendFailed;

	readonly record struct Row(long Seq, long Offset, int Length, int Segment, RowKind Kind);

	static RowKind Tag(string kind) => kind switch {
		EntryKind.Wake => RowKind.Wake, EntryKind.Idle => RowKind.Idle, EntryKind.Error => RowKind.Error,
		EntryKind.ToolCall => RowKind.Call, EntryKind.ToolResult => RowKind.Result,
		EntryKind.Reasoning => RowKind.Reasoning, EntryKind.Elide => RowKind.Elide,
		EntryKind.Compaction => RowKind.Compaction, EntryKind.Reload => RowKind.Reload,
		EntryKind.ContextReset => RowKind.Reset, EntryKind.Assistant => RowKind.Assistant,
		_ => RowKind.Other,
	};

	static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

	public Transcript(string dir) {
		Directory.CreateDirectory(dir);
		segments = Directory.GetFiles(dir, "*.jsonl").Order(StringComparer.Ordinal).ToList();
		if(segments.Count == 0) { segments.Add(Path.Combine(dir, "000001.jsonl")); return; }
		var block = new byte[64 << 10];
		for(var i = 0; i < segments.Count; i++) IndexSegment(i, block);
	}

	void IndexSegment(int segment, byte[] block) {
		using var fs = OpenRead(segments[segment]);
		var partial = new ArrayBufferWriter<byte>();
		long offset = 0;
		int n;
		while((n = fs.Read(block)) != 0) {
			ReadOnlySpan<byte> rest = block.AsSpan(0, n);
			if(offset == 0 && partial.WrittenCount == 0 &&
				(rest.StartsWith(new byte[] { 0xff, 0xfe }) || rest.StartsWith(new byte[] { 0xfe, 0xff }) ||
				 rest.StartsWith(new byte[] { 0, 0, 0xfe, 0xff })))
				throw new InvalidDataException("Transcript segments must use UTF-8.");
			int newline;
			while((newline = rest.IndexOfAny((byte) '\n', (byte) '\r')) >= 0) {
				var line = rest[..newline];
				if(partial.WrittenCount != 0) {
					partial.Write(line);
					line = partial.WrittenSpan;
				}
				IndexLine(line, offset, segment);
				offset += line.Length + 1L;
				partial.ResetWrittenCount();
				rest = rest[(newline + 1)..];
			}
			partial.Write(rest);
		}
		if(partial.WrittenCount > 0) IndexLine(partial.WrittenSpan, offset, segment);
	}

	void IndexLine(ReadOnlySpan<byte> line, long offset, int segment) {
		if(offset == 0 && line.StartsWith("\uFEFF"u8)) { line = line[3..]; offset = 3; }
		if(line.IndexOfAnyExcept((byte) ' ', (byte) '\t', (byte) '\r') < 0) return;
		try {
			if(JsonSerializer.Deserialize<Entry>(line, JsonIO.Options) is { } e)
				rows.Add(new(e.Seq, offset, line.Length, segment, Tag(e.Kind)));
		} catch(JsonException) when(IsUnicodeWhitespace(line)) {
			// StreamReader-based transcripts also ignored Unicode-only blank lines.
		} catch(JsonException) when(segment == segments.Count - 1) {
			// Keep the damaged segment newest, so even the old reader can recover it.
			// Rolling it into the archive would make the next startup reject the log.
			damagedTail = true;
		}
	}

	static bool IsUnicodeWhitespace(ReadOnlySpan<byte> line) {
		while(!line.IsEmpty) {
			if(Rune.DecodeFromUtf8(line, out var rune, out var consumed) != OperationStatus.Done || !Rune.IsWhiteSpace(rune)) return false;
			line = line[consumed..];
		}
		return true;
	}

	// Rows are immutable once published. Each reader captures the current end, so an append
	// cannot leak into an already captured page/model request. Never hold gate across disk I/O.
	internal Snapshot Capture() { lock(gate) return new(this, rows.Count); }

	internal sealed class Snapshot(Transcript log, int count) {
		int Bound(long? seq, bool inclusive, int fallback) {
			if(seq == null) return fallback;
			lock(log.gate) {
				var lo = 0;
				var hi = count;
				while(lo < hi) {
					var mid = lo + (hi - lo) / 2;
					var value = log.rows[mid].Seq;
					if(value < seq.Value || inclusive && value == seq.Value) lo = mid + 1;
					else hi = mid;
				}
				return lo;
			}
		}

		Row GetRow(int index) { lock(log.gate) return log.rows[index]; }
		internal RowKind? LastKind => count == 0 ? null : GetRow(count - 1).Kind;

		IEnumerable<Row> Select(RowKind kinds, long? after, long? before = null, bool reverse = false) {
			var start = Bound(after, inclusive: true, 0);
			var end = Bound(before, inclusive: false, count);
			for(var i = reverse ? end - 1 : start; reverse ? i >= start : i < end; i += reverse ? -1 : 1) {
				var row = GetRow(i);
				if((row.Kind & kinds) != 0) yield return row;
			}
		}

		internal long? LastSeq(RowKind kinds, long? after = null) => Select(kinds, after, reverse: true).Select(r => (long?) r.Seq).FirstOrDefault();
		internal Entry? ReadLast(RowKind kinds, long? after = null) => ReadRows<Entry>(Select(kinds, after, reverse: true).Take(1)).FirstOrDefault();
		internal IEnumerable<Entry> Read(RowKind kinds = RowKind.All, long? after = null) => ReadRows<Entry>(Select(kinds, after));
		internal IEnumerable<CallRecord> ReadCalls(RowKind kinds, long? after) => ReadRows<CallRecord>(Select(kinds, after));
		internal TranscriptPage Tail(int limit, RowKind kinds = RowKind.All, long? after = null, long? before = null) {
			var (entries, older) = Tail(limit, static e => e, kinds, after, before);
			return new(entries, older);
		}

		internal (IReadOnlyList<T> Entries, bool HasOlder) Tail<T>(int limit, Func<Entry, T> project, RowKind kinds = RowKind.All,
			long? after = null, long? before = null) {
			var tail = Select(kinds, after, before, reverse: true).Take(limit + 1).ToList();
			return (ReadRows<Entry>(tail.Take(limit).Reverse()).Select(project).ToList(), tail.Count > limit);
		}

		IEnumerable<T> ReadRows<T>(IEnumerable<Row> selected) {
			FileStream? fs = null;
			var segment = -1;
			try {
				foreach(var row in selected) {
					if(segment != row.Segment) {
						fs?.Dispose();
						string path;
						lock(log.gate) path = log.segments[row.Segment];
						fs = OpenRead(path);
						segment = row.Segment;
					}
					yield return ReadRow<T>(fs!, row);
				}
			} finally { fs?.Dispose(); }
		}

		static T ReadRow<T>(FileStream fs, Row row) {
			// Do not pool exceptionally large rows: those buffers could outlive the request.
			var pooled = row.Length <= 1 << 20;
			var bytes = pooled ? ArrayPool<byte>.Shared.Rent(row.Length) : new byte[row.Length];
			try {
				if(fs.Position != row.Offset) fs.Position = row.Offset;
				var line = bytes.AsSpan(0, row.Length);
				fs.ReadExactly(line);
				return JsonSerializer.Deserialize<T>(line, JsonIO.Options)!;
			} finally { if(pooled) ArrayPool<byte>.Shared.Return(bytes); }
		}
	}

	public IReadOnlyList<Entry> Entries => Capture().Read().ToList();
	public TranscriptPage ReadPage(int limit, long? before = null) => Capture().Tail(Math.Clamp(limit, 1, 100), before: before);
	public Entry? Last => Capture().ReadLast(RowKind.All);

	public Entry Append(Entry entry) => Append(entry, static (fs, bytes) => { fs.Write(bytes); fs.WriteByte((byte) '\n'); });

	internal Entry Append(Entry entry, Action<FileStream, byte[]> write) {
		lock(gate) {
			if(appendFailed) throw new IOException("A previous transcript append failed; restart to re-index before appending again.");
			entry = entry with {
				Seq = rows.Count > 0 ? rows[^1].Seq + 1 : 1,
				Ts = entry.Ts == default ? DateTimeOffset.UtcNow : entry.Ts,
			};
			var path = segments[^1];
			if(!damagedTail && File.Exists(path) && new FileInfo(path).Length > SegmentBytes) {
				path = Path.Combine(Path.GetDirectoryName(path)!, $"{int.Parse(Path.GetFileNameWithoutExtension(path)) + 1:D6}.jsonl");
				segments.Add(path);
			}
			var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonIO.Options);
			long offset;
			try {
				using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
				if(fs.Length > 0) {
					using var check = OpenRead(path);
					check.Seek(-1, SeekOrigin.End);
					if(check.ReadByte() != '\n') fs.WriteByte((byte) '\n');
				}
				offset = fs.Position;
				write(fs, bytes);
				fs.Flush(flushToDisk: true);
			} catch(Exception e) when(e is IOException or UnauthorizedAccessException) {
				// A failed write/flush may have left a partial or even complete line. Do not
				// append a separator or reuse its sequence until startup has inspected it.
				appendFailed = true;
				throw;
			}
			rows.Add(new(entry.Seq, offset, bytes.Length, segments.Count - 1, Tag(entry.Kind)));
			return entry;
		}
	}
}
