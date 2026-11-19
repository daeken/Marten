using System.Text.Json.Nodes;
using Marten.Storage;

namespace Marten.Models;

/// <summary>A tool as the model sees it. Strict schemas are enforced by the backend; MCP schemas aren't strict-compatible.</summary>
public sealed record ToolSpec(string Name, string Description, JsonObject Parameters, bool Strict = true);

public sealed record ModelRequest(
	string Model,
	string? Effort,
	string Instructions,
	IReadOnlyList<Entry> History,
	IReadOnlyList<ToolSpec> Tools,
	string CacheKey) {
	public string? EffortSource { get; init; }
}

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

/// <summary>DNS or connection establishment failed before the HTTP response. Uses the long recovery policy.</summary>
public sealed class ProviderUnreachableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A failure worth retrying after a short backoff: response timeouts, 5xx, a stream cut off mid-response.</summary>
public sealed class TransientModelException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ContextFullException(int? requestBytes = null) : Exception(requestBytes is int bytes
	? $"The serialized model request exceeds the payload budget (requestBytes={bytes})."
	: "The model context window is full.") {
	public int? RequestBytes { get; } = requestBytes;
}

public sealed class NotConnectedException(string message) : Exception(message);
