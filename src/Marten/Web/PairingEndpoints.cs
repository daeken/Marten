using System.Text.Json;
using Marten.Storage;
using Microsoft.AspNetCore.Http.Features;

namespace Marten.Web;

public static class PairingEndpoints {
	internal sealed class Management;
	internal sealed class Redemption;
	internal sealed class PairedAction;

	public static T AllowPaired<T>(this T endpoint) where T : IEndpointConventionBuilder => endpoint.WithMetadata(new PairedAction());

	/// <summary>Opt-in surface for native-only HTTPS transports. Authentication still runs in Security.</summary>
	public static bool IsPairedUiEndpoint(HttpContext ctx) => ctx.GetEndpoint()?.Metadata is { } metadata &&
		(metadata.GetMetadata<PairedAction>() != null || metadata.GetMetadata<Management>() != null || metadata.GetMetadata<Redemption>() != null);

	public static void MapPairing(this RouteGroupBuilder api, DevicePairing pairing, Uri? origin) {
		var pair = api.MapGroup("/pair");
		pair.MapGet("/devices", () => new { enabled = origin != null, devices = pairing.List() }).WithMetadata(new Management());
		pair.MapPost("/create", () => {
			if(origin == null) return Results.Problem("No trusted HTTPS pairing origin is configured.", statusCode: 409);
			if(pairing.Create() is not { } invitation)
				return Results.Problem("Pairing limit reached. Wait for invitations to expire or revoke an old device.", statusCode: 429);
			var uri = $"marten://pair?v=1&server={Uri.EscapeDataString(origin.GetLeftPart(UriPartial.Authority))}&code={invitation.Code}";
			return Results.Ok(new { uri, invitation.ExpiresAt });
		}).WithMetadata(new Management());
		pair.MapDelete("/devices/{id:guid}", (Guid id) => pairing.Revoke(id) ? Results.NoContent() : Results.NotFound()).WithMetadata(new Management());
		pair.MapPost("/redeem", (Delegate)((HttpContext ctx) => Redeem(ctx, pairing))).WithMetadata(new Redemption());
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
