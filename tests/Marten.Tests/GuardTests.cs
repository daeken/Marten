using System.Text.Json;
using Marten.Agents;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class GuardTests {
	static string Args(object o) => JsonSerializer.Serialize(o);

	static (AgentManager M, Agent Chat) Setup(TempDir dir, Config config) {
		AgentManager? m = null;
		Func<AgentManager> get = () => m!;
		var source = new ScriptToolSource(get);
		var tools = new ToolRegistry().Add(new ReadFileTool()).Add(new CallToolTool()).Add(new DefineToolTool(get, source)).AddSource(source);
		m = new AgentManager(new Home(dir.File("home")), config, new RoutedBackend(), tools);
		var chat = m.CreateChat("c");
		File.WriteAllText(dir.File("ok.txt"), "fine");
		File.WriteAllText(dir.File("secret.txt"), "hidden");
		return (m, chat);
	}

	[Fact]
	public async Task DenyRulesBlockMatchingCallsOnly() {
		using var dir = new TempDir();
		var (m, chat) = Setup(dir, new Config { Rules = [
			new GuardRule { Tool = "read_*", Match = "secret", Action = GuardAction.Deny, Reason = "no secrets" },
			new GuardRule { Tool = "read_file", Agents = [AgentKind.Task], Action = GuardAction.Deny },
		] });
		var denied = await m.Tools.InvokeAsync(chat, "read_file", Args(new { path = dir.File("secret.txt") }), CancellationToken.None);
		Assert.True(denied.IsError);
		Assert.Contains("no secrets", denied.Text);
		var ok = await m.Tools.InvokeAsync(chat, "read_file", Args(new { path = dir.File("ok.txt") }), CancellationToken.None);
		Assert.False(ok.IsError, ok.Text);
		Assert.Contains("fine", ok.Text);
		// Through call_tool, the inner call is what's checked.
		var via = await m.Tools.InvokeAsync(chat, "call_tool", Args(new { name = "read_file", arguments = JsonSerializer.Serialize(new { path = dir.File("secret.txt") }) }), CancellationToken.None);
		Assert.True(via.IsError);
		Assert.Contains("no secrets", via.Text);
	}

	[Fact]
	public async Task AskRulesPutACardToSeraAndAllowOnce() {
		using var dir = new TempDir();
		var (m, chat) = Setup(dir, new Config { Rules = [new GuardRule { Tool = "read_file", Action = GuardAction.Ask, Reason = "reads need a look" }] });
		var args = Args(new { path = dir.File("ok.txt") });
		var first = await m.Tools.InvokeAsync(chat, "read_file", args, CancellationToken.None);
		Assert.True(first.IsError);
		Assert.Contains("needs Sera's approval", first.Text);
		var card = Assert.Single(m.SeraInbox(), q => q.Kind == "approval");
		Assert.Contains("ok.txt", card.Context);
		Assert.Contains("Still waiting", (await m.Tools.InvokeAsync(chat, "read_file", args, CancellationToken.None)).Text);
		Assert.Single(m.SeraInbox(), q => q.Kind == "approval");

		m.AnswerQuestion(card.Id, 0, null);
		var allowed = await m.Tools.InvokeAsync(chat, "read_file", args, CancellationToken.None);
		Assert.False(allowed.IsError, allowed.Text);
		// Allowed once: the next identical call asks again.
		Assert.True((await m.Tools.InvokeAsync(chat, "read_file", args, CancellationToken.None)).IsError);
		var second = m.SeraInbox().Last(q => q.Kind == "approval");
		m.AnswerQuestion(second.Id, 1, "not now");
		var denied = await m.Tools.InvokeAsync(chat, "read_file", args, CancellationToken.None);
		Assert.Contains("Sera denied", denied.Text);
		Assert.Contains("not now", denied.Text);
	}

	[Fact]
	public async Task ReviewedToolsLoadOnlyOnceApproved() {
		using var dir = new TempDir();
		var (m, chat) = Setup(dir, new Config { ReviewTools = true });
		var global = Path.Combine(m.Home.Root, "tools");
		Directory.CreateDirectory(global);
		const string Old = "<#\n.SYNOPSIS\nAn old tool.\n#>\nparam()\n'old'";
		File.WriteAllText(Path.Combine(global, "old_tool.ps1"), Old);
		Assert.Contains(m.Tools.For(chat), t => t.Name == "old_tool");

		const string Script = "<#\n.SYNOPSIS\nSays hi.\n#>\nparam()\n'hi'";
		var r = await m.Tools.InvokeAsync(chat, "define_tool", Args(new { name = "say_hi", script = Script, scope = "global" }), CancellationToken.None);
		Assert.False(r.IsError, r.Text);
		Assert.Contains("can't be called until she approves", r.Text);
		Assert.DoesNotContain(m.Tools.For(chat), t => t.Name == "say_hi");
		var card = Assert.Single(m.SeraInbox(), q => q.Kind == "tool_review");
		Assert.Contains("'hi'", card.Context);
		m.AnswerQuestion(card.Id, 0, null);
		Assert.Contains(m.Tools.For(chat), t => t.Name == "say_hi");

		// An edit made outside define_tool drops it until it's reviewed again.
		File.WriteAllText(Path.Combine(global, "say_hi.ps1"), Script + "\n'more'");
		Assert.DoesNotContain(m.Tools.For(chat), t => t.Name == "say_hi");

		await m.Tools.InvokeAsync(chat, "define_tool", Args(new { name = "bad_idea", script = Script, scope = "global" }), CancellationToken.None);
		var reject = m.SeraInbox().Last(q => q.Kind == "tool_review");
		m.AnswerQuestion(reject.Id, 1, null);
		Assert.False(File.Exists(Path.Combine(global, "bad_idea.ps1")));
	}
}
