using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Marten.Storage;

namespace Marten.Models.OpenAI;

/// <summary>The OpenAI Responses API over a ChatGPT subscription token. Stateless: store=false, full history each call.</summary>
public sealed class ResponsesBackend(ChatGptAuth auth, HttpClient http) : IModelBackend {
	public const string ProviderName = "openai";
	public const string ToolNamespace = "marten";
	static readonly HashSet<string> ContextCodes = ["context_length_exceeded", "context_window_exceeded", "too_many_tokens"];
	static readonly HashSet<string> QuotaCodes = ["subscription_sharing_usage_limit_exceeded", "subscription_sharing_usage_unavailable", "rate_limit_exceeded", "usage_limit_reached"];

	public string Provider => ProviderName;
	public bool Connected => auth.Connected;

	async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct) {
		for(var attempt = 0; ; attempt++) {
			var request = build();
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.TokenAsync(attempt == 1, ct));
			HttpResponseMessage response;
			try {
				response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
			} catch(HttpRequestException e) {
				throw new TransientModelException("Could not reach OpenAI.", e);
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
		string? code = null;
		try {
			var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
			code = body?["error"]?["code"]?.GetValue<string>() ?? body?["error"]?["type"]?.GetValue<string>();
		} catch { }
		if(status == 413 || (code != null && ContextCodes.Contains(code))) return new ContextFullException();
		if(status == 429 || (code != null && QuotaCodes.Contains(code)))
			return new RateLimitedException($"ChatGPT usage limit reached ({code ?? "429"}).", response.Headers.RetryAfter?.Delta);
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
		var stream = new ResponseStream();
		try {
			await foreach(var ev in Sse.ReadAsync(await response.Content.ReadAsStreamAsync(ct), ct))
				if(stream.Feed(ev) is { } delta) onTextDelta(delta);
		} catch(IOException e) {
			throw new TransientModelException("The model stream was cut off.", e);
		}
		return stream.Finish(req.Model);
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
					["parameters"] = t.Parameters.DeepClone(), ["strict"] = true,
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
	public static JsonArray ToWire(IEnumerable<Entry> history) {
		var input = new JsonArray();
		foreach(var e in history) {
			var mine = e.Provider == ProviderName && e.Raw is { } raw;
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
						if(e.Namespace != null) call["namespace"] = e.Namespace;
						input.Add(call);
					}
					break;
				case EntryKind.ToolResult:
					input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = e.CallId, ["output"] = e.Text ?? "" });
					break;
			}
		}
		return input;
	}

	/// <summary>
	/// Accumulates a streamed response. Items are collected from output_item events as well as
	/// response.completed, because the completed event's output can arrive empty.
	/// </summary>
	public sealed class ResponseStream {
		readonly List<JsonObject> output = new();
		readonly StringBuilder text = new();
		bool completed;
		JsonNode? usage;

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
					usage = ev["response"]?["usage"]?.DeepClone();
					break;
				case "response.failed":
				case "response.incomplete":
				case "error":
					var code = ev["response"]?["error"]?["code"]?.GetValue<string>() ?? ev["error"]?["code"]?.GetValue<string>()
						?? ev["code"]?.GetValue<string>() ?? ev["response"]?["incomplete_details"]?["reason"]?.GetValue<string>();
					if(code != null && ContextCodes.Contains(code)) throw new ContextFullException();
					if(code != null && QuotaCodes.Contains(code)) throw new RateLimitedException($"ChatGPT usage limit reached ({code}).");
					throw new TransientModelException($"The model response {(ev["type"]!.GetValue<string>() == "response.incomplete" ? "was incomplete" : "failed")}{(code != null ? $" ({code})" : "")}.");
			}
			return null;
		}

		public ModelResult Finish(string model) {
			if(!completed) throw new TransientModelException("The model stream ended before completion.");
			var items = new List<Entry>();
			foreach(var item in output) {
				var raw = JsonSerializer.Deserialize<JsonElement>(item.ToJsonString());
				switch(item["type"]?.GetValue<string>()) {
					case "message":
						var t = string.Concat(item["content"]?.AsArray()
							.Where(c => c?["type"]?.GetValue<string>() == "output_text")
							.Select(c => c!["text"]?.GetValue<string>()) ?? []);
						items.Add(new Entry { Kind = EntryKind.Assistant, Text = t, Provider = ProviderName, Model = model, Raw = raw });
						break;
					case "reasoning":
						var summary = string.Join("\n\n", item["summary"]?.AsArray()
							.Select(s => s?["text"]?.GetValue<string>()).Where(s => s != null) ?? []);
						items.Add(new Entry { Kind = EntryKind.Reasoning, Text = summary.Length > 0 ? summary : null, Provider = ProviderName, Model = model, Raw = raw });
						break;
					case "function_call":
						items.Add(new Entry {
							Kind = EntryKind.ToolCall, CallId = item["call_id"]?.GetValue<string>(), Name = item["name"]?.GetValue<string>(),
							Namespace = item["namespace"]?.GetValue<string>(), Arguments = item["arguments"]?.GetValue<string>() ?? "{}",
							Provider = ProviderName, Model = model, Raw = raw,
						});
						break;
				}
			}
			JsonElement? u = usage == null ? null : JsonSerializer.Deserialize<JsonElement>(usage.ToJsonString());
			return new ModelResult(items, u);
		}
	}
}
