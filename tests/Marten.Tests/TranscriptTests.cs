using System.Text;
using System.Text.Json;
using Marten.Storage;

namespace Marten.Tests;

public class TranscriptTests {
	[Fact]
	public void PagesBackwardsWithAnExclusiveCursor() {
		using var dir = new TempDir();
		var t = new Transcript(dir.Path);
		var empty = t.ReadPage(2);
		Assert.Empty(empty.Entries);
		Assert.False(empty.HasOlder);

		for(int i = 0; i < 4; i++) t.Append(new Entry { Kind = EntryKind.Message });
		var tail = t.ReadPage(2);
		Assert.Equal([3L, 4L], tail.Entries.Select(e => e.Seq));
		Assert.True(tail.HasOlder);
		t.Append(new Entry { Kind = EntryKind.Message });
		var head = t.ReadPage(2, tail.Entries[0].Seq);
		Assert.Equal([1L, 2L], head.Entries.Select(e => e.Seq));
		Assert.False(head.HasOlder);
		var pastHead = t.ReadPage(2, head.Entries[0].Seq);
		Assert.Empty(pastHead.Entries);
		Assert.False(pastHead.HasOlder);
	}

	[Fact]
	public void ClampsPageSizeAndHandlesCursorsBetweenSequenceNumbers() {
		using var dir = new TempDir();
		File.WriteAllLines(Path.Combine(dir.Path, "000001.jsonl"), Enumerable.Range(1, 110)
			.Select(i => JsonIO.Serialize(new Entry { Kind = EntryKind.Message, Seq = 2L * i })));
		var t = new Transcript(dir.Path);
		var tail = t.ReadPage(int.MaxValue, long.MaxValue);
		Assert.Equal(Enumerable.Range(11, 100).Select(i => 2L * i), tail.Entries.Select(e => e.Seq));
		Assert.True(tail.HasOlder);
		var single = t.ReadPage(0, 5);
		Assert.Equal(4L, Assert.Single(single.Entries).Seq);
		Assert.True(single.HasOlder);
	}

	[Fact]
	public void IndexedSnapshotAndExclusiveCursorsAcrossSegmentsPreserveRawRows() {
		using var dir = new TempDir();
		using var raw = JsonDocument.Parse("{\"encrypted\":\"☃\", \"items\" : [1,2]}");
		var first = new Entry { Seq = 4, Kind = EntryKind.Assistant, Text = "", Raw = raw.RootElement, Instructions = "旧" };
		File.WriteAllText(dir.File("000001.jsonl"), "\r\n" + JsonIO.Serialize(first) + "\r\n\t\r\n", new UTF8Encoding(true));
		var large = new Entry { Seq = 8, Kind = EntryKind.Message, Text = new string('é', 70_000) };
		File.WriteAllText(dir.File("000002.jsonl"), JsonIO.Serialize(large)); // Valid final row has no newline yet.
		var log = new Transcript(dir.Path);
		var snapshot = log.Capture();
		Assert.Equal(9, log.Append(new Entry { Kind = EntryKind.Elide }).Seq);
		Assert.Equal(10, log.Append(new Entry { Kind = EntryKind.Message, Text = "latest" }).Seq);
		Assert.Equal([4L, 8L], snapshot.Read().Select(e => e.Seq));
		var reloaded = new Transcript(dir.Path);
		var latest = reloaded.ReadPage(2);
		Assert.Equal([9L, 10L], latest.Entries.Select(e => e.Seq));
		Assert.True(latest.HasOlder);
		var archived = reloaded.ReadPage(2, latest.Entries[0].Seq);
		Assert.Equal([4L, 8L], archived.Entries.Select(e => e.Seq));
		Assert.Equal(JsonIO.Serialize(first), JsonIO.Serialize(archived.Entries[0]));
		Assert.Equal(large.Text, archived.Entries[1].Text);
		Assert.False(archived.HasOlder);
		Assert.Equal([8L, 10L], reloaded.Capture().Tail(2, RowKind.All & ~RowKind.Elide, after: 4).Entries.Select(e => e.Seq));
	}

	[Fact]
	public void RejectsMalformedTerminatedRowsInSealedSegments() {
		using var dir = new TempDir();
		File.WriteAllText(dir.File("000001.jsonl"), "{\"seq\":1,\"kind\":\"message\"}\n{\"seq\":2,\"kind\":[]}\n");
		File.WriteAllText(dir.File("000002.jsonl"), "{\"seq\":3,\"kind\":\"message\"}\n");
		Assert.ThrowsAny<JsonException>(() => new Transcript(dir.Path));
	}

	[Fact]
	public void RetainsExistingLatestSegmentCorruptionTolerance() {
		using var dir = new TempDir();
		File.WriteAllText(dir.File("000001.jsonl"), "{\"seq\":1,\"kind\":\"message\"}\n{\"seq\":2,\"kind\":[]}\n{\"seq\":3,\"kind\":\"message\"}\n");
		var log = new Transcript(dir.Path);
		Assert.Equal([1L, 3L], log.Entries.Select(e => e.Seq));
		Assert.Equal(4, log.Append(new Entry { Kind = EntryKind.Message }).Seq);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FailedAppendCannotBePaddedAndDamagedOversizeTailRemainsNewest(bool terminated) {
		using var dir = new TempDir();
		var log = new Transcript(dir.Path);
		log.Append(new Entry { Kind = EntryKind.Message });
		File.WriteAllText(dir.File("000002.jsonl"), JsonIO.Serialize(new Entry { Seq = 2, Kind = EntryKind.Message, Text = new string('a', (4 << 20) - 128) }) + "\n");
		log = new Transcript(dir.Path);
		Assert.Throws<IOException>(() => log.Append(new Entry { Kind = EntryKind.ToolResult }, (fs, _) => {
			fs.Write("{\"seq\":3,\"kind\":\"broken"u8);
			if(terminated) fs.WriteByte((byte) '\n');
			throw new IOException("synthetic interrupted write");
		}));
		var path = dir.File("000002.jsonl");
		var length = new FileInfo(path).Length;
		Assert.Equal(2, log.Last!.Seq);
		Assert.Throws<IOException>(() => log.Append(new Entry { Kind = EntryKind.ToolResult }));
		Assert.Equal(length, new FileInfo(path).Length);
		for(var restart = 0; restart < 2; restart++) {
			log = new Transcript(dir.Path);
			Assert.Equal(3L + restart, log.Append(new Entry { Kind = EntryKind.Message, Text = new string('b', 256) }).Seq);
			Assert.False(File.Exists(dir.File("000003.jsonl")));
		}
		Assert.True(new FileInfo(path).Length > (4 << 20));
		Assert.Equal([1L, 2L, 3L, 4L], new Transcript(dir.Path).Entries.Select(e => e.Seq));
	}

	[Fact]
	public void CleanOversizeSegmentRollsOverAndBeforeWriteFailureDoesNotPublish() {
		using var dir = new TempDir();
		var log = new Transcript(dir.Path);
		log.Append(new Entry { Kind = EntryKind.Message, Text = new string('a', 4 << 20) });
		Assert.Throws<IOException>(() => log.Append(new Entry { Kind = EntryKind.ToolResult }, (_, _) => throw new IOException("before write")));
		Assert.Equal(1, log.Last!.Seq);
		Assert.Equal(0, new FileInfo(dir.File("000002.jsonl")).Length);
		log = new Transcript(dir.Path);
		Assert.Equal(2, log.Append(new Entry { Kind = EntryKind.Message }).Seq);
		Assert.Equal([1L, 2L], new Transcript(dir.Path).Entries.Select(e => e.Seq));
	}

	[Fact]
	public void ReadsCrOnlyUtf8ButRejectsUnsupportedUtf16BomRatherThanSkipping() {
		using var dir = new TempDir();
		var contents = "{\"seq\":1,\"kind\":\"message\"}\r{\"seq\":2,\"kind\":\"assistant\"}";
		var path = dir.File("000001.jsonl");
		File.WriteAllText(path, contents, new UTF8Encoding(true));
		Assert.Equal([1L, 2L], new Transcript(dir.Path).Entries.Select(e => e.Seq));
		File.WriteAllText(path, contents, Encoding.Unicode);
		Assert.Throws<InvalidDataException>(() => new Transcript(dir.Path));
	}

	[Fact]
	public void SurvivesATornFinalLine() {
		using var dir = new TempDir();
		var t = new Transcript(dir.Path);
		t.Append(new Entry { Kind = EntryKind.Message, Text = "one" });
		t.Append(new Entry { Kind = EntryKind.Message, Text = "two" });
		File.AppendAllText(Path.Combine(dir.Path, "000001.jsonl"), "{\"seq\":3,\"kind\":\"mess");

		var reloaded = new Transcript(dir.Path);
		Assert.Equal(["one", "two"], reloaded.Entries.Select(e => e.Text));
		var next = reloaded.Append(new Entry { Kind = EntryKind.Message, Text = "three" });
		Assert.Equal(3, next.Seq);
		Assert.Contains("{\"seq\":3,\"kind\":\"mess\n", File.ReadAllText(dir.File("000001.jsonl")));
		Assert.Equal(["one", "two", "three"], new Transcript(dir.Path).Entries.Select(e => e.Text));
	}
}
