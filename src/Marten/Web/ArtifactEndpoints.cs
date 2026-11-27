using System.Buffers;
using Marten.Agents;
using Marten.Storage;
using Microsoft.Net.Http.Headers;

namespace Marten.Web;

internal static class ArtifactEndpoints {
	internal static void MapArtifacts(this RouteGroupBuilder api, AgentManager manager) {
		api.MapGet("/agents/{id}/artifacts/{artifactId}", (HttpContext c, string id, string artifactId) => Serve(c, manager, id, artifactId, false)).AllowPaired();
		api.MapGet("/agents/{id}/artifacts/{artifactId}/preview", (HttpContext c, string id, string artifactId) => Serve(c, manager, id, artifactId, true)).AllowPaired();
	}

	static async Task Serve(HttpContext ctx, AgentManager manager, string id, string artifactId, bool preview) {
		var request = ctx.Request;
		var response = ctx.Response;
		response.Headers.CacheControl = "no-store";
		response.Headers.XContentTypeOptions = "nosniff";
		if(request.QueryString.HasValue || !request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || ctx.Items.ContainsKey("client")) {
			response.StatusCode = StatusCodes.Status403Forbidden;
			return;
		}
		if(!Artifacts.IdValid(artifactId) || manager.Get(id) is not { } agent || agent.Transcript.Artifact(artifactId) is not { } item ||
			(preview && item.PreviewKind is not ("text" or "png")) || manager.ArtifactStore?.Open(id, item, agent.Transcript) is not { } file) {
			response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}
		await using var stream = file;
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, Security.DeviceRevocationOf(ctx));
		var ct = linked.Token;
		try {
			ct.ThrowIfCancellationRequested();
			var disposition = new ContentDispositionHeaderValue("attachment");
			disposition.SetHttpFileName(item.Name);
			response.Headers.ContentDisposition = disposition.ToString();
			if(preview) {
				var limit = item.PreviewKind == "png" ? Artifacts.MaxPng : Artifacts.MaxText;
				if(item.PreviewKind == "png" && item.Bytes > limit) { response.StatusCode = 404; return; }
				var bytes = new byte[(int) Math.Min(limit, item.Bytes)];
				await stream.ReadExactlyAsync(bytes, ct);
				var clipped = item.Bytes > bytes.Length;
				var count = item.PreviewKind == "text" ? Artifacts.TextPrefix(bytes, clipped) : Artifacts.SafePng(bytes) ? bytes.Length : -1;
				if(count < 0) { response.StatusCode = 404; return; }
				if(clipped) response.Headers["X-Marten-Preview-Truncated"] = "1";
				response.ContentType = item.PreviewKind == "text" ? "text/plain; charset=utf-8" : "image/png";
				response.ContentLength = count;
				await response.Body.WriteAsync(bytes.AsMemory(0, count), ct);
			} else {
				response.ContentType = "application/octet-stream";
				response.ContentLength = item.Bytes;
				var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
				try {
					long remaining = item.Bytes;
					while(remaining > 0) {
						int take = (int) Math.Min(remaining, 64 * 1024);
						await stream.ReadExactlyAsync(buffer.AsMemory(0, take), ct);
						await response.Body.WriteAsync(buffer.AsMemory(0, take), ct);
						remaining -= take;
					}
				} finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
			}
		} catch(OperationCanceledException) when(ct.IsCancellationRequested) { ctx.Abort(); }
		catch(IOException) { ctx.Abort(); }
	}
}
