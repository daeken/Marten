namespace Marten.Storage;

/// <summary>Shared local enrollment and wire-format check for an opaque Jev API key, not upstream validation.</summary>
internal static class JevKeyFormat {
	internal const int MaxLength = 512;

	internal static bool IsValid(string? key) {
		if(key == null || key.Length is < 1 or > MaxLength) return false;
		var value = key.AsSpan().TrimEnd('=');
		if(value.IsEmpty) return false;
		foreach(var c in value)
			if(!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '~' or '+' or '/' or '-')) return false;
		return true;
	}
}
