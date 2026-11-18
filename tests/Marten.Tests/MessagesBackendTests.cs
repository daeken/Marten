using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Marten.Models;
using Marten.Models.Anthropic;
using Marten.Storage;

namespace Marten.Tests;

public class MessagesBackendTests {
	static Entry E(string kind, string? text = null, string? provider = null, string? model = null, string? callId = null, string? name = null, string? args = null, JsonElement? raw = null) =>
		new() { Kind = kind, Text = text, Provider = provider, Model = model, CallId = callId, Name = name, Arguments = args, Raw = raw };

	static JsonArray Wire(List<Anthropic.Models.Messages.MessageParam> messages) =>
		JsonNode.Parse(JsonSerializer.Serialize(messages))!.AsArray();

	[Fact]
	public void TheLogBecomesAlternatingTurnsWithResultsFirstAndOnlyOwnThinking() {
		var thinking = JsonSerializer.SerializeToElement(new { type = "thinking", thinking = "hmm", signature = "sig" });
		var history = new List<Entry> {
			E(EntryKind.Wake),
			E(EntryKind.Message, "hi"),
			E(EntryKind.Message, "also this"),
			E(EntryKind.Reasoning, "hmm", "anthropic", "claude-opus-5-5", raw: thinking),
			E(EntryKind.Reasoning, "other model", "anthropic", "claude-sonnet-5-5", raw: thinking),
			E(EntryKind.Reasoning, "openai's", "openai", "gpt-5", raw: JsonSerializer.SerializeToElement(new { type = "reasoning" })),
			E(EntryKind.Assistant, "looking"),
			E(EntryKind.ToolCall, callId: "call_1", name: "read_file", args: "{\"path\":\"a\"}"),
			E(EntryKind.ToolCall, callId: "fc.2", name: "powershell", args: "{\"command\":"),
			// A steering message lands before the result; the result still leads the user turn.
			E(EntryKind.Message, "steer"),
			E(EntryKind.ToolResult, "contents", callId: "call_1"),
			E(EntryKind.Idle),
		};
		var wire = Wire(MessagesBackend.ToMessages("anthropic", "claude-opus-5-5", history));

		Assert.Equal(3, wire.Count);
		Assert.Equal("user", wire[0]!["role"]!.GetValue<string>());
		Assert.Equal(2, wire[0]!["content"]!.AsArray().Count);

		var assistant = wire[1]!["content"]!.AsArray();
		Assert.Equal(["thinking", "text", "tool_use", "tool_use"], assistant.Select(b => b!["type"]!.GetValue<string>()));
		Assert.Equal("sig", assistant[0]!["signature"]!.GetValue<string>());
		Assert.Equal("fc_2", assistant[3]!["id"]!.GetValue<string>());
		Assert.Equal("{\"command\":", assistant[3]!["input"]!["_unparsed"]!.GetValue<string>());

		var user = wire[2]!["content"]!.AsArray();
		Assert.Equal(["tool_result", "tool_result", "text"], user.Select(b => b!["type"]!.GetValue<string>()));
		Assert.Equal("call_1", user[0]!["tool_use_id"]!.GetValue<string>());
		Assert.Equal("fc_2", user[1]!["tool_use_id"]!.GetValue<string>());
		Assert.True(user[1]!["is_error"]!.GetValue<bool>());
		Assert.Equal("steer", user[2]!["text"]!.GetValue<string>());
	}

	[Fact]
	public void ImagesBecomeANoteForProvidersThatCantSeeThem() {
		using var dir = new TempDir();
		var png = dir.File("shot.png");
		File.WriteAllBytes(png, [1, 2, 3]);
		var history = new List<Entry> {
			E(EntryKind.Message, "look"),
			E(EntryKind.ToolCall, callId: "c1", name: "shot", args: "{}"),
			E(EntryKind.ToolResult, "took it", callId: "c1") with { Images = [new ImageRef(png, "image/png")] },
		};
		var seeing = Wire(MessagesBackend.ToMessages("p", "m", history));
		Assert.Equal("image", seeing[2]!["content"]![0]!["content"]![1]!["type"]!.GetValue<string>());
		var blind = Wire(MessagesBackend.ToMessages("p", "m", history, images: false));
		var text = blind[2]!["content"]![0]!["content"]!.GetValue<string>();
		Assert.Contains("can't see images", text);
		Assert.Contains(png, text);
	}

	[Fact]
	public void ModelsRouteByPrefixOrExplicitProvider() {
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var primary = new FakeBackend();
		var claude = new MessagesBackend(MessagesProvider.Anthropic, home);
		var zai = new MessagesBackend(MessagesProvider.Zai, home);
		var deepseek = new MessagesBackend(MessagesProvider.DeepSeek, home);
		var router = new BackendRouter(primary, [(claude, claude.Settings.Prefixes), (zai, zai.Settings.Prefixes), (deepseek, deepseek.Settings.Prefixes)]);

		Assert.Same(claude, router.Resolve("claude-opus-5-5").Backend);
		Assert.Same(zai, router.Resolve("glm-4.7").Backend);
		Assert.Same(deepseek, router.Resolve("deepseek-v4-pro").Backend);
		Assert.Same(primary, router.Resolve("gpt-5.2-codex").Backend);
		Assert.Equal((zai, "some-model"), router.Resolve("zai/some-model"));
	}

	[Fact]
	public async Task OnlyConnectedBackendsListModels() {
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		// Nothing listens on the discard port, so the live listing fails fast and the built-in list stands in.
		var zai = new MessagesBackend(MessagesProvider.Zai with { BaseUrl = "http://127.0.0.1:9", EnvVar = null }, home);
		var deepseek = new MessagesBackend(MessagesProvider.DeepSeek with { EnvVar = null }, home);
		var router = new BackendRouter(new FakeBackend(), [(zai, zai.Settings.Prefixes), (deepseek, deepseek.Settings.Prefixes)]);
		var before = await router.ListModelsAsync(default);
		zai.SetKey("k");
		var after = await router.ListModelsAsync(default);
		Assert.Equal(before.Concat(MessagesProvider.Zai.Models), after);
		Assert.Equal("saved", zai.KeySource);
		zai.SetKey(null);
		Assert.Null(zai.KeySource);
	}

	[Fact]
	public async Task AStreamedReplyBecomesEntriesAndTheRequestCarriesTheRightFields() {
		using var dir = new TempDir();
		using var server = new FakeMessagesServer(Sse(
			("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"m","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1,"cache_creation_input_tokens":5,"cache_read_input_tokens":100}}}"""),
			("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}"""),
			("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me look."}}"""),
			("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig123"}}"""),
			("content_block_stop", """{"type":"content_block_stop","index":0}"""),
			("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}"""),
			("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hello"}}"""),
			("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":" world"}}"""),
			("content_block_stop", """{"type":"content_block_stop","index":1}"""),
			("content_block_start", """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"read_file","input":{}}}"""),
			("content_block_delta", """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}"""),
			("content_block_delta", """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"a.txt\"}"}}"""),
			("content_block_stop", """{"type":"content_block_stop","index":2}"""),
			("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":42}}"""),
			("message_stop", """{"type":"message_stop"}""")));
		var provider = MessagesProvider.Anthropic with { Name = "test", BaseUrl = server.Url, Bearer = true, EnvVar = null };
		var backend = new MessagesBackend(provider, new Home(dir.File("home")));
		backend.SetKey("k-test");

		var old = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
		Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "must-not-leak");
		ModelResult result;
		var deltas = new StringBuilder();
		try {
			var tools = new List<ToolSpec> { new("read_file", "Read a file", new JsonObject {
				["type"] = "object", ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("path"),
			}) };
			result = await backend.CompleteAsync(new ModelRequest("claude-opus-5-5", "minimal", "You are Marten.", [E(EntryKind.Message, "hi")], tools, "cache"),
				d => deltas.Append(d), default);
		} finally {
			Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", old);
		}

		Assert.Equal("Bearer k-test", server.Headers!["Authorization"]);
		Assert.Null(server.Headers["x-api-key"]);
		var body = JsonNode.Parse(server.Body!)!;
		Assert.Equal("claude-opus-5-5", body["model"]!.GetValue<string>());
		Assert.Equal("You are Marten.", body["system"]!.GetValue<string>());
		Assert.True(body["stream"]!.GetValue<bool>());
		Assert.Equal("adaptive", body["thinking"]!["type"]!.GetValue<string>());
		Assert.Equal("low", body["output_config"]!["effort"]!.GetValue<string>());
		Assert.Equal("ephemeral", body["cache_control"]!["type"]!.GetValue<string>());
		Assert.Equal("path", body["tools"]![0]!["input_schema"]!["required"]![0]!.GetValue<string>());

		Assert.Equal("Hello world", deltas.ToString());
		Assert.Equal([EntryKind.Reasoning, EntryKind.Assistant, EntryKind.ToolCall], result.Items.Select(i => i.Kind));
		Assert.Equal("Let me look.", result.Items[0].Text);
		Assert.Equal("sig123", result.Items[0].Raw!.Value.GetProperty("signature").GetString());
		Assert.Equal("{\"path\":\"a.txt\"}", result.Items[2].Arguments);
		Assert.Equal("toolu_1", result.Items[2].CallId);
		Assert.All(result.Items, i => Assert.Equal("test", i.Provider));
		Assert.Equal(115, result.Usage!.Value.GetProperty("input_tokens").GetInt64());

		// The reply replays: the thinking block goes back with its signature.
		var replay = Wire(MessagesBackend.ToMessages("test", "claude-opus-5-5", [E(EntryKind.Message, "hi"), .. result.Items, E(EntryKind.ToolResult, "x", callId: "toolu_1")]));
		Assert.Equal("sig123", replay[1]!["content"]![0]!["signature"]!.GetValue<string>());
	}

	[Theory]
	[InlineData(429, """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""", typeof(RateLimitedException))]
	[InlineData(400, """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 210000 tokens > 200000 maximum"}}""", typeof(ContextFullException))]
	[InlineData(529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", typeof(TransientModelException))]
	[InlineData(401, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", typeof(NotConnectedException))]
	public async Task ErrorsMapOntoTheRuntimesOwn(int status, string body, Type expected) {
		using var dir = new TempDir();
		using var server = new FakeMessagesServer(body, status, "application/json");
		var backend = new MessagesBackend(MessagesProvider.Anthropic with { Name = "test", BaseUrl = server.Url, EnvVar = null }, new Home(dir.File("home")));
		backend.SetKey("k");
		var e = await Assert.ThrowsAnyAsync<Exception>(() =>
			backend.CompleteAsync(new ModelRequest("claude-opus-5-5", null, "x", [E(EntryKind.Message, "hi")], [], "c"), _ => { }, default));
		Assert.IsType(expected, e);
	}

	[Fact]
	public async Task TheProvidersOwnListWinsAndConfigExtrasComeFirst() {
		using var dir = new TempDir();
		using var server = new FakeMessagesServer(
			"""{"data":[{"type":"model","id":"glm-9","display_name":"GLM 9","created_at":"2026-01-01T00:00:00Z"}],"has_more":false,"first_id":"glm-9","last_id":"glm-9"}""",
			contentType: "application/json");
		var zai = new MessagesBackend(MessagesProvider.Zai with { BaseUrl = server.Url, EnvVar = null }, new Home(dir.File("home")), _ => ["glm-custom"]);
		zai.SetKey("k");
		Assert.Equal(["glm-custom", "glm-9"], await zai.ListModelsAsync(default));
	}

	[Fact]
	public async Task NoKeyMeansNotConnected() {
		using var dir = new TempDir();
		var backend = new MessagesBackend(MessagesProvider.Zai with { EnvVar = null }, new Home(dir.File("home")));
		Assert.False(backend.Connected);
		await Assert.ThrowsAsync<NotConnectedException>(() =>
			backend.CompleteAsync(new ModelRequest("glm-4.7", null, "x", [], [], "c"), _ => { }, default));
	}

	static string Sse(params (string Event, string Data)[] events) =>
		string.Concat(events.Select(e => $"event: {e.Event}\ndata: {e.Data}\n\n"));

	/// <summary>Answers every request once with a canned body, and keeps the last request's headers and body.</summary>
	sealed class FakeMessagesServer : IDisposable {
		readonly HttpListener listener = new();
		public string Url { get; }
		public System.Collections.Specialized.NameValueCollection? Headers { get; private set; }
		public string? Body { get; private set; }

		public FakeMessagesServer(string response, int status = 200, string contentType = "text/event-stream") {
			var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			var port = ((IPEndPoint) probe.LocalEndpoint).Port;
			probe.Stop();
			Url = $"http://127.0.0.1:{port}";
			listener.Prefixes.Add(Url + "/");
			listener.Start();
			_ = Task.Run(async () => {
				while(listener.IsListening) {
					HttpListenerContext ctx;
					try { ctx = await listener.GetContextAsync(); } catch { return; }
					Headers = ctx.Request.Headers;
					using(var reader = new StreamReader(ctx.Request.InputStream)) Body = await reader.ReadToEndAsync();
					ctx.Response.StatusCode = status;
					ctx.Response.ContentType = contentType;
					var bytes = Encoding.UTF8.GetBytes(response);
					await ctx.Response.OutputStream.WriteAsync(bytes);
					ctx.Response.Close();
				}
			});
		}

		public void Dispose() => listener.Close();
	}
}
