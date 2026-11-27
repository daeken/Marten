using System.Collections.Concurrent;
using Marten.Agents;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class PolicyRefusalTests {
	const string ProviderBody = "private-provider-body";

	sealed class RefusingBackend(string code) : IModelBackend {
		public readonly ConcurrentQueue<ModelRequest> Requests = new();
		public string Provider => "fake";
		public bool Connected => true;
		public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["gpt-6"]);
		public Task<ModelResult> CompleteAsync(ModelRequest request, Action<string> onTextDelta, CancellationToken ct) {
			Requests.Enqueue(request);
			return request.Model == "gpt-6"
				? Task.FromException<ModelResult>(new ModelRefusedException(ProviderBody, ProviderBody, code))
				: Task.FromResult(FakeBackend.Say("Unexpected fallback."));
		}
	}

	[Theory]
	[InlineData(null, "cyber_policy", "cyber_policy")]
	[InlineData("gpt-6", ProviderBody + "_policy", null)]
	public async Task RefusalEndsTheTurnWithoutChangingSettingsOrReplaying(string? model, string code, string? visibleCode) {
		using var dir = new TempDir();
		var backend = new RefusingBackend(code);
		var config = JsonIO.Deserialize<Config>("""{"model":"gpt-6","refusalFallbackModel":"deepseek-flash"}""")!;
		Assert.Equal("deepseek-flash", config.RefusalFallbackModel);
		var manager = new AgentManager(new Home(dir.File("home")), config, backend, new ToolRegistry());
		var (_, parent) = manager.CreateProject("p", [dir.Path], null, false);
		var worker = manager.SpawnTask(parent, "worker", false, model, "high");
		parent.SetClosed(true); // Keep the notification from starting a separate owner turn.
		var settingsPath = Path.Combine(worker.Directory, "agent.json");
		var settingsBefore = File.ReadAllText(settingsPath);
		var task = worker.AddTask("do the thing", parent.Info.Id);

		worker.Send(new Inbound("Sera", "do the thing", SendMode.Queued));
		await Wait.Until(() => worker.Status == AgentStatus.Error && parent.PendingCount == 1);

		Assert.Equal("gpt-6", Assert.Single(backend.Requests).Model);
		Assert.Equal(model, worker.Info.Model);
		Assert.Equal("high", worker.Info.Effort);
		Assert.Equal(settingsBefore, File.ReadAllText(settingsPath));
		Assert.Equal(task, Assert.Single(worker.Tasks));
		Assert.Equal(0, worker.PendingCount);
		Assert.Equal("turn_error", worker.Transcript.Last!.Reason);
		Assert.Equal(ModelCallOutcome.Failed, worker.ModelCall!.Outcome);

		var detail = worker.StatusDetail!;
		Assert.Contains("refused the request on policy grounds", detail);
		Assert.Contains("without retrying or switching models", detail);
		if(visibleCode != null) Assert.Contains($"({visibleCode})", detail);
		Assert.DoesNotContain(ProviderBody, detail);
		Assert.Contains(detail, Assert.Single(worker.Transcript.Entries, e => e.Kind == EntryKind.Error).Text);
		Assert.DoesNotContain(worker.Transcript.Entries, e => e.Text?.Contains(ProviderBody) == true);
		var notification = Assert.Single(manager.AgentMessages(worker.Info.Id));
		Assert.Equal(parent.Info.Id, notification.ToId);
		Assert.Equal("turn_error", notification.Reason);
		Assert.Contains(detail, notification.Text);
		Assert.DoesNotContain("do the thing", notification.Text);
		Assert.DoesNotContain(ProviderBody, notification.Text);

		var json = File.ReadAllText(Path.Combine(worker.Directory, Agent.ModelFailureFile));
		Assert.DoesNotContain(ProviderBody, json);
		var diagnostic = JsonIO.Deserialize<ModelAttemptFailure>(json)!;
		Assert.Equal([nameof(ModelRefusedException)], diagnostic.Causes);
	}
}
