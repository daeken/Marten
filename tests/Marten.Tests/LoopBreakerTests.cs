using System.Text.Json;
using Marten.Agents;
using Marten.Storage;
using Marten.Tools;
using static Marten.Agents.LoopBreaker;

namespace Marten.Tests;

public class LoopBreakerTests {
	static readonly DateTimeOffset T0 = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void TripsOnABackAndForthButNotAOneWayBurst() {
		var b = new LoopBreaker(() => new Config { LoopMessages = 8, LoopWindowMinutes = 10 });
		for(var i = 0; i < 20; i++) Assert.Null(b.Admit("orch", "task", T0.AddSeconds(i)));
		Assert.Null(b.Admit("task", "orch", T0.AddSeconds(20))); // 1 reply is not enough.
		Assert.Equal(Status.Challenge, b.Admit("task", "orch", T0.AddSeconds(21))!.Status);
		for(var i = 0; i < 7; i++)
			Assert.Null(i % 2 == 0 ? b.Admit("a", "b", T0.AddSeconds(22 + i)) : b.Admit("b", "a", T0.AddSeconds(22 + i)));
		Assert.Equal(Status.Challenge, b.Admit("b", "a", T0.AddSeconds(29))!.Status);
		Assert.Equal(Status.Pending, b.Admit("a", "b", T0.AddSeconds(30))!.Status);
	}

	[Fact]
	public void OldSendsFallOutOfTheWindowAndSeraResets() {
		var b = new LoopBreaker(() => new Config { LoopMessages = 4, LoopWindowMinutes = 1 });
		b.Admit("a", "b", T0); b.Admit("b", "a", T0); b.Admit("a", "b", T0);
		var later = T0.AddMinutes(2);
		Assert.Null(b.Admit("b", "a", later));
		b.Admit("a", "b", later); b.Admit("b", "a", later);
		var hold = b.Admit("a", "b", later)!;
		Assert.Equal(Status.Challenge, hold.Status);
		b.Reset("b");
		Assert.Equal(Status.Unavailable, b.Respond("a", "b", hold.ChallengeId!.Value, true, later).Status);
		Assert.Null(b.Admit("a", "b", later));
		var off = new LoopBreaker(() => new Config { LoopMessages = 0 });
		for(var i = 0; i < 50; i++) Assert.Null(off.Admit(i % 2 == 0 ? "a" : "b", i % 2 == 0 ? "b" : "a", T0));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ChillAndTimeoutSuppressRetriesWithoutExtendingCooldown(bool respond) {
		var b = new LoopBreaker(() => new Config { LoopMessages = 2 });
		Assert.Null(b.Admit("a", "b", T0));
		var hold = b.Admit("b", "a", T0)!;
		var deadline = hold.Until!.Value;
		Assert.Equal(Status.Pending, b.Admit("b", "a", deadline.AddTicks(-1))!.Status);
		var start = respond ? deadline.AddSeconds(-10) : deadline;
		if(respond) Assert.Equal(Status.Chill, b.Respond("b", "a", hold.ChallengeId!.Value, false, start).Status);
		else Assert.Equal(Status.Cooling, b.Respond("b", "a", hold.ChallengeId!.Value, true, start).Status);
		var cool = b.Admit("b", "a", start)!;
		Assert.Equal(Status.Cooling, cool.Status);
		Assert.Equal(cool, b.Admit("a", "b", cool.Until!.Value.AddTicks(-1)));
		Assert.Null(b.Admit("b", "a", cool.Until.Value));
		Assert.Equal(Status.Challenge, b.Admit("a", "b", cool.Until.Value)!.Status);
	}

	[Fact]
	public void AContinuationCannotBeBankedBeyondItsWindow() {
		var b = new LoopBreaker(() => new Config { LoopMessages = 2, LoopWindowMinutes = 1 });
		Assert.Null(b.Admit("a", "b", T0));
		var hold = b.Admit("b", "a", T0)!;
		var grant = b.Respond("b", "a", hold.ChallengeId!.Value, true, T0);
		Assert.Equal(T0.AddMinutes(1), grant.Until);
		Assert.Null(b.Admit("b", "a", T0.AddSeconds(59)));
		Assert.Equal(Status.Challenge, b.Admit("a", "b", T0.AddMinutes(1))!.Status);
	}

	static (AgentManager M, Agent A, Agent B, Agent C) Setup(TempDir dir) {
		Directory.CreateDirectory(dir.File("repo"));
		AgentManager? m = null;
		Func<AgentManager> get = () => m!;
		var tools = new ToolRegistry().Add(new SendMessageTool(get)).Add(new RespondMessageLoopTool(get)).Add(new CallToolTool());
		m = new AgentManager(new Home(dir.File("home")), new Config { LoopMessages = 4 }, new RoutedBackend(), tools);
		var (_, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		var a = m.SpawnTask(orch, "ta", false, null, null);
		var b = m.SpawnTask(orch, "tb", false, null, null);
		return (m, a, b, m.SpawnTask(orch, "tc", false, null, null));
	}

	static async Task Prime(AgentManager m, Agent a, Agent b) {
		Assert.False((await Send(m, a, b)).IsError);
		Assert.False((await Send(m, b, a)).IsError);
		Assert.False((await Send(m, a, b)).IsError);
	}

	static Task<ToolOutput> Call(AgentManager m, Agent from, string name, object args, bool delegated = false) => delegated
		? m.Tools.InvokeAsync(from, "call_tool", JsonSerializer.Serialize(new { name, arguments = args }), default)
		: m.Tools.InvokeAsync(from, name, JsonSerializer.Serialize(args), default);
	static Task<ToolOutput> Send(AgentManager m, Agent from, Agent to, string text = "ping", bool delegated = false) =>
		Call(m, from, "send_message", new { to = to.Info.Id, text, mode = "queued" }, delegated);
	static Task<ToolOutput> Decide(AgentManager m, Agent from, Agent to, string id, string decision, string? reason = null, bool delegated = false) =>
		Call(m, from, "respond_message_loop", new { peer_id = to.Info.Id, challenge_id = id, decision, reason }, delegated);
	static JsonElement Check(ToolOutput output, string status, bool isError = false) {
		Assert.Equal(isError, output.IsError);
		using var doc = JsonDocument.Parse(output.Text);
		Assert.Equal(status, doc.RootElement.GetProperty("status").GetString());
		return doc.RootElement.Clone();
	}

	[Fact]
	public async Task OwnerCanContinueAndRetryManuallyForAFiniteSharedBudget() {
		using var dir = new TempDir();
		var (m, a, b, _) = Setup(dir);
		await Prime(m, a, b);
		Assert.Equal(3, m.AgentMessages().Count);
		var hold = Check(await Send(m, b, a, "need the test path", delegated: true), "loop_check", isError: true);
		var id = hold.GetProperty("challenge_id").GetString()!;
		Assert.Equal(a.Info.Id, hold.GetProperty("peer_id").GetString());
		Assert.Equal("respond_message_loop", hold.GetProperty("response_tool").GetString());
		Check(await Decide(m, a, b, id, "continue", "Need the test path"), "loop_check_unavailable", isError: true);
		Assert.True((await Decide(m, b, a, id, "continue", "  ")).IsError);
		Assert.Equal(id, Check(await Send(m, b, a), "loop_pending", isError: true).GetProperty("challenge_id").GetString());
		var grant = Check(await Decide(m, b, a, id, "continue", "Need the exact failing test path to investigate", delegated: true), "continued");
		Assert.Equal(4, grant.GetProperty("message_budget").GetInt32());
		Assert.Equal(3, m.AgentMessages().Count); // No replay or held attempt counted as delivered.
		Assert.False((await Send(m, b, a, "need the test path", delegated: true)).IsError);
		for(var i = 0; i < 3; i++) Assert.False((await Send(m, a, b)).IsError);
		Assert.Equal("need the test path", Assert.Single(m.AgentMessages(), msg => msg.Text == "need the test path").Text);
		var next = Check(await Send(m, a, b), "loop_check", isError: true);
		Assert.NotEqual(id, next.GetProperty("challenge_id").GetString());
		Check(await Decide(m, a, b, id, "chill"), "loop_check_unavailable", isError: true);
		Assert.Equal(7, m.AgentMessages().Count);
		Assert.False(a.IsPaused || b.IsPaused);
		Assert.Empty(m.SeraInbox());
	}

	[Fact]
	public async Task PowerShellSendAndResponseUseTheSameCheck() {
		using var dir = new TempDir();
		var (m, a, b, _) = Setup(dir);
		await Prime(m, a, b);
		var ps = new PowerShellTool(() => 30, () => 3, new ChildProcessWatcher());
		var script = $"send_message -to '{a.Info.Id}' -text 'from PowerShell' -mode queued";
		var held = await ps.RunAsync(b, script, TimeSpan.FromSeconds(30), default);
		Assert.Contains("loop_check", held.Text); // PowerShell renders the registry error in its error stream.
		var pending = Check(await Send(m, b, a), "loop_pending", isError: true);
		var id = pending.GetProperty("challenge_id").GetString();
		var chill = await ps.RunAsync(b, $"respond_message_loop -peer_id '{a.Info.Id}' -challenge_id '{id}' -decision chill -reason $null", TimeSpan.FromSeconds(30), default);
		Check(chill, "chill");
		Assert.Equal(3, m.AgentMessages().Count);
	}

	[Fact]
	public async Task ChillSuppressesBothDirectionsButOtherPairsAndSeraAreUnaffected() {
		using var dir = new TempDir();
		var (m, a, b, c) = Setup(dir);
		await Prime(m, a, b);
		var hold = Check(await Send(m, b, a), "loop_check", isError: true);
		var id = hold.GetProperty("challenge_id").GetString()!;
		var pending = Check(await Send(m, a, b), "loop_pending", isError: true);
		Assert.Equal(JsonValueKind.Null, pending.GetProperty("challenge_id").ValueKind);
		Assert.False((await Send(m, b, c)).IsError);
		var chill = Check(await Decide(m, b, a, id, "chill"), "chill");
		Assert.Contains("do other work or go idle", chill.GetProperty("instruction").GetString());
		Check(await Send(m, b, a), "chill", isError: true);
		Check(await Send(m, a, b), "chill", isError: true);
		Assert.False((await Send(m, c, b)).IsError);
		Assert.Equal(5, m.AgentMessages().Count);
		Assert.False(a.IsPaused || b.IsPaused || c.IsPaused);
		Assert.Empty(m.SeraInbox());
		a.Send(new Inbound("Sera", "Please coordinate.", SendMode.Queued));
		Assert.False((await Send(m, b, a)).IsError);
	}
}
