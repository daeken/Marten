using System.Diagnostics;
using Marten.Agents;

namespace Marten.Web;

internal static class ServerShutdown {
	public static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(3);

	public static async Task RunAsync(WebApplication app, RestartDrain restart, Func<int> stopGraceSeconds,
		Action cleanForeground, Func<Task> stopMcp) {
		var signalled = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
		var start = Stopwatch.GetTimestamp();
		void Report(string phase) => Console.Error.WriteLine($"Marten shutdown +{Stopwatch.GetElapsedTime(start).TotalMilliseconds:0}ms {phase}");

		async Task<bool> Phase(string name, Func<Task> work, TimeSpan timeout) {
			// Bound the caller's wait even when work blocks before returning its task.
			var task = Task.Run(async () => {
				Report($"{name} begin");
				await work();
			});
			try { await task.WaitAsync(timeout); }
			catch(TimeoutException) {
				Report($"{name} timed out");
				return false;
			} catch(Exception) {
				Report($"{name} failed");
				return false;
			}
			Report($"{name} end");
			return true;
		}

		using var registration = app.Lifetime.ApplicationStopping.Register(() => {
			start = Stopwatch.GetTimestamp();
			// A stuck ProcessExit handler, synchronous callback or logger cannot keep this
			// process alive forever. A dedicated background thread doesn't need a pool worker.
			new Thread(() => {
				Thread.Sleep(TimeSpan.FromSeconds(30));
				Process.GetCurrentProcess().Kill(); // This process only; never detached jobs.
			}) { IsBackground = true, Name = "Marten shutdown deadline" }.Start();
			var grace = TimeSpan.FromSeconds(Math.Clamp(stopGraceSeconds(), 0, 10));
			// Close admission and signal cancellation here. No logging, process I/O or drain waits
			// in this callback: they would delay Kestrel closing the listener.
			signalled.TrySetResult(restart.DrainAsync(grace, grace + TimeSpan.FromSeconds(2), cleanForeground, Report));
		});
		try {
			await app.StartAsync();
		} catch {
			// Match RunAsync on a startup failure, but don't leave startup tools running.
			app.Lifetime.StopApplication();
			await Task.WhenAll(Drain(), Dispose());
			throw;
		}

		await signalled.Task;
		await Task.WhenAll(Drain(), StopHost());
		Report("exit");

		async Task Drain() {
			await Phase("restart-drain", () => signalled.Task.Unwrap(), TimeSpan.FromSeconds(23));
			await Phase("mcp-stop", stopMcp, TimeSpan.FromSeconds(5));
		}

		Task Dispose() => Phase("host-dispose", () => app.DisposeAsync().AsTask(), TimeSpan.FromSeconds(3));

		async Task StopHost() {
			// Budget slightly more than HostOptions so Kestrel can finish aborting timed-out requests.
			if(await Phase("host-stop", () => app.StopAsync(), HostTimeout + TimeSpan.FromSeconds(1)))
				await Dispose(); // Don't race disposal against an in-progress stop.
		}
	}
}
