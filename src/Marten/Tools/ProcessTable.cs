using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Marten.Tools;

/// <summary>Reads (pid, ppid) for every process, without spawning anything where the OS allows it.</summary>
public static partial class ProcessTable {
	public static readonly int Self = Environment.ProcessId;

	public static Dictionary<int, int> ParentMap() {
		try {
			if(OperatingSystem.IsLinux()) return Linux();
			if(OperatingSystem.IsMacOS()) return Mac();
		} catch { }
		return ViaPs();
	}

	public static HashSet<int> ChildrenOf(int pid, Dictionary<int, int>? map = null) =>
		(map ?? ParentMap()).Where(kv => kv.Value == pid).Select(kv => kv.Key).ToHashSet();

	/// <summary>The process and all its descendants, deepest first, so children die before parents can respawn them.</summary>
	public static List<int> Tree(int root, Dictionary<int, int> map) {
		var result = new List<int>();
		void Walk(int pid) {
			foreach(var child in map.Where(kv => kv.Value == pid).Select(kv => kv.Key)) Walk(child);
			result.Add(pid);
		}
		Walk(root);
		return result;
	}

	/// <summary>This process and every ancestor, which no cleanup should ever kill.</summary>
	public static HashSet<int> SelfAndAncestors(Dictionary<int, int> map) {
		var result = new HashSet<int>();
		for(var pid = Self; pid > 1 && result.Add(pid) && map.TryGetValue(pid, out var parent);) pid = parent;
		return result;
	}

	public static void KillTree(int root) {
		var map = ParentMap();
		var keep = SelfAndAncestors(map);
		foreach(var pid in Tree(root, map).Where(p => !keep.Contains(p)))
			try { Process.GetProcessById(pid).Kill(); } catch { }
	}

	static Dictionary<int, int> Linux() {
		var map = new Dictionary<int, int>();
		foreach(var dir in Directory.EnumerateDirectories("/proc")) {
			if(!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
			try {
				// /proc/<pid>/stat: "pid (comm) state ppid ..."; comm can hold spaces and parens.
				var stat = File.ReadAllText(Path.Combine(dir, "stat"));
				var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
				map[pid] = int.Parse(fields[1]);
			} catch { }
		}
		return map;
	}

	[LibraryImport("libproc", EntryPoint = "proc_listallpids")]
	private static partial int ProcListAllPids([Out] int[]? buffer, int bufferSize);

	[LibraryImport("libproc", EntryPoint = "proc_pidinfo")]
	private static partial int ProcPidInfo(int pid, int flavor, ulong arg, [Out] byte[] buffer, int bufferSize);

	static Dictionary<int, int> Mac() {
		const int PROC_PIDTBSDINFO = 3, BsdInfoSize = 136, PpidOffset = 16;
		var count = ProcListAllPids(null, 0);
		var pids = new int[count + 64];
		count = ProcListAllPids(pids, pids.Length * sizeof(int));
		var map = new Dictionary<int, int>();
		var info = new byte[BsdInfoSize];
		foreach(var pid in pids.Take(count)) {
			if(pid <= 0) continue;
			if(ProcPidInfo(pid, PROC_PIDTBSDINFO, 0, info, info.Length) == BsdInfoSize)
				map[pid] = BitConverter.ToInt32(info, PpidOffset);
		}
		return map;
	}

	static Dictionary<int, int> ViaPs() {
		var psi = new ProcessStartInfo("ps", "-A -o pid= -o ppid=") { RedirectStandardOutput = true, UseShellExecute = false };
		using var ps = Process.Start(psi)!;
		var map = new Dictionary<int, int>();
		foreach(var line in ps.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if(parts.Length == 2 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var ppid) && pid != ps.Id)
				map[pid] = ppid;
		}
		ps.WaitForExit();
		return map;
	}
}

/// <summary>
/// Attributes Marten's direct child processes to the PowerShell commands that started them.
/// The OS can't say which thread forked a child (on macOS), so while commands run we poll for new
/// children and give each to the commands running when it appeared. A child seen while only one
/// command ran is that command's; one seen during overlap is ambiguous and is reported, not killed.
/// Children Marten starts itself (git, MCP servers, background launchers) go through StartExcluded
/// and are never attributed. All state is process-wide: every watcher sees every child of this process.
/// </summary>
public sealed class ChildProcessWatcher {
	public sealed class Lease : IDisposable {
		public readonly HashSet<int> Owned = new();
		public readonly HashSet<int> Ambiguous = new();
		public void Dispose() => Release(this);
	}

	static readonly object gate = new();
	static readonly List<Lease> active = new();
	static HashSet<int> known = ProcessTable.ChildrenOf(ProcessTable.Self);
	static readonly HashSet<int> excluded = new();
	static readonly HashSet<int> foreground = new();
	static Timer? timer;

	/// <summary>
	/// Start a process Marten owns, excluded before any poll can see it: the start and the exclusion
	/// happen under the lock IsExcluded takes, so a poll that sees the new pid waits for its exclusion.
	/// </summary>
	public static Process? StartExcluded(ProcessStartInfo psi) {
		lock(excluded) {
			var p = Process.Start(psi);
			if(p != null) excluded.Add(p.Id);
			return p;
		}
	}

	public static bool IsExcluded(int pid) { lock(excluded) return excluded.Contains(pid); }

	public Lease Begin() {
		lock(gate) {
			Poll();
			var lease = new Lease();
			active.Add(lease);
			timer ??= new Timer(_ => { lock(gate) Poll(); }, null, 200, 200);
			return lease;
		}
	}

	static void Release(Lease lease) {
		lock(gate) {
			Poll();
			active.Remove(lease);
			if(active.Count == 0) {
				timer?.Dispose();
				timer = null;
			}
		}
	}

	/// <summary>Take a fresh snapshot while holding the attribution lock.</summary>
	public (int[] Owned, int[] Ambiguous) Snapshot(Lease lease) {
		lock(gate) {
			Poll();
			return (lease.Owned.ToArray(), lease.Ambiguous.ToArray());
		}
	}

	// At shutdown all runspaces are stopping, so shared/ambiguous foreground children can be
	// killed too. Explicitly excluded runtime children and detached process_start jobs are not swept.
	public void CleanForeground() {
		int[] roots;
		lock(gate) {
			Poll();
			roots = foreground.ToArray();
		}
		foreach(var pid in roots) {
			var map = ProcessTable.ParentMap();
			if(map.GetValueOrDefault(pid) == ProcessTable.Self && !IsExcluded(pid))
				ProcessTable.KillTree(pid);
		}
	}

	static void Poll() {
		var now = ProcessTable.ChildrenOf(ProcessTable.Self);
		foreground.IntersectWith(now);
		foreach(var pid in now) {
			if(active.Count == 0 || known.Contains(pid) || IsExcluded(pid)) continue;
			foreground.Add(pid);
			if(active.Count == 1) active[0].Owned.Add(pid);
			else foreach(var lease in active) lease.Ambiguous.Add(pid);
		}
		known = now;
	}
}
