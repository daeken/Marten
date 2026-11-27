using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Marten.Web;

// Deliberately no managed pathname fallback. Darwin arm64 local APFS only for this pilot.
internal static class ProjectFileSystem {
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
		public bool File => (Mode & 0xf000) == 0x8000 && Links == 1 && Size >= 0;
		public string Identity => FormattableString.Invariant($"{Device:x}:{Inode:x}:{BirthSec:x}:{BirthNsec:x}:{Generation:x}");
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
	[DllImport(Lib, EntryPoint = "fstatfs")]
	static extern int FStatFs(SafeFileHandle handle, [Out] byte[] buffer);
	[DllImport(Lib, EntryPoint = "proc_pidfdinfo")]
	static extern int PidFdInfo(int pid, SafeFileHandle handle, int flavor, [Out] byte[] buffer, int size);
	[DllImport(Lib, EntryPoint = "realpath")]
	static extern IntPtr RealPath(string path, [Out] byte[] buffer);
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
	static string PathOf(SafeFileHandle fd) {
		var buf = new byte[1200];
		return PidFdInfo(Environment.ProcessId, fd, 2, buf, buf.Length) == buf.Length ? Decode(buf, 176) : throw new IOException();
	}
	public static string? Physical(string path) {
		var buf = new byte[1024];
		return RealPath(path, buf) != IntPtr.Zero ? Decode(buf) : null;
	}

	public static SafeFileHandle Root(string canonical, string? identity = null) {
		if(!Supported || !Path.IsPathFullyQualified(canonical) || canonical.Length < 2 || canonical.EndsWith('/') ||
			Physical(canonical) != canonical) throw new IOException();
		var fd = Handle(Open(canonical, DirectoryFlag | CloseOnExec | NoLinks | NonBlock));
		try {
			var stat = Stat(fd);
			var fs = new byte[2168];
			if(!stat.Directory || (identity != null && stat.Identity != identity) || PathOf(fd) != canonical ||
				FStatFs(fd, fs) != 0 || (BitConverter.ToUInt32(fs, 64) & 0x1000) == 0 || Decode(fs, 72) != "apfs" ||
				Decode(fs, 88) == canonical) throw new IOException();
			return fd;
		} catch { fd.Dispose(); throw; }
	}

	public static bool Leaf(string s) => s.Length is > 0 and <= 255 && s[0] != '.' &&
		s.All(c => c != '/' && c != '\\' && !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format);

	// Only internal, enumerated components. No caller-provided paths. Full relative lookup from
	// the pinned root even after inspecting parents; check both stat and kernel vnode path.
	public static SafeFileHandle Descendant(SafeFileHandle root, string canonical, string relative, int device, bool readFile = false) {
		if(relative.Length == 0) return Handle(OpenAt(root, ".", DirectoryFlag | CloseOnExec | Beneath | NoLinks));
		var parts = relative.Split('/');
		if(parts.Length > 32 || Utf8.GetByteCount(relative) > 768 || !parts.All(Leaf)) throw new IOException();
		var prefix = "";
		for(var i = 0; i < parts.Length - 1; i++) {
			prefix = prefix.Length == 0 ? parts[i] : prefix + "/" + parts[i];
			using var parent = Handle(OpenAt(root, prefix, DirectoryFlag | CloseOnExec | Beneath | NoLinks | NonBlock));
			if(Stat(parent).Device != device || PathOf(parent) != canonical + "/" + prefix) throw new IOException();
		}
		var flags = CloseOnExec | Beneath | NoLinks | NonBlock;
		var probe = Handle(OpenAt(root, relative, flags | EventOnly));
		using(probe) {
			var before = Stat(probe);
			if(before.Device != device || (!before.Directory && !before.File) || PathOf(probe) != canonical + "/" + relative ||
				(readFile && !before.File)) throw new IOException();
			var fd = Handle(OpenAt(root, relative, flags | (before.Directory ? DirectoryFlag : readFile ? 0 : EventOnly)));
			try {
				if(!Stat(fd).SameRevision(before) || PathOf(fd) != canonical + "/" + relative) throw new IOException();
				return fd;
			} catch { fd.Dispose(); throw; }
		}
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
