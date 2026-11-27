using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marten.Storage;

internal sealed record JevStatus(bool HasKey, bool Enabled, bool ImagePreviewEnabled, bool StorageHealthy);
internal enum JevChange { Saved, InvalidKey, KeyRequired, StorageUnavailable }

/// <summary>An in-process capability for one enabled settings generation. Never serialize it or give it to an agent.</summary>
internal sealed class JevGrant(string key, bool imagePreviewEnabled) {
	readonly CancellationTokenSource revocation = new();
	internal bool ImagePreviewEnabled { get; } = imagePreviewEnabled;
	internal CancellationToken Revoked => revocation.Token;
	internal string GetKeyForProvider() => key;
	internal async Task RetireAsync() {
		// CancelAsync marks the token canceled synchronously, but invokes callbacks asynchronously.
		try { await revocation.CancelAsync(); }
		catch(AggregateException) { /* Never report callback exceptions, which could contain credentials. */ }
	}
}

internal sealed class JevSettings(Home home) {
	internal const string RecordName = "jev-settings.json";
	internal const int MaxRecordBytes = 16 * 1024;
	const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
	const UnixFileMode PrivateDirectory = PrivateFile | UnixFileMode.UserExecute;
	internal static readonly JsonSerializerOptions StrictJson = new(JsonIO.Options) {
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		AllowDuplicateProperties = false,
		MaxDepth = 4,
	};

	sealed class Stored {
		[JsonRequired] public int Version { get; init; } = 1;
		[JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Key { get; init; }
		[JsonRequired] public bool Enabled { get; init; }
		[JsonRequired] public bool ImagePreviewEnabled { get; init; }
		public bool Matches(Stored other) => Version == other.Version && Key == other.Key &&
			Enabled == other.Enabled && ImagePreviewEnabled == other.ImagePreviewEnabled;
	}

	readonly object gate = new();
	readonly string directory = home.Secrets;
	readonly string path = Path.Combine(home.Secrets, RecordName);
	bool initialized;
	Stored? stored;
	JevGrant? grant;

	internal JevStatus Status() {
		JevGrant? retired = null;
		try {
			lock(gate) {
				CheckDisk(ref retired);
				return new(stored?.Key != null, grant != null, stored?.ImagePreviewEnabled == true, stored != null);
			}
		} finally { Retire(retired); }
	}

	internal JevGrant? TryAcquire() {
		JevGrant? retired = null;
		try {
			lock(gate) {
				CheckDisk(ref retired);
				return grant;
			}
		} finally { Retire(retired); }
	}

	/// <summary>Recheck immediately before outbound work and before accepting/publishing its result.
	/// This is a cooperative fence, not a transaction around later broker side effects.</summary>
	internal bool IsCurrent(JevGrant? candidate) => candidate != null && ReferenceEquals(TryAcquire(), candidate);

	internal JevChange Replace(string? key) => JevKeyFormat.IsValid(key) ? Change(Operation.Replace, key) : JevChange.InvalidKey;
	internal JevChange SetOptions(bool enabled, bool imagePreviewEnabled) => Change(Operation.Options, null, enabled, imagePreviewEnabled);
	internal JevChange Remove() => Change(Operation.Remove);

	enum Operation { Replace, Options, Remove }

	JevChange Change(Operation operation, string? key = null, bool enabled = false, bool imagePreviewEnabled = false) {
		JevGrant? retired = null;
		try {
			lock(gate) {
				CheckDisk(ref retired);
				if(operation == Operation.Options) {
					if(stored == null) return JevChange.StorageUnavailable;
					if(stored.Key == null && (enabled || imagePreviewEnabled)) return JevChange.KeyRequired;
					if(stored.Enabled == enabled && stored.ImagePreviewEnabled == imagePreviewEnabled) return JevChange.Saved;
					key = stored.Key;
				} else if(operation == Operation.Replace) {
					enabled = stored?.Enabled == true;
					imagePreviewEnabled = stored?.ImagePreviewEnabled == true;
				}
				// Withdraw capability before attempting persistence, including on removal and I/O failure.
				// A removal writes one keyless, disabled record, with no delete/re-enable window.
				Install(null, ref retired);
				var next = new Stored { Key = key, Enabled = enabled, ImagePreviewEnabled = imagePreviewEnabled };
				try { Save(next); }
				catch(Exception ex) when(StorageError(ex)) { return JevChange.StorageUnavailable; }
				Install(next, ref retired);
				return JevChange.Saved;
			}
		} finally { Retire(retired); }
	}

	void Install(Stored? next, ref JevGrant? retired) {
		if(grant != null) retired = grant;
		stored = next;
		grant = next is { Enabled: true, Key: { } key } ? new(key, next.ImagePreviewEnabled) : null;
	}

	void CheckDisk(ref JevGrant? retired) {
		// Once storage is invalid or changed out of band, require explicit repair. Do not pick up
		// credentials restored from an old generation, including after a failed durable mutation.
		if(initialized && stored == null) return;
		Stored? disk;
		try { disk = Load(); }
		catch(Exception ex) when(StorageError(ex)) { disk = null; }
		if(!initialized) {
			initialized = true;
			Install(disk, ref retired);
		} else if(disk == null || !stored!.Matches(disk)) Install(null, ref retired);
	}

	static void Retire(JevGrant? retired) {
		// Never call cancellation under gate; callbacks can reenter settings.
		if(retired != null) _ = retired.RetireAsync();
	}

	static bool StorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException;

	void CheckDirectory() {
		if(OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
		var dir = new DirectoryInfo(directory);
		if(!dir.Exists || dir.LinkTarget != null || File.GetUnixFileMode(directory) != PrivateDirectory) throw new IOException();
	}

	Stored? Load() {
		if(OperatingSystem.IsWindows()) return null;
		CheckDirectory();
		var file = new FileInfo(path);
		if(file.LinkTarget != null) return null;
		try {
			if((File.GetAttributes(path) & FileAttributes.Directory) != 0 || File.GetUnixFileMode(path) != PrivateFile ||
				file.Length is <= 0 or > MaxRecordBytes) return null;
		} catch(FileNotFoundException) { return new(); }
		using var stream = File.OpenRead(path);
		var bytes = new byte[(int)file.Length];
		try {
			stream.ReadExactly(bytes);
			if(stream.ReadByte() != -1) return null;
			var result = JsonSerializer.Deserialize<Stored>(bytes, StrictJson);
			return result is { Version: 1 } && (result.Key == null ? !result.Enabled && !result.ImagePreviewEnabled : JevKeyFormat.IsValid(result.Key)) ? result : null;
		} catch(InvalidOperationException) { return null; } // E.g. invalid UTF-8 in a JSON string.
		finally { CryptographicOperations.ZeroMemory(bytes); }
	}

	void Save(Stored next) {
		if(OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
		CheckDirectory();
		var tmp = Path.Combine(directory, $".{RecordName}.{Guid.NewGuid():N}.tmp");
		var bytes = JsonSerializer.SerializeToUtf8Bytes(next, StrictJson);
		var created = false;
		try {
			using(var stream = new FileStream(tmp, new FileStreamOptions {
				Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = PrivateFile,
			})) {
				created = true;
				// A restrictive umask may remove permissions; set/check exact mode before writing any key bytes.
				File.SetUnixFileMode(tmp, PrivateFile);
				stream.Write(bytes);
				stream.Flush(flushToDisk: true);
			}
			File.Move(tmp, path, overwrite: true);
			created = false;
		} finally {
			CryptographicOperations.ZeroMemory(bytes);
			if(created) File.Delete(tmp);
		}
	}
}
