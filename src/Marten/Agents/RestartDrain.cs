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

	public Task DrainAsync(TimeSpan grace, TimeSpan cleanup, Action cleanForeground, Action<string>? phase = null) {
		Task settled;
		lock(gate) {
			closed = true;
			settled = Task.WhenAll(active.Select(a => a.Task));
		}
		// CancelAsync signals immediately without putting untrusted cancellation callbacks on the signal thread.
		var stopModels = stopping.CancelAsync();
		return Task.Run(async () => {
			phase?.Invoke("tool-grace begin");
			try {
				await WaitWithin(settled, grace);
				phase?.Invoke("tool-grace settled");
			} catch(TimeoutException) {
				phase?.Invoke("tool-grace expired");
			}
			var stopTools = cancelTools.CancelAsync();
			var stopped = Task.WhenAll(settled, stopModels, stopTools);
			var sweep = Sweep("foreground sweep");
			try {
				await WaitWithin(Task.WhenAll(stopped, sweep), cleanup);
				phase?.Invoke("operations settled");
			} catch(TimeoutException) {
				if(!stopped.IsCompleted) phase?.Invoke("operations timed out");
				if(!sweep.IsCompleted) phase?.Invoke("foreground sweep timed out");
				throw;
			} finally {
				// A pipeline can fork while handling cancellation. Don't queue a second
				// sweep behind one that is still running after its deadline.
				if(sweep.IsCompleted) {
					try { await Sweep("foreground final sweep").WaitAsync(TimeSpan.FromSeconds(1)); }
					catch(TimeoutException) {
						phase?.Invoke("foreground final sweep timed out");
						throw;
					}
				} else {
					phase?.Invoke("foreground final sweep skipped");
				}
			}
		});

		Task Sweep(string name) => Task.Run(() => {
			phase?.Invoke($"{name} begin");
			try { cleanForeground(); }
			catch {
				phase?.Invoke($"{name} failed");
				throw;
			}
			phase?.Invoke($"{name} end");
		});
	}

	// CLR timer limits apply to one wait, not to the configured grace. Do not silently
	// clamp large values (or throw before cancellation/cleanup ever starts).
	static async Task WaitWithin(Task task, TimeSpan timeout) {
		var maxTimer = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
		while(timeout > maxTimer) {
			try { await task.WaitAsync(maxTimer); return; }
			catch(TimeoutException) when(!task.IsCompleted) { timeout -= maxTimer; }
		}
		await task.WaitAsync(timeout);
	}
}
