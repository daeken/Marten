using System.Text.Json;
using Marten.Agents;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class LoopBreakerTests {
	static readonly DateTimeOffset T0 = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void TripsOnABackAndForthButNotAOneWayBurst() {
		var b = new LoopBreaker(() => new Config { LoopMessages = 8, LoopWindowMinutes = 10 });
		for(var i = 0; i < 20; i++) Assert.Null(b.Record("orch", "task", T0.AddSeconds(i)));
		var trips = new List<int?>();
		for(var i = 0; i < 8; i++) trips.Add(i % 2 == 0 ? b.Record("a", "b", T0.AddSeconds(i)) : b.Record("b", "a", T0.AddSeconds(i)));
		Assert.All(trips.Take(7), t => Assert.Null(t));
		Assert.Equal(8, trips[7]);
		// The pair starts over after tripping.
		Assert.Null(b.Record("a", "b", T0.AddSeconds(9)));
	}

	[Fact]
	public void OldSendsFallOutOfTheWindowAndSeraResets() {
		var b = new LoopBreaker(() => new Config { LoopMessages = 4, LoopWindowMinutes = 1 });
		b.Record("a", "b", T0); b.Record("b", "a", T0); b.Record("a", "b", T0);
		Assert.Null(b.Record("b", "a", T0.AddMinutes(2)));
		b.Record("a", "b", T0.AddMinutes(2)); b.Record("b", "a", T0.AddMinutes(2));
		b.Reset("b");
		Assert.Null(b.Record("a", "b", T0.AddMinutes(2)));
		var off = new LoopBreaker(() => new Config { LoopMessages = 0 });
		for(var i = 0; i < 50; i++) Assert.Null(off.Record(i % 2 == 0 ? "a" : "b", i % 2 == 0 ? "b" : "a", T0));
	}

	[Fact]
	public async Task ATripRefusesTheSendPausesBothAndTellsSera() {
		using var dir = new TempDir();
		Directory.CreateDirectory(dir.File("repo"));
		AgentManager? m = null;
		Func<AgentManager> get = () => m!;
		var tools = new ToolRegistry().Add(new SendMessageTool(get));
		m = new AgentManager(new Home(dir.File("home")), new Config { LoopMessages = 4 }, new RoutedBackend(), tools);
		var (_, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		var a = m.SpawnTask(orch, "ta", false, null, null);
		var b = m.SpawnTask(orch, "tb", false, null, null);
		string Send(Agent from, Agent to) => JsonSerializer.Serialize(new { to = to.Info.Id, text = "ping", mode = "queued" });

		for(var i = 0; i < 3; i++) {
			var (from, to) = i % 2 == 0 ? (a, b) : (b, a);
			Assert.False((await m.Tools.InvokeAsync(from, "send_message", Send(from, to), CancellationToken.None)).IsError);
		}
		var r = await m.Tools.InvokeAsync(b, "send_message", Send(b, a), CancellationToken.None);
		Assert.True(r.IsError);
		Assert.Contains("loop", r.Text);
		Assert.True(a.IsPaused && b.IsPaused);
		Assert.Contains(m.SeraInbox(), s => s.Text.Contains("Loop breaker"));
	}
}
