namespace Marten.Agents;

public sealed partial class AgentManager {
	internal void CheckIdleProjects() {
		var members = agents.Values; // snapshot, including closed workers
		var workers = members.Where(a => a.Info.Kind == AgentKind.Task).ToLookup(a => a.Info.ParentId);
		foreach(var project in projects.Values) {
			if(project.Info.OrchestratorId is not { } id || Get(id) is not { } orchestrator) continue;
			orchestrator.CheckProjectIdle(workers[id], () => agents.Count == members.Count);
		}
	}
}

public sealed partial class Agent {
	const string ProjectIdleWake = "project_idle";
	long lastWakeSeq;
	long? pendingProjectIdleWork;
	int standaloneCommands;

	// Schedules and manual notebook replays can run without a model turn (or a slot wait).
	internal IDisposable TrackStandaloneCommand() {
		lock(gate) standaloneCommands++;
		return new StandaloneCommand(this);
	}

	sealed class StandaloneCommand(Agent owner) : IDisposable {
		public void Dispose() { lock(owner.gate) owner.standaloneCommands--; }
	}

	// Called with both the lifecycle and status locks held. A status of Idle by itself
	// misses undelivered messages, recovery work and the edges of a turn.
	bool IdleForNotice => !IsPaused && Status == AgentStatus.Idle && activeTurn == null &&
		resumeReason == null && inbox.Count == 0 && standaloneCommands == 0;

	internal void CheckProjectIdle(IEnumerable<Agent> workers, Func<bool> membershipUnchanged) {
		// Don't wait for other agents (or call event subscribers) while holding these locks.
		// A busy transition simply defers the decision to the next scheduler tick.
		var held = new Stack<object>();
		bool Hold(object sync) {
			if(!Monitor.TryEnter(sync)) return false;
			held.Push(sync);
			return true;
		}
		try {
			if(!Hold(gate) || Info.Closed || !Hold(statusGate) || !IdleForNotice) return;
			var waiting = Tasks.Where(t => t.State == TaskState.Waiting).ToList();
			if(waiting.Count == 0) return;

			// Worker wake sequence numbers only grow, including across restarts. Keep closed
			// workers in the sum so closing/reopening one isn't mistaken for progress.
			long work = 0;
			int openWorkers = 0;
			foreach(var worker in workers) {
				if(!Hold(worker.gate)) return;
				work += worker.lastWakeSeq;
				if(worker.Info.Closed) continue;
				if(!Hold(worker.statusGate) || !worker.IdleForNotice) return;
				openWorkers++;
			}
			// Agents are only added, never removed. If one was spawned during this scan,
			// retry with the complete membership rather than overlooking its first turn.
			if(state.ProjectIdleWorkSeen == work || !membershipUnchanged() || IsPaused || manager.Restart.IsStopping) return;
			var list = string.Join("\n", waiting.Select(t => $"- #{t.Id} {t.Title}" +
				(string.IsNullOrWhiteSpace(t.Reason) ? "" : $": {t.Reason}") +
				(t.WaitingOn is { } edge ? $" (waiting on {edge.AgentId} #{edge.TaskId})" : "")));
			var status = openWorkers == 0 ? "There are no non-closed task agents." : "All non-closed task agents are idle.";
			inbox.Add(new Inbound(NudgeSender, $"{status} Review your waiting tasks:\n{list}\nIf nothing can proceed, leave them waiting.", SendMode.Queued, ProjectIdleWake));
			pendingProjectIdleWork = work;
		} finally {
			while(held.TryPop(out var sync)) Monitor.Exit(sync);
		}
		signal.Release();
		manager.Publish(new { type = "inbox", agentId = Info.Id, pending = PendingCount });
	}
}
