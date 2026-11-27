using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Marten.Storage;

public sealed record BackupSettings {
	/// <summary>Where snapshots go. Null means a folder beside ~/.marten, named like it with "-backups".</summary>
	public string? Directory { get; init; }
	/// <summary>Minutes between snapshots; 0 turns them off. A snapshot is skipped when nothing changed.</summary>
	public int IntervalMinutes { get; init; } = 60;
	/// <summary>The newest this many snapshots are always kept.</summary>
	public int KeepRecent { get; init; } = 48;
	/// <summary>Past those, the newest snapshot of each of the last this many days is kept.</summary>
	public int KeepDays { get; init; } = 30;
	/// <summary>Files bigger than this are left out, and listed in the snapshot's manifest.</summary>
	public int MaxFileMegabytes { get; init; } = 64;
	/// <summary>Extra paths to leave out, relative to ~/.marten, with * matching within a folder name and ** across folders.</summary>
	public List<string> Exclude { get; init; } = new();
}

public sealed record BackupInfo(string Path, DateTimeOffset Taken, long Bytes, bool Encrypted);

/// <summary>
/// Versioned snapshots of ~/.marten as .tar.gz files, encrypted with a passphrase when secrets/backup-passphrase exists.
/// Secrets, browser profiles, worktrees and operator artifact bytes are never included.
/// </summary>
public sealed class Backups(Home home, Func<BackupSettings> settings) {
	public static readonly string[] AlwaysExcluded = [
		"agents/*/artifact-inbox/**", "agents/*/artifacts/**",
		"secrets/**", "agents/*/browser/**", "agents/*/browser-out/**", "projects/*/worktrees/**", "**/node_modules/**", "**/*.tmp", "paused",
	];
	const string Prefix = "marten-", Stamp = "yyyyMMdd-HHmmss";
	readonly SemaphoreSlim gate = new(1, 1);

	public string PassphrasePath => System.IO.Path.Combine(home.Secrets, "backup-passphrase");
	public string Directory => settings().Directory is { Length: > 0 } d
		? System.IO.Path.GetFullPath(d.StartsWith("~/") ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), d[2..]) : d)
		: home.Root.TrimEnd('/', '\\') + "-backups";
	string FingerprintPath => System.IO.Path.Combine(Directory, ".fingerprint");

	public bool Encrypting => File.Exists(PassphrasePath) && File.ReadAllText(PassphrasePath).Trim().Length > 0;

	public void SetPassphrase(string? passphrase) {
		if(string.IsNullOrWhiteSpace(passphrase)) { File.Delete(PassphrasePath); return; }
		AtomicFile.Write(PassphrasePath, passphrase.Trim());
		if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(PassphrasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		// The next snapshot should be encrypted even if nothing else changed.
		if(System.IO.Directory.Exists(Directory)) File.Delete(FingerprintPath);
	}

	public IReadOnlyList<BackupInfo> List() {
		if(!System.IO.Directory.Exists(Directory)) return [];
		return System.IO.Directory.GetFiles(Directory, Prefix + "*")
			.Select(p => TakenAt(p) is { } t ? new BackupInfo(p, t, new FileInfo(p).Length, p.EndsWith(".enc")) : null)
			.OfType<BackupInfo>().OrderByDescending(b => b.Taken).ToList();
	}

	static DateTimeOffset? TakenAt(string path) {
		var m = Regex.Match(System.IO.Path.GetFileName(path), @"^marten-(\d{8}-\d{6})\.tar\.gz(\.enc)?$");
		return m.Success && DateTime.TryParseExact(m.Groups[1].Value, Stamp, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
			? new DateTimeOffset(t, TimeSpan.Zero) : null;
	}

	/// <summary>Files that go into a snapshot, as paths relative to ~/.marten with forward slashes.</summary>
	public List<(string Relative, FileInfo File)> Included(List<string> skipped) {
		var s = settings();
		var patterns = AlwaysExcluded.Concat(s.Exclude).Select(Glob).ToList();
		var backupDir = System.IO.Path.GetFullPath(Directory) + System.IO.Path.DirectorySeparatorChar;
		var files = new List<(string, FileInfo)>();
		var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
		foreach(var path in System.IO.Directory.EnumerateFiles(home.Root, "*", opts)) {
			if(path.StartsWith(backupDir)) continue;
			var rel = System.IO.Path.GetRelativePath(home.Root, path).Replace('\\', '/');
			if(patterns.Any(p => p.IsMatch(rel))) continue;
			var info = new FileInfo(path);
			if(info.Length > s.MaxFileMegabytes * 1024L * 1024) { skipped.Add($"{rel} ({info.Length / 1024 / 1024} MB)"); continue; }
			files.Add((rel, info));
		}
		files.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
		return files;
	}

	static Regex Glob(string pattern) {
		var sb = new StringBuilder("^");
		for(var i = 0; i < pattern.Length; i++) {
			if(pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') {
				i++;
				if(i + 1 < pattern.Length && pattern[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); } else sb.Append(".*");
			} else if(pattern[i] == '*') sb.Append("[^/]*");
			else sb.Append(Regex.Escape(pattern[i].ToString()));
		}
		return new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant);
	}

	/// <summary>Take a snapshot unless nothing changed since the last one (or force). Returns it, or null when skipped.</summary>
	public async Task<BackupInfo?> TakeAsync(bool force = false, CancellationToken ct = default) {
		await gate.WaitAsync(ct);
		try {
			var skipped = new List<string>();
			var files = Included(skipped);
			var encrypt = Encrypting;
			var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
				string.Join("\n", files.Select(f => $"{f.Relative}\t{f.File.Length}\t{f.File.LastWriteTimeUtc.Ticks}")) + $"\nencrypt={encrypt}")));
			System.IO.Directory.CreateDirectory(Directory);
			if(!force && File.Exists(FingerprintPath) && File.ReadAllText(FingerprintPath).Trim() == fingerprint && List().Count > 0) return null;

			var now = DateTimeOffset.UtcNow;
			var name = $"{Prefix}{now.UtcDateTime.ToString(Stamp)}.tar.gz{(encrypt ? ".enc" : "")}";
			var path = System.IO.Path.Combine(Directory, name);
			var partial = path + ".partial";
			await using(var file = new FileStream(partial, FileMode.Create, FileAccess.Write)) {
				Stream outer = encrypt ? new EncryptingStream(file, File.ReadAllText(PassphrasePath).Trim(), leaveOpen: true) : file;
				await using(var gz = new GZipStream(outer, CompressionLevel.Optimal, leaveOpen: true))
				await using(var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true)) {
					var manifest = new StringBuilder($"Marten snapshot of {home.Root}, taken {now:O}\n");
					foreach(var (rel, info) in files) {
						ct.ThrowIfCancellationRequested();
						byte[] data;
						try {
							// Read what's there now. Files are written atomically or appended, so this is a consistent prefix.
							await using var fs = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
							data = new byte[fs.Length];
							await fs.ReadExactlyAsync(data, ct);
						} catch(Exception e) when(e is IOException or UnauthorizedAccessException) { skipped.Add($"{rel} (unreadable: {e.Message})"); continue; }
						var entry = new PaxTarEntry(TarEntryType.RegularFile, rel) { ModificationTime = info.LastWriteTimeUtc, DataStream = new MemoryStream(data) };
						if(!OperatingSystem.IsWindows()) entry.Mode = info.UnixFileMode;
						await tar.WriteEntryAsync(entry, ct);
					}
					if(skipped.Count > 0) manifest.Append("Left out:\n").AppendJoin("", skipped.Select(s => $"  {s}\n"));
					await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ".marten-backup.txt") {
						DataStream = new MemoryStream(Encoding.UTF8.GetBytes(manifest.ToString())), ModificationTime = now,
					}, ct);
				}
				if(outer != file) await outer.DisposeAsync();
				await file.FlushAsync(ct);
			}
			File.Move(partial, path, overwrite: true);
			AtomicFile.Write(FingerprintPath, fingerprint);
			Prune();
			return new BackupInfo(path, now, new FileInfo(path).Length, encrypt);
		} finally { gate.Release(); }
	}

	/// <summary>Keep the newest KeepRecent, plus the newest of each of the last KeepDays days; delete the rest.</summary>
	public void Prune() {
		var s = settings();
		var all = List();
		var keep = all.Take(Math.Max(1, s.KeepRecent)).ToHashSet();
		var cutoff = DateTimeOffset.UtcNow.Date.AddDays(-s.KeepDays);
		foreach(var day in all.Where(b => b.Taken.UtcDateTime >= cutoff).GroupBy(b => b.Taken.UtcDateTime.Date)) keep.Add(day.First());
		foreach(var b in all.Where(b => !keep.Contains(b))) File.Delete(b.Path);
		foreach(var stale in System.IO.Directory.GetFiles(Directory, "*.partial")) File.Delete(stale);
	}

	/// <summary>Extract a snapshot into an empty or new folder. Never writes into a live ~/.marten.</summary>
	public static async Task RestoreAsync(string archive, string target, string? passphrase, CancellationToken ct = default) {
		target = System.IO.Path.GetFullPath(target);
		if(System.IO.Directory.Exists(target) && System.IO.Directory.EnumerateFileSystemEntries(target).Any())
			throw new IOException($"{target} isn't empty; restore into a new folder.");
		System.IO.Directory.CreateDirectory(target);
		await using var file = File.OpenRead(archive);
		Stream inner = file;
		if(archive.EndsWith(".enc")) {
			if(string.IsNullOrWhiteSpace(passphrase)) throw new InvalidOperationException("This snapshot is encrypted; a passphrase is needed.");
			inner = new DecryptingStream(file, passphrase.Trim());
		}
		await using var gz = new GZipStream(inner, CompressionMode.Decompress);
		await using var tar = new TarReader(gz);
		var root = target + System.IO.Path.DirectorySeparatorChar;
		while(await tar.GetNextEntryAsync(copyData: false, ct) is { } entry) {
			if(entry.EntryType != TarEntryType.RegularFile || entry.DataStream == null) continue;
			var dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(target, entry.Name));
			if(!dest.StartsWith(root)) throw new IOException($"Refusing to restore {entry.Name} outside {target}.");
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
			await using(var o = File.Create(dest)) await entry.DataStream.CopyToAsync(o, ct);
			File.SetLastWriteTimeUtc(dest, entry.ModificationTime.UtcDateTime);
			if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(dest, entry.Mode);
		}
	}

	/// <summary>Takes snapshots on the configured interval until stopped.</summary>
	public void Start(CancellationToken stop, Action<string> log) => _ = Task.Run(async () => {
		while(!stop.IsCancellationRequested) {
			var minutes = settings().IntervalMinutes;
			if(minutes > 0) {
				var last = List().FirstOrDefault()?.Taken ?? DateTimeOffset.MinValue;
				if(DateTimeOffset.UtcNow - last >= TimeSpan.FromMinutes(minutes))
					try { await TakeAsync(ct: stop); } catch(OperationCanceledException) { break; } catch(Exception e) { log($"Backup failed: {e.Message}"); }
			}
			try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch(OperationCanceledException) { break; }
		}
	});
}

/// <summary>
/// AES-256-GCM in 1 MiB chunks. Header: "MARTENBK", version 1, 16-byte salt, PBKDF2 iterations (int32 BE), 7-byte nonce
/// prefix. Each chunk: int32 BE length with the top bit set on the last chunk, ciphertext, 16-byte tag. The nonce is the
/// prefix, a 4-byte chunk counter and a last-chunk byte, so reordering, dropping or truncating chunks fails to decrypt.
/// </summary>
static class Chunked {
	public static readonly byte[] Magic = "MARTENBK"u8.ToArray();
	public const int ChunkSize = 1 << 20, TagSize = 16, Iterations = 600_000, HeaderSize = 8 + 1 + 16 + 4 + 7;

	public static byte[] Key(string passphrase, byte[] salt, int iterations) =>
		Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);

	public static byte[] Nonce(byte[] prefix, uint counter, bool last) {
		var n = new byte[12];
		prefix.CopyTo(n, 0);
		BinaryPrimitives.WriteUInt32BigEndian(n.AsSpan(7), counter);
		n[11] = (byte) (last ? 1 : 0);
		return n;
	}
}

sealed class EncryptingStream : Stream {
	readonly Stream inner; readonly bool leaveOpen; readonly AesGcm aes; readonly byte[] prefix, header;
	readonly byte[] buffer = new byte[Chunked.ChunkSize]; int filled; uint counter; bool done;

	public EncryptingStream(Stream inner, string passphrase, bool leaveOpen = false) {
		this.inner = inner; this.leaveOpen = leaveOpen;
		var salt = RandomNumberGenerator.GetBytes(16);
		prefix = RandomNumberGenerator.GetBytes(7);
		header = new byte[Chunked.HeaderSize];
		Chunked.Magic.CopyTo(header, 0);
		header[8] = 1;
		salt.CopyTo(header, 9);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(25), Chunked.Iterations);
		prefix.CopyTo(header, 29);
		aes = new AesGcm(Chunked.Key(passphrase, salt, Chunked.Iterations), Chunked.TagSize);
		inner.Write(header);
	}

	void Emit(bool last) {
		var cipher = new byte[filled];
		var tag = new byte[Chunked.TagSize];
		aes.Encrypt(Chunked.Nonce(prefix, counter++, last), buffer.AsSpan(0, filled), cipher, tag, header);
		var len = new byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(len, (uint) filled | (last ? 0x8000_0000u : 0));
		inner.Write(len); inner.Write(cipher); inner.Write(tag);
		filled = 0;
	}

	public override void Write(byte[] data, int offset, int count) => Write(data.AsSpan(offset, count));
	public override void Write(ReadOnlySpan<byte> data) {
		while(data.Length > 0) {
			if(filled == buffer.Length) Emit(false);
			var n = Math.Min(data.Length, buffer.Length - filled);
			data[..n].CopyTo(buffer.AsSpan(filled));
			filled += n; data = data[n..];
		}
	}
	protected override void Dispose(bool disposing) {
		if(disposing && !done) {
			done = true;
			Emit(true);
			inner.Flush();
			aes.Dispose();
			if(!leaveOpen) inner.Dispose();
		}
		base.Dispose(disposing);
	}
	public override ValueTask DisposeAsync() { Dispose(true); GC.SuppressFinalize(this); return ValueTask.CompletedTask; }
	public override void Flush() { }
	public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
	public override long Length => throw new NotSupportedException();
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
	public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
	public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
	public override void SetLength(long v) => throw new NotSupportedException();
}

sealed class DecryptingStream : Stream {
	readonly Stream inner; readonly AesGcm aes; readonly byte[] prefix, header = new byte[Chunked.HeaderSize];
	byte[] plain = []; int pos; uint counter; bool sawLast;

	public DecryptingStream(Stream inner, string passphrase) {
		this.inner = inner;
		inner.ReadExactly(header);
		if(!header.AsSpan(0, 8).SequenceEqual(Chunked.Magic) || header[8] != 1) throw new InvalidDataException("Not a Marten encrypted snapshot.");
		var iterations = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(25));
		prefix = header[29..36];
		aes = new AesGcm(Chunked.Key(passphrase, header[9..25], iterations), Chunked.TagSize);
	}

	bool Next() {
		if(sawLast) return false;
		var len = new byte[4];
		var got = inner.ReadAtLeast(len, 4, throwOnEndOfStream: false);
		if(got < 4) throw new InvalidDataException("The snapshot is truncated.");
		var raw = BinaryPrimitives.ReadUInt32BigEndian(len);
		var last = (raw & 0x8000_0000u) != 0;
		var size = (int) (raw & 0x7fff_ffffu);
		if(size > Chunked.ChunkSize) throw new InvalidDataException("The snapshot is corrupt.");
		var cipher = new byte[size]; var tag = new byte[Chunked.TagSize];
		inner.ReadExactly(cipher); inner.ReadExactly(tag);
		plain = new byte[size];
		try { aes.Decrypt(Chunked.Nonce(prefix, counter++, last), cipher, tag, plain, header); }
		catch(AuthenticationTagMismatchException) { throw new CryptographicException("Wrong passphrase, or the snapshot was altered."); }
		pos = 0; sawLast = last;
		return true;
	}

	public override int Read(byte[] b, int o, int c) => Read(b.AsSpan(o, c));
	public override int Read(Span<byte> dest) {
		while(pos == plain.Length) if(!Next()) return 0;
		var n = Math.Min(dest.Length, plain.Length - pos);
		plain.AsSpan(pos, n).CopyTo(dest);
		pos += n;
		return n;
	}
	protected override void Dispose(bool disposing) { if(disposing) { aes.Dispose(); inner.Dispose(); } base.Dispose(disposing); }
	public override void Flush() { }
	public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
	public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
	public override void SetLength(long v) => throw new NotSupportedException();
	public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}
