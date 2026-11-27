using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Marten.Storage;
using Marten.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Marten.Tests;

public class MacFilesTests {
	[Fact]
	public async Task RejectsNonNativeLoopbackEvenWithRealPairedCredentialAndCanonicalHost() {
		await using var f = await Fixture.Start(enabled: false);
		Assert.Equal(HttpStatusCode.Forbidden, (await f.Get("roots", proof: false)).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await f.Get("roots")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await f.Get("roots", token: "")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await f.Get("roots", token: f.Operator)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await f.Get("roots", token: "outside")).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await f.Get("roots?token=fake")).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await f.Get("roots", host: "wrong.example")).StatusCode);
	}

	[Fact]
	public async Task ListsHiddenRegularFilesSkipsLinksAndSpecialsAndStagesExactBytes() {
		if(!MacFileSystem.Supported) return;
		await using var f = await Fixture.Start();
		var leaf = Path.Combine(f.Root, ".scratch");
		await File.WriteAllTextAsync(leaf, "test bytes");
		File.CreateSymbolicLink(Path.Combine(f.Root, "alias"), leaf);
		using(var p = System.Diagnostics.Process.Start("/usr/bin/mkfifo", Path.Combine(f.Root, "queue"))) {
			await p!.WaitForExitAsync(); Assert.Equal(0, p.ExitCode);
		}
		var root = (await f.Json("roots"))[0].GetProperty("id").GetString();
		var row = (await f.Json($"entries/{root}")).GetProperty("entries").EnumerateArray().Single();
		Assert.Equal(".scratch", row.GetProperty("name").GetString());
		var id = row.GetProperty("id").GetString();
		Assert.Equal(HttpStatusCode.NotFound, (await f.Get($"bytes/{root}/share")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await f.Get($"bytes/{id}/share", token: f.Other)).StatusCode);
		var bytes = await f.Get($"bytes/{id}/share");
		Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
		Assert.Equal("attachment", bytes.Content.Headers.ContentDisposition?.DispositionType);
		Assert.Equal(10, bytes.Content.Headers.ContentLength);
		Assert.Equal("test bytes", await bytes.Content.ReadAsStringAsync());
		await File.WriteAllTextAsync(leaf, "changed data");
		var stale = await f.Get($"bytes/{id}/share");
		Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		Assert.Empty(await stale.Content.ReadAsByteArrayAsync());
	}

	[Fact]
	public async Task ContinuationsAreCredentialAndDirectoryRevisionBound() {
		if(!MacFileSystem.Supported) return;
		await using var f = await Fixture.Start();
		for(var i = 0; i < 103; i++) File.WriteAllText(Path.Combine(f.Root, $"f{i:000}"), "");
		var root = (await f.Json("roots"))[0].GetProperty("id").GetString();
		var page = await f.Json($"entries/{root}");
		Assert.Equal(100, page.GetProperty("entries").GetArrayLength());
		var next = page.GetProperty("next").GetString();
		Assert.Equal(HttpStatusCode.NotFound, (await f.Get($"entries/{next}", token: f.Other)).StatusCode);
		Assert.Equal(3, (await f.Json($"entries/{next}")).GetProperty("entries").GetArrayLength());
		File.WriteAllText(Path.Combine(f.Root, "changed"), "");
		Assert.Equal(HttpStatusCode.Conflict, (await f.Get($"entries/{next}")).StatusCode);
	}

	sealed class Fixture(TempDir data, WebApplication app, string url, string root, string bearer, string other,
		string op, string ingress) : IAsyncDisposable {
		readonly HttpClient client = new() { BaseAddress = new Uri(url) };
		public string Other => other;
		public string Operator => op;
		public string Root => root;

		public static async Task<Fixture> Start(bool enabled = true) {
			var data = new TempDir();
			var root = Directory.CreateDirectory(data.File("scratch")).FullName;
			if(root.StartsWith("/var/", StringComparison.Ordinal)) root = "/private" + root;
			var home = new Home(data.File("home"));
			var cfg = new Config { PairingOrigin = "https://mac-files.example" };
			var pairing = new DevicePairing(home);
			var bearer = pairing.Redeem(pairing.Create()!.Code, "fixture-one")!;
			var other = pairing.Redeem(pairing.Create()!.Code, "fixture-two")!;
			var ingress = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
			var security = new Security(home, cfg) { Pairing = pairing, ClientAuth = t => t == "outside" ? t : null };
			var builder = WebApplication.CreateSlimBuilder();
			builder.Logging.ClearProviders();
			builder.WebHost.UseUrls("http://127.0.0.1:0");
			var app = builder.Build();
			app.UseRouting(); app.Use(security.Middleware);
			new MacFiles(enabled, ctx => ctx.Request.Headers[TailnetTransport.NativeProofHeader] == ingress,
				pairing, app.Lifetime.ApplicationStopping, root).Map(app.MapGroup("/api"));
			await app.StartAsync();
			return new Fixture(data, app, app.Urls.Single(), root, bearer, other, security.Token, ingress);
		}

		public async Task<HttpResponseMessage> Get(string tail, string? token = null, bool proof = true, string? host = null) {
			using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/mac-files/{tail}");
			request.Headers.Host = host ?? "mac-files.example";
			if(proof) request.Headers.Add(TailnetTransport.NativeProofHeader, ingress);
			if((token ?? bearer).Length != 0) request.Headers.Authorization = new("Bearer", token ?? bearer);
			return await client.SendAsync(request);
		}
		public async Task<JsonElement> Json(string tail) {
			using var r = await Get(tail);
			Assert.Equal(HttpStatusCode.OK, r.StatusCode);
			Assert.True(r.Headers.CacheControl?.NoStore);
			using var document = JsonDocument.Parse(await r.Content.ReadAsByteArrayAsync());
			return document.RootElement.Clone();
		}
		public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); data.Dispose(); }
	}
}
