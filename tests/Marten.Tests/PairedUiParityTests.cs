using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Marten.Storage;
using Marten.Web;

namespace Marten.Tests;

public class PairedUiParityTests {
	[Fact]
	public async Task ActualUiRoutesHavePairedAuthorityWithoutExposingOutsideApi() {
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		const string host = "pairing-test.example";
		using var port = new TcpListener(IPAddress.Loopback, 0);
		port.Start();
		var listenPort = ((IPEndPoint) port.LocalEndpoint).Port;
		port.Stop();
		var config = new Config { Port = listenPort, PairingOrigin = $"https://{host}", Backup = new() { IntervalMinutes = 0 } };
		config.Save(home);
		var pairing = new DevicePairing(home);
		var token = pairing.Redeem(pairing.Create()!.Code, "Parity test")!;
		var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
			WorkingDirectory = dir.File("home"), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
		};
		start.ArgumentList.Add(typeof(Security).Assembly.Location);
		foreach(var key in start.Environment.Keys.Where(k => k.StartsWith("MARTEN_") || k.StartsWith("AWS_") ||
			k.StartsWith("ASPNETCORE_") || k.EndsWith("_API_KEY")).ToArray()) start.Environment.Remove(key);
		start.Environment["MARTEN_HOME"] = home.Root;
		start.Environment["AWS_CONFIG_FILE"] = dir.File("aws-config");
		start.Environment["AWS_SHARED_CREDENTIALS_FILE"] = dir.File("aws-credentials");
		using var process = Process.Start(start)!;
		process.OutputDataReceived += (_, _) => { };
		process.ErrorDataReceived += (_, _) => { };
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{listenPort}") };
		http.DefaultRequestHeaders.Host = host;
		http.DefaultRequestHeaders.Authorization = new("Bearer", token);
		try {
			while(true) {
				Assert.False(process.HasExited, "Scratch Marten exited before listening.");
				try {
					using var response = await http.GetAsync("/api/status", timeout.Token);
					Assert.Equal(HttpStatusCode.OK, response.StatusCode);
					break;
				} catch(HttpRequestException) { await Task.Delay(50, timeout.Token); }
			}

			using var create = await http.PostAsJsonAsync("/api/agents", new { name = "Parity" }, timeout.Token);
			Assert.Equal(HttpStatusCode.OK, create.StatusCode);
			var id = (await create.Content.ReadFromJsonAsync<JsonElement>(timeout.Token)).GetProperty("id").GetString();
			var imageDir = Path.Combine(home.AgentDir(id!), "images");
			Directory.CreateDirectory(imageDir);
			await File.WriteAllBytesAsync(Path.Combine(imageDir, "parity.png"), [1, 2], timeout.Token);
			var ad = $"/api/agents/{id}";
			var none = "/api/agents/does-not-exist";
			foreach(var path in new[] { "/api/agents", "/api/inbox", "/api/messages", "/api/usage", "/api/models", "/api/processes", "/api/tools", "/api/mcp", "/api/backups", "/api/clients", "/api/pair/devices", $"{ad}/transcript?limit=80", $"{ad}/tasks", $"{ad}/schedules", $"{ad}/notebook", $"{ad}/processes" }) {
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
		}

		async Task Check(HttpMethod method, string path, HttpStatusCode[] allowed, object? json = null) {
			using var request = new HttpRequestMessage(method, path);
			if(json != null) request.Content = JsonContent.Create(json);
			using var response = await http.SendAsync(request, timeout.Token);
			Assert.True(allowed.Contains(response.StatusCode), $"{method} {path}: got {response.StatusCode}");
		}
	}
}
