using System.Text.Json;
using System.Text.Json.Nodes;
using Marten.Models;
using Marten.Models.OpenAI;
using Marten.Storage;
using Xunit;

namespace Marten.Tests;

public class BedrockResponsesTests {
	sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond());
	}

	static BedrockResponsesBackend TestBackend(HttpClient http, Action<string>? trace = null) => new(http,
		new() { RegionEndpoint = Amazon.RegionEndpoint.USEast1, DefaultAWSCredentials = new Amazon.Runtime.BasicAWSCredentials("test", "test") },
		"us-east-1", false, _ => [], traceEffort: trace);

	[Theory]
	[InlineData(HttpRequestError.NameResolutionError, true)]
	[InlineData(HttpRequestError.ConnectionError, true)]
	[InlineData(HttpRequestError.ResponseEnded, false)]
	[InlineData(HttpRequestError.SecureConnectionError, false)]
	public async Task OnlyConnectionFailuresEnterNetworkRecovery(HttpRequestError kind, bool outage) {
		using var http = new HttpClient(new StubHandler(() => throw new HttpRequestException(kind, "test")));
		var backend = TestBackend(http);
		Func<Task> call = () => backend.CompleteAsync(new("model", null, "", [], [], "cache"), _ => { }, default);
		if(outage) await Assert.ThrowsAsync<ProviderUnreachableException>(call);
		else await Assert.ThrowsAsync<TransientModelException>(call);
	}

	[Fact]
	public void WireInspectionIgnoresLookalikesInInputAndTools() {
		Assert.Null(BedrockResponsesBackend.ReadWireEffort("""{"input":[{"reasoning":{"effort":"high"}}],"tools":[{"effort":"low"}]}"""u8));
		Assert.Equal("high", BedrockResponsesBackend.ReadWireEffort("""{"reasoning":{"nested":{"effort":"low"},"effort":"high"}}"""u8));
	}

	[Fact]
	public async Task OptInTraceReportsEffortComparisonWithoutOtherRequestData() {
		var sse = "data: " + """{"type":"response.completed","response":{"model":"sensitive-model","reasoning":{"effort":"sensitive-label"},"usage":{"output_tokens_details":{"reasoning_tokens":3}}}}""" + "\n\n";
		using var http = new HttpClient(new StubHandler(() => new() { Content = new StringContent(sse) }));
		List<string> events = [];
		await TestBackend(http, events.Add).CompleteAsync(new("sensitive-model", "low", "sensitive-prompt", [], [], "sensitive-key"), _ => { }, default);
		Assert.Equal(2, events.Count);
		Assert.DoesNotContain("sensitive", string.Concat(events));
		var request = JsonNode.Parse(events[0])!;
		var completed = JsonNode.Parse(events[1])!;
		Assert.Equal(request["call"]!.GetValue<string>(), completed["call"]!.GetValue<string>());
		Assert.Equal("low", request["selected"]!.GetValue<string>());
		Assert.Equal("low", request["wire"]!.GetValue<string>());
		Assert.True(request["selectedMatchesWire"]!.GetValue<bool>());
		Assert.Equal("other", completed["server"]!.GetValue<string>());
		Assert.False(completed["wireMatchesServer"]!.GetValue<bool>());
		Assert.Equal(3, completed["reasoningTokens"]!.GetValue<int>());
	}
	sealed class RecordingHandler : HttpMessageHandler {
		public List<Uri> Uris { get; } = [];
		public List<long?> Lengths { get; } = [];
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
			Uris.Add(request.RequestUri!);
			if(request.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
				Content = new StringContent("""{"data":[{"id":"example-model"}]}"""),
			});
			Lengths.Add(request.Content!.Headers.ContentLength);
			return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest));
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task EnforcesInclusiveWireByteLimitBeforeTransport(bool mantle) {
		var handler = new RecordingHandler();
		using var http = new HttpClient(handler);
		var backend = new BedrockResponsesBackend(http, new() { RegionEndpoint = Amazon.RegionEndpoint.USEast1,
			DefaultAWSCredentials = new Amazon.Runtime.BasicAWSCredentials("test", "test") }, "us-east-1", mantle, _ => []);
		var req = new ModelRequest("model", null, "é\"", [], [], "cache");
		var overhead = JsonSerializer.SerializeToUtf8Bytes(BedrockResponsesBackend.BuildBody(req, backend.Provider)).Length;
		req = req with { Instructions = req.Instructions + new string('x', 32_000_000 - overhead) };
		// Exactly at the budget: send, and don't misclassify the handler's ordinary 400.
		var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.CompleteAsync(req, _ => { }, default));
		Assert.Contains("HTTP 400", rejection.Message);
		var overflow = await Assert.ThrowsAsync<ContextFullException>(() => backend.CompleteAsync(req with { Instructions = req.Instructions + "x" }, _ => { }, default));
		Assert.Equal(32_000_001, overflow.RequestBytes);
		Assert.Equal(32_000_000L, Assert.Single(handler.Lengths));
	}

	[Theory]
	[InlineData(false, "example-model", "https://bedrock-runtime.us-east-1.amazonaws.com/openai/v1/responses")]
	[InlineData(true, "example-model", "https://bedrock-mantle.us-east-1.api.aws/v1/responses")]
	[InlineData(true, "example-alternate", "https://bedrock-mantle.us-east-1.api.aws/openai/v1/responses")]
	public async Task SendsToDocumentedRoute(bool mantle, string model, string url) {
		var handler = new RecordingHandler();
		using var http = new HttpClient(handler);
		var backend = new BedrockResponsesBackend(http, new() { RegionEndpoint = Amazon.RegionEndpoint.USEast1,
			DefaultAWSCredentials = new Amazon.Runtime.BasicAWSCredentials("test", "test") }, "us-east-1", mantle,
			_ => [], new HashSet<string> { "example-alternate" });
		await Assert.ThrowsAsync<InvalidOperationException>(() => backend.CompleteAsync(new(model, null, "", [], [], "cache"), _ => { }, default));
		Assert.Equal(new Uri(url), Assert.Single(handler.Uris));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ListsConfiguredIdsAndDiscoversOnlyOnMantle(bool mantle) {
		var handler = new RecordingHandler();
		using var http = new HttpClient(handler);
		List<string> configured = ["configured-id"];
		var backend = new BedrockResponsesBackend(http, new() { RegionEndpoint = Amazon.RegionEndpoint.USEast1,
			DefaultAWSCredentials = new Amazon.Runtime.BasicAWSCredentials("test", "test") }, "us-east-1", mantle, _ => configured);
		Assert.Equal($"{backend.Provider}/configured-id", Assert.Single(await backend.ListModelsAsync(default)));
		Assert.Empty(handler.Uris);
		configured.Clear();
		var ids = await backend.ListModelsAsync(default);
		if(mantle) {
			Assert.Equal("mantle/example-model", Assert.Single(ids));
			Assert.Equal(new Uri("https://bedrock-mantle.us-east-1.api.aws/v1/models"), Assert.Single(handler.Uris));
		} else {
			Assert.Empty(ids);
			Assert.Empty(handler.Uris);
		}
	}
	[Theory]
	[InlineData("bedrock", "mantle")]
	[InlineData("mantle", "openai")]
	public void SendsFlatToolsAndDoesNotReplayOtherProvidersRawItems(string provider, string previous) {
		var history = new Entry[] {
			new() { Kind = EntryKind.Reasoning, Provider = previous, Raw = JsonSerializer.SerializeToElement(new { type = "reasoning" }) },
			new() { Kind = EntryKind.ToolCall, Provider = previous, Namespace = "marten", Name = "read", CallId = "call_1" },
			new() { Kind = EntryKind.ToolResult, CallId = "call_1", Text = "done" },
		};
		var request = new ModelRequest("model", null, "instructions", history, [new("read", "Read", new JsonObject())], "cache");
		var body = BedrockResponsesBackend.BuildBody(request, provider);
		Assert.False(body["store"]!.GetValue<bool>());
		Assert.Equal("function", body["tools"]![0]!["type"]!.GetValue<string>());
		var input = body["input"]!.AsArray();
		Assert.Equal(2, input.Count);
		Assert.Null(input[0]!["namespace"]);
		Assert.Equal("call_1", input[1]!["call_id"]!.GetValue<string>());
	}
}
