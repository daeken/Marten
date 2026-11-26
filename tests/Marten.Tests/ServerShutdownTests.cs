using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using Marten.Agents;
using Marten.Mcp;
using Marten.Storage;
using Marten.Tools;
using Xunit.Abstractions;

namespace Marten.Tests;

public sealed class ServerShutdownTests(ITestOutputHelper output) {
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ExternalSigtermDropsWebSocketAndExitsWithoutReplayingWork(bool loaded) {
		if(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var port = FreePort();
		var config = new Config { Port = port, StopGraceSeconds = 1, Backup = new() { IntervalMinutes = 0 } };
		var manager = new AgentManager(home, config, new FakeBackend(), new ToolRegistry());
		var agent = manager.CreateChat("fixture", dir.Path);
		if(loaded) {
			// No model calls are needed to seed a durable one-shot. Its runspace ignores
			// cancellation while in a managed call, and has both kinds of child process.
			agent.AddSchedule("""
				$null = process_start -command 'exec /bin/sleep 90' -name fixture
				$p = [System.Diagnostics.Process]::Start('/bin/sleep', '90')
				[IO.File]::AppendAllText((Join-Path $PWD 'runs'), "1`n")
				[IO.File]::WriteAllText((Join-Path $PWD 'foreground.pid'), [string]$p.Id)
				[Threading.Thread]::Sleep(90000)
				""", null, DateTimeOffset.UtcNow, null, 120);
			config.Mcp["stubborn"] = new McpServerConfig {
				Command = "/bin/sh", Cwd = dir.Path,
				Args = ["-c", "echo $$ > mcp.pid; exec /bin/sleep 90"],
			};
		}
		config.Save(home);
		try {
			await using(var server = new Server(home, port, output)) {
				await server.Ready();
				using var events = new ClientWebSocket();
				using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				events.Options.SetRequestHeader("Authorization", "Bearer " + server.Token);
				await events.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/events"), connect.Token);
				if(loaded)
					await Wait.Until(() => FixturePid("foreground.pid") > 0 && FixturePid("mcp.pid") > 0 && DetachedPid() > 0);
				var elapsed = Stopwatch.StartNew();
				Posix.Terminate(server.Pid, group: false);
				// Do not acknowledge any close frame. A passive peer must not hold the host open.
				using var wsDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				try {
					while((await events.ReceiveAsync(new byte[4096], wsDeadline.Token)).MessageType != WebSocketMessageType.Close) { }
				} catch(WebSocketException) when(!wsDeadline.IsCancellationRequested) { }
				output.WriteLine($"WS ended after {elapsed.ElapsedMilliseconds}ms");
				await server.Exited.WaitAsync(TimeSpan.FromSeconds(9));
				output.WriteLine($"SIGTERM exit after {elapsed.ElapsedMilliseconds}ms (loaded={loaded})");
				Assert.InRange(elapsed.ElapsedMilliseconds, 0, loaded ? 9000 : 3000);
				Assert.Equal(0, server.ExitCode);
				var phases = await server.ShutdownTimings();
				output.WriteLine(phases);
				foreach(var phase in new[] { "host-stop end", "host-dispose end", "mcp-stop end", "exit" })
					Assert.Contains(phase, phases);
				if(loaded) Assert.Matches("restart-drain (end|timed out)", phases);
				else Assert.Contains("restart-drain end", phases);
			}
			if(!loaded) return;
			await Wait.Until(() => !Posix.Alive(FixturePid("foreground.pid")) && !Posix.Alive(FixturePid("mcp.pid")), 2000);
			Assert.True(Posix.Alive(DetachedPid()));
			// Don't start a second MCP child: the interrupted schedule is the recovery target.
			config.Mcp.Clear();
			config.Save(home);
			await using var restarted = new Server(home, port, output);
			await restarted.Ready(); // Also proves the same listener can be rebound immediately.
			var schedules = JsonIO.Load<List<Schedule>>(Path.Combine(agent.Directory, "schedules.json"), () => []);
			var recovered = Assert.Single(schedules);
			Assert.True(recovered.Done);
			Assert.False(recovered.InProgress);
			Assert.Equal("interrupted by restart; not re-run", recovered.LastResult);
			Assert.Single(File.ReadAllLines(dir.File("runs")));
			Assert.True(Posix.Alive(DetachedPid()));
			Posix.Terminate(restarted.Pid, group: false);
			await restarted.Exited.WaitAsync(TimeSpan.FromSeconds(3));
			Assert.Equal(0, restarted.ExitCode);
		} finally {
			foreach(var pid in new[] { FixturePid("foreground.pid"), FixturePid("mcp.pid"), DetachedPid() }) {
				if(pid > 0 && Posix.Alive(pid)) Posix.ForceKill(pid, group: false);
			}
		}

		int DetachedPid() => JsonIO.Load<List<BackgroundProcess>>(Path.Combine(agent.Directory, "processes.json"), () => []).SingleOrDefault()?.Pid ?? 0;
		int FixturePid(string file) => File.Exists(dir.File(file)) && int.TryParse(File.ReadAllText(dir.File(file)), out var pid) ? pid : 0;
	}

	static int FreePort() {
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		return ((IPEndPoint) listener.LocalEndpoint).Port;
	}

	sealed class Server : IAsyncDisposable {
		readonly Home home;
		readonly int port;
		readonly Process process;
		readonly Task<string> stderr;
		readonly Task<string> stdout;
		public int Pid => process.Id;
		public int ExitCode => process.ExitCode;
		public Task Exited { get; }
		public string Token => File.ReadAllText(Path.Combine(home.Secrets, "ui-token")).Trim();

		readonly ITestOutputHelper output;

		public Server(Home home, int port, ITestOutputHelper output) {
			this.output = output;
			this.home = home;
			this.port = port;
			var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Marten")) {
				WorkingDirectory = home.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
			};
			psi.Environment["MARTEN_HOME"] = home.Root;
			psi.Environment["AWS_REGION"] = "";
			psi.Environment.Remove("MARTEN_WEB_ROOT");
			process = ChildProcessWatcher.StartExcluded(psi)!;
			stdout = process.StandardOutput.ReadToEndAsync();
			stderr = process.StandardError.ReadToEndAsync();
			Exited = process.WaitForExitAsync();
		}

		public async Task Ready() {
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
			using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(12));
			while(!process.HasExited) {
				try {
					using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/status");
					req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
					using var res = await http.SendAsync(req, ready.Token);
					if(res.IsSuccessStatusCode) return;
				} catch(Exception ex) when(ex is FileNotFoundException or HttpRequestException or OperationCanceledException) { }
				await Task.Delay(30, ready.Token);
			}
			throw new InvalidOperationException($"Fixture exited before it was ready (exit code {ExitCode}).");
		}

		public async Task<string> ShutdownTimings() => string.Join('\n', (await stderr.WaitAsync(TimeSpan.FromSeconds(2))).Split('\n')
			.Where(line => line.StartsWith("Marten shutdown +", StringComparison.Ordinal)));

		public async ValueTask DisposeAsync() {
			try {
				if(!process.HasExited) process.Kill(entireProcessTree: true);
				await Exited.WaitAsync(TimeSpan.FromSeconds(5));
				await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2));
				if(process.ExitCode != 0) output.WriteLine(await ShutdownTimings());
			} finally {
				process.Dispose();
			}
		}
	}
}
