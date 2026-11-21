using System.Text.Json.Nodes;
using Marten.Agents;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

[Collection("Process attribution")]
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
		try {
		Assert.True(Posix.Alive(p.Pid));
		// Detached: not Marten's child, and in its own process group.
		Assert.NotEqual(ProcessTable.Self, ProcessTable.ParentMap()[p.Pid]);
		// macOS reports /private/var/... for /var/..., so match on the directory's own name.
		await Wait.Until(() => File.ReadAllText(p.LogPath).Contains("hello from /") && File.ReadAllText(p.LogPath).Contains(Path.GetFileName(dir.Path)));
		await Wait.Until(() => ProcessTable.Tree(p.Pid, ProcessTable.ParentMap()).Count >= 3);
		var tree = ProcessTable.Tree(p.Pid, ProcessTable.ParentMap());
		Assert.Contains("#1 server — running", (await new ProcessListTool().InvokeAsync(agent, new JsonObject(), default)).Text);

		await new ProcessStopTool().InvokeAsync(agent, new JsonObject { ["id"] = 1 }, default);
		await Wait.Until(() => !tree.Any(Posix.Alive));
		Assert.NotNull(agent.Processes.Single().Stopped);
		Assert.Equal("No background processes.", (await new ProcessListTool().InvokeAsync(agent, new JsonObject(), default)).Text);
		} finally {
			await agent.StopAllProcesses();
		}
	}

	[Fact]
	public async Task ShutdownAndRestartLeaveDetachedJobsRunningAndClosingTheAgentStopsThem() {
		using var dir = new TempDir();
		var m = Manager(dir);
		Directory.CreateDirectory(dir.File("repo"));
		var (_, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		var task = m.SpawnTask(orch, "t", false, null, null);
		var p = task.StartProcess("sleep 300", null, null);
		try {
		Assert.Equal("sleep", p.Name);

		var watcher = new ChildProcessWatcher();
		await m.Restart.DrainAsync(TimeSpan.Zero, TimeSpan.FromSeconds(1), watcher.CleanForeground);
		var reloaded = Manager(dir);
		reloaded.LoadAll();
		var again = reloaded.Get(task.Info.Id)!;
		Assert.True(Posix.Alive(again.Processes.Single().Pid));

		again.SetClosed(true);
		await Wait.Until(() => !Posix.Alive(p.Pid) && again.Processes.Single().Stopped != null);
		} finally {
			await ProcessLauncher.StopAsync(p.Pid, TimeSpan.FromSeconds(1));
		}
	}

	[Fact]
	public async Task StoppingAFinishedJobSparesAProtectedProcessLeftInItsGroup() {
		// A deploy job relaunches Marten in the background and exits; the new Marten stays in the job's
		// process group. Stopping that finished job later must not signal the group and kill Marten.
		using var dir = new TempDir();
		var pidFile = dir.File("survivor.pid");
		var pid = new ProcessLauncher().Start($"sleep 301 & echo $! > '{pidFile}'; sleep 302", dir.Path, dir.File("job.log"));
		await Wait.Until(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0);
		var survivor = int.Parse(File.ReadAllText(pidFile).Trim());
		try {
			Assert.Equal(pid, Posix.GetPgid(survivor));
			var others = ProcessTable.Tree(pid, ProcessTable.ParentMap()).Where(p => p != survivor).ToList();
			Assert.NotEmpty(others);

			await ProcessLauncher.StopAsync(pid, TimeSpan.FromSeconds(1), extraProtected: [survivor]);

			await Wait.Until(() => !others.Any(Posix.Alive));
			Assert.True(Posix.Alive(survivor));
		} finally {
			Posix.ForceKill(survivor, group: false);
			await ProcessLauncher.StopAsync(pid, TimeSpan.FromSeconds(1));
		}
	}

	[Fact]
	public async Task AMissingWorkingDirectoryIsAToolError() {
		using var dir = new TempDir();
		var agent = Manager(dir).CreateChat("t", dir.Path);
		await Assert.ThrowsAsync<ToolException>(() => new ProcessStartTool().InvokeAsync(agent,
			new JsonObject { ["command"] = "true", ["name"] = null, ["working_directory"] = "nope" }, default));
	}
}
