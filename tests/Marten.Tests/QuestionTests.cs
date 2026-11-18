using System.Text.Json.Nodes;
using Marten.Agents;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class QuestionTests {
	static AgentManager Manager(TempDir dir, IModelBackend backend) {
		AgentManager? m = null;
		Func<AgentManager> get = () => m!;
		var tools = new ToolRegistry().Add(new SendMessageTool(get)).Add(new SpawnAgentTool(get)).Add(new AskSeraTool(get));
		return m = new AgentManager(new Home(dir.File("home")), new Config(), backend, tools);
	}

	static JsonObject Ask(string q, params string[] options) => new() {
		["question"] = q, ["options"] = options.Length == 0 ? null : new JsonArray(options.Select(o => (JsonNode) o).ToArray()), ["context"] = "because",
	};

	[Fact]
	public async Task OrchestratorsAskOnACardAndTheAnswerComesBackQueued() {
		using var dir = new TempDir();
		Directory.CreateDirectory(dir.File("repo"));
		var backend = new FakeBackend { Gate = new() };
		var m = Manager(dir, backend);
		var (_, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		var task = m.SpawnTask(orch, "t", false, null, null);
		Assert.DoesNotContain(m.Tools.For(task), t => t.Name == "ask_sera");

		var r = await m.Tools.InvokeAsync(orch, "ask_sera", Ask("Ship it?", "Yes", "No").ToJsonString(), default);
		Assert.False(r.IsError, r.Text);
		var q = m.SeraInbox().Single();
		Assert.True(q.Question);
		Assert.Equal(["Yes", "No"], q.Options!);
		Assert.Equal("because", q.Context);

		Assert.Throws<ArgumentException>(() => m.AnswerQuestion(q.Id, null, "  "));
		var answered = m.AnswerQuestion(q.Id, 0, "after the tests pass");
		Assert.Equal("Yes: after the tests pass", answered.Answer);
		Assert.Throws<InvalidOperationException>(() => m.AnswerQuestion(q.Id, 1, null));
		Assert.Equal("Yes: after the tests pass", m.SeraInbox().Single().Answer);

		// The asker is woken with reason "answer".
		backend.Script.Enqueue(_ => FakeBackend.Say("shipping"));
		backend.Gate.SetResult();
		await Wait.Until(() => orch.Transcript.Last?.Kind == EntryKind.Idle);
		Assert.Equal("answer", orch.Transcript.Entries.First(e => e.Kind == EntryKind.Wake).Reason);
		Assert.Contains(orch.Transcript.Entries, e => e.From == "Sera" && e.Text!.Contains("Ship it?") && e.Text.Contains("after the tests pass"));

		Assert.True((await m.Tools.InvokeAsync(orch, "ask_sera", Ask("x", "only one").ToJsonString(), default)).IsError);
		var free = await m.Tools.InvokeAsync(m.CreateChat("c", dir.Path), "ask_sera", Ask("What name?").ToJsonString(), default);
		Assert.False(free.IsError, free.Text);
		Assert.Equal("the short one", m.AnswerQuestion(m.SeraInbox().Last().Id, null, "the short one").Answer);
	}

	[Fact]
	public async Task EveryMessageBetweenAgentsIsLogged() {
		using var dir = new TempDir();
		Directory.CreateDirectory(dir.File("repo"));
		var backend = new FakeBackend { Gate = new() };
		var m = Manager(dir, backend);
		var (project, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		await m.Tools.InvokeAsync(orch, "spawn_agent",
			"""{"name":"parser","brief":"Write the parser.","task":null,"worktree":null,"model":null,"effort":null}""", default);
		var task = m.Agents.Single(a => a.Info.Kind == AgentKind.Task);
		await m.Tools.InvokeAsync(task, "send_message", $$"""{"to":"{{orch.Info.Id}}","text":"done","mode":"queued"}""", default);
		var chat = m.CreateChat("c", dir.Path);
		chat.Send(new Inbound("Sera", "not logged: Sera's own words show in transcripts", SendMode.Queued));

		var all = m.AgentMessages();
		Assert.Equal(2, all.Count);
		Assert.Equal(("Orchestrator", "parser", "Write the parser.", SendMode.Queued), (all[0].FromName, all[0].ToName, all[0].Text, all[0].Mode));
		Assert.Equal((task.Info.Id, orch.Info.Id, project.Info.Id), (all[1].FromId, all[1].ToId, all[1].ProjectId));
		Assert.Single(m.AgentMessages(agentId: task.Info.Id, limit: 1));
		Assert.Empty(m.AgentMessages(projectId: "other"));
	}
}
