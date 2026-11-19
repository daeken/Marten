using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.Runtime.Internal.Util;

namespace Marten.Models.OpenAI;

internal sealed class BedrockResponsesBackend(HttpClient http, AmazonBedrockRuntimeConfig aws, string region, bool mantle,
	Func<string, IReadOnlyList<string>> models, IReadOnlySet<string>? mantleOpenAiRouteModels = null, Action<string>? traceEffort = null) : IModelBackend {
	readonly DefaultAWSCredentialsIdentityResolver credentials = new();
	public string Provider => mantle ? "mantle" : "bedrock";
	public bool Connected => true; // Configured; authorization is checked by AWS on each request.
	string Host => mantle ? $"bedrock-mantle.{region}.api.aws" : $"bedrock-runtime.{region}.amazonaws.com";
	string ResponsesPath(string model) => mantle && mantleOpenAiRouteModels?.Contains(model) != true
		? "/v1/responses" : "/openai/v1/responses";
	// Marten-side budget, not an AWS service quota.
	internal const int MaxRequestBytes = 32_000_000;

	async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[] body, CancellationToken ct) {
		if(method == HttpMethod.Post && body.Length > MaxRequestBytes) throw new ContextFullException(body.Length);
		var endpoint = new Uri($"https://{Host}");
		var signed = new DefaultRequest(new ConverseRequest(), "bedrock") {
			Endpoint = endpoint, ResourcePath = path, HttpMethod = method.Method, Content = body,
		};
		if(method == HttpMethod.Post) signed.Headers["Content-Type"] = "application/json";
		try {
			var source = aws.DefaultAWSCredentials ?? credentials.ResolveIdentity(aws, ct);
			var identity = await source.GetCredentialsAsync().WaitAsync(ct);
			if(identity.UseToken) signed.Headers["X-Amz-Security-Token"] = identity.Token;
			var signature = new AWS4Signer().SignRequest(signed, aws, new RequestMetrics(), identity.AccessKey, identity.SecretKey);
			signed.Headers["Authorization"] = signature.ForAuthorizationHeader;
		} catch(Exception e) when(e is not OperationCanceledException) {
			// Credential-process stderr can contain tokens. Never surface the SDK exception here.
			throw new NotConnectedException("Could not resolve AWS credentials. Check your AWS profile or refresh your AWS login.");
		}
		using var request = new HttpRequestMessage(method, new Uri(endpoint, path));
		if(method == HttpMethod.Post) request.Content = new ByteArrayContent(body);
		foreach(var header in signed.Headers)
			if(!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
				request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
		HttpResponseMessage response;
		try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
		catch(HttpRequestException e) {
			ct.ThrowIfCancellationRequested();
			throw ModelHttp.IsConnectFailure(e) ? new ProviderUnreachableException($"Could not reach {Provider}.", e)
				: new TransientModelException($"{Provider} request failed before receiving response headers.", e);
		}
		if(response.IsSuccessStatusCode) return response;
		using(response) {
			var status = (int) response.StatusCode;
			throw status switch {
				401 or 403 => new NotConnectedException($"AWS denied access to {Provider} in {region}. Check credentials, IAM permissions, and model access."),
				413 => new ContextFullException(),
				429 => new RateLimitedException($"{Provider} throttled the request.", response.Headers.RetryAfter?.Delta),
				>= 500 => new TransientModelException($"{Provider} returned HTTP {status}."),
				_ => new InvalidOperationException($"{Provider} rejected the request (HTTP {status}). Check the model ID and regional availability."),
			};
		}
	}

	public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) {
		var configured = models(Provider);
		if(configured.Count > 0) return configured.Select(m => $"{Provider}/{m}").ToList();
		return mantle ? await DiscoverMantleModelsAsync(ct) : [];
	}

	internal async Task<IReadOnlyList<string>> DiscoverMantleModelsAsync(CancellationToken ct) {
		using var response = await SendAsync(HttpMethod.Get, "/v1/models", [], ct);
		var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["data"]?.AsArray();
		return data?.Select(m => m?["id"]?.GetValue<string>()).OfType<string>().Select(m => $"{Provider}/{m}").ToList() ?? [];
	}

	internal static JsonObject BuildBody(ModelRequest req, string provider) {
		var body = new JsonObject {
			["model"] = req.Model, ["instructions"] = req.Instructions,
			["input"] = ResponsesBackend.ToWire(req.History, provider),
			["store"] = false, ["stream"] = true,
		};
		if(req.Tools.Count > 0) body["tools"] = new JsonArray(req.Tools.Select(t => (JsonNode) new JsonObject {
			["type"] = "function", ["name"] = t.Name, ["description"] = t.Description,
			["parameters"] = t.Parameters.DeepClone(), ["strict"] = t.Strict,
		}).ToArray());
		if(req.Effort != null) body["reasoning"] = new JsonObject { ["effort"] = req.Effort };
		return body;
	}

	// Inspect the exact bytes passed to the signer and transport. Skip input, tools, and nested lookalikes.
	internal static string? ReadWireEffort(ReadOnlySpan<byte> body) {
		var reader = new Utf8JsonReader(body);
		reader.Read(); // root object produced by BuildBody
		while(reader.Read() && reader.TokenType == JsonTokenType.PropertyName) {
			var reasoning = reader.ValueTextEquals("reasoning"u8);
			reader.Read();
			if(reasoning && reader.TokenType == JsonTokenType.StartObject) {
				while(reader.Read() && reader.TokenType == JsonTokenType.PropertyName) {
					var effort = reader.ValueTextEquals("effort"u8);
					reader.Read();
					if(effort && reader.TokenType == JsonTokenType.String) return reader.GetString();
					reader.Skip();
				}
			} else reader.Skip();
		}
		return null;
	}

	// Keep arbitrary strings from config or a response out of diagnostics.
	static string? EffortLabel(string? effort) => effort switch {
		null or "none" or "minimal" or "low" or "medium" or "high" or "xhigh" => effort,
		_ => "other",
	};
	public async Task<ModelResult> CompleteAsync(ModelRequest req, Action<string> onTextDelta, CancellationToken ct) {
		var body = JsonSerializer.SerializeToUtf8Bytes(BuildBody(req, Provider));
		Action<ResponseCompletedMetadata>? onCompleted = null;
		if(traceEffort != null && body.Length <= MaxRequestBytes) {
			var call = Guid.NewGuid().ToString("N");
			var wire = ReadWireEffort(body);
			traceEffort(JsonSerializer.Serialize(new { kind = "bedrock-effort", call, phase = "request",
				source = req.EffortSource is "agent" or "global" or "provider-default" ? req.EffortSource : null,
				selected = EffortLabel(req.Effort), wire = EffortLabel(wire), selectedMatchesWire = req.Effort == wire }));
			onCompleted = meta => traceEffort(JsonSerializer.Serialize(new { kind = "bedrock-effort", call, phase = "completed",
				server = EffortLabel(meta.ReasoningEffort), wireMatchesServer = meta.ReasoningEffort == null ? (bool?) null : wire == meta.ReasoningEffort,
				reasoningTokens = meta.ReasoningTokens }));
		}
		using var response = await SendAsync(HttpMethod.Post, ResponsesPath(req.Model), body, ct);
		return await ResponsesBackend.ReadResponse(response, req, onTextDelta, ct, Provider, onCompleted);
	}
}
