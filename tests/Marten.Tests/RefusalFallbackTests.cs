using Marten.Agents;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

/// <summary>A provider that blocks a request on policy grounds must not end the turn: the agent moves to
/// the fallback model and is told what was refused, where it is now, and what it can do about it.</summary>
public class RefusalFallbackTests {
	sealed class RefusingBackend(Func<ModelRequest, Task<ModelResult>> complete) : IModelBackend {
		public readonly List<ModelRequest> Requests = new();
		public string Provider => "fake";
		public bool Connected => true;
		public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["gpt-6"]);
		public Task<ModelResult> CompleteAsync(ModelRequest request, Action<string> onTextDelta, CancellationToken ct) {
			lock(Requests) Requests.Add(request);
			return complete(request);
		}
	}

	static AgentManager Manager(TempDir dir, IModelBackend backend, Config config) =>
		new(new Home(dir.File("home")), config, backend, new ToolRegistry().Add(new ReadFileTool()).Add(new EditFileTool()));

	static bool Idle(Agent a) => a.Status != AgentStatus.Running && a.Transcript.Last?.Kind == EntryKind.Idle;

	static ModelRefusedException Refused() =>
		new("The model refused the request.", "I'm sorry, but I can't help with that.", "refusal");

	/// <summary>The refusal notice, told apart from the other runtime notes a first turn writes.</summary>
	static Entry Notice(Agent agent) =>
		Assert.Single(agent.Transcript.Entries, e => e.Kind == EntryKind.Message && e.From == Agent.NudgeSender && e.Text!.Contains("flagged this request"));

	[Fact]
	public async Task ARefusalMovesTheAgentToTheFallbackAndExplainsWhatHappened() {
		using var dir = new TempDir();
		var backend = new RefusingBackend(request => request.Model == "gpt-6"
			? Task.FromException<ModelResult>(Refused())
			: Task.FromResult(FakeBackend.Say("Carrying on.")));
		var manager = Manager(dir, backend, new Config { Model = "gpt-6", RefusalFallbackModel = "deepseek-flash" });
		var agent = manager.CreateChat("chat", dir.Path);

		agent.Send(new Inbound("Sera", "do the thing", SendMode.Queued));
		await Wait.Until(() => Idle(agent));

		Assert.Equal(AgentStatus.Idle, agent.Status);
		Assert.Equal(["gpt-6", "deepseek-flash"], backend.Requests.Select(r => r.Model));
		Assert.Equal("deepseek-flash", agent.Info.Model);
		Assert.DoesNotContain(agent.Transcript.Entries, e => e.Kind == EntryKind.Error);
		var notice = Notice(agent);
		Assert.Contains("gpt-6 flagged this request", notice.Text!);
		Assert.Contains("I'm sorry, but I can't help with that.", notice.Text!);
		Assert.Contains("You're on deepseek-flash now", notice.Text!);
		Assert.Contains("Stay here", notice.Text!);
		Assert.Contains("start a fresh chat with more context", notice.Text!);
		Assert.Contains("say so plainly in your reply to Sera", notice.Text!);
		// The notice reaches the fallback model's own request, not just the transcript.
		Assert.Contains(backend.Requests[1].History, e => e.Kind == EntryKind.Message && e.Text == notice.Text);
	}

	[Fact]
	public async Task ATaskAgentIsToldToGoThroughItsOrchestrator() {
		using var dir = new TempDir();
		var backend = new RefusingBackend(request => request.Model == "gpt-6"
			? Task.FromException<ModelResult>(Refused())
			: Task.FromResult(FakeBackend.Say("Carrying on.")));
		var manager = Manager(dir, backend, new Config { Model = "gpt-6", RefusalFallbackModel = "deepseek-flash" });
		var (_, parent) = manager.CreateProject("p", [dir.Path], null, false);
		var worker = manager.SpawnTask(parent, "worker", false, null, null);

		worker.Send(new Inbound("Sera", "do the thing", SendMode.Queued));
		await Wait.Until(() => Idle(worker));

		var notice = Notice(worker);
		Assert.Contains("Ask your orchestrator (send_message) to spawn a fresh agent", notice.Text!);
		Assert.Contains("tell your orchestrator with send_message", notice.Text!);
	}

	[Fact]
	public async Task ARefusalOnTheFallbackItselfEndsTheTurn() {
		using var dir = new TempDir();
		var backend = new RefusingBackend(_ => Task.FromException<ModelResult>(Refused()));
		var manager = Manager(dir, backend, new Config { Model = "gpt-6", RefusalFallbackModel = "deepseek-flash" });
		var agent = manager.CreateChat("chat", dir.Path);

		agent.Send(new Inbound("Sera", "do the thing", SendMode.Queued));
		await Wait.Until(() => agent.Status == AgentStatus.Error && Idle(agent));

		// One fallback, never a second, and never a retry of the model that refused.
		Assert.Equal(["gpt-6", "deepseek-flash"], backend.Requests.Select(r => r.Model));
		Assert.Contains(agent.Transcript.Entries, e => e.Kind == EntryKind.Error && e.Text!.Contains("refused the request"));
	}

	[Fact]
	public async Task WithoutAFallbackARefusalIsATerminalTurnError() {
		using var dir = new TempDir();
		var backend = new RefusingBackend(_ => Task.FromException<ModelResult>(Refused()));
		var manager = Manager(dir, backend, new Config { Model = "gpt-6", RefusalFallbackModel = null });
		var agent = manager.CreateChat("chat", dir.Path);

		agent.Send(new Inbound("Sera", "do the thing", SendMode.Queued));
		await Wait.Until(() => agent.Status == AgentStatus.Error && Idle(agent));

		Assert.Single(backend.Requests);
		Assert.Contains(agent.Transcript.Entries, e => e.Kind == EntryKind.Error);
		Assert.DoesNotContain(agent.Transcript.Entries, e => e.Kind == EntryKind.Message && e.Text!.Contains("flagged this request"));
		Assert.Null(agent.Info.Model);
	}

	[Fact]
	public async Task ARefusalFromTheFallbacksOwnModelDoesNotFallBackToItself() {
		using var dir = new TempDir();
		var backend = new RefusingBackend(_ => Task.FromException<ModelResult>(Refused()));
		var manager = Manager(dir, backend, new Config { Model = "deepseek-flash", RefusalFallbackModel = "deepseek-flash" });
		var agent = manager.CreateChat("chat", dir.Path);

		agent.Send(new Inbound("Sera", "do the thing", SendMode.Queued));
		await Wait.Until(() => agent.Status == AgentStatus.Error && Idle(agent));

		Assert.Single(backend.Requests);
		Assert.Contains(agent.Transcript.Entries, e => e.Kind == EntryKind.Error);
	}
}
