using Marten.Storage;
using Marten.Web;
using Microsoft.AspNetCore.Http;

namespace Marten.Tests;

public class TailnetTransportTests {
	const string Host = "marten.example.ts.net";

	[Theory]
	[InlineData(Host, true)]
	[InlineData("marten.example.ts.net.", false)]
	[InlineData("https://marten.example.ts.net", false)]
	[InlineData("marten.example.ts.net:443", false)]
	[InlineData("marten.example.ts.net/path", false)]
	[InlineData("Marten.example.ts.net", false)]
	[InlineData("127.0.0.1", false)]
	public void OnlyCanonicalDeviceNames(string host, bool valid) =>
		Assert.Equal(valid ? host : null, new TailnetSettings { Host = host }.CanonicalHost());

	[Theory]
	[InlineData(Host, "/", false, 404)]
	[InlineData(Host, "/sw.js", false, 404)]
	[InlineData(Host, "/api/ext/whoami", false, 404)]
	[InlineData(Host, "/api/events", false, 404)]
	[InlineData(Host, "/api/events", true, 204)]
	[InlineData("127.0.0.1:7450", "/", false, 204)]
	public async Task TailnetRequiresEndpointMetadata(string host, string path, bool native, int status) {
		var ctx = new DefaultHttpContext();
		ctx.Request.Host = new HostString(host);
		ctx.Request.Path = path;
		if(native) ctx.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new TailnetApi()), null));
		await TailnetTransport.RequireNativeEndpoint(Host, next => {
			next.Response.StatusCode = StatusCodes.Status204NoContent;
			return Task.CompletedTask;
		})(ctx);
		Assert.Equal(status, ctx.Response.StatusCode);
	}

	[Fact]
	public async Task SupervisorUsesPrivatePathsAndEndsHelpersOnStop() {
		if(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
		using var dir = new TempDir();
		var home = new Home(dir.File("scratch home"));
		Directory.CreateDirectory(Path.Combine(home.Root, "bin"));
		var helper = Path.Combine(home.Root, "bin", "marten-tsnet");
		await File.WriteAllTextAsync(helper, """
			#!/bin/sh
			printf '%s\n' "$@" > args
			printf 'fake-sensitive-output\n' >&2
			printf 'READY\n'
			read unused
			printf 'eof' > stopped
			""");
		File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		await File.WriteAllTextAsync(Path.Combine(home.Secrets, "tailnet-auth-key"), "fake-secret-key");
		using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		var config = new Config { Port = 17450, Tailnet = new TailnetSettings { Host = Host } };
		var task = TailnetTransport.RunAsync(home, config, line => ready.TrySetResult(line), stop.Token);
		Assert.Equal($"Native tailnet transport ready: https://{Host}", await ready.Task.WaitAsync(stop.Token));
		Assert.Equal(new[] { Host, "17450", Path.Combine(home.Secrets, "tailnet-state"), Path.Combine(home.Secrets, "tailnet-auth-key") },
			await File.ReadAllLinesAsync(Path.Combine(home.Root, "args"), stop.Token));
		stop.Cancel();
		await task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal("eof", await File.ReadAllTextAsync(Path.Combine(home.Root, "stopped")));
	}

	[Fact]
	public async Task MissingHelperLeavesLocalMartenAvailable() {
		if(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var logs = new List<string>();
		await TailnetTransport.RunAsync(home, new Config(), logs.Add, CancellationToken.None);
		Assert.Empty(logs);
		await TailnetTransport.RunAsync(home, new Config { Tailnet = new TailnetSettings { Host = Host } }, logs.Add, CancellationToken.None);
		Assert.Contains("MARTEN_HOME/bin/marten-tsnet", Assert.Single(logs));
	}
}
