using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Marten.Storage;

namespace Marten.Agents;

// The coordinates are UTF-16 offsets into Entry.Text, not the provider's rendered message.
// A hit is a literal search lead, not an assertion that the referenced message answers the question.
internal readonly record struct EvidenceReference(long Seq, int Start, int Length);
internal enum EvidenceStatus { Skipped, Found, NoMatch, TimedOut }
internal readonly record struct EvidenceResult(
	EvidenceStatus Status, ImmutableArray<EvidenceReference> References, string? Gap = null);

/// <summary>Local lexical lookup only. No retrieval beyond the admitted reply view.</summary>
internal static class LocalEvidence {
	internal const int MaxQuestionChars = 1024;
	internal const int MaxViewRows = 128;
	internal const int MaxMessages = 48;
	internal const int MaxMessageChars = 2048;
	internal const int MaxViewChars = 24 * 1024;
	internal const int MaxReferences = 3;
	const int MaxQuestionTerms = 32;
	const int MaxTermChars = 64;
	static readonly TimeSpan MaxBudget = TimeSpan.FromMilliseconds(250);
	static readonly HashSet<string> StopWords = new((
		"about after again also and any are because been before being between but can could does doing during " +
		"each for from had has have here how into its just like may more most much must need not now only other " +
		"our out over please said same should since some such tell than that the their them then there these they " +
		"this those through too under very want was way were what when where which who why will with would you your"
	).Split(' '), StringComparer.OrdinalIgnoreCase);

	internal static bool IsUncertainty(string? label) => label is "need more information" or "undecidable";

	internal static EvidenceResult ForDecision(string? label, string? question, IReadOnlyList<Entry> admittedView,
		long? questionSeq = null, CancellationToken ct = default, TimeSpan? budget = null) =>
		IsUncertainty(label) ? Find(question, admittedView, questionSeq, ct, budget) : new(EvidenceStatus.Skipped, []);

	/// <summary>
	/// Search the exact chronological CallModel view for this conversation, not a raw log, freshly
	/// loaded history, or model output. The caller must keep it stable during capture and fence this
	/// lookup and any subsequent dereference/use against the same turn and view; a sequence number
	/// alone is not freshness or authorization. Pass the question's sequence if it is in the view.
	/// This method also supports local general-question lookup, with authorization/scheduling in the caller.
	/// Cancellation and programming failures propagate; timeout returns no partial hits and is not a gap.
	/// </summary>
	internal static EvidenceResult Find(string? question, IReadOnlyList<Entry> admittedView,
		long? questionSeq = null, CancellationToken ct = default, TimeSpan? budget = null) {
		var started = Stopwatch.GetTimestamp();
		var duration = budget ?? MaxBudget;
		ArgumentOutOfRangeException.ThrowIfNegative(duration.Ticks, nameof(budget));
		duration = duration > MaxBudget ? MaxBudget : duration;
		bool Expired() {
			ct.ThrowIfCancellationRequested();
			return Stopwatch.GetElapsedTime(started) >= duration;
		}
		static EvidenceResult Timeout() => new(EvidenceStatus.TimedOut, []);
		static EvidenceResult Gap() => new(EvidenceStatus.NoMatch, [], "No matching reference in the local evidence window.");

		if(Expired()) return Timeout();
		var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if(question != null) {
			var prefix = Prefix(question, MaxQuestionChars);
			foreach(var term in Terms(prefix, prefix.Length != question.Length)) {
				if(!StopWords.Contains(term.Text)) terms.Add(term.Text);
				if(terms.Count == MaxQuestionTerms) break;
			}
		}
		if(Expired()) return Timeout();
		if(terms.Count == 0) return Gap();

		// Capture privately: the matcher sees only bounded text and numeric coordinates, never Entry
		// metadata, raw/reasoning content, tool data, or a means of obtaining other messages.
		var capture = ImmutableArray.CreateBuilder<ProjectedMessage>();
		var remaining = MaxViewChars;
		var count = admittedView.Count;
		for(var i = count - 1; i >= Math.Max(0, count - MaxViewRows) && remaining > 0 && capture.Count < MaxMessages; i--) {
			if(Expired()) return Timeout();
			var entry = admittedView[i];
			if(entry.Seq <= 0 || entry.Seq == questionSeq || entry.Text is not { Length: > 0 } text) continue;
			if(entry.Kind != EntryKind.Assistant && (entry.Kind != EntryKind.Message || entry.From == Agent.NudgeSender)) continue;
			var prefix = Prefix(text, Math.Min(remaining, MaxMessageChars));
			if(prefix.Length == 0) continue;
			capture.Add(new(entry.Seq, prefix, prefix.Length != text.Length));
			remaining -= prefix.Length;
		}
		var projection = capture.ToImmutable();
		var references = ImmutableArray.CreateBuilder<EvidenceReference>(MaxReferences);
		foreach(var message in projection) {
			if(Expired()) return Timeout();
			foreach(var term in Terms(message.Text, message.Capped)) {
				if(!terms.Contains(term.Text)) continue;
				references.Add(new(message.Seq, term.Start, term.Text.Length));
				break;
			}
			if(references.Count == MaxReferences) break;
		}
		if(Expired()) return Timeout();
		return references.Count == 0 ? Gap() : new(EvidenceStatus.Found, references.ToImmutable());
	}

	readonly record struct ProjectedMessage(long Seq, string Text, bool Capped);
	readonly record struct Term(int Start, string Text);

	static string Prefix(string text, int cap) {
		if(text.Length <= cap) return text;
		if(char.IsHighSurrogate(text[cap - 1]) && char.IsLowSurrogate(text[cap])) cap--;
		return text[..cap];
	}

	static IEnumerable<Term> Terms(string text, bool capped) {
		var start = 0;
		var length = 0;
		for(var offset = 0; offset < text.Length;) {
			var valid = Rune.TryGetRuneAt(text, offset, out var rune);
			var width = valid ? rune.Utf16SequenceLength : 1;
			// Malformed surrogates are separators. Combining marks remain part of a word; no
			// normalization or fuzzy matching that could make the reported span ambiguous.
			if(valid && (Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is
				UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)) {
				if(length == 0) start = offset;
				length += width;
			} else {
				if(length is >= 3 and <= MaxTermChars) yield return new(start, text.Substring(start, length));
				length = 0;
			}
			offset += width;
		}
		// A truncated final token might only be a prefix of a different word.
		if(!capped && length is >= 3 and <= MaxTermChars) yield return new(start, text.Substring(start, length));
	}
}
