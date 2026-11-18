using System.Text.Json;
using Marten.Agents;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class CompletionContractTests {
	static string Args(object o) => JsonSerializer.Serialize(o);

	[Fact]
	public async Task ATaskWithACheckIsDoneOnlyWhenTheCheckPasses() {
		using var dir = new TempDir();
		Directory.CreateDirectory(dir.File("repo"));
		AgentManager? m = null;
		Func<AgentManager> get = () => m!;
		var tools = new ToolRegistry().Add(new TaskAddTool(get)).Add(new TaskUpdateTool(get)).Add(new SpawnAgentTool(get));
		m = new AgentManager(new Home(dir.File("home")), new Config(), new RoutedBackend(), tools);
		var (_, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		var r = await m.Tools.InvokeAsync(orch, "spawn_agent", Args(new { name = "w", brief = "write ok.txt", task = "Write ok.txt", check = "test -f ok.txt && echo found", worktree = (bool?) null, model = (string?) null, effort = (string?) null }), CancellationToken.None);
		Assert.False(r.IsError, r.Text);
		var worker = m.Agents.Single(a => a.Info.Kind == AgentKind.Task);
		Assert.Equal("test -f ok.txt && echo found", worker.Tasks.Single().Check);

		var fail = await m.Tools.InvokeAsync(worker, "task_update", Args(new { id = 1, state = "done", reason = "wrote it" }), CancellationToken.None);
		Assert.True(fail.IsError);
		Assert.Contains("exited 1", fail.Text);
		Assert.Equal(TaskState.Open, worker.Tasks.Single().State);
		Assert.False(worker.Tasks.Single().LastCheck!.Passed);

		File.WriteAllText(Path.Combine(worker.WorkingDirectory, "ok.txt"), "ok");
		var pass = await m.Tools.InvokeAsync(worker, "task_update", Args(new { id = 1, state = "done", reason = "wrote it" }), CancellationToken.None);
		Assert.False(pass.IsError, pass.Text);
		var task = worker.Tasks.Single();
		Assert.Equal(TaskState.Done, task.State);
		Assert.True(task.LastCheck!.Passed);
		Assert.Contains("found", task.LastCheck.Output);
		Assert.Equal("wrote it (check `test -f ok.txt && echo found` passed)", task.Reason);

		// Waiting and blocked don't run the check.
		var other = worker.AddTask("second", orch.Info.Id, "exit 3");
		var waiting = await m.Tools.InvokeAsync(worker, "task_update", Args(new { id = other.Id, state = "waiting", reason = "on orch" }), CancellationToken.None);
		Assert.False(waiting.IsError);
		Assert.Null(worker.GetTask(other.Id)!.LastCheck);
	}

	[Fact]
	public async Task ACheckThatRunsTooLongFails() {
		using var dir = new TempDir();
		var run = await CompletionCheck.RunAsync("echo started; sleep 30", dir.Path, TimeSpan.FromMilliseconds(500), CancellationToken.None);
		Assert.False(run.Passed);
		Assert.Null(run.ExitCode);
		Assert.Contains("started", run.Output);
	}
}
