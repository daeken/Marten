using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Agents;

/// <summary>Work that never finishes on its own, like a dev server, started detached by an agent.</summary>
public sealed record BackgroundProcess {
	public required int Id { get; init; }
	public required string Name { get; init; }
	public required string Command { get; init; }
	public required string WorkingDirectory { get; init; }
	/// <summary>The process (and process group) that runs the command.</summary>
	public required int Pid { get; init; }
	public required DateTimeOffset Started { get; init; }
	public required string LogPath { get; init; }
	public DateTimeOffset? Stopped { get; init; }
}

public static partial class Posix {
	[LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
	private static partial int Kill(int pid, int sig);

	const int SIGKILL = 9, SIGTERM = 15, EPERM = 1;

	public static bool Alive(int pid) => pid > 0 && (Kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() == EPERM);
	public static void Terminate(int pid, bool group) => Kill(group ? -pid : pid, SIGTERM);
	public static void ForceKill(int pid, bool group) => Kill(group ? -pid : pid, SIGKILL);
}

/// <summary>
/// Starts commands detached from Marten: in their own process group, reparented away from Marten, with
/// output going to a log file rather than a pipe. A restart of Marten leaves them running.
/// </summary>
public sealed class ProcessLauncher(ChildProcessWatcher? watcher = null) {
	static readonly string Shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";

	public int Start(string command, string workingDirectory, string logPath) {
		Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
		File.AppendAllText(logPath, $"[marten] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} started in {workingDirectory}: {command}\n");
		var psi = new ProcessStartInfo(Shell) {
			WorkingDirectory = workingDirectory, UseShellExecute = false,
			RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
		};
		// set -m puts the job in its own process group, so stopping it reaches everything it spawned.
		psi.ArgumentList.Add("-c");
		psi.ArgumentList.Add("set -m; ( eval \"$MARTEN_CMD\" ) >> \"$MARTEN_LOG\" 2>&1 < /dev/null & echo $!");
		psi.Environment["MARTEN_CMD"] = command;
		psi.Environment["MARTEN_LOG"] = logPath;
		using var launcher = Process.Start(psi) ?? throw new ToolException("Couldn't start the shell.");
		watcher?.Exclude(launcher.Id);
		launcher.StandardInput.Close();
		var output = launcher.StandardOutput.ReadToEnd();
		launcher.WaitForExit();
		if(!int.TryParse(output.Trim(), out var pid)) throw new ToolException($"The shell didn't report a process id: {output}{launcher.StandardError.ReadToEnd()}");
		return pid;
	}

	/// <summary>SIGTERM the group, wait out the grace period, then SIGKILL whatever is left.</summary>
	public static async Task StopAsync(int pid, TimeSpan grace) {
		var tree = ProcessTable.Tree(pid, ProcessTable.ParentMap());
		Posix.Terminate(pid, group: true);
		foreach(var p in tree) Posix.Terminate(p, group: false);
		var deadline = DateTime.UtcNow + grace;
		while(DateTime.UtcNow < deadline && tree.Any(Posix.Alive)) await Task.Delay(100);
		Posix.ForceKill(pid, group: true);
		foreach(var p in tree.Where(Posix.Alive)) Posix.ForceKill(p, group: false);
	}
}

public sealed partial class Agent {
	List<BackgroundProcess>? processes;
	string ProcessesPath => Path.Combine(Directory, "processes.json");

	List<BackgroundProcess> LoadedProcesses() => processes ??= JsonIO.Load(ProcessesPath, () => new List<BackgroundProcess>());

	public IReadOnlyList<BackgroundProcess> Processes { get { lock(gate) return LoadedProcesses().ToList(); } }

	public BackgroundProcess StartProcess(string command, string? name, string? workingDirectory) {
		var cwd = workingDirectory is { Length: > 0 } w ? Path.GetFullPath(w, WorkingDirectory) : WorkingDirectory;
		if(!System.IO.Directory.Exists(cwd)) throw new ToolException($"{cwd} doesn't exist.");
		int id;
		lock(gate) id = LoadedProcesses().Count == 0 ? 1 : processes!.Max(p => p.Id) + 1;
		var log = Path.Combine(Directory, "processes", $"{id}.log");
		var pid = manager.Launcher.Start(command, cwd, log);
		var proc = new BackgroundProcess {
			Id = id, Name = name is { Length: > 0 } n ? n : command.Split(' ', 2)[0], Command = command, WorkingDirectory = cwd,
			Pid = pid, Started = DateTimeOffset.UtcNow, LogPath = log,
		};
		lock(gate) {
			LoadedProcesses().Add(proc);
			JsonIO.Save(ProcessesPath, processes!);
		}
		manager.Publish(new { type = "processes", agentId = Info.Id });
		return proc;
	}

	public async Task<BackgroundProcess?> StopProcess(int id) {
		var proc = Processes.FirstOrDefault(p => p.Id == id);
		if(proc == null) return null;
		if(proc.Stopped == null) await ProcessLauncher.StopAsync(proc.Pid, TimeSpan.FromSeconds(manager.Config.StopGraceSeconds));
		BackgroundProcess stopped;
		lock(gate) {
			var list = LoadedProcesses();
			var i = list.FindIndex(p => p.Id == id);
			stopped = list[i] = list[i] with { Stopped = list[i].Stopped ?? DateTimeOffset.UtcNow };
			JsonIO.Save(ProcessesPath, list);
		}
		manager.Publish(new { type = "processes", agentId = Info.Id });
		return stopped;
	}

	/// <summary>Closing an agent stops everything it started.</summary>
	public Task StopAllProcesses() => Task.WhenAll(Processes.Where(p => p.Stopped == null).Select(p => StopProcess(p.Id)));
}

static class ProcessView {
	public static string Describe(BackgroundProcess p) {
		var state = p.Stopped != null ? "stopped" : Posix.Alive(p.Pid) ? "running" : "exited";
		return $"#{p.Id} {p.Name} — {state}, pid {p.Pid}, started {p.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n    {p.Command}\n    log: {p.LogPath}";
	}
}

public sealed class ProcessStartTool : ITool {
	public string Name => "process_start";
	public string Description =>
		"Start work that never finishes on its own, like a dev server or a watcher, detached from you. Output goes to a log " +
		"file. It keeps running across your turns and across Marten restarts, until you stop it or you are closed. Work that " +
		"finishes, like a test run, belongs in powershell with a longer timeout instead.";
	public JsonObject Parameters => Schema.Object(
		("command", Schema.Str("Shell command (run by bash), e.g. \"npm run dev\".")),
		("name", Schema.Str("Short name, e.g. \"vite\". Null to use the command's first word.", nullable: true)),
		("working_directory", Schema.Str("Where to run it; null for your working directory.", nullable: true)));

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var p = AgentTools.Self(ctx).StartProcess(args.ReqString("command"), args.OptString("name"), args.OptString("working_directory"));
		return Task.FromResult(new ToolOutput($"Started #{p.Id} {p.Name} (pid {p.Pid}). Log: {p.LogPath}"));
	}
}

public sealed class ProcessListTool : ITool {
	public string Name => "process_list";
	public string Description => "List the background processes you started, whether each is still running, and their log files.";
	public JsonObject Parameters => Schema.Object();

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var list = AgentTools.Self(ctx).Processes.Where(p => p.Stopped == null).ToList();
		return Task.FromResult(new ToolOutput(list.Count == 0 ? "No background processes." : string.Join("\n", list.Select(ProcessView.Describe))));
	}
}

public sealed class ProcessStopTool : ITool {
	public string Name => "process_stop";
	public string Description => "Stop one of your background processes and everything it spawned (SIGTERM, then SIGKILL after a grace period).";
	public JsonObject Parameters => Schema.Object(("id", Schema.Int("Process number from process_list.")));

	public async Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var id = args.OptInt("id") ?? throw new ToolException("Missing required argument: id");
		var p = await AgentTools.Self(ctx).StopProcess(id) ?? throw new ToolException($"You have no background process {id}.");
		return new ToolOutput($"Stopped #{p.Id} {p.Name}.");
	}
}
