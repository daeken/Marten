using System.Collections.Concurrent;
using System.Text.Json;
using Marten.Agents;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class PendingMessageTests {
	[Fact]
	public async Task OnlyComposerSendsHaveStableDistinctIdsAndOnlyQueuedCanBeRecalled() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Say("done"));
		var manager = new AgentManager(new Home(dir.File("home")), new Config(), backend, new ToolRegistry());
		var events = new ConcurrentQueue<JsonElement>();
		manager.Events += e => events.Enqueue(JsonSerializer.SerializeToElement(e, JsonIO.Options));
		var agent = manager.CreateChat("test", dir.Path);
		agent.SetPaused(true);
		agent.Send(new Inbound("Sera", "not a composer send", SendMode.Queued));
		agent.Send(new Inbound("Sera", "question answer", SendMode.Immediate, "answer"));
		agent.Send(new Inbound(Agent.NudgeSender, "nudge", SendMode.Queued));
		agent.SendOperator("same", SendMode.Queued);
		agent.SendOperator("same", SendMode.Queued);
		agent.SendOperator("please steer", SendMode.Immediate);
		var pending = agent.GetPendingMessages();
		Assert.Equal(3, pending.Length);
		Assert.Equal(3, pending.Select(m => m.Id).Distinct().Count());
		Assert.Equal(pending, agent.GetPendingMessages());
		Assert.Null(agent.RecallPendingMessage(pending[2].Id));
		Assert.Null(agent.RecallPendingMessage("invalid"));
		Assert.Equal(pending[0], agent.RecallPendingMessage(pending[0].Id));
		Assert.Null(agent.RecallPendingMessage(pending[0].Id));
		Assert.Equal(pending[1..], agent.GetPendingMessages());
		Assert.Equal(5, agent.PendingCount);
		var inbox = events.Last(e => e.GetProperty("type").GetString() == "inbox");
		Assert.Equal(5, inbox.GetProperty("pending").GetInt32());
		Assert.Equal(new[] { "agentId", "pending", "type" }, inbox.EnumerateObject().Select(p => p.Name).Order());

		agent.SetPaused(false);
		await Wait.Until(() => agent.Transcript.Last?.Kind == EntryKind.Idle);
		Assert.Empty(agent.GetPendingMessages());
		Assert.Single(agent.Transcript.Entries, e => e.Kind == EntryKind.Message && e.Text == "same");
		Assert.Contains(agent.Transcript.Entries, e => e.Kind == EntryKind.Message && e.Text == "please steer");
		Assert.Null(agent.RecallPendingMessage(pending[1].Id));
	}

	[Fact]
	public async Task RecallingLastPausedMessageDoesNotStartAnEmptyTurn() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		var manager = new AgentManager(new Home(dir.File("home")), new Config(), backend, new ToolRegistry());
		var agent = manager.CreateChat("test", dir.Path);
		agent.SetPaused(true);
		agent.SendOperator("cancel me", SendMode.Queued);
		await Wait.Until(() => agent.Status == AgentStatus.Paused);
		var message = Assert.Single(agent.GetPendingMessages());
		Assert.Equal(message, agent.RecallPendingMessage(message.Id));
		agent.SetPaused(false);
		await Wait.Until(() => agent.Status == AgentStatus.Idle);
		Assert.DoesNotContain(agent.Transcript.Entries, e => e.Kind == EntryKind.Wake || e.Text == message.Text);
		Assert.Empty(backend.Requests);
	}

	[Fact]
	public async Task RecallAndClaimHaveExactlyOneWinner() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Say("done"));
		var manager = new AgentManager(new Home(dir.File("home")), new Config(), backend, new ToolRegistry());
		var agent = manager.CreateChat("test", dir.Path);
		agent.SetPaused(true);
		agent.SendOperator("racing", SendMode.Queued);
		agent.Send(new Inbound("test", "keep the turn alive", SendMode.Queued));
		var message = Assert.Single(agent.GetPendingMessages());
		await Wait.Until(() => agent.Status == AgentStatus.Paused);
		var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var resume = Task.Run(async () => { await start.Task; agent.SetPaused(false); });
		var recall = Task.Run(async () => { await start.Task; return agent.RecallPendingMessage(message.Id); });
		start.SetResult();
		await resume;
		var removed = await recall;
		await Wait.Until(() => agent.Transcript.Last?.Kind == EntryKind.Idle);
		Assert.Equal(removed == null ? 1 : 0, agent.Transcript.Entries.Count(e => e.Kind == EntryKind.Message && e.Text == message.Text));
		Assert.Empty(agent.GetPendingMessages());
		Assert.Null(agent.RecallPendingMessage(message.Id));
	}
}
