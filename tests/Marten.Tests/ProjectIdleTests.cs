using Marten.Agents;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class ProjectIdleTests {
	static AgentManager Manager(TempDir dir, IModelBackend? backend = null) =>
		new(new Home(dir.File("home")), new Config { Model = "fake-1" }, backend ?? new RoutedBackend(), new ToolRegistry());

	static TaskItem Add(Agent owner, string title, TaskState state = TaskState.Waiting) {
		var task = owner.AddTask(title, owner.Info.Id);
		return state == TaskState.Open ? task : owner.UpdateTask(task.Id, state, "review pending").After;
	}

	static bool Idle(Agent agent) => agent.Status == AgentStatus.Idle && agent.Transcript.Last?.Kind == EntryKind.Idle && agent.PendingCount == 0;
	static int Notices(Agent agent) => agent.Transcript.Entries.Count(e => e.Kind == EntryKind.Wake && e.Reason == "project_idle");
	static void NoNotice(Agent agent) {
		Assert.Equal(0, agent.PendingCount);
		Assert.Equal(0, Notices(agent));
	}

	[Fact]
	public async Task WaitingOrchestratorGetsContextOncePerIdleEpisode() {
		using var dir = new TempDir();
		var m = Manager(dir);
		var (_, orch) = m.CreateProject("p", [dir.Path], null, false);
		var worker = m.SpawnTask(orch, "builder", false, null, null);
		var review = Add(orch, "Review artifacts");
		var docs = Add(orch, "Verify manual");
		var scheduler = new Scheduler(m);
		var now = DateTimeOffset.UtcNow;
		void Tick() { scheduler.Tick(now); now = now.AddMinutes(1); }

		Tick();
		await Wait.Until(() => Notices(orch) == 1 && Idle(orch));
		var note = Assert.Single(orch.Transcript.Entries, e => e.Kind == EntryKind.Message && e.From == Agent.NudgeSender && e.Text!.Contains(review.Title));
		Assert.Contains(docs.Title, note.Text);
		Assert.Equal([review, docs], orch.Tasks);

		Tick();
		Assert.Equal(0, orch.PendingCount);
		Assert.Equal(1, Notices(orch));

		// A complete worker turn *between* checks counts, even though the scheduler never saw it running.
		worker.Send(new Inbound("Sera", "inspect the artifacts", SendMode.Queued));
		await Wait.Until(() => Idle(worker));
		Tick();
		await Wait.Until(() => Notices(orch) == 2 && Idle(orch));

		var extra = Add(orch, "Review updated manifest");
		Tick();
		await Wait.Until(() => Notices(orch) == 3 && Idle(orch));
		Assert.Contains(orch.Transcript.Entries, e => e.Kind == EntryKind.Message && e.Text!.Contains(extra.Title));
		Assert.Equal([review, docs, extra], orch.Tasks);
		NoNotice(worker);
	}

	[Theory]
	[InlineData("orchestrator paused")]
	[InlineData("project paused")]
	[InlineData("globally paused")]
	[InlineData("worker paused")]
	[InlineData("orchestrator closed")]
	public void DoesNotWakeThroughPauseOrClose(string condition) {
		using var dir = new TempDir();
		var m = Manager(dir);
		var (project, orch) = m.CreateProject("p", [dir.Path], null, false);
		m.SpawnTask(orch, "idle", false, null, null);
		var worker = m.SpawnTask(orch, "other", false, null, null);
		Add(orch, "Review");
		switch(condition) {
			case "orchestrator paused": orch.SetPaused(true); break;
			case "project paused": m.SetProjectPaused(project, true); break;
			case "globally paused": m.SetPausedAll(true); break;
			case "worker paused": worker.SetPaused(true); break;
			case "orchestrator closed": orch.SetClosed(true); break;
		}
		new Scheduler(m).Tick(DateTimeOffset.UtcNow);
		NoNotice(orch);
		if(condition == "orchestrator closed") Assert.True(orch.Info.Closed);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DoesNotWakeUntilBusyAgentFinishes(bool busyOrchestrator) {
		using var dir = new TempDir();
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var backend = new FakeBackend { Gate = gate };
		backend.Script.Enqueue(_ => FakeBackend.Say("finished"));
		backend.Script.Enqueue(_ => FakeBackend.Say("noted"));
		var m = Manager(dir, backend);
		var (_, orch) = m.CreateProject("p", [dir.Path], null, false);
		m.SpawnTask(orch, "idle", false, null, null);
		var worker = m.SpawnTask(orch, "other", false, null, null);
		var busy = busyOrchestrator ? orch : worker;
		var scheduler = new Scheduler(m);
		var now = DateTimeOffset.UtcNow;
		busy.Send(new Inbound("Sera", "work", SendMode.Queued));
		try {
			await Wait.Until(() => { lock(backend.Requests) return backend.Requests.Count == 1; });
			Add(orch, "Review");
			scheduler.Tick(now);
			NoNotice(orch);
		} finally { gate.TrySetResult(); }
		await Wait.Until(() => Idle(busy));
		scheduler.Tick(now.AddMinutes(1));
		await Wait.Until(() => Notices(orch) == 1 && Idle(orch));
	}

	[Fact]
	public void WaitingMustBeOnOrchestratorNotAWorkerOrChat() {
		using var dir = new TempDir();
		var m = Manager(dir);
		var (_, orch) = m.CreateProject("p", [dir.Path], null, false);
		Add(orch, "open", TaskState.Open);
		Add(orch, "blocked", TaskState.Blocked);
		Add(orch, "done", TaskState.Done);
		Add(m.SpawnTask(orch, "worker", false, null, null), "waiting");
		Add(m.CreateChat("other", dir.Path), "waiting");
		new Scheduler(m).Tick(DateTimeOffset.UtcNow);
		Assert.All(m.Agents, NoNotice);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task EmptyRosterQualifiesAndForeignWorkersDoNotCount(bool includeClosed) {
		using var dir = new TempDir();
		var m = Manager(dir);
		var (_, orch) = m.CreateProject("p", [dir.Path], null, false);
		var (_, other) = m.CreateProject("other", [dir.Path], null, false);
		m.SpawnTask(other, "suspended", false, null, null).SetPaused(true);
		if(includeClosed) {
			var closed = m.SpawnTask(orch, "former", false, null, null);
			closed.SetPaused(true);
			closed.SetClosed(true);
		}
		Add(orch, "Review mine");
		Add(other, "Review theirs");
		new Scheduler(m).Tick(DateTimeOffset.UtcNow);
		await Wait.Until(() => Notices(orch) == 1 && Idle(orch));
		Assert.DoesNotContain(orch.Transcript.Entries, e => e.Text?.Contains("Review theirs") == true);
		Assert.All(m.Agents.Where(a => a != orch), NoNotice);
	}
}
