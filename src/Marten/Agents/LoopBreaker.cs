using Marten.Storage;

namespace Marten.Agents;

/// <summary>
/// Catches two agents messaging back and forth without Sera. When a pair passes LoopMessages
/// within LoopWindowMinutes, with real traffic both ways, the send that tips it is refused, both
/// agents are paused, and Sera is told. A message from Sera to either agent clears its history.
/// </summary>
public sealed class LoopBreaker(Func<Config> config) {
	readonly object gate = new();
	readonly List<(DateTimeOffset At, string From, string To)> sends = new();

	public void Reset(string agentId) {
		lock(gate) sends.RemoveAll(s => s.From == agentId || s.To == agentId);
	}

	/// <summary>Record a send. Returns how many messages the pair exchanged when it trips, otherwise null.</summary>
	public int? Record(string from, string to, DateTimeOffset now) {
		var c = config();
		if(c.LoopMessages <= 0) return null;
		var window = TimeSpan.FromMinutes(c.LoopWindowMinutes);
		lock(gate) {
			sends.RemoveAll(s => now - s.At > window);
			sends.Add((now, from, to));
			bool InPair((DateTimeOffset At, string From, string To) s) => (s.From == from && s.To == to) || (s.From == to && s.To == from);
			var pair = sends.Where(InPair).ToList();
			var forward = pair.Count(s => s.From == from);
			// One-way bursts (an orchestrator briefing a task agent) aren't loops; a conversation is.
			if(pair.Count < c.LoopMessages || Math.Min(forward, pair.Count - forward) < Math.Max(1, c.LoopMessages / 4)) return null;
			sends.RemoveAll(InPair);
			return pair.Count;
		}
	}
}
