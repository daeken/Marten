using System.Runtime.InteropServices;
using System.Text;
using Marten.Agents;
using Marten.Tools;
using Marten.Storage;

namespace Marten.Tests;

public sealed class ArtifactTests {
	[Fact]
	public async Task ToolProducesDurableResultAndEntryEvent() {
		if(!ArtifactFiles.Supported) return;
		using var temp = new TempDir();
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Call("publish", "publish_artifact", "{\"filename\":\"deliverable.txt\",\"label\":null}"));
		backend.Script.Enqueue(_ => FakeBackend.Say("Done."));
		AgentManager manager = null!;
		var tool = new PublishArtifactTool(() => manager);
		manager = new AgentManager(new Home(temp.File("home")), new Config(), backend, new ToolRegistry().Add(tool));
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
		var directory = home.File("agent");
		var transcriptDir = Path.Combine(directory, "transcript");
		var transcript = new Transcript(transcriptDir);
		var store = new Artifacts();
		store.Recover(directory, transcript);
		var source = Path.Combine(directory, Artifacts.Staging, "draft.txt");
		await File.WriteAllTextAsync(source, "hello, snapshot");
		var orphan = await store.Publish(directory, "draft.txt", null, default);
		var item = await store.Publish(directory, "draft.txt", "nice\u202E/label", default);
		Assert.Equal("text", item.PreviewKind);
		Assert.Equal("nicelabel.txt", item.Name);
		Assert.Null(transcript.Artifact(item.Id));
		transcript.Append(new Entry { Kind = EntryKind.ToolResult, Artifacts = [item] });
		File.Delete(source);
		var loaded = new Transcript(transcriptDir);
		Assert.Equal(item, loaded.Artifact(item.Id));
		Assert.Equal(item, Assert.Single(Assert.Single(loaded.ReadPage(80).Entries).Artifacts!));
		new Artifacts().Recover(directory, loaded);
		Assert.Null(Artifacts.Open(directory, orphan));
		using var blob = Artifacts.Open(directory, item);
		Assert.NotNull(blob);
		using var reader = new StreamReader(blob!);
		Assert.Equal("hello, snapshot", await reader.ReadToEndAsync());
	}

	[Fact]
	public async Task UnterminatedArtifactRecordDoesNotAuthorizeAfterRecoveryOrLaterAppend() {
		if(!ArtifactFiles.Supported) return;
		using var temp = new TempDir();
		var directory = temp.File("agent");
		var path = Path.Combine(directory, "transcript");
		var log = new Transcript(path);
		var store = new Artifacts();
		store.Recover(directory, log);
		await File.WriteAllTextAsync(Path.Combine(directory, Artifacts.Staging, "fixture.txt"), "uncommitted");
		var item = await store.Publish(directory, "fixture.txt", null, default);
		log.Append(new Entry { Kind = EntryKind.ToolCall, CallId = "test" });
		await File.AppendAllTextAsync(Path.Combine(path, "000001.jsonl"),
			JsonIO.Serialize(new Entry { Kind = EntryKind.ToolResult, Seq = 2, CallId = "test", Artifacts = [item] }));
		var recovered = new Transcript(path);
		Assert.Null(recovered.Artifact(item.Id));
		Assert.Single(recovered.Entries);
		new Artifacts().Recover(directory, recovered);
		Assert.Null(Artifacts.Open(directory, item));
		Assert.Equal(2, recovered.Append(new Entry { Kind = EntryKind.ToolResult, CallId = "test" }).Seq);
		Assert.Null(new Transcript(path).Artifact(item.Id));
	}

	[Fact]
	public async Task StagingRejectsTraversalLinksFifoAndOversizeAndLeavesNoCancelledCopy() {
		if(!ArtifactFiles.Supported) return;
		using var home = new TempDir();
		var directory = home.File("agent");
		var transcript = new Transcript(Path.Combine(directory, "transcript"));
		var store = new Artifacts();
		store.Recover(directory, transcript);
		var inbox = Path.Combine(directory, Artifacts.Staging);
		var privatePath = home.File("private.txt");
		await File.WriteAllTextAsync(privatePath, "private");
		File.CreateSymbolicLink(Path.Combine(inbox, "symlink.txt"), privatePath);
		Assert.Equal(0, HardLink(privatePath, Path.Combine(inbox, "hard.txt")));
		Assert.Equal(0, Fifo(Path.Combine(inbox, "fifo.txt"), 0x180));
		Directory.CreateDirectory(Path.Combine(inbox, "dir"));
		using(var large = File.Create(Path.Combine(inbox, "large.txt"))) large.SetLength(Artifacts.MaxBytes + 1L);
		foreach(var leaf in new[] { "../private.txt", privatePath, "symlink.txt", "hard.txt", "fifo.txt", "dir", "large.txt" })
			await Assert.ThrowsAsync<IOException>(() => store.Publish(directory, leaf, null, default));
		await File.WriteAllTextAsync(Path.Combine(inbox, "draft.txt"), "finished");
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Publish(directory, "draft.txt", null, new(true)));
		Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory, Artifacts.Store)));
		Directory.Move(inbox, inbox + "-old");
		Directory.CreateSymbolicLink(inbox, inbox + "-old");
		await Assert.ThrowsAsync<IOException>(() => store.Publish(directory, "draft.txt", null, default));
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
