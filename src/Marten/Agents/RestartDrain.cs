namespace Marten.Agents;

/// <summary>One-way shutdown gate. Model work stops immediately; admitted tools have a separate deadline.</summary>
internal sealed class RestartDrain {
	readonly object gate = new();
	readonly CancellationTokenSource stopping = new();
	readonly CancellationTokenSource cancelTools = new();
	readonly HashSet<TaskCompletionSource> active = new();
	bool closed;

	public CancellationToken Stopping => stopping.Token;
	public CancellationToken CancelTools => cancelTools.Token;
	public bool IsStopping { get { lock(gate) return closed; } }

	public void ThrowIfStopping() {
		if(IsStopping) throw new OperationCanceledException("Marten is stopping.", Stopping);
	}

	public IDisposable? Enter() {
		lock(gate) {
			if(closed) return null;
			var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			active.Add(completion);
			return new Lease(this, completion);
		}
	}

	sealed class Lease(RestartDrain drain, TaskCompletionSource completion) : IDisposable {
		public void Dispose() {
			lock(drain.gate) drain.active.Remove(completion);
			completion.TrySetResult();
		}
	}

	public async Task DrainAsync(TimeSpan grace, TimeSpan cleanup, Action cleanForeground) {
		Task settled;
		lock(gate) {
			closed = true;
			settled = Task.WhenAll(active.Select(a => a.Task));
		}
		// CancelAsync signals immediately without putting untrusted cancellation callbacks on the signal thread.
		var stopModels = stopping.CancelAsync();
		await Task.WhenAny(settled, Task.Delay(grace));
		var stopTools = cancelTools.CancelAsync();
		try {
			cleanForeground();
			var finished = Task.WhenAll(settled, stopModels, stopTools);
			await finished.WaitAsync(cleanup);
		} finally {
			// A pipeline might have forked again while responding to cancellation.
			cleanForeground();
		}
	}
}
