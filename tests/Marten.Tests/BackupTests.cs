using System.Security.Cryptography;
using Marten.Storage;

namespace Marten.Tests;

public class BackupTests {
	static (Home Home, Backups Backups) Setup(TempDir dir, BackupSettings? settings = null) {
		var home = new Home(dir.File("home"));
		File.WriteAllText(Path.Combine(home.Root, "config.json"), "{}");
		File.WriteAllText(Path.Combine(home.Secrets, "anthropic-api-key"), "sk-secret");
		Directory.CreateDirectory(Path.Combine(home.Agents, "a1", "browser"));
		File.WriteAllText(Path.Combine(home.Agents, "a1", "browser", "Cookies"), "cookie");
		File.WriteAllText(Path.Combine(home.Agents, "a1", "transcript.jsonl"), "{\"seq\":1}\n");
		Directory.CreateDirectory(Path.Combine(home.Projects, "p", "worktrees", "t"));
		File.WriteAllText(Path.Combine(home.Projects, "p", "worktrees", "t", "code.cs"), "x");
		File.WriteAllText(Path.Combine(home.Projects, "p", "MEMORY.md"), "remember");
		return (home, new Backups(home, () => settings ?? new BackupSettings()));
	}

	[Fact]
	public async Task SnapshotsLeaveOutSecretsAndRestoreIntoANewFolder() {
		using var dir = new TempDir();
		var (home, b) = Setup(dir);
		Assert.Equal(dir.File("home") + "-backups", b.Directory);
		var snap = await b.TakeAsync();
		Assert.NotNull(snap);
		Assert.False(snap!.Encrypted);
		Assert.Null(await b.TakeAsync());

		await Backups.RestoreAsync(snap.Path, dir.File("restored"), null);
		Assert.Equal("{\"seq\":1}\n", File.ReadAllText(dir.File("restored/agents/a1/transcript.jsonl")));
		Assert.Equal("remember", File.ReadAllText(dir.File("restored/projects/p/MEMORY.md")));
		Assert.False(Directory.Exists(dir.File("restored/secrets")));
		Assert.False(Directory.Exists(dir.File("restored/agents/a1/browser")));
		Assert.False(Directory.Exists(dir.File("restored/projects/p/worktrees")));
		Assert.Contains("Marten snapshot", File.ReadAllText(dir.File("restored/.marten-backup.txt")));
		await Assert.ThrowsAsync<IOException>(() => Backups.RestoreAsync(snap.Path, dir.File("restored"), null));

		File.AppendAllText(Path.Combine(home.Agents, "a1", "transcript.jsonl"), "{\"seq\":2}\n");
		Assert.NotNull(await b.TakeAsync());
	}

	[Fact]
	public async Task EncryptedSnapshotsNeedThePassphrase() {
		using var dir = new TempDir();
		var (home, b) = Setup(dir);
		// Enough data to span several chunks.
		File.WriteAllBytes(Path.Combine(home.Agents, "a1", "big.bin"), RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17));
		b.SetPassphrase("correct horse");
		Assert.True(b.Encrypting);
		var snap = await b.TakeAsync();
		Assert.True(snap!.Encrypted);
		Assert.EndsWith(".tar.gz.enc", snap.Path);

		await Assert.ThrowsAsync<CryptographicException>(() => Backups.RestoreAsync(snap.Path, dir.File("wrong"), "battery staple"));
		await Assert.ThrowsAsync<InvalidOperationException>(() => Backups.RestoreAsync(snap.Path, dir.File("none"), null));
		await Backups.RestoreAsync(snap.Path, dir.File("ok"), "correct horse");
		Assert.Equal(File.ReadAllBytes(Path.Combine(home.Agents, "a1", "big.bin")), File.ReadAllBytes(dir.File("ok/agents/a1/big.bin")));

		// Cutting the file short is caught rather than restoring a partial snapshot silently.
		var bytes = File.ReadAllBytes(snap.Path);
		var cut = dir.File("cut.tar.gz.enc");
		File.WriteAllBytes(cut, bytes[..^100]);
		await Assert.ThrowsAnyAsync<Exception>(() => Backups.RestoreAsync(cut, dir.File("cut"), "correct horse"));
	}

	[Fact]
	public void PruneKeepsRecentAndOnePerDay() {
		using var dir = new TempDir();
		var (_, b) = Setup(dir, new BackupSettings { KeepRecent = 3, KeepDays = 5 });
		Directory.CreateDirectory(b.Directory);
		var now = DateTime.UtcNow;
		var names = new List<string>();
		for(var day = 0; day < 10; day++)
			for(var hour = 0; hour < 4; hour++) {
				var t = now.Date.AddDays(-day).AddHours(hour);
				if(t > now) continue;
				var name = Path.Combine(b.Directory, $"marten-{t:yyyyMMdd-HHmmss}.tar.gz");
				File.WriteAllText(name, "x");
				names.Add(name);
			}
		b.Prune();
		var left = b.List();
		var recent = names.OrderByDescending(n => n).Take(3);
		Assert.All(recent, n => Assert.Contains(left, l => l.Path == n));
		// One per day for days 0..5, plus the recent ones; nothing older than the window.
		Assert.All(left, l => Assert.True(l.Taken.UtcDateTime >= now.Date.AddDays(-5)));
		Assert.Equal(6, left.Select(l => l.Taken.UtcDateTime.Date).Distinct().Count());
		Assert.True(left.Count <= 3 + 6);
	}
}
