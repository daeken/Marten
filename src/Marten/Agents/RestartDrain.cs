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
			var graceElapsed = await Task.WhenAny(settled, Task.Delay(grace)) != settled;
			phase?.Invoke(graceElapsed ? "tool-grace expired" : "tool-grace settled");
			var stopTools = cancelTools.CancelAsync();
			var stopped = Task.WhenAll(settled, stopModels, stopTools);
			var sweep = Task.Run(() => {
				phase?.Invoke("foreground sweep begin");
				cleanForeground();
				phase?.Invoke("foreground sweep end");
			});
			try {
				await Task.WhenAll(stopped, sweep).WaitAsync(cleanup);
				phase?.Invoke("operations settled");
			} catch(TimeoutException) {
				phase?.Invoke("operations timed out");
				throw;
			} finally {
				// A pipeline can fork while handling cancellation. Don't run the final sweep
				// concurrently with the first, even if the first is stuck in OS cleanup.
				var finalSweep = Task.Run(async () => {
					try { await sweep; } catch { /* retry even if the first sweep failed */ }
					phase?.Invoke("foreground final sweep begin");
					cleanForeground();
					phase?.Invoke("foreground final sweep end");
				});
				await finalSweep.WaitAsync(TimeSpan.FromSeconds(1));
			}
		});
	}
}
