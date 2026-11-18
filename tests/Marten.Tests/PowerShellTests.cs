using System.Diagnostics;
using Marten.Tools;

namespace Marten.Tests;

public class PowerShellTests {
	static readonly ChildProcessWatcher Watcher = new();
	internal static PowerShellTool Tool(int timeout = 30) => new(() => timeout, () => 3, Watcher);

	[Fact]
	public async Task RendersStringsObjectsAndErrorsInline() {
		using var dir = new TempDir();
		var ctx = new TestContext(dir.Path);
		var r = await Tool().RunAsync(ctx, "'plain'; [pscustomobject]@{ A = 1; B = 'x' } | Format-Table; Write-Warning 'careful'; Write-Error 'bad'; Write-Host 'host'", TimeSpan.FromSeconds(30), default);
		Assert.Contains("plain\n", r.Text);
		Assert.Matches(@"A\s+B", r.Text);
		Assert.Contains("WARNING: careful", r.Text);
		Assert.Contains("bad", r.Text);
		Assert.Contains("host", r.Text);
		Assert.True(r.Text.IndexOf("plain") < r.Text.IndexOf("careful"));
	}

	[Fact]
	public async Task StartsInTheWorkingDirectory() {
		using var dir = new TempDir();
		var r = await Tool().RunAsync(new TestContext(dir.Path), "(Get-Location).Path", TimeSpan.FromSeconds(30), default);
		Assert.Equal(Path.GetFullPath(dir.Path), r.Text.Trim());
	}

	[Fact]
	public async Task OnlyStateCarriesOver() {
		using var dir = new TempDir();
		var ctx = new TestContext(dir.Path);
		await Tool().RunAsync(ctx, "$State = @{ count = 1; items = @('a') }; $other = 5", TimeSpan.FromSeconds(30), default);
		var r = await Tool().RunAsync(ctx, "$State.count += 1; $State.items += 'b'; \"$($State.count) $($State.items -join ',') [$other]\"", TimeSpan.FromSeconds(30), default);
		Assert.Equal("2 a,b []", r.Text.Trim());
		Assert.Equal(2, ctx.PowerShellState!["count"]!.GetValue<long>());
	}

	[Fact]
	public async Task NonJsonStateIsALoudError() {
		using var dir = new TempDir();
		var ctx = new TestContext(dir.Path);
		await Tool().RunAsync(ctx, "$State = @{ ok = $true }", TimeSpan.FromSeconds(30), default);
		var r = await Tool().RunAsync(ctx, "$State = Get-Item .", TimeSpan.FromSeconds(30), default);
		Assert.True(r.IsError);
		Assert.Contains("DirectoryInfo", r.Text);
		Assert.True(ctx.PowerShellState!["ok"]!.GetValue<bool>());
	}

	[Fact]
	public async Task ToolsAreCallableAsFunctions() {
		using var dir = new TempDir();
		File.WriteAllText(dir.File("f.txt"), "hello\n");
		var ctx = new TestContext(dir.Path);
		var r = await Tool().RunAsync(ctx, "read_file -path f.txt", TimeSpan.FromSeconds(30), default);
		Assert.Contains("1\thello", r.Text);
		Assert.NotNull(ctx.Files.LastSeen(dir.File("f.txt")));
	}

	[Fact]
	public async Task NativeOutputAndExitCodes() {
		using var dir = new TempDir();
		var r = await Tool().RunAsync(new TestContext(dir.Path), "sh -c 'echo out; echo err >&2; exit 3'", TimeSpan.FromSeconds(30), default);
		Assert.Contains("out", r.Text);
		Assert.Contains("err", r.Text);
		Assert.Contains("[exit code 3]", r.Text);
	}
}

/// <summary>
/// A child is killed on timeout only when its command was the only one running, so these run alone:
/// commands from tests running alongside would make every child ambiguous.
/// </summary>
[CollectionDefinition("Process attribution", DisableParallelization = true)]
public class ProcessAttributionCollection;

[Collection("Process attribution")]
public class PowerShellTimeoutTests {
	static PowerShellTool Tool() => PowerShellTests.Tool();

	[Fact]
	public async Task TimeoutKillsChildProcesses() {
		using var dir = new TempDir();
		var ctx = new TestContext(dir.Path);
		var sw = Stopwatch.StartNew();
		var r = await Tool().RunAsync(ctx, "sleep 60", TimeSpan.FromSeconds(1), default);
		Assert.True(r.IsError);
		Assert.Contains("Timed out", r.Text);
		Assert.Contains("killed child processes", r.Text);
		Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
		Assert.Equal(0, ctx.Abandoned);
	}
}
