using System.Text.Json.Nodes;
using Marten.Storage;

namespace Marten.Models;

public sealed record ToolSpec(string Name, string Description, JsonObject Parameters);

public sealed record ModelRequest(
	string Model,
	string? Effort,
	string Instructions,
	IReadOnlyList<Entry> History,
	IReadOnlyList<ToolSpec> Tools,
	string CacheKey);

/// <summary>Entries produced by one model call, not yet sequenced into the log.</summary>
public sealed record ModelResult(IReadOnlyList<Entry> Items, System.Text.Json.JsonElement? Usage);

public interface IModelBackend {
	/// <summary>Provider tag written into entries, so raw items replay only to their own provider.</summary>
	string Provider { get; }
	bool Connected { get; }
	Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
	Task<ModelResult> CompleteAsync(ModelRequest request, Action<string> onTextDelta, CancellationToken ct);
}

/// <summary>The backend refused for quota or rate reasons. The caller waits and retries; it never switches models.</summary>
public sealed class RateLimitedException(string message, TimeSpan? retryAfter = null) : Exception(message) {
	public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>A failure worth retrying after a short backoff: network errors, 5xx, a stream cut off mid-response.</summary>
public sealed class TransientModelException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ContextFullException() : Exception("The model context window is full.");

public sealed class NotConnectedException(string message) : Exception(message);
