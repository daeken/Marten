using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.Json.Nodes;

namespace Marten.Tools;

/// <summary>What an agent exposes to PowerShell: its state variable, its $Marten object, and a flag for abandoned runspaces.</summary>
public interface IPowerShellOwner : IToolContext {
	JsonNode? PowerShellState { get; set; }
	object MartenObject { get; }
	void RunspaceAbandoned(string command);
}

/// <summary>
/// Runs PowerShell in-process. Each command gets a fresh runspace; only $State carries over, as JSON.
/// One command at a time per agent. On timeout the pipeline is stopped and the child processes it
/// started are killed; if the stop doesn't return, the runspace is abandoned and flagged.
/// </summary>
public sealed class PowerShellTool(Func<int> defaultTimeoutSeconds, Func<int> stopGraceSeconds, ChildProcessWatcher children) : ITool {
	public const string StateVariable = "State";
	const int RenderWidth = 160;

	public string Name => "powershell";
	public string Description =>
		"Run a PowerShell 7 command in-process, in a fresh runspace that starts in your working directory. " +
		"No variables survive between runs except $State, which is saved as JSON after each run and must hold only " +
		"JSON-serializable values (hashtables, arrays, strings, numbers, booleans, null). $Marten exposes the runtime. " +
		"Every Marten tool is also a PowerShell function of the same name (e.g. read_file -path x). " +
		"Strings come back as written, other objects as formatted text, with errors and warnings inline. " +
		"timeout_seconds overrides the default timeout for long work such as test runs.";
	public JsonObject Parameters => Schema.Object(
		("command", Schema.Str("PowerShell script to run.")),
		("timeout_seconds", Schema.Int("Timeout for this run, or null for the default.", nullable: true)));
	public bool CallableFromPowerShell => false;

	readonly Dictionary<string, SemaphoreSlim> agentGates = new();

	SemaphoreSlim GateFor(string agentId) {
		lock(agentGates) {
			if(!agentGates.TryGetValue(agentId, out var gate)) agentGates[agentId] = gate = new SemaphoreSlim(1, 1);
			return gate;
		}
	}

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		if(ctx is not IPowerShellOwner owner) throw new ToolException("This agent can't run PowerShell.");
		var timeout = args.OptInt("timeout_seconds") is int t and > 0 ? t : defaultTimeoutSeconds();
		return RunAsync(owner, args.ReqString("command"), TimeSpan.FromSeconds(timeout), ct);
	}

	public async Task<ToolOutput> RunAsync(IPowerShellOwner owner, string command, TimeSpan timeout, CancellationToken ct) {
		var gate = GateFor(owner.AgentId);
		await gate.WaitAsync(ct);
		try {
			return await RunLockedAsync(owner, command, timeout, ct);
		} finally {
			gate.Release();
		}
	}

	async Task<ToolOutput> RunLockedAsync(IPowerShellOwner owner, string command, TimeSpan timeout, CancellationToken ct) {
		var runspace = RunspaceFactory.CreateRunspace(BuildSessionState(owner));
		runspace.Open();
		var abandoned = false;
		try {
			runspace.SessionStateProxy.Path.SetLocation(WildcardPattern.Escape(owner.WorkingDirectory));
			runspace.SessionStateProxy.SetVariable(StateVariable, StateJson.ToPowerShell(owner.PowerShellState));

			using var ps = PowerShell.Create(runspace);
			ps.AddScript(command);
			ps.Commands.Commands[0].MergeMyResults(PipelineResultTypes.All, PipelineResultTypes.Output);
			var output = new PSDataCollection<PSObject>();
			using var lease = children.Begin();
			var invoke = ps.InvokeAsync(new PSDataCollection<PSObject>(), output);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
			var finished = await Task.WhenAny(invoke, Task.Delay(timeout, linked.Token));
			string? interruption = null;
			if(finished != invoke) {
				interruption = ct.IsCancellationRequested ? "Cancelled" : $"Timed out after {timeout.TotalSeconds:0}s";
				var killed = KillOwnedChildren(lease);
				var stop = ps.StopAsync(null, null);
				if(await Task.WhenAny(Task.WhenAll(invoke.ContinueWith(_ => { }), stop.ContinueWith(_ => { })),
					   Task.Delay(TimeSpan.FromSeconds(stopGraceSeconds()))) is var done && !invoke.IsCompleted) {
					abandoned = true;
					owner.RunspaceAbandoned(command);
					interruption += "; the pipeline did not stop, so its runspace was abandoned";
				}
				if(killed.Killed.Count > 0) interruption += $"; killed child processes {string.Join(", ", killed.Killed)}";
				if(killed.Ambiguous.Count > 0)
					interruption += $"; processes {string.Join(", ", killed.Ambiguous)} started while other commands ran too, so they were left running";
			} else {
				linked.Cancel();
			}

			var text = new StringBuilder();
			Exception? failure = null;
			if(!abandoned) {
				try { await invoke; } catch(Exception e) when(e is RuntimeException or PipelineStoppedException or InvalidOperationException) { failure = e; }
				Render(runspace, output, text);
			}
			if(failure is RuntimeException re) text.Append(FormatError(re.ErrorRecord)).Append('\n');
			else if(failure != null && interruption == null) text.Append("error: ").Append(failure.Message).Append('\n');

			if(!abandoned && interruption == null) {
				if(runspace.SessionStateProxy.GetVariable("LASTEXITCODE") is int code && code != 0 && ps.HadErrors)
					text.Append($"[exit code {code}]\n");
				try {
					owner.PowerShellState = StateJson.FromPowerShell(runspace.SessionStateProxy.GetVariable(StateVariable));
				} catch(StateJson.NotSerializableException e) {
					text.Append($"error: ${StateVariable} was not saved: {e.Message}\n");
					return ToolOutput.Error(text.ToString());
				}
			}
			if(interruption != null) {
				text.Append($"[{interruption}]\n");
				return ToolOutput.Error(text.ToString());
			}
			return new ToolOutput(text.Length == 0 ? "[no output]" : text.ToString());
		} finally {
			if(!abandoned) runspace.Dispose();
		}
	}

	(List<int> Killed, List<int> Ambiguous) KillOwnedChildren(ChildProcessWatcher.Lease lease) {
		children.Refresh();
		var map = ProcessTable.ParentMap();
		var killed = new List<int>();
		foreach(var pid in lease.Owned.Where(map.ContainsKey)) {
			ProcessTable.KillTree(pid);
			killed.Add(pid);
		}
		return (killed, lease.Ambiguous.Where(map.ContainsKey).ToList());
	}

	InitialSessionState BuildSessionState(IPowerShellOwner owner) {
		var iss = InitialSessionState.CreateDefault2();
		if(OperatingSystem.IsWindows()) iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
		iss.Variables.Add(new SessionStateVariableEntry("Marten", owner.MartenObject, "The Marten runtime."));
		foreach(var tool in owner.Tools.For(owner).Where(t => t.CallableFromPowerShell))
			iss.Commands.Add(new SessionStateFunctionEntry(tool.Name, ToolFunction(tool)));
		return iss;
	}

	/// <summary>A PowerShell function whose parameters mirror the tool's top-level schema properties.</summary>
	static string ToolFunction(ITool tool) {
		if(tool.PowerShellBody is { } body) return body;
		var props = (tool.Parameters["properties"] as JsonObject)?.Select(p => p.Key).ToList() ?? [];
		var param = string.Join(", ", props.Select(p => $"${p}"));
		return $"param({param})\n$Marten.CallTool('{tool.Name}', $PSBoundParameters)";
	}

	void Render(Runspace runspace, IEnumerable<PSObject> output, StringBuilder text) {
		var pending = new List<PSObject>();
		void Flush() {
			if(pending.Count == 0) return;
			using var ps = PowerShell.Create(runspace);
			ps.AddCommand("Out-String").AddParameter("Width", RenderWidth);
			try {
				foreach(var s in ps.Invoke(pending)) text.Append(s.BaseObject.ToString()!.Trim('\r', '\n')).Append('\n');
			} catch(Exception e) {
				foreach(var p in pending) text.Append(p).Append('\n');
				text.Append($"[formatting failed: {e.Message}]\n");
			}
			pending.Clear();
		}
		foreach(var item in output) {
			if(item == null) continue;
			switch(item.BaseObject) {
				case string s:
					Flush();
					text.Append(s).Append('\n');
					break;
				case ErrorRecord er:
					Flush();
					text.Append(FormatError(er)).Append('\n');
					break;
				case WarningRecord w:
					Flush();
					text.Append("WARNING: ").Append(w.Message).Append('\n');
					break;
				case InformationRecord info:
					Flush();
					text.Append(info.MessageData).Append('\n');
					break;
				case VerboseRecord or DebugRecord or ProgressRecord:
					break;
				default:
					pending.Add(item);
					break;
			}
		}
		Flush();
	}

	static string FormatError(ErrorRecord er) {
		// Native stderr lines arrive as error records; show them as plain text.
		if(er.FullyQualifiedErrorId == "NativeCommandError" || er.FullyQualifiedErrorId == "NativeCommandErrorMessage")
			return er.Exception.Message;
		var position = er.InvocationInfo?.ScriptLineNumber is > 0 and var line ? $" (line {line})" : "";
		return $"error{position}: {er}";
	}
}

/// <summary>Converts $State between PowerShell objects and JSON, refusing anything that isn't plain data.</summary>
public static class StateJson {
	public sealed class NotSerializableException(string message) : Exception(message);

	public static JsonNode? FromPowerShell(object? value, string path = "$State", int depth = 0) {
		if(depth > 64) throw new NotSerializableException($"{path} is nested too deeply.");
		if(value is PSObject pso) {
			if(pso.BaseObject is PSCustomObject) {
				var obj = new JsonObject();
				foreach(var prop in pso.Properties.Where(p => p.MemberType == PSMemberTypes.NoteProperty))
					obj[prop.Name] = FromPowerShell(prop.Value, $"{path}.{prop.Name}", depth + 1);
				return obj;
			}
			value = pso.BaseObject;
		}
		switch(value) {
			case null: return null;
			case string s: return JsonValue.Create(s);
			case bool b: return JsonValue.Create(b);
			case int or long or short or byte or sbyte or ushort or uint or ulong:
				return JsonValue.Create(Convert.ToInt64(value));
			case double d when double.IsFinite(d): return JsonValue.Create(d);
			case float f when float.IsFinite(f): return JsonValue.Create((double) f);
			case decimal m: return JsonValue.Create(m);
			case IDictionary dict:
				var o = new JsonObject();
				foreach(DictionaryEntry kv in dict) {
					if(kv.Key is not string key) throw new NotSerializableException($"{path} has a non-string key ({kv.Key?.GetType().Name}).");
					o[key] = FromPowerShell(kv.Value, $"{path}.{key}", depth + 1);
				}
				return o;
			case IList list:
				var a = new JsonArray();
				var i = 0;
				foreach(var item in list) a.Add(FromPowerShell(item, $"{path}[{i++}]", depth + 1));
				return a;
			default:
				throw new NotSerializableException($"{path} holds a {value.GetType().FullName}, which isn't JSON data.");
		}
	}

	public static object? ToPowerShell(JsonNode? node) => node switch {
		null => null,
		JsonObject obj => obj.Aggregate(new Hashtable(StringComparer.OrdinalIgnoreCase), (h, kv) => { h[kv.Key] = ToPowerShell(kv.Value); return h; }),
		JsonArray arr => new ArrayList(arr.Select(ToPowerShell).ToList()),
		JsonValue v when v.TryGetValue<bool>(out var b) => b,
		JsonValue v when v.TryGetValue<long>(out var l) => l,
		JsonValue v when v.TryGetValue<double>(out var d) => d,
		JsonValue v when v.TryGetValue<string>(out var s) => s,
		JsonValue v => v.ToString(),
		_ => null,
	};
}
