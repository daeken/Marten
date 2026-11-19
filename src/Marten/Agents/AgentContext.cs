using System.Text.Json;
using Marten.Models;
using Marten.Storage;

namespace Marten.Agents;

/// <summary>
/// What the model sees, computed from the append-only log. Elision and compaction append entries
/// that change the view; nothing in the log is ever rewritten or lost.
/// </summary>
public sealed partial class Agent {
	const int CharsPerToken = 4;
	/// <summary>The most recent tool results are never elided automatically; the model is still using them.</summary>
	const int KeepRecentResults = 4;

	string OutputPath(string callId) =>
		Path.Combine(OutputsDir, $"{string.Concat(callId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'))}.txt");

	public List<Entry> View() {
		var entries = Transcript.Entries;
		var compaction = entries.LastOrDefault(e => e.Kind == EntryKind.Compaction);
		var elided = entries.Where(e => e.Kind == EntryKind.Elide).SelectMany(e => e.Seqs ?? []).ToHashSet();
		var view = new List<Entry>();
		if(compaction != null)
			view.Add(new Entry {
				Seq = compaction.Seq, Kind = EntryKind.Message, From = NudgeSender,
				Text = $"Earlier context was compacted. Your summary of it:\n\n{compaction.Text}",
			});
		foreach(var e in entries) {
			if(compaction != null && e.Seq <= compaction.UpTo) continue;
			if(e.Kind is EntryKind.Elide or EntryKind.Compaction or EntryKind.Reload) continue;
			view.Add(e.Kind == EntryKind.ToolResult && elided.Contains(e.Seq) ? e with { Text = ElidedNote(e), Images = null } : e);
		}
		return view;
	}

	string ElidedNote(Entry e) {
		var path = e.FullOutput != null ? Path.Combine(Directory, e.FullOutput) : OutputPath(e.CallId ?? e.Seq.ToString());
		return $"[Elided to save context ({e.Text?.Length ?? 0:N0} characters). The full result is in {path}; read it with read_file if you need it again.]";
	}

	/// <summary>Roughly what an entry costs, in characters (4 to a token); an image counts as about 1,500 tokens.</summary>
	static long Chars(Entry e) =>
		(e.Text?.Length ?? 0) + (e.Arguments?.Length ?? 0) + (e.Kind == EntryKind.Reasoning && e.Raw is { } raw ? raw.GetRawText().Length / 2 : 0)
		+ (e.Images?.Count ?? 0) * 6_000;

	int Window(string model) => manager.Config.ContextWindows.TryGetValue(model, out var w) ? w : manager.Config.ContextTokens;

	/// <summary>Tokens the next request will use: the last reported input size plus a character estimate for what's new.</summary>
	public long EstimateTokens(IReadOnlyList<Entry> view) {
		var baseTokens = (Instructions.Length + 4_000) / CharsPerToken;
		long lastSeq;
		long lastTokens;
		lock(gate) (lastSeq, lastTokens) = (state.LastInputSeq, state.LastInputTokens);
		var changedSince = Transcript.Entries.Any(e => e.Seq > lastSeq && e.Kind is EntryKind.Elide or EntryKind.Compaction or EntryKind.Reload);
		if(lastTokens > 0 && !changedSince)
			return lastTokens + view.Where(e => e.Seq > lastSeq).Sum(Chars) / CharsPerToken;
		return baseTokens + view.Sum(Chars) / CharsPerToken;
	}

	void RecordUsage(ModelResult result, IReadOnlyList<Entry> view) {
		if(result.Usage is not { ValueKind: JsonValueKind.Object } usage || !usage.TryGetProperty("input_tokens", out var input) || !input.TryGetInt64(out var tokens))
			return;
		lock(gate) state = state with { LastInputTokens = tokens, LastInputSeq = view.Count > 0 ? view[^1].Seq : 0 };
		SaveState();
	}

	/// <summary>Before each model call: elide old tool results in one batch past their budget, then compact past the threshold.</summary>
	async Task ManageContext(string model, TurnSlots slots, CancellationToken ct) {
		var window = Window(model);
		var view = View();
		var results = view.Where(e => e.Kind == EntryKind.ToolResult && !(e.Text ?? "").StartsWith("[Elided")).ToList();
		var budget = (long) (window * manager.Config.ElideAt);
		var resultTokens = results.Sum(Chars) / CharsPerToken;
		if(resultTokens > budget) {
			// Batch it: drop well below the budget so this doesn't recur every turn and break the cache.
			var target = budget / 2;
			var victims = new List<Entry>();
			foreach(var r in results.Take(Math.Max(0, results.Count - KeepRecentResults))) {
				if(resultTokens <= target) break;
				victims.Add(r);
				resultTokens -= Chars(r) / CharsPerToken;
			}
			if(victims.Count > 0) Elide(victims);
			view = View();
		}
		if(EstimateTokens(view) > window * manager.Config.CompactAt) await Compact(model, slots, ct);
	}

	void Elide(IReadOnlyList<Entry> results) {
		System.IO.Directory.CreateDirectory(OutputsDir);
		foreach(var r in results.Where(r => r.FullOutput == null)) {
			var path = OutputPath(r.CallId ?? r.Seq.ToString());
			if(!File.Exists(path)) AtomicFile.Write(path, r.Text ?? "");
		}
		Append(new Entry { Kind = EntryKind.Elide, Seqs = results.Select(r => r.Seq).ToList() });
	}

	/// <summary>The elide-now tool: drop this agent's last N tool results from its view at once.</summary>
	public int ElideLast(int count) {
		var results = View().Where(e => e.Kind == EntryKind.ToolResult && !(e.Text ?? "").StartsWith("[Elided")).TakeLast(count).ToList();
		if(results.Count > 0) Elide(results);
		return results.Count;
	}

	/// <summary>
	/// Chat agents and orchestrators roll over: the older half is summarized and the rest stays verbatim.
	/// Task agents compact everything. Either way the system prompt is rebuilt with current memory and notes.
	/// </summary>
	public async Task Compact(string model, CancellationToken ct) {
		using var slots = new TurnSlots(this);
		await slots.EnsureAgent(ct);
		await Compact(model, slots, ct);
	}

	// Returns true if a suffix remains that another compaction pass could reduce.
	async Task<bool> Compact(string model, TurnSlots slots, CancellationToken ct) {
		var view = View();
		if(view.Count < 2) return false;
		// A new summary must cover some real history, not just a wake or the
		// synthetic prior summary (whose Seq can be newer than its retained tail).
		var firstInput = view.FindIndex(e => e.Kind is EntryKind.Assistant or EntryKind.ToolResult || e.Kind == EntryKind.Message && e.From != NudgeSender);
		bool CanCut(int i) => firstInput >= 0 && i > firstInput
			&& (view[i].Kind is EntryKind.Wake or EntryKind.Message) && view[i].From != NudgeSender;
		// A single turn can span many model calls without a new user message. At a
		// completed tool round, the next model output starts a fresh replay group.
		bool AfterToolRound(int i) => view[i - 1].Kind == EntryKind.ToolResult
			&& view[i].Kind is EntryKind.Assistant or EntryKind.Reasoning or EntryKind.ToolCall;
		var whole = Info.Kind == AgentKind.Task;
		var cut = view.Count;
		if(!whole) {
			var half = view.Sum(Chars) / 2;
			long acc = 0;
			cut = -1;
			for(var i = 0; i < view.Count - 1; i++) {
				acc += Chars(view[i]);
				// Cut only where a new input starts, never between a tool call and its result.
				if(acc >= half && CanCut(i + 1)) {
					cut = i + 1;
					break;
				}
			}
			if(cut < 0) {
				cut = view.Count;
				whole = true;
			}
		}
		var lightweight = false;
		ModelResult result;
		while(true) {
			var ask = whole
				? "Your context is about to be compacted. Write a summary that lets you carry on with no other history: the goal, what you've done, the current state, what's in progress, open problems, decisions and Sera's preferences, important paths and commands, and your next steps. Your task list, NOTES.md and project memory are kept separately; don't repeat them. Reply with the summary only, and don't call tools."
				: "The older part of your context, everything above this message, is about to be replaced by a summary; what comes after it stays verbatim. Write that summary: decisions, facts learned, Sera's preferences and instructions, work done, open threads, and anything you'd need to refer back to. Reply with the summary only, and don't call tools.";
			var prefix = view.Take(cut);
			if(lightweight) prefix = prefix.Where(e => e.Kind != EntryKind.Reasoning || !string.IsNullOrWhiteSpace(e.Text)).Select(SummaryEntry);
			var request = AgentRequest(model, [.. prefix, new Entry { Kind = EntryKind.Message, From = NudgeSender, Text = ask }]);
			if(lightweight) request = request with { Tools = [] };
			try {
				result = await slots.Complete(request, ModelCallKind.Compaction, _ => { }, "Compacting context", ct);
				break;
			} catch(ContextFullException e) when(e.RequestBytes.HasValue) {
				if(!lightweight) { lightweight = true; continue; }
				// Retry a strictly smaller legal prefix, aiming to halve each attempt.
				var next = Enumerable.Range(1, cut - 1).Where(i => CanCut(i) || AfterToolRound(i))
						.OrderBy(i => Math.Abs(i - cut / 2)).FirstOrDefault();
				if(next == 0) throw new InvalidOperationException($"Even the smallest summarizable history exceeds the payload budget (requestBytes={e.RequestBytes}).", e);
				cut = next;
				whole = false;
			}
		}
		var summary = string.Join("\n\n", result.Items.Where(i => i.Kind == EntryKind.Assistant && !string.IsNullOrWhiteSpace(i.Text)).Select(i => i.Text));
		if(summary.Length == 0) throw new InvalidOperationException("Compaction produced no summary.");
		var upTo = cut < view.Count ? view[cut].Seq - 1 : view[^1].Seq;
		var instructions = manager.RebuildInstructions(this, out var memory);
		await CommitModelOutput(slots, ct, () => {
			Append(new Entry { Kind = EntryKind.Compaction, Text = summary, UpTo = upTo, Instructions = instructions, Reason = whole ? "whole" : "rollover" });
			Instructions = instructions;
			AtomicFile.Write(Path.Combine(Directory, "system.md"), instructions);
			if(memory is { } snapshot) SawMemory(snapshot.Index, snapshot.Topics);
			MarkPromptSeen(string.Join("\n\n", manager.PromptSources(Info)));
			SnapshotDynamicTools();
			state = state with { LastInputTokens = 0 };
			SaveState();
			SetStatus(AgentStatus.Running);
		});
		return cut < view.Count;
	}

	// For an oversized summarizer only: use the neutral transcript (including readable
	// reasoning summaries) and saved image paths rather than opaque replay and base64.
	static Entry SummaryEntry(Entry e) => e with {
		Kind = e.Kind == EntryKind.Reasoning ? EntryKind.Assistant : e.Kind,
		Text = e.Images is { Count: > 0 } images ? e.Text + "\nSaved images: " + string.Join(", ", images.Select(img => img.Path)) : e.Text,
		Raw = null, Images = null,
	};
}
