using System.Text.Json;
using Marten.Agents;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class TaskDependencyTests : IAsyncLifetime {
	readonly TempDir dir = new();
	AgentManager manager;
	Agent owner, producer;

	public TaskDependencyTests() {
		manager = NewManager();
		manager.SetPausedAll(true); // Keep model turns from consuming or acting on wakes until explicitly resumed.
		var (_, orch) = manager.CreateProject("p", [dir.Path], null, false);
		owner = manager.SpawnTask(orch, "consumer", false, null, null);
		producer = manager.SpawnTask(orch, "producer", false, null, null);
	}

	AgentManager NewManager() {
		var tools = new ToolRegistry().Add(new TaskWaitForTool(() => manager!)).Add(new TaskUpdateTool(() => manager!));
		return new AgentManager(new Home(dir.File("home")), new Config(), new RoutedBackend(), tools);
	}

	public Task InitializeAsync() => Task.CompletedTask;
	Task Stop() => manager.Restart.DrainAsync(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), () => { });
	public async Task DisposeAsync() { await Stop(); dir.Dispose(); }
	async Task Reload(Action? simulateInterruptedWrite = null) {
		await Stop();
		simulateInterruptedWrite?.Invoke();
		manager = NewManager();
		manager.LoadAll();
		owner = manager.Get(owner.Info.Id)!;
		producer = manager.Get(producer.Info.Id)!;
	}

	static int Add(Agent a, string title, string? check = null) => a.AddTask(title, a.Info.Id, check).Id;
	Task<ToolOutput> Call(Agent a, string name, object args) => manager.Tools.InvokeAsync(a, name, JsonSerializer.Serialize(args), default);
	Task<ToolOutput> Link(Agent a, int id, string agent, int taskId) => Call(a, "task_wait_for", new { id, agent, task_id = taskId });
	Task<ToolOutput> Done(Agent a, int id) => Call(a, "task_update", new { id, state = "done", reason = "ready" });
	static void Success(ToolOutput output) => Assert.False(output.IsError, output.Text);
	static void AssertTask(Agent a, int id, TaskState state, TaskDependency? dependency = null) {
		var task = Assert.IsType<TaskItem>(a.GetTask(id));
		Assert.Equal(state, task.State);
		Assert.Equal(dependency, task.WaitingOn);
	}

	[Fact]
	public async Task OnlyPassingThePrerequisiteCheckReopensAndWakesItsWaiters() {
		var target = Add(producer, "build artifact", "test -f artifact");
		var mine = Add(owner, "inspect artifact");
		var other = manager.Get(owner.Info.ParentId!)!;
		var theirs = Add(other, "publish artifact");
		var dep = new TaskDependency(producer.Info.Id, target);
		Success(await Link(owner, mine, producer.Info.Name, target));
		Success(await Link(other, theirs, producer.Info.Id, target));
		Assert.Empty(manager.SeraInbox()); // Even an orchestrator's wait is not a blocked alert.
		Assert.Empty(manager.AgentMessages()); // Both tasks were self-assigned.
		Assert.True((await Done(producer, target)).IsError);
		Assert.False(producer.GetTask(target)!.LastCheck!.Passed);
		AssertTask(producer, target, TaskState.Open);
		AssertTask(owner, mine, TaskState.Waiting, dep);
		AssertTask(other, theirs, TaskState.Waiting, dep);
		Assert.Equal(0, owner.PendingCount);
		Assert.Equal(0, other.PendingCount);

		File.WriteAllText(dir.File("artifact"), "ready");
		Success(await Done(producer, target));
		AssertTask(producer, target, TaskState.Done);
		Success(await Done(producer, target)); // Repeated completion must not duplicate either wake.
		AssertTask(owner, mine, TaskState.Open);
		AssertTask(other, theirs, TaskState.Open);
		Assert.Equal(1, owner.PendingCount);
		Assert.Equal(1, other.PendingCount);
		manager.SetPausedAll(false);
		await Wait.Until(() => owner.Transcript.Entries.Any(e => e.Kind == EntryKind.Message && e.Text!.Contains("build artifact") && e.Text.Contains("inspect artifact")));
	}

	[Fact]
	public async Task RegistrationUsesScopedLookupAndNotifiesTheAssigner() {
		var assigner = manager.Get(owner.Info.ParentId!)!;
		var mine = owner.AddTask("review", assigner.Info.Id).Id;
		var target = Add(producer, "produce");
		var other = manager.SpawnTask(assigner, producer.Info.Name, false, null, null);
		Add(other, "same name, different agent");
		Assert.True((await Link(owner, mine, producer.Info.Name, target)).IsError);
		Assert.True((await Link(owner, mine, producer.Info.Id, target + 1)).IsError);
		AssertTask(owner, mine, TaskState.Open);
		Assert.Equal(0, assigner.PendingCount);
		Success(await Link(owner, mine, producer.Info.Id, target));
		Assert.Equal(1, assigner.PendingCount);
		Success(await Link(owner, mine, producer.Info.Id, target));
		Assert.Equal(1, assigner.PendingCount);
		var notice = Assert.Single(manager.AgentMessages());
		Assert.Equal(owner.Info.Id, notice.FromId);
		Assert.Equal(assigner.Info.Id, notice.ToId);
		Assert.Equal(SendMode.Queued, notice.Mode);
		Assert.Contains("waiting", notice.Text);
		Assert.Empty(manager.SeraInbox());
		var api = (MartenApi)assigner.MartenObject;
		Assert.Throws<ToolException>(() => api.Tasks(producer.Info.Name));
		var row = JsonSerializer.SerializeToElement(Assert.Single(api.Tasks(owner.Info.Id)));
		Assert.Equal(mine, row.GetProperty("Id").GetInt32());
		Assert.Equal(new TaskDependency(producer.Info.Id, target), row.GetProperty("WaitingOn").Deserialize<TaskDependency>());
		var (_, foreign) = manager.CreateProject("other", [dir.Path], null, false);
		Assert.Throws<ToolException>(() => api.Tasks(foreign.Info.Id));
	}

	[Fact]
	public async Task NameLookupIgnoresCaseAndOutOfProjectNamesakes() {
		var (_, foreign) = manager.CreateProject("other", [dir.Path], null, false);
		var namesake = manager.SpawnTask(foreign, producer.Info.Name, false, null, null);
		Add(namesake, "foreign task");
		var target = Add(producer, "local task");
		var mine = Add(owner, "review");
		var name = producer.Info.Name.ToUpperInvariant();
		Success(await Link(owner, mine, name, target));
		AssertTask(owner, mine, TaskState.Waiting, new(producer.Info.Id, target));
		var row = JsonSerializer.SerializeToElement(Assert.Single(((MartenApi)owner.MartenObject).Tasks(name)));
		Assert.Equal("local task", row.GetProperty("Title").GetString());
	}

	[Fact]
	public async Task ProjectlessAgentCanWaitOnItsOwnDistinctTaskButNotAnotherChat() {
		var chat = manager.CreateChat("solo", dir.Path);
		var stranger = manager.CreateChat("elsewhere", dir.Path);
		var mine = Add(chat, "review");
		var target = Add(chat, "prepare");
		var foreign = Add(stranger, "foreign task");
		Assert.True((await Link(chat, mine, stranger.Info.Id, foreign)).IsError);
		var api = (MartenApi)chat.MartenObject;
		Assert.Throws<ToolException>(() => api.Tasks(stranger.Info.Id));
		Success(await Link(chat, mine, chat.Info.Id, target));
		AssertTask(chat, mine, TaskState.Waiting, new(chat.Info.Id, target));
		Assert.Equal(2, api.Tasks(chat.Info.Id).Length);
		Success(await Done(chat, target));
		AssertTask(chat, mine, TaskState.Open);
	}

	[Fact]
	public async Task AnAlreadyDoneTargetDoesNotLeaveTheCallerWaiting() {
		var target = Add(producer, "finished");
		var assigner = manager.Get(owner.Info.ParentId!)!;
		var mine = owner.AddTask("follow up", assigner.Info.Id).Id;
		producer.UpdateTask(target, TaskState.Done, "ok");
		var result = await Link(owner, mine, producer.Info.Name, target);
		Success(result);
		Assert.Equal($"Task #{mine} is open.", result.Text);
		Assert.Equal(0, assigner.PendingCount);
		Assert.Empty(manager.AgentMessages());
		AssertTask(owner, mine, TaskState.Open);
	}

	[Fact]
	public async Task ReplacedOrManuallyClearedDependenciesCannotBeWokenByStaleCompletions() {
		var first = Add(producer, "first");
		var second = Add(producer, "second");
		var manual = Add(owner, "manual wait");
		var replaced = Add(owner, "retargeted wait");
		Success(await Link(owner, manual, producer.Info.Id, first));
		Success(await Link(owner, replaced, producer.Info.Id, first));
		Success(await Call(owner, "task_update", new { id = manual, state = "waiting", reason = "on a schedule" }));
		Success(await Link(owner, replaced, producer.Info.Id, second));
		producer.UpdateTask(first, TaskState.Done, "ok");
		AssertTask(owner, manual, TaskState.Waiting);
		Assert.Equal("on a schedule", owner.GetTask(manual)!.Reason);
		AssertTask(owner, replaced, TaskState.Waiting, new(producer.Info.Id, second));
		Assert.Equal(0, owner.PendingCount);
		producer.UpdateTask(second, TaskState.Done, "ok");
		AssertTask(owner, replaced, TaskState.Open);
		AssertTask(owner, manual, TaskState.Waiting);
		Assert.Equal(1, owner.PendingCount);
	}

	[Fact]
	public async Task RestartRestoresLiveLinksAndUndeliveredWakes() {
		var target = Add(producer, "assemble");
		var mine = Add(owner, "ship");
		owner.UpdateTask(mine, TaskState.Waiting, "need assembly", new(producer.Info.Id, target));
		await Reload();
		AssertTask(owner, mine, TaskState.Waiting, new(producer.Info.Id, target));
		Assert.Equal(0, owner.PendingCount);
		producer.UpdateTask(target, TaskState.Done, "ok");
		await Reload(); // Completion was persisted, but the paused owner never received its message.
		AssertTask(owner, mine, TaskState.Open);
		manager.SetPausedAll(false);
		await Wait.Until(() => owner.Transcript.Entries.Any(e => e.Kind == EntryKind.Message && e.Text!.Contains("assemble")));
	}

	[Fact]
	public async Task RestartReconcilesCompletionPersistedBeforeItsWaiterWasReopened() {
		var target = Add(producer, "assemble");
		var mine = Add(owner, "ship");
		owner.UpdateTask(mine, TaskState.Waiting, "need assembly", new(producer.Info.Id, target));
		// Model a crash between the two agents' atomic task-file writes.
		await Reload(() => JsonIO.Save(Path.Combine(producer.Directory, "tasks.json"), new[] { producer.GetTask(target)! with { State = TaskState.Done } }));
		AssertTask(owner, mine, TaskState.Open);
		Assert.Equal(1, owner.PendingCount);
	}

	[Fact]
	public async Task RejectsTransitiveCyclesAndCrossProjectTargetsWithoutReplacingALiveWait() {
		var a = Add(owner, "a");
		var b = Add(producer, "b");
		var c = Add(producer, "c");
		Success(await Link(owner, a, producer.Info.Id, b));
		Success(await Link(producer, b, producer.Info.Id, c)); // Same agent, distinct task.
		Assert.True((await Link(producer, c, owner.Info.Id, a)).IsError);
		AssertTask(producer, c, TaskState.Open);
		Assert.True((await Link(owner, a, owner.Info.Id, a)).IsError);
		var (_, outsider) = manager.CreateProject("other", [dir.Path], null, false);
		var foreign = Add(outsider, "foreign");
		Assert.True((await Link(owner, a, outsider.Info.Id, foreign)).IsError);
		Assert.Throws<ToolException>(() => owner.UpdateTask(a, TaskState.Waiting, "foreign", new(outsider.Info.Id, foreign)));
		AssertTask(owner, a, TaskState.Waiting, new(producer.Info.Id, b));
		AssertTask(producer, b, TaskState.Waiting, new(producer.Info.Id, c));
	}
}
