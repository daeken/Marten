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
	public Uri? PairingOrigin { get; }
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
		if(config.PairingOrigin != null) {
			PairingOrigin = ParsePairingOrigin(config.PairingOrigin);
			if(PairingOrigin != null) {
				hosts.Add(PairingOrigin.Authority);
				origins.Add(PairingOrigin.GetLeftPart(UriPartial.Authority));
			}
		}
		if(config.DevOrigin != null) origins.Add(config.DevOrigin.TrimEnd('/'));
	}

	static Uri? ParsePairingOrigin(string value) {
		if(value.Length > 512 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '%' or '?' or '#' or '@') ||
			!value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
			!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.IsLoopback || uri.Port <= 0 ||
			uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)) return null;
		// Require origin syntax; Uri normalizes paths such as /x/.. to / and would otherwise accept them.
		var afterScheme = value["https://".Length..];
		var slash = afterScheme.IndexOf('/');
		return slash < 0 || slash == afterScheme.Length - 1 ? new UriBuilder(uri) { Host = uri.IdnHost, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri : null;
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
		if(!HostAllowed(ctx) || !OriginAllowed(ctx)) {
			ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
			return;
		}
		var atPairingOrigin = PairingOrigin is { } origin &&
			string.Equals(ctx.Request.Host.Value, origin.Authority, StringComparison.OrdinalIgnoreCase);
		if(atPairingOrigin && !PairingEndpoints.IsPairedUiEndpoint(ctx)) {
			ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
			return;
		}
		var metadata = ctx.GetEndpoint()?.Metadata;
		var bearer = Bearer(ctx);
		if(metadata?.GetMetadata<PairingEndpoints.Management>() != null) {
			ctx.Response.Headers.CacheControl = "no-store";
			// The original operator has to bootstrap locally. Already-paired devices have full UI authority.
			if(Pairing?.Authenticate(bearer) is null && (!LocalOperatorOrigin(ctx) || !IsOperator(bearer))) {
				ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
				return;
			}
		} else if(metadata?.GetMetadata<PairingEndpoints.Redemption>() != null) {
			ctx.Response.Headers.CacheControl = "no-store";
			// The only credential for redemption is the code in the JSON body.
			if(!atPairingOrigin ||
				(ctx.Request.Headers.Origin.ToString() is { Length: > 0 } requestOrigin && requestOrigin != PairingOrigin!.GetLeftPart(UriPartial.Authority)) ||
				ctx.Request.Headers.ContainsKey("Authorization") || ctx.Request.QueryString.HasValue) {
				ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
				return;
			}
		} else if(ctx.Request.Path.StartsWithSegments("/api") && (atPairingOrigin || !TokenValid(ctx))) {
			if(Pairing?.Authenticate(bearer) is { } revoked) {
				ctx.Response.Headers.CacheControl = "no-store";
				if(metadata?.GetMetadata<PairingEndpoints.PairedAction>() == null) {
					ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
					return;
				}
				ctx.Items[DeviceRevocation] = revoked;
				await next(ctx);
				return;
			}
			if(atPairingOrigin || ClientAuth?.Invoke(Supplied(ctx)) is not { } client) {
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
