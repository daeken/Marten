using System.Text.Json.Nodes;
using Marten.Agents;
using Marten.Mcp;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class McpTests {
	static string Node => new[] { "/opt/homebrew/bin/node", "/usr/local/bin/node", "/opt/node22/bin/node", "/usr/bin/node" }
		.FirstOrDefault(File.Exists) ?? "node";

	internal static (AgentManager, McpHub) Manager(TempDir dir, IModelBackend backend, int directLimit = 12) {
		AgentManager? m = null;
		Func<AgentManager> get = () => m!;
		var hub = new McpHub(get);
		var tools = new ToolRegistry().Add(new ReadFileTool())
			.Add(new PowerShellTool(() => 30, () => 3, new ChildProcessWatcher()))
			.Add(new ToolSearchTool()).Add(new CallToolTool())
			.AddSource(hub);
		var config = new Config {
			DirectToolLimit = directLimit,
			Mcp = new() {
				["fake"] = new McpServerConfig {
					Command = Node, Args = [Path.Combine(AppContext.BaseDirectory, "fake-mcp.mjs")],
					Env = new() { ["FAKE_MCP_STARTS"] = dir.File("starts.txt") },
				},
			},
		};
		m = new AgentManager(new Home(dir.File("home")), config, backend, tools) { Mcp = hub };
		return (m, hub);
	}

	static int Starts(TempDir dir) => File.Exists(dir.File("starts.txt")) ? File.ReadAllLines(dir.File("starts.txt")).Length : 0;

	[Fact]
	public async Task SchemasAreReadOnceAndServersStartLazilyPerAgent() {
		using var dir = new TempDir();
		var (m, hub) = Manager(dir, new FakeBackend());
		var a = m.CreateChat("a", dir.Path);
		var b = m.CreateChat("b", dir.Path);
		Assert.Empty(m.Tools.DynamicFor(a));

		await hub.RefreshAsync(false, default);
		Assert.Equal(1, Starts(dir));
		Assert.Null(hub.Schemas["fake"].Error);
		Assert.Equal(["fake__echo", "fake__fail", "fake__pixel"], m.Tools.DynamicFor(a).Select(t => t.Name).Order());
		Assert.False(m.Tools.SpecsFor(a).Single(t => t.Name == "fake__echo").Strict);
		// Cached: an unchanged config doesn't start it again, even from a fresh hub.
		await new McpHub(() => m).RefreshAsync(false, default);
		Assert.Equal(1, Starts(dir));

		var r1 = await m.Tools.InvokeAsync(a, "fake__echo", """{"text":"hi"}""", default);
		var r2 = await m.Tools.InvokeAsync(a, "fake__echo", """{"text":"again"}""", default);
		var r3 = await m.Tools.InvokeAsync(b, "fake__echo", """{"text":"hi"}""", default);
		Assert.StartsWith("hi (pid ", r1.Text);
		Assert.Equal(r1.Text[3..], r2.Text[6..]);
		Assert.NotEqual(r1.Text, r3.Text);
		Assert.Equal(3, Starts(dir));
		Assert.Equal(2, hub.Running.Count());

		var fail = await m.Tools.InvokeAsync(a, "fake__fail", "{}", default);
		Assert.True(fail.IsError);

		// From PowerShell, through the same instance.
		var ps = (PowerShellTool) m.Tools.Get("powershell")!;
		var viaPs = await ps.RunAsync(a, "fake__echo -text shell", TimeSpan.FromSeconds(30), default);
		Assert.Contains("shell (pid ", viaPs.Text);
		Assert.Equal(3, Starts(dir));

		a.SetClosed(true);
		await Wait.Until(() => hub.Running.Count() == 1);
		await hub.StopAll();
	}

	[Fact]
	public async Task ImagesFromMcpAndViewImageReachTheModel() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		var (m, hub) = Manager(dir, backend);
		await hub.RefreshAsync(false, default);
		File.WriteAllBytes(dir.File("shot.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));
		backend.Script.Enqueue(_ => FakeBackend.Call("c1", "fake__pixel", "{}"));
		backend.Script.Enqueue(_ => FakeBackend.Call("c2", "powershell", """{"command":"View-Image shot.png","timeout_seconds":null}"""));
		backend.Script.Enqueue(_ => FakeBackend.Say("two pixels"));
		var agent = m.CreateChat("t", dir.Path);
		agent.Send(new Inbound("Sera", "look", SendMode.Queued));
		await Wait.Until(() => agent.Transcript.Last?.Kind == EntryKind.Idle);

		var results = agent.Transcript.Entries.Where(e => e.Kind == EntryKind.ToolResult).ToList();
		Assert.All(results, r => Assert.Equal("image/png", Assert.Single(r.Images!).MimeType));
		Assert.Contains("Showing", results[1].Text);
		var wire = Marten.Models.OpenAI.ResponsesBackend.ToWire(backend.Requests[2].History);
		var outputs = wire.OfType<JsonObject>().Where(i => i["type"]!.GetValue<string>() == "function_call_output").ToList();
		Assert.All(outputs, o => Assert.StartsWith("data:image/png;base64,", o["output"]![1]!["image_url"]!.GetValue<string>()));

		// Elided results drop their images too.
		agent.ElideLast(2);
		Assert.All(agent.View().Where(e => e.Kind == EntryKind.ToolResult), e => Assert.Null(e.Images));
		await hub.StopAll();
	}

	[Fact]
	public async Task ABrokenServerIsReportedNotFatal() {
		using var dir = new TempDir();
		var (m, hub) = Manager(dir, new FakeBackend());
		m.UpdateConfig(c => c with { Mcp = new() { ["broken"] = new McpServerConfig { Command = "/nonexistent/server" } } });
		await hub.RefreshAsync(false, default);
		Assert.Contains("Couldn't start", hub.Schemas["broken"].Error);
		Assert.Empty(m.Tools.DynamicFor(m.CreateChat("t", dir.Path)));
	}
}
