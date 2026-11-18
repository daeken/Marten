using Marten.Models;

namespace Marten.Agents;

/// <summary>Independent jitter spreads outage probes. A shared start interval also staggers
/// completion retries that become eligible together (e.g. after a pause or a slot queue).</summary>
internal sealed class NetworkRecoveryPacer(TimeProvider clock) {
	static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
	readonly object gate = new();
	long? lastStart;

	public TimeSpan Admit() {
		lock(gate) {
			var now = clock.GetTimestamp();
			if(lastStart is { } last) {
				var wait = Interval - clock.GetElapsedTime(last, now);
				if(wait > TimeSpan.Zero) return wait;
			}
			lastStart = now;
			return TimeSpan.Zero;
		}
	}
}

public sealed partial class Agent {
	internal static readonly TimeSpan NetworkRecoveryWindow = TimeSpan.FromMinutes(45);

	internal static TimeSpan NetworkBackoff(int failures, double sample) {
		var cap = Math.Min(300, 10 * Math.Pow(2, Math.Min(failures - 1, 5)));
		return TimeSpan.FromSeconds(cap * (0.5 + sample * 0.5));
	}

	// Run just the failed operation again: no new wake, transcript mutation, or tool dispatch.
	// Other failure classes leave this loop and follow their existing policies in CallModel.
	async Task<T> RecoverNetwork<T>(TurnSlots slots, Func<NetworkRetry?, CancellationToken, Task<T>> attempt, CancellationToken ct) {
		try {
			return await attempt(null, ct);
		} catch(ProviderUnreachableException first) {
			ct.ThrowIfCancellationRequested();
			var recovery = new NetworkRetry(this, first);
			using var budget = new CancellationTokenSource(NetworkRecoveryWindow, manager.NetworkClock);
			using var retry = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
			try {
				for(int failures = 1; ; failures++) {
					await recovery.Wait(slots, NetworkBackoff(failures, manager.NetworkJitter()), retry.Token);
					try {
						var result = await attempt(recovery, retry.Token);
						recovery.CheckDeadline(retry.Token);
						return result;
					} catch(ProviderUnreachableException e) {
						recovery.Error = e;
					}
				}
			} catch(OperationCanceledException) when(budget.IsCancellationRequested) {
				ct.ThrowIfCancellationRequested();
				manager.Restart.ThrowIfStopping();
				throw recovery.Expired();
			}
		}
	}

	sealed class NetworkRetry(Agent agent, ProviderUnreachableException error) {
		readonly TimeProvider clock = agent.manager.NetworkClock;
		readonly long started = agent.manager.NetworkClock.GetTimestamp();
		public ProviderUnreachableException Error { get; set; } = error;
		TimeSpan Remaining => NetworkRecoveryWindow - clock.GetElapsedTime(started);

		public InvalidOperationException Expired() => new(
			$"Provider network recovery expired after {NetworkRecoveryWindow.TotalMinutes:0} minutes. {Error.Message}", Error);

		public void CheckDeadline(CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			// Check elapsed time as well: after sleep or process stalls the CTS timer
			// may not have executed yet even though the retry timer has fired.
			if(Remaining <= TimeSpan.Zero) throw Expired();
		}

		public async Task Wait(TurnSlots slots, TimeSpan delay, CancellationToken ct) {
			slots.ReleaseAgent();
			slots.ThrowIfStopped(ct);
			CheckDeadline(ct);
			var remaining = Remaining;
			// At the budget boundary we expire, not dispatch another attempt.
			var next = delay < remaining ? clock.GetUtcNow() + delay : (DateTimeOffset?) null;
			var pending = Task.Delay(next.HasValue ? delay : Timeout.InfiniteTimeSpan, clock, ct);
			agent.SetStatus(AgentStatus.WaitingForNetwork, next.HasValue ? Error.Message : $"{Error.Message} The recovery window ends before another retry.", next);
			await pending;
		}
	}
}
