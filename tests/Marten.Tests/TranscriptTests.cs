using Marten.Storage;

namespace Marten.Tests;

public class TranscriptTests {
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
