using System.Text.Json.Nodes;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Agents;

internal sealed class PublishArtifactTool(Func<AgentManager> manager) : ITool {
	public string Name => "publish_artifact";
	public string Description => "Publish an immutable file for the operator in this conversation. First copy only the intended deliverable (never secrets) into your artifact-inbox under Marten home/agents/<your agent ID>. Then call this tool with the filename, not a path.";
	public JsonObject Parameters => Schema.Object(
		("filename", Schema.Str("A single existing filename in your artifact-inbox, no directories.")),
		("label", Schema.Str("Optional display name; the source file extension is retained.", nullable: true)));
	public bool CallableFromPowerShell => false;
	public bool AvailableTo(IToolContext ctx) => ArtifactFiles.Supported && ctx is Agent;

	public async Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		if(ctx is not Agent agent) throw new ToolException("Publishing requires an agent conversation.");
		var filename = args.OptString("filename") ?? throw new ToolException("Specify a staging filename.");
		var label = args.OptString("label");
		if(label?.Length > 160) throw new ToolException("The optional label is limited to 160 characters.");
		try {
			var item = await manager().ArtifactStore.Publish(agent.Directory, filename, label, ct);
			return new ToolOutput("Published an artifact for the operator in this conversation.") { Artifacts = [item] };
		} catch(IOException e) { throw new ToolException(e.Message); }
		catch(Exception e) when(e is UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException) {
			throw new ToolException("Could not safely publish from the artifact-inbox on this host.");
		}
	}
}
