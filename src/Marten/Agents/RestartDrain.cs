namespace Marten.Agents;

/// <summary>Shared admission gate for reversible release preparation and one-way signal shutdown.</summary>
internal sealed class RestartDrain(TimeProvider? clock = null) {
	readonly TimeProvider time = clock ?? TimeProvider.System;
	ReleaseLease? release;
	int pendingMessages;
	public const int MaxLeaseSeconds = 300;

	internal sealed record ReleaseStatus(string Id, int Pid, string State, int RemainingMs, int ActiveOperations, int PendingMessages);

	sealed class ReleaseLease(string id, long started, TimeSpan duration) {
		public readonly string Id = id;
		public readonly TaskCompletionSource Reopened = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public readonly long Started = started;
		public readonly TimeSpan Duration = duration;
		public ITimer? Timer;
	}
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

	// Autonomous launch and HTTP mutation entry. Follow-up work inside a tracked operation
	// needs no new admission; it must be able to finish and save during the reversible lease.
	public IDisposable? Enter() {
		lock(gate) return closed || CurrentRelease() != null ? null : Track();
	}

	// Powershell cancels the run token before relinquishing its outer admission, even
	// when abandoning a runspace. Track nested calls separately so an abandoned run
	// cannot leave an uncounted inbox producer that is still finishing.
	public IDisposable? EnterNested(CancellationToken run) {
		if(!run.CanBeCanceled) return Enter();
		lock(gate) return closed || run.IsCancellationRequested ? null : Track();
	}

	IDisposable Track() {
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		active.Add(completion);
		return new Lease(this, completion);
	}

	public async ValueTask<IDisposable?> EnterTurnAsync() {
		while(true) {
			Task wait;
			lock(gate) {
				if(closed) return null;
				if(CurrentRelease() is not { } lease) return Track();
				wait = lease.Reopened.Task;
			}
			await wait.ConfigureAwait(false);
		}
	}

	// The inbox owner holds its lifecycle lock. The admission gate never acquires any agent
	// lock; appending and removing from an inbox cannot deadlock a status check.
	public void ChangePendingMessages(int delta) {
		lock(gate) pendingMessages += delta;
	}

	public ReleaseStatus? BeginRelease(string id, int leaseSeconds) {
		ArgumentOutOfRangeException.ThrowIfLessThan(leaseSeconds, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(leaseSeconds, MaxLeaseSeconds);
		lock(gate) {
			if(closed || CurrentRelease() != null) return null;
			var lease = new ReleaseLease(id, time.GetTimestamp(), TimeSpan.FromSeconds(leaseSeconds));
			lease.Timer = time.CreateTimer(_ => Expire(lease), null, lease.Duration, Timeout.InfiniteTimeSpan);
			release = lease;
			return Snapshot(lease);
		}
	}

	public ReleaseStatus? GetRelease(string id) {
		lock(gate) return CurrentRelease() is { } lease && lease.Id == id ? Snapshot(lease) : null;
	}

	public bool AbortRelease(string id) {
		lock(gate) {
			if(CurrentRelease() is not { } lease || lease.Id != id) return false;
			EndRelease(lease);
			return true;
		}
	}

	TimeSpan Remaining(ReleaseLease lease) => lease.Duration - time.GetElapsedTime(lease.Started);

	// The timer wakes parked turn loops. The monotonic deadline is also checked on each
	// observation/admission so a delayed timer cannot extend the lifetime of a Ready result.
	ReleaseLease? CurrentRelease() {
		if(release is { } lease && Remaining(lease) <= TimeSpan.Zero) EndRelease(lease);
		return release;
	}

	ReleaseStatus? Snapshot(ReleaseLease lease) {
		var ms = (int) Remaining(lease).TotalMilliseconds;
		if(ms <= 0) return null;
		return new(lease.Id, Environment.ProcessId, pendingMessages != 0 ? "busy" : active.Count != 0 ? "draining" : "ready", ms, active.Count, pendingMessages);
	}

	void Expire(ReleaseLease lease) {
		lock(gate) {
			if(release != lease) return;
			var remaining = Remaining(lease);
			if(remaining <= TimeSpan.Zero) EndRelease(lease);
			else lease.Timer!.Change(remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining, Timeout.InfiniteTimeSpan);
		}
	}

	void EndRelease(ReleaseLease lease) {
		release = null;
		lease.Timer?.Dispose();
		lease.Reopened.TrySetResult();
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
			if(release is { } lease) EndRelease(lease);
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
