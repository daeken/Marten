using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Agents;

// Internal, non-serialized output metadata. Never pass this object to PowerShell or a model.
internal sealed class JevPresentation(JevSettings settings, JevGrant grant, string? uncertainty, string question, long started, JevResult result) {
	int consumed;
	internal static ToolOutput Withdrawn() => ToolOutput.Error("Jev was disabled or its settings changed; result withheld. A request already sent may have been billed.");
	internal bool IsCurrent() => settings.IsCurrent(grant);
	internal ToolOutput Accept(CancellationToken ct, EvidenceResult? evidence = null) {
		ct.ThrowIfCancellationRequested();
		if(Interlocked.Exchange(ref consumed, 1) != 0) return ToolOutput.Error("Jev result was already handed off.");
		// This check is the acceptance point, not an assertion about when later bytes are delivered.
		if(!IsCurrent()) return Withdrawn();
		ct.ThrowIfCancellationRequested();
		object answer = result.Answer is JevChoiceAnswer choice ? new { choice = choice.Choice, confidence = choice.Confidence, probabilities = choice.Probabilities }
			: new { probability_yes = ((JevNoulAnswer)result.Answer).ProbabilityYes };
		return new(JsonSerializer.Serialize(new { model = result.Model, answer, input_tokens = result.InputTokens, output_tokens = result.OutputTokens,
			evidence = NeedsEvidence ? Render(evidence) : null }));
	}
	internal bool NeedsEvidence => LocalEvidence.IsUncertainty(uncertainty);
	internal EvidenceResult Evidence(IReadOnlyList<Entry> view, CancellationToken ct) {
		long? questionSeq = null;
		for(int i = view.Count - 1; i >= Math.Max(0, view.Count - LocalEvidence.MaxViewRows); i--) {
			var entry = view[i];
			if(entry.Kind == EntryKind.Message && entry.Text == question) { questionSeq = entry.Seq; break; }
		}
		return LocalEvidence.ForDecision(uncertainty, question, view, questionSeq, ct,
			TimeSpan.FromTicks(Math.Max(0, (JevTool.Deadline - Stopwatch.GetElapsedTime(started)).Ticks)));
	}
	internal static object? Render(EvidenceResult? evidence) => evidence switch {
		{ Status: EvidenceStatus.Found } found => new { status = "references", references = found.References.Select(r => new { seq = r.Seq, start = r.Start, length = r.Length }) },
		{ Status: EvidenceStatus.NoMatch } => new { status = "no matching reference" },
		{ Status: EvidenceStatus.TimedOut } => new { status = "timed out" },
		_ => new { status = "unavailable" }
	};
}

internal sealed class JevTool(JevSettings settings, JevClient client, UsageLedger ledger, Artifacts? artifacts, CancellationToken stopping) : ITool {
	internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
	internal const int MaxArgumentsChars = 32_768;
	readonly SemaphoreSlim slots = new(4);
	public string Name => "jev";
	public string Description => "Use TypeSafe Jev routinely for a second opinion: check a crisp acceptance criterion (decide), compare explicit alternatives (choice), estimate a yes probability (noul), or identify when more information is needed (decide). It provides judgment, not proof. Send only relevant explicit state and question to TypeSafe AI; never include secrets. Optional same-agent published PNG requires image preview. No automatic context is sent.";
	public JsonObject Parameters => Schema.Object(
		("mode", Schema.Enum("Evaluation mode", "decide", "choice", "noul")),
		("state", Schema.Str("Relevant text to send; images can use empty text.")),
		("question", Schema.Str("Specific criterion or question to send.")),
		("options", Schema.Nullable(Schema.Array("Choice only: label/description pairs (2–8); null otherwise.",
			Schema.Object(("label", Schema.Str("Option label")), ("description", Schema.Str("When to select this option")))))),
		("image_artifact", Schema.Str("Same-agent published PNG artifact ID, or null", nullable: true)));
	public bool AvailableTo(IToolContext ctx) => settings.TryAcquire() != null;

	public async Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var started = Stopwatch.GetTimestamp();
		if(settings.TryAcquire() is not { } grant) return JevPresentation.Withdrawn();
		if(!slots.Wait(0)) return ToolOutput.Error("Jev concurrency limit reached.");
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping, grant.Revoked);
		linked.CancelAfter(Deadline);
		var token = linked.Token;
		UsageLedger.JevReservation? reserved = null;
		bool dispatched = false;
		long sentAt = 0;
		try {
			token.ThrowIfCancellationRequested();
			var input = Parse(args);
			JevPreviewImage? image = input.Image == null ? null : await Stage(ctx, input.Image, grant, token);
			var request = input.Mode switch {
				"decide" => JevRequest.Decide(input.State, input.Question, image),
				"choice" => JevRequest.Choice(input.State, input.Question, input.Options!, image),
				_ => JevRequest.Noul(input.State, input.Question, image: image),
			};
			reserved = ledger.ReserveJev();
			if(reserved == null) return ToolOutput.Error("Jev budget cannot cover another call.");
			Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> send) {
				// Authoritative admission precedes send. Revocation after admission cancels best-effort.
				if(!settings.IsCurrent(grant)) throw new JevException(JevErrorKind.Cancelled);
				token.ThrowIfCancellationRequested();
				dispatched = true;
				sentAt = Stopwatch.GetTimestamp();
				return send();
			}
			JevResult? validated = null;
			JevResult result;
			try { result = await client.EvaluateAsync(request, grant.GetKeyForProvider(), token, Send, value => validated = value); }
			finally { if(validated != null) ledger.CloseJev(reserved, validated, Stopwatch.GetElapsedTime(sentAt), input.Mode == "decide"); }
			if(!settings.IsCurrent(grant)) return JevPresentation.Withdrawn();
			token.ThrowIfCancellationRequested();
			var choice = result.Answer as JevChoiceAnswer;
			var uncertainty = input.Mode == "decide" ? choice?.Choice : null;
			return new("") { Jev = new JevPresentation(settings, grant, uncertainty, input.Question, started, result) };
		} catch(JevException ex) {
			if(!settings.IsCurrent(grant)) return JevPresentation.Withdrawn();
			ct.ThrowIfCancellationRequested();
			return ToolOutput.Error($"Jev failed ({ex.Kind}).{(dispatched ? " Usage may be unknown; request was not retried." : "")}");
		} catch(OperationCanceledException) {
			if(!settings.IsCurrent(grant)) return JevPresentation.Withdrawn();
			ct.ThrowIfCancellationRequested();
			return ToolOutput.Error("Jev deadline or shutdown; request was not retried.");
		} catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or OverflowException) {
			return ToolOutput.Error("Jev could not prepare or account for the request; no automatic retry.");
		} finally {
			try {
				if(reserved != null && !dispatched) ledger.CloseJev(reserved, null);
			} catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException) {
				// Preserve the durable unknown reservation if accounting storage is unavailable.
			} finally { slots.Release(); }
		}
	}

	async Task<JevPreviewImage> Stage(IToolContext ctx, string id, JevGrant grant, CancellationToken ct) {
		if(!grant.ImagePreviewEnabled || !Artifacts.IdValid(id) || ctx is not Agent agent ||
			agent.Transcript.Artifact(id) is not { PreviewKind: "png", Bytes: > 0 and <= JevPreviewImage.MaxBytes } item ||
			artifacts?.Open(agent.AgentId, item, agent.Transcript) is not { } opened) throw JevRequest.Invalid();
		await using var file = opened;
		var buffer = new byte[(int)item.Bytes + 1];
		var read = await file.ReadAtLeastAsync(buffer, buffer.Length, false, ct);
		if(read != item.Bytes) throw JevRequest.Invalid();
		return JevPreviewImage.Stage(buffer.AsSpan(0, read), allowPreview: true);
	}

	sealed record Input(string Mode, string State, string Question, Dictionary<string, string>? Options, string? Image);
	static Input Parse(JsonObject args) {
		static string? String(JsonNode? node, int max) {
			if(node is not JsonValue value || !value.TryGetValue<string>(out var text) || text == null || text.Length > max) return null;
			return text;
		}
		if(args.Count > 5 || args.Any(p => p.Key is not ("mode" or "state" or "question" or "options" or "image_artifact"))) throw JevRequest.Invalid();
		var mode = String(args["mode"], 6);
		var state = String(args["state"], JevRequest.MaxStateBytes);
		var question = String(args["question"], 2_000);
		var image = String(args["image_artifact"], 80);
		if(mode is not ("decide" or "choice" or "noul") || state == null || question == null || (args["image_artifact"] != null && image == null)) throw JevRequest.Invalid();
		Dictionary<string, string>? options = null;
		if(mode == "choice") {
			if(args["options"] is not JsonArray { Count: >= 2 and <= 8 } items) throw JevRequest.Invalid();
			options = new(StringComparer.Ordinal);
			foreach(var option in items) {
				if(option is not JsonObject { Count: 2 } pair || String(pair["label"], 64) is not { } label ||
					String(pair["description"], 1_024) is not { } description || !options.TryAdd(label, description)) throw JevRequest.Invalid();
			}
		} else if(args["options"] != null) throw JevRequest.Invalid();
		return new(mode, state, question, options, image);
	}
}
