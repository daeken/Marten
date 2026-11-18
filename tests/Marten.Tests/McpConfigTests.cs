using Marten.Mcp;

namespace Marten.Tests;

public class McpConfigTests {
	[Fact]
	public void PlaceholdersGiveEachAgentItsOwnState() {
		var config = new McpServerConfig {
			Command = "npx", Args = ["@playwright/mcp", "--user-data-dir", "{agentDir}/browser", "--output-dir", "~/out/{agent}"],
			Env = new() { ["STATE"] = "{martenHome}/x" }, Cwd = "{agentDir}",
		};
		var e = config.Expand("chat-1", "/m/agents/chat-1", "/m");
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		Assert.Equal(["@playwright/mcp", "--user-data-dir", "/m/agents/chat-1/browser", "--output-dir", Path.Combine(home, "out/chat-1")], e.Args);
		Assert.Equal("/m/x", e.Env["STATE"]);
		Assert.Equal("/m/agents/chat-1", e.Cwd);
		Assert.Equal("{agentDir}/browser", config.Args[2]);
	}
}
