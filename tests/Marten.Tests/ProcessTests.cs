using System.Text.Json.Nodes;
using Marten.Agents;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class BackgroundProcessTests {
	static AgentManager Manager(TempDir dir) =>
		new(new Home(dir.File("home")), new Config { StopGraceSeconds = 2 }, new FakeBackend(),
			new ToolRegistry().Add(new ProcessStartTool()).Add(new ProcessListTool()).Add(new ProcessStopTool()));

	[Fact]
	public async Task StartsDetachedLogsToAFileAndStopsTheWholeTree() {
		using var dir = new TempDir();
		var agent = Manager(dir).CreateChat("t", dir.Path);
		var r = await new ProcessStartTool().InvokeAsync(agent,
			new JsonObject { ["command"] = "echo hello from $PWD; sleep 300 | cat", ["name"] = "server", ["working_directory"] = null }, default);
		Assert.StartsWith("Started #1 server", r.Text);
		var p = agent.Processes.Single();
		Assert.True(Posix.Alive(p.Pid));
		// Detached: not Marten's child, and in its own process group.
		Assert.NotEqual(ProcessTable.Self, ProcessTable.ParentMap()[p.Pid]);
		await Wait.Until(() => File.ReadAllText(p.LogPath).Contains($"hello from {dir.Path}"));
		await Wait.Until(() => ProcessTable.Tree(p.Pid, ProcessTable.ParentMap()).Count >= 3);
		var tree = ProcessTable.Tree(p.Pid, ProcessTable.ParentMap());
		Assert.Contains("#1 server — running", (await new ProcessListTool().InvokeAsync(agent, new JsonObject(), default)).Text);

		await new ProcessStopTool().InvokeAsync(agent, new JsonObject { ["id"] = 1 }, default);
		await Wait.Until(() => !tree.Any(Posix.Alive));
		Assert.NotNull(agent.Processes.Single().Stopped);
		Assert.Equal("No background processes.", (await new ProcessListTool().InvokeAsync(agent, new JsonObject(), default)).Text);
	}

	[Fact]
	public async Task ARestartLeavesThemRunningAndClosingTheAgentStopsThem() {
		using var dir = new TempDir();
		var m = Manager(dir);
		Directory.CreateDirectory(dir.File("repo"));
		var (_, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		var task = m.SpawnTask(orch, "t", false, null, null);
		var p = task.StartProcess("sleep 300", null, null);
		Assert.Equal("sleep", p.Name);

		var reloaded = Manager(dir);
		reloaded.LoadAll();
		var again = reloaded.Get(task.Info.Id)!;
		Assert.True(Posix.Alive(again.Processes.Single().Pid));

		again.SetClosed(true);
		await Wait.Until(() => !Posix.Alive(p.Pid) && again.Processes.Single().Stopped != null);
	}

	[Fact]
	public async Task AMissingWorkingDirectoryIsAToolError() {
		using var dir = new TempDir();
		var agent = Manager(dir).CreateChat("t", dir.Path);
		await Assert.ThrowsAsync<ToolException>(() => new ProcessStartTool().InvokeAsync(agent,
			new JsonObject { ["command"] = "true", ["name"] = null, ["working_directory"] = "nope" }, default));
	}
}
