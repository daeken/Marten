using System.Net;
using System.Text;
using System.Text.Json;
using Marten.Storage;
using Marten.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Marten.Tests;

public class JevSettingsTests {
	const string First = "fake-jev-key-for-unit-test";
	const string Second = "fake-rotated-key-for-unit-test";
	const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

	[Theory]
	[InlineData(null, false)]
	[InlineData("", false)]
	[InlineData("a", true)]
	[InlineData("aAzZ09._~+/-==", true)]
	[InlineData("=", false)]
	[InlineData("===", false)]
	[InlineData("=a", false)]
	[InlineData("a=b=", false)]
	[InlineData("a<", false)]
	[InlineData("é", false)]
	[InlineData("a b", false)]
	[InlineData(" a", false)]
	[InlineData("a\t", false)]
	[InlineData("a\r\n", false)]
	public void KeyGrammar(string? value, bool valid) => Assert.Equal(valid, JevKeyFormat.IsValid(value));

	[Fact]
	public void KeyLengthCountsPaddingAndRejectedReplacementPreservesTheActiveGrant() {
		var max = new string('a', 512);
		var padded = new string('a', 510) + "==";
		Assert.True(JevKeyFormat.IsValid(max));
		Assert.True(JevKeyFormat.IsValid(padded));
		Assert.False(JevKeyFormat.IsValid(max + "="));
		Assert.False(JevKeyFormat.IsValid(max + "a"));
		if(OperatingSystem.IsWindows()) return;
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var settings = new JevSettings(home);
		Assert.Equal(JevChange.Saved, settings.Replace(padded));
		Assert.Equal(JevChange.Saved, settings.SetOptions(true, false));
		var grant = Assert.IsType<JevGrant>(settings.TryAcquire());
		Assert.Equal(padded, grant.GetKeyForProvider());
		Assert.Equal(JevChange.InvalidKey, settings.Replace(max + "="));
		Assert.True(settings.IsCurrent(grant));
		Assert.False(grant.Revoked.IsCancellationRequested);
	}

	[Fact]
	public async Task IndependentOptionsPersistAndEveryEffectiveChangeFencesTheOldGrant() {
		if(OperatingSystem.IsWindows()) return;
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var settings = new JevSettings(home);
		Assert.Equal(new(false, false, false, true), settings.Status());
		Assert.Equal(JevChange.KeyRequired, settings.SetOptions(true, false));
		Assert.Equal(JevChange.InvalidKey, settings.Replace("x\r\ny"));
		Assert.Equal(JevChange.Saved, settings.Replace(First));
		Assert.Equal(new(true, false, false, true), settings.Status());
		Assert.Null(settings.TryAcquire());
		Assert.Equal(JevChange.Saved, settings.SetOptions(false, true));
		Assert.Equal(new(true, false, true, true), new JevSettings(home).Status());
		Assert.Null(settings.TryAcquire());
		Assert.Equal(JevChange.Saved, settings.SetOptions(true, true));
		var first = Assert.IsType<JevGrant>(settings.TryAcquire());
		Assert.True(first.ImagePreviewEnabled);
		Assert.Equal(First, first.GetKeyForProvider());
		Assert.Equal(JevChange.Saved, settings.SetOptions(true, true));
		Assert.Same(first, settings.TryAcquire());
		Assert.Equal(JevChange.Saved, settings.SetOptions(true, false));
		Assert.True(first.Revoked.IsCancellationRequested);
		Assert.False(settings.IsCurrent(first));
		var beforeRotation = Assert.IsType<JevGrant>(settings.TryAcquire());
		Assert.False(beforeRotation.ImagePreviewEnabled);
		Assert.Equal(JevChange.Saved, settings.Replace(Second));
		Assert.True(beforeRotation.Revoked.IsCancellationRequested);
		Assert.False(settings.IsCurrent(beforeRotation));
		var rotated = Assert.IsType<JevGrant>(settings.TryAcquire());
		Assert.Equal(Second, rotated.GetKeyForProvider());
		Assert.Equal(Second, new JevSettings(home).TryAcquire()?.GetKeyForProvider());
		var path = Path.Combine(home.Secrets, JevSettings.RecordName);
		Assert.Equal(Private, File.GetUnixFileMode(path));
		Assert.DoesNotContain(First, File.ReadAllText(path));
		Assert.DoesNotContain(new Backups(home, () => new()).Included([]), f => f.Relative.StartsWith("secrets/"));
		Assert.Single(Directory.GetFiles(home.Secrets));
		var callback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var subscription = rotated.Revoked.Register(() => callback.SetResult(!settings.IsCurrent(rotated)));
		Assert.Equal(JevChange.Saved, settings.Remove());
		Assert.True(rotated.Revoked.IsCancellationRequested);
		Assert.True(await callback.Task.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.Equal(new(false, false, false, true), new JevSettings(home).Status());
		Assert.DoesNotContain(Second, File.ReadAllText(path));
		Assert.Equal(Private, File.GetUnixFileMode(path));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("{not-json")]
	[InlineData("{\"version\":1,\"enabled\":true,\"key\":\"fake\"}")]
	[InlineData("{\"version\":1,\"enabled\":true,\"key\":null,\"imagePreviewEnabled\":false}")]
	[InlineData("{\"version\":1,\"enabled\":true,\"key\":\"fake\",\"imagePreviewEnabled\":false,\"enabled\":true}")]
	[InlineData("{\"version\":1,\"enabled\":true,\"key\":\"fake=key\",\"imagePreviewEnabled\":false}")]
	public void MissingOrMalformedStorageRetiresAndCannotRearmByRestoringOldBytes(string? replacement) {
		if(OperatingSystem.IsWindows()) return;
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var path = Path.Combine(home.Secrets, JevSettings.RecordName);
		var settings = new JevSettings(home);
		Assert.Equal(JevChange.Saved, settings.Replace(First));
		Assert.Equal(JevChange.Saved, settings.SetOptions(true, false));
		var grant = Assert.IsType<JevGrant>(settings.TryAcquire());
		var original = File.ReadAllText(path);
		if(replacement == null) File.Delete(path);
		else File.WriteAllText(path, replacement);
		Assert.Null(settings.TryAcquire());
		Assert.True(grant.Revoked.IsCancellationRequested);
		Assert.Null(new JevSettings(home).TryAcquire());
		File.WriteAllText(path, original);
		File.SetUnixFileMode(path, Private);
		Assert.False(settings.Status().StorageHealthy);
		Assert.False(settings.IsCurrent(grant));
		Assert.Equal(JevChange.StorageUnavailable, settings.SetOptions(true, false));
		Assert.Equal(JevChange.Saved, settings.Replace(Second));
		Assert.Equal(new(true, false, false, true), settings.Status());
	}

	[Fact]
	public void UntrustedModeAndFailedRemovalLeaveTheGateOff() {
		if(OperatingSystem.IsWindows()) return;
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var path = Path.Combine(home.Secrets, JevSettings.RecordName);
		var settings = new JevSettings(home);
		Assert.Equal(JevChange.Saved, settings.Replace(First));
		Assert.Equal(JevChange.Saved, settings.SetOptions(true, false));
		var grant = Assert.IsType<JevGrant>(settings.TryAcquire());
		File.SetUnixFileMode(path, Private | UnixFileMode.GroupRead);
		Assert.Null(new JevSettings(home).TryAcquire());
		File.SetUnixFileMode(path, Private);
		File.SetUnixFileMode(home.Secrets, Private | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
		Assert.Equal(JevChange.StorageUnavailable, settings.Remove());
		Assert.True(grant.Revoked.IsCancellationRequested);
		File.SetUnixFileMode(home.Secrets, Private | UnixFileMode.UserExecute);
		Assert.Null(settings.TryAcquire());
		Assert.Equal(JevChange.Saved, settings.Remove());
		Assert.Null(new JevSettings(home).TryAcquire());
	}

	[Fact]
	public async Task LocalMetadataRejectsNonLoopbackAndNeverFallsThroughToOutsideAuth() {
		using var dir = new TempDir();
		var security = new Security(new Home(dir.File("home")), new Config()) {
			ClientAuth = _ => throw new InvalidOperationException("No outside auth fallback expected"),
		};
		var called = false;
		var middleware = security.Middleware(_ => { called = true; return Task.CompletedTask; });
		var ctx = new DefaultHttpContext();
		ctx.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(OperatorRoute.LocalBearer), "jev-fixture"));
		ctx.Request.Host = new HostString("127.0.0.1:7450");
		ctx.Request.Scheme = "http";
		ctx.Request.Headers.Authorization = "Bearer " + security.Token;
		ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
		await middleware(ctx);
		Assert.False(called);
		Assert.Equal(403, ctx.Response.StatusCode);
		Assert.Equal("no-store", ctx.Response.Headers.CacheControl.ToString());
	}

	[Fact]
	public async Task RealScratchRoutesAreBearerOnlyAndNeverReturnKeys() {
		if(OperatingSystem.IsWindows()) return;
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var pairing = new DevicePairing(home);
		var paired = pairing.Redeem(pairing.Create()!.Code, "fixture")!;
		var builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		await using var app = builder.Build();
		// The middleware closure resolves after the synthetic listener selects its ephemeral port.
		Security? security = null;
		app.UseRouting();
		app.Use(next => ctx => security!.Middleware(next)(ctx));
		app.MapGroup("/api").MapJevSettings(new JevSettings(home));
		await app.StartAsync();
		var uri = new Uri(app.Urls.Single());
		using var client = new HttpClient { BaseAddress = uri };
		security = new Security(home, new Config { Port = uri.Port, PairingOrigin = "https://paired.example", RemoteHosts = ["remote.example"], DevOrigin = "http://localhost:5173" }) {
			Pairing = pairing, ClientAuth = t => t == "outside-fixture" ? t : null,
		};
		var op = security.Token;
		const string settings = "/api/jev/settings";
		const string key = "/api/jev/key";
		async Task<string> Check(HttpMethod method, string path, HttpStatusCode code, string? token = null, string? body = null,
			string? host = null, string? origin = null, bool forwarded = false) {
			using var request = new HttpRequestMessage(method, path);
			if(token != null) request.Headers.Authorization = new("Bearer", token);
			if(body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
			if(host != null) request.Headers.Host = host;
			if(origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
			if(forwarded) request.Headers.Add("X-Forwarded-Host", "paired.example");
			using var response = await client.SendAsync(request);
			Assert.Equal(code, response.StatusCode);
			Assert.True(response.Headers.CacheControl?.NoStore);
			var text = await response.Content.ReadAsStringAsync();
			Assert.DoesNotContain(First, text);
			Assert.DoesNotContain(Second, text);
			return text;
		}
		await Check(HttpMethod.Get, settings, HttpStatusCode.Forbidden);
		await Check(HttpMethod.Get, settings + "?token=" + op, HttpStatusCode.Forbidden);
		await Check(HttpMethod.Delete, key + "?token=ignored", HttpStatusCode.Forbidden, op);
		await Check(HttpMethod.Get, settings, HttpStatusCode.Forbidden, paired);
		await Check(HttpMethod.Get, settings, HttpStatusCode.Forbidden, paired, host: "paired.example", origin: "https://paired.example");
		await Check(HttpMethod.Get, settings, HttpStatusCode.Forbidden, op, host: "paired.example");
		await Check(HttpMethod.Get, settings, HttpStatusCode.Forbidden, op, host: "remote.example");
		await Check(HttpMethod.Get, settings, HttpStatusCode.Forbidden, "outside-fixture");
		await Check(HttpMethod.Get, settings, HttpStatusCode.Forbidden, op, origin: "null");
		await Check(HttpMethod.Get, settings, HttpStatusCode.Forbidden, op, origin: $"http://localhost:{uri.Port}");
		await Check(HttpMethod.Get, settings, HttpStatusCode.Forbidden, op, origin: "http://localhost:5173");
		await Check(HttpMethod.Get, settings, HttpStatusCode.Forbidden, op, forwarded: true);
		await Check(HttpMethod.Get, settings, HttpStatusCode.OK, op);
		await Check(HttpMethod.Put, key, HttpStatusCode.BadRequest, op, "{\"key\":\"" + First + "\",");
		await Check(HttpMethod.Put, key, HttpStatusCode.BadRequest, op, JsonIO.Serialize(new { key = "fake=invalid" }));
		await Check(HttpMethod.Put, key, HttpStatusCode.BadRequest, op, JsonIO.Serialize(new { key = new string('a', 513) }));
		await Check(HttpMethod.Put, key, HttpStatusCode.Forbidden, paired, JsonIO.Serialize(new { key = First }));
		await Check(HttpMethod.Put, settings, HttpStatusCode.Conflict, op, "{\"enabled\":true,\"imagePreviewEnabled\":false}");
		const string padded = "valid+/_~.-==";
		Assert.DoesNotContain(padded, await Check(HttpMethod.Put, key, HttpStatusCode.OK, op, JsonIO.Serialize(new { key = padded })));
		await Check(HttpMethod.Put, key, HttpStatusCode.OK, op, JsonIO.Serialize(new { key = First }), origin: uri.GetLeftPart(UriPartial.Authority));
		await Check(HttpMethod.Put, settings, HttpStatusCode.OK, op, "{\"enabled\":true,\"imagePreviewEnabled\":false}");
		await Check(HttpMethod.Put, key, HttpStatusCode.OK, op, JsonIO.Serialize(new { key = Second }));
		var statusText = await Check(HttpMethod.Get, settings, HttpStatusCode.OK, op);
		Assert.Equal(new(true, true, false, true), JsonIO.Deserialize<JevStatus>(statusText));
		using var removed = JsonDocument.Parse(await Check(HttpMethod.Delete, key, HttpStatusCode.OK, op));
		Assert.True(removed.RootElement.GetProperty("localRemovalSucceeded").GetBoolean());
		Assert.False(removed.RootElement.GetProperty("upstreamRevoked").GetBoolean());
		Assert.False(removed.RootElement.GetProperty("settings").GetProperty("enabled").GetBoolean());
		Assert.False(removed.RootElement.GetProperty("settings").GetProperty("hasKey").GetBoolean());
	}
}
