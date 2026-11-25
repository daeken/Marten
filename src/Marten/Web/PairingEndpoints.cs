using System.Globalization;
using System.Text.Json;
using Marten.Storage;
using Microsoft.AspNetCore.Http.Features;

namespace Marten.Web;

// One route policy consumed by Security for both credential and native-authority admission.
internal sealed class OperatorRoute {
	public static readonly OperatorRoute Action = new(), Management = new(), Redemption = new();
	private OperatorRoute() { }
}

public static class PairingEndpoints {
	public static T AllowPaired<T>(this T endpoint) where T : IEndpointConventionBuilder => endpoint.WithMetadata(OperatorRoute.Action);

	public static void MapPairing(this RouteGroupBuilder api, DevicePairing pairing, Security security) {
		var pair = api.MapGroup("/pair");
		pair.MapGet("/devices", () => new { enabled = security.PairingOrigin != null, requiresTailscaleEnrollment = security.EmbeddedTailnet, devices = pairing.List() }).WithMetadata(OperatorRoute.Management);
		pair.MapPost("/create", (Delegate)((HttpContext ctx) => Create(ctx, pairing, security))).WithMetadata(OperatorRoute.Management);
		pair.MapDelete("/devices/{id:guid}", (Guid id) => pairing.Revoke(id) ? Results.NoContent() : Results.NotFound()).WithMetadata(OperatorRoute.Management);
		pair.MapPost("/redeem", (Delegate)((HttpContext ctx) => Redeem(ctx, pairing))).WithMetadata(OperatorRoute.Redemption);
	}

	static async Task<IResult> Create(HttpContext ctx, DevicePairing pairing, Security security) {
		if(ctx.Request.QueryString.HasValue) return Results.StatusCode(403);
		if(security.PairingOrigin is not { } origin)
			return Results.Problem(security.EmbeddedTailnet ? "The embedded tailnet listener is not ready." : "No trusted HTTPS pairing origin is configured.", statusCode: 409);
		string? tsAuthKey = null;
		if(security.EmbeddedTailnet) {
			if(ctx.Request.ContentLength is 0 || !ctx.Request.HasJsonContentType()) return InvalidEnrollmentKey();
			var (request, error) = await ReadJson<CreateRequest>(ctx);
			if(error != null) return error;
			tsAuthKey = request?.TsAuthKey;
			const string prefix = "tskey-auth-";
			if(tsAuthKey is not { Length: >= 12 and <= 256 } || !tsAuthKey.StartsWith(prefix, StringComparison.Ordinal) ||
				tsAuthKey.AsSpan(prefix.Length).ContainsAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-"))
				return InvalidEnrollmentKey();
		} else if(ctx.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true) {
			return Results.Problem("Direct HTTPS pairing does not accept an enrollment key.", statusCode: 400);
		}
		if(security.PairingOrigin == null) return Results.StatusCode(409);
		if(pairing.Create() is not { } invitation)
			return Results.Problem("Pairing limit reached. Wait for invitations to expire or revoke an old device.", statusCode: 429);
		var expiresAt = invitation.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
		var fields = new Dictionary<string, object> { ["v"] = 2, ["server"] = origin.GetLeftPart(UriPartial.Authority), ["code"] = invitation.Code, ["expiresAt"] = expiresAt };
		if(tsAuthKey != null) fields.Add("tsAuthKey", tsAuthKey);
		return Results.Ok(new { payload = JsonSerializer.Serialize(fields, JsonIO.Options), expiresAt });
	}

	static IResult InvalidEnrollmentKey() => Results.Problem("Enter a one-time client Tailscale auth key (tskey-auth-…).", statusCode: 400);

	static async Task<IResult> Redeem(HttpContext ctx, DevicePairing pairing) {
		using var attempt = pairing.BeginAttempt();
		if(attempt == null) {
			ctx.Response.Headers.RetryAfter = "60";
			return Results.StatusCode(429);
		}
		var (request, error) = await ReadJson<RedeemRequest>(ctx);
		if(error != null) return error;
		return pairing.Redeem(request?.Code, request?.DeviceName) is { } token ? Results.Ok(new { token }) : Results.BadRequest();
	}

	static async Task<(T? Body, IResult? Error)> ReadJson<T>(HttpContext ctx) where T : class {
		if(!ctx.Request.HasJsonContentType()) return (null, Results.StatusCode(415));
		const int maxBodyBytes = 4096;
		if(ctx.Request.ContentLength > maxBodyBytes) return (null, Results.StatusCode(413));
		if(ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = maxBodyBytes;
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
		timeout.CancelAfter(TimeSpan.FromSeconds(5));
		try {
			return (await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, JsonIO.Options, timeout.Token), null);
		} catch(JsonException) {
			return (null, Results.BadRequest());
		} catch(BadHttpRequestException ex) {
			return (null, Results.StatusCode(ex.StatusCode));
		} catch(OperationCanceledException) {
			return (null, Results.StatusCode(408));
		}
	}

	sealed record CreateRequest(string? TsAuthKey);
	sealed record RedeemRequest(string? Code, string? DeviceName);
}
