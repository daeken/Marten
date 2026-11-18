using System.Text;
using System.Text.RegularExpressions;
using Marten.Storage;

namespace Marten.Projects;

public sealed record ProjectInfo {
	public required string Id { get; init; }
	public required string Name { get; init; }
	public required DateTimeOffset Created { get; init; }
	/// <summary>Directories or repos the project works on. Agents are told about them, and their AGENTS.md files are read.</summary>
	public required List<string> Bindings { get; init; }
	/// <summary>Default for spawning task agents into their own git worktree.</summary>
	public bool WorktreesByDefault { get; init; }
	public string? OrchestratorId { get; init; }
	/// <summary>Every agent in a paused project holds, as if paused itself.</summary>
	public bool Paused { get; init; }
}

public sealed record MemoryEdit(DateTimeOffset At, string AgentId, string? Reason, List<string> Added, List<string> Removed) {
	// Absent in older audit records, which all refer to MEMORY.md.
	public string? Path { get; init; }
}

/// <summary>A project's place in ~/.marten: project.json, prompt.md, MEMORY.md and the memory audit log.</summary>
public sealed partial class Project {
	public ProjectInfo Info { get; private set; }
	public string Directory { get; }
	readonly object gate = new();

	public Project(string directory, ProjectInfo info) {
		Directory = directory;
		Info = info;
	}

	public string PromptPath => Path.Combine(Directory, "prompt.md");
	public string MemoryPath => Path.Combine(Directory, "MEMORY.md");
	public string MemoryTopicsDirectory => Path.Combine(Directory, "memory");
	string MemoryLogPath => Path.Combine(Directory, "memory-log.jsonl");
	public string WorktreesDir => Path.Combine(Directory, "worktrees");

	public static Project Create(string projectsDir, string name, IEnumerable<string> bindings, string? prompt, bool worktrees) {
		var slug = SlugRegex().Replace(name.ToLowerInvariant(), "-").Trim('-');
		if(slug.Length == 0) slug = "project";
		if(slug.Length > 32) slug = slug[..32].TrimEnd('-');
		var id = $"{slug}-{Guid.NewGuid().ToString("N")[..4]}";
		var dir = Path.Combine(projectsDir, id);
		System.IO.Directory.CreateDirectory(dir);
		var info = new ProjectInfo {
			Id = id, Name = name.Trim(), Created = DateTimeOffset.UtcNow, Bindings = bindings.ToList(), WorktreesByDefault = worktrees,
		};
		var project = new Project(dir, info);
		AtomicFile.Write(project.PromptPath, prompt?.Trim() is { Length: > 0 } p ? p + "\n" : "");
		AtomicFile.Write(project.MemoryPath, "");
		project.Save();
		return project;
	}

	public static Project? Load(string dir) {
		var path = Path.Combine(dir, "project.json");
		return File.Exists(path) && JsonIO.Load<ProjectInfo?>(path, () => null) is { } info ? new Project(dir, info) : null;
	}

	public void Update(Func<ProjectInfo, ProjectInfo> change) {
		lock(gate) {
			Info = change(Info);
			Save();
		}
	}

	void Save() => JsonIO.Save(Path.Combine(Directory, "project.json"), Info);

	public string Prompt => File.Exists(PromptPath) ? File.ReadAllText(PromptPath).Trim() : "";
	public string Memory => !Tools.Paths.IsLink(MemoryPath) && File.Exists(MemoryPath) ? File.ReadAllText(MemoryPath) : "";

	/// <summary>Only direct Markdown files, never links or their targets. No topic content is read here.</summary>
	public List<string> MemoryTopics() {
		var dir = new DirectoryInfo(MemoryTopicsDirectory);
		if(dir.LinkTarget != null || !dir.Exists) return [];
		try {
			return dir.EnumerateFiles()
				.Where(f => f.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase) && f.LinkTarget == null)
				.Select(f => $"memory/{f.Name}").Order(StringComparer.Ordinal).ToList();
		} catch(DirectoryNotFoundException) {
			return [];
		}
	}

	public void LogMemoryEdit(MemoryEdit edit) {
		lock(gate) File.AppendAllText(MemoryLogPath, JsonIO.Serialize(edit) + "\n", Encoding.UTF8);
	}

	public List<MemoryEdit> MemoryLog() {
		if(!File.Exists(MemoryLogPath)) return [];
		var edits = new List<MemoryEdit>();
		foreach(var line in File.ReadLines(MemoryLogPath))
			try {
				if(JsonIO.Deserialize<MemoryEdit>(line) is { } e) edits.Add(e);
			} catch(System.Text.Json.JsonException) { }
		return edits;
	}

	/// <summary>AGENTS.md at the root of each bound location, the usual place for repo instructions.</summary>
	public IEnumerable<(string Path, string Text)> AgentsFiles() {
		foreach(var binding in Info.Bindings) {
			var path = Path.Combine(binding, "AGENTS.md");
			if(File.Exists(path)) yield return (path, File.ReadAllText(path).Trim());
		}
	}

	[GeneratedRegex("[^a-z0-9]+")]
	private static partial Regex SlugRegex();
}

/// <summary>Line-level diff between two versions of a memory file, ignoring order.</summary>
public static class LineDiff {
	public static (List<string> Added, List<string> Removed) Of(string before, string after) {
		var b = Lines(before);
		var a = Lines(after);
		var removed = new List<string>(b);
		var added = new List<string>();
		foreach(var line in a)
			if(!removed.Remove(line)) added.Add(line);
		return (added, removed);
	}

	static List<string> Lines(string text) =>
		text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
}
