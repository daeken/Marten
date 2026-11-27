using System.Runtime.InteropServices;
using System.Text;
using Marten.Agents;
using Marten.Tools;
using Marten.Storage;

namespace Marten.Tests;

public sealed class ArtifactTests {
	[Fact]
	public async Task RuntimeAndStartupDoNotSynthesizeFailureAfterResultFsync() {
		if(!ArtifactFiles.Supported) return;
		using var temp = new TempDir();
		var home = new Home(temp.File("runtime"));
		var initial = new FakeBackend();
		initial.Script.Enqueue(_ => FakeBackend.Call("publish-once", "publish_artifact", "{\"filename\":\"note.txt\"}"));
		using var store = new Artifacts(home, _ => { }, p => { if(p == "after result flush") throw new IOException("fixture failure"); });
		AgentManager manager = null!;
		manager = new AgentManager(home, new Config(), initial, new ToolRegistry().Add(new PublishArtifactTool(() => manager))) { ArtifactStore = store };
		var agent = manager.CreateChat("indeterminate", temp.Path);
		var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
		manager.Events += e => events.Enqueue(JsonIO.Serialize(e));
		await File.WriteAllTextAsync(Path.Combine(agent.Directory, Artifacts.Staging, "note.txt"), "example");
		agent.Send(new Inbound("Sera", "Publish it", SendMode.Queued));
		await Wait.Until(() => agent.Status == AgentStatus.Error);
		Assert.Single(initial.Requests);
		Assert.DoesNotContain(agent.Transcript.ReadPage(100).Entries, e => e.Kind == EntryKind.ToolResult);
		var persisted = Assert.Single(new Transcript(Path.Combine(agent.Directory, "transcript")).Entries, e => e.Kind == EntryKind.ToolResult);
		var artifact = Assert.Single(persisted.Artifacts!);
		Assert.Null(agent.Transcript.Artifact(artifact.Id));
		Assert.DoesNotContain(events, e => e.Contains("\"artifacts\":["));
		store.Dispose();
		using var reopened = new Artifacts(home, _ => { });
		var resumed = new FakeBackend();
		resumed.Script.Enqueue(_ => FakeBackend.Say("Recovered successfully"));
		var next = new AgentManager(home, new Config(), resumed, new ToolRegistry()) { ArtifactStore = reopened };
		next.Events += e => events.Enqueue(JsonIO.Serialize(e));
		next.LoadAll();
		var recovered = next.Get(agent.Info.Id)!;
		await Wait.Until(() => recovered.Transcript.Last?.Kind == EntryKind.Idle);
		var result = Assert.Single(recovered.Transcript.ReadPage(100).Entries, e => e.Kind == EntryKind.ToolResult && e.CallId == "publish-once");
		Assert.False(result.IsError == true);
		Assert.Equal(artifact, recovered.Transcript.Artifact(artifact.Id));
		using var bytes = reopened.Open(agent.Info.Id, artifact, recovered.Transcript);
		Assert.NotNull(bytes);
		var replay = Assert.Single(Assert.Single(resumed.Requests).History, e => e.Kind == EntryKind.ToolResult && e.CallId == "publish-once");
		Assert.False(replay.IsError == true);
		Assert.DoesNotContain(events, e => e.Contains("\"artifacts\":["));
	}

	const string ValidId = "00000000000000000000000000000001";
	const string ValidRef = "{\"id\":\"" + ValidId + "\",\"name\":\"note.txt\",\"bytes\":0,\"previewKind\":\"text\"}";

	[Theory]
	[InlineData("{broken", true)]
	[InlineData("""{"kind":"tool_result","artifacts":[null]}""", true)]
	[InlineData("""{"kind":"tool_result","artifacts":[{}]}""", false)]
	[InlineData("""{"kind":"tool_result","artifacts":[{"id":"bad","name":"note.txt","bytes":0,"previewKind":"text"}]}""", true)]
	[InlineData("{\"kind\":\"tool_result\",\"artifacts\":[{\"id\":\"" + ValidId + "\",\"name\":\"note.txt\",\"previewKind\":\"text\"}]}", true)]
	[InlineData("{\"kind\":\"tool_result\",\"artifacts\":[{\"id\":\"" + ValidId + "\",\"name\":\"../note.txt\",\"bytes\":0,\"previewKind\":\"text\"}]}", true)]
	[InlineData("{\"kind\":\"tool_result\",\"artifacts\":[{\"id\":\"" + ValidId + "\",\"name\":\"note.txt\",\"bytes\":8388609,\"previewKind\":\"text\"}]}", true)]
	[InlineData("{\"kind\":\"tool_result\",\"artifacts\":[{\"id\":\"" + ValidId + "\",\"name\":\"note.txt\",\"bytes\":0,\"previewKind\":\"html\"}]}", true)]
	[InlineData("{\"kind\":\"tool_result\",\"artifacts\":[" + ValidRef + "," + ValidRef + "]}", true)]
	[InlineData("{\"kind\":\"assistant\",\"artifacts\":[" + ValidRef + "]}", true)]
	public void MalformedSegmentDoesNotPreventHealthyAgentStarting(string line, bool archive) {
		using var temp = new TempDir();
		var home = new Home(temp.File("runtime"));
		var manager = new AgentManager(home, new Config(), new FakeBackend(), new ToolRegistry());
		var damaged = manager.CreateChat("damaged", temp.Path);
		var healthy = manager.CreateChat("healthy", temp.Path);
		var log = Path.Combine(damaged.Directory, "transcript");
		var corruptPath = Path.Combine(log, "000001.jsonl");
		File.WriteAllText(corruptPath, line + "\n");
		if(archive) File.WriteAllText(Path.Combine(log, "000002.jsonl"), "");
		var restarted = new AgentManager(home, new Config(), new FakeBackend(), new ToolRegistry());
		restarted.LoadAll();
		Assert.Null(restarted.Get(damaged.Info.Id));
		Assert.NotNull(restarted.Get(healthy.Info.Id));
		Assert.Equal(line + "\n", File.ReadAllText(corruptPath));
	}

	[Fact]
	public void InvalidArtifactDoesNotWriteOrPoisonTranscript() {
		using var scratch = new TempDir();
		var log = new Transcript(scratch.Path);
		var item = new ArtifactRef(ValidId, "note.txt", 0, "text");
		var result = new Entry { Kind = EntryKind.ToolResult, Artifacts = [item] };
		Assert.Throws<InvalidDataException>(() => log.Append(result with { Artifacts = [item, null!] }));
		Assert.Empty(Directory.GetFiles(scratch.Path));
		Assert.Empty(log.Entries);
		Assert.Null(log.Artifact(item.Id));
		Assert.Equal(1, log.Append(result).Seq);
		var segment = scratch.File("000001.jsonl");
		var committed = File.ReadAllBytes(segment);
		Assert.Throws<InvalidDataException>(() => log.Append(result));
		Assert.Equal(committed, File.ReadAllBytes(segment));
		Assert.Equal(item, log.Artifact(item.Id));
		Assert.Equal(2, log.Append(new Entry { Kind = EntryKind.Idle }).Seq);
	}

	[Theory]
	[InlineData("")]
	[InlineData("\n")]
	public void ArtifactAppendChecksTailOnVerifiedDescriptor(string terminator) {
		if(!ArtifactFiles.Supported) return;
		using var scratch = new TempDir();
		var home = new Home(scratch.File("runtime"));
		var directory = home.AgentDir("agent");
		var path = Path.Combine(directory, "transcript");
		var segment = Path.Combine(path, "000001.jsonl");
		var initial = JsonIO.Serialize(new Entry { Kind = EntryKind.ToolCall, Seq = 1, CallId = "publish" });
		Directory.CreateDirectory(path);
		File.WriteAllText(segment, initial + terminator);
		var log = new Transcript(path);
		using var lease = ArtifactFiles.AcquireHome(home.Root, _ => { });
		using var agents = ArtifactFiles.DirectoryAt(lease!.Root, "agents");
		using var agent = ArtifactFiles.DirectoryAt(agents, "agent");
		using var verifiedDir = ArtifactFiles.DirectoryAt(agent, "transcript");
		var moved = path + "-fixture";
		Directory.Move(path, moved); // The verified directory remains open, but the old path no longer resolves.
		var result = new Entry { Kind = EntryKind.ToolResult, CallId = "publish", Artifacts = [new(ValidId, "note.txt", 0, "text")] };
		var appended = log.AppendArtifact(result, name => ArtifactFiles.OpenLog(verifiedDir, name), () => ArtifactFiles.SyncNamespace(verifiedDir), () => { });
		Directory.Move(moved, path);
		Assert.Equal(initial + "\n" + JsonIO.Serialize(appended) + "\n", File.ReadAllText(segment));
		Assert.Equal(appended.Seq, log.Last!.Seq);
		Assert.Equal(ValidId, Assert.Single(new Transcript(path).Last!.Artifacts!).Id);
	}

	[Fact]
	public async Task FailedExclusiveCreateDoesNotDeleteUnownedTemp() {
		if(!ArtifactFiles.Supported) return;
		using var scratch = new TempDir();
		var home = new Home(scratch.File("runtime"));
		var directory = home.AgentDir("agent");
		var warnings = new List<string>();
		Directory.CreateDirectory(directory);
		using var store = new Artifacts(home, warnings.Add, nextId: () => Guid.ParseExact(ValidId, "N"));
		Assert.True(store.Enabled);
		store.PrepareAgent("agent");
		await File.WriteAllTextAsync(Path.Combine(directory, Artifacts.Staging, "note.txt"), "source");
		var collision = Path.Combine(home.Root, Artifacts.Store, "agent." + ValidId + ".tmp");
		await File.WriteAllTextAsync(collision, "pre-existing");
		await Assert.ThrowsAsync<IOException>(() => store.Publish("agent", "note.txt", null, default));
		Assert.Equal("pre-existing", await File.ReadAllTextAsync(collision));
		Assert.Equal(collision, Assert.Single(Directory.GetFiles(Path.GetDirectoryName(collision)!)));
		Assert.False(store.Enabled);
		Assert.Single(warnings);
	}

	[Fact]
	public async Task ToolProducesDurableResultAndEntryEvent() {
		if(!ArtifactFiles.Supported) return;
		using var temp = new TempDir();
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Call("publish", "publish_artifact", "{\"filename\":\"deliverable.txt\",\"label\":null}"));
		backend.Script.Enqueue(_ => FakeBackend.Say("Done."));
		AgentManager manager = null!;
		var tool = new PublishArtifactTool(() => manager);
		var home = new Home(temp.File("home"));
		using var store = new Artifacts(home, message => throw new Exception(message));
		manager = new AgentManager(home, new Config(), backend, new ToolRegistry().Add(tool)) { ArtifactStore = store };
		var agent = manager.CreateChat("artifact test", temp.Path);
		var incr = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		manager.Events += e => {
			var json = JsonIO.Serialize(e);
			if(json.Contains("\"artifacts\":[")) incr.TrySetResult(json);
		};
		await File.WriteAllTextAsync(Path.Combine(agent.Directory, Artifacts.Staging, "deliverable.txt"), "published bytes");
		agent.Send(new Inbound("Sera", "Publish the deliverable.", SendMode.Queued));
		var eventJson = await incr.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await Wait.Until(() => agent.Transcript.Last?.Kind == EntryKind.Idle);
		var item = Assert.Single(Assert.Single(agent.Transcript.Entries, e => e.Kind == EntryKind.ToolResult).Artifacts!);
		Assert.Equal(item, new Transcript(Path.Combine(agent.Directory, "transcript")).Artifact(item.Id));
		Assert.Contains(item.Id, eventJson);
		Assert.DoesNotContain("published bytes", eventJson);
		Assert.False(tool.CallableFromPowerShell);
	}

	[Fact]
	public async Task SnapshotSurvivesSourceDeletionAndRecoveryButOnlyCommittedIdIsIndexed() {
		if(!ArtifactFiles.Supported) return;
		using var home = new TempDir();
		var runtimeHome = new Home(home.File("runtime"));
		const string agentId = "agent";
		var directory = runtimeHome.AgentDir(agentId);
		var transcriptDir = Path.Combine(directory, "transcript");
		var transcript = new Transcript(transcriptDir);
		using var store = new Artifacts(runtimeHome, message => throw new Exception(message));
		store.PrepareAgent(agentId);
		var source = Path.Combine(directory, Artifacts.Staging, "draft.txt");
		await File.WriteAllTextAsync(source, "hello, snapshot");
		var orphan = await store.Publish(agentId, "draft.txt", null, default);
		var item = await store.Publish(agentId, "draft.txt", "nice\u202E/label", default);
		Assert.Equal("text", item.PreviewKind);
		Assert.Equal("nicelabel.txt", item.Name);
		Assert.Null(transcript.Artifact(item.Id));
		transcript.Append(new Entry { Kind = EntryKind.ToolResult, Artifacts = [item] });
		File.Delete(source);
		var loaded = new Transcript(transcriptDir);
		Assert.Equal(item, loaded.Artifact(item.Id));
		Assert.Equal(item, Assert.Single(Assert.Single(loaded.ReadPage(80).Entries).Artifacts!));
		store.Dispose();
		using var resumed = new Artifacts(runtimeHome, message => throw new Exception(message));
		Assert.Null(loaded.Artifact(orphan.Id));
		Assert.True(File.Exists(Path.Combine(runtimeHome.Root, Artifacts.Store, agentId + "." + orphan.Id)));
		using var blob = resumed.Open(agentId, item);
		Assert.NotNull(blob);
		using var reader = new StreamReader(blob!);
		Assert.Equal("hello, snapshot", await reader.ReadToEndAsync());
	}

	[Theory]
	[InlineData("")]
	[InlineData(" !incom")]
	public async Task UnterminatedArtifactRecordDoesNotAuthorizeAfterRecoveryOrLaterAppend(string interruptedMarker) {
		if(!ArtifactFiles.Supported) return;
		using var temp = new TempDir();
		var runtimeHome = new Home(temp.File("runtime"));
		const string agentId = "agent";
		var directory = runtimeHome.AgentDir(agentId);
		var path = Path.Combine(directory, "transcript");
		var log = new Transcript(path);
		using var store = new Artifacts(runtimeHome, message => throw new Exception(message));
		store.PrepareAgent(agentId);
		await File.WriteAllTextAsync(Path.Combine(directory, Artifacts.Staging, "fixture.txt"), "uncommitted");
		var item = await store.Publish(agentId, "fixture.txt", null, default);
		log.Append(new Entry { Kind = EntryKind.ToolCall, CallId = "test", Arguments = new string('x', (4 << 20) + 1) });
		await File.AppendAllTextAsync(Path.Combine(path, "000001.jsonl"),
			JsonIO.Serialize(new Entry { Kind = EntryKind.ToolResult, Seq = 2, CallId = "test", Artifacts = [item] }) + interruptedMarker);
		var original = await File.ReadAllBytesAsync(Path.Combine(path, "000001.jsonl"));
		var recovered = new Transcript(path);
		Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(path, "000001.jsonl")));
		Assert.Null(recovered.Artifact(item.Id));
		Assert.Single(recovered.Entries);
		Assert.Equal(2, recovered.Append(new Entry { Kind = EntryKind.ToolResult, CallId = "test" }).Seq);
		Assert.Single(Directory.GetFiles(path, "*.jsonl")); // Never roll a damaged newest segment into the archive.
		if(interruptedMarker.Length == 0) {
			// A durably marked artifact tail remains rejected even when a later segment exists.
			await File.WriteAllTextAsync(Path.Combine(path, "000002.jsonl"), JsonIO.Serialize(new Entry { Kind = EntryKind.Idle, Seq = 3 }) + "\n");
		}
		Assert.Null(new Transcript(path).Artifact(item.Id));
	}

	[Fact]
	public async Task StagingRejectsTraversalLinksFifoAndOversizeAndLeavesNoCancelledCopy() {
		if(!ArtifactFiles.Supported) return;
		using var home = new TempDir();
		var runtimeHome = new Home(home.File("runtime"));
		const string agentId = "agent";
		var directory = runtimeHome.AgentDir(agentId);
		var transcript = new Transcript(Path.Combine(directory, "transcript"));
		using var store = new Artifacts(runtimeHome, message => throw new Exception(message));
		store.PrepareAgent(agentId);
		var inbox = Path.Combine(directory, Artifacts.Staging);
		var privatePath = home.File("private.txt");
		await File.WriteAllTextAsync(privatePath, "private");
		File.CreateSymbolicLink(Path.Combine(inbox, "symlink.txt"), privatePath);
		Assert.Equal(0, HardLink(privatePath, Path.Combine(inbox, "hard.txt")));
		Assert.Equal(0, Fifo(Path.Combine(inbox, "fifo.txt"), 0x180));
		Directory.CreateDirectory(Path.Combine(inbox, "dir"));
		using(var large = File.Create(Path.Combine(inbox, "large.txt"))) large.SetLength(Artifacts.MaxBytes + 1L);
		foreach(var leaf in new[] { "../private.txt", privatePath, "symlink.txt", "hard.txt", "fifo.txt", "dir", "large.txt" })
			await Assert.ThrowsAsync<IOException>(() => store.Publish(agentId, leaf, null, default));
		await File.WriteAllTextAsync(Path.Combine(inbox, "draft.txt"), "finished");
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Publish(agentId, "draft.txt", null, new(true)));
		Assert.Empty(Directory.EnumerateFiles(Path.Combine(runtimeHome.Root, Artifacts.Store)));
		Directory.Move(inbox, inbox + "-old");
		Directory.CreateSymbolicLink(inbox, inbox + "-old");
		await Assert.ThrowsAsync<IOException>(() => store.Publish(agentId, "draft.txt", null, default));
	}

	[Theory]
	[InlineData("snapshot namespace", false)]
	[InlineData("transcript namespace", false)]
	[InlineData("after result flush", true)]
	public async Task IndeterminateFailureRetainsFinalizedBlobAndDefersResultAuthorization(string point, bool committedOnDisk) {
		if(!ArtifactFiles.Supported) return;
		using var scratch = new TempDir();
		var home = new Home(scratch.File("runtime"));
		const string id = "agent";
		var directory = home.AgentDir(id);
		var history = Path.Combine(directory, "transcript");
		var log = new Transcript(history);
		var warnings = new List<string>();
		using var store = new Artifacts(home, warnings.Add, p => { if(p == point) throw new IOException("Injected I/O fault."); });
		Assert.True(store.Enabled);
		store.PrepareAgent(id);
		await File.WriteAllTextAsync(Path.Combine(directory, Artifacts.Staging, "report.txt"), "report bytes");
		ArtifactRef? item = null;
		await Assert.ThrowsAsync<IOException>(async () => {
			item = await store.Publish(id, "report.txt", null, default);
			store.Commit(id, log, new Entry { Kind = EntryKind.ToolResult, Artifacts = [item] });
		});
		Assert.False(store.Enabled);
		Assert.Single(warnings);
		Assert.Empty(log.Entries);
		var snapshots = Directory.GetFiles(Path.Combine(home.Root, Artifacts.Store), "agent.*");
		Assert.Single(snapshots);
		Assert.Throws<ArtifactFiles.BusyException>(() => ArtifactFiles.AcquireHome(home.Root, _ => { }));
		store.Dispose();
		using var reopened = new Artifacts(home, warnings.Add);
		Assert.True(reopened.Enabled);
		Assert.True(File.Exists(snapshots[0]));
		Assert.Equal(committedOnDisk, new Transcript(history).Entries.Count == 1);
		if(committedOnDisk) { using var file = reopened.Open(id, item!); Assert.NotNull(file); }
	}

	[Fact]
	public async Task LeaseAndInventoryFailClosedAndHomeAliasDoesNotRetarget() {
		if(!ArtifactFiles.Supported) return;
		using var scratch = new TempDir();
		var original = new Home(scratch.File("original"));
		var alternate = new Home(scratch.File("alternate"));
		var alias = scratch.File("home-alias");
		Directory.CreateSymbolicLink(alias, original.Root);
		const string id = "agent";
		Directory.CreateDirectory(original.AgentDir(id));
		Directory.CreateDirectory(alternate.AgentDir(id));
		var warnings = new List<string>();
		using var store = new Artifacts(new Home(alias), warnings.Add);
		Assert.True(store.Enabled);
		store.PrepareAgent(id);
		var filename = Path.Combine(original.AgentDir(id), Artifacts.Staging, "launch.exe");
		await File.WriteAllTextAsync(filename, "original bytes");
		Assert.Throws<ArtifactFiles.BusyException>(() => ArtifactFiles.AcquireHome(original.Root, warnings.Add));
		Directory.Delete(alias);
		Directory.CreateSymbolicLink(alias, alternate.Root);
		var item = await store.Publish(id, "launch.exe", "CON", default);
		Assert.Equal("artifact.bin", item.Name);
		Assert.False(Directory.Exists(Path.Combine(alternate.Root, Artifacts.Store)));
		var agent = original.AgentDir(id);
		Directory.Move(agent, agent + "-old");
		Directory.CreateSymbolicLink(agent, agent + "-old");
		await Assert.ThrowsAsync<IOException>(() => store.Publish(id, "launch.exe", null, default));
		store.Dispose();
		var unverified = Path.Combine(original.Root, Artifacts.Store, "malformed");
		await File.WriteAllTextAsync(unverified, "leave untouched");
		using var partial = new Artifacts(original, warnings.Add);
		Assert.False(partial.Enabled);
		Assert.True(File.Exists(unverified));
	}

	[Fact]
	public void PreviewValidationIsBoundedAndRejectsActiveFormats() {
		var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
		Assert.True(Artifacts.SafePng(png));
		Assert.False(Artifacts.SafePng(png[..^1]));
		var oversized = png.ToArray();
		oversized[16] = 1;
		Assert.False(Artifacts.SafePng(oversized));
		Assert.False(Artifacts.SafePng("<svg/>"u8));
		Assert.Equal(-1, Artifacts.TextPrefix([0xff, 0], false));
		Assert.Equal(1, Artifacts.TextPrefix(Encoding.UTF8.GetBytes("Xé")[..2], true));
		Assert.Equal(-1, Artifacts.TextPrefix(Encoding.UTF8.GetBytes("Xé")[..2], false));
	}

	[DllImport("libc", EntryPoint = "link")]
	static extern int HardLink(string source, string target);
	[DllImport("libc", EntryPoint = "mkfifo")]
	static extern int Fifo(string path, int mode);
}
