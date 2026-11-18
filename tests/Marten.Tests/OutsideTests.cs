using System.Text.Json;
using Marten.Agents;
using Marten.Storage;
using Marten.Tools;
using Microsoft.AspNetCore.Http;

namespace Marten.Tests;

public class OutsideTests {
	static (AgentManager M, Agent Orch) Setup(TempDir dir) {
		Directory.CreateDirectory(dir.File("repo"));
		AgentManager? m = null;
		Func<AgentManager> get = () => m!;
		var tools = new ToolRegistry().Add(new SendMessageTool(get));
		m = new AgentManager(new Home(dir.File("home")), new Config(), new RoutedBackend(), tools);
		var (_, orch) = m.CreateProject("Open Thedas", [dir.File("repo")], null, false);
		return (m, orch);
	}

	[Fact]
	public void ClientsGetATokenFileThatAuthenticates() {
		using var dir = new TempDir();
		var (m, _) = Setup(dir);
		var c = m.Outside.Register("OpenThedas-CC");
		Assert.Equal("openthedas-cc", c.Name);
		var token = File.ReadAllText(m.Outside.TokenPath(c.Name));
		Assert.Equal("openthedas-cc", m.Outside.Authenticate(token));
		Assert.Null(m.Outside.Authenticate(token + "x"));
		Assert.Null(m.Outside.Authenticate(""));
		Assert.Equal(c, m.Outside.Register("openthedas-cc"));
		Assert.Equal(token, File.ReadAllText(m.Outside.TokenPath(c.Name)));
		m.Outside.Register("openthedas-cc", rotate: true);
		Assert.Null(m.Outside.Authenticate(token));
		Assert.Throws<ArgumentException>(() => m.Outside.Register("no spaces"));
		if(!OperatingSystem.IsWindows())
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(m.Outside.TokenPath(c.Name)));
	}

	[Fact]
	public async Task MessagesFlowBothWays() {
		using var dir = new TempDir();
		var (m, orch) = Setup(dir);
		m.Outside.Register("cc");
		orch.SetPaused(true); // keep it from running; we only look at what arrives

		// By project name, an outside message reaches the orchestrator, labelled as the client and never as Sera.
		var target = m.Outside.Deliver("cc", "open thedas", "Please run the tests.", SendMode.Queued);
		Assert.Equal(orch, target);
		Assert.Equal(1, orch.PendingCount);
		Assert.Contains(m.AgentMessages(), x => x.FromId == "client:cc" && x.ToId == orch.Info.Id);

		// The orchestrator answers with send_message to client:cc; the client reads it through the API.
		var r = await m.Tools.InvokeAsync(orch, "send_message", JsonSerializer.Serialize(new { to = "client:cc", text = "Tests pass.", mode = "queued" }), CancellationToken.None);
		Assert.False(r.IsError, r.Text);
		var inbox = m.Outside.Inbox("cc", ack: true);
		var msg = Assert.Single(inbox);
		Assert.Equal("Tests pass.", msg.Text);
		Assert.Equal(orch.Info.Id, msg.Header("reply_to"));
		Assert.Empty(m.Outside.Inbox("cc", ack: false));

		var unknown = await m.Tools.InvokeAsync(orch, "send_message", JsonSerializer.Serialize(new { to = "client:nope", text = "hi", mode = "queued" }), CancellationToken.None);
		Assert.True(unknown.IsError);
	}

	[Theory]
	[InlineData("GET", "/api/ext/status", true)]
	[InlineData("POST", "/api/ext/messages", true)]
	[InlineData("GET", "/api/agents/x/transcript", true)]
	[InlineData("GET", "/api/messages", true)]
	[InlineData("POST", "/api/agents/x/messages", false)]
	[InlineData("PUT", "/api/backends/anthropic/key", false)]
	[InlineData("GET", "/api/inbox", false)]
	[InlineData("POST", "/api/questions/x/answer", false)]
	[InlineData("GET", "/api/status", false)]
	public void ClientTokensReachOnlyTheirRoutes(string method, string path, bool allowed) {
		var ctx = new DefaultHttpContext();
		ctx.Request.Method = method;
		ctx.Request.Path = path;
		Assert.Equal(allowed, Marten.Web.Security.ClientMayCall(ctx));
	}
}
