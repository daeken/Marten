using System.Net;
using System.Security.Cryptography;
using System.Text;
using Marten.Storage;

namespace Marten.Web;

/// <summary>
/// Localhost is not a security boundary: any web page can send requests to it, and DNS rebinding
/// can make them look same-site. So the API needs a random token, and Host and Origin must name
/// Marten itself.
/// </summary>
public sealed class Security {
	public string Token { get; }
	readonly HashSet<string> localHosts;
	public DevicePairing? Pairing { get; init; }
	public string? TailnetHost { get; }
	readonly HashSet<string> hosts;
	readonly HashSet<string> origins;

	public Security(Home home, Config config) {
		var path = Path.Combine(home.Secrets, "ui-token");
		if(!File.Exists(path)) {
			AtomicFile.Write(path, System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));
			if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
		Token = File.ReadAllText(path).Trim();
		localHosts = new(StringComparer.OrdinalIgnoreCase) { $"127.0.0.1:{config.Port}", $"localhost:{config.Port}" };
		hosts = new(localHosts, StringComparer.OrdinalIgnoreCase);
		origins = localHosts.Select(h => $"http://{h}").ToHashSet();
		// Names Marten is reached by through an HTTPS proxy, e.g. `tailscale serve` for the phone app.
		foreach(var h in config.RemoteHosts.Select(h => h.Trim().TrimEnd('/')).Where(h => h.Length > 0)) {
			var bare = h.Replace("https://", "").Replace("http://", "");
			hosts.Add(bare);
			origins.Add($"https://{bare}");
		}
		TailnetHost = config.Tailnet?.CanonicalHost();
		if(config.DevOrigin != null) origins.Add(config.DevOrigin.TrimEnd('/'));
	}

	public bool HostAllowed(HttpContext ctx) => hosts.Contains(ctx.Request.Host.Value ?? "");

	public bool OriginAllowed(HttpContext ctx) {
		var origin = ctx.Request.Headers.Origin.ToString();
		return origin.Length == 0 || origins.Contains(origin);
	}

	/// <summary>Maps an outside client's token to its name, or null.</summary>
	public Func<string, string?>? ClientAuth { get; set; }

	static string? Bearer(HttpContext ctx) => ctx.Request.Headers.Authorization.ToString() is { } auth &&
		auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth["Bearer ".Length..].Trim() : null;

	static string Supplied(HttpContext ctx) => Bearer(ctx) ?? ctx.Request.Query["token"].ToString();

	bool IsOperator(string? token) => token != null &&
		CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Token));

	public bool TokenValid(HttpContext ctx) => IsOperator(Supplied(ctx));

	bool LocalOperatorOrigin(HttpContext ctx) {
		var host = ctx.Request.Host.Value ?? "";
		var origin = ctx.Request.Headers.Origin.ToString();
		return ctx.Connection.RemoteIpAddress is { } peer && IPAddress.IsLoopback(peer) && localHosts.Contains(host) &&
			(origin.Length == 0 || origin.Equals($"http://{host}", StringComparison.OrdinalIgnoreCase));
	}

	static readonly object DeviceRevocation = new();
	public static CancellationToken DeviceRevocationOf(HttpContext ctx) =>
		ctx.Items.TryGetValue(DeviceRevocation, out var item) && item is CancellationToken token ? token : default;

	/// <summary>What an outside client's token reaches: the /api/ext routes, plus reading transcripts, tasks, messages and memory.</summary>
	public static bool ClientMayCall(HttpContext ctx) {
		var path = ctx.Request.Path.Value ?? "";
		if(path.StartsWith("/api/ext/") || path == "/api/ext") return true;
		if(!HttpMethods.IsGet(ctx.Request.Method)) return false;
		return System.Text.RegularExpressions.Regex.IsMatch(path, @"^/api/(agents/[^/]+/(transcript|tasks)|messages|projects/[^/]+/memory)$");
	}

	public RequestDelegate Middleware(RequestDelegate next) => async ctx => {
		var metadata = ctx.GetEndpoint()?.Metadata;
		var bearer = Bearer(ctx);
		if(TailnetHost is { } host && string.Equals(ctx.Request.Host.Host.TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase)) {
			// No legacy fallback, even when the authority is misconfigured as a web proxy alias.
			ctx.Response.Headers.CacheControl = "no-store";
			if(ctx.Request.Host.Value != host || (ctx.Request.Headers.ContainsKey("Origin") && ctx.Request.Headers.Origin.ToString() != $"https://{host}")) {
				ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
				return;
			}
			if(metadata?.GetMetadata<TailnetApi>() == null) {
				ctx.Response.StatusCode = StatusCodes.Status404NotFound;
				return;
			}
			if(metadata.GetMetadata<PairingEndpoints.Redemption>() != null) {
				if(ctx.Request.Headers.ContainsKey("Authorization") || ctx.Request.QueryString.HasValue) {
					ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
					return;
				}
			} else if(Pairing?.Authenticate(bearer) is { } revoked && !ctx.Request.Query.ContainsKey("token")) {
				ctx.Items[DeviceRevocation] = revoked;
			} else {
				ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
				return;
			}
			await next(ctx);
			return;
		}
		if(!HostAllowed(ctx) || !OriginAllowed(ctx) || metadata?.GetMetadata<PairingEndpoints.Redemption>() != null) {
			ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
			return;
		}
		if(metadata?.GetMetadata<PairingEndpoints.Management>() != null) {
			ctx.Response.Headers.CacheControl = "no-store";
			if(!LocalOperatorOrigin(ctx) || !IsOperator(bearer)) {
				ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
				return;
			}
		} else if(ctx.Request.Path.StartsWithSegments("/api") && !TokenValid(ctx)) {
			if(ClientAuth?.Invoke(Supplied(ctx)) is not { } client) {
				ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
				return;
			}
			if(!ClientMayCall(ctx)) {
				ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
				return;
			}
			ctx.Items["client"] = client;
		}
		await next(ctx);
	};
}
