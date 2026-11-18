using Marten.Tools;

namespace Marten.Tests;

public class ToolSearchTests {
	[Fact]
	public async Task PastTheDirectLimitMcpToolsAreFoundBySearchAndCalledByName() {
		using var dir = new TempDir();
		var (m, hub) = McpTests.Manager(dir, new FakeBackend(), directLimit: 2);
		await hub.RefreshAsync(false, default);
		var a = m.CreateChat("a", dir.Path);
		try {
			Assert.DoesNotContain(a.RequestTools(), t => t.Name.StartsWith("fake__"));
			Assert.Contains(a.RequestTools(), t => t.Name == "tool_search");

			var overview = await m.Tools.InvokeAsync(a, "tool_search", """{"query":"","limit":null}""", default);
			Assert.Contains("fake: 3 tools", overview.Text);
			var found = await m.Tools.InvokeAsync(a, "tool_search", """{"query":"echoes text","limit":null}""", default);
			Assert.StartsWith("## fake__echo", found.Text);
			Assert.Contains("\"required\":[\"text\"]", found.Text);
			// Words that don't match don't sink a tool that matches others.
			var loose = await m.Tools.InvokeAsync(a, "tool_search", "{\"query\":\"pixel image screenshot\",\"limit\":null}", default);
			Assert.StartsWith("## fake__pixel", loose.Text);
			var all = await m.Tools.InvokeAsync(a, "tool_search", """{"query":"server:fake","limit":1}""", default);
			Assert.Equal(3, all.Text.Split("## ").Length - 1);

			var echoed = await m.Tools.InvokeAsync(a, "call_tool", """{"name":"fake__echo","arguments":{"text":"hi"}}""", default);
			Assert.StartsWith("hi (pid ", echoed.Text);
			var pixel = await m.Tools.InvokeAsync(a, "call_tool", """{"name":"fake__pixel","arguments":{}}""", default);
			Assert.Single(pixel.Images!);
			Assert.True((await m.Tools.InvokeAsync(a, "call_tool", """{"name":"nope","arguments":{}}""", default)).IsError);
		} finally {
			await hub.StopFor(a.Info.Id);
		}

		// Under the limit they stay direct.
		using var dir2 = new TempDir();
		var (m2, hub2) = McpTests.Manager(dir2, new FakeBackend());
		await hub2.RefreshAsync(false, default);
		Assert.Contains(m2.CreateChat("b", dir2.Path).RequestTools(), t => t.Name == "fake__echo");
	}
}

public class McpProcessTests {
	[Fact]
	public async Task ACommandTimingOutWhileAServerStartsDoesNotKillTheServer() {
		using var dir = new TempDir();
		var (m, hub) = McpTests.Manager(dir, new FakeBackend());
		await hub.RefreshAsync(false, default);
		var a = m.CreateChat("a", dir.Path);
		var b = m.CreateChat("b", dir.Path);
		try {
			var sleeping = m.Tools.InvokeAsync(a, "powershell", """{"command":"Start-Sleep 30","timeout_seconds":3}""", default);
			await Task.Delay(500);
			// b's server starts while a's command is the only one running, so a naive watcher would pin it on a.
			var first = await m.Tools.InvokeAsync(b, "fake__echo", """{"text":"x"}""", default);
			var timedOut = await sleeping;
			Assert.Contains("Timed out", timedOut.Text);
			var second = await m.Tools.InvokeAsync(b, "fake__echo", """{"text":"x"}""", default);
			Assert.Equal(first.Text, second.Text);
		} finally {
			await hub.StopFor(b.Info.Id);
		}
	}
}
