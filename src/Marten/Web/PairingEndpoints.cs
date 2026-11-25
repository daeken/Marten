using System.Text.Json;
using Marten.Storage;
using Microsoft.AspNetCore.Http.Features;

namespace Marten.Web;

public static class PairingEndpoints {
	internal sealed class Management;
	internal sealed class Redemption;

	public static T AllowPaired<T>(this T endpoint) where T : IEndpointConventionBuilder => endpoint.WithMetadata(new TailnetApi());

	public static void MapPairing(this RouteGroupBuilder api, DevicePairing pairing, bool configured, string? host, Func<bool> ready) {
		var pair = api.MapGroup("/pair");
		pair.MapGet("/devices", () => new { configured, valid = host != null, ready = ready(), enrollmentAvailable = false, devices = pairing.List() })
			.WithMetadata(new Management());
		// Never issue the obsolete Marten-only deep-link QR. The dual-secret issuer is not wired yet.
		pair.MapPost("/create", () => Results.Problem("One-scan enrollment is not available.", statusCode: 409)).WithMetadata(new Management());
		pair.MapDelete("/devices/{id:guid}", (Guid id) => pairing.Revoke(id) ? Results.NoContent() : Results.NotFound()).WithMetadata(new Management());
		pair.MapPost("/redeem", (Delegate)((HttpContext ctx) => Redeem(ctx, pairing))).WithMetadata(new Redemption(), new TailnetApi());
	}

	static async Task<IResult> Redeem(HttpContext ctx, DevicePairing pairing) {
		using var attempt = pairing.BeginAttempt();
		if(attempt == null) {
			ctx.Response.Headers.RetryAfter = "60";
			return Results.StatusCode(429);
		}
		if(!ctx.Request.HasJsonContentType()) return Results.StatusCode(415);
		const int maxBodyBytes = 4096;
		if(ctx.Request.ContentLength > maxBodyBytes) return Results.StatusCode(413);
		if(ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = maxBodyBytes;
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
		timeout.CancelAfter(TimeSpan.FromSeconds(5));
		RedeemRequest? request;
		try {
			request = await JsonSerializer.DeserializeAsync<RedeemRequest>(ctx.Request.Body, JsonIO.Options, timeout.Token);
		} catch(JsonException) {
			return Results.BadRequest();
		} catch(BadHttpRequestException ex) {
			return Results.StatusCode(ex.StatusCode);
		} catch(OperationCanceledException) {
			return Results.StatusCode(408);
		}
		return pairing.Redeem(request?.Code, request?.DeviceName) is { } token ? Results.Ok(new { token }) : Results.BadRequest();
	}

	sealed record RedeemRequest(string? Code, string? DeviceName);
}
