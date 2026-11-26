using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Marten.Models;
using Marten.Models.Anthropic;
using Marten.Models.OpenAI;
using Marten.Storage;

namespace Marten.Agents;

internal enum ModelFailureBackend { Unknown, OpenAI, Anthropic, Zai, DeepSeek, Bedrock, Mantle }

internal sealed record ModelAttemptFailure(Guid RunId, long AttemptId, ModelCallKind Kind, ModelFailureBackend Backend, string? Model,
	DateTimeOffset StartedAt, DateTimeOffset EndedAt, long ElapsedMilliseconds, ModelTransportPhase? TransportPhase,
	bool AttemptCancelled, bool TurnCancelled, bool ShuttingDown, IReadOnlyList<string> Causes);

public sealed partial class Agent {
	internal const string ModelFailureFile = "model-attempt-failure.json";
	static readonly Guid modelFailureRunId = Guid.NewGuid();

	// Only known implementations are described, without consulting config or arbitrary provider properties.
	static (ModelFailureBackend Backend, string? Model) DescribeDispatch(IModelBackend backend, string model) {
		var provider = backend switch {
			ResponsesBackend => ModelFailureBackend.OpenAI,
			MessagesBackend messages => messages.Provider switch {
				"anthropic" => ModelFailureBackend.Anthropic, "zai" => ModelFailureBackend.Zai,
				"deepseek" => ModelFailureBackend.DeepSeek, _ => ModelFailureBackend.Unknown,
			},
			BedrockResponsesBackend bedrock => bedrock.Provider == "mantle" ? ModelFailureBackend.Mantle : ModelFailureBackend.Bedrock,
			_ => ModelFailureBackend.Unknown,
		};
		// Model ids are configurable strings; omit unrecognized ids rather than trying to scrub them.
		var safeModel = (provider, model) switch {
			(ModelFailureBackend.OpenAI, "gpt-6.2-sol") => model,
			(ModelFailureBackend.Anthropic, "claude-sonnet-5-5" or "claude-sonnet-5") => model,
			(ModelFailureBackend.Zai, "glm-5.3" or "glm-5.3-flash" or "glm-4.7") => model,
			(ModelFailureBackend.DeepSeek, "deepseek-v4-pro" or "deepseek-flash") => model,
			_ => null,
		};
		return (provider, safeModel);
	}

	void SaveModelFailure(ModelCallStatus call, ModelFailureBackend backend, string? model, long elapsedMilliseconds,
		Exception failure, CancellationToken ct, CancellationToken turnToken) {
		try {
			var cancelled = ct.IsCancellationRequested;
			var turnCancelled = turnToken.IsCancellationRequested;
			var stopping = manager.Restart.IsStopping;
			// An ordinary cancellation is not an attempt failure. Keep real exceptions even when
			// they race cancellation, with the flags needed to distinguish them from an active failure.
			if(failure is OperationCanceledException && (cancelled || turnCancelled || stopping)) return;
			List<string> causes = [];
			ModelTransportPhase? phase = null;
			for(Exception? e = failure; e != null && causes.Count < 8; e = e.InnerException) {
				phase ??= ModelTransportFailure.Phase(e);
				causes.Add(CauseKind(e));
			}
			var record = new ModelAttemptFailure(modelFailureRunId, call.Id, call.Kind, backend, model,
				call.StartedAt, call.EndedAt!.Value, elapsedMilliseconds, phase, cancelled, turnCancelled, stopping, causes);
			var path = Path.Combine(Directory, ModelFailureFile);
			// One writer per agent; a fixed staging file bounds residue even after a crash.
			var temp = path + ".tmp";
			try {
				using(var stream = new FileStream(temp, FileMode.Create, FileAccess.Write)) {
					stream.Write(Encoding.UTF8.GetBytes(JsonIO.Serialize(record)));
					stream.Flush(flushToDisk: true);
				}
				File.Move(temp, path, overwrite: true);
			} finally {
				File.Delete(temp);
			}
		} catch(Exception) {
			// Diagnostics are best effort; do not mask the original exception or log storage exception text.
		}
	}

	// Emit only predefined categories, not arbitrary type names, objects, methods or exception strings.
	static string CauseKind(Exception error) => error switch {
		ProviderUnreachableException => nameof(ProviderUnreachableException),
		TransientModelException => nameof(TransientModelException),
		RateLimitedException => nameof(RateLimitedException),
		ContextFullException => nameof(ContextFullException),
		ModelRefusedException => nameof(ModelRefusedException),
		NotConnectedException => nameof(NotConnectedException),
		ObjectDisposedException => nameof(ObjectDisposedException),
		HttpRequestException => nameof(HttpRequestException),
		HttpIOException => nameof(HttpIOException),
		SocketException => nameof(SocketException),
		AuthenticationException => nameof(AuthenticationException),
		OperationCanceledException => nameof(OperationCanceledException),
		TimeoutException => nameof(TimeoutException),
		IOException => nameof(IOException),
		_ => "other",
	};
}
