using System.Diagnostics;
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
		var started = Stopwatch.GetTimestamp();
		Exception? failure = null;
		(ModelFailureBackend Backend, string? Model) dispatch = default;
		void ObserveDispatch(IModelBackend backend, string model) => dispatch = DescribeDispatch(backend, model);
		try {
			manager.Publish(new { type = "model_call_start", agentId = Info.Id, modelCall = call });
			void OnDelta(string delta) {
				lock(gate) {
					if(!turnToken.IsCancellationRequested && !ct.IsCancellationRequested && !IsPaused) onDelta(delta);
				}
			}
			Task<ModelResult> pending;
			if(manager.Backend is BackendRouter router) pending = router.CompleteAsync(request, OnDelta, ct, ObserveDispatch);
			else {
				ObserveDispatch(manager.Backend, request.Model);
				pending = manager.Backend.CompleteAsync(request, OnDelta, ct);
			}
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
		} catch(Exception e) {
			failure = e;
			throw;
		} finally {
			if(outcome == ModelCallOutcome.Failed && (ct.IsCancellationRequested || turnToken.IsCancellationRequested)) outcome = ModelCallOutcome.Cancelled;
			var ended = call with { EndedAt = DateTimeOffset.UtcNow, Outcome = outcome };
			modelCall = ended;
			if(failure != null) SaveModelFailure(ended, dispatch.Backend, dispatch.Model, (long) Stopwatch.GetElapsedTime(started).TotalMilliseconds, failure, ct, turnToken);
			manager.Publish(new { type = "model_call_end", agentId = Info.Id, modelCall = ended });
		}
	}

	static async Task Observe(Task pending) => await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

	/// <summary>
	/// A provider blocked the request on policy grounds. Move the agent to the fallback model, and say so in
	/// the transcript, together with the three things it can do: carry on where it is, hand the work to a
	/// fresh agent with a fuller brief, or escalate the refusal if it thinks the block was right.
	/// </summary>
	void MoveToRefusalFallback(ModelRefusedException refused, string? from, string to) {
		var refusedBy = from ?? manager.KnownDefaultModel ?? "The provider";
		UpdateSettings(to, Info.Effort);
		Append(new Entry { Kind = EntryKind.Message, From = NudgeSender, Text = RefusalNotice(refused, refusedBy, to) });
		SetStatus(AgentStatus.Running, $"{refusedBy} refused the request; now on {to}.");
	}

	static string Clip(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

	string RefusalNotice(ModelRefusedException refused, string from, string to) {
		var reason = !string.IsNullOrWhiteSpace(refused.Reason) ? refused.Reason
			: !string.IsNullOrWhiteSpace(refused.Code) ? refused.Code : "the provider gave no reason";
		// Keep the provider's code visible: it is what Sera and the orchestrator will search for.
		if(refused.Code is { Length: > 0 } code && !string.Equals(code, reason, StringComparison.OrdinalIgnoreCase))
			reason = $"{reason} [{code}]";
		var options = Info.Kind switch {
			AgentKind.Orchestrator =>
				$"- Spawn a fresh agent with more context (spawn_agent) and hand the work over with a fuller brief, so it is less likely to be flagged again.\n" +
				"- If you think the refusal was right and this shouldn't be done, surface it to Sera with send_message to \"sera\": what was asked, what was refused, and why.",
			AgentKind.Chat =>
				"- If you think the refusal was right and this shouldn't be done, say so plainly in your reply to Sera: what was asked, what was refused, and why. She can start a fresh chat with more context if the block was a mistake.",
			_ =>
				"- Ask your orchestrator (send_message) to spawn a fresh agent with more context and a fuller brief, so it is less likely to be flagged again.\n" +
				"- If you think the refusal was right and this shouldn't be done, tell your orchestrator with send_message: what was asked, what was refused, and why.",
		};
		return $"{from} flagged this request and refused it: \"{Clip(reason, 400)}\"\n\n" +
			$"You're on {to} now, for this call and after, until you change it (set_model). Nothing was lost: your history, tools and tasks are as they were.\n\n" +
			$"You can choose to:\n- Stay here: carry on with the work on {to}.\n{options}";
	}

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
