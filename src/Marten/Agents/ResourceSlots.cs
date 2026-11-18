using Marten.Storage;
using Marten.Tools;

namespace Marten.Agents;

/// <summary>Process-wide limits, independent of projects and providers. Limits are fixed at startup.</summary>
internal sealed class ResourceSlots(Config config) {
	public SlotPool Agents { get; } = new(config.MaxRunningAgents);
	public SlotPool Models { get; } = new(config.MaxConcurrentModelCalls);
	readonly Dictionary<string, SlotPool> named = config.Slots.ToDictionary(p => p.Key,
		p => p.Value > 0 && !string.IsNullOrWhiteSpace(p.Key) ? new SlotPool(p.Value)
			: throw new ArgumentException($"Command slot '{p.Key}' must have a name and positive capacity."), StringComparer.Ordinal);

	public string[] Names => named.Keys.ToArray();

	public SlotPool Named(string name) => named.TryGetValue(name, out var pool) ? pool
		: throw new ToolException($"Unknown command slot '{name}'. Configure it in config.json. Available: {string.Join(", ", named.Keys)}.");
}

internal sealed class SlotPool(int limit) {
	readonly SemaphoreSlim? semaphore = limit switch {
		< 0 => throw new ArgumentOutOfRangeException(nameof(limit), "Resource limits cannot be negative."),
		0 => null,
		_ => new(limit, limit),
	};

	public async Task<IDisposable> Take(Action onWait, CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		if(semaphore != null && !semaphore.Wait(0, ct)) {
			onWait();
			await semaphore.WaitAsync(ct);
		}
		return new Lease(semaphore);
	}

	sealed class Lease(SemaphoreSlim? semaphore) : IDisposable {
		SemaphoreSlim? held = semaphore;
		public void Dispose() => Interlocked.Exchange(ref held, null)?.Release();
	}
}
