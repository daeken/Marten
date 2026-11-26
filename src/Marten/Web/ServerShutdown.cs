using System.Diagnostics;
using Marten.Agents;

namespace Marten.Web;

internal static class ServerShutdown {
	public static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(3);

	public static async Task<bool> RunAsync(WebApplication app, RestartDrain restart, Func<int> stopGraceSeconds,
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
			var grace = TimeSpan.FromSeconds(Math.Max(stopGraceSeconds(), 0));
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
		var results = await Task.WhenAll(Drain(), StopHost());
		var clean = results.All(ok => ok);
		Report(clean ? "exit" : "exit incomplete");
		return clean;

		async Task<bool> Drain() {
			// RestartDrain owns the configured grace and its cleanup wait; don't impose a second,
			// shorter deadline here. MCP gets its own deadline after the drain attempt finishes.
			Report("restart-drain begin");
			bool drained;
			try {
				await signalled.Task.Unwrap();
				Report("restart-drain end");
				drained = true;
			} catch(TimeoutException) {
				Report("restart-drain timed out");
				drained = false;
			} catch(Exception) {
				Report("restart-drain failed");
				drained = false;
			}
			return await Phase("mcp-stop", stopMcp, TimeSpan.FromSeconds(5)) && drained;
		}

		Task<bool> Dispose() => Phase("host-dispose", () => app.DisposeAsync().AsTask(), TimeSpan.FromSeconds(3));

		async Task<bool> StopHost() {
			// Budget slightly more than HostOptions so Kestrel can finish aborting timed-out requests.
			if(!await Phase("host-stop", () => app.StopAsync(), HostTimeout + TimeSpan.FromSeconds(1))) {
				Report("host-dispose skipped after incomplete stop"); // Don't race an in-progress stop.
				return false;
			}
			return await Dispose();
		}
	}
}
