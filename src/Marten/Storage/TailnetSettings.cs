using System.Text.RegularExpressions;

namespace Marten.Storage;

public sealed record TailnetSettings {
	public string? Host { get; init; }

	public string? CanonicalHost() => Host is { Length: <= 134 } host && Regex.IsMatch(host,
		@"\A[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.ts\.net\z",
		RegexOptions.CultureInvariant) ? host : null;
}
