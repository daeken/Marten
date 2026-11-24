using Marten.Storage;

namespace Marten.Agents;

/// <summary>
/// Gates internal sends pair by pair, without pausing agents. Outside clients keep their legacy
/// pause behavior. A message from Sera to either participant clears that pair's state.
/// </summary>
public sealed class LoopBreaker(Func<Config> config) {
	readonly object gate = new();
	readonly List<(DateTimeOffset At, string From, string To)> sends = new();
	readonly Dictionary<(string A, string B), Exchange> exchanges = new();
	static readonly TimeSpan DecisionTime = TimeSpan.FromMinutes(1), Cooldown = TimeSpan.FromMinutes(1);

	internal enum Status { Challenge, Pending, Chill, Cooling, Continued, Unavailable }
	internal sealed record Result(Status Status, Guid? ChallengeId = null, DateTimeOffset? Until = null, int? Budget = null);
	sealed record Decision(Guid Id, string Owner, DateTimeOffset Deadline);
	sealed class Exchange {
		public readonly Queue<DateTimeOffset> Forward = new(), Reverse = new();
		public Decision? Pending;
		public DateTimeOffset? CoolUntil, FreshUntil;
		public int Remaining;
	}

	static (string A, string B) Pair(string a, string b) => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

	public void Reset(string agentId) {
		lock(gate) {
			sends.RemoveAll(s => s.From == agentId || s.To == agentId);
			foreach(var pair in exchanges.Keys.Where(p => p.A == agentId || p.B == agentId).ToArray()) exchanges.Remove(pair);
		}
	}

	static void Trim(Queue<DateTimeOffset> queue, DateTimeOffset now, TimeSpan window) {
		while(queue.TryPeek(out var at) && now - at > window) queue.Dequeue();
	}

	// Lazy expiry: no timers, wakeups, or retained message text. A timeout's cooldown starts at
	// the decision deadline, regardless of when the pair next tries to send.
	void Prune(DateTimeOffset now, TimeSpan window) {
		foreach(var (pair, exchange) in exchanges) {
			if(exchange.Pending is { } pending && now >= pending.Deadline) {
				exchange.Pending = null;
				exchange.CoolUntil = pending.Deadline + Cooldown;
			}
			if(now >= exchange.CoolUntil) exchange.CoolUntil = null;
			if(now >= exchange.FreshUntil) exchange.FreshUntil = null;
			Trim(exchange.Forward, now, window);
			Trim(exchange.Reverse, now, window);
			if(exchange.Pending == null && exchange.CoolUntil == null && exchange.FreshUntil == null &&
				exchange.Forward.Count == 0 && exchange.Reverse.Count == 0) exchanges.Remove(pair);
		}
	}

	/// <summary>Admit and count an internal send, or withhold it without storing it.</summary>
	internal Result? Admit(string from, string to, DateTimeOffset? at = null) {
		var c = config();
		lock(gate) {
			if(c.LoopMessages <= 0) { exchanges.Clear(); return null; }
			var now = at ?? DateTimeOffset.UtcNow;
			Prune(now, TimeSpan.FromMinutes(c.LoopWindowMinutes));
			var pair = Pair(from, to);
			if(!exchanges.TryGetValue(pair, out var exchange)) exchanges[pair] = exchange = new();
			if(exchange.Pending is { } pending)
				return new(Status.Pending, pending.Owner == from ? pending.Id : null, pending.Deadline);
			if(exchange.CoolUntil is { } coolUntil) return new(Status.Cooling, Until: coolUntil);

			var outgoing = from == pair.A ? exchange.Forward : exchange.Reverse;
			var incoming = from == pair.A ? exchange.Reverse : exchange.Forward;
			var minimum = Math.Max(1, c.LoopMessages / 4);
			var trip = exchange.FreshUntil != null ? exchange.Remaining == 0 :
				(long) outgoing.Count + incoming.Count + 1 >= c.LoopMessages && outgoing.Count + 1 >= minimum && incoming.Count >= minimum;
			if(trip) {
				var challenge = new Decision(Guid.NewGuid(), from, now + DecisionTime);
				exchange.Pending = challenge;
				exchange.FreshUntil = null;
				outgoing.Clear();
				incoming.Clear();
				return new(Status.Challenge, challenge.Id, challenge.Deadline);
			}
			if(exchange.FreshUntil != null) exchange.Remaining--;
			// Only the latest N timestamps per direction can affect the threshold (even during one-way bursts).
			while(outgoing.Count >= c.LoopMessages) outgoing.Dequeue();
			outgoing.Enqueue(now);
			return null;
		}
	}

	internal Result Respond(string from, string to, Guid challengeId, bool continueExchange, DateTimeOffset? at = null) {
		var c = config();
		var window = TimeSpan.FromMinutes(c.LoopWindowMinutes);
		lock(gate) {
			if(c.LoopMessages <= 0) { exchanges.Clear(); return new(Status.Unavailable); }
			var now = at ?? DateTimeOffset.UtcNow;
			Prune(now, window);
			if(!exchanges.TryGetValue(Pair(from, to), out var exchange)) return new(Status.Unavailable);
			if(exchange.CoolUntil is { } coolUntil) return new(Status.Cooling, Until: coolUntil);
			if(exchange.Pending is not { } pending || pending.Id != challengeId || pending.Owner != from)
				return new(Status.Unavailable);
			exchange.Pending = null;
			if(!continueExchange) {
				exchange.CoolUntil = now + Cooldown;
				return new(Status.Chill, Until: exchange.CoolUntil);
			}
			exchange.FreshUntil = now + window;
			exchange.Remaining = c.LoopMessages;
			return new(Status.Continued, Until: exchange.FreshUntil, Budget: exchange.Remaining);
		}
	}

	/// <summary>Legacy outside-client breaker; returns the attempted count when it trips.</summary>
	public int? RecordOutside(string from, string to, DateTimeOffset now) {
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
