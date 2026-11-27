using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Marten.Web;

// Darwin arm64 only. No fallback to pathname reads.
internal static class MacFileSystem {
	const int DirectoryFlag = 0x100000, CloseOnExec = 0x1000000, NoLinks = 0x20000000,
		Beneath = 0x1000, NonBlock = 4, EventOnly = 0x8000;
	const string Lib = "/usr/lib/libSystem.B.dylib";
	static readonly UTF8Encoding Utf8 = new(false, true);
	public static bool Supported => OperatingSystem.IsMacOSVersionAtLeast(26) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 && EffectiveUserId() != 0;

	// Darwin arm64 layouts from sys/stat.h, sys/mount.h and dirent.h.
	[StructLayout(LayoutKind.Sequential)]
	internal readonly record struct Stamp(int Device, ushort Mode, ushort Links, ulong Inode, uint Uid, uint Gid, int Rdev,
		long AccessSec, long AccessNsec, long ModifySec, long ModifyNsec, long ChangeSec, long ChangeNsec,
		long BirthSec, long BirthNsec, long Size, long Blocks, int BlockSize, uint Flags, uint Generation, int Spare, long Q1, long Q2) {
		public bool Directory => (Mode & 0xf000) == 0x4000;
		public bool File => (Mode & 0xf000) == 0x8000 && Size >= 0;
		public (int, ulong, long, long, uint) Identity => (Device, Inode, BirthSec, BirthNsec, Generation);
		public bool SameRevision(Stamp other) => Identity == other.Identity && Mode == other.Mode && Links == other.Links &&
			Size == other.Size && ModifySec == other.ModifySec && ModifyNsec == other.ModifyNsec &&
			ChangeSec == other.ChangeSec && ChangeNsec == other.ChangeNsec;
	}

	[DllImport(Lib, EntryPoint = "geteuid")]
	static extern uint EffectiveUserId();
	[DllImport(Lib, EntryPoint = "open")]
	static extern int Open(string path, int flags);
	[DllImport(Lib, EntryPoint = "openat")]
	static extern int OpenAt(SafeFileHandle handle, string path, int flags);
	[DllImport(Lib, EntryPoint = "fstat")]
	static extern int FStat(SafeFileHandle handle, out Stamp stamp);
	[DllImport(Lib, EntryPoint = "fstatat")]
	static extern int FStatAt(SafeFileHandle handle, string path, out Stamp stamp, int flags);
	[DllImport(Lib, EntryPoint = "fstatfs")]
	static extern int FStatFs(SafeFileHandle handle, [Out] byte[] buffer);
	[DllImport(Lib, EntryPoint = "fdopendir")]
	static extern IntPtr FdOpenDir(int handle);
	[DllImport(Lib, EntryPoint = "readdir", SetLastError = true)]
	static extern IntPtr ReadDir(IntPtr directory);
	[DllImport(Lib, EntryPoint = "closedir")]
	static extern int CloseDir(IntPtr directory);

	static string Decode(byte[] buffer, int offset = 0) {
		var end = Array.IndexOf(buffer, (byte) 0, offset);
		return end >= offset ? Utf8.GetString(buffer, offset, end - offset) : throw new IOException();
	}
	static SafeFileHandle Handle(int fd) => fd < 0 ? throw new IOException() : new SafeFileHandle((IntPtr) fd, true);
	public static Stamp Stat(SafeFileHandle fd) => FStat(fd, out var stamp) == 0 ? stamp : throw new IOException();
	internal readonly record struct Mount(long FilesystemId, string Type, string MountedOn, string MountedFrom);
	internal readonly record struct Version(Stamp Stat, Mount Mount) {
		public bool SameObject(Version other) => Mount == other.Mount && Stat.Identity == other.Stat.Identity;
		public bool SameRevision(Version other) => Mount == other.Mount && Stat.SameRevision(other.Stat);
	}
	public static Version Inspect(SafeFileHandle fd) {
		var fs = new byte[2168];
		if(FStatFs(fd, fs) != 0) throw new IOException();
		var type = Decode(fs, 72);
		if(type is "devfs" or "fdesc" or "procfs" or "autofs") throw new IOException();
		return new Version(Stat(fd), new Mount(BitConverter.ToInt64(fs, 48), type, Decode(fs, 88), Decode(fs, 1112)));
	}

	public static SafeFileHandle Root(string path) => Supported
		? Handle(Open(path, DirectoryFlag | CloseOnExec | NoLinks | NonBlock)) : throw new IOException();

	public static bool Leaf(string s) => s.Length is > 0 and <= 255 && s is not "." and not ".." &&
		s.All(c => c != '/' && !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format);

	// Only internally enumerated names. Mount crossings are intentional. Probe while holding the
	// descriptor, then open with no-follow, nonblocking and type flags; reject any replacement.
	public static SafeFileHandle Descendant(SafeFileHandle root, string relative, bool readFile = false) {
		var flags = CloseOnExec | Beneath | NoLinks | NonBlock;
		if(relative.Length == 0 && !readFile) return Handle(OpenAt(root, ".", flags | DirectoryFlag));
		if(Utf8.GetByteCount(relative) > 1000 || !relative.Split('/').All(Leaf)) throw new IOException();
		// Stat without opening first: O_EVTONLY alone is not a safe type classifier.
		if(FStatAt(root, relative, out var listed, 0x20 | 0x800 | 0x2000) != 0 ||
			(!listed.Directory && !listed.File) || (readFile && !listed.File)) throw new IOException();
		using var probe = Handle(OpenAt(root, relative, flags | EventOnly));
		var before = Inspect(probe);
		if(!before.Stat.SameRevision(listed)) throw new IOException();
		var fd = Handle(OpenAt(root, relative, flags | (before.Stat.Directory ? DirectoryFlag : readFile ? 0 : EventOnly)));
		try {
			if(!Inspect(fd).SameRevision(before)) throw new IOException();
			return fd;
		} catch { fd.Dispose(); throw; }
	}

	public static IEnumerable<string?> Names(SafeFileHandle directory) {
		using var fd = Handle(OpenAt(directory, ".", DirectoryFlag | CloseOnExec | Beneath | NoLinks));
		var dir = FdOpenDir((int) fd.DangerousGetHandle());
		if(dir == IntPtr.Zero) throw new IOException();
		fd.SetHandleAsInvalid(); // fdopendir owns it now
		try {
			while(true) {
				var entry = ReadDir(dir);
				if(entry == IntPtr.Zero) {
					if(Marshal.GetLastPInvokeError() != 0) throw new IOException();
					yield break;
				}
				var len = Marshal.ReadInt16(entry, 18);
				if(len is < 1 or > 255) { yield return null; continue; }
				var bytes = new byte[len];
				Marshal.Copy(entry + 21, bytes, 0, len);
				string? name;
				try { name = Utf8.GetString(bytes); } catch(DecoderFallbackException) { name = null; }
				yield return name;
			}
		} finally { CloseDir(dir); }
	}
}
