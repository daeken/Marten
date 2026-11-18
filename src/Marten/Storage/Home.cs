using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Marten.Storage;

/// <summary>Paths inside ~/.marten. Everything Marten keeps lives here as plain files.</summary>
public sealed class Home {
	public string Root { get; }

	public Home(string? root = null) {
		Root = Path.GetFullPath(root
			?? Environment.GetEnvironmentVariable("MARTEN_HOME")
			?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".marten"));
		Directory.CreateDirectory(Root);
		Directory.CreateDirectory(Secrets);
		Directory.CreateDirectory(Agents);
		Directory.CreateDirectory(Projects);
		Directory.CreateDirectory(Prompts);
		if(!OperatingSystem.IsWindows())
			File.SetUnixFileMode(Secrets, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
	}

	public string ConfigPath => Path.Combine(Root, "config.json");
	public string Secrets => Path.Combine(Root, "secrets");
	public string Agents => Path.Combine(Root, "agents");
	public string Projects => Path.Combine(Root, "projects");
	public string SeraInboxPath => Path.Combine(Root, "inbox.jsonl");
	public string Prompts => Path.Combine(Root, "prompts");
	/// <summary>Present while everything is paused.</summary>
	public string PausedPath => Path.Combine(Root, "paused");
	public string AgentDir(string id) => Path.Combine(Agents, id);
}

public static class JsonIO {
	public static readonly JsonSerializerOptions Options = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
	};
	public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

	public static string Serialize<T>(T value, bool indented = false) =>
		JsonSerializer.Serialize(value, indented ? Indented : Options);
	public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

	public static T Load<T>(string path, Func<T> fallback) =>
		File.Exists(path) ? Deserialize<T>(File.ReadAllText(path)) ?? fallback() : fallback();
	public static void Save<T>(string path, T value) => AtomicFile.Write(path, Serialize(value, indented: true));
}

public static class AtomicFile {
	/// <summary>Write via a temp file and rename, keeping the original's Unix mode.</summary>
	public static void Write(string path, string contents) => Write(path, Encoding.UTF8.GetBytes(contents));

	public static void Write(string path, byte[] contents) {
		var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
		Directory.CreateDirectory(dir);
		var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
		UnixFileMode? mode = null;
		if(!OperatingSystem.IsWindows() && File.Exists(path)) mode = File.GetUnixFileMode(path);
		using(var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write)) {
			fs.Write(contents);
			fs.Flush(flushToDisk: true);
		}
		if(mode is { } m) File.SetUnixFileMode(tmp, m);
		File.Move(tmp, path, overwrite: true);
	}
}
