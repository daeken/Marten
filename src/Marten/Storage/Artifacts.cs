using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace Marten.Storage;

// Unset size is distinct from a valid empty snapshot when reading a persisted ref.
public sealed record ArtifactRef(string Id, string Name, long Bytes = -1, string PreviewKind = "");

internal sealed class Artifacts : IDisposable {
	public const string Staging = "artifact-inbox", Store = "artifact-store";
	public const int MaxBytes = 8 << 20, MaxText = 64 << 10, MaxPng = 2 << 20;
	const long AgentQuota = 256L << 20, HostQuota = 2L << 30;
	const int AgentCount = 1024, HostCount = 16_384; // tiny/empty files also consume disk and transcript space
	readonly object gate = new();
	readonly Dictionary<string, (long Bytes, int Count)> usage = new(StringComparer.Ordinal);
	long totalBytes;
	int totalCount;

	public static bool IdValid(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
	internal static bool RefValid(ArtifactRef? item) => item != null && IdValid(item.Id) &&
		item.Bytes is >= 0 and <= MaxBytes && item.PreviewKind is "none" or "text" or "png" &&
		item.Name is { Length: > 0 and <= 125 } name && name == name.Trim(' ', '.') && name.All(SafeDisplayCharacter);
	static bool CanFailClosed(Exception e) => e is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException;

	readonly SafeFileHandle? root, data;
	readonly ArtifactFiles.HomeLease? localLease;
	readonly Action<string> warning;
	readonly Action<string>? fault;
	readonly Func<Guid>? nextId;
	volatile bool enabled;
	public bool Enabled => enabled;

	public Artifacts(Home home, Action<string> warning, Action<string>? fault = null, ArtifactFiles.HomeLease? ownedHome = null, bool useOwnedHomeOnly = false, Func<Guid>? nextId = null) {
		this.warning = warning;
		this.fault = fault;
		this.nextId = nextId;
		try {
			if(ownedHome == null && !useOwnedHomeOnly) localLease = ArtifactFiles.AcquireHome(home.Root, warning);
			root = (ownedHome ?? localLease)?.Root;
			if(root == null) return;
			ArtifactFiles.RequireSameDirectory(root, home.Root);
			using var agents = ArtifactFiles.DirectoryAt(root, "agents");
			data = ArtifactFiles.DirectoryAt(root, Store, create: true, privateMode: true);
			Sync(root, "home namespace");
			Recover();
			var key = "probe-" + Guid.NewGuid().ToString("N");
			using(var f = ArtifactFiles.Create(data, key + ".tmp")) { f.WriteByte(1); f.Flush(flushToDisk: true); }
			ArtifactFiles.Finalize(data, key + ".tmp", key);
			using(var f = ArtifactFiles.Read(data, key, out var length, privateMode: true))
				if(length != 1 || f.ReadByte() != 1) throw new IOException("Artifact storage probe failed.");
			if(!ArtifactFiles.Delete(data, key)) throw new IOException("Could not remove the artifact storage probe.");
			Sync(data, "store namespace");
			enabled = true;
		} catch(Exception e) when(CanFailClosed(e)) {
			warning($"Artifact publishing disabled: {e.Message}");
			Dispose();
		}
	}

	void Sync(SafeFileHandle dir, string point) {
		fault?.Invoke(point);
		ArtifactFiles.SyncNamespace(dir);
	}

	static bool SafeAgentId(string id) => id.Length is > 0 and <= 96 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
	static string Key(string agentId, string artifactId) => agentId + "." + artifactId;

	void Recover() {
		foreach(var key in ArtifactFiles.Names(data!, HostCount * 2 + 1)) {
			var stem = key.EndsWith(".tmp", StringComparison.Ordinal) ? key[..^4] : key;
			var temporary = stem.Length != key.Length;
			var parts = stem.Split('.');
			var probe = parts.Length == 1 && stem.StartsWith("probe-", StringComparison.Ordinal) && IdValid(stem[6..]);
			if(!probe && (parts.Length != 2 || !SafeAgentId(parts[0]) || !IdValid(parts[1])))
				throw new IOException("Unexpected entry in artifact store; inventory is not verified.");
			long size;
			using(var blob = ArtifactFiles.Read(data!, key, out size, privateMode: true)) {
				if(size is < 0 or > MaxBytes) throw new IOException("Invalid artifact size in inventory.");
			}
			if(temporary || probe) {
				if(!ArtifactFiles.Delete(data!, key)) throw new IOException("Cannot clean a temporary artifact.");
			} else {
				// Finalized blobs may outlive an indeterminate append. Never delete or omit them from quota.
				Account(parts[0], size, 1, enforce: false);
			}
		}
		Sync(data!, "inventory namespace");
	}

	public void PrepareAgent(string agentId) {
		if(!Enabled) return;
		try {
			using var agents = ArtifactFiles.DirectoryAt(root!, "agents");
			using var agent = OpenAgent(agents, agentId);
			using var inbox = ArtifactFiles.DirectoryAt(agent, Staging, create: true, privateMode: true);
		} catch(Exception e) when(CanFailClosed(e)) {
			warning($"Artifact staging unavailable for an agent: {e.Message}");
		}
	}

	static SafeFileHandle OpenAgent(SafeFileHandle agents, string agentId) => SafeAgentId(agentId)
		? ArtifactFiles.DirectoryAt(agents, agentId) : throw new IOException("Unsafe artifact owner ID.");

	public void Dispose() {
		enabled = false;
		localLease?.Dispose();
		data?.Dispose();
	}

	void StopWrites() {
		enabled = false;
		warning("Artifact publishing disabled: filesystem state was indeterminate; restart to verify artifact inventory.");
	}

	void Account(string directory, long bytes, int count, bool enforce) {
		lock(gate) {
			var old = usage.GetValueOrDefault(directory);
			if(enforce && (!Enabled || old.Bytes + bytes > AgentQuota || totalBytes + bytes > HostQuota || old.Count + count > AgentCount || totalCount + count > HostCount))
				throw new IOException("Artifact storage quota reached.");
			usage[directory] = (old.Bytes + bytes, old.Count + count);
			totalBytes += bytes;
			totalCount += count;
		}
	}

	public async Task<ArtifactRef> Publish(string agentId, string filename, string? label, CancellationToken ct, string? runtimeDirectory = null) {
		if(filename.Length is 0 or > 160 || filename[0] == '.' || filename.Any(c => char.IsControl(c) || c is '/' or '\\' or ':'))
			throw new IOException("Use a single filename in your artifact-inbox.");
		var name = DisplayName(label, filename);
		if(!Enabled) throw new IOException("Artifact publishing is disabled.");
		using var agents = ArtifactFiles.DirectoryAt(root!, "agents");
		using var agent = OpenAgent(agents, agentId);
		if(runtimeDirectory != null) ArtifactFiles.RequireSameDirectory(agent, runtimeDirectory);
		using var inbox = ArtifactFiles.DirectoryAt(agent, Staging, privateMode: true);
		await using var source = ArtifactFiles.Read(inbox, filename, out var size);
		if(size is < 0 or > MaxBytes) throw new IOException("Artifact exceeds the 8 MiB limit.");
		var dir = data!;
		var id = (nextId?.Invoke() ?? Guid.NewGuid()).ToString("N");
		var key = Key(agentId, id);
		var temp = key + ".tmp";
		var ext = Path.GetExtension(filename).ToLowerInvariant();
		var candidate = ext == ".png" && size <= MaxPng ? "png" : ext is ".txt" or ".md" or ".log" or ".csv" or ".tsv" or ".json" ? "text" : "none";
		var sample = new byte[(int) Math.Min(size, candidate == "png" ? MaxPng : candidate == "text" ? MaxText : 0)];
		Account(agentId, size, 1, enforce: true);
		bool ownsTemp = false, finalizing = false, finalized = false;
		try {
			await using(var destination = ArtifactFiles.Create(dir, temp, () => ownsTemp = true)) {
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
			finalizing = true;
			ArtifactFiles.Finalize(dir, temp, key);
			finalized = true;
			Sync(dir, "snapshot namespace");
			return new(id, name, size, kind);
		} catch(Exception e) when((!ownsTemp || finalizing) && CanFailClosed(e)) {
			StopWrites(); // Inventory may not match our reservation. Keep it until restart.
			throw;
		} finally {
			if(ownsTemp && !finalized) {
				try {
					if(!ArtifactFiles.Delete(dir, temp)) throw new IOException("Temporary artifact state indeterminate.");
					Sync(dir, "temporary removal namespace");
					if(!finalizing) Account(agentId, -size, -1, enforce: false);
				} catch(Exception e) when(CanFailClosed(e)) { if(Enabled) StopWrites(); }
			}
		}
	}

	public Entry Commit(string agentId, Transcript log, Entry entry) {
		try {
			if(!Enabled) throw new IOException("Artifact writes disabled before the result commit.");
			using var agents = ArtifactFiles.DirectoryAt(root!, "agents");
			using var agent = OpenAgent(agents, agentId);
			using var transcript = ArtifactFiles.DirectoryAt(agent, "transcript");
			ArtifactFiles.RequireSameDirectory(transcript, log.DirectoryPath);
			Sync(root!, "home namespace");
			Sync(agents, "agents namespace");
			Sync(agent, "agent namespace");
			return log.AppendArtifact(entry, name => ArtifactFiles.OpenLog(transcript, name), () => Sync(transcript, "transcript namespace"), () => fault?.Invoke("after result flush"));
		} catch(Exception e) when(CanFailClosed(e)) {
			StopWrites(); // Preserve files/accounting. The append may actually be durable.
			throw;
		}
	}

	public FileStream? Open(string agentId, ArtifactRef item, Transcript? log = null) {
		if(data is not { IsClosed: false } || !SafeAgentId(agentId) || !IdValid(item.Id) || item.Bytes is < 0 or > MaxBytes) return null;
		try {
			if(log != null) {
				using var agents = ArtifactFiles.DirectoryAt(root!, "agents");
				using var agent = OpenAgent(agents, agentId);
				using var transcript = ArtifactFiles.DirectoryAt(agent, "transcript");
				ArtifactFiles.RequireSameDirectory(transcript, log.DirectoryPath);
			}
			var stream = ArtifactFiles.Read(data, Key(agentId, item.Id), out var size, privateMode: true);
			if(size == item.Bytes) return stream;
			stream.Dispose();
		} catch(Exception e) when(CanFailClosed(e)) { }
		return null;
	}

	static bool SafeDisplayCharacter(char c) => !char.IsControl(c) && !char.IsSurrogate(c) &&
		char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format && c is not ('/' or '\\' or ':' or '"' or '*' or '?' or '<' or '>' or '|');

	static string DisplayName(string? label, string filename) {
		static string Clean(string value) => new string(value.Where(SafeDisplayCharacter).Take(120).ToArray()).Trim(' ', '.');
		// Keep the source extension rather than allowing the label to give downloaded bytes a different executable type.
		var ext = Path.GetExtension(filename);
		ext = ext.ToLowerInvariant() switch {
			".txt" or ".md" or ".log" or ".csv" or ".tsv" or ".json" or ".png" or ".jpg" or ".jpeg" or ".pdf" or ".zip" => ext.ToLowerInvariant(),
			_ => ".bin",
		};
		var stem = Clean(string.IsNullOrWhiteSpace(label) ? Path.GetFileNameWithoutExtension(filename) : label);
		if(ext.Length > 0 && stem.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) stem = stem[..^ext.Length].Trim(' ', '.');
		var device = stem.Split('.', 2)[0].Trim().ToUpperInvariant();
		if(device is "CON" or "PRN" or "AUX" or "NUL" || (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(device[3]))) stem = "";
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
