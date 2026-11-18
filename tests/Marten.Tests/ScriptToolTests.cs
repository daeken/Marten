using System.Text.Json.Nodes;
using Marten.Agents;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class ScriptToolTests {
	static AgentManager Manager(TempDir dir, IModelBackend backend) {
		AgentManager? m = null;
		Func<AgentManager> get = () => m!;
		var source = new ScriptToolSource(get);
		var tools = new ToolRegistry().Add(new ReadFileTool())
			.Add(new PowerShellTool(() => 30, () => 3, new ChildProcessWatcher()))
			.Add(new DefineToolTool(get, source)).Add(new RemoveToolTool(get, source))
			.AddSource(source);
		return m = new AgentManager(new Home(dir.File("home")), new Config(), backend, tools);
	}

	const string Greet = """
		<#
		.SYNOPSIS
		Greets someone.
		.PARAMETER Name
		Who to greet.
		.PARAMETER Times
		How many times.
		#>
		param(
			[Parameter(Mandatory)][string]$Name,
			[int]$Times = 1,
			[string[]]$Tags
		)
		1..$Times | ForEach-Object { "hello $Name" }
		if($Tags) { "tags: $($Tags -join ',')" }
		""";

	static JsonObject Define(string name, string script, string scope) => new() { ["name"] = name, ["script"] = script, ["scope"] = scope };

	[Fact]
	public void TheParamBlockIsTheSchemaAndTheHelpIsTheDescription() {
		var tool = ScriptTool.Parse("greet", Greet, "/x/greet.ps1", "global");
		Assert.Equal("Greets someone.", tool.Description);
		var props = (JsonObject) tool.Parameters["properties"]!;
		Assert.Equal("string", props["Name"]!["type"]!.GetValue<string>());
		Assert.Equal("Who to greet.", props["Name"]!["description"]!.GetValue<string>());
		Assert.Equal("""["integer","null"]""", props["Times"]!["type"]!.ToJsonString());
		Assert.Equal("string", props["Tags"]!["items"]!["type"]!.GetValue<string>());
		Assert.Equal(3, ((JsonArray) tool.Parameters["required"]!).Count);

		Assert.Throws<ToolException>(() => ScriptTool.Parse("nohelp", "param([string]$x)\n$x", "/x", "global"));
		Assert.Throws<ToolException>(() => ScriptTool.Parse("broken", "<# .SYNOPSIS x #>\nparam(", "/x", "global"));
		Assert.Throws<ToolException>(() => ScriptTool.Parse("Bad-Name", Greet, "/x", "global"));
	}

	[Fact]
	public async Task DefinedToolsRunDirectlyAndAsInlinePowerShellFunctions() {
		using var dir = new TempDir();
		var m = Manager(dir, new FakeBackend());
		var agent = m.CreateChat("t", dir.Path);
		var r = await m.Tools.InvokeAsync(agent, "define_tool", Define("greet", Greet, "global").ToJsonString(), default);
		Assert.False(r.IsError, r.Text);
		Assert.True(File.Exists(Path.Combine(m.Home.Root, "tools", "greet.ps1")));

		var direct = await m.Tools.InvokeAsync(agent, "greet", """{"Name":"Sera","Times":2,"Tags":null}""", default);
		Assert.Equal("hello Sera\nhello Sera\n", direct.Text);
		var ps = (PowerShellTool) m.Tools.Get("powershell")!;
		var inline = await ps.RunAsync(agent, "greet -Name Marten -Tags a,b", TimeSpan.FromSeconds(30), default);
		Assert.Equal("hello Marten\ntags: a,b\n", inline.Text);

		Assert.True((await m.Tools.InvokeAsync(agent, "define_tool", Define("read_file", Greet, "global").ToJsonString(), default)).IsError);
		Assert.True((await m.Tools.InvokeAsync(agent, "define_tool", Define("x_tool", Greet, "project").ToJsonString(), default)).IsError);
		await m.Tools.InvokeAsync(agent, "remove_tool", """{"name":"greet","scope":"global"}""", default);
		Assert.True((await m.Tools.InvokeAsync(agent, "greet", """{"Name":"x"}""", default)).IsError);
	}

	[Fact]
	public async Task ProjectToolsReachOnlyThatProjectAndChangesArriveAsANoteThenAtCompaction() {
		using var dir = new TempDir();
		Directory.CreateDirectory(dir.File("repo"));
		var backend = new FakeBackend();
		var m = Manager(dir, backend);
		var (_, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		var chat = m.CreateChat("c", dir.Path);
		Assert.DoesNotContain(orch.RequestTools(), t => t.Name == "greet");

		await m.Tools.InvokeAsync(orch, "define_tool", Define("greet", Greet, "project").ToJsonString(), default);
		Assert.Contains(m.Tools.For(orch), t => t.Name == "greet");
		Assert.DoesNotContain(m.Tools.For(chat), t => t.Name == "greet");
		// Not in the request until compaction, so the cached prefix holds.
		Assert.DoesNotContain(orch.RequestTools(), t => t.Name == "greet");

		backend.Script.Enqueue(_ => FakeBackend.Say("ok"));
		orch.Send(new Inbound("Sera", "hi", SendMode.Queued));
		await Wait.Until(() => orch.Transcript.Last?.Kind == EntryKind.Idle);
		var note = orch.Transcript.Entries.Single(e => e.Text?.StartsWith("Tools changed") == true);
		Assert.Contains("greet: Greets someone.", note.Text);
		Assert.Contains("greet -Name <Name>", note.Text);

		backend.Script.Enqueue(_ => FakeBackend.Say("SUMMARY"));
		await orch.Compact("fake-1", default);
		Assert.Contains(orch.RequestTools(), t => t.Name == "greet");
	}
}
