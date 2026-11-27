using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Marten.Models;

internal enum JevErrorKind { InvalidInput, Authentication, Forbidden, Redirect, RateLimit, Unavailable, Deadline, Cancelled, Transport, Protocol }

// Never retain the provider's exception, error body, or arbitrary headers (they can echo input).
internal sealed class JevException(JevErrorKind kind, HttpStatusCode? status = null, TimeSpan? retryAfter = null)
	: Exception($"Jev request failed ({kind}).") {
	public JevErrorKind Kind { get; } = kind;
	public HttpStatusCode? Status { get; } = status;
	public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal abstract record JevAnswer;
internal sealed record JevChoiceAnswer(string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities) : JevAnswer;
internal sealed record JevNoulAnswer(double ProbabilityYes) : JevAnswer;
internal sealed record JevResult(string Model, JevAnswer Answer, int InputTokens, int OutputTokens);

/// <summary>Prepared single-question payload. Holds no key, file path, destination, or caller-defined question ID.</summary>
internal sealed class JevRequest {
	internal const string QuestionId = "q1";
	const string ImagePart = "image1";
	internal const int MaxStateBytes = 12_000;
	internal const int MaxWireBytes = JevPreviewImage.MaxBytes + 32_768;
	static readonly System.Text.UTF8Encoding Utf8 = new(false, true);
	static readonly IReadOnlyDictionary<string, string> DecisionCriteria = new Dictionary<string, string>(StringComparer.Ordinal) {
		["yes"] = "The supplied evidence establishes yes under the question's criterion.",
		["no"] = "The supplied evidence establishes no under the question's criterion.",
		["need more information"] = "A specific obtainable fact or clarification could resolve the question, but is missing.",
		["undecidable"] = "There is no identifiable way to decide under the stated criterion and authorized scope. Lack of confidence alone is not enough."
	};
	readonly byte[] json;
	readonly JevPreviewImage? image;
	internal IReadOnlyList<string>? Labels { get; }
	internal bool HasImage => image != null;

	JevRequest(string state, string instructions, IReadOnlyDictionary<string, string>? criteria, string? yes, string? no, JevPreviewImage? image) {
		CheckText(state, MaxStateBytes, allowEmpty: image != null);
		CheckText(instructions, 2_000);
		this.image = image;
		Dictionary<string, string>? options = null;
		if(criteria != null) {
			options = new(StringComparer.Ordinal);
			foreach(var (label, description) in criteria) {
				if(options.Count == 8) throw Invalid();
				CheckText(label, 64);
				CheckText(description, 1_024);
				if(label.Any(char.IsControl) || label.Trim() != label || !options.TryAdd(label, description)) throw Invalid();
			}
			if(options.Count < 2) throw Invalid();
			Labels = Array.AsReadOnly(options.Keys.ToArray());
		} else {
			if((yes == null) != (no == null)) throw Invalid();
			if(yes != null) { CheckText(yes, 1_024); CheckText(no, 1_024); }
		}
		var question = new Dictionary<string, object> { ["type"] = options != null ? "choice" : "noul", ["instructions"] = instructions };
		if(options != null) question["criteria"] = options;
		else if(yes != null) question["criteria"] = new Dictionary<string, string> { ["true"] = yes, ["false"] = no! };
		var body = new Dictionary<string, object> {
			["model"] = JevClient.Model, ["state"] = state, ["questions"] = new Dictionary<string, object> { [QuestionId] = question }
		};
		if(image != null) {
			// Provider expands placeholder strings even inside text; use an opaque host-only ID, exactly once.
			var reference = $"marten-image-{Guid.NewGuid():N}";
			body["state"] = new { text = state, image = reference };
			body["assets"] = new Dictionary<string, object> { [reference] = new { type = "image", part = ImagePart } };
		}
		json = JsonSerializer.SerializeToUtf8Bytes(body);
		if(json.Length > MaxWireBytes - JevPreviewImage.MaxBytes - 512) throw Invalid();
	}

	public static JevRequest Choice(string state, string instructions, IReadOnlyDictionary<string, string> criteria, JevPreviewImage? image = null) =>
		new(state, instructions, criteria ?? throw Invalid(), null, null, image);

	public static JevRequest Decide(string state, string instructions, JevPreviewImage? image = null) =>
		Choice(state, instructions, DecisionCriteria, image);

	public static JevRequest Noul(string state, string instructions, string? yes = null, string? no = null, JevPreviewImage? image = null) =>
		new(state, instructions, null, yes, no, image);

	/// <summary>Copy the prepared JSON for broker review; image bytes are held by the staged image.</summary>
	public byte[] CopyJsonBytes() => (byte[])json.Clone();

	internal HttpContent CreateContent() {
		var body = new ByteArrayContent(json);
		body.Headers.ContentType = new("application/json");
		if(image == null) return body;
		var multipart = new MultipartFormDataContent();
		multipart.Add(body, "request");
		multipart.Add(image.CreateContent(), ImagePart, "image.png");
		return multipart;
	}

	static void CheckText(string? text, int maxBytes, bool allowEmpty = false) {
		if(text == null || text.Length > maxBytes || (!allowEmpty && string.IsNullOrWhiteSpace(text))) throw Invalid();
		try { if(Utf8.GetByteCount(text) > maxBytes) throw Invalid(); }
		catch(System.Text.EncoderFallbackException) { throw Invalid(); }
	}

	internal static JevException Invalid() => new(JevErrorKind.InvalidInput);
}

/// <summary>Direct fixed-origin TypeSafe API. No broker, consent, or runtime registration here.</summary>
internal sealed class JevClient {
	public const string Model = "jev-1.14.0";
	internal const int MaxResponseBytes = 16_384;
	static readonly Uri Endpoint = new("https://api.typesafe.ai/v1/systemone");
	readonly Func<HttpMessageHandler> transport;
	readonly TimeSpan deadline;

	public JevClient() : this(() => CreateTransport(), TimeSpan.FromSeconds(20)) { }
	// Tests supply a fresh fake per call; no injectable URI for any caller.
	internal JevClient(Func<HttpMessageHandler> transport, TimeSpan deadline) {
		this.transport = transport;
		this.deadline = deadline;
	}

	internal static SocketsHttpHandler CreateTransport(Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connect = null) {
		int opened = 0;
		return new() {
			AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
			AutomaticDecompression = DecompressionMethods.None, ActivityHeadersPropagator = null,
			MaxResponseHeadersLength = 16, MaxResponseDrainSize = 0,
			ConnectCallback = (context, ct) => {
				// One fresh HTTP/1.1 connection per call, even if SocketsHttpHandler attempts an internal reconnect.
				if(Interlocked.Exchange(ref opened, 1) != 0) throw new IOException("Jev transport is single-use.");
				return connect != null ? connect(context, ct) : Connect(context, ct);
			}
		};
	}

	static async ValueTask<Stream> Connect(SocketsHttpConnectionContext ctx, CancellationToken ct) {
		var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
		try {
			await socket.ConnectAsync(ctx.DnsEndPoint, ct).ConfigureAwait(false);
			return new NetworkStream(socket, ownsSocket: true);
		} catch {
			socket.Dispose();
			throw;
		}
	}

	public async Task<JevResult> EvaluateAsync(JevRequest request, string apiKey, CancellationToken ct = default) {
		if(request == null) throw JevRequest.Invalid();
		if(!ValidKey(apiKey)) throw new JevException(JevErrorKind.Authentication);
		using var expiration = CancellationTokenSource.CreateLinkedTokenSource(ct);
		expiration.CancelAfter(deadline);
		var token = expiration.Token;
		try {
			token.ThrowIfCancellationRequested();
			using var client = new HttpClient(transport()) { Timeout = Timeout.InfiniteTimeSpan };
			using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) {
				Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
				Content = request.CreateContent()
			};
			// Fix the serialized multipart length before any header calculation or transport write.
			if(request.HasImage) await message.Content.LoadIntoBufferAsync(JevRequest.MaxWireBytes, token).ConfigureAwait(false);
			if(message.Content.Headers.ContentLength is not long length || length > JevRequest.MaxWireBytes) throw JevRequest.Invalid();
			message.Headers.Authorization = new("Bearer", apiKey);
			message.Headers.ConnectionClose = true;
			message.Headers.ExpectContinue = false;
			message.Headers.Accept.Add(new("application/json"));
			if(request.HasImage) message.Headers.Add("TypeSafe-Danger-Zone-Images", "true");
			using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
			token.ThrowIfCancellationRequested();
			if(response.StatusCode != HttpStatusCode.OK) throw FromStatus(response);
			if(response.Content.Headers.ContentType?.MediaType is not string type || !type.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
				response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > MaxResponseBytes) throw Protocol();
			await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
			var data = new byte[MaxResponseBytes + 1];
			var count = 0;
			while(count < data.Length) {
				int read = await stream.ReadAsync(data.AsMemory(count), token).ConfigureAwait(false);
				if(read == 0) break;
				count += read;
			}
			token.ThrowIfCancellationRequested();
			if(count > MaxResponseBytes) throw Protocol();
			var result = Parse(data.AsMemory(0, count), request.Labels);
			token.ThrowIfCancellationRequested();
			return result;
		} catch(OperationCanceledException) when(ct.IsCancellationRequested) {
			throw new JevException(JevErrorKind.Cancelled);
		} catch(OperationCanceledException) when(token.IsCancellationRequested) {
			throw new JevException(JevErrorKind.Deadline);
		} catch(Exception e) when(e is IOException or HttpRequestException or OperationCanceledException) {
			throw new JevException(ct.IsCancellationRequested ? JevErrorKind.Cancelled : token.IsCancellationRequested ? JevErrorKind.Deadline : JevErrorKind.Transport);
		}
	}

	static bool ValidKey(string? key) {
		if(key == null || key.Length is < 1 or > 512) return false;
		var value = key.AsSpan().TrimEnd('=');
		if(value.Length == 0) return false;
		foreach(char c in value)
			if(!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '~' or '+' or '/' or '-')) return false;
		return true;
	}

	static JevException FromStatus(HttpResponseMessage response) {
		var status = response.StatusCode;
		var kind = (int)status switch {
			>= 300 and < 400 => JevErrorKind.Redirect,
			400 or 413 or 415 or 422 => JevErrorKind.InvalidInput,
			401 => JevErrorKind.Authentication,
			403 => JevErrorKind.Forbidden,
			429 => JevErrorKind.RateLimit,
			>= 500 and < 600 => JevErrorKind.Unavailable,
			_ => JevErrorKind.Protocol
		};
		TimeSpan? retryAfter = null;
		if(kind is JevErrorKind.RateLimit or JevErrorKind.Unavailable && response.Headers.RetryAfter is { } retry) {
			var delay = retry.Delta ?? (retry.Date - DateTimeOffset.UtcNow);
			if(delay is { TotalSeconds: >= 0 and <= 86_400 }) retryAfter = delay;
		}
		return new(kind, status, retryAfter);
	}

	static JevResult Parse(ReadOnlyMemory<byte> body, IReadOnlyList<string>? labels) {
		using var doc = ParseDocument(body);
		var root = doc.RootElement;
		if(Text(Field(root, "model")) != Model) throw Protocol();
		var answers = Field(root, "answers");
		var answer = Field(answers, JevRequest.QuestionId);
		if(answers.GetPropertyCount() != 1) throw Protocol();
		var type = Text(Field(answer, "type"));
		JevAnswer parsed;
		if(labels == null && type == "noul") parsed = new JevNoulAnswer(Unit(Field(answer, "noul")));
		else if(labels != null && type == "choice") {
			var label = Text(Field(answer, "choice"));
			var probabilities = Field(answer, "probabilities");
			var values = new Dictionary<string, double>(StringComparer.Ordinal);
			foreach(var option in labels) values.Add(option, Unit(Field(probabilities, option)));
			if(!values.TryGetValue(label, out var selected) || probabilities.GetPropertyCount() != labels.Count ||
				values.Values.Any(v => v > selected) || Math.Abs(values.Values.Sum() - 1) > 0.02) throw Protocol();
			parsed = new JevChoiceAnswer(label, Unit(Field(answer, "confidence")), new ReadOnlyDictionary<string, double>(values));
		} else throw Protocol();
		var usage = Field(root, "usage");
		return new(Model, parsed, Tokens(Field(usage, "input_tokens")), Tokens(Field(usage, "output_tokens")));
	}

	static JsonDocument ParseDocument(ReadOnlyMemory<byte> body) {
		try { return JsonDocument.Parse(body, new() { MaxDepth = 12, AllowDuplicateProperties = false }); }
		catch(Exception e) when(e is JsonException or InvalidOperationException) {
			// Duplicate-name validation also decodes names; invalid Unicode escapes can throw InvalidOperationException.
			throw Protocol();
		}
	}

	static JsonElement Field(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) ? value : throw Protocol();
	static string Text(JsonElement field) {
		if(field.ValueKind != JsonValueKind.String) throw Protocol();
		try { return field.GetString()!; }
		catch(InvalidOperationException) { throw Protocol(); } // Invalid Unicode in a JSON string.
	}
	static double Unit(JsonElement field) => field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out double d) && double.IsFinite(d) && d >= 0 && d <= 1 ? d : throw Protocol();
	static int Tokens(JsonElement field) => field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out int n) && n is >= 0 and <= 1_000_000 ? n : throw Protocol();
	static JevException Protocol() => new(JevErrorKind.Protocol);
}
