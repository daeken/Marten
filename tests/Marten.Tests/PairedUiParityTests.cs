using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Marten.Agents;
using Marten.Storage;
using Marten.Web;

namespace Marten.Tests;

public class PairedUiParityTests {
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ActualUiRoutesHavePairedAuthorityWithoutExposingOutsideApi(bool embedded) {
		if(embedded && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var host = embedded ? "marten.scratch.ts.net" : "pairing-test.example";
		const string ignored = "ignored.example";
		const string legacyHost = "legacy-web.example";
		using var port = new TcpListener(IPAddress.Loopback, 0);
		port.Start();
		var listenPort = ((IPEndPoint) port.LocalEndpoint).Port;
		port.Stop();
		var config = new Config { Port = listenPort, PairingOrigin = $"https://{(embedded ? ignored : host)}",
			Tailnet = embedded ? new() { Host = host } : null, RemoteHosts = new() { $"{host}:443", $"{host}.", legacyHost },
			Backup = new() { IntervalMinutes = 0 } };
		config.Save(home);
		var webRoot = dir.File("dist");
		Directory.CreateDirectory(webRoot);
		await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "scratch UI");
		await File.WriteAllTextAsync(Path.Combine(webRoot, "sw.js"), "// scratch service worker");
		if(embedded) {
			var bin = Path.Combine(home.Root, "bin");
			Directory.CreateDirectory(bin);
			var helper = Path.Combine(bin, "marten-tsnet");
			await File.WriteAllTextAsync(helper, "#!/bin/sh\nprintf 'READY\\n'\nread unused\n");
			if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
		const string fixtureId = "artifact-fixture";
		ArtifactRef? artifact = null;
		const string missingId = "abcdabcdabcdabcdabcdabcdabcdabcd";
		const string privateLeaf = "fixture-private-leaf.txt", privateLabel = "fixture-private-label", privateRaw = "fixture-private-raw";
		if(ArtifactFiles.Supported) {
			var fixture = home.AgentDir(fixtureId);
			var log = new Transcript(Path.Combine(fixture, "transcript"));
			using var store = new Artifacts(home, message => throw new Exception(message));
			store.PrepareAgent(fixtureId);
			await File.WriteAllTextAsync(Path.Combine(fixture, Artifacts.Staging, "example.txt"), new string('a', Artifacts.MaxText + 1));
			artifact = await store.Publish(fixtureId, "example.txt", null, default);
			log.Append(new Entry { Kind = EntryKind.ToolCall, Name = "publish_artifact", CallId = "fixture-call",
				Arguments = JsonIO.Serialize(new { filename = privateLeaf, label = privateLabel }),
				Raw = JsonSerializer.SerializeToElement(new { name = "publish_artifact", arguments = privateRaw }) });
			log.Append(new Entry { Kind = EntryKind.ToolCall, Name = "call_tool", CallId = "delegated",
				Arguments = JsonIO.Serialize(new { name = "publish_artifact", arguments = new { filename = privateLeaf, label = privateLabel } }),
				Raw = JsonSerializer.SerializeToElement(new { name = "call_tool", arguments = privateRaw }) });
			log.Append(new Entry { Kind = EntryKind.ToolCall, Name = "read_file", Arguments = "unrelated-legacy-arguments" });
			log.Append(new Entry { Kind = EntryKind.ToolResult, Artifacts = [artifact] });
			// A reference-only restore must not read replacement bytes from the agent-writable inbox.
			var missing = artifact with { Id = missingId };
			log.Append(new Entry { Kind = EntryKind.ToolResult, Artifacts = [missing] });
			await File.WriteAllTextAsync(Path.Combine(fixture, Artifacts.Staging, missingId), new string('b', (int) missing.Bytes));
			await File.WriteAllTextAsync(Path.Combine(fixture, "system.md"), "Offline test fixture");
			JsonIO.Save(Path.Combine(fixture, "agent.json"), new AgentInfo { Id = fixtureId, Kind = AgentKind.Chat,
				Name = "Offline fixture", WorkingDirectory = fixture, Created = DateTimeOffset.UtcNow, Closed = true });
		}
		var pairing = new DevicePairing(home);
		var token = pairing.Redeem(pairing.Create()!.Code, "Parity test")!;
		var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
			WorkingDirectory = dir.File("home"), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
		};
		start.ArgumentList.Add(typeof(Security).Assembly.Location);
		foreach(var envName in start.Environment.Keys.Where(k => k.StartsWith("MARTEN_") || k.StartsWith("AWS_") ||
			k.StartsWith("ASPNETCORE_") || k.EndsWith("_API_KEY")).ToArray()) start.Environment.Remove(envName);
		start.Environment["MARTEN_HOME"] = home.Root;
		start.Environment["MARTEN_WEB_ROOT"] = webRoot;
		start.Environment["AWS_CONFIG_FILE"] = dir.File("aws-config");
		start.Environment["AWS_SHARED_CREDENTIALS_FILE"] = dir.File("aws-credentials");
		using var process = Process.Start(start)!;
		var logs = new ConcurrentQueue<string>();
		process.OutputDataReceived += (_, e) => { if(e.Data != null) logs.Enqueue(e.Data); };
		process.ErrorDataReceived += (_, e) => { if(e.Data != null) logs.Enqueue(e.Data); };
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{listenPort}") };
		http.DefaultRequestHeaders.Host = host;
		http.DefaultRequestHeaders.Authorization = new("Bearer", token);
		const string key = "tskey-auth-scratch_not-a-provider-key";
		string? code = null;
		try {
			while(true) {
				Assert.False(process.HasExited, $"Scratch Marten exited before listening: {string.Join(Environment.NewLine, logs)}");
				try {
					using var response = await http.GetAsync("/api/status", timeout.Token);
					if(embedded && response.StatusCode == HttpStatusCode.Forbidden) { await Task.Delay(50, timeout.Token); continue; }
					Assert.Equal(HttpStatusCode.OK, response.StatusCode);
					break;
				} catch(HttpRequestException) { await Task.Delay(50, timeout.Token); }
			}

			await Check(HttpMethod.Get, "/", [HttpStatusCode.Forbidden]);
			await Check(HttpMethod.Get, "/sw.js", [HttpStatusCode.Forbidden]);
			var local = $"127.0.0.1:{listenPort}";
			using var shell = new HttpRequestMessage(HttpMethod.Get, "/");
			shell.Headers.Host = local;
			using var localResponse = await http.SendAsync(shell, timeout.Token);
			Assert.Equal("scratch UI", await localResponse.Content.ReadAsStringAsync(timeout.Token));
			var op = await File.ReadAllTextAsync(Path.Combine(home.Secrets, "ui-token"), timeout.Token);
			if(embedded) {
				async Task<HttpResponseMessage> Pair(HttpMethod method, string path, string authority, string? bearer = null, object? body = null) {
					using var req = new HttpRequestMessage(method, path);
					req.Headers.Host = authority;
					if(bearer != null) req.Headers.Authorization = new("Bearer", bearer);
					if(body != null) req.Content = JsonContent.Create(body);
					using var isolated = new HttpClient { BaseAddress = http.BaseAddress };
					return await isolated.SendAsync(req, timeout.Token);
				}
				using var missing = await Pair(HttpMethod.Post, "/api/pair/create", local, op);
				Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
				using var wrongKey = await Pair(HttpMethod.Post, "/api/pair/create", local, op, new { tsAuthKey = "tskey-api-fake" });
				Assert.Equal(HttpStatusCode.BadRequest, wrongKey.StatusCode);
				using var invited = await Pair(HttpMethod.Post, "/api/pair/create", local, op, new { tsAuthKey = key });
				Assert.Equal(HttpStatusCode.OK, invited.StatusCode);
				Assert.True(invited.Headers.CacheControl?.NoStore);
				var offer = await invited.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
				using var qr = JsonDocument.Parse(offer.GetProperty("payload").GetString()!);
				Assert.Equal(2, qr.RootElement.GetProperty("v").GetInt32());
				Assert.Equal($"https://{host}", qr.RootElement.GetProperty("server").GetString());
				Assert.True(qr.RootElement.GetProperty("tsAuthKey").GetString() == key);
				var deadline = qr.RootElement.GetProperty("expiresAt").GetString();
				Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", deadline!);
				Assert.Equal(deadline, offer.GetProperty("expiresAt").GetString());
				code = qr.RootElement.GetProperty("code").GetString();
				using var rejected = await Pair(HttpMethod.Post, "/api/pair/redeem", host, body: new { code, deviceName = "Phone\u202E" });
				Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
				using var redeemed = await Pair(HttpMethod.Post, "/api/pair/redeem", host, body: new { code, deviceName = "Scratch phone" });
				Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
				var credential = (await redeemed.Content.ReadFromJsonAsync<JsonElement>(timeout.Token)).GetProperty("token").GetString()!;
				Assert.True(credential != op && credential != key && credential != code);
				http.DefaultRequestHeaders.Authorization = new("Bearer", credential);
				using var used = await Pair(HttpMethod.Post, "/api/pair/redeem", host, body: new { code, deviceName = "Try again" });
				Assert.Equal(HttpStatusCode.BadRequest, used.StatusCode);
				using var legacy = await Pair(HttpMethod.Get, "/api/status", host, op);
				Assert.Equal(HttpStatusCode.Unauthorized, legacy.StatusCode);
				using var query = await Pair(HttpMethod.Get, "/api/status?token=" + op, host);
				Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);

				using var forbidden = await Pair(HttpMethod.Post, "/api/pair/create", host, op, new { tsAuthKey = key });
				Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
				using var unauth = await Pair(HttpMethod.Post, "/api/pair/create", host, body: new { tsAuthKey = key });
				Assert.Equal(HttpStatusCode.Forbidden, unauth.StatusCode);
				using var delegated = await Pair(HttpMethod.Post, "/api/pair/create", host, credential, new { tsAuthKey = "tskey-auth-second_synthetic_key" });
				Assert.Equal(HttpStatusCode.OK, delegated.StatusCode);
				using var next = JsonDocument.Parse((await delegated.Content.ReadFromJsonAsync<JsonElement>(timeout.Token)).GetProperty("payload").GetString()!);
				Assert.True(code != next.RootElement.GetProperty("code").GetString());
				var original = Assert.Single((await ReadList()).EnumerateArray(), d => d.GetProperty("name").GetString() == "Parity test");
				using var revoked = await Pair(HttpMethod.Delete, "/api/pair/devices/" + original.GetProperty("id").GetString(), host, credential);
				Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
				async Task<JsonElement> ReadList() {
					using var listed = await Pair(HttpMethod.Get, "/api/pair/devices", host, credential);
					Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
					return (await listed.Content.ReadFromJsonAsync<JsonElement>(timeout.Token)).GetProperty("devices");
				}

				foreach(var alias in new[] { ignored, host + ":443", host + ".", host.ToUpperInvariant() }) {
					using var req = new HttpRequestMessage(HttpMethod.Get, "/api/status");
					req.Headers.Host = alias;
					using var res = await http.SendAsync(req, timeout.Token);
					Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
				}
			}

			// Same device Bearer as the canonical requests, at an independently allowed web alias.
			await Check(HttpMethod.Get, "/api/status", [HttpStatusCode.OK]);
			await Check(HttpMethod.Get, "/api/pair/devices", [HttpStatusCode.OK]);
			await Check(HttpMethod.Get, "/api/status", [HttpStatusCode.Unauthorized], authority: legacyHost);
			await Check(HttpMethod.Get, "/api/pair/devices", [HttpStatusCode.Forbidden], authority: legacyHost);
			await Check(HttpMethod.Post, "/api/pair/create", [HttpStatusCode.Forbidden],
				embedded ? new { tsAuthKey = key } : null, authority: legacyHost);
			await Check(HttpMethod.Delete, $"/api/pair/devices/{Guid.Empty}", [HttpStatusCode.Forbidden], authority: legacyHost);
			await Check(HttpMethod.Get, "/api/status", [HttpStatusCode.OK], authority: legacyHost, bearer: op);
			await Check(HttpMethod.Get, "/api/pair/devices", [HttpStatusCode.OK], authority: local, bearer: op);

			using var create = await http.PostAsJsonAsync("/api/agents", new { name = "Parity" }, timeout.Token);
			Assert.Equal(HttpStatusCode.OK, create.StatusCode);
			var id = (await create.Content.ReadFromJsonAsync<JsonElement>(timeout.Token)).GetProperty("id").GetString();
			var imageDir = Path.Combine(home.AgentDir(id!), "images");
			Directory.CreateDirectory(imageDir);
			await File.WriteAllBytesAsync(Path.Combine(imageDir, "parity.png"), [1, 2], timeout.Token);
			var ad = $"/api/agents/{id}";
			var none = "/api/agents/does-not-exist";
			foreach(var path in new[] { "/api/agents", "/api/inbox", "/api/messages", "/api/usage", "/api/models", "/api/processes", "/api/tools", "/api/mcp", "/api/backups", "/api/clients", "/api/pair/devices", $"{ad}/transcript?limit=80", $"{ad}/tasks", $"{ad}/schedules", $"{ad}/notebook", $"{ad}/processes", $"{ad}/processes?includeStopped=true" }) {
				// A scratch server has no providers configured; model discovery may return 502, but not an authorization error.
				await Check(HttpMethod.Get, path, path == "/api/models" ? [HttpStatusCode.OK, HttpStatusCode.BadGateway] : [HttpStatusCode.OK]);
			}
			using var notebook = await http.GetAsync($"{ad}/notebook.ipynb", timeout.Token);
			Assert.Equal("application/x-ipynb+json", notebook.Content.Headers.ContentType?.MediaType);
			using var image = await http.GetAsync($"{ad}/images/parity.png", timeout.Token);
			Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
			await Check(HttpMethod.Get, $"{ad}/processes/1/log", [HttpStatusCode.NotFound]);
			await Check(HttpMethod.Get, "/api/projects/missing/memory", [HttpStatusCode.NotFound]);
			await Check(HttpMethod.Get, "/api/events", [HttpStatusCode.BadRequest]); // GET must admit upgrades; socket lifecycle tested separately
			await Check(HttpMethod.Post, "/api/auth/login", [HttpStatusCode.OK]); // just creates the scratch host's loopback listener
			await Check(HttpMethod.Post, "/api/auth/logout", [HttpStatusCode.OK]);
			await Check(HttpMethod.Put, "/api/config", [HttpStatusCode.OK], new { model = "", effort = "" });
			await Check(HttpMethod.Put, "/api/backends/anthropic/key", [HttpStatusCode.OK], new { key = "" });
			await Check(HttpMethod.Put, "/api/backups/passphrase", [HttpStatusCode.OK], new { key = "" });
			await Check(HttpMethod.Post, "/api/backups", [HttpStatusCode.OK]);
			await Check(HttpMethod.Post, "/api/mcp/refresh", [HttpStatusCode.OK]);
			await Check(HttpMethod.Post, "/api/clients", [HttpStatusCode.OK], new { name = "parity" });
			if(artifact != null) {
				var fixture = $"/api/agents/{fixtureId}";
				var download = $"{fixture}/artifacts/{artifact.Id}";
				using var full = await http.GetAsync(download, timeout.Token);
				Assert.Equal(HttpStatusCode.OK, full.StatusCode);
				Assert.True(full.Headers.CacheControl?.NoStore);
				Assert.Equal("nosniff", Assert.Single(full.Headers.GetValues("X-Content-Type-Options")));
				Assert.Equal("application/octet-stream", full.Content.Headers.ContentType?.MediaType);
				Assert.Equal("attachment", full.Content.Headers.ContentDisposition?.DispositionType);
				Assert.Equal(artifact.Bytes, (await full.Content.ReadAsByteArrayAsync(timeout.Token)).Length);
				using var preview = await http.GetAsync(download + "/preview", timeout.Token);
				Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
				Assert.Equal("text/plain", preview.Content.Headers.ContentType?.MediaType);
				Assert.Equal("1", Assert.Single(preview.Headers.GetValues("X-Marten-Preview-Truncated")));
				Assert.Equal(Artifacts.MaxText, (await preview.Content.ReadAsByteArrayAsync(timeout.Token)).Length);
				await Check(HttpMethod.Get, download, [HttpStatusCode.OK], authority: local, bearer: op);
				await Check(HttpMethod.Get, download + "?ignored=1", [HttpStatusCode.Forbidden]);
				await Check(HttpMethod.Get, $"{ad}/artifacts/{artifact.Id}", [HttpStatusCode.NotFound]);
				var missing = $"{fixture}/artifacts/{missingId}";
				await Check(HttpMethod.Get, missing, [HttpStatusCode.NotFound]);
				await Check(HttpMethod.Get, missing + "/preview", [HttpStatusCode.NotFound]);
				using var retained = await http.GetAsync(fixture + "/transcript?limit=80", timeout.Token);
				Assert.Contains(missingId, await retained.Content.ReadAsStringAsync(timeout.Token));
				using var operatorHistory = await http.GetAsync(fixture + "/transcript", timeout.Token);
				Assert.True(operatorHistory.Headers.CacheControl?.NoStore);
				Assert.Contains(privateRaw, await operatorHistory.Content.ReadAsStringAsync(timeout.Token));
				using var isolated = new HttpClient { BaseAddress = http.BaseAddress };
				using var queryOnly = await isolated.GetAsync(download + "?token=" + op, timeout.Token);
				Assert.Equal(HttpStatusCode.Forbidden, queryOnly.StatusCode);
				using var cross = new HttpRequestMessage(HttpMethod.Get, download);
				cross.Headers.TryAddWithoutValidation("Origin", "https://other.example");
				using var blocked = await http.SendAsync(cross, timeout.Token);
				Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
				var outsider = await File.ReadAllTextAsync(Path.Combine(home.Secrets, "clients", "parity.token"), timeout.Token);
				await Check(HttpMethod.Get, download, [HttpStatusCode.Forbidden], authority: local, bearer: outsider);
				isolated.DefaultRequestHeaders.Authorization = new("Bearer", outsider);
				foreach(var path in new[] { fixture + "/transcript", fixture + "/transcript?limit=80", fixture + "/transcript?before=6&limit=80", $"/api/ext/agents/{fixtureId}/transcript" }) {
					using var history = await isolated.GetAsync(path, timeout.Token);
					Assert.Equal(HttpStatusCode.OK, history.StatusCode);
					Assert.True(history.Headers.CacheControl?.NoStore);
					var json = await history.Content.ReadAsStringAsync(timeout.Token);
					foreach(var hidden in new[] { artifact.Id, missingId, privateLeaf, privateLabel, privateRaw }) Assert.DoesNotContain(hidden, json);
					Assert.Contains("unrelated-legacy-arguments", json);
				}
			}
			await Check(HttpMethod.Delete, "/api/clients/parity", [HttpStatusCode.OK]);
			await Check(HttpMethod.Post, "/api/pause", [HttpStatusCode.OK], new { paused = true });
			// Validate authorization without starting a project orchestrator and its PowerShell runspace.
			await Check(HttpMethod.Post, "/api/projects", [HttpStatusCode.BadRequest], new { name = "Parity project", bindings = Array.Empty<string>() });
			await Check(HttpMethod.Post, "/api/projects/missing/pause", [HttpStatusCode.NotFound], new { paused = true });
			await Check(HttpMethod.Post, "/api/tools/approve", [HttpStatusCode.NotFound], new { path = dir.File("nonexistent.ps1") });
			await Check(HttpMethod.Post, "/api/questions/missing/answer", [HttpStatusCode.NotFound], new { text = "No question" });
			await Check(HttpMethod.Post, $"{none}/notebook/1/rerun", [HttpStatusCode.NotFound]);
			await Check(HttpMethod.Post, $"{none}/processes/1/stop", [HttpStatusCode.NotFound]);
			await Check(HttpMethod.Delete, $"{none}/schedules/1", [HttpStatusCode.NotFound]);
			foreach(var action in new[] { "close", "pause", "messages", "start-fresh-context", "interrupt" })
				await Check(HttpMethod.Post, $"{none}/{action}", [HttpStatusCode.NotFound], new { closed = true, paused = true, text = "test" });
			await Check(HttpMethod.Put, $"{none}/settings", [HttpStatusCode.NotFound], new { model = "", effort = "" });
			await Check(HttpMethod.Get, "/api/ext/whoami", [HttpStatusCode.Forbidden]);
			await Check(HttpMethod.Get, "/api/not-in-ui", [HttpStatusCode.Forbidden]);
		} finally {
			if(!process.HasExited) process.Kill(entireProcessTree: true);
			await process.WaitForExitAsync();
			process.WaitForExit();
			Assert.False(logs.Any(l => l.Contains(key) || (code != null && l.Contains(code))), "Pairing material appeared in server logs.");
		}

		async Task Check(HttpMethod method, string path, HttpStatusCode[] allowed, object? json = null,
			string? authority = null, string? bearer = null) {
			using var request = new HttpRequestMessage(method, path);
			if(authority != null) request.Headers.Host = authority;
			if(bearer != null) request.Headers.Authorization = new("Bearer", bearer);
			if(json != null) request.Content = JsonContent.Create(json);
			using var response = await http.SendAsync(request, timeout.Token);
			Assert.True(allowed.Contains(response.StatusCode), $"{method} {path}: got {response.StatusCode}");
		}
	}
}
