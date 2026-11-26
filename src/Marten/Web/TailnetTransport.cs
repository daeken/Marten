using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Marten.Storage;

namespace Marten.Web;

/// <summary>Explicit opt-in for native tailnet routing. Security still authenticates and authorizes the request.</summary>
public sealed class TailnetApi;

public static class TailnetTransport {
	public static RequestDelegate RequireNativeEndpoint(string host, RequestDelegate next) => ctx => {
		if(ctx.Request.Host.Value == host && ctx.GetEndpoint()?.Metadata.GetMetadata<TailnetApi>() == null) {
			ctx.Response.StatusCode = StatusCodes.Status404NotFound;
			return Task.CompletedTask;
		}
		return next(ctx);
	};

	public static async Task RunAsync(Home home, Config config, Action<string> log, CancellationToken stopping) {
		if(config.Tailnet == null || stopping.IsCancellationRequested) return;
		if(!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) {
			log("Tailnet transport requires macOS or Linux.");
			return;
		}
		if(config.Tailnet.CanonicalHost() is not { } host) {
			log("Tailnet transport requires an exact lowercase device.tailnet.ts.net host.");
			return;
		}
		var start = new ProcessStartInfo(Path.Combine(home.Root, "bin", "marten-tsnet")) {
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = home.Root,
		};
		// Do not pass Marten/model credentials, machine Tailscale settings, or proxy overrides to the helper.
		start.Environment.Clear();
		foreach(var arg in new[] { host, config.Port.ToString(CultureInfo.InvariantCulture),
			Path.Combine(home.Secrets, "tailnet-state"), Path.Combine(home.Secrets, "tailnet-auth-key") })
			start.ArgumentList.Add(arg);
		try {
			using var process = Process.Start(start)!;
			var output = DrainOutputAsync(process.StandardOutput.BaseStream, host, log, stopping);
			var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, stopping);
			try {
				await process.WaitForExitAsync(stopping);
				await Task.WhenAll(output, errors);
				if(!stopping.IsCancellationRequested) log($"Tailnet transport stopped: {Failure(process.ExitCode)} Local Marten is still available.");
			} catch(OperationCanceledException) when(stopping.IsCancellationRequested) {
				// EOF is the helper's lifetime lease; it also fires if Marten crashes.
				process.StandardInput.Close();
				using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
				try { await process.WaitForExitAsync(grace.Token); }
				catch(OperationCanceledException) {
					try { process.Kill(entireProcessTree: true); }
					catch(InvalidOperationException) when(process.HasExited) { }
					await process.WaitForExitAsync();
				}
				try { await Task.WhenAll(output, errors); }
				catch(OperationCanceledException) { }
			}
		} catch(Exception e) when(e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException) {
			log("Tailnet helper could not run. Check the executable at MARTEN_HOME/bin/marten-tsnet and its permissions.");
		}
	}

	static async Task DrainOutputAsync(Stream stream, string host, Action<string> log, CancellationToken stopping) {
		// Protocol is one fixed-size marker, never echo text from the child.
		var ready = new byte[6];
		if(await stream.ReadAtLeastAsync(ready, ready.Length, false, stopping) == ready.Length
			&& ready.AsSpan().SequenceEqual("READY\n"u8)) log($"Native tailnet transport ready: https://{host}");
		await stream.CopyToAsync(Stream.Null, stopping);
	}

	static string Failure(int code) => code switch {
		10 => "invalid transport configuration or helper build.",
		11 => "the private tailnet state directory is insecure, inaccessible or in use.",
		12 => "install a private device enrollment key in secrets/tailnet-auth-key (needed for first use).",
		13 => "could not connect to Tailscale; check connectivity, device approval and enrollment.",
		14 => "the actual device DNS name does not match tailnet.host.",
		15 => "enable MagicDNS and HTTPS certificates in the tailnet.",
		16 => "could not obtain a TLS certificate for the configured device DNS name.",
		17 => "could not serve HTTPS on the private tailnet listener.",
		_ => "helper exited unexpectedly.",
	};
}
