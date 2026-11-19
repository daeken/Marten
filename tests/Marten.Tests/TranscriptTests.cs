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
		Assert.Equal(["one", "two", "three"], new Transcript(dir.Path).Entries.Select(e => e.Text));
	}
}
