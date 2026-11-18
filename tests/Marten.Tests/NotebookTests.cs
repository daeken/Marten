using System.Text.Json;
using System.Text.Json.Nodes;
using Marten.Agents;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class NotebookTests {
	static string Args(string command) => JsonSerializer.Serialize(new { command, timeout_seconds = (int?) null });

	[Fact]
	public async Task RunsAreRecordedAndReproducible() {
		using var dir = new TempDir();
		var tools = new ToolRegistry().Add(PowerShellTests.Tool());
		var m = new AgentManager(new Home(dir.File("home")), new Config(), new RoutedBackend(), tools);
		var chat = m.CreateChat("nb", dir.Path);

		Assert.False((await m.Tools.InvokeAsync(chat, "powershell", Args("$State = @{ n = 1 }; 'set'"), CancellationToken.None)).IsError);
		Assert.False((await m.Tools.InvokeAsync(chat, "powershell", Args("$State.n = $State.n + 1; \"n is $($State.n)\""), CancellationToken.None)).IsError);
		await m.Tools.InvokeAsync(chat, "powershell", Args("[guid]::NewGuid().ToString()"), CancellationToken.None);

		var cells = chat.Notebook();
		Assert.Equal([1, 2, 3], cells.Select(c => c.Id));
		Assert.Null(cells[0].StateIn);
		Assert.Equal(1, cells[1].StateIn!["n"]!.GetValue<int>());
		Assert.Equal(2, cells[1].StateOut!["n"]!.GetValue<int>());
		Assert.Contains("n is 2", cells[1].Output);
		Assert.Equal(dir.Path, cells[1].WorkingDirectory);

		// Re-running cell 2 starts from its recorded $State, not the agent's current one, and leaves the agent's alone.
		var before = chat.PowerShellState!.ToJsonString();
		var (run, sameOutput, sameState) = await chat.RerunCell(2, CancellationToken.None);
		Assert.True(sameOutput, run.Output);
		Assert.True(sameState);
		Assert.Equal(before, chat.PowerShellState!.ToJsonString());
		Assert.Equal(3, chat.Notebook().Count);
		var (_, sameGuid, _) = await chat.RerunCell(3, CancellationToken.None);
		Assert.False(sameGuid);
		await Assert.ThrowsAsync<KeyNotFoundException>(() => chat.RerunCell(99, CancellationToken.None));

		var nb = JsonNode.Parse(chat.NotebookIpynb())!;
		Assert.Equal(4, nb["nbformat"]!.GetValue<int>());
		var code = nb["cells"]!.AsArray().Where(c => c!["cell_type"]!.GetValue<string>() == "code").ToList();
		Assert.Equal(3, code.Count);
		var source = string.Concat(code[1]!["source"]!.AsArray().Select(l => l!.GetValue<string>()));
		Assert.Contains("$State = ConvertFrom-Json -AsHashtable", source);
		Assert.Contains("\"n\":1", source);
		Assert.Contains("$State.n = $State.n + 1", source);
	}
}
