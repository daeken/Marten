using System.Text.Json;
using Marten.Storage;

namespace Marten.Models;

/// <summary>Dollars per million tokens. CacheWrite defaults to 1.25x input when unset.</summary>
public sealed record Price(decimal Input, decimal Output, decimal? CacheRead = null, decimal? CacheWrite = null) {
	/// <summary>Built-in prices for current Claude models; config entries override these by model id.</summary>
	public static readonly IReadOnlyDictionary<string, Price> Defaults = new Dictionary<string, Price> {
		["claude-fable-5-2"] = new(8m, 40m, 0.20m, 10m),
		["claude-fable-5-1"] = new(10m, 50m, 0.25m, 12.5m),
		["claude-opus-5-5"] = new(4m, 20m, 0.20m, 5m),
		["claude-opus-5"] = new(5m, 25m, 0.50m, 6.25m),
		["claude-sonnet-5-5"] = new(2m, 10m, 0.10m, 2.5m),
		["claude-sonnet-5"] = new(2m, 10m, 0.20m, 2.5m),
		["claude-haiku-5-5"] = new(0.10m, 0.50m, 0.01m, 0.125m),
	};
}

/// <summary>One model call's tokens, normalized across providers.</summary>
public sealed record CallUsage(long Input, long CacheRead, long CacheWrite, long Output) {
	/// <summary>
	/// Reads either our Messages usage (uncached_input_tokens plus cache fields) or the Responses
	/// shape (input_tokens with input_tokens_details.cached_tokens).
	/// </summary>
	public static CallUsage? From(JsonElement? usage) {
		if(usage is not { ValueKind: JsonValueKind.Object } u) return null;
		long Get(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
		var output = Get(u, "output_tokens");
		if(u.TryGetProperty("uncached_input_tokens", out _))
			return new(Get(u, "uncached_input_tokens"), Get(u, "cache_read_input_tokens"), Get(u, "cache_creation_input_tokens"), output);
		var cached = u.TryGetProperty("input_tokens_details", out var d) ? Get(d, "cached_tokens") : 0;
		return new(Math.Max(0, Get(u, "input_tokens") - cached), cached, 0, output);
	}

	public decimal Cost(Price p) =>
		(Input * p.Input + CacheRead * (p.CacheRead ?? p.Input * 0.1m) + CacheWrite * (p.CacheWrite ?? p.Input * 1.25m) + Output * p.Output) / 1_000_000m;
}

/// <summary>Totals for one provider and model on one day.</summary>
public sealed record UsageTotals {
	public long Calls { get; init; }
	public long Input { get; init; }
	public long CacheRead { get; init; }
	public long CacheWrite { get; init; }
	public long Output { get; init; }
	/// <summary>Null when the model has no known price.</summary>
	public decimal? Usd { get; init; }

	public UsageTotals Add(CallUsage u, decimal? usd) => this with {
		Calls = Calls + 1, Input = Input + u.Input, CacheRead = CacheRead + u.CacheRead,
		CacheWrite = CacheWrite + u.CacheWrite, Output = Output + u.Output,
		Usd = usd is { } c ? (Usd ?? 0) + c : Usd,
	};
}

/// <summary>
/// Daily token and dollar totals in ~/.marten/usage.json, keyed by local date, then provider, then model.
/// Budgets are checked against today's dollars before each call.
/// </summary>
public sealed class UsageLedger(Home home, Func<Config> config) {
	readonly object gate = new();
	Dictionary<string, Dictionary<string, Dictionary<string, UsageTotals>>>? days;
	string Path => System.IO.Path.Combine(home.Root, "usage.json");
	public static string Today => DateTime.Now.ToString("yyyy-MM-dd");

	Dictionary<string, Dictionary<string, Dictionary<string, UsageTotals>>> Days() =>
		days ??= JsonIO.Load(Path, () => new Dictionary<string, Dictionary<string, Dictionary<string, UsageTotals>>>());

	public Price? PriceFor(string model) =>
		config().Prices.TryGetValue(model, out var p) ? p : Price.Defaults.GetValueOrDefault(model);

	public void Record(string provider, string model, JsonElement? usage) {
		if(CallUsage.From(usage) is not { } u) return;
		var usd = PriceFor(model) is { } price ? u.Cost(price) : (decimal?) null;
		lock(gate) {
			var day = Days().GetValueOrDefault(Today) ?? (Days()[Today] = new());
			var byModel = day.GetValueOrDefault(provider) ?? (day[provider] = new());
			byModel[model] = (byModel.GetValueOrDefault(model) ?? new UsageTotals()).Add(u, usd);
			// Keep 90 days.
			foreach(var old in Days().Keys.Order().SkipLast(90).ToList()) Days().Remove(old);
			JsonIO.Save(Path, Days());
		}
	}

	public decimal SpentToday(string? provider = null) {
		lock(gate) {
			if(Days().GetValueOrDefault(Today) is not { } day) return 0;
			return day.Where(p => provider == null || p.Key == provider).SelectMany(p => p.Value.Values).Sum(t => t.Usd ?? 0);
		}
	}

	/// <summary>The most recent days, newest first.</summary>
	public object Recent(int count) {
		lock(gate) return Days().OrderByDescending(d => d.Key).Take(count).ToDictionary(d => d.Key, d => d.Value);
	}

	/// <summary>Throws a rate-limit wait until local midnight when today's spend has reached the provider's or the overall cap.</summary>
	public void CheckBudget(string provider, string label) {
		var budgets = config().Budgets;
		foreach(var (key, name) in new[] { (provider, label), ("total", "overall") }) {
			if(!budgets.TryGetValue(key, out var cap)) continue;
			var spent = SpentToday(key == "total" ? null : key);
			if(spent >= cap)
				throw new RateLimitedException($"The {name} daily budget of ${cap:0.##} is spent (${spent:0.##} today). It resets at midnight; raise it in config to resume sooner.", UntilMidnight());
		}
	}

	/// <summary>Until midnight, but at most 15 minutes, so a raised cap takes effect soon.</summary>
	static TimeSpan UntilMidnight() {
		var left = DateTime.Today.AddDays(1) - DateTime.Now + TimeSpan.FromSeconds(5);
		return left < TimeSpan.FromMinutes(15) ? left : TimeSpan.FromMinutes(15);
	}
}
