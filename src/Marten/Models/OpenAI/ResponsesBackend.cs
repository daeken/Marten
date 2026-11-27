using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Marten.Storage;

namespace Marten.Models.OpenAI;

internal sealed record ResponseCompletedMetadata(string? Model, string? ReasoningEffort, long? ReasoningTokens);

/// <summary>The OpenAI Responses API over a ChatGPT subscription token. Stateless: store=false, full history each call.</summary>
public sealed class ResponsesBackend(ChatGptAuth auth, HttpClient http) : IModelBackend {
	public const string ProviderName = "openai";
	public const string ToolNamespace = "marten";
	static readonly HashSet<string> ContextCodes = ["context_length_exceeded", "context_window_exceeded", "too_many_tokens"];
	static readonly HashSet<string> QuotaCodes = ["subscription_sharing_usage_limit_exceeded", "subscription_sharing_usage_unavailable", "rate_limit_exceeded", "usage_limit_reached"];
	// OpenAI blocks the request itself (a moderation hit) rather than failing: retrying can't help.
	// Besides the generic codes, each safety domain has its own: cyber_policy, and its siblings.
	static readonly HashSet<string> RefusalCodes = ["content_policy_violation", "content_filter", "policy_violation", "invalid_prompt"];

	/// <summary>Whether a provider code means the request was blocked on policy grounds.</summary>
	internal static bool IsRefusal(string? code) => code != null && (RefusalCodes.Contains(code)
		|| code.EndsWith("_policy", StringComparison.OrdinalIgnoreCase)
		|| code.Contains("moderation", StringComparison.OrdinalIgnoreCase));
	const string ModelNotFoundCode = "model_not_found";
	static InvalidOperationException ModelNotFound() => new("The selected model is unavailable or not accessible (model_not_found). Check the agent's model settings.");

	public string Provider => ProviderName;
	public bool Connected => auth.Connected;

	async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct) {
		for(var attempt = 0; ; attempt++) {
			using var request = build();
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.TokenAsync(attempt == 1, ct));
			HttpResponseMessage response;
			try {
				response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
			} catch(HttpRequestException e) {
				ct.ThrowIfCancellationRequested();
				throw ModelHttp.IsConnectFailure(e) ? new ProviderUnreachableException("Could not reach OpenAI.", e)
					: new TransientModelException("OpenAI request failed before receiving response headers.", e);
			}
			if(response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) {
				response.Dispose();
				continue;
			}
			if(response.IsSuccessStatusCode) return response;
			using(response) throw await ErrorFor(response, ct);
		}
	}

	static async Task<Exception> ErrorFor(HttpResponseMessage response, CancellationToken ct) {
		var status = (int) response.StatusCode;
		string? code = null, message = null;
		try {
			var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
			code = body?["error"]?["code"]?.GetValue<string>() ?? body?["error"]?["type"]?.GetValue<string>();
			message = body?["error"]?["message"]?.GetValue<string>();
		} catch(Exception e) when(e is not OperationCanceledException and not TransientModelException) { }
		if(code == ModelNotFoundCode) return ModelNotFound();
		if(status == 413 || (code != null && ContextCodes.Contains(code))) return new ContextFullException();
		if(status == 429 || (code != null && QuotaCodes.Contains(code)))
			return new RateLimitedException($"ChatGPT usage limit reached ({code ?? "429"}).", response.Headers.RetryAfter?.Delta);
		if(IsRefusal(code)) return new ModelRefusedException($"OpenAI blocked the request ({code}).", message, code);
		if(status is 401 or 403) return new NotConnectedException("ChatGPT rejected Marten's access. Sign in again.");
		if(status >= 500) return new TransientModelException($"OpenAI returned {status}.");
		// Keep only safe atoms from error bodies: never echo prompts or HTML back into a transcript.
		return new InvalidOperationException($"The model request failed ({status}{(code != null ? $", {code}" : "")}).");
	}

	public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) {
		using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{ChatGptAuth.Resource}/models"), ct);
		var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
		return body?["models"]?.AsArray()
			.Where(m => m?["visibility"]?.GetValue<string>() == "list")
			.Select(m => m!["slug"]!.GetValue<string>()).ToList() ?? [];
	}

	public async Task<ModelResult> CompleteAsync(ModelRequest req, Action<string> onTextDelta, CancellationToken ct) {
		var body = BuildBody(req);
		var json = body.ToJsonString();
		using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{ChatGptAuth.Resource}/responses") {
			Content = new StringContent(json, Encoding.UTF8, "application/json"),
			Headers = { Accept = { new("text/event-stream") } },
		}, ct);
		return await ReadResponse(response, req, onTextDelta, ct);
	}

	internal static async Task<ModelResult> ReadResponse(HttpResponseMessage response, ModelRequest req, Action<string> onTextDelta, CancellationToken ct, string provider = ProviderName, Action<ResponseCompletedMetadata>? onCompleted = null) {
		var stream = new ResponseStream(provider);
		try {
			await foreach(var ev in Sse.ReadAsync(await response.Content.ReadAsStreamAsync(ct), ct))
				if(stream.Feed(ev) is { } delta) onTextDelta(delta);
		} catch(IOException e) {
			throw new TransientModelException("The model stream was cut off.", e);
		}
		var result = stream.Finish(req.Model);
		if(stream.Metadata is { } metadata) onCompleted?.Invoke(metadata);
		return result;
	}

	public static JsonObject BuildBody(ModelRequest req) {
		var body = new JsonObject {
			["model"] = req.Model,
			["instructions"] = req.Instructions,
			["input"] = ToWire(req.History),
			["tools"] = new JsonArray(new JsonObject {
				["type"] = "namespace", ["name"] = ToolNamespace, ["description"] = "Marten runtime tools",
				["tools"] = new JsonArray(req.Tools.Select(t => (JsonNode) new JsonObject {
					["type"] = "function", ["name"] = t.Name, ["description"] = t.Description,
					["parameters"] = t.Parameters.DeepClone(), ["strict"] = t.Strict,
				}).ToArray()),
			}),
			["store"] = false,
			["stream"] = true,
			["include"] = new JsonArray("reasoning.encrypted_content"),
			// The subscription route rejects prompt_cache_retention; the key alone keeps routing stable.
			["prompt_cache_key"] = req.CacheKey,
		};
		if(req.Effort != null) body["reasoning"] = new JsonObject { ["effort"] = req.Effort };
		return body;
	}

	/// <summary>Convert the neutral log into Responses input items. Our own raw items replay verbatim.</summary>
	public static JsonArray ToWire(IEnumerable<Entry> history, string provider = ProviderName) {
		var input = new JsonArray();
		foreach(var e in history) {
			var mine = e.Provider == provider && e.Raw is { } raw;
			switch(e.Kind) {
				case EntryKind.Message:
					input.Add(new JsonObject {
						["type"] = "message", ["role"] = "user",
						["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = e.Text ?? "" }),
					});
					break;
				case EntryKind.Assistant:
					input.Add(mine ? JsonNode.Parse(e.Raw!.Value.GetRawText()) : new JsonObject {
						["type"] = "message", ["role"] = "assistant",
						["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = e.Text ?? "" }),
					});
					break;
				case EntryKind.Reasoning:
					// Another provider's reasoning can't cross over; it is simply dropped.
					if(mine) input.Add(JsonNode.Parse(e.Raw!.Value.GetRawText()));
					break;
				case EntryKind.ToolCall:
					if(mine) {
						input.Add(JsonNode.Parse(e.Raw!.Value.GetRawText()));
					} else {
						var call = new JsonObject {
							["type"] = "function_call", ["call_id"] = e.CallId, ["name"] = e.Name, ["arguments"] = e.Arguments ?? "{}",
						};
						// Calls from other providers carry no namespace; every Marten tool lives in ours.
						if(provider == ProviderName) call["namespace"] = e.Namespace ?? ToolNamespace;
						input.Add(call);
					}
					break;
				case EntryKind.ToolResult:
					input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = e.CallId, ["output"] = ToolOutputWire(e) });
					break;
			}
		}
		return input;
	}

	/// <summary>Plain text, or text plus images as input content when the result carries images.</summary>
	static JsonNode ToolOutputWire(Entry e) {
		if(e.Images is not { Count: > 0 } images) return e.Text ?? "";
		var parts = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = e.Text ?? "" });
		foreach(var img in images) {
			byte[] bytes;
			try { bytes = File.ReadAllBytes(img.Path); } catch { parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = $"[image {img.Path} is gone]" }); continue; }
			parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{img.MimeType};base64,{Convert.ToBase64String(bytes)}" });
		}
		return parts;
	}

	/// <summary>
	/// Accumulates a streamed response. Items are collected from output_item events as well as
	/// response.completed, because the completed event's output can arrive empty.
	/// </summary>
	public sealed class ResponseStream(string provider = ProviderName) {
		readonly List<JsonObject> output = new();
		readonly StringBuilder text = new();
		// A refusal arrives either as its own delta events or as a content part of type "refusal".
		readonly StringBuilder refusal = new();
		bool completed;
		JsonNode? usage;
		internal ResponseCompletedMetadata? Metadata { get; private set; }

		static string? StringValue(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

		void Put(JsonNode? node) {
			if(node is not JsonObject item) return;
			var id = item["id"]?.GetValue<string>();
			var existing = id == null ? null : output.FirstOrDefault(o => o["id"]?.GetValue<string>() == id);
			if(existing == null) {
				output.Add((JsonObject) item.DeepClone());
				return;
			}
			foreach(var (key, value) in item) existing[key] = value?.DeepClone();
		}

		/// <summary>Feed one event; returns a text delta to stream to the UI, if any.</summary>
		public string? Feed(JsonObject ev) {
			switch(ev["type"]?.GetValue<string>()) {
				case "response.output_text.delta":
					var delta = ev["delta"]?.GetValue<string>();
					text.Append(delta);
					return delta;
				case "response.refusal.delta":
					// Not streamed to the UI: a fallback model answers instead.
					refusal.Append(ev["delta"]?.GetValue<string>());
					break;
				case "response.output_item.added":
				case "response.output_item.done":
					Put(ev["item"]);
					break;
				case "response.function_call_arguments.done":
					var id = ev["item_id"]?.GetValue<string>();
					if(output.FirstOrDefault(o => o["id"]?.GetValue<string>() == id) is { } call && ev["arguments"] is { } args)
						call["arguments"] = args.DeepClone();
					break;
				case "response.completed":
					completed = true;
					if(ev["response"]?["output"] is JsonArray items)
						foreach(var item in items) Put(item);
					var response = ev["response"] as JsonObject;
					usage = response?["usage"]?.DeepClone();
					var details = (usage as JsonObject)?["output_tokens_details"] as JsonObject;
					Metadata = new(StringValue(response?["model"]), StringValue((response?["reasoning"] as JsonObject)?["effort"]),
						details?["reasoning_tokens"] is JsonValue tokens && tokens.TryGetValue<long>(out var n) ? n : null);
					break;
				case "response.failed":
				case "response.incomplete":
				case "error":
					var error = ev["response"]?["error"] as JsonObject ?? ev["error"] as JsonObject;
					var code = StringValue(error?["code"]) ?? StringValue(ev["code"])
						?? StringValue(ev["response"]?["incomplete_details"]?["reason"]);
					if(code == ModelNotFoundCode) throw ModelNotFound();
					if(code != null && ContextCodes.Contains(code)) throw new ContextFullException();
					if(code != null && QuotaCodes.Contains(code)) throw new RateLimitedException($"ChatGPT usage limit reached ({code}).");
					if(IsRefusal(code)) throw new ModelRefusedException($"OpenAI blocked the request ({code}).", StringValue(error?["message"]), code);
					throw new TransientModelException($"The model response {(ev["type"]!.GetValue<string>() == "response.incomplete" ? "was incomplete" : "failed")}{(code != null ? $" ({code})" : "")}.");
			}
			return null;
		}

		public ModelResult Finish(string model) {
			if(!completed) throw new TransientModelException("The model stream ended before completion.");
			// Refusals can arrive only as content parts, with no refusal delta events at all; the completed
			// item holds the whole sentence, so it wins over the deltas when both are present.
			var wholeRefusal = new StringBuilder();
			foreach(var item in output.Where(i => i["type"]?.GetValue<string>() == "message"))
				if(item["content"] is JsonArray parts)
					foreach(var part in parts.Where(p => p?["type"]?.GetValue<string>() == "refusal"))
						wholeRefusal.Append(part?["refusal"]?.GetValue<string>());
			var refused = wholeRefusal.Length > 0 ? wholeRefusal.ToString() : refusal.ToString();
			if(refused.Length > 0) throw new ModelRefusedException("The model refused the request.", refused, "refusal");
			var items = new List<Entry>();
			foreach(var item in output) {
				var raw = JsonSerializer.Deserialize<JsonElement>(item.ToJsonString());
				switch(item["type"]?.GetValue<string>()) {
					case "message":
						var t = string.Concat(item["content"]?.AsArray()
							.Where(c => c?["type"]?.GetValue<string>() == "output_text")
							.Select(c => c!["text"]?.GetValue<string>()) ?? []);
						items.Add(new Entry { Kind = EntryKind.Assistant, Text = t, Provider = provider, Model = model, Raw = raw });
						break;
					case "reasoning":
						var summary = string.Join("\n\n", item["summary"]?.AsArray()
							.Select(s => s?["text"]?.GetValue<string>()).Where(s => s != null) ?? []);
						items.Add(new Entry { Kind = EntryKind.Reasoning, Text = summary.Length > 0 ? summary : null, Provider = provider, Model = model, Raw = raw });
						break;
					case "function_call":
						items.Add(new Entry {
							Kind = EntryKind.ToolCall, CallId = item["call_id"]?.GetValue<string>(), Name = item["name"]?.GetValue<string>(),
							Namespace = item["namespace"]?.GetValue<string>(), Arguments = item["arguments"]?.GetValue<string>() ?? "{}",
							Provider = provider, Model = model, Raw = raw,
						});
						break;
				}
			}
			JsonElement? u = usage == null ? null : JsonSerializer.Deserialize<JsonElement>(usage.ToJsonString());
			return new ModelResult(items, u);
		}
	}
}
