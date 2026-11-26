using System.Diagnostics;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Marten.Models;
using Marten.Storage;

namespace Marten.Agents;

internal sealed record ModelFailureCause(string Kind, HttpRequestError? HttpError, bool SslStreamDisposed, bool SslStreamFrame);

internal sealed record ModelAttemptFailure(long AttemptId, ModelCallKind Kind, string Backend,
	DateTimeOffset StartedAt, DateTimeOffset EndedAt, long ElapsedMilliseconds, ModelTransportPhase? TransportPhase,
	bool AttemptCancelled, bool TurnCancelled, bool ShuttingDown, IReadOnlyList<ModelFailureCause> Causes);

public sealed partial class Agent {
	internal const string ModelFailureFile = "model-attempt-failure.json";

	void SaveModelFailure(ModelCallStatus call, string model, long elapsedMilliseconds, Exception failure, CancellationToken ct, CancellationToken turnToken) {
		try {
			var cancelled = ct.IsCancellationRequested;
			var turnCancelled = turnToken.IsCancellationRequested;
			var stopping = manager.Restart.IsStopping;
			// An ordinary cancellation is not an attempt failure. Keep real exceptions even when
			// they race cancellation, with the flags needed to distinguish them from an active failure.
			if(failure is OperationCanceledException && (cancelled || turnCancelled || stopping)) return;
			var backend = manager.Backend is BackendRouter router ? router.Resolve(model).Backend : manager.Backend;
			var provider = backend.Provider switch {
				"openai" => "openai", "anthropic" => "anthropic", "zai" => "zai", "deepseek" => "deepseek",
				"bedrock" => "bedrock", "mantle" => "mantle", _ => "other",
			};
			List<ModelFailureCause> causes = [];
			ModelTransportPhase? phase = null;
			for(Exception? e = failure; e != null && causes.Count < 8; e = e.InnerException) {
				phase ??= ModelTransportFailure.Phase(e);
				causes.Add(new(CauseKind(e), e switch {
					HttpRequestException request => request.HttpRequestError,
					HttpIOException io => io.HttpRequestError,
					_ => null,
				}, e is ObjectDisposedException disposed && disposed.ObjectName == typeof(SslStream).FullName,
					new StackTrace(e, fNeedFileInfo: false).GetFrames().Any(f => f.GetMethod()?.DeclaringType is { } type &&
						(type == typeof(SslStream) || type.DeclaringType == typeof(SslStream)))));
			}
			JsonIO.Save(Path.Combine(Directory, ModelFailureFile), new ModelAttemptFailure(call.Id, call.Kind, provider,
				call.StartedAt, call.EndedAt!.Value, elapsedMilliseconds, phase, cancelled, turnCancelled, stopping, causes));
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
