using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Marten.Agents;
using Microsoft.AspNetCore.Http.Features;

namespace Marten.Web;

internal static class ReleaseDrainEndpoints {
	const string Path = "/api/release/drain";
	const string IdHeader = "X-Marten-Drain-Id";
	const string PidHeader = "X-Marten-Drain-Pid";
	sealed class Control;
	internal sealed class OutsideWrite;

	internal static void MapReleaseDrain(this RouteGroupBuilder api, RestartDrain drain) {
		var routes = api.MapGroup("/release").WithMetadata(OperatorRoute.LocalBearer, new Control());
		routes.MapPost("/drain", (Delegate)((HttpContext ctx) => Begin(ctx, drain)));
		routes.MapGet("/drain", (Delegate)((HttpContext ctx) => Identify(ctx, id => Status(drain.GetRelease(id)))));
		routes.MapDelete("/drain", (Delegate)((HttpContext ctx) => Identify(ctx, id => drain.AbortRelease(id) ? Results.NoContent() : Conflict())));
	}

	// Install after routing and Security so existing auth and permissions are still authoritative.
	// A request that won admission owns it until its endpoint and response work have finished.
	internal static RequestDelegate GateWrites(RestartDrain drain, RequestDelegate next) => async ctx => {
		var endpoint = ctx.GetEndpoint();
		var method = ctx.Request.Method;
		if(endpoint == null || endpoint.Metadata.GetMetadata<Control>() != null || !ctx.Request.Path.StartsWithSegments("/api") ||
			HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) ||
			(endpoint.Metadata.GetMetadata<OutsideWrite>() != null && ctx.Items["client"] is not string)) {
			await next(ctx);
			return;
		}
		using var operation = drain.Enter();
		if(operation == null) {
			await Retry(ctx).ExecuteAsync(ctx);
			return;
		}
		await next(ctx);
	};

	internal static IResult Retry(HttpContext ctx) {
		ctx.Response.Headers.CacheControl = "no-store";
		ctx.Response.Headers.RetryAfter = "1";
		return Results.Problem("Marten is draining for restart. This write was not accepted; retry after service resumes.", statusCode: 503);
	}

	static bool Exact(HttpContext ctx) => string.Equals(ctx.Request.Path.Value, Path, StringComparison.Ordinal) &&
		(ctx.Features.Get<IHttpRequestFeature>()?.RawTarget is not { Length: > 0 } raw || raw == Path);

	static bool ValidId(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
	static IResult Conflict() => Results.Conflict(new { error = "Drain identity does not match this runtime or the lease is unavailable." });
	static IResult Status(RestartDrain.ReleaseStatus? status) => status is null ? Conflict() : Results.Ok(status);

	static IResult Identify(HttpContext ctx, Func<string, IResult> action) {
		if(!Exact(ctx)) return Results.NotFound();
		var id = ctx.Request.Headers[IdHeader].ToString();
		if(!ValidId(id) || !int.TryParse(ctx.Request.Headers[PidHeader].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
			return Results.BadRequest();
		return pid == Environment.ProcessId ? action(id) : Conflict();
	}

	static async Task<IResult> Begin(HttpContext ctx, RestartDrain drain) {
		if(!Exact(ctx)) return Results.NotFound();
		const int limit = 512;
		if(!ctx.Request.HasJsonContentType() || ctx.Request.ContentLength > limit) return Results.BadRequest();
		if(ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = limit;
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
		timeout.CancelAfter(TimeSpan.FromSeconds(5));
		var buffer = new byte[limit + 1];
		try {
			var size = await ctx.Request.Body.ReadAtLeastAsync(buffer, buffer.Length, false, timeout.Token);
			if(size > limit) return Results.BadRequest();
			var req = JsonSerializer.Deserialize<BeginRequest>(buffer.AsSpan(0, size));
			if(req == null || !ValidId(req.Id) || req.Pid <= 0 || req.LeaseSeconds is < 1 or > RestartDrain.MaxLeaseSeconds) return Results.BadRequest();
			return req.Pid == Environment.ProcessId ? Status(drain.BeginRelease(req.Id!, req.LeaseSeconds)) : Conflict();
		} catch(JsonException) { return Results.BadRequest(); }
		catch(BadHttpRequestException) { return Results.BadRequest(); }
		catch(OperationCanceledException) { return Results.StatusCode(408); }
	}

	[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
	sealed class BeginRequest {
		[JsonPropertyName("id")] public string? Id { get; init; }
		[JsonPropertyName("pid")] public int Pid { get; init; }
		[JsonPropertyName("leaseSeconds")] public int LeaseSeconds { get; init; }
	}
}
