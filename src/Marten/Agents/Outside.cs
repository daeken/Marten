using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Agents;

/// <summary>An agent outside Marten (a Claude Code session, say) that Sera gave a token.</summary>
public sealed record OutsideClient(string Name, string TokenHash, DateTimeOffset Created);

/// <summary>A reply waiting for a client: header lines, a "---" line, then the text.</summary>
public sealed record MailMessage(string File, Dictionary<string, string> Headers, string Text) {
	public string? Header(string key) => Headers.GetValueOrDefault(key);
}

/// <summary>
/// Agents outside Marten. Each gets a token (secrets/clients/NAME.token) for the /api/ext routes. Replies from
/// Marten agents wait in outside/NAME/inbox/ until the client reads them through the API.
/// To Marten agents they're peers named "NAME (outside)", never Sera.
/// </summary>
public sealed class Outside(AgentManager manager) {
	static readonly Regex ValidName = new("^[a-z0-9][a-z0-9-]{0,39}$");
	public const string Prefix = "client:";
	readonly object gate = new();
	List<OutsideClient>? clients;
	DateTime clientsStamp;
	int seq;

	string ClientsPath => Path.Combine(manager.Home.Root, "clients.json");
	public string TokenDir => Path.Combine(manager.Home.Secrets, "clients");
	public string TokenPath(string name) => Path.Combine(TokenDir, name + ".token");
	public string InDir(string name) => Path.Combine(manager.Home.Root, "outside", name, "inbox");
	public static string Label(string name) => $"{name} (outside)";

	public IReadOnlyList<OutsideClient> Clients {
		get {
			lock(gate) {
				var stamp = File.Exists(ClientsPath) ? File.GetLastWriteTimeUtc(ClientsPath) : DateTime.MinValue;
				if(clients == null || stamp != clientsStamp) {
					clients = JsonIO.Load(ClientsPath, () => new List<OutsideClient>());
					clientsStamp = stamp;
				}
				return clients.ToList();
			}
		}
	}

	public OutsideClient? Get(string name) => Clients.FirstOrDefault(c => c.Name == name);

	static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

	/// <summary>Add a client, or give an existing one a new token. The token goes only to its file.</summary>
	public OutsideClient Register(string name, bool rotate = false) {
		name = name.Trim().ToLowerInvariant();
		if(!ValidName.IsMatch(name)) throw new ArgumentException("Client names are lowercase letters, digits and dashes, up to 40.");
		var existing = Get(name);
		if(existing != null && !rotate && File.Exists(TokenPath(name))) return existing;
		var token = "mx_" + System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
		Directory.CreateDirectory(TokenDir);
		if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(TokenDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		var path = TokenPath(name);
		File.WriteAllText(path, "");
		if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		File.WriteAllText(path, token);
		var client = new OutsideClient(name, HashToken(token), existing?.Created ?? DateTimeOffset.UtcNow);
		lock(gate) {
			var list = JsonIO.Load(ClientsPath, () => new List<OutsideClient>());
			list.RemoveAll(c => c.Name == name);
			list.Add(client);
			JsonIO.Save(ClientsPath, list);
			clients = null;
		}
		return client;
	}

	public bool Remove(string name) {
		lock(gate) {
			var list = JsonIO.Load(ClientsPath, () => new List<OutsideClient>());
			if(list.RemoveAll(c => c.Name == name) == 0) return false;
			JsonIO.Save(ClientsPath, list);
			clients = null;
		}
		File.Delete(TokenPath(name));
		return true;
	}

	/// <summary>The client a bearer token belongs to, or null.</summary>
	public string? Authenticate(string token) {
		if(token.Length == 0) return null;
		var hash = Encoding.UTF8.GetBytes(HashToken(token));
		return Clients.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(c.TokenHash), hash))?.Name;
	}

	/// <summary>An agent by id or name, or a project by id or name (meaning its orchestrator).</summary>
	public Agent Resolve(string to) {
		to = to.Trim();
		if(manager.Get(to) is { } byId) return byId;
		var project = manager.Projects.FirstOrDefault(p => p.Info.Id == to)
			?? manager.Projects.SingleOrDefault(p => string.Equals(p.Info.Name, to, StringComparison.OrdinalIgnoreCase));
		if(project?.Info.OrchestratorId is { } orchId && manager.Get(orchId) is { } orch) return orch;
		var named = manager.Agents.Where(a => string.Equals(a.Info.Name, to, StringComparison.OrdinalIgnoreCase)).ToList();
		return named.Count switch {
			1 => named[0],
			0 => throw new ToolException($"No agent or project called {to}."),
			_ => throw new ToolException($"Several agents are called {to}: {string.Join(", ", named.Select(a => a.Info.Id))}. Use an id."),
		};
	}

	// ---- Outside → Marten ----

	/// <summary>Deliver a client's message to an agent. Returns the agent it reached.</summary>
	public Agent Deliver(string client, string to, string text, SendMode mode) {
		if(Get(client) == null) throw new ToolException($"No client {client}.");
		if(string.IsNullOrWhiteSpace(text)) throw new ToolException("The message is empty.");
		var target = Resolve(to);
		if(manager.Loops.Record(Prefix + client, target.Info.Id, DateTimeOffset.UtcNow) is { } count) {
			target.SetPaused(true);
			manager.MessageSera(target, $"Loop breaker: {Label(client)} and {target.Info.Name} exchanged {count} messages within " +
				$"{manager.Config.LoopWindowMinutes:0.#} minutes without you, so {target.Info.Name} is paused. Resume it when you've looked.");
			throw new ToolException($"Not sent: you and {target.Info.Name} have exchanged {count} messages within {manager.Config.LoopWindowMinutes:0.#} minutes, " +
				"which looks like a loop. It's paused and Sera has been told.");
		}
		var body = $"{text.Trim()}\n\n(From {client}, an agent outside Marten. Reply with send_message to \"{Prefix}{client}\".)";
		manager.LogAgentMessage(new AgentMessage(DateTimeOffset.UtcNow, Prefix + client, Label(client), target.Info.Id, target.Info.Name,
			target.Info.ProjectId, mode, text.Trim(), "outside"));
		target.Send(new Inbound(Label(client), body, mode, "outside"));
		return target;
	}

	// ---- Marten → outside ----

	/// <summary>Queue an agent's message for a client to read through the API.</summary>
	public MailMessage Write(Agent from, string client, string text) {
		if(Get(client) == null) throw new ToolException($"No outside client {client}. Known: {string.Join(", ", Clients.Select(c => c.Name)) switch { "" => "none", var s => s }}.");
		if(manager.Loops.Record(from.Info.Id, Prefix + client, DateTimeOffset.UtcNow) is { } count) {
			from.SetPaused(true);
			manager.MessageSera(from, $"Loop breaker: {from.Info.Name} and {Label(client)} exchanged {count} messages within " +
				$"{manager.Config.LoopWindowMinutes:0.#} minutes without you, so {from.Info.Name} is paused. Resume it when you've looked.");
			throw new ToolException($"Not sent: you and {client} have exchanged {count} messages, which looks like a loop. You're paused and Sera has been told.");
		}
		var dir = InDir(client);
		Directory.CreateDirectory(dir);
		var now = DateTimeOffset.UtcNow;
		var name = $"{now.UtcDateTime:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref seq) % 10000:0000}-{Regex.Replace(from.Info.Name, "[^A-Za-z0-9_-]", "_")}.md";
		var headers = new Dictionary<string, string> {
			["from"] = from.Info.Name, ["from_id"] = from.Info.Id, ["at"] = now.ToString("O"), ["reply_to"] = from.Info.Id,
		};
		if(from.Info.ProjectId != null) headers["project"] = from.Info.ProjectId;
		var path = Path.Combine(dir, name);
		AtomicFile.Write(path, string.Concat(headers.Select(h => $"{h.Key}: {h.Value}\n")) + "---\n" + text.Trim() + "\n");
		manager.LogAgentMessage(new AgentMessage(now, from.Info.Id, from.Info.Name, Prefix + client, Label(client), from.Info.ProjectId, SendMode.Queued, text.Trim(), "outside"));
		manager.Publish(new { type = "outside", client });
		return new MailMessage(path, headers, text.Trim());
	}

	/// <summary>Unread messages for a client, oldest first. Ack moves them to inbox/read/.</summary>
	public List<MailMessage> Inbox(string client, bool ack) {
		var dir = InDir(client);
		if(!Directory.Exists(dir)) return [];
		var list = Directory.EnumerateFiles(dir, "*.md").Order().Select(f => Parse(f, File.ReadAllText(f))).ToList();
		if(ack && list.Count > 0) {
			var read = Path.Combine(dir, "read");
			Directory.CreateDirectory(read);
			foreach(var m in list) try { File.Move(m.File, Path.Combine(read, Path.GetFileName(m.File)), overwrite: true); } catch(IOException) { }
		}
		return list;
	}

	public static MailMessage Parse(string file, string content) {
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var lines = content.Replace("\r\n", "\n").Split('\n');
		var sep = Array.IndexOf(lines, "---");
		if(sep < 0 || lines.Take(sep).Any(l => l.Length > 0 && !Regex.IsMatch(l, @"^[A-Za-z_][A-Za-z0-9_-]*:")))
			return new MailMessage(file, headers, content.Trim());
		foreach(var l in lines.Take(sep).Where(l => l.Length > 0)) {
			var i = l.IndexOf(':');
			headers[l[..i].Trim()] = l[(i + 1)..].Trim();
		}
		return new MailMessage(file, headers, string.Join('\n', lines.Skip(sep + 1)).Trim());
	}
}
