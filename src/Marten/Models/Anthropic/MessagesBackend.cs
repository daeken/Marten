using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Marten.Storage;

namespace Marten.Models.Anthropic;

/// <summary>One provider that speaks the Anthropic Messages API: Anthropic itself, or a compatible endpoint.</summary>
public sealed record MessagesProvider {
	public required string Name { get; init; }
	public required string Label { get; init; }
	/// <summary>Null for Anthropic's own endpoint.</summary>
	public string? BaseUrl { get; init; }
	/// <summary>Send the key as a Bearer token instead of x-api-key.</summary>
	public bool Bearer { get; init; }
	public string? EnvVar { get; init; }
	public int MaxTokens { get; init; } = 32_000;
	/// <summary>Ask for adaptive thinking with summarized text, and top-level prompt caching.</summary>
	public bool ClaudeFeatures { get; init; }
	/// <summary>Map the agent's effort onto output_config.effort.</summary>
	public bool SendEffort { get; init; }
	/// <summary>Models to list when the provider's models endpoint fails or isn't there.</summary>
	public IReadOnlyList<string> Models { get; init; } = [];
	/// <summary>Whether images reach the model. When false, a tool result's images become a note with their paths.</summary>
	public bool Images { get; init; } = true;
	/// <summary>Bare model names this provider owns, by prefix.</summary>
	public IReadOnlyList<string> Prefixes { get; init; } = [];

	public static readonly MessagesProvider Anthropic = new() {
		Name = "anthropic", Label = "Anthropic", EnvVar = "ANTHROPIC_API_KEY", MaxTokens = 64_000,
		ClaudeFeatures = true, SendEffort = true, Prefixes = ["claude-"],
	};
	// https://docs.z.ai/scenario-example (Anthropic-compatible endpoint, token auth)
	public static readonly MessagesProvider Zai = new() {
		Name = "zai", Label = "z.ai", BaseUrl = "https://api.z.ai/api/anthropic", Bearer = true, EnvVar = "ZAI_API_KEY",
		Models = ["glm-5.3", "glm-5.3-flash", "glm-4.7"], Prefixes = ["glm-"],
		// Tested 2026-11-18: glm-5.3 gets image blocks through this endpoint but describes them from guesswork.
		Images = false,
	};
	// https://api-docs.deepseek.com/guides/anthropic_api
	public static readonly MessagesProvider DeepSeek = new() {
		Name = "deepseek", Label = "DeepSeek", BaseUrl = "https://api.deepseek.com/anthropic", EnvVar = "DEEPSEEK_API_KEY",
		SendEffort = true, Models = ["deepseek-v4-pro", "deepseek-flash"], Prefixes = ["deepseek-"],
	};
	public static readonly IReadOnlyList<MessagesProvider> All = [Anthropic, Zai, DeepSeek];
}

/// <summary>An Anthropic Messages API backend. Stateless: the full history goes up each call, streamed back.</summary>
/// <param name="extraModels">Models Sera added in config for this provider, listed first.</param>
public sealed class MessagesBackend(MessagesProvider provider, Home home, Func<string, IReadOnlyList<string>>? extraModels = null) : IModelBackend {
	IReadOnlyList<string>? liveModels;
	DateTimeOffset liveModelsAt;

	public string Provider => provider.Name;
	public MessagesProvider Settings => provider;
	string KeyPath => Path.Combine(home.Secrets, $"{provider.Name}-api-key");

	string? Key() {
		try {
			if(File.Exists(KeyPath) && File.ReadAllText(KeyPath).Trim() is { Length: > 0 } k) return k;
		} catch(IOException) { }
		return provider.EnvVar != null && Environment.GetEnvironmentVariable(provider.EnvVar) is { Length: > 0 } env ? env.Trim() : null;
	}

	public bool Connected => Key() != null;
	/// <summary>Where the key came from, for the UI. Never the key itself.</summary>
	public string? KeySource => File.Exists(KeyPath) ? "saved" : Key() != null ? provider.EnvVar : null;

	public void SetKey(string? key) {
		if(string.IsNullOrWhiteSpace(key)) {
			File.Delete(KeyPath);
			return;
		}
		AtomicFile.Write(KeyPath, key.Trim());
		if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
	}

	IAnthropicClient Client() {
		var key = Key() ?? throw new NotConnectedException($"No {provider.Label} API key. Add one under Backends.");
		// Set every credential explicitly, so an ANTHROPIC_* variable in the environment never reaches another provider.
		var client = provider.Bearer ? new AnthropicClient { ApiKey = null, AuthToken = key } : new AnthropicClient { ApiKey = key, AuthToken = null };
		// Marten's own loop retries with backoff and shows the wait; the SDK shouldn't retry underneath it.
		return client.WithOptions(o => o with { BaseUrl = provider.BaseUrl ?? o.BaseUrl, MaxRetries = 0, Timeout = TimeSpan.FromMinutes(30) });
	}

	/// <summary>
	/// Config extras, then the provider's own list from its models endpoint (cached for an hour),
	/// or the built-in list when the endpoint fails or isn't there.
	/// </summary>
	public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) {
		if(!Connected) return [];
		if(liveModels == null || DateTimeOffset.UtcNow - liveModelsAt > TimeSpan.FromHours(1)) {
			try {
				var page = await Client().Models.List(cancellationToken: ct);
				var ids = page.Items.Select(m => m.ID).ToList();
				liveModels = ids.Count > 0 ? ids : null;
			} catch(Exception e) when(e is not OperationCanceledException) {
				if(provider.Models.Count == 0) throw Map(e);
				liveModels = null;
			}
			liveModelsAt = DateTimeOffset.UtcNow;
		}
		return (extraModels?.Invoke(provider.Name) ?? []).Concat(liveModels ?? provider.Models).Distinct().ToList();
	}

	public async Task<ModelResult> CompleteAsync(ModelRequest req, Action<string> onTextDelta, CancellationToken ct) {
		var parameters = BuildParams(provider, req);
		var stream = new MessageStream(provider.Name, req.Model);
		try {
			await foreach(var ev in Client().Messages.CreateStreaming(parameters, ct))
				if(stream.Feed(ev) is { } delta) onTextDelta(delta);
		} catch(Exception e) when(e is not OperationCanceledException and not ContextFullException) {
			throw Map(e);
		}
		return stream.Finish();
	}

	Exception Map(Exception e) {
		var message = e.Message ?? "";
		return e switch {
			NotConnectedException => e,
			AnthropicRateLimitException => new RateLimitedException($"{provider.Label} rate limit reached."),
			AnthropicUnauthorizedException => new NotConnectedException($"{provider.Label} rejected the API key."),
			Anthropic5xxException => new TransientModelException($"{provider.Label} returned a server error.", e),
			AnthropicBadRequestException when message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("context", StringComparison.OrdinalIgnoreCase) && message.Contains("exceed", StringComparison.OrdinalIgnoreCase)
				=> new ContextFullException(),
			AnthropicIOException or AnthropicSseException or HttpRequestException or IOException
				=> new TransientModelException($"Could not reach {provider.Label}.", e),
			AnthropicApiException when message.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
				=> new TransientModelException($"{provider.Label} is overloaded.", e),
			// API errors carry only the provider's own error text, never our prompt.
			AnthropicApiException => new InvalidOperationException($"The {provider.Label} request failed: {Trim(message)}"),
			_ => e,
		};
	}

	static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;

	public static MessageCreateParams BuildParams(MessagesProvider provider, ModelRequest req) {
		var tools = req.Tools.Select(t => (ToolUnion) new Tool {
			Name = t.Name, Description = t.Description, InputSchema = Schema(t.Parameters),
		}).ToList();
		var p = new MessageCreateParams {
			Model = req.Model,
			MaxTokens = provider.MaxTokens,
			System = req.Instructions,
			Messages = ToMessages(provider.Name, req.Model, req.History, provider.Images),
			Tools = tools,
		};
		if(provider.ClaudeFeatures) {
			p = p with {
				Thinking = new ThinkingConfigAdaptive { Display = Display.Summarized },
				CacheControl = new CacheControlEphemeral(),
			};
		}
		if(provider.SendEffort && MapEffort(req.Effort) is { } effort)
			p = p with { OutputConfig = new OutputConfig { Effort = effort } };
		return p;
	}

	static Effort? MapEffort(string? effort) => effort?.ToLowerInvariant() switch {
		"minimal" or "none" or "low" => Effort.Low,
		"medium" => Effort.Medium,
		"high" => Effort.High,
		"xhigh" => Effort.Xhigh,
		"max" => Effort.Max,
		_ => null,
	};

	static InputSchema Schema(JsonObject parameters) {
		var props = new Dictionary<string, JsonElement>();
		if(parameters["properties"] is JsonObject ps)
			foreach(var (name, value) in ps) props[name] = JsonSerializer.SerializeToElement(value);
		var required = parameters["required"] is JsonArray r ? r.Select(x => x!.GetValue<string>()).ToList() : [];
		return new InputSchema { Properties = props, Required = required };
	}

	/// <summary>
	/// Convert the neutral log into Messages turns. Consecutive same-role content merges into one turn,
	/// tool results lead their user turn, and our own thinking replays only to the model that wrote it.
	/// </summary>
	public static List<MessageParam> ToMessages(string providerName, string model, IEnumerable<Entry> history, bool images = true) {
		var turns = new List<(bool User, List<ContentBlockParam> Blocks)>();
		List<ContentBlockParam> Turn(bool user) {
			if(turns.Count == 0 || turns[^1].User != user) turns.Add((user, new()));
			return turns[^1].Blocks;
		}
		foreach(var e in history) {
			switch(e.Kind) {
				case EntryKind.Message:
					if(!string.IsNullOrEmpty(e.Text)) Turn(true).Add(new TextBlockParam { Text = e.Text });
					break;
				case EntryKind.Assistant:
					if(!string.IsNullOrEmpty(e.Text)) Turn(false).Add(new TextBlockParam { Text = e.Text });
					break;
				case EntryKind.Reasoning:
					if(e.Provider == providerName && e.Model == model && e.Raw is { ValueKind: JsonValueKind.Object } raw && ThinkingParam(raw) is { } thinking)
						Turn(false).Add(thinking);
					break;
				case EntryKind.ToolCall:
					Turn(false).Add(new ToolUseBlockParam { ID = SafeId(e.CallId), Name = e.Name ?? "", Input = Input(e.Arguments) });
					break;
				case EntryKind.ToolResult:
					var blocks = Turn(true);
					// tool_result blocks must come before any text in their user turn.
					var at = blocks.TakeWhile(b => b.TryPickToolResult(out _)).Count();
					blocks.Insert(at, ResultParam(images ? e : WithoutImages(e)));
					break;
			}
		}
		RepairToolPairs(turns);
		return turns.Select(t => t.User
			? (MessageParam) new UserMessageParam { Content = t.Blocks }
			: new AssistantMessageParam { Content = t.Blocks }).ToList();
	}

	/// <summary>Every tool_use needs a tool_result in the next user turn; a lost one gets a stand-in.</summary>
	static void RepairToolPairs(List<(bool User, List<ContentBlockParam> Blocks)> turns) {
		for(var i = 0; i < turns.Count; i++) {
			if(turns[i].User) continue;
			var ids = turns[i].Blocks.Select(b => b.TryPickToolUse(out var u) ? u.ID : null).OfType<string>().ToList();
			if(ids.Count == 0) continue;
			if(i + 1 == turns.Count || !turns[i + 1].User) turns.Insert(i + 1, (true, new()));
			var next = turns[i + 1].Blocks;
			var have = next.Select(b => b.TryPickToolResult(out var r) ? r.ToolUseID : null).OfType<string>().ToHashSet();
			var at = next.TakeWhile(b => b.TryPickToolResult(out _)).Count();
			foreach(var id in ids.Where(id => !have.Contains(id)))
				next.Insert(at++, new ToolResultBlockParam { ToolUseID = id, Content = "No result was recorded for this call.", IsError = true });
		}
	}

	/// <summary>Tool use ids must match [a-zA-Z0-9_-]+; ids from other providers are mapped onto that.</summary>
	static string SafeId(string? id) {
		if(string.IsNullOrEmpty(id)) return "call_unknown";
		var sb = new StringBuilder(id.Length);
		foreach(var c in id) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
		return sb.ToString();
	}

	static IReadOnlyDictionary<string, JsonElement> Input(string? arguments) {
		try {
			if(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments ?? "{}") is { } d) return d;
		} catch(JsonException) { }
		// A truncated call still replays, as what it was.
		return new Dictionary<string, JsonElement> { ["_unparsed"] = JsonSerializer.SerializeToElement(arguments ?? "") };
	}

	/// <summary>For models that can't see images: say so, with the paths, instead of sending them.</summary>
	static Entry WithoutImages(Entry e) => e.Images is not { Count: > 0 } imgs ? e : e with {
		Images = null,
		Text = $"{e.Text}\n\n[{imgs.Count} image{(imgs.Count == 1 ? "" : "s")} not shown: this model can't see images. Saved at: {string.Join(", ", imgs.Select(i => i.Path))}]",
	};

	static ContentBlockParam ResultParam(Entry e) {
		var id = SafeId(e.CallId);
		var text = string.IsNullOrEmpty(e.Text) ? "(no output)" : e.Text;
		if(e.Images is not { Count: > 0 } images)
			return new ToolResultBlockParam { ToolUseID = id, Content = text, IsError = e.IsError == true ? true : null };
		var parts = new List<Block> { new TextBlockParam { Text = text } };
		foreach(var img in images) {
			try {
				parts.Add(new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(File.ReadAllBytes(img.Path)), MediaType = img.MimeType } });
			} catch(IOException) {
				parts.Add(new TextBlockParam { Text = $"[image {img.Path} is gone]" });
			}
		}
		return new ToolResultBlockParam { ToolUseID = id, Content = parts, IsError = e.IsError == true ? true : null };
	}

	static ContentBlockParam? ThinkingParam(JsonElement raw) {
		var type = raw.TryGetProperty("type", out var t) ? t.GetString() : null;
		if(type == "thinking" && raw.TryGetProperty("signature", out var sig))
			return new ThinkingBlockParam { Thinking = raw.TryGetProperty("thinking", out var th) ? th.GetString() ?? "" : "", Signature = sig.GetString() ?? "" };
		if(type == "redacted_thinking" && raw.TryGetProperty("data", out var data))
			return new RedactedThinkingBlockParam { Data = data.GetString() ?? "" };
		return null;
	}

	/// <summary>Accumulates a streamed message into log entries, in the order the blocks arrived.</summary>
	public sealed class MessageStream(string providerName, string model) {
		sealed class Block {
			public required string Type;
			public readonly StringBuilder Text = new();
			public string? Signature, Data, Id, Name;
		}
		readonly SortedDictionary<long, Block> blocks = new();
		long inputTokens, cacheCreate, cacheRead, outputTokens;
		string? stopReason;
		bool stopped;

		public string? Feed(RawMessageStreamEvent ev) {
			if(ev.TryPickStart(out var start)) {
				var u = start.Message.Usage;
				inputTokens = u.InputTokens;
				cacheCreate = u.CacheCreationInputTokens ?? 0;
				cacheRead = u.CacheReadInputTokens ?? 0;
				outputTokens = u.OutputTokens;
			} else if(ev.TryPickContentBlockStart(out var bs)) {
				var cb = bs.ContentBlock;
				Block b;
				if(cb.TryPickText(out var text)) { b = new Block { Type = "text" }; b.Text.Append(text.Text); }
				else if(cb.TryPickThinking(out var th)) { b = new Block { Type = "thinking", Signature = th.Signature }; b.Text.Append(th.Thinking); }
				else if(cb.TryPickRedactedThinking(out var rt)) b = new Block { Type = "redacted_thinking", Data = rt.Data };
				else if(cb.TryPickToolUse(out var tu)) b = new Block { Type = "tool_use", Id = tu.ID, Name = tu.Name };
				else b = new Block { Type = "other" };
				blocks[bs.Index] = b;
				if(b.Type == "text" && b.Text.Length > 0) return b.Text.ToString();
			} else if(ev.TryPickContentBlockDelta(out var bd)) {
				if(!blocks.TryGetValue(bd.Index, out var b)) return null;
				var d = bd.Delta;
				if(d.TryPickText(out var t)) { b.Text.Append(t.Text); return t.Text; }
				if(d.TryPickInputJson(out var j)) b.Text.Append(j.PartialJson);
				else if(d.TryPickThinking(out var th)) b.Text.Append(th.Thinking);
				else if(d.TryPickSignature(out var s)) b.Signature = (b.Signature ?? "") + s.Signature;
			} else if(ev.TryPickDelta(out var md)) {
				stopReason = md.Delta.StopReason?.ToString();
				outputTokens = md.Usage.OutputTokens;
				if(md.Usage.InputTokens is { } i) inputTokens = i;
				if(md.Usage.CacheCreationInputTokens is { } cc) cacheCreate = cc;
				if(md.Usage.CacheReadInputTokens is { } cr) cacheRead = cr;
			} else if(ev.TryPickStop(out _)) {
				stopped = true;
			}
			return null;
		}

		public ModelResult Finish() {
			if(!stopped) throw new TransientModelException("The model stream ended before completion.");
			var items = new List<Entry>();
			foreach(var b in blocks.Values) {
				switch(b.Type) {
					case "text":
						if(b.Text.Length > 0) items.Add(new Entry { Kind = EntryKind.Assistant, Text = b.Text.ToString(), Provider = providerName, Model = model });
						break;
					case "thinking":
						items.Add(new Entry {
							Kind = EntryKind.Reasoning, Text = b.Text.Length > 0 ? b.Text.ToString() : null, Provider = providerName, Model = model,
							Raw = JsonSerializer.SerializeToElement(new { type = "thinking", thinking = b.Text.ToString(), signature = b.Signature ?? "" }),
						});
						break;
					case "redacted_thinking":
						items.Add(new Entry {
							Kind = EntryKind.Reasoning, Provider = providerName, Model = model,
							Raw = JsonSerializer.SerializeToElement(new { type = "redacted_thinking", data = b.Data ?? "" }),
						});
						break;
					case "tool_use":
						var args = b.Text.Length > 0 ? b.Text.ToString() : "{}";
						items.Add(new Entry { Kind = EntryKind.ToolCall, CallId = b.Id, Name = b.Name, Arguments = args, Provider = providerName, Model = model });
						break;
				}
			}
			if(stopReason == "refusal" && !items.Any(i => i.Kind == EntryKind.Assistant))
				items.Add(new Entry { Kind = EntryKind.Assistant, Text = "(The model declined to continue.)", Provider = providerName, Model = model });
			// input_tokens counts only uncached input; the context size is the sum.
			var usage = JsonSerializer.SerializeToElement(new Dictionary<string, object?> {
				["input_tokens"] = inputTokens + cacheCreate + cacheRead,
				["output_tokens"] = outputTokens,
				["uncached_input_tokens"] = inputTokens,
				["cache_creation_input_tokens"] = cacheCreate,
				["cache_read_input_tokens"] = cacheRead,
				["stop_reason"] = stopReason,
			});
			return new ModelResult(items, usage);
		}
	}
}
