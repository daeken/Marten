using System.Runtime.CompilerServices;
using Marten.Storage;

namespace Marten.Web;

internal static class TranscriptStreaming {
	internal static async Task WriteFull(HttpContext ctx, Transcript log, bool outside, CancellationToken stopping) {
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping, Security.DeviceRevocationOf(ctx));
		// Keep the legacy JSON array, without buffering the entire archive.
		await ctx.Response.WriteAsJsonAsync(Stream(log, outside), cancellation.Token);
	}

	static async IAsyncEnumerable<Entry> Stream(Transcript log, bool outside, [EnumeratorCancellation] CancellationToken ct = default) {
		await Task.CompletedTask;
		using var entries = log.Capture().Read().GetEnumerator();
		while(true) {
			ct.ThrowIfCancellationRequested();
			if(!entries.MoveNext()) yield break;
			yield return outside ? ArtifactTranscriptProjection.Outside(entries.Current, log) : entries.Current;
		}
	}
}
