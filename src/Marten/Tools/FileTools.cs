using System.Text;
using System.Text.Json.Nodes;
using Marten.Storage;

namespace Marten.Tools;

public static class Paths {
	public static string Resolve(IToolContext ctx, string path) {
		if(path == "~" || path.StartsWith("~/"))
			path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
		return Path.GetFullPath(Path.Combine(ctx.WorkingDirectory, path));
	}
}

public sealed class ReadFileTool : ITool {
	public string Name => "read_file";
	public string Description =>
		"Read a text file, whole or a range of lines. Lines come back numbered (number, tab, text). " +
		"Reading a file is required before editing it, and records the version you saw.";
	public JsonObject Parameters => Schema.Object(
		("path", Schema.Str("File path, absolute or relative to your working directory.")),
		("start_line", Schema.Int("First line to return, 1-based. Null for the start of the file.", nullable: true)),
		("end_line", Schema.Int("Last line to return, inclusive. Null for the end of the file.", nullable: true)));

	public async Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var path = Paths.Resolve(ctx, args.ReqString("path"));
		if(Directory.Exists(path)) throw new ToolException($"{path} is a directory.");
		if(!File.Exists(path)) throw new ToolException($"{path} does not exist.");
		var bytes = await File.ReadAllBytesAsync(path, ct);
		if(bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).IndexOf((byte) 0) >= 0)
			throw new ToolException($"{path} looks like a binary file.");
		ctx.Files.Saw(path, bytes);
		var lines = Encoding.UTF8.GetString(bytes).Split('\n');
		if(lines.Length > 0 && lines[^1] == "") lines = lines[..^1];
		var start = Math.Max(1, args.OptInt("start_line") ?? 1);
		var end = Math.Min(lines.Length, args.OptInt("end_line") ?? lines.Length);
		if(lines.Length == 0) return new ToolOutput($"{path} is empty.");
		if(start > lines.Length) throw new ToolException($"{path} has only {lines.Length} lines.");
		var sb = new StringBuilder();
		var width = end.ToString().Length;
		for(var i = start; i <= end; i++)
			sb.Append(i.ToString().PadLeft(width)).Append('\t').Append(lines[i - 1].TrimEnd('\r')).Append('\n');
		if(start > 1 || end < lines.Length) sb.Append($"[lines {start}-{end} of {lines.Length}]\n");
		return new ToolOutput(sb.ToString());
	}
}

/// <summary>
/// Applies a list of anchored insertions, replacements and deletions to one file, all or nothing.
/// Anchors match exactly and must each match once in the file as it stood. The edit fails if the
/// file changed since the agent last read it.
/// </summary>
public sealed class EditFileTool : ITool {
	public string Name => "edit_file";
	public string Description =>
		"Edit one file atomically with a list of anchored edits. Each anchor is exact text that must occur exactly once " +
		"in the file as it stands now; all anchors are matched against that same original, so edits never see each other. " +
		"Ops: replace (anchor -> text), insert_before, insert_after, delete (removes the anchor; text is ignored). " +
		"Either every edit applies or none does, and a failure names the edit that missed. " +
		"You must have read the file (read_file) since it last changed. To create a new file, pass a single edit with op " +
		"'create' and the whole contents as text; the file must not exist.";
	public JsonObject Parameters => Schema.Object(
		("path", Schema.Str("File path, absolute or relative to your working directory.")),
		("edits", Schema.Array("Edits to apply together.", Schema.Object(
			("op", Schema.Enum("What to do at the anchor.", "replace", "insert_before", "insert_after", "delete", "create")),
			("anchor", Schema.Str("Exact text to find; must match once. Null only for create.", nullable: true)),
			("text", Schema.Str("New text. Null for delete.", nullable: true))))));

	public sealed record Edit(string Op, string? Anchor, string? Text);

	/// <summary>
	/// One lock per path, so the version check and the write are a single step for every agent in
	/// this process. Without it two agents can both pass the check and the second write erases the first.
	/// </summary>
	static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> PathLocks = new(StringComparer.Ordinal);

	public async Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var path = Paths.Resolve(ctx, args.ReqString("path"));
		var edits = (args["edits"] as JsonArray ?? throw new ToolException("edits must be a list."))
			.Select(e => e as JsonObject ?? throw new ToolException("Each edit must be an object."))
			.Select(e => new Edit(e.ReqString("op"), e.OptString("anchor"), e.OptString("text"))).ToList();
		if(edits.Count == 0) throw new ToolException("No edits given.");

		var gate = PathLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(ct);
		try {
			return await ApplyLocked(ctx, path, edits, ct);
		} finally {
			gate.Release();
		}
	}

	static async Task<ToolOutput> ApplyLocked(IToolContext ctx, string path, List<Edit> edits, CancellationToken ct) {

		if(edits.Any(e => e.Op == "create")) {
			if(edits.Count != 1) throw new ToolException("create must be the only edit.");
			if(File.Exists(path)) throw new ToolException($"{path} already exists. Read it and edit it instead.");
			var bytes = Encoding.UTF8.GetBytes(edits[0].Text ?? "");
			AtomicFile.Write(path, bytes);
			ctx.Files.Saw(path, bytes);
			return new ToolOutput($"Created {path} ({CountLines(edits[0].Text ?? "")} lines).");
		}

		if(!File.Exists(path)) throw new ToolException($"{path} does not exist. Use op 'create' to make it.");
		var original = await File.ReadAllBytesAsync(path, ct);
		var seen = ctx.Files.LastSeen(path);
		if(seen == null) throw new ToolException($"Read {path} before editing it.");
		if(seen != FileTracker.Hash(original)) throw new ToolException($"{path} changed since you last read it. Read it again, then retry.");

		var result = Apply(Encoding.UTF8.GetString(original), edits);
		var updated = Encoding.UTF8.GetBytes(result);
		AtomicFile.Write(path, updated);
		ctx.Files.Saw(path, updated);
		return new ToolOutput($"Applied {edits.Count} edit{(edits.Count == 1 ? "" : "s")} to {path}.");
	}

	static int CountLines(string s) => s.Length == 0 ? 0 : s.Count(c => c == '\n') + (s.EndsWith('\n') ? 0 : 1);

	/// <summary>Pure core of the editor; throws ToolException naming the first edit that can't apply.</summary>
	public static string Apply(string text, IReadOnlyList<Edit> edits) {
		var spans = new List<(int Start, int End, string Insert, int Index)>();
		for(var i = 0; i < edits.Count; i++) {
			var e = edits[i];
			var label = $"Edit {i + 1} ({e.Op})";
			if(string.IsNullOrEmpty(e.Anchor)) throw new ToolException($"{label} has no anchor.");
			var at = text.IndexOf(e.Anchor, StringComparison.Ordinal);
			if(at < 0) throw new ToolException($"{label}: anchor not found.");
			if(text.IndexOf(e.Anchor, at + 1, StringComparison.Ordinal) >= 0)
				throw new ToolException($"{label}: anchor matches more than once. Make it longer.");
			var end = at + e.Anchor.Length;
			spans.Add(e.Op switch {
				"replace" => (at, end, e.Text ?? throw new ToolException($"{label} needs text."), i),
				"delete" => (at, end, "", i),
				"insert_before" => (at, at, e.Text ?? throw new ToolException($"{label} needs text."), i),
				"insert_after" => (end, end, e.Text ?? throw new ToolException($"{label} needs text."), i),
				_ => throw new ToolException($"{label}: unknown op."),
			});
		}
		// Replacements may not overlap each other; insertions may sit at either edge of one.
		var ordered = spans.OrderBy(s => s.Start).ThenBy(s => s.End).ToList();
		for(var i = 1; i < ordered.Count; i++)
			if(ordered[i].Start < ordered[i - 1].End || (ordered[i].Start == ordered[i - 1].Start && ordered[i].End > ordered[i].Start && ordered[i - 1].End > ordered[i - 1].Start))
				throw new ToolException($"Edit {ordered[i].Index + 1} overlaps edit {ordered[i - 1].Index + 1}.");
		var sb = new StringBuilder(text);
		foreach(var s in ordered.AsEnumerable().Reverse())
			sb.Remove(s.Start, s.End - s.Start).Insert(s.Start, s.Insert);
		return sb.ToString();
	}
}
