using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Marten.Storage;

/// <summary>Flat, descriptor-relative opens in a runtime-owned directory. No weak fallback.</summary>
internal static partial class ArtifactFiles {
	const int DirectoryType = 0x4000, RegularType = 0x8000, TypeMask = 0xf000;
	const int PrivateDirectory = 0x1c0, PrivateFile = 0x180; // 0700, 0600
	static bool Mac => OperatingSystem.IsMacOS();
	public static bool Supported => Mac || OperatingSystem.IsLinux();
	static int ReadFlags => Mac ? 0x1000104 : 0xa0800; // CLOEXEC | NOFOLLOW | NONBLOCK
	static int DirectoryFlag => Mac ? 0x100000 : 0x10000;
	static int NewFlags => ReadFlags | (Mac ? 0xa01 : 0xc1); // WRONLY | CREATE | EXCL

	public static SafeFileHandle PinHome(string path) {
		if(!Supported) throw new IOException("Artifact storage is unsupported on this host.");
		// The operator's configured home may intentionally be an alias. Follow it here only.
		var fd = Checked(Open(path, (ReadFlags & ~(Mac ? 0x100 : 0x20000)) | DirectoryFlag, 0));
		try { CheckDirectory(fd, null, false); return fd; }
		catch { fd.Dispose(); throw; }
	}

	public static SafeFileHandle DirectoryAt(SafeFileHandle parent, string name, bool create = false, bool privateMode = false) {
		ValidateLeaf(name);
		if(create && MkdirAt(parent, name, PrivateDirectory) != 0 && Marshal.GetLastPInvokeError() != 17)
			throw new IOException("Cannot create an artifact directory.");
		var dir = OpenChild(parent, name, ReadFlags | DirectoryFlag, 0);
		try { CheckDirectory(dir, parent, privateMode); return dir; }
		catch { dir.Dispose(); throw; }
	}

	static void CheckDirectory(SafeFileHandle dir, SafeFileHandle? parent, bool privateMode) {
		var stat = Inspect(dir);
		if((stat.Mode & TypeMask) != DirectoryType || (privateMode ? (stat.Mode & 0x1ff) != PrivateDirectory : (stat.Mode & 0x12) != 0) ||
			stat.Uid != EffectiveUid() || (parent != null && stat.Device != Inspect(parent).Device))
			throw new IOException("Unsafe artifact directory.");
	}

	public static void RequireSameDirectory(SafeFileHandle verified, string runtimePath) {
		// The string is only an identity check. All subsequent artifact I/O uses the verified FD.
		using var runtime = PinHome(runtimePath);
		var expected = Inspect(verified);
		var actual = Inspect(runtime);
		if(expected.Device != actual.Device || expected.Inode != actual.Inode) throw new IOException("Artifact directory identity changed.");
	}

	public static SafeFileHandle Lease(SafeFileHandle dir) {
		var file = OpenChild(dir, ".artifact-writer.lock", ReadFlags | (Mac ? 0x202 : 0x42), PrivateFile);
		try {
			CheckFile(dir, file, privateMode: true);
			if(Flock(file, 6) != 0) throw new IOException("Another process owns the artifact store or locking is unavailable.");
			return file;
		} catch { file.Dispose(); throw; }
	}

	public static string[] Names(SafeFileHandle dir, int limit) {
		// fdopendir owns its descriptor and the enumeration must report errors, not treat them as EOF.
		int dup = Duplicate(dir);
		if(dup < 0) throw new IOException("Cannot enumerate artifact storage.");
		var stream = FdOpenDir(dup);
		if(stream == 0) { Close(dup); throw new IOException("Cannot enumerate artifact storage."); }
		var names = new List<string>();
		try {
			unsafe {
				byte* entry = stackalloc byte[4096];
				for(;;) {
					if(ReadDir(stream, (nint) entry, out var result) != 0) throw new IOException("Incomplete artifact inventory.");
					if(result == 0) return names.ToArray();
					int offset = Mac ? 21 : 19; // Darwin dirent / Linux dirent64
					var bytes = new ReadOnlySpan<byte>(entry + offset, 256);
					int end = bytes.IndexOf((byte) 0);
					if(end < 0 || bytes[..end].ContainsAnyExceptInRange((byte) 0x20, (byte) 0x7e)) throw new IOException("Invalid artifact storage name.");
					var name = System.Text.Encoding.ASCII.GetString(bytes[..end]);
					if(name is "." or "..") continue;
					if(names.Count == limit) throw new IOException("Artifact inventory is too large.");
					names.Add(name);
				}
			}
		} finally { CloseDir(stream); }
	}

	public static FileStream Read(SafeFileHandle dir, string name, out long size, bool privateMode = false) {
		var file = OpenChild(dir, name, ReadFlags, 0);
		try {
			size = CheckFile(dir, file, privateMode);
			return new FileStream(file, FileAccess.Read, 64 * 1024, isAsync: false);
		} catch { file.Dispose(); throw; }
	}

	static long CheckFile(SafeFileHandle dir, SafeFileHandle file, bool privateMode) {
		var stat = Inspect(file);
		if((stat.Mode & TypeMask) != RegularType || stat.Links != 1 || stat.Device != Inspect(dir).Device ||
			(privateMode && (stat.Uid != EffectiveUid() || (stat.Mode & 0x1ff) != PrivateFile)))
			throw new IOException("Artifact must be a regular, singly linked file on the staging filesystem.");
		return stat.Size;
	}

	public static FileStream OpenLog(SafeFileHandle dir, string name) {
		var fd = OpenChild(dir, name, ReadFlags | (Mac ? 0x20a : 0x442), PrivateFile); // RDWR | CREAT | APPEND
		try {
			CheckFile(dir, fd, privateMode: false);
			return new FileStream(fd, FileAccess.ReadWrite, 64 * 1024, isAsync: false);
		} catch { fd.Dispose(); throw; }
	}

	public static FileStream Create(SafeFileHandle dir, string name) {
		var handle = OpenChild(dir, name, NewFlags, PrivateFile);
		try { return new FileStream(handle, FileAccess.Write, 64 * 1024, isAsync: false); }
		catch { handle.Dispose(); throw; }
	}

	public static void Finalize(SafeFileHandle dir, string temporary, string id) {
		if((Mac ? RenameMac(dir, temporary, dir, id, 4) : RenameLinux(dir, temporary, dir, id, 1)) != 0)
			throw new IOException("Cannot atomically name the artifact snapshot.");
	}

	public static void SyncNamespace(SafeFileHandle dir) {
		if(Sync(dir) != 0) throw new IOException("Cannot sync the artifact directory namespace.");
	}

	public static bool Delete(SafeFileHandle dir, string name) => UnlinkAt(dir, name, 0) == 0;

	static void ValidateLeaf(string name) {
		if(name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
			throw new IOException("Expected a single artifact filename.");
	}

	static SafeFileHandle OpenChild(SafeFileHandle parent, string name, int flags, int mode) {
		ValidateLeaf(name);
		if(Mac) return Checked(MacOpenAt(parent, name, flags, mode));
		// Linux openat2 (x64/arm64, Linux 5.6+) rejects bind mounts as well as ordinary mounts.
		if(RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
			throw new IOException("Artifact storage is unsupported on this Linux architecture.");
		var how = new OpenHow { Flags = (ulong) flags, Mode = (ulong) mode, Resolve = 0x0d }; // BENEATH | NO_SYMLINKS | NO_XDEV
		return Checked((int) LinuxOpenAt2(437, parent, name, ref how, 24));
	}

	static SafeFileHandle Checked(int fd) => fd < 0 ? throw new IOException("Cannot safely open the artifact file or directory.") : new((nint) fd, ownsHandle: true);

	readonly record struct FileState(int Mode, uint Uid, uint Links, ulong Device, ulong Inode, long Size);
	static FileState Inspect(SafeFileHandle fd) {
		if(Mac) {
			if((RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacStatInode64(fd, out var st) : MacStat(fd, out st)) != 0) throw new IOException("Cannot inspect artifact descriptor.");
			return new(st.Mode, st.Uid, st.Links, st.Device, st.Inode, st.Size);
		}
		const uint basic = 0x307; // TYPE | MODE | NLINK | INO | SIZE
		if(LinuxStat(fd, "", 0x1000, basic | 8, out var linux) != 0 || (linux.Mask & (basic | 8)) != (basic | 8))
			throw new IOException("Cannot inspect artifact descriptor.");
		return new(linux.Mode, linux.Uid, linux.Links, ((ulong) linux.DevMajor << 32) | linux.DevMinor, linux.Inode, linux.Size);
	}

	[StructLayout(LayoutKind.Sequential)]
	struct OpenHow { public ulong Flags, Mode, Resolve; }
	// Darwin 64-bit struct stat (INO64) and Linux's architecture-independent struct statx.
	[StructLayout(LayoutKind.Explicit, Size = 144)]
	struct DarwinStat {
		[FieldOffset(0)] public uint Device;
		[FieldOffset(4)] public ushort Mode;
		[FieldOffset(6)] public ushort Links;
		[FieldOffset(8)] public ulong Inode;
		[FieldOffset(16)] public uint Uid;
		[FieldOffset(96)] public long Size;
	}
	[StructLayout(LayoutKind.Explicit, Size = 256)]
	struct Statx {
		[FieldOffset(0)] public uint Mask;
		[FieldOffset(16)] public uint Links;
		[FieldOffset(20)] public uint Uid;
		[FieldOffset(28)] public ushort Mode;
		[FieldOffset(32)] public ulong Inode;
		[FieldOffset(40)] public long Size;
		[FieldOffset(136)] public uint DevMajor;
		[FieldOffset(140)] public uint DevMinor;
	}

	[LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
	private static partial int Open(string path, int flags, int mode);
	// Darwin's fixed-argument syscall entry, not the variadic libc wrapper (arm64 ABI differs).
	[LibraryImport("libc", EntryPoint = "__openat", StringMarshalling = StringMarshalling.Utf8)]
	private static partial int MacOpenAt(SafeFileHandle dir, string path, int flags, int mode);
	[LibraryImport("libc", EntryPoint = "syscall", StringMarshalling = StringMarshalling.Utf8)]
	private static partial long LinuxOpenAt2(long code, SafeFileHandle dir, string path, ref OpenHow how, nuint size);
	[LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
	private static partial int LinuxStat(SafeFileHandle dir, string path, int flags, uint mask, out Statx stat);
	[LibraryImport("libc", EntryPoint = "fstat")]
	private static partial int MacStat(SafeFileHandle fd, out DarwinStat stat);
	[LibraryImport("libc", EntryPoint = "fstat$INODE64")]
	private static partial int MacStatInode64(SafeFileHandle fd, out DarwinStat stat);
	[LibraryImport("libc", EntryPoint = "geteuid")]
	private static partial uint EffectiveUid();
	[LibraryImport("libc", EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
	private static partial int MkdirAt(SafeFileHandle dir, string path, int mode);
	[LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8)]
	private static partial int RenameLinux(SafeFileHandle src, string source, SafeFileHandle dst, string destination, uint flags);
	[LibraryImport("libc", EntryPoint = "renameatx_np", StringMarshalling = StringMarshalling.Utf8)]
	private static partial int RenameMac(SafeFileHandle src, string source, SafeFileHandle dst, string destination, uint flags);
	[LibraryImport("libc", EntryPoint = "flock")]
	private static partial int Flock(SafeFileHandle fd, int flags);
	[LibraryImport("libc", EntryPoint = "dup")]
	private static partial int Duplicate(SafeFileHandle fd);
	[LibraryImport("libc", EntryPoint = "fdopendir")]
	private static partial nint FdOpenDir(int fd);
	[LibraryImport("libc", EntryPoint = "readdir_r")]
	private static partial int ReadDir(nint dir, nint buf, out nint result);
	[LibraryImport("libc", EntryPoint = "closedir")]
	private static partial int CloseDir(nint dir);
	[LibraryImport("libc", EntryPoint = "close")]
	private static partial int Close(int fd);
	[LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8)]
	private static partial int UnlinkAt(SafeFileHandle dir, string path, int flags);
	[LibraryImport("libc", EntryPoint = "fsync")]
	private static partial int Sync(SafeFileHandle dir);
}
