using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Marten.Mcp;

/// <summary>How to launch one MCP server. stdio only: no OAuth, no remote servers.</summary>
public sealed record McpServerConfig {
	public required string Command { get; init; }
	public List<string> Args { get; init; } = new();
	public Dictionary<string, string> Env { get; init; } = new();
	/// <summary>Working directory; null means the calling agent's.</summary>
	public string? Cwd { get; init; }
	public bool Disabled { get; init; }

	/// <summary>
	/// Fill {agent}, {agentDir} and {martenHome} in args, env and cwd, and expand a leading ~/, so each agent's
	/// instance can get its own state, e.g. a browser profile under its own directory.
	/// </summary>
	public McpServerConfig Expand(string agentId, string agentDir, string martenHome) {
		var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string X(string v) {
			v = v.Replace("{agent}", agentId).Replace("{agentDir}", agentDir).Replace("{martenHome}", martenHome);
			return v.StartsWith("~/") ? Path.Combine(userHome, v[2..]) : v;
		}
		return this with {
			Command = X(Command), Args = Args.Select(X).ToList(),
			Env = Env.ToDictionary(kv => kv.Key, kv => X(kv.Value)), Cwd = Cwd == null ? null : X(Cwd),
		};
	}
}

public sealed class McpException(string message) : Exception(message);

/// <summary>A minimal MCP client over stdio: newline-delimited JSON-RPC 2.0.</summary>
public sealed class McpClient : IAsyncDisposable {
	public const string ProtocolVersion = "2025-06-18";
	readonly Process process;
	readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> pending = new();
	readonly SemaphoreSlim writeGate = new(1, 1);
	readonly StreamWriter? stderrLog;
	long nextId;
	public string Name { get; }
	public bool Exited => process.HasExited;
	public int Pid => process.Id;

	McpClient(string name, Process process, string? stderrLogPath) {
		Name = name;
		this.process = process;
		if(stderrLogPath != null) {
			Directory.CreateDirectory(Path.GetDirectoryName(stderrLogPath)!);
			stderrLog = new StreamWriter(stderrLogPath, append: true) { AutoFlush = true };
		}
		_ = Task.Run(ReadLoop);
		_ = Task.Run(async () => {
			string? line;
			while((line = await process.StandardError.ReadLineAsync()) != null)
				try { stderrLog?.WriteLine(line); } catch { }
		});
	}

	public static async Task<McpClient> StartAsync(string name, McpServerConfig config, string workingDirectory, string? stderrLogPath, TimeSpan timeout, CancellationToken ct) {
		var psi = new ProcessStartInfo(config.Command) {
			WorkingDirectory = config.Cwd ?? workingDirectory, UseShellExecute = false,
			RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
			StandardOutputEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false),
		};
		foreach(var a in config.Args) psi.ArgumentList.Add(a);
		foreach(var (k, v) in config.Env) psi.Environment[k] = v;
		Process process;
		try {
			process = Process.Start(psi) ?? throw new McpException($"Couldn't start {config.Command}.");
		} catch(System.ComponentModel.Win32Exception e) {
			throw new McpException($"Couldn't start {config.Command}: {e.Message}");
		}
		var client = new McpClient(name, process, stderrLogPath);
		try {
			await client.RequestAsync("initialize", new JsonObject {
				["protocolVersion"] = ProtocolVersion,
				["capabilities"] = new JsonObject(),
				["clientInfo"] = new JsonObject { ["name"] = "marten", ["version"] = "0.1" },
			}, timeout, ct);
			await client.NotifyAsync("notifications/initialized", null);
		} catch {
			await client.DisposeAsync();
			throw;
		}
		return client;
	}

	async Task ReadLoop() {
		try {
			string? line;
			while((line = await process.StandardOutput.ReadLineAsync()) != null) {
				if(line.Length == 0) continue;
				JsonObject? msg;
				try { msg = JsonNode.Parse(line) as JsonObject; } catch { continue; }
				if(msg == null) continue;
				if(msg["method"] is JsonValue method) {
					// A request from the server. Answer pings; refuse anything else.
					if(msg["id"] is { } reqId)
						await SendAsync(method.GetValue<string>() == "ping"
							? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = reqId.DeepClone(), ["result"] = new JsonObject() }
							: new JsonObject { ["jsonrpc"] = "2.0", ["id"] = reqId.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" } });
					continue;
				}
				if(msg["id"] is JsonValue idv && idv.TryGetValue<long>(out var id) && pending.TryRemove(id, out var tcs)) {
					if(msg["error"] is JsonObject err) tcs.TrySetException(new McpException(err["message"]?.GetValue<string>() ?? err.ToJsonString()));
					else tcs.TrySetResult(msg["result"]?.DeepClone());
				}
			}
		} catch { }
		foreach(var (_, tcs) in pending) tcs.TrySetException(new McpException($"MCP server {Name} exited."));
		pending.Clear();
	}

	async Task SendAsync(JsonObject msg) {
		await writeGate.WaitAsync();
		try {
			await process.StandardInput.WriteLineAsync(msg.ToJsonString());
			await process.StandardInput.FlushAsync();
		} finally {
			writeGate.Release();
		}
	}

	public Task NotifyAsync(string method, JsonObject? parameters) {
		var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
		if(parameters != null) msg["params"] = parameters;
		return SendAsync(msg);
	}

	public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, TimeSpan timeout, CancellationToken ct) {
		if(process.HasExited) throw new McpException($"MCP server {Name} isn't running.");
		var id = Interlocked.Increment(ref nextId);
		var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
		pending[id] = tcs;
		var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
		if(parameters != null) msg["params"] = parameters;
		await SendAsync(msg);
		try {
			return await tcs.Task.WaitAsync(timeout, ct);
		} catch(TimeoutException) {
			pending.TryRemove(id, out _);
			_ = NotifyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = "timeout" });
			throw new McpException($"{Name} didn't answer {method} within {timeout.TotalSeconds:0}s.");
		} catch(OperationCanceledException) {
			pending.TryRemove(id, out _);
			_ = NotifyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = "cancelled" });
			throw;
		}
	}

	public async Task<List<JsonObject>> ListToolsAsync(TimeSpan timeout, CancellationToken ct) {
		var tools = new List<JsonObject>();
		string? cursor = null;
		do {
			var result = await RequestAsync("tools/list", cursor == null ? null : new JsonObject { ["cursor"] = cursor }, timeout, ct) as JsonObject;
			foreach(var t in result?["tools"] as JsonArray ?? []) if(t is JsonObject o) tools.Add((JsonObject) o.DeepClone());
			cursor = result?["nextCursor"]?.GetValue<string>();
		} while(cursor != null);
		return tools;
	}

	public async Task<JsonObject> CallToolAsync(string tool, JsonObject args, TimeSpan timeout, CancellationToken ct) =>
		await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args }, timeout, ct) as JsonObject ?? new JsonObject();

	public async ValueTask DisposeAsync() {
		try { process.StandardInput.Close(); } catch { }
		try {
			if(!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
		} catch { }
		process.Dispose();
		stderrLog?.Dispose();
		await Task.CompletedTask;
	}
}
