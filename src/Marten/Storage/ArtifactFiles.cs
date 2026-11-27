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

	public static SafeFileHandle DirectoryAt(string agentDir, string name, bool create) {
		if(!Supported) throw new IOException("Artifact storage is unsupported on this host.");
		using var parent = Checked(Open(agentDir, ReadFlags | DirectoryFlag, 0));
		if(create && MkdirAt(parent, name, PrivateDirectory) < 0 && Marshal.GetLastPInvokeError() != 17)
			throw new IOException("Cannot create the private artifact directory.");
		var dir = OpenChild(parent, name, ReadFlags | DirectoryFlag, 0);
		try {
			var stat = Inspect(dir);
			if((stat.Mode & TypeMask) != DirectoryType || (stat.Mode & 0x1ff) != PrivateDirectory || stat.Uid != EffectiveUid() || stat.Device != Inspect(parent).Device)
				throw new IOException("Unsafe artifact directory.");
			return dir;
		} catch { dir.Dispose(); throw; }
	}

	public static FileStream Read(SafeFileHandle dir, string name, out long size) {
		var file = OpenChild(dir, name, ReadFlags, 0);
		try {
			var stat = Inspect(file);
			if((stat.Mode & TypeMask) != RegularType || stat.Links != 1 || stat.Device != Inspect(dir).Device)
				throw new IOException("Artifact must be a regular, singly linked file on the staging filesystem.");
			size = stat.Size;
			return new FileStream(file, FileAccess.Read, 64 * 1024, isAsync: false);
		} catch { file.Dispose(); throw; }
	}

	public static FileStream Create(SafeFileHandle dir, string name) {
		var handle = OpenChild(dir, name, NewFlags, PrivateFile);
		try { return new FileStream(handle, FileAccess.Write, 64 * 1024, isAsync: false); }
		catch { handle.Dispose(); throw; }
	}

	public static void Finalize(SafeFileHandle dir, string temporary, string id) {
		// No overwrite even on an ID collision. The temp name is removed before any reader can see a ref.
		if(LinkAt(dir, temporary, dir, id, 0) != 0) throw new IOException("Cannot finalize the artifact snapshot.");
		if(!Delete(dir, temporary)) {
			Delete(dir, id);
			throw new IOException("Cannot finalize the artifact snapshot.");
		}
		if(Sync(dir) != 0) {
			Delete(dir, id);
			throw new IOException("Cannot commit the artifact snapshot.");
		}
	}

	public static bool Delete(SafeFileHandle dir, string name) => UnlinkAt(dir, name, 0) == 0;

	static SafeFileHandle OpenChild(SafeFileHandle parent, string name, int flags, int mode) {
		if(name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
			throw new IOException("Expected a single artifact filename.");
		if(Mac) return Checked(MacOpenAt(parent, name, flags, mode));
		// Linux openat2 (x64/arm64, Linux 5.6+) rejects bind mounts as well as ordinary mounts.
		if(RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
			throw new IOException("Artifact storage is unsupported on this Linux architecture.");
		var how = new OpenHow { Flags = (ulong) flags, Mode = (ulong) mode, Resolve = 0x0d }; // BENEATH | NO_SYMLINKS | NO_XDEV
		return Checked((int) LinuxOpenAt2(437, parent, name, ref how, 24));
	}

	static SafeFileHandle Checked(int fd) => fd < 0 ? throw new IOException("Cannot safely open the artifact file or directory.") : new((nint) fd, ownsHandle: true);

	readonly record struct FileState(int Mode, uint Uid, uint Links, ulong Device, long Size);
	static FileState Inspect(SafeFileHandle fd) {
		if(Mac) {
			if((RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacStatInode64(fd, out var st) : MacStat(fd, out st)) != 0) throw new IOException("Cannot inspect artifact descriptor.");
			return new(st.Mode, st.Uid, st.Links, st.Device, st.Size);
		}
		const uint basic = 0x207; // TYPE | MODE | NLINK | SIZE
		if(LinuxStat(fd, "", 0x1000, basic | 8, out var linux) != 0 || (linux.Mask & (basic | 8)) != (basic | 8))
			throw new IOException("Cannot inspect artifact descriptor.");
		return new(linux.Mode, linux.Uid, linux.Links, ((ulong) linux.DevMajor << 32) | linux.DevMinor, linux.Size);
	}

	[StructLayout(LayoutKind.Sequential)]
	struct OpenHow { public ulong Flags, Mode, Resolve; }
	// Darwin 64-bit struct stat (INO64) and Linux's architecture-independent struct statx.
	[StructLayout(LayoutKind.Explicit, Size = 144)]
	struct DarwinStat {
		[FieldOffset(0)] public uint Device;
		[FieldOffset(4)] public ushort Mode;
		[FieldOffset(6)] public ushort Links;
		[FieldOffset(16)] public uint Uid;
		[FieldOffset(96)] public long Size;
	}
	[StructLayout(LayoutKind.Explicit, Size = 256)]
	struct Statx {
		[FieldOffset(0)] public uint Mask;
		[FieldOffset(16)] public uint Links;
		[FieldOffset(20)] public uint Uid;
		[FieldOffset(28)] public ushort Mode;
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
	[LibraryImport("libc", EntryPoint = "linkat", StringMarshalling = StringMarshalling.Utf8)]
	private static partial int LinkAt(SafeFileHandle src, string source, SafeFileHandle dst, string destination, int flags);
	[LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8)]
	private static partial int UnlinkAt(SafeFileHandle dir, string path, int flags);
	[LibraryImport("libc", EntryPoint = "fsync")]
	private static partial int Sync(SafeFileHandle dir);
}
