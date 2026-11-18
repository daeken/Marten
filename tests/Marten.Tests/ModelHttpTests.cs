using System.Net;
using System.Net.Sockets;
using System.Text;
using Marten.Models;
using Marten.Models.Anthropic;
using Marten.Models.OpenAI;
using Marten.Storage;
using Xunit;

namespace Marten.Tests;

public sealed class ModelHttpTests {
	static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
	static readonly ModelRequest Request = new("test-model", null, "test", [new Entry { Kind = EntryKind.Message, Text = "hi" }], [], "test");
	const string ResponsesDelta = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hi\"}\n\n";
	const string MessagesDelta = "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"hi\"}}\n\n";

	[Theory]
	[InlineData("responses")]
	[InlineData("anthropic")]
	[InlineData("zai")]
	[InlineData("deepseek")]
	public async Task AStallInsideAnSseLineIsTransient(string provider) {
		using var dir = new TempDir();
		await using var server = new Loopback(async (res, ct) => {
			await Write(res, "data: {", ct);
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
		});
		using var http = server.Client(Guard, TimeSpan.FromMilliseconds(300));
		var model = Backend(provider, dir, server, http);
		var error = await Assert.ThrowsAsync<TransientModelException>(() => model.CompleteAsync(Request, _ => { }, default).WaitAsync(Guard));
		Assert.Contains("waiting for bytes", error.Message);
	}

	[Fact]
	public async Task AConnectDeadlineIsTransient() {
		using var http = ModelHttp.CreateClient(new SocketsHttpHandler {
			UseProxy = false, ConnectTimeout = TimeSpan.FromMilliseconds(300),
			ConnectCallback = async (_, ct) => {
				await Task.Delay(Timeout.InfiniteTimeSpan, ct);
				return Stream.Null;
			},
		}, Guard, () => Guard);
		var error = await Assert.ThrowsAsync<TransientModelException>(() => http.GetAsync("http://127.0.0.1").WaitAsync(Guard));
		Assert.Contains("connecting", error.Message);
	}

	[Theory]
	[InlineData("responses")]
	[InlineData("anthropic")]
	public async Task MissingHeadersAreTransient(string provider) {
		using var dir = new TempDir();
		await using var server = new Loopback((_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
		using var http = server.Client(TimeSpan.FromMilliseconds(300), Guard);
		var model = Backend(provider, dir, server, http);
		var error = await Assert.ThrowsAsync<TransientModelException>(() => model.CompleteAsync(Request, _ => { }, default).WaitAsync(Guard));
		Assert.Contains("response headers", error.Message);
	}

	[Theory]
	[InlineData("responses")]
	[InlineData("anthropic")]
	public async Task BytesWithoutEventsKeepTheStreamAlivePastBothDeadlines(string provider) {
		using var dir = new TempDir();
		await using var server = new Loopback(async (res, ct) => {
			await Write(res, ":", ct); // An SSE comment, with no complete line/event until the end.
			for(var i = 0; i < 9; i++) {
				await Task.Delay(150, ct);
				await Write(res, "p", ct);
			}
			await Write(res, provider == "responses"
				? "\n\ndata: {\"type\":\"response.completed\",\"response\":{}}\n\n"
				: "\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", ct);
		});
		using var http = server.Client(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(750));
		var model = Backend(provider, dir, server, http);
		await model.CompleteAsync(Request, _ => { }, default).WaitAsync(Guard);
	}

	[Theory]
	[InlineData("responses")]
	[InlineData("anthropic")]
	public async Task CancelWhileWaitingForHeadersRemainsCancellation(string provider) {
		using var dir = new TempDir();
		var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var server = new Loopback((_, ct) => {
			waiting.SetResult();
			return Task.Delay(Timeout.InfiniteTimeSpan, ct);
		});
		using var http = server.Client(Guard, Guard);
		using var stop = new CancellationTokenSource();
		var model = Backend(provider, dir, server, http);
		var completion = model.CompleteAsync(Request, _ => { }, stop.Token);
		await (await Task.WhenAny(waiting.Task, completion).WaitAsync(Guard));
		await stop.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(Guard));
	}

	[Theory]
	[InlineData("responses")]
	[InlineData("anthropic")]
	public async Task CancelAfterDeltaRemainsCancellation(string provider) {
		using var dir = new TempDir();
		await using var server = new Loopback(async (res, ct) => {
			await Write(res, provider == "responses" ? ResponsesDelta : MessagesDelta, ct);
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
		});
		using var http = server.Client(Guard, Guard);
		using var stop = new CancellationTokenSource();
		var model = Backend(provider, dir, server, http);
		var delta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var completion = model.CompleteAsync(Request, _ => delta.TrySetResult(), stop.Token);
		await (await Task.WhenAny(delta.Task, completion).WaitAsync(Guard));
		Assert.True(delta.Task.IsCompletedSuccessfully, "The model finished without emitting a delta.");
		await stop.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(Guard));
	}

	[Theory]
	[InlineData("responses")]
	[InlineData("anthropic")]
	public async Task ErrorBodiesCannotHideIdleTimeouts(string provider) {
		using var dir = new TempDir();
		await using var server = new Loopback(async (res, ct) => {
			res.StatusCode = 400;
			res.ContentType = "application/json";
			await Write(res, "{", ct);
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
		});
		using var http = server.Client(Guard, TimeSpan.FromMilliseconds(300));
		var model = Backend(provider, dir, server, http);
		var error = await Assert.ThrowsAsync<TransientModelException>(() => model.CompleteAsync(Request, _ => { }, default).WaitAsync(Guard));
		Assert.Contains("waiting for bytes", error.Message);
	}

	[Fact]
	public async Task StalledRefreshBodyIsTransientBeforeInference() {
		using var dir = new TempDir();
		await using var server = new Loopback(async (res, ct) => {
			res.ContentType = "application/json";
			await Write(res, "{", ct);
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
		});
		using var authHttp = new HttpClient(new RedirectToLoopback(server.Url)) { Timeout = TimeSpan.FromSeconds(1) };
		using var modelHttp = new HttpClient(new FakeAuthHandler((_, _, _) => throw new Exception("Inference should not start.")));
		var model = new ResponsesBackend(ExpiredAuth(dir, authHttp), modelHttp);
		var error = await Assert.ThrowsAsync<TransientModelException>(() => model.CompleteAsync(Request, _ => { }, default).WaitAsync(Guard));
		Assert.Contains("waiting for ChatGPT authorization", error.Message);
	}

	[Fact]
	public async Task CancelingRefreshRemainsCancellationAndReleasesTheGate() {
		using var dir = new TempDir();
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var http = new HttpClient(new FakeAuthHandler(async (attempt, _, ct) => {
			if(attempt == 1) {
				started.SetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			}
			return new HttpResponseMessage(HttpStatusCode.OK) {
				Content = new StringContent("""{"access_token":"new-test-token","expires_in":3600,"token_type":"bearer"}"""),
			};
		})) { Timeout = Timeout.InfiniteTimeSpan };
		using var stop = new CancellationTokenSource();
		var auth = ExpiredAuth(dir, http);
		var canceled = auth.TokenAsync(false, stop.Token);
		await (await Task.WhenAny(started.Task, canceled).WaitAsync(Guard));
		var queued = auth.TokenAsync(false, default);
		await stop.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(Guard));
		Assert.Equal("new-test-token", await queued.WaitAsync(Guard));
	}

	[Theory]
	[InlineData(0, typeof(TransientModelException))]
	[InlineData(503, typeof(TransientModelException))]
	[InlineData(401, typeof(NotConnectedException))]
	public async Task RefreshDistinguishesTransportFailureAndCredentialRejection(int status, Type expected) {
		using var dir = new TempDir();
		using var http = new HttpClient(new FakeAuthHandler((_, _, _) => status == 0
			? throw new HttpRequestException("test network failure") : Task.FromResult(new HttpResponseMessage((HttpStatusCode) status))));
		var auth = ExpiredAuth(dir, http);
		var error = await Assert.ThrowsAnyAsync<Exception>(() => auth.TokenAsync(false, default).WaitAsync(Guard));
		Assert.IsType(expected, error);
	}

	static ChatGptAuth ExpiredAuth(TempDir dir, HttpClient http) {
		var home = new Home(dir.File("home"));
		JsonIO.Save(Path.Combine(home.Secrets, "openai.json"), new ChatGptAuth.Credentials {
			HostId = "host", ClientId = "test-client", AccessToken = "expired-test-token", RefreshToken = "test-refresh-token",
			Scope = "resource.invoke chatgpt.tokens.use.direct", ExpiresAt = 0,
		});
		return new ChatGptAuth(home, http);
	}

	sealed class FakeAuthHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler {
		int attempts;
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
			send(Interlocked.Increment(ref attempts), request, ct);
	}

	static IModelBackend Backend(string provider, TempDir dir, Loopback server, HttpClient http) {
		var home = new Home(dir.File("home"));
		if(provider == "responses") {
			JsonIO.Save(Path.Combine(home.Secrets, "openai.json"), new ChatGptAuth.Credentials {
				HostId = "host", AccessToken = "local-test-only", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
			});
			return new ResponsesBackend(new ChatGptAuth(home, http), http);
		}
		var settings = MessagesProvider.All.Single(p => p.Name == provider) with { BaseUrl = server.Url, EnvVar = null };
		var model = new MessagesBackend(settings, home, modelHttp: http);
		model.SetKey("local-test-only");
		return model;
	}

	static async Task Write(HttpListenerResponse response, string body, CancellationToken ct) {
		await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body), ct);
		await response.OutputStream.FlushAsync(ct);
	}

	sealed class Loopback : IAsyncDisposable {
		readonly HttpListener listener = new();
		readonly CancellationTokenSource shutdown = new();
		readonly Task worker;
		public string Url { get; }

		public Loopback(Func<HttpListenerResponse, CancellationToken, Task> serve) {
			var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			var port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			Url = $"http://127.0.0.1:{port}";
			listener.Prefixes.Add(Url + "/");
			listener.Start();
			worker = Serve(serve);
		}

		async Task Serve(Func<HttpListenerResponse, CancellationToken, Task> serve) {
			try {
				var context = await listener.GetContextAsync();
				using var response = context.Response;
				await context.Request.InputStream.CopyToAsync(Stream.Null, shutdown.Token);
				response.ContentType = "text/event-stream";
				response.SendChunked = true;
				await serve(response, shutdown.Token);
			} catch(Exception e) when(shutdown.IsCancellationRequested && e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
		}

		public HttpClient Client(TimeSpan headers, TimeSpan idle) => ModelHttp.CreateClient(new RedirectToLoopback(Url), headers, () => idle);

		public async ValueTask DisposeAsync() {
			await shutdown.CancelAsync();
			listener.Close();
			await worker;
			shutdown.Dispose();
		}
	}

	// Responses normally targets a fixed HTTPS endpoint. Exercise real HTTP streaming locally without making an external call.
	sealed class RedirectToLoopback(string url) : DelegatingHandler(new SocketsHttpHandler { UseProxy = false }) {
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
			request.RequestUri = new Uri(url + request.RequestUri!.PathAndQuery);
			return base.SendAsync(request, ct);
		}
	}
}
