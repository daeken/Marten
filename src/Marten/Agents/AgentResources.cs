using Marten.Models;

namespace Marten.Agents;

public sealed partial class Agent {
	readonly object statusGate = new();
	(AgentStatus Status, string? Detail) turnStatus = (AgentStatus.Idle, null);
	string? commandSlotWait;

	// A scheduled command or notebook replay may wait even when the agent has no turn.
	(AgentStatus Status, string? Detail) CurrentStatus {
		get {
			lock(statusGate) return commandSlotWait == null ? turnStatus : (AgentStatus.WaitingForSlot, commandSlotWait);
		}
	}

	AgentStatus TurnStatus { get { lock(statusGate) return turnStatus.Status; } }

	void SetStatus(AgentStatus status, string? detail = null) {
		lock(statusGate) {
			turnStatus = (status, detail);
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
		var (status, detail) = CurrentStatus;
		manager.Publish(new { type = "status", agentId = Info.Id, status, detail });
	}

	/// <summary>One turn can hold an agent slot and briefly a model slot, always in that order.
	/// Neither slot is held across a pause or a model retry delay.</summary>
	sealed class TurnSlots(Agent agent) : IDisposable {
		readonly ResourceSlots pools = agent.manager.Resources;
		IDisposable? running;

		public void ReleaseAgent() {
			running?.Dispose();
			running = null;
		}

		public void Dispose() => ReleaseAgent();

		public async Task EnsureAgent(CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			if(agent.IsPaused) ReleaseAgent();
			if(running != null) return;
			while(running == null) {
				await agent.WaitWhilePaused(ct);
				running = await AcquireUntilPaused(pools.Agents, "Running agent limit", ct);
			}
			agent.SetStatus(AgentStatus.Running);
		}

		public async Task<ModelResult> Complete(ModelRequest request, ModelCallKind kind, Action<string> onDelta, string? detail, CancellationToken ct) {
			while(true) {
				await EnsureAgent(ct);
				using var model = await AcquireUntilPaused(pools.Models, "Concurrent model call limit", ct);
				if(model == null) {
					ReleaseAgent();
					continue;
				}
				ct.ThrowIfCancellationRequested();
				agent.SetStatus(AgentStatus.Running, detail);
				return await agent.CompleteModel(request, kind, onDelta, ct);
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
