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
	[InlineData(null, false, "https://standalone.example:8443", true)]
	[InlineData("Marten.example.ts.net", true, null, false)]
	[InlineData(Host, true, "https://" + Host, false)]
	public async Task StandaloneOriginIsNeverAnEmbeddedFallback(string? tailnetHost, bool embedded, string? configuredOrigin, bool enabled) {
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var config = new Config { PairingOrigin = "https://standalone.example:8443",
			Tailnet = embedded ? new() { Host = tailnetHost } : null };
		var transport = new TailnetTransport(config);
		var security = new Security(home, config, transport);
		Assert.Equal(configuredOrigin, security.ConfiguredPairingOrigin?.GetLeftPart(UriPartial.Authority));
		Assert.Equal(enabled, security.PairingOrigin != null);
		var ctx = new DefaultHttpContext();
		ctx.Request.Host = new HostString("standalone.example:8443");
		await security.Middleware(c => { c.Response.StatusCode = 204; return Task.CompletedTask; })(ctx);
		Assert.Equal(403, ctx.Response.StatusCode); // direct pairing authority also never serves the web shell
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
		var transport = new TailnetTransport(config);
		Assert.False(transport.IsReady);
		var task = transport.RunAsync(home, line => ready.TrySetResult(line), stop.Token);
		Assert.Equal($"Native tailnet transport ready: https://{Host}", await ready.Task.WaitAsync(stop.Token));
		Assert.True(transport.IsReady);
		Assert.Equal(new[] { Host, "17450", Path.Combine(home.Secrets, "tailnet-state"), Path.Combine(home.Secrets, "tailnet-auth-key") },
			await File.ReadAllLinesAsync(Path.Combine(home.Root, "args"), stop.Token));
		stop.Cancel();
		await task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(transport.IsReady);
		Assert.Equal("eof", await File.ReadAllTextAsync(Path.Combine(home.Root, "stopped")));
	}

	[Fact]
	public async Task MissingHelperLeavesLocalMartenAvailable() {
		if(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var logs = new List<string>();
		await new TailnetTransport(new Config()).RunAsync(home, logs.Add, CancellationToken.None);
		Assert.Empty(logs);
		await new TailnetTransport(new Config { Tailnet = new TailnetSettings { Host = Host } }).RunAsync(home, logs.Add, CancellationToken.None);
		Assert.Contains("MARTEN_HOME/bin/marten-tsnet", Assert.Single(logs));
	}
}
