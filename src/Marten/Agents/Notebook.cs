using System.Text.Json.Nodes;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Agents;

public sealed partial class Agent {
	const int CellOutputMax = 20_000;
	int? cellCount;
	string NotebookPath => Path.Combine(Directory, "notebook.jsonl");

	public void RecordCell(NotebookCell cell) {
		NotebookCell saved;
		lock(gate) {
			cellCount = (cellCount ?? (File.Exists(NotebookPath) ? File.ReadLines(NotebookPath).Count(l => l.Length > 0) : 0)) + 1;
			saved = cell with {
				Id = cellCount.Value,
				Output = cell.Output.Length > CellOutputMax ? cell.Output[..CellOutputMax] + $"\n… ({cell.Output.Length - CellOutputMax} more chars)" : cell.Output,
			};
			File.AppendAllText(NotebookPath, JsonIO.Serialize(saved) + "\n");
		}
		manager.Publish(new { type = "notebook", agentId = Info.Id, cell = saved.Id });
	}

	public List<NotebookCell> Notebook() {
		lock(gate) {
			if(!File.Exists(NotebookPath)) return [];
			return File.ReadLines(NotebookPath).Where(l => l.Length > 0)
				.Select(l => { try { return JsonIO.Deserialize<NotebookCell>(l); } catch { return null; } })
				.OfType<NotebookCell>().ToList();
		}
	}

	/// <summary>Run a recorded cell again: same command, directory and $State going in, in a fresh runspace. The agent's own $State is untouched.</summary>
	public async Task<(NotebookCell Run, bool SameOutput, bool SameState)> RerunCell(int id, CancellationToken ct) {
		var cell = Notebook().FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException($"No cell {id}.");
		if(manager.Tools.Get("powershell") is not PowerShellTool ps) throw new InvalidOperationException("PowerShell isn't available.");
		var replay = new ReplayOwner(this, cell);
		await ps.RunAsync(replay, cell.Command, CommandTimeout, ct);
		var run = replay.Recorded ?? throw new InvalidOperationException("The run wasn't recorded.");
		return (run, run.Output == cell.Output, JsonNode.DeepEquals(run.StateOut, cell.StateOut));
	}

	sealed class ReplayOwner(Agent agent, NotebookCell cell) : IPowerShellOwner {
		public string AgentId => agent.AgentId;
		public string WorkingDirectory => System.IO.Directory.Exists(cell.WorkingDirectory) ? cell.WorkingDirectory : agent.WorkingDirectory;
		public FileTracker Files => agent.Files;
		public ToolRegistry Tools => agent.Tools;
		public JsonNode? PowerShellState { get; set; } = cell.StateIn?.DeepClone();
		public object MartenObject => agent.MartenObject;
		public void RunspaceAbandoned(string command) => agent.RunspaceAbandoned(command);
		public NotebookCell? Recorded { get; private set; }
		public void RecordCell(NotebookCell run) => Recorded = run;
	}

	/// <summary>The notebook as Jupyter (.NET Interactive, PowerShell kernel). Each cell restores its own location and $State, so cells run alone.</summary>
	public string NotebookIpynb() {
		static JsonArray Lines(string text) {
			var lines = text.Split('\n');
			return new JsonArray(lines.Select((l, i) => (JsonNode) (i < lines.Length - 1 ? l + "\n" : l)).ToArray());
		}
		var cells = new JsonArray {
			new JsonObject {
				["cell_type"] = "markdown", ["metadata"] = new JsonObject(),
				["source"] = Lines($"# {Info.Name} · PowerShell notebook\n\nExported from Marten. Each cell sets the working directory and `$State` as they were before that run, " +
					"so cells can run alone. `$Marten` and Marten's tool functions only exist inside Marten."),
			},
		};
		foreach(var c in Notebook()) {
			var state = c.StateIn == null ? "$State = $null" : $"$State = ConvertFrom-Json -AsHashtable @'\n{c.StateIn.ToJsonString()}\n'@";
			var source = $"# Marten cell #{c.Id}, {c.At:yyyy-MM-dd HH:mm:ss}Z, {c.DurationMs} ms{(c.IsError ? ", failed" : "")}\n" +
				$"Set-Location '{c.WorkingDirectory.Replace("'", "''")}'\n{state}\n\n{c.Command}";
			cells.Add(new JsonObject {
				["cell_type"] = "code", ["execution_count"] = c.Id, ["metadata"] = new JsonObject { ["dotnet_interactive"] = new JsonObject { ["language"] = "pwsh" } },
				["source"] = Lines(source),
				["outputs"] = new JsonArray(new JsonObject { ["output_type"] = "stream", ["name"] = c.IsError ? "stderr" : "stdout", ["text"] = Lines(c.Output) }),
			});
		}
		var nb = new JsonObject {
			["nbformat"] = 4, ["nbformat_minor"] = 5,
			["metadata"] = new JsonObject {
				["kernelspec"] = new JsonObject { ["display_name"] = ".NET (PowerShell)", ["language"] = "PowerShell", ["name"] = ".net-pwsh" },
				["language_info"] = new JsonObject { ["name"] = "PowerShell" },
			},
			["cells"] = cells,
		};
		return nb.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
	}
}
