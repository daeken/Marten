using System.Net;
using System.Text;
using System.Text.Json;
using Marten.Agents;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public sealed class JevIntegrationTests {
	const string Key = "fake-jev-fixture-key";
	const string Args = """{"mode":"decide","state":"Explicit facts","question":"Has Quartz shipped?"}""";

	sealed class Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, Action<JevSettings>? beforeAdmission = null) : IDisposable {
		readonly TempDir temp = new();
		internal readonly FakeBackend Backend = new();
		internal readonly ToolRegistry Tools = new();
		internal JevSettings Settings { get; private set; } = null!;
		internal UsageLedger Ledger { get; private set; } = null!;
		internal Home Home { get; private set; } = null!;
		internal AgentManager Manager { get; private set; } = null!;
		internal int Calls;
		internal void Start(Config? config = null, Artifacts? store = null, Home? home = null) {
			Home = home ?? new Home(temp.File("home"));
			config ??= new();
			Settings = new(Home);
			Ledger = new(Home, () => config);
			var transport = new JevClient(() => {
				beforeAdmission?.Invoke(Settings);
				return new Handler((request, ct) => { Interlocked.Increment(ref Calls); return send(request, ct); });
			}, TimeSpan.FromSeconds(5));
			Tools.Add(new JevTool(Settings, transport, Ledger, store, default)).Add(new CallToolTool());
			Manager = new AgentManager(Home, config, Backend, Tools) { ArtifactStore = store };
		}
		internal Agent Agent() => Manager.CreateChat("Jev fixture", temp.Path);
		internal void TurnOn(bool images = false) {
			Assert.Equal(JevChange.Saved, Settings.Replace(Key));
			Assert.Equal(JevChange.Saved, Settings.SetOptions(true, images));
		}
		internal UsageTotals Usage() => JsonIO.Load<Dictionary<string, Dictionary<string, Dictionary<string, UsageTotals>>>>(
			Path.Combine(Home.Root, "usage.json"), () => new())[UsageLedger.Today]["jev"][JevClient.Model];
		public void Dispose() => temp.Dispose();
	}

	sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
	}

	static HttpResponseMessage Reply(string label) {
		string[] labels = ["yes", "no", "need more information", "undecidable"];
		return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
			model = JevClient.Model,
			answers = new { q1 = new { type = "choice", choice = label, confidence = 0.9, probabilities = labels.ToDictionary(x => x, x => x == label ? 1 : 0) } },
			usage = new { input_tokens = 10, output_tokens = 2 }
		}), Encoding.UTF8, "application/json") };
	}

	[Fact]
	public async Task GlobalAvailabilityAndNormalDispatchPathsUseSameAuthorityAndAccounting() {
		using var f = new Fixture(async (request, ct) => {
			Assert.Equal(Key, request.Headers.Authorization?.Parameter);
			using var wire = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(ct));
			Assert.Equal("Explicit facts", wire.RootElement.GetProperty("state").GetString());
			Assert.Equal(3, wire.RootElement.GetPropertyCount());
			Assert.DoesNotContain(Key, wire.RootElement.GetRawText());
			return Reply("undecidable");
		});
		f.Start();
		var a = f.Agent();
		var b = f.Agent();
		Assert.DoesNotContain(a.RequestTools(), s => s.Name == "jev");
		Assert.True((await f.Tools.InvokeAsync(b, "jev", Args, default)).IsError);
		Assert.Equal(JevChange.Saved, f.Settings.Replace(Key));
		Assert.True((await f.Tools.InvokeAsync(a, "jev", Args, default)).IsError);
		Assert.Equal(0, f.Calls);
		f.TurnOn();
		Assert.Contains(a.RequestTools(), s => s.Name == "jev");
		Assert.Contains(b.RequestTools(), s => s.Name == "jev");
		Assert.Contains("unavailable", (await f.Tools.InvokeAsync(a, "jev", Args, default)).Text);
		var delegated = JsonSerializer.Serialize(new { name = "jev", arguments = JsonDocument.Parse(Args).RootElement });
		Assert.Contains("unavailable", (await f.Tools.InvokeAsync(b, "call_tool", delegated, default)).Text);
		var ps = new MartenApi(a, f.Manager, default, manualReplay: true);
		var dictionary = new System.Collections.Hashtable { ["mode"] = "decide", ["state"] = "Explicit facts", ["question"] = "Has Quartz shipped?" };
		Assert.Contains("unavailable", ps.CallTool("jev", dictionary));
		Assert.Equal(3, f.Calls);
		Assert.Equal(30L, f.Usage().Input);
		Assert.Equal(0L, f.Usage().UnknownCalls);
		Assert.Equal(JevChange.Saved, f.Settings.Remove());
		Assert.Throws<ArgumentException>(() => ps.CallTool("jev", dictionary));
		Assert.True((await f.Tools.InvokeAsync(b, "call_tool", delegated, default)).IsError);
		Assert.Equal(3, f.Calls);
	}

	[Theory]
	[InlineData("yes", false)]
	[InlineData("no", false)]
	[InlineData("need more information", true)]
	[InlineData("undecidable", true)]
	[InlineData("failure", false)]
	public async Task AdmittedViewProducesOnlyReferencesForExactUncertainty(string label, bool references) {
		using var f = new Fixture(async (request, ct) => {
			var wire = await request.Content!.ReadAsStringAsync(ct);
			Assert.DoesNotContain("Private evidence", wire);
			if(label == "failure") throw new HttpRequestException("provider body marker");
			return Reply(label);
		});
		f.Start();
		f.TurnOn();
		var a = f.Agent();
		f.Backend.Script.Enqueue(_ => FakeBackend.Call("jev-test", "jev", Args));
		f.Backend.Script.Enqueue(_ => FakeBackend.Say("done"));
		a.Send(new Inbound("Sera", "Private evidence: Quartz arrived", SendMode.Queued));
		await Wait.Until(() => a.Transcript.Last?.Kind == EntryKind.Idle);
		var rows = a.Transcript.Entries;
		var result = Assert.Single(rows, row => row.Kind == EntryKind.ToolResult);
		Assert.DoesNotContain("Private evidence", result.Text);
		Assert.DoesNotContain(Key, result.Text);
		Assert.DoesNotContain("provider body marker", result.Text);
		Assert.Equal(label == "failure", result.IsError == true);
		if(references) {
			using var json = JsonDocument.Parse(result.Text!);
			var reference = Assert.Single(json.RootElement.GetProperty("evidence").GetProperty("references").EnumerateArray());
			Assert.Equal(Assert.Single(rows, row => row.Text == "Private evidence: Quartz arrived").Seq, reference.GetProperty("seq").GetInt64());
		} else Assert.DoesNotContain("references", result.Text);
		Assert.Equal(1, f.Calls);
		Assert.Equal(label == "failure" ? 1 : 0, f.Usage().UnknownCalls);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DisableOrRotateBeforeAdmissionPreventsSendAndRefundsReservation(bool rotate) {
		using var f = new Fixture((_, _) => throw new InvalidOperationException("should not dispatch"),
			settings => Assert.Equal(JevChange.Saved, rotate ? settings.Replace("synthetic-replacement") : settings.SetOptions(false, false)));
		f.Start();
		f.TurnOn();
		var result = await f.Tools.InvokeAsync(f.Agent(), "jev", Args, default);
		Assert.True(result.IsError);
		Assert.Equal(0, f.Calls);
		Assert.Equal(0, f.Usage().UnknownCalls);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DisableOrRemoveDuringSendCancelsAndDiscardsUnacceptedAnswer(bool remove) {
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var f = new Fixture(async (_, ct) => {
			using var registration = ct.Register(() => retired.TrySetResult());
			entered.TrySetResult();
			await retired.Task.WaitAsync(TimeSpan.FromSeconds(5));
			return Reply("yes");
		});
		f.Start();
		f.TurnOn();
		var pending = f.Tools.InvokeAsync(f.Agent(), "jev", Args, default);
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(JevChange.Saved, remove ? f.Settings.Remove() : f.Settings.SetOptions(false, false));
		var result = await pending;
		Assert.True(result.IsError);
		Assert.Contains("withheld", result.Text);
		Assert.Equal(1, f.Calls);
		Assert.Equal(1L, f.Usage().UnknownCalls);
	}

	sealed class DelegatingFixture(Action<ToolOutput> after) : ITool {
		public string Name => "fixture_delegate";
		public string Description => "Fixture delegation";
		public System.Text.Json.Nodes.JsonObject Parameters => Schema.Object();
		public async Task<ToolOutput> InvokeAsync(IToolContext ctx, System.Text.Json.Nodes.JsonObject args, CancellationToken ct) {
			var pending = await ctx.Tools.InvokeAsync(ctx, "jev", Args, ct);
			after(pending);
			return pending;
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealTurnHandoffOrdersAcceptanceAgainstRetirement(bool retireAfterAcceptance) {
		using var f = new Fixture((_, _) => Task.FromResult(Reply("yes")));
		f.Start();
		f.TurnOn();
		string? pendingText = null;
		JevChange? retired = null;
		f.Tools.Add(new DelegatingFixture(pending => {
			pendingText = pending.Text;
			if(!retireAfterAcceptance) retired = f.Settings.Remove();
		}));
		f.Manager.Events += e => {
			if(retireAfterAcceptance && JsonIO.Serialize(e).Contains("\"kind\":\"tool_result\"")) retired = f.Settings.Remove();
		};
		var a = f.Agent();
		f.Backend.Script.Enqueue(_ => FakeBackend.Call("real-accept", "fixture_delegate", "{}"));
		f.Backend.Script.Enqueue(_ => FakeBackend.Say("done"));
		a.Send(new Inbound("Sera", "evaluate this", SendMode.Queued));
		await Wait.Until(() => a.Transcript.Last?.Kind == EntryKind.Idle);
		var entry = Assert.Single(a.Transcript.Entries, row => row.Kind == EntryKind.ToolResult);
		Assert.Equal("", pendingText);
		Assert.Equal(JevChange.Saved, retired);
		Assert.Equal(!retireAfterAcceptance, entry.IsError == true);
		Assert.Contains(retireAfterAcceptance ? "\"choice\":\"yes\"" : "withheld", entry.Text);
		Assert.Equal(10, f.Usage().Input);
		Assert.Equal(0, f.Usage().UnknownCalls);
		Assert.Equal(1, f.Usage().Jev?.Yes);
	}

	[Fact]
	public async Task RealScheduleUsesPowerShellPathWithoutBorrowingModelEvidence() {
		using var f = new Fixture((_, _) => Task.FromResult(Reply("undecidable")));
		f.Start();
		f.Tools.Add(new PowerShellTool(() => 30, () => 3, new ChildProcessWatcher()));
		f.TurnOn();
		var a = f.Agent();
		var now = DateTimeOffset.UtcNow;
		a.AddSchedule("jev -mode decide -state 'Explicit facts' -question 'Has Quartz shipped?'", null, now, null, null);
		await Task.WhenAll(new Scheduler(f.Manager).Tick(now));
		await Wait.Until(() => a.Transcript.Last?.Kind == EntryKind.Idle);
		Assert.Contains(a.Transcript.Entries, row => row.Text?.Contains("\"evidence\":{\"status\":\"unavailable\"}") == true);
		Assert.Equal(1, f.Calls);
		Assert.Equal(1, f.Usage().Jev?.Undecidable);
	}

	[Fact]
	public async Task ChangedVisibilityWithholdsEvidenceButNotTheVerdict() {
		Agent a = null!;
		using var f = new Fixture((_, _) => {
			a.Transcript.Append(new Entry { Kind = EntryKind.Elide, Seqs = [] });
			return Task.FromResult(Reply("undecidable"));
		});
		f.Start();
		f.TurnOn();
		a = f.Agent();
		f.Backend.Script.Enqueue(_ => FakeBackend.Call("jev-stale", "jev", Args));
		f.Backend.Script.Enqueue(_ => FakeBackend.Say("done"));
		a.Send(new Inbound("Sera", "Quartz arrived", SendMode.Queued));
		await Wait.Until(() => a.Transcript.Last?.Kind == EntryKind.Idle);
		var result = Assert.Single(a.Transcript.Entries, row => row.Kind == EntryKind.ToolResult);
		Assert.Contains("undecidable", result.Text);
		Assert.Contains("unavailable", result.Text);
		Assert.DoesNotContain("references", result.Text);
		Assert.Equal(1, f.Calls);
	}

	[Fact]
	public async Task PreviewRequiresCurrentOptInAndSameAgentPublishedPng() {
		if(!ArtifactFiles.Supported) return;
		using var homeDir = new TempDir();
		var home = new Home(homeDir.File("image-home"));
		using var store = new Artifacts(home, _ => { });
		var png = JevClientTests.Png();
		using var f = new Fixture(async (request, ct) => {
			Assert.Equal("true", Assert.Single(request.Headers.GetValues("TypeSafe-Danger-Zone-Images")));
			var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
			Assert.Equal(2, parts.Length);
			Assert.Equal(JevPreviewImage.Stage(png, true).CopyPngBytes(), await parts[1].ReadAsByteArrayAsync(ct));
			return Reply("yes");
		});
		f.Start(store: store, home: home);
		f.TurnOn();
		var owner = f.Agent();
		var other = f.Agent();
		var staging = Path.Combine(owner.Directory, Artifacts.Staging);
		await File.WriteAllBytesAsync(Path.Combine(staging, "fixture.png"), png);
		var item = await store.Publish(owner.AgentId, "fixture.png", null, default, owner.Directory);
		var args = Args.Insert(1, $"\"image_artifact\":\"{item.Id}\",");
		Assert.True((await f.Tools.InvokeAsync(owner, "jev", args, default)).IsError);
		Assert.Equal(JevChange.Saved, f.Settings.SetOptions(true, true));
		Assert.True((await f.Tools.InvokeAsync(owner, "jev", args, default)).IsError); // staged but unpublished
		store.Commit(owner.AgentId, owner.Transcript, new Entry { Kind = EntryKind.ToolResult, Artifacts = [item] });
		Assert.True((await f.Tools.InvokeAsync(other, "jev", args, default)).IsError);
		Assert.Equal(0, f.Calls);
		Assert.False((await f.Tools.InvokeAsync(owner, "jev", args, default)).IsError);
		Assert.Equal(1, f.Calls);
	}

	[Fact]
	public async Task InvalidBoundedInputsAndBudgetFailBeforeDispatch() {
		using var f = new Fixture((_, _) => throw new InvalidOperationException("should not dispatch"));
		f.Start(new Config { Budgets = new() { ["jev"] = 0 } });
		f.TurnOn();
		var a = f.Agent();
		foreach(var args in new[] { Args.Replace("Explicit facts", new string('é', 6_001)), Args.Insert(1, "\"key\":\"not allowed\","),
			Args.Insert(1, "\"image_artifact\":\"https://fixture.invalid/raw.png\","), new string(' ', JevTool.MaxArgumentsChars + 1) }) {
			var result = await f.Tools.InvokeAsync(a, "jev", args, default);
			Assert.True(result.IsError);
			Assert.DoesNotContain("budget", result.Text);
		}
		Assert.Contains("budget", (await f.Tools.InvokeAsync(a, "jev", Args, default)).Text);
		Assert.Equal(0, f.Calls);
	}
}
