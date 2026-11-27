using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Marten.Storage;
using Microsoft.AspNetCore.Http.Features;

namespace Marten.Web;

internal static class JevEndpoints {
	internal static void MapJevSettings(this RouteGroupBuilder api, JevSettings settings) {
		var routes = api.MapGroup("/jev").WithMetadata(OperatorRoute.LocalBearer);
		routes.MapGet("/settings", () => settings.Status());
		routes.MapPut("/settings", (Delegate)((HttpContext ctx) => Update<OptionsRequest>(ctx,
			body => Result(settings, settings.SetOptions(body.Enabled, body.ImagePreviewEnabled)))));
		routes.MapPut("/key", (Delegate)((HttpContext ctx) => Update<KeyRequest>(ctx,
			body => Result(settings, settings.Replace(body.Key)))));
		routes.MapDelete("/key", () => {
			var removed = settings.Remove() == JevChange.Saved;
			return Results.Json(new { settings = settings.Status(), localRemovalSucceeded = removed, upstreamRevoked = false },
				statusCode: removed ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
		});
	}

	static IResult Result(JevSettings settings, JevChange result) => result switch {
		JevChange.Saved => Results.Ok(settings.Status()),
		JevChange.InvalidKey => Results.Problem("Enter a Jev API key (up to 512 characters; letters, digits, ._~+/-, and optional trailing =).", statusCode: 400),
		JevChange.KeyRequired => Results.Problem("Set a Jev API key first.", statusCode: 409),
		_ => Results.Problem("Jev settings could not be saved. Capability is off if storage is unavailable.", statusCode: 503),
	};

	static async Task<IResult> Update<T>(HttpContext ctx, Func<T, IResult> apply) where T : class {
		if(!ctx.Request.HasJsonContentType()) return Results.StatusCode(415);
		const int limit = 16 * 1024;
		if(ctx.Request.ContentLength > limit) return Results.StatusCode(413);
		if(ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = limit;
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
		timeout.CancelAfter(TimeSpan.FromSeconds(5));
		var buffer = new byte[limit + 1];
		try {
			var length = await ctx.Request.Body.ReadAtLeastAsync(buffer, buffer.Length, false, timeout.Token);
			if(length > limit) return Results.StatusCode(413);
			return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), JevSettings.StrictJson) is { } body
				? apply(body) : Results.BadRequest();
		} catch(JsonException) { return Results.BadRequest(); }
		catch(BadHttpRequestException ex) { return Results.StatusCode(ex.StatusCode); }
		catch(OperationCanceledException) { return Results.StatusCode(408); }
		finally { CryptographicOperations.ZeroMemory(buffer); }
	}

	sealed class KeyRequest { [JsonRequired] public string? Key { get; init; } }
	sealed class OptionsRequest {
		[JsonRequired] public bool Enabled { get; init; }
		[JsonRequired] public bool ImagePreviewEnabled { get; init; }
	}
}
