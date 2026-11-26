using System.Text.Json.Nodes;
using Marten.Models.OpenAI;
using Marten.Storage;

namespace Marten.Tests;

public class ResponsesTests {
	static JsonObject Ev(string json) => (JsonObject) JsonNode.Parse(json)!;

	[Theory]
	[InlineData("""{"model":"server","reasoning":{"effort":"high"},"usage":{"output_tokens_details":{"reasoning_tokens":42}}}""", "server", "high", 42L)]
	[InlineData("""{"usage":{"output_tokens_details":{"reasoning_tokens":0}}}""", null, null, 0L)]
	[InlineData("""{"reasoning":{},"usage":{"output_tokens":12}}""", null, null, null)]
	[InlineData("{}", null, null, null)]
	public async Task ReadResponseReportsOnlyCompletedMetadata(string completed, string? model, string? effort, long? tokens) {
		var sse = "data: {\"type\":\"response.created\",\"response\":{\"model\":\"early\"}}\n\n" +
			$"data: {{\"type\":\"response.completed\",\"response\":{completed}}}\n\n";
		using var response = new HttpResponseMessage { Content = new StringContent(sse) };
		List<ResponseCompletedMetadata> reported = [];
		await ResponsesBackend.ReadResponse(response, new("requested", "low", "", [], [], "key"), _ => { }, default, onCompleted: reported.Add);
		Assert.Equal(new ResponseCompletedMetadata(model, effort, tokens), Assert.Single(reported));
	}

	[Fact]
	public void CollectsItemsEvenWhenCompletedOutputIsEmpty() {
		var s = new ResponsesBackend.ResponseStream();
		Assert.Equal("Hi", s.Feed(Ev("""{"type":"response.output_text.delta","delta":"Hi"}""")));
		s.Feed(Ev("""{"type":"response.output_item.done","item":{"id":"rs_1","type":"reasoning","summary":[],"encrypted_content":"xyz"}}"""));
		s.Feed(Ev("""{"type":"response.output_item.added","item":{"id":"fc_1","type":"function_call","call_id":"c1","name":"read_file","namespace":"marten","arguments":""}}"""));
		s.Feed(Ev("""{"type":"response.function_call_arguments.done","item_id":"fc_1","arguments":"{\"path\":\"a\"}"}"""));
		s.Feed(Ev("""{"type":"response.completed","response":{"status":"completed","output":[],"usage":{"input_tokens":5}}}"""));
		var result = s.Finish("m");
		Assert.Equal([EntryKind.Reasoning, EntryKind.ToolCall], result.Items.Select(i => i.Kind));
		var call = result.Items[1];
		Assert.Equal(("c1", "read_file", "marten", "{\"path\":\"a\"}"), (call.CallId, call.Name, call.Namespace, call.Arguments));
		Assert.NotNull(result.Usage);
	}

	[Fact]
	public void StreamWithoutCompletionIsTransient() {
		var s = new ResponsesBackend.ResponseStream();
		s.Feed(Ev("""{"type":"response.output_text.delta","delta":"Hi"}"""));
		Assert.Throws<Marten.Models.TransientModelException>(() => s.Finish("m"));
	}

	[Theory]
	[InlineData("content_filter")]
	[InlineData("content_policy_violation")]
	public void PolicyBlocksAreRefusalsNotFailures(string code) {
		var s = new ResponsesBackend.ResponseStream();
		var e = Assert.Throws<Marten.Models.ModelRefusedException>(() => s.Feed(new JsonObject {
			["type"] = "response.failed", ["response"] = new JsonObject { ["error"] = new JsonObject { ["code"] = code } },
		}));
		Assert.Equal(code, e.Code);
	}

	[Fact]
	public void AContentFilteredCompletionIsARefusal() {
		var s = new ResponsesBackend.ResponseStream();
		Assert.Throws<Marten.Models.ModelRefusedException>(() => s.Feed(Ev(
			"""{"type":"response.incomplete","response":{"incomplete_details":{"reason":"content_filter"}}}""")));
	}

	[Theory]
	[InlineData(true)]
	// delta: refusal as delta events, or only as the completed item's content part.
	[InlineData(false)]
	public void ARefusalBecomesARefusalWithItsOwnWords(bool delta) {
		var s = new ResponsesBackend.ResponseStream();
		if(delta) s.Feed(Ev("""{"type":"response.refusal.delta","delta":"I can't help with that."}"""));
		s.Feed(Ev(delta
			? """{"type":"response.completed","response":{"output":[]}}"""
			: """{"type":"response.completed","response":{"output":[{"id":"m1","type":"message","role":"assistant","content":[{"type":"refusal","refusal":"I can't help with that."}]}]}}"""));
		var e = Assert.Throws<Marten.Models.ModelRefusedException>(() => s.Finish("m"));
		Assert.Equal("refusal", e.Code);
		Assert.Equal("I can't help with that.", e.Reason);
	}

	[Fact]
	public void QuotaFailuresWaitRatherThanFail() {
		var s = new ResponsesBackend.ResponseStream();
		Assert.Throws<Marten.Models.RateLimitedException>(() =>
			s.Feed(Ev("""{"type":"response.failed","response":{"error":{"code":"subscription_sharing_usage_limit_exceeded"}}}""")));
	}

	[Fact]
	public void OtherProvidersReasoningIsDroppedAndCallsAreRebuilt() {
		var wire = ResponsesBackend.ToWire([
			new Entry { Kind = EntryKind.Message, Text = "hi" },
			new Entry { Kind = EntryKind.Reasoning, Provider = "anthropic", Text = "thinking" },
			new Entry { Kind = EntryKind.ToolCall, Provider = "anthropic", CallId = "c1", Name = "read_file", Arguments = "{}" },
			new Entry { Kind = EntryKind.ToolResult, CallId = "c1", Text = "out" },
			new Entry { Kind = EntryKind.Error, Text = "never sent" },
		]);
		Assert.Equal(["message", "function_call", "function_call_output"], wire.Select(w => w!["type"]!.GetValue<string>()));
	}
}
