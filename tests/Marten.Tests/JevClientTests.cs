using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Marten.Models;
using Xunit;

namespace Marten.Tests;

public class JevClientTests {
	const string FixtureKey = "synthetic-fixture-token";
	const string NoulJson = """{"model":"jev-1.14.0","answers":{"q1":{"type":"noul","noul":0.72}},"usage":{"input_tokens":100,"output_tokens":4}}""";
	static JevRequest Noul() => JevRequest.Noul("Local context", "Meets the criterion?");

	[Fact]
	public async Task NoulUsesFixedWireContract() {
		var fake = new Fake(async (message, ct) => {
			Assert.Equal("https://api.typesafe.ai/v1/systemone", message.RequestUri!.AbsoluteUri);
			Assert.Equal(HttpMethod.Post, message.Method);
			Assert.Equal(HttpVersion.Version11, message.Version);
			Assert.Equal(HttpVersionPolicy.RequestVersionExact, message.VersionPolicy);
			Assert.True(message.Headers.ConnectionClose);
			Assert.False(message.Headers.ExpectContinue);
			Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
			Assert.Equal(FixtureKey, message.Headers.Authorization.Parameter);
			Assert.False(message.Headers.Contains("TypeSafe-Danger-Zone-Images"));
			Assert.False(message.Headers.Contains("TypeSafe-Danger-Zone-Long-Context"));
			Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
			var json = await message.Content.ReadAsStringAsync(ct);
			Assert.DoesNotContain(FixtureKey, json);
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			Assert.Equal("jev-1.14.0", root.GetProperty("model").GetString());
			Assert.Equal("Current text", root.GetProperty("state").GetString());
			Assert.False(root.TryGetProperty("assets", out _));
			var question = root.GetProperty("questions").GetProperty("q1");
			Assert.Equal("noul", question.GetProperty("type").GetString());
			Assert.Equal("Is it complete?", question.GetProperty("instructions").GetString());
			Assert.Equal("All fields", question.GetProperty("criteria").GetProperty("true").GetString());
			Assert.Equal("Missing fields", question.GetProperty("criteria").GetProperty("false").GetString());
			return Json(NoulJson);
		});
		var result = await Client(fake).EvaluateAsync(JevRequest.Noul("Current text", "Is it complete?", "All fields", "Missing fields"), FixtureKey);
		Assert.Equal(0.72, Assert.IsType<JevNoulAnswer>(result.Answer).ProbabilityYes);
		Assert.Equal((100, 4), (result.InputTokens, result.OutputTokens));
		Assert.Equal(1, fake.Calls);
	}

	[Theory]
	[InlineData("yes")]
	[InlineData("no")]
	[InlineData("need more information")]
	[InlineData("undecidable")]
	public async Task FourExactDecisionLabelsAreTypedResults(string label) {
		string[] expected = ["yes", "no", "need more information", "undecidable"];
		var fake = new Fake(async (message, ct) => {
			using var doc = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct));
			var question = doc.RootElement.GetProperty("questions").GetProperty("q1");
			Assert.Equal("choice", question.GetProperty("type").GetString());
			var criteria = question.GetProperty("criteria").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
			Assert.Equal(expected, criteria.Keys);
			Assert.Equal(4, criteria.Values.Distinct().Count());
			var answer = new { type = "choice", choice = label, confidence = 0.02, probabilities = expected.ToDictionary(x => x, x => x == label ? 0.4 : 0.2) };
			return Json(JsonSerializer.Serialize(new { model = JevClient.Model, answers = new { q1 = answer }, usage = new { input_tokens = 123, output_tokens = 10 } }));
		});
		var result = await Client(fake).EvaluateAsync(JevRequest.Decide("facts", "Is the item ready?"), FixtureKey);
		var decision = Assert.IsType<JevChoiceAnswer>(result.Answer);
		Assert.Equal(label, decision.Choice);
		Assert.Equal(0.02, decision.Confidence); // Confidence is not a remapping rule.
		Assert.Equal(0.4, decision.Probabilities[label]);
	}

	[Fact]
	public async Task ChoiceSnapshotsValidatedOptions() {
		var options = new Dictionary<string, string> { ["first"] = "The first outcome", ["second"] = "The second outcome" };
		var request = JevRequest.Choice("facts", "Pick one", options);
		options.Clear();
		var fake = new Fake(async (message, ct) => {
			using var wire = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct));
			Assert.Equal(2, wire.RootElement.GetProperty("questions").GetProperty("q1").GetProperty("criteria").GetPropertyCount());
			return Json(ChoiceJson());
		});
		Assert.Equal("first", Assert.IsType<JevChoiceAnswer>((await Client(fake).EvaluateAsync(request, FixtureKey)).Answer).Choice);
	}

	[Fact]
	public async Task InvalidInputsAndKeyNeverDispatch() {
		var fake = new Fake((_, _) => throw new Exception("unexpected dispatch"));
		Assert.Equal(JevErrorKind.Authentication, (await Assert.ThrowsAsync<JevException>(() => Client(fake).EvaluateAsync(Noul(), "bad\r\nAuthorization: fake"))).Kind);
		Assert.Equal(JevErrorKind.InvalidInput, Assert.Throws<JevException>(() => JevRequest.Noul("facts", "q", yes: "yes only")).Kind);
		Assert.Throws<JevException>(() => JevRequest.Choice("facts", "q", new Dictionary<string, string> { ["one"] = "one option" }));
		Assert.Throws<JevException>(() => JevRequest.Choice("facts", "q", new Dictionary<string, string> { ["one\n"] = "bad label", ["two"] = "ok" }));
		Assert.Throws<JevException>(() => JevRequest.Noul(new string('é', 6_001), "q"));
		Assert.Throws<JevException>(() => JevRequest.Noul("\uD800", "q"));
		Assert.Equal(0, fake.Calls);
	}

	[Theory]
	[InlineData(307, "Redirect")]
	[InlineData(308, "Redirect")]
	[InlineData(400, "InvalidInput")]
	[InlineData(401, "Authentication")]
	[InlineData(403, "Forbidden")]
	[InlineData(413, "InvalidInput")]
	[InlineData(415, "InvalidInput")]
	[InlineData(422, "InvalidInput")]
	[InlineData(429, "RateLimit")]
	[InlineData(529, "Unavailable")]
	[InlineData(201, "Protocol")]
	public async Task StatusIsSanitizedAndNeverRetried(int status, string category) {
		var kind = Enum.Parse<JevErrorKind>(category);
		var fake = new Fake((_, _) => {
			var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new UnreadableContent() };
			response.Headers.Location = new("https://redirect.invalid/" + FixtureKey);
			response.Headers.RetryAfter = new(TimeSpan.FromSeconds(2));
			return Task.FromResult(response);
		});
		var error = await Assert.ThrowsAsync<JevException>(() => Client(fake).EvaluateAsync(Noul(), FixtureKey));
		AssertError(error, kind);
		Assert.Equal((HttpStatusCode)status, error.Status);
		Assert.Equal(kind is JevErrorKind.RateLimit or JevErrorKind.Unavailable ? TimeSpan.FromSeconds(2) : (TimeSpan?)null, error.RetryAfter);
		Assert.Equal(1, fake.Calls);
	}

	[Theory]
	[InlineData("1e400")]
	[InlineData("-0.1")]
	[InlineData("1.001")]
	[InlineData("null")]
	[InlineData("\"0.5\"")]
	public async Task MalformedNoulProbabilityIsProtocolError(string value) =>
		AssertError(await Assert.ThrowsAsync<JevException>(() => Client(Reply(NoulJson.Replace("0.72", value))).EvaluateAsync(Noul(), FixtureKey)), JevErrorKind.Protocol);

	[Theory]
	[InlineData("invented", "0.5", "{\"first\":0.7,\"second\":0.3}")]
	[InlineData("first", "0.5", "{\"first\":0.1,\"second\":0.9}")]
	[InlineData("first", "0.5", "{\"first\":0.7,\"second\":0.1}")]
	[InlineData("first", "0.5", "{\"first\":0.7,\"second\":0.3,\"extra\":0}")]
	[InlineData("first", "0.5", "{\"first\":0.7,\"second\":0.3,\"first\":0.7}")]
	[InlineData("first", "0.5", "{\"first\":0.7}")]
	[InlineData("first", "1e500", "{\"first\":0.7,\"second\":0.3}")]
	[InlineData("\\uD800", "0.5", "{\"first\":0.7,\"second\":0.3}")]
	public async Task MalformedChoiceIsProtocolError(string choice, string confidence, string probabilities) {
		var request = JevRequest.Choice("facts", "select", new Dictionary<string, string> { ["first"] = "one", ["second"] = "two" });
		AssertError(await Assert.ThrowsAsync<JevException>(() => Client(Reply(ChoiceJson(choice, confidence, probabilities))).EvaluateAsync(request, FixtureKey)), JevErrorKind.Protocol);
	}

	[Theory]
	[InlineData("jev-latest", "q1", "100")]
	[InlineData("jev-1.14.0", "caller-picked-id", "100")]
	[InlineData("jev-1.14.0", "q1", "-1")]
	[InlineData("jev-1.14.0", "q1", "1000001")]
	public async Task MalformedEnvelopeIsProtocolError(string model, string id, string tokens) {
		var json = NoulJson.Replace("jev-1.14.0", model).Replace("q1", id).Replace("100", tokens);
		AssertError(await Assert.ThrowsAsync<JevException>(() => Client(Reply(json)).EvaluateAsync(Noul(), FixtureKey)), JevErrorKind.Protocol);
	}

	[Fact]
	public async Task MalformedEscapedPropertyNameIsSanitizedProtocolError() {
		const string marker = "provider-supplied-marker";
		var json = NoulJson.Insert(1, $"\"{marker}\\uD800\":true,");
		var error = await Assert.ThrowsAsync<JevException>(() => Client(Reply(json)).EvaluateAsync(Noul(), FixtureKey));
		AssertError(error, JevErrorKind.Protocol);
		Assert.Equal("Jev request failed (Protocol).", error.Message);
		Assert.DoesNotContain(marker, error.ToString());
	}

	[Fact]
	public async Task ResponseBoundAppliesWithoutContentLength() {
		var body = new SizedStream(JevClient.MaxResponseBytes + 20_000);
		var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
		response.Content.Headers.ContentType = new("application/json");
		var error = await Assert.ThrowsAsync<JevException>(() => Client(new Fake((_, _) => Task.FromResult(response))).EvaluateAsync(Noul(), FixtureKey));
		AssertError(error, JevErrorKind.Protocol);
		Assert.Equal(JevClient.MaxResponseBytes + 1, body.BytesRead);
	}

	[Fact]
	public async Task TransportFailureIsSanitizedAndNotRetried() {
		var fake = new Fake((_, _) => throw new HttpRequestException(FixtureKey));
		AssertError(await Assert.ThrowsAsync<JevException>(() => Client(fake).EvaluateAsync(Noul(), FixtureKey)), JevErrorKind.Transport);
		Assert.Equal(1, fake.Calls);
	}

	[Fact]
	public async Task DeadlineDuringBodyAndCallerCancellationAreDistinct() {
		var fake = new Fake((_, _) => {
			var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SizedStream(0, stall: true)) };
			response.Content.Headers.ContentType = new("application/json");
			return Task.FromResult(response);
		});
		AssertError(await Assert.ThrowsAsync<JevException>(() => new JevClient(() => fake, TimeSpan.FromMilliseconds(100)).EvaluateAsync(Noul(), FixtureKey)), JevErrorKind.Deadline);
		Assert.Equal(1, fake.Calls);
		using var ct = new CancellationTokenSource();
		ct.Cancel();
		var notSent = Reply(NoulJson);
		AssertError(await Assert.ThrowsAsync<JevException>(() => Client(notSent).EvaluateAsync(Noul(), FixtureKey, ct.Token)), JevErrorKind.Cancelled);
		Assert.Equal(0, notSent.Calls);
	}

	[Fact]
	public async Task ProductionTransportForbidsRedirectProxyAndSecondPhysicalConnection() {
		int opened = 0;
		using var handler = JevClient.CreateTransport((_, _) => { opened++; return ValueTask.FromResult<Stream>(Stream.Null); });
		Assert.False(handler.AllowAutoRedirect);
		Assert.False(handler.UseProxy);
		Assert.False(handler.UseCookies);
		Assert.Null(handler.ActivityHeadersPropagator);
		await handler.ConnectCallback!(null!, default);
		await Assert.ThrowsAsync<IOException>(async () => await handler.ConnectCallback!(null!, default));
		Assert.Equal(1, opened);
	}

	[Fact]
	public async Task PreviewPngIsOwnedAndSentAsDocumentedMultipart() {
		var source = Png();
		var image = JevPreviewImage.Stage(source, allowPreview: true);
		Array.Fill(source, (byte)0);
		var bytes = image.CopyPngBytes();
		Array.Fill(image.CopyPngBytes(), (byte)1);
		var fake = new Fake(async (message, ct) => {
			Assert.Equal("true", Assert.Single(message.Headers.GetValues("TypeSafe-Danger-Zone-Images")));
			Assert.False(message.Headers.Contains("TypeSafe-Danger-Zone-Long-Context"));
			var multipart = Assert.IsType<MultipartFormDataContent>(message.Content);
			var parts = multipart.ToArray();
			Assert.Equal(2, parts.Length);
			Assert.Equal("request", parts[0].Headers.ContentDisposition!.Name!.Trim('"'));
			Assert.Equal("application/json", parts[0].Headers.ContentType!.MediaType);
			Assert.Equal("image/png", parts[1].Headers.ContentType!.MediaType);
			Assert.Equal(bytes, await parts[1].ReadAsByteArrayAsync(ct));
			var json = await parts[0].ReadAsStringAsync(ct);
			using var doc = JsonDocument.Parse(json);
			var placeholder = doc.RootElement.GetProperty("state").GetProperty("image").GetString()!;
			Assert.Equal(2, json.Split(placeholder, StringSplitOptions.None).Length - 1); // one assets-map key, one content reference
			var asset = doc.RootElement.GetProperty("assets").GetProperty(placeholder);
			Assert.Equal("image", asset.GetProperty("type").GetString());
			Assert.Equal(asset.GetProperty("part").GetString(), parts[1].Headers.ContentDisposition!.Name!.Trim('"'));
			var wire = await multipart.ReadAsByteArrayAsync(ct);
			Assert.Equal(multipart.Headers.ContentLength, wire.Length);
			return Json(NoulJson);
		});
		await Client(fake).EvaluateAsync(JevRequest.Noul("", "See the image?", image: image), FixtureKey);
		Assert.Equal(1, fake.Calls);
	}

	[Fact]
	public void ImageStageRejectsOptOutMetadataAnimationBoundsAndMalformedData() {
		Assert.Throws<JevException>(() => JevPreviewImage.Stage(Png(), allowPreview: false));
		Assert.Throws<JevException>(() => JevPreviewImage.Stage(Png(279), allowPreview: true));
		Assert.Throws<JevException>(() => JevPreviewImage.Stage(Png(1_025), allowPreview: true));
		Assert.Throws<JevException>(() => JevPreviewImage.Stage(Png(ancillary: "acTL"), allowPreview: true));
		Assert.Throws<JevException>(() => JevPreviewImage.Stage(Png(ancillary: "tEXt"), allowPreview: true));
		Assert.Throws<JevException>(() => JevPreviewImage.Stage(new byte[JevPreviewImage.MaxBytes + 1], allowPreview: true));
		Assert.Throws<JevException>(() => JevPreviewImage.Stage(Png(extraRow: true), allowPreview: true));
		var corrupted = Png();
		corrupted[^1] ^= 1;
		Assert.Throws<JevException>(() => JevPreviewImage.Stage(corrupted, allowPreview: true));
	}

	static JevClient Client(Fake fake) => new(() => fake, TimeSpan.FromSeconds(5));
	static Fake Reply(string json) => new((_, _) => Task.FromResult(Json(json)));
	static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
	static void AssertError(JevException error, JevErrorKind kind) {
		Assert.Equal(kind, error.Kind);
		Assert.Null(error.InnerException);
		Assert.DoesNotContain(FixtureKey, error.ToString());
	}
	static string ChoiceJson(string label = "first", string confidence = "0.5", string probabilities = "{\"first\":0.7,\"second\":0.3}") =>
		$$$"""{"model":"jev-1.14.0","answers":{"q1":{"type":"choice","choice":"{{{label}}}","confidence":{{{confidence}}},"probabilities":{{{probabilities}}} }},"usage":{"input_tokens":10,"output_tokens":4}}""";

	sealed class Fake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler {
		public int Calls { get; private set; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
			Calls++;
			return send(request, ct);
		}
	}

	sealed class UnreadableContent : HttpContent {
		protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Do not read provider error bodies.");
		protected override bool TryComputeLength(out long length) { length = 0; return false; }
	}

	sealed class SizedStream(int size, bool stall = false) : Stream {
		public int BytesRead { get; private set; }
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
			if(stall) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			int n = Math.Min(size - BytesRead, buffer.Length);
			buffer.Span[..n].Clear();
			BytesRead += n;
			return n;
		}
		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	static byte[] Png(int width = 280, string? ancillary = null, bool extraRow = false) {
		using var png = new MemoryStream();
		png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
		byte[] header = new byte[13];
		BinaryPrimitives.WriteInt32BigEndian(header, width);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 280);
		header[8] = 8;
		header[9] = 2;
		Chunk(png, "IHDR", header);
		if(ancillary != null) Chunk(png, ancillary, [1, 2, 3, 4]);
		using var compressed = new MemoryStream();
		using(var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) {
			var row = new byte[1 + width * 3];
			for(var y = 0; y < (extraRow ? 281 : 280); y++) zlib.Write(row);
		}
		Chunk(png, "IDAT", compressed.ToArray());
		Chunk(png, "IEND", []);
		return png.ToArray();
	}

	static void Chunk(Stream png, string kind, byte[] data) {
		Span<byte> n = stackalloc byte[4];
		BinaryPrimitives.WriteInt32BigEndian(n, data.Length);
		png.Write(n);
		var type = Encoding.ASCII.GetBytes(kind);
		png.Write(type);
		png.Write(data);
		uint crc = uint.MaxValue;
		foreach(byte b in type.Concat(data)) {
			crc ^= b;
			for(var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
		}
		BinaryPrimitives.WriteUInt32BigEndian(n, ~crc);
		png.Write(n);
	}
}
