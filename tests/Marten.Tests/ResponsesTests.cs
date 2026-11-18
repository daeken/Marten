using System.Text.Json.Nodes;
using Marten.Models.OpenAI;
using Marten.Storage;

namespace Marten.Tests;

public class ResponsesTests {
	static JsonObject Ev(string json) => (JsonObject) JsonNode.Parse(json)!;

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
