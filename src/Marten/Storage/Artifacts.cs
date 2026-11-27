using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Marten.Storage;

public sealed record ArtifactRef(string Id, string Name, long Bytes, string PreviewKind);

internal sealed class Artifacts {
	public const string Staging = "artifact-inbox", Store = "artifacts";
	public const int MaxBytes = 8 << 20, MaxText = 64 << 10, MaxPng = 2 << 20;
	const long AgentQuota = 256L << 20, HostQuota = 2L << 30;
	const int AgentCount = 1024, HostCount = 16_384; // tiny/empty files also consume disk and transcript space
	readonly object gate = new();
	readonly Dictionary<string, (long Bytes, int Count)> usage = new(StringComparer.Ordinal);
	long totalBytes;
	int totalCount;

	public static bool IdValid(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
	static bool CanFailClosed(Exception e) => e is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException;

	public void Recover(string directory, Transcript transcript) {
		if(!ArtifactFiles.Supported) return;
		try { using var inbox = ArtifactFiles.DirectoryAt(directory, Staging, create: true); }
		catch(Exception e) when(CanFailClosed(e)) { /* The tool will fail closed for an unsafe or unsupported inbox. */ }
		if(!Directory.Exists(Path.Combine(directory, Store))) return;
		try {
			using var dir = ArtifactFiles.DirectoryAt(directory, Store, create: false);
			foreach(var file in Directory.EnumerateFiles(Path.Combine(directory, Store))) {
				var key = Path.GetFileName(file);
				var temporary = key.EndsWith(".tmp", StringComparison.Ordinal) && IdValid(key[..^4]);
				if(!temporary && !IdValid(key)) continue;
				if(temporary || transcript.Artifact(key) == null) {
					if(ArtifactFiles.Delete(dir, key)) continue;
				}
				try {
					using var blob = ArtifactFiles.Read(dir, key, out var size);
					Account(directory, size, 1, enforce: false);
				} catch(Exception e) when(CanFailClosed(e)) { /* Unreadable entries cannot be served. */ }
			}
		} catch(Exception e) when(CanFailClosed(e)) { /* No fallback to unchecked paths. */ }
	}

	void Account(string directory, long bytes, int count, bool enforce) {
		lock(gate) {
			var old = usage.GetValueOrDefault(directory);
			if(enforce && (old.Bytes + bytes > AgentQuota || totalBytes + bytes > HostQuota || old.Count + count > AgentCount || totalCount + count > HostCount))
				throw new IOException("Artifact storage quota reached.");
			usage[directory] = (old.Bytes + bytes, old.Count + count);
			totalBytes += bytes;
			totalCount += count;
		}
	}

	public async Task<ArtifactRef> Publish(string directory, string filename, string? label, CancellationToken ct) {
		if(filename.Length is 0 or > 160 || filename[0] == '.' || filename.Any(c => char.IsControl(c) || c is '/' or '\\' or ':'))
			throw new IOException("Use a single filename in your artifact-inbox.");
		var name = DisplayName(label, filename);
		using var inbox = ArtifactFiles.DirectoryAt(directory, Staging, create: false);
		await using var source = ArtifactFiles.Read(inbox, filename, out var size);
		if(size is < 0 or > MaxBytes) throw new IOException("Artifact exceeds the 8 MiB limit.");
		using var dir = ArtifactFiles.DirectoryAt(directory, Store, create: true);
		var id = Guid.NewGuid().ToString("N");
		var temp = id + ".tmp";
		var ext = Path.GetExtension(filename).ToLowerInvariant();
		var candidate = ext == ".png" && size <= MaxPng ? "png" : ext is ".txt" or ".md" or ".log" or ".csv" or ".tsv" or ".json" ? "text" : "none";
		var sample = new byte[(int) Math.Min(size, candidate == "png" ? MaxPng : candidate == "text" ? MaxText : 0)];
		Account(directory, size, 1, enforce: true);
		bool madeTemp = false, committed = false;
		try {
			await using(var destination = ArtifactFiles.Create(dir, temp)) {
				madeTemp = true;
				var buffer = ArrayPool<byte>.Shared.Rent(MaxText);
				try {
					long copied = 0;
					while(copied < size) {
						int read = await source.ReadAsync(buffer.AsMemory(0, (int) Math.Min(MaxText, size - copied)), ct);
						if(read == 0) throw new IOException("Artifact changed size during publishing; retry after writing is complete.");
						await destination.WriteAsync(buffer.AsMemory(0, read), ct);
						if(copied < sample.Length) buffer.AsSpan(0, Math.Min(read, sample.Length - (int) copied)).CopyTo(sample.AsSpan((int) copied));
						copied += read;
					}
					if(await source.ReadAsync(buffer.AsMemory(0, 1), ct) != 0)
						throw new IOException("Artifact changed size during publishing; retry after writing is complete.");
				} finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
				ct.ThrowIfCancellationRequested();
				destination.Flush(flushToDisk: true);
			}
			var kind = candidate switch {
				"png" when SafePng(sample) => "png",
				"text" when TextPrefix(sample, size > sample.Length) >= 0 => "text",
				_ => "none",
			};
			ct.ThrowIfCancellationRequested();
			ArtifactFiles.Finalize(dir, temp, id);
			committed = true;
			return new(id, name, size, kind);
		} finally {
			if(!committed && (!madeTemp || ArtifactFiles.Delete(dir, temp))) Account(directory, -size, -1, enforce: false);
		}
	}

	public void Discard(string directory, ArtifactRef item) {
		try {
			using var dir = ArtifactFiles.DirectoryAt(directory, Store, create: false);
			if(ArtifactFiles.Delete(dir, item.Id)) Account(directory, -item.Bytes, -1, enforce: false);
		} catch(Exception e) when(CanFailClosed(e)) { /* Retry orphan cleanup at startup. */ }
	}

	public static FileStream? Open(string directory, ArtifactRef item) {
		if(!IdValid(item.Id) || item.Bytes is < 0 or > MaxBytes) return null;
		try {
			using var dir = ArtifactFiles.DirectoryAt(directory, Store, create: false);
			var stream = ArtifactFiles.Read(dir, item.Id, out var size);
			if(size == item.Bytes) return stream;
			stream.Dispose();
		} catch(Exception e) when(CanFailClosed(e)) { }
		return null;
	}

	static string DisplayName(string? label, string filename) {
		static string Clean(string value) => new string(value.Where(c => !char.IsControl(c) && !char.IsSurrogate(c) &&
			char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format && c is not ('/' or '\\' or ':' or '"' or '*' or '?' or '<' or '>' or '|')).Take(120).ToArray()).Trim(' ', '.');
		// Keep the source extension rather than allowing the label to give downloaded bytes a different executable type.
		var ext = Path.GetExtension(filename);
		ext = ext.Length <= 12 && ext.Skip(1).All(char.IsAsciiLetterOrDigit) ? ext : "";
		var stem = Clean(string.IsNullOrWhiteSpace(label) ? Path.GetFileNameWithoutExtension(filename) : label);
		if(ext.Length > 0 && stem.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) stem = stem[..^ext.Length].Trim(' ', '.');
		return (stem.Length == 0 ? "artifact" : stem) + ext;
	}

	internal static int TextPrefix(ReadOnlySpan<byte> utf8, bool clipped) {
		int valid = 0;
		while(valid < utf8.Length) {
			var status = Rune.DecodeFromUtf8(utf8[valid..], out var rune, out var count);
			if(status == OperationStatus.NeedMoreData && clipped) return valid;
			if(status != OperationStatus.Done || (Rune.IsControl(rune) && rune.Value is not (9 or 10 or 13))) return -1;
			valid += count;
		}
		return valid;
	}

	internal static bool SafePng(ReadOnlySpan<byte> bytes) {
		if(bytes.Length is < 45 or > MaxPng || !bytes[..8].SequenceEqual(new byte[] {137, 80, 78, 71, 13, 10, 26, 10})) return false;
		var data = bytes[8..];
		bool header = false, pixels = false;
		while(data.Length >= 12) {
			var length = BinaryPrimitives.ReadUInt32BigEndian(data);
			if(length > data.Length - 12) return false;
			var n = (int) length;
			var type = data.Slice(4, 4);
			if(type.SequenceEqual("IHDR"u8)) {
				if(header || n != 13) return false;
				var w = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
				var h = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
				if(w is 0 or > 8192 || h is 0 or > 8192 || w * h > 8_000_000) return false;
				header = true;
			} else if(!header) return false;
			else if(type.SequenceEqual("IDAT"u8)) pixels |= n > 0;
			else if(type.SequenceEqual("IEND"u8)) return pixels && n == 0 && data.Length == 12;
			// Minimal allowlist: no animation, compressed metadata or other ancillary payloads.
			else if(!type.SequenceEqual("PLTE"u8) && !type.SequenceEqual("tRNS"u8)) return false;
			data = data[(n + 12)..];
		}
		return false;
	}
}
