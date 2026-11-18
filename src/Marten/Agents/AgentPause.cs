using Marten.Storage;

namespace Marten.Agents;

/// <summary>
/// Pausing holds an agent before its next model call: a running tool finishes and its result is
/// recorded, then the agent waits. Messages queue up meanwhile. A paused agent isn't nudged, and
/// its schedules don't fire. An agent is paused by itself, through its project, or with everything.
/// </summary>
public sealed partial class Agent {
	public bool IsPaused => Info.Paused || (Project?.Info.Paused ?? false) || manager.PausedAll;

	public void SetPaused(bool paused) {
		lock(gate) {
			if(Info.Paused == paused) return;
			Info = Info with { Paused = paused };
			JsonIO.Save(Path.Combine(Directory, "agent.json"), Info);
		}
		manager.PauseChanged();
	}

	async Task WaitWhilePaused(CancellationToken ct) {
		while(true) {
			// Take the signal before checking, so a resume between the two isn't missed.
			var changed = manager.PauseSignal;
			if(!IsPaused) return;
			if(TurnStatus != AgentStatus.Paused) SetStatus(AgentStatus.Paused, "Paused");
			await changed.WaitAsync(ct);
		}
	}

	/// <summary>After a resume: an agent that went idle with open tasks while paused gets the nudge it missed.</summary>
	internal void PauseChangedHere() {
		if(IsPaused || turnCts != null || TurnStatus != AgentStatus.Idle || state.NudgesStopped) return;
		MaybeNudge(taskChanges);
	}
}
