using System.Text.Json.Nodes;
using Marten.Agents;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class ScheduleTests {
	static AgentManager Manager(TempDir dir, IModelBackend backend) {
		var tools = new ToolRegistry().Add(new ReadFileTool())
			.Add(new PowerShellTool(() => 30, () => 3, new ChildProcessWatcher()))
			.Add(new ScheduleAddTool()).Add(new ScheduleListTool()).Add(new ScheduleRemoveTool());
		return new AgentManager(new Home(dir.File("home")), new Config(), backend, tools);
	}

	static bool Idle(Agent a) => a.Status == AgentStatus.Idle && a.Transcript.Last?.Kind == EntryKind.Idle && a.PendingCount == 0;

	[Fact]
	public async Task OutputWakesTheOwnerAndARunIsSkippedWhileTheLastOutputIsUndelivered() {
		using var dir = new TempDir();
		var backend = new FakeBackend { Gate = new() };
		backend.Script.Enqueue(_ => FakeBackend.Say("first"));
		backend.Script.Enqueue(_ => FakeBackend.Say("saw the schedule"));
		var m = Manager(dir, backend);
		var agent = m.CreateChat("t", dir.Path);
		var now = DateTimeOffset.UtcNow;
		agent.AddSchedule("'ci is green'", "watch CI", now, 60, null);
		var scheduler = new Scheduler(m);

		// The agent is mid-turn, so the output waits in its inbox.
		agent.Send(new Inbound("Sera", "hi", SendMode.Queued));
		await Wait.Until(() => backend.Requests.Count == 1);
		await Task.WhenAll(scheduler.Tick(now));
		Assert.Equal(1, agent.PendingCount);
		Assert.Empty(scheduler.Tick(now.AddSeconds(61)));
		Assert.StartsWith("skipped", agent.Schedules[0].LastResult);
		Assert.Equal(now.AddSeconds(120), agent.Schedules[0].NextRun);

		backend.Gate.SetResult();
		await Wait.Until(() => Idle(agent) && backend.Requests.Count == 2);
		var wake = agent.Transcript.Entries.Last(e => e.Kind == EntryKind.Wake);
		Assert.Equal("schedule", wake.Reason);
		Assert.Contains(agent.Transcript.Entries, e => e.Kind == EntryKind.Message && e.Text!.StartsWith("Schedule #1 (watch CI):") && e.Text.Contains("ci is green"));
		// Delivered, so the next run goes ahead.
		var next = scheduler.Tick(now.AddSeconds(121));
		Assert.Equal(1, next.Count);
		await Task.WhenAll(next);
	}

	[Fact]
	public async Task NoOutputMeansNothingHappens() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		var m = Manager(dir, backend);
		var agent = m.CreateChat("t", dir.Path);
		agent.AddSchedule("if($false) { 'never' }", null, DateTimeOffset.UtcNow, null, null);
		await Task.WhenAll(new Scheduler(m).Tick(DateTimeOffset.UtcNow));
		Assert.Equal(0, agent.PendingCount);
		Assert.Empty(backend.Requests);
		Assert.Equal("no output", agent.Schedules[0].LastResult);
		Assert.True(agent.Schedules[0].Done);
	}

	[Fact]
	public async Task PausedOrClosedOwnersHoldOneShotsAndSkipIntervals() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Say("ok"));
		var m = Manager(dir, backend);
		var agent = m.CreateChat("t", dir.Path);
		var now = DateTimeOffset.UtcNow;
		agent.AddSchedule("'once'", null, now, null, null);
		agent.AddSchedule("'tick'", null, now, 30, null);
		var scheduler = new Scheduler(m);

		agent.SetPaused(true);
		Assert.Empty(scheduler.Tick(now));
		Assert.False(agent.Schedules[0].Done);
		Assert.Equal(now.AddSeconds(30), agent.Schedules[1].NextRun);
		agent.SetPaused(false);
		agent.SetClosed(true);
		Assert.Empty(scheduler.Tick(now.AddSeconds(1)));
		agent.SetClosed(false);

		var runs = scheduler.Tick(now.AddSeconds(2));
		Assert.Equal(1, runs.Count);
		await Task.WhenAll(runs);
		Assert.True(agent.Schedules[0].Done);
		Assert.Empty(scheduler.Tick(now.AddSeconds(3)));
	}

	[Fact]
	public void MissedIntervalRunsAreSkippedAtStartup() {
		using var dir = new TempDir();
		var m = Manager(dir, new FakeBackend());
		var agent = m.CreateChat("t", dir.Path);
		var now = DateTimeOffset.UtcNow;
		agent.AddSchedule("'x'", null, now.AddMinutes(-10), 60, null);
		agent.AddSchedule("'y'", null, now.AddMinutes(-10), null, null);
		new Scheduler(m).SkipMissedIntervals(now);
		Assert.Equal(now.AddMinutes(-10).AddSeconds(660), agent.Schedules[0].NextRun);
		Assert.Equal(now.AddMinutes(-10), agent.Schedules[1].NextRun);
	}

	[Fact]
	public async Task TheToolTakesExactlyOneOfAtOrEvery() {
		using var dir = new TempDir();
		var agent = Manager(dir, new FakeBackend()).CreateChat("t", dir.Path);
		var tool = new ScheduleAddTool();
		JsonObject Args(string? at, string? every) => new() { ["command"] = "'x'", ["at"] = at, ["every"] = every, ["description"] = null, ["timeout_seconds"] = null };

		Assert.StartsWith("Schedule #1 runs every 15m", (await tool.InvokeAsync(agent, Args(null, "15m"), default)).Text);
		var before = DateTimeOffset.UtcNow;
		await tool.InvokeAsync(agent, Args("10m", null), default);
		Assert.InRange(agent.Schedules[1].NextRun, before.AddMinutes(10), DateTimeOffset.UtcNow.AddMinutes(10));
		await tool.InvokeAsync(agent, Args("2030-01-02T03:04:05Z", null), default);
		Assert.Equal(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero), agent.Schedules[2].NextRun);
		await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(agent, Args("10m", "15m"), default));
		await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(agent, Args(null, "5s"), default));
		await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(agent, Args(null, "soon"), default));

		Assert.Contains("#2 once", (await new ScheduleListTool().InvokeAsync(agent, new JsonObject(), default)).Text);
		await new ScheduleRemoveTool().InvokeAsync(agent, new JsonObject { ["id"] = 2 }, default);
		Assert.DoesNotContain(agent.Schedules, s => s.Id == 2);
	}
}
