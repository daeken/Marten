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
		if(config.DevOrigin != null) origins.Add(config.DevOrigin.TrimEnd('/'));
	}

	public bool HostAllowed(HttpContext ctx) => hosts.Contains(ctx.Request.Host.Value ?? "");

	public bool OriginAllowed(HttpContext ctx) {
		var origin = ctx.Request.Headers.Origin.ToString();
		return origin.Length == 0 || origins.Contains(origin);
	}

	public bool TokenValid(HttpContext ctx) {
		var supplied = ctx.Request.Headers.Authorization.ToString() is { } auth && auth.StartsWith("Bearer ")
			? auth["Bearer ".Length..]
			: ctx.Request.Query["token"].ToString();
		return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(Token));
	}

	public RequestDelegate Middleware(RequestDelegate next) => async ctx => {
		if(!HostAllowed(ctx) || !OriginAllowed(ctx)) {
			ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
			return;
		}
		if(ctx.Request.Path.StartsWithSegments("/api") && !TokenValid(ctx)) {
			ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
			return;
		}
		await next(ctx);
	};
}
