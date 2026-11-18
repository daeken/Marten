using System.Collections;
using Marten.Tools;

namespace Marten.Agents;

/// <summary>The $Marten object in every runspace: the runtime, as seen by one agent.</summary>
public sealed class MartenApi(Agent agent, AgentManager manager) {
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
	}).ToArray<object>();

	/// <summary>Show an image file to the model with this command's result. View-Image wraps it.</summary>
	public string ViewImage(string path) => agent.ViewImage(path).Path;

	/// <summary>Call any Marten tool by name. Arguments are plain data; failures throw so PowerShell reports them.</summary>
	public string CallTool(string name, IDictionary? arguments) {
		var tool = manager.Tools.Find(agent, name) ?? throw new ArgumentException($"Unknown tool: {name}");
		if(tool.PowerShellBody != null) throw new InvalidOperationException($"{name} runs inline in PowerShell; call it as a function: {name} -param value.");
		if(!tool.CallableFromPowerShell) throw new InvalidOperationException($"{name} can't be called from PowerShell.");
		var args = StateJson.FromPowerShell(arguments ?? new Hashtable(), "arguments")?.ToJsonString() ?? "{}";
		var result = manager.Tools.InvokeAsync(agent, name, args, CancellationToken.None).GetAwaiter().GetResult();
		if(result.IsError) throw new InvalidOperationException(result.Text);
		return result.Text;
	}
}
