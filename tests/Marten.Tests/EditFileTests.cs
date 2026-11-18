using System.Text.Json.Nodes;
using Marten.Tools;
using E = Marten.Tools.EditFileTool.Edit;

namespace Marten.Tests;

public class EditFileTests {
	[Fact]
	public void AppliesAllEditsAgainstTheOriginal() {
		var result = EditFileTool.Apply("alpha\nbeta\ngamma\n", [
			new E("replace", "beta", "BETA"),
			new E("insert_before", "alpha", "start\n"),
			new E("insert_after", "gamma\n", "end\n"),
			new E("delete", "alpha\n", null),
		]);
		Assert.Equal("start\nBETA\ngamma\nend\n", result);
	}

	[Fact]
	public void InsertionsAtTheSameSpotKeepTheirOrder() {
		var result = EditFileTool.Apply("x", [new E("insert_before", "x", "1"), new E("insert_before", "x", "2")]);
		Assert.Equal("12x", result);
	}

	[Theory]
	[InlineData("missing", "anchor not found")]
	[InlineData("a", "more than once")]
	public void NamesTheEditThatMissed(string anchor, string why) {
		var e = Assert.Throws<ToolException>(() => EditFileTool.Apply("a a b", [new E("replace", "b", "c"), new E("replace", anchor, "z")]));
		Assert.Contains("Edit 2", e.Message);
		Assert.Contains(why, e.Message);
	}

	[Fact]
	public void RejectsOverlappingEdits() {
		var e = Assert.Throws<ToolException>(() => EditFileTool.Apply("hello world", [new E("replace", "hello wo", "x"), new E("replace", "world", "y")]));
		Assert.Contains("overlaps", e.Message);
	}

	static JsonObject Args(string path, params object[] edits) => new() {
		["path"] = path,
		["edits"] = new JsonArray(edits.Select(e => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(e))).ToArray()),
	};

	[Fact]
	public async Task RefusesUnreadOrChangedFiles() {
		using var dir = new TempDir();
		var ctx = new TestContext(dir.Path);
		var tool = new EditFileTool();
		File.WriteAllText(dir.File("f.txt"), "one\n");
		var edit = Args("f.txt", new { op = "replace", anchor = "one", text = "two" });

		await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(ctx, edit, default));
		await new ReadFileTool().InvokeAsync(ctx, new JsonObject { ["path"] = "f.txt" }, default);
		File.WriteAllText(dir.File("f.txt"), "one\nother text\n");
		var stale = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(ctx, edit, default));
		Assert.Contains("changed since you last read", stale.Message);

		await new ReadFileTool().InvokeAsync(ctx, new JsonObject { ["path"] = "f.txt" }, default);
		await tool.InvokeAsync(ctx, edit, default);
		Assert.Equal("two\nother text\n", File.ReadAllText(dir.File("f.txt")));
		// Our own edit counts as having seen the result, so a follow-up edit needs no re-read.
		await tool.InvokeAsync(ctx, Args("f.txt", new { op = "delete", anchor = "other text\n", text = (string?) null }), default);
		Assert.Equal("two\n", File.ReadAllText(dir.File("f.txt")));
	}

	[Fact]
	public async Task CreatesNewFilesOnly() {
		using var dir = new TempDir();
		var ctx = new TestContext(dir.Path);
		var create = Args("sub/new.txt", new { op = "create", anchor = (string?) null, text = "hi\n" });
		await new EditFileTool().InvokeAsync(ctx, create, default);
		Assert.Equal("hi\n", File.ReadAllText(dir.File("sub/new.txt")));
		await Assert.ThrowsAsync<ToolException>(() => new EditFileTool().InvokeAsync(ctx, create, default));
	}

	[Fact]
	public async Task EmptyAnchorInitializesOnlyAnUnchangedReadEmptyFile() {
		using var dir = new TempDir();
		var ctx = new TestContext(dir.Path);
		var tool = new EditFileTool();
		var path = dir.File("empty.md");
		File.WriteAllBytes(path, []);
		var edit = Args("empty.md", new { op = "replace", anchor = "", text = "first line\n" });
		Assert.Contains("Read ", (await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(ctx, edit, default))).Message);
		await new ReadFileTool().InvokeAsync(ctx, new JsonObject { ["path"] = "empty.md" }, default);
		File.WriteAllText(path, "concurrent edit\n");
		Assert.Contains("changed since", (await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(ctx, edit, default))).Message);
		File.WriteAllBytes(path, []);
		await tool.InvokeAsync(ctx, edit, default);
		Assert.Equal("first line\n", File.ReadAllText(path));
		Assert.Contains("no anchor", (await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(ctx, edit, default))).Message);
		Assert.Throws<ToolException>(() => EditFileTool.Apply("", [new E("replace", "", "x"), new E("replace", "", "y")]));
		Assert.Throws<ToolException>(() => EditFileTool.Apply("", [new E("insert_before", "", "x")]));
	}

	[Fact]
	public async Task ReadsRangesWithLineNumbers() {
		using var dir = new TempDir();
		var ctx = new TestContext(dir.Path);
		File.WriteAllText(dir.File("f.txt"), string.Join("\n", Enumerable.Range(1, 12).Select(i => $"line {i}")) + "\n");
		var r = await new ReadFileTool().InvokeAsync(ctx, new JsonObject { ["path"] = "f.txt", ["start_line"] = 9, ["end_line"] = 10 }, default);
		Assert.Equal(" 9\tline 9\n10\tline 10\n[lines 9-10 of 12]\n", r.Text);
	}
}

public class EditFileConcurrencyTests {
	[Fact]
	public async Task ConcurrentEditorsCannotEraseEachOther() {
		using var dir = new TempDir();
		var path = dir.File("shared.txt");
		File.WriteAllText(path, "base\n");
		// Many agents read the same version, then all edit at once. Exactly one may win.
		var agents = Enumerable.Range(0, 24).Select(_ => new TestContext(dir.Path)).ToList();
		foreach(var a in agents) await new ReadFileTool().InvokeAsync(a, new System.Text.Json.Nodes.JsonObject { ["path"] = "shared.txt" }, default);
		var start = new TaskCompletionSource();
		var tasks = agents.Select((a, i) => Task.Run(async () => {
			await start.Task;
			var args = System.Text.Json.Nodes.JsonNode.Parse($$"""{"path":"shared.txt","edits":[{"op":"insert_after","anchor":"base\n","text":"agent {{i}}\n"}]}""")!.AsObject();
			try {
				await new EditFileTool().InvokeAsync(a, args, default);
				return true;
			} catch(ToolException) {
				return false;
			}
		})).ToList();
		start.SetResult();
		var results = await Task.WhenAll(tasks);
		Assert.Equal(1, results.Count(ok => ok));
		Assert.Equal(2, File.ReadAllLines(path).Length);
	}
}
