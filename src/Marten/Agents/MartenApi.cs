using System.Collections;
using System.Collections.ObjectModel;
using System.Management.Automation;
using Marten.Tools;

namespace Marten.Agents;

/// <summary>The $Marten object in every runspace: the runtime, as seen by one agent.</summary>
public sealed class MartenApi(Agent agent, AgentManager manager, CancellationToken runCancellation = default) {
	bool insideSlot;
	public string[] Slots => manager.Resources.Names;
	public string AgentId => agent.Info.Id;
	public string AgentName => agent.Info.Name;
	public string AgentKind => agent.Info.Kind.ToString();
	public string Home => manager.Home.Root;
	public string AgentDirectory => agent.Directory;
	public string[] Tools => manager.Tools.For(agent).Select(t => t.Name).ToArray();
	public string? ProjectId => agent.Info.ProjectId;
	public string? ProjectMemoryPath => manager.ProjectOf(agent)?.MemoryPath;
	public object[] Tasks() => agent.Tasks.Select(t => new { t.Id, t.Title, State = t.State.ToString(), t.Reason }).ToArray<object>();

	public object[] Agents() => manager.Agents.Select(a => new {
		a.Info.Id, Kind = a.Info.Kind.ToString(), a.Info.Name, Status = a.Status.ToString(), a.Info.ProjectId, a.Info.ParentId, a.Info.Closed,
		a.StatusDetail, a.ModelCall,
	}).ToArray<object>();

	/// <summary>Your last few PowerShell runs: command, directory, $State in and out, output. Re-run one by pasting its command with its state.</summary>
	public object[] Notebook(int last = 10) => agent.Notebook().TakeLast(Math.Clamp(last, 1, 200)).Select(c => new {
		c.Id, c.At, c.Command, c.WorkingDirectory, StateIn = c.StateIn?.ToJsonString(), StateOut = c.StateOut?.ToJsonString(), c.Output, c.IsError, c.DurationMs,
	}).ToArray<object>();

	/// <summary>Show an image file to the model with this command's result. View-Image wraps it.</summary>
	public string ViewImage(string path) => agent.ViewImage(path).Path;

	/// <summary>Run a foreground script block under a configured named command limit.</summary>
	public Collection<PSObject> WithSlot(string name, ScriptBlock body) {
		if(!runCancellation.CanBeCanceled) throw new InvalidOperationException("WithSlot requires a running PowerShell command.");
		if(insideSlot) throw new InvalidOperationException("Command slots cannot be nested.");
		var pool = manager.Resources.Named(name);
		insideSlot = true;
		var waiting = false;
		try {
			using var lease = pool.Take(() => {
				waiting = true;
				agent.SetCommandSlotWait(name);
			}, runCancellation).GetAwaiter().GetResult();
			if(waiting) {
				waiting = false;
				agent.SetCommandSlotWait(null);
			}
			runCancellation.ThrowIfCancellationRequested();
			return body.Invoke();
		} finally {
			insideSlot = false;
			if(waiting) agent.SetCommandSlotWait(null);
		}
	}

	/// <summary>Call any Marten tool by name. Arguments are plain data; failures throw so PowerShell reports them.</summary>
	public string CallTool(string name, IDictionary? arguments) {
		var tool = manager.Tools.Find(agent, name) ?? throw new ArgumentException($"Unknown tool: {name}");
		if(tool.PowerShellBody != null) throw new InvalidOperationException($"{name} runs inline in PowerShell; call it as a function: {name} -param value.");
		if(!tool.CallableFromPowerShell) throw new InvalidOperationException($"{name} can't be called from PowerShell.");
		var args = StateJson.FromPowerShell(arguments ?? new Hashtable(), "arguments")?.ToJsonString() ?? "{}";
		var result = manager.Tools.InvokeAsync(agent, name, args, runCancellation).GetAwaiter().GetResult();
		if(result.IsError) throw new InvalidOperationException(result.Text);
		return result.Text;
	}
}
