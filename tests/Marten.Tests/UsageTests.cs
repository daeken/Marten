using System.Text.Json;
using Marten.Models;
using Marten.Storage;

namespace Marten.Tests;

public class UsageTests {
	static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

	[Fact]
	public void BothUsageShapesNormalizeAndPrice() {
		var messages = CallUsage.From(J(new { input_tokens = 1_115_000, uncached_input_tokens = 10_000, cache_creation_input_tokens = 5_000, cache_read_input_tokens = 1_100_000, output_tokens = 2_000 }))!;
		Assert.Equal(new CallUsage(10_000, 1_100_000, 5_000, 2_000), messages);
		// Opus 5.5: 0.01M x $4 + 1.1M x $0.20 + 0.005M x $5 + 0.002M x $20
		Assert.Equal(0.04m + 0.22m + 0.025m + 0.04m, messages.Cost(Price.Defaults["claude-opus-5-5"]));

		var responses = CallUsage.From(J(new { input_tokens = 1000, input_tokens_details = new { cached_tokens = 800 }, output_tokens = 50 }))!;
		Assert.Equal(new CallUsage(200, 800, 0, 50), responses);
		Assert.Null(CallUsage.From(null));
	}

	[Fact]
	public async Task ACapHoldsItsBackendButNotOthers() {
		using var dir = new TempDir();
		var config = new Config { Budgets = new() { ["fake"] = 0.01m } };
		var ledger = new UsageLedger(new Home(dir.File("home")), () => config);
		var priced = new FakeBackend();
		priced.Script.Enqueue(_ => FakeBackend.Say("one") with { Usage = J(new { uncached_input_tokens = 0, output_tokens = 1_000 }) });
		var router = new BackendRouter(priced, [], ledger);
		config = config with { Prices = new() { ["m"] = new Price(1m, 20m) } };

		await router.CompleteAsync(new ModelRequest("m", null, "x", [], [], "c"), _ => { }, default);
		Assert.Equal(0.02m, ledger.SpentToday("fake"));
		var e = await Assert.ThrowsAsync<RateLimitedException>(() => router.CompleteAsync(new ModelRequest("m", null, "x", [], [], "c"), _ => { }, default));
		Assert.Contains("daily budget", e.Message);
		Assert.True(e.RetryAfter <= TimeSpan.FromMinutes(15));
		Assert.Single(priced.Requests);

		// It survives a reload, and an uncapped provider is untouched.
		var reloaded = new UsageLedger(new Home(dir.File("home")), () => config);
		Assert.Equal(0.02m, reloaded.SpentToday());
		reloaded.CheckBudget("other", "other");
	}
}
