using Marten.Models;
using Marten.Storage;

namespace Marten.Agents;

public enum ModelCallKind { Reply, Compaction }
public enum ModelCallOutcome { Succeeded, Failed, Cancelled }

/// <summary>The latest completion attempt in this process. EndedAt is null only while it is in flight.</summary>
public sealed record ModelCallStatus(long Id, string Model, ModelCallKind Kind, DateTimeOffset StartedAt,
	DateTimeOffset? EndedAt = null, ModelCallOutcome? Outcome = null);

public sealed partial class Agent {
	volatile ModelCallStatus? modelCall;
	long modelCallId;
	public ModelCallStatus? ModelCall => modelCall;

	async Task<ModelResult> CompleteModel(ModelRequest request, ModelCallKind kind, Action<string> onDelta, CancellationToken ct, CancellationToken turnToken) {
		manager.Restart.ThrowIfStopping();
		turnToken.ThrowIfCancellationRequested();
		ct.ThrowIfCancellationRequested();
		var call = new ModelCallStatus(Interlocked.Increment(ref modelCallId), request.Model, kind, DateTimeOffset.UtcNow);
		modelCall = call;
		var outcome = ModelCallOutcome.Failed;
		try {
			manager.Publish(new { type = "model_call_start", agentId = Info.Id, modelCall = call });
			var pending = manager.Backend.CompleteAsync(request, delta => {
				lock(gate) {
					if(!turnToken.IsCancellationRequested && !ct.IsCancellationRequested && !IsPaused) onDelta(delta);
				}
			}, ct);
			ModelResult result;
			try {
				result = await pending.WaitAsync(ct);
			} catch(OperationCanceledException) when(ct.IsCancellationRequested) {
				// A backend that ignores cancellation must not retain Marten's slots or
				// publish an answer later. Still observe its eventual failure, if any.
				_ = Observe(pending);
				throw;
			}
			turnToken.ThrowIfCancellationRequested();
			ct.ThrowIfCancellationRequested();
			outcome = ModelCallOutcome.Succeeded;
			return result;
		} finally {
			if(outcome == ModelCallOutcome.Failed && (ct.IsCancellationRequested || turnToken.IsCancellationRequested)) outcome = ModelCallOutcome.Cancelled;
			var ended = call with { EndedAt = DateTimeOffset.UtcNow, Outcome = outcome };
			modelCall = ended;
			manager.Publish(new { type = "model_call_end", agentId = Info.Id, modelCall = ended });
		}
	}

	static async Task Observe(Task pending) => await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

	void FailTurn(Exception e) {
		AnswerDanglingCalls("Interrupted by a turn failure before this finished.");
		Append(new Entry { Kind = EntryKind.Error, Text = $"Turn failed: {e.Message}" });
		Append(new Entry { Kind = EntryKind.Idle, Reason = "turn_error" });
		SetStatus(AgentStatus.Error, e.Message);

		var outstanding = Tasks.Where(t => t.State != TaskState.Done).ToList();
		var recipients = outstanding.Select(t => t.AssignedBy).Append(Info.ParentId).OfType<string>().Distinct();
		foreach(var id in recipients) {
			if(id == Info.Id || manager.Get(id) is not { } recipient) continue;
			var relevant = outstanding.Where(t => id == Info.ParentId || t.AssignedBy == id).Select(t => $"#{t.Id} ({t.State})");
			var list = string.Join(", ", relevant);
			var tasksNote = list.Length == 0 ? "" : $" Unfinished tasks: {list}.";
			var message = $"Marten: {AgentManager.Label(this)} stopped with a terminal turn error: {e.Message}.{tasksNote} No task states were changed.";
			recipient.Send(Inbound.FromAgent(this, message, SendMode.Queued) with { Reason = "turn_error" });
		}
	}
}
