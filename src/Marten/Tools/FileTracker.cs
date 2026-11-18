using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Marten.Tools;

/// <summary>
/// Remembers the version of each file an agent last saw, so the atomic editor can refuse to edit
/// a file that changed underneath it. Persisted with the agent's state so it survives a restart.
/// </summary>
public sealed class FileTracker {
	readonly ConcurrentDictionary<string, string> seen;

	public FileTracker(IDictionary<string, string>? saved = null) =>
		seen = new(saved ?? new Dictionary<string, string>());

	public event Action? Changed;

	public IReadOnlyDictionary<string, string> Snapshot => new Dictionary<string, string>(seen);

	public static string Hash(byte[] contents) => Convert.ToHexString(SHA256.HashData(contents));

	public void Saw(string fullPath, byte[] contents) {
		seen[fullPath] = Hash(contents);
		Changed?.Invoke();
	}

	public string? LastSeen(string fullPath) => seen.GetValueOrDefault(fullPath);
}
