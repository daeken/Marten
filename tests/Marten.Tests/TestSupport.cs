using System.Text.Json.Nodes;
using Marten.Agents;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public sealed class TempDir : IDisposable {
	public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "marten-test-" + Guid.NewGuid().ToString("N"));
	public TempDir() => Directory.CreateDirectory(Path);
	public string File(string name) => System.IO.Path.Combine(Path, name);
	public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public sealed class TestContext(string dir, ToolRegistry? tools = null) : IPowerShellOwner {
	public string AgentId { get; } = Guid.NewGuid().ToString("N");
	public string WorkingDirectory => dir;
	public FileTracker Files { get; } = new();
	public ToolRegistry Tools { get; } = tools ?? new ToolRegistry().Add(new ReadFileTool()).Add(new EditFileTool());
	public JsonNode? PowerShellState { get; set; }
	public object MartenObject => new TestMarten(this);
	public int Abandoned;
	public void RunspaceAbandoned(string command) => Abandoned++;
}

public sealed class TestMarten(TestContext ctx) {
	public string CallTool(string name, System.Collections.IDictionary args) {
		var json = StateJson.FromPowerShell(args)!.ToJsonString();
		var r = ctx.Tools.InvokeAsync(ctx, name, json, CancellationToken.None).GetAwaiter().GetResult();
		if(r.IsError) throw new InvalidOperationException(r.Text);
		return r.Text;
	}
}

/// <summary>A backend that replays scripted responses and records every request.</summary>
public sealed class FakeBackend : IModelBackend {
	public readonly Queue<Func<ModelRequest, ModelResult>> Script = new();
	public readonly List<ModelRequest> Requests = new();
	public TaskCompletionSource? Gate;
	public string Provider => "fake";
	public bool Connected => true;
	public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["fake-1"]);

	public async Task<ModelResult> CompleteAsync(ModelRequest request, Action<string> onTextDelta, CancellationToken ct) {
		lock(Requests) Requests.Add(request);
		if(Gate is { } g) await g.Task.WaitAsync(ct);
		if(Script.Count == 0) throw new InvalidOperationException("Script exhausted");
		return Script.Dequeue()(request);
	}

	public static ModelResult Say(string text) => new([new Entry { Kind = EntryKind.Assistant, Text = text, Provider = "fake" }], null);
	public static ModelResult Call(string id, string name, string args) =>
		new([new Entry { Kind = EntryKind.ToolCall, CallId = id, Name = name, Arguments = args, Provider = "fake" }], null);
}

public static class Wait {
	public static async Task Until(Func<bool> condition, int timeoutMs = 10_000) {
		var start = Environment.TickCount64;
		while(!condition()) {
			if(Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("Condition not met");
			await Task.Delay(20);
		}
	}
}
