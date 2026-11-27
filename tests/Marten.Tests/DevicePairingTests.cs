using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Marten.Storage;
using Marten.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Marten.Tests;

public class DevicePairingTests {
	[Fact]
	public void PendingInvitationsAndAttemptsAreBoundedAndExpire() {
		using var dir = new TempDir();
		var clock = new FakeTimeProvider();
		var pairing = new DevicePairing(new Home(dir.File("home")), clock);
		var first = pairing.Create()!;
		for(var i = 1; i < 8; i++) Assert.NotNull(pairing.Create());
		Assert.Null(pairing.Create());
		clock.Advance(DevicePairing.CodeLifetime);
		Assert.Null(pairing.Redeem(first.Code, "Expired phone"));
		Assert.NotNull(pairing.Create());
		var slow = Enumerable.Range(0, 8).Select(_ => pairing.BeginAttempt()).ToArray();
		Assert.All(slow, attempt => Assert.NotNull(attempt));
		Assert.Null(pairing.BeginAttempt());
		foreach(var attempt in slow) attempt!.Dispose();
		for(var i = 8; i < DevicePairing.AttemptsPerMinute; i++) {
			using var attempt = pairing.BeginAttempt();
			Assert.NotNull(attempt);
		}
		Assert.Null(pairing.BeginAttempt());
		clock.Advance(TimeSpan.FromMinutes(1));
		using var retry = pairing.BeginAttempt();
		Assert.NotNull(retry);
	}

	[Fact]
	public async Task LocalOperatorPairsOneDeviceAndRevokesItsActiveSocket() {
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var origin = new Uri("https://marten.example");
		var pairing = new DevicePairing(home);
		const string legacy = "legacy-web.example";
		var sec = new Security(home, new Config { PairingOrigin = origin.AbsoluteUri, RemoteHosts = [legacy] }) {
			Pairing = pairing, ClientAuth = s => s == "outside" ? "client" : null,
		};
		var hub = new EventHub();
		var builder = WebApplication.CreateSlimBuilder();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Logging.ClearProviders();
		await using var app = builder.Build();
		app.UseRouting();
		app.Use(sec.Middleware);
		app.UseWebSockets();
		var api = app.MapGroup("/api");
		api.MapPairing(pairing, sec);
		api.MapGet("/status", () => Results.Ok()).AllowPaired();
		api.MapPost("/clients", () => Results.Ok()).AllowPaired();
		api.MapPut("/backends/{provider}/key", () => Results.Ok()).AllowPaired();
		api.MapGet("/new-admin-surface", () => Results.Ok());
		api.MapGet("/ext/status", () => Results.Ok());
		var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		api.MapGet("/events", (RequestDelegate)(async ctx => {
			using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
			var serving = hub.Serve(socket, ctx.RequestAborted, app.Lifetime.ApplicationStopping, Security.DeviceRevocationOf(ctx));
			listening.TrySetResult();
			await serving;
		})).AllowPaired();
		await app.StartAsync();
		using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
		const string local = "127.0.0.1:7450"; // logical production Host; listener uses an ephemeral test port
		const string remote = "marten.example";
		const string create = "/api/pair/create", list = "/api/pair/devices", redeem = "/api/pair/redeem";

		async Task<HttpResponseMessage> Send(HttpMethod method, string path, string host, string? token = null, object? body = null, string? browserOrigin = null) {
			using var req = new HttpRequestMessage(method, path);
			req.Headers.Host = host;
			if(browserOrigin != null) req.Headers.Add("Origin", browserOrigin);
			if(token != null) req.Headers.Authorization = new("Bearer", token);
			if(body != null) req.Content = JsonContent.Create(body);
			return await http.SendAsync(req);
		}
		async Task Expect(HttpStatusCode status, HttpMethod method, string path, string host, string? token = null, object? body = null, string? browserOrigin = null) {
			using var response = await Send(method, path, host, token, body, browserOrigin);
			Assert.Equal(status, response.StatusCode);
		}

		await Expect(HttpStatusCode.Forbidden, HttpMethod.Post, create, remote, sec.Token); // helper peer is loopback
		await Expect(HttpStatusCode.Forbidden, HttpMethod.Get, list, remote, sec.Token);
		await Expect(HttpStatusCode.Forbidden, HttpMethod.Post, create, local, "outside");
		await Expect(HttpStatusCode.Forbidden, HttpMethod.Post, create, local, sec.Token, browserOrigin: "https://marten.example");
		await Expect(HttpStatusCode.Forbidden, HttpMethod.Post, create + "?token=" + sec.Token, local);
		using var created = await Send(HttpMethod.Post, create, local, sec.Token, browserOrigin: "http://" + local);
		Assert.Equal(HttpStatusCode.OK, created.StatusCode);
		Assert.True(created.Headers.CacheControl?.NoStore);
		using var qr = JsonDocument.Parse((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("payload").GetString()!);
		Assert.Equal(2, qr.RootElement.GetProperty("v").GetInt32());
		Assert.Equal("https://marten.example", qr.RootElement.GetProperty("server").GetString());
		Assert.False(qr.RootElement.TryGetProperty("tsAuthKey", out _));
		var code = qr.RootElement.GetProperty("code").GetString()!;
		Assert.Equal(43, code.Length);
		var body = new { code, deviceName = "Phone" };
		await Expect(HttpStatusCode.Forbidden, HttpMethod.Post, redeem, remote, "outside", body);
		await Expect(HttpStatusCode.Forbidden, HttpMethod.Post, redeem, local, body: body);
		var contenders = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Send(HttpMethod.Post, redeem, remote, body: body)));
		using var success = Assert.Single(contenders, r => r.StatusCode == HttpStatusCode.OK);
		using var consumed = Assert.Single(contenders, r => r.StatusCode == HttpStatusCode.BadRequest);
		Assert.True(success.Headers.CacheControl?.NoStore);
		var token = (await success.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
		Assert.Equal(43, token.Length);
		Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(home.Secrets, "paired-devices.json")));
		Assert.NotNull(new DevicePairing(home).Authenticate(token));
		await Expect(HttpStatusCode.OK, HttpMethod.Get, "/api/status", remote, token);
		await Expect(HttpStatusCode.Unauthorized, HttpMethod.Get, "/api/status?token=" + token, remote);
		await Expect(HttpStatusCode.OK, HttpMethod.Post, "/api/clients", remote, token);
		await Expect(HttpStatusCode.OK, HttpMethod.Put, "/api/backends/openai/key", remote, token);
		await Expect(HttpStatusCode.Forbidden, HttpMethod.Get, "/api/new-admin-surface", remote, token);
		await Expect(HttpStatusCode.Forbidden, HttpMethod.Get, "/api/ext/status", remote, token);
		await Expect(HttpStatusCode.Forbidden, HttpMethod.Get, "/api/ext/status", remote, "outside");
		await Expect(HttpStatusCode.OK, HttpMethod.Get, "/api/ext/status", local, "outside");
		await Expect(HttpStatusCode.OK, HttpMethod.Get, "/api/ext/status", legacy, "outside");
		await Expect(HttpStatusCode.Unauthorized, HttpMethod.Get, "/api/status", remote, sec.Token);
		await Expect(HttpStatusCode.Unauthorized, HttpMethod.Get, "/api/status", remote, "outside");
		await Expect(HttpStatusCode.Forbidden, HttpMethod.Get, "/", remote);
		await Expect(HttpStatusCode.Forbidden, HttpMethod.Post, redeem, remote, token, body);
		var device = Assert.Single(pairing.List());
		await Expect(HttpStatusCode.Forbidden, HttpMethod.Delete, $"{list}/{device.Id}", remote, sec.Token);
		await Expect(HttpStatusCode.OK, HttpMethod.Get, list, remote, token);
		using var nextInvitation = await Send(HttpMethod.Post, "/api/PAIR/create/", remote, token);
		Assert.Equal(HttpStatusCode.OK, nextInvitation.StatusCode);
		using var nextQr = JsonDocument.Parse((await nextInvitation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("payload").GetString()!);
		using var nextRedeemed = await Send(HttpMethod.Post, redeem, remote, body: new { code = nextQr.RootElement.GetProperty("code").GetString(), deviceName = "Tablet" });
		Assert.Equal(HttpStatusCode.OK, nextRedeemed.StatusCode);
		var otherToken = (await nextRedeemed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
		Assert.NotEqual(token, otherToken);
		Assert.NotEqual(sec.Token, otherToken);

		using var ws = new ClientWebSocket();
		ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
		ws.Options.SetRequestHeader("Host", remote);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await ws.ConnectAsync(new UriBuilder(http.BaseAddress!) { Scheme = "ws", Path = "/api/events" }.Uri, timeout.Token);
		await listening.Task.WaitAsync(timeout.Token);
		hub.Broadcast(new { type = "test" });
		var buffer = new byte[256];
		Assert.Equal(WebSocketMessageType.Text, (await ws.ReceiveAsync(buffer, timeout.Token)).MessageType);
		await Expect(HttpStatusCode.NoContent, HttpMethod.Delete, $"{list}/{device.Id}", remote, otherToken);
		await Expect(HttpStatusCode.OK, HttpMethod.Get, "/api/status", remote, otherToken);
		try {
			Assert.Equal(WebSocketMessageType.Close, (await ws.ReceiveAsync(buffer, timeout.Token)).MessageType);
		} catch(WebSocketException) { } // revocation may terminate the transport without a close frame
		await Expect(HttpStatusCode.Unauthorized, HttpMethod.Get, "/api/status", remote, token);
		await Expect(HttpStatusCode.Unauthorized, HttpMethod.Get, "/api/events", remote, token);
		Assert.Null(new DevicePairing(home).Authenticate(token));
	}
}
