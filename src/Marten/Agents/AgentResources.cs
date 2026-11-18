using Marten.Models;

namespace Marten.Agents;

public sealed partial class Agent {
	readonly object statusGate = new();
	(AgentStatus Status, string? Detail, DateTimeOffset? RetryAt) turnStatus = (AgentStatus.Idle, null, null);
	string? commandSlotWait;

	// A scheduled command or notebook replay may wait even when the agent has no turn.
	internal (AgentStatus Status, string? Detail, DateTimeOffset? RetryAt) CurrentStatus {
		get {
			lock(statusGate) return commandSlotWait == null || turnStatus.Status == AgentStatus.WaitingForNetwork ? turnStatus : (AgentStatus.WaitingForSlot, commandSlotWait, null);
		}
	}

	AgentStatus TurnStatus { get { lock(statusGate) return turnStatus.Status; } }

	void SetStatus(AgentStatus status, string? detail = null, DateTimeOffset? retryAt = null) {
		lock(statusGate) {
			turnStatus = (status, detail, retryAt);
			PublishStatus();
		}
	}

	internal void SetCommandSlotWait(string? name) {
		lock(statusGate) {
			commandSlotWait = name == null ? null : $"Command slot: {name}";
			PublishStatus();
		}
	}

	void PublishStatus() {
		var (status, detail, retryAt) = CurrentStatus;
		manager.Publish(new { type = "status", agentId = Info.Id, status, detail, retryAt });
	}

	/// <summary>One turn can hold an agent slot and briefly a model slot, always in that order.
	/// Neither slot is held across a pause or a model retry delay.</summary>
	sealed class TurnSlots(Agent agent, CancellationToken turnToken = default) : IDisposable {
		public CancellationToken TurnToken => turnToken;

		public void ThrowIfStopped(CancellationToken ct) {
			turnToken.ThrowIfCancellationRequested();
			ct.ThrowIfCancellationRequested();
		}
		readonly ResourceSlots pools = agent.manager.Resources;
		IDisposable? running;

		public void ReleaseAgent() {
			running?.Dispose();
			running = null;
		}

		public void Dispose() => ReleaseAgent();

		public async Task EnsureAgent(CancellationToken ct) {
			ThrowIfStopped(ct);
			if(agent.IsPaused) ReleaseAgent();
			if(running != null) return;
			while(running == null) {
				await agent.WaitWhilePaused(ct);
				running = await AcquireUntilPaused(pools.Agents, "Running agent limit", ct);
			}
			agent.SetStatus(AgentStatus.Running);
		}

		public Task<ModelResult> Complete(ModelRequest request, ModelCallKind kind, Action<string> onDelta, string? detail, CancellationToken ct) =>
			agent.RecoverNetwork(this, (recovery, token) => CompleteOnce(request, kind, onDelta, detail, recovery, token), ct);

		async Task<ModelResult> CompleteOnce(ModelRequest request, ModelCallKind kind, Action<string> onDelta, string? detail, NetworkRetry? recovery, CancellationToken ct) {
			while(true) {
				await EnsureAgent(ct);
				TimeSpan wait;
				using(var model = await AcquireUntilPaused(pools.Models, "Concurrent model call limit", ct)) {
					if(model == null) {
						ReleaseAgent();
						continue;
					}
					lock(agent.gate) {
						ThrowIfStopped(ct);
						agent.manager.Restart.ThrowIfStopping();
						if(agent.IsPaused) continue;
						recovery?.CheckDeadline(ct);
						wait = recovery == null ? TimeSpan.Zero : agent.manager.NetworkPacer.Admit();
						if(wait == TimeSpan.Zero) agent.SetStatus(AgentStatus.Running, detail);
					}
					if(wait == TimeSpan.Zero) return await agent.CompleteModel(request, kind, onDelta, ct, turnToken);
				}
				// Do not sleep with either lease, and check pacing again at actual dispatch.
				await recovery!.Wait(this, wait + TimeSpan.FromSeconds(agent.manager.NetworkJitter()), ct);
			}
		}

		async Task<IDisposable?> AcquireUntilPaused(SlotPool pool, string detail, CancellationToken ct) {
			var changed = agent.manager.PauseSignal;
			if(agent.IsPaused) return null;
			using var queue = CancellationTokenSource.CreateLinkedTokenSource(ct);
			var pending = pool.Take(() => agent.SetStatus(AgentStatus.WaitingForSlot, detail), queue.Token);
			while(!pending.IsCompleted) {
				if(await Task.WhenAny(pending, changed) == pending) break;
				changed = agent.manager.PauseSignal;
				if(!agent.IsPaused) continue;
				queue.Cancel();
				// A grant can race with a pause. Return it instead of parking with it held.
				try { (await pending).Dispose(); }
				catch(OperationCanceledException) when(!ct.IsCancellationRequested) { }
				return null;
			}
			var lease = await pending;
			if(!agent.IsPaused && !ct.IsCancellationRequested) return lease;
			lease.Dispose();
			ct.ThrowIfCancellationRequested();
			return null;
		}
	}
}
