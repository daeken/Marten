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
	readonly HashSet<string> hosts;
	readonly HashSet<string> origins;

	public Security(Home home, Config config) {
		var path = Path.Combine(home.Secrets, "ui-token");
		if(!File.Exists(path)) {
			AtomicFile.Write(path, System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));
			if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
		Token = File.ReadAllText(path).Trim();
		hosts = [$"127.0.0.1:{config.Port}", $"localhost:{config.Port}"];
		origins = hosts.Select(h => $"http://{h}").ToHashSet();
		// Names Marten is reached by through an HTTPS proxy, e.g. `tailscale serve` for the phone app.
		foreach(var h in config.RemoteHosts.Select(h => h.Trim().TrimEnd('/')).Where(h => h.Length > 0)) {
			var bare = h.Replace("https://", "").Replace("http://", "");
			hosts.Add(bare);
			origins.Add($"https://{bare}");
		}
		if(config.DevOrigin != null) origins.Add(config.DevOrigin.TrimEnd('/'));
	}

	public bool HostAllowed(HttpContext ctx) => hosts.Contains(ctx.Request.Host.Value ?? "");

	public bool OriginAllowed(HttpContext ctx) {
		var origin = ctx.Request.Headers.Origin.ToString();
		return origin.Length == 0 || origins.Contains(origin);
	}

	/// <summary>Maps an outside client's token to its name, or null.</summary>
	public Func<string, string?>? ClientAuth { get; set; }

	static string Supplied(HttpContext ctx) => ctx.Request.Headers.Authorization.ToString() is { } auth && auth.StartsWith("Bearer ")
		? auth["Bearer ".Length..].Trim()
		: ctx.Request.Query["token"].ToString();

	public bool TokenValid(HttpContext ctx) =>
		CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Supplied(ctx)), Encoding.UTF8.GetBytes(Token));

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
		if(ctx.Request.Path.StartsWithSegments("/api") && !TokenValid(ctx)) {
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
