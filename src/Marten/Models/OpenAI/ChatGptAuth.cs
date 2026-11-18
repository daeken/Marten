using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Marten.Storage;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Marten.Models.OpenAI;

/// <summary>
/// ChatGPT subscription sign-in: OAuth with PKCE against auth.openai.com, registering Marten as a
/// dynamic agent client. Ported from Cosmurgy's auth.ts and Primer's provider.rs.
/// </summary>
public sealed class ChatGptAuth {
	public const string Issuer = "https://auth.openai.com";
	public const string Resource = "https://api.openai.com/v1";
	const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
	const string PlanScope = "chatgpt.tokens.use.direct";

	public sealed record Credentials {
		public required string HostId { get; init; }
		public string? ClientId { get; init; }
		public string? Subject { get; init; }
		public string? Email { get; init; }
		public string? AccessToken { get; init; }
		public string? RefreshToken { get; init; }
		public string? IdToken { get; init; }
		public long ExpiresAt { get; init; }
		public long? EarliestRefreshAt { get; init; }
		public string? Scope { get; init; }
	}

	readonly string path;
	readonly HttpClient http;
	readonly SemaphoreSlim refreshGate = new(1, 1);
	Credentials creds;
	PendingLogin? pending;

	// OAuth replies are small and buffered. Unlike inference, bound the entire request and body.
	internal static HttpClient CreateHttpClient() {
		var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) }) {
			Timeout = TimeSpan.FromSeconds(60),
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Marten/0.1");
		return client;
	}

	public ChatGptAuth(Home home, HttpClient http) {
		path = Path.Combine(home.Secrets, "openai.json");
		this.http = http;
		creds = JsonIO.Load(path, () => new Credentials { HostId = $"urn:uuid:{Guid.NewGuid()}" });
		Save();
	}

	void Save() {
		JsonIO.Save(path, creds);
		if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
	}

	public bool Connected => creds.AccessToken != null;
	public string? Email => creds.Email;
	public bool SigningIn => pending != null;
	public string? LastError { get; private set; }

	static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	static string Secret() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

	sealed class PendingLogin(HttpListener listener, string redirect) : IDisposable {
		public readonly HttpListener Listener = listener;
		public readonly string Redirect = redirect;
		public readonly string State = Secret(), Nonce = Secret(), Verifier = Secret();
		public void Dispose() { try { Listener.Close(); } catch { } }
	}

	/// <summary>Start a sign-in. Returns the URL Sera opens; the loopback callback finishes it in the background.</summary>
	public string BeginLogin() {
		pending?.Dispose();
		var (listener, port) = ListenOnFreePort();
		var login = new PendingLogin(listener, $"http://127.0.0.1:{port}/auth/callback");
		pending = login;
		var query = new Dictionary<string, string?> {
			["client_id"] = creds.ClientId ?? "dynamic_agent_client",
			["ext_agent_host_id"] = creds.HostId,
			["response_type"] = "code",
			["redirect_uri"] = login.Redirect,
			["scope"] = Scopes,
			["resource"] = Resource,
			["state"] = login.State,
			["nonce"] = login.Nonce,
			["code_challenge_method"] = "S256",
			["code_challenge"] = System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(login.Verifier))),
		};
		if(creds.ClientId == null) query["agent_name_hint"] = "Marten";
		if(creds.IdToken != null) query["id_token_hint"] = creds.IdToken;
		_ = Task.Run(() => AwaitCallback(login));
		// The URL can carry an ID-token hint. Never log it.
		return $"{Issuer}/api/accounts/authorize?" + string.Join("&",
			query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
	}

	static (HttpListener, int) ListenOnFreePort() {
		for(var attempt = 0; ; attempt++) {
			var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			var port = ((IPEndPoint) probe.LocalEndpoint).Port;
			probe.Stop();
			var listener = new HttpListener();
			listener.Prefixes.Add($"http://127.0.0.1:{port}/auth/");
			try {
				listener.Start();
				return (listener, port);
			} catch(HttpListenerException) when(attempt < 5) { }
		}
	}

	async Task AwaitCallback(PendingLogin login) {
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
		using var reg = timeout.Token.Register(login.Dispose);
		try {
			while(true) {
				var ctx = await login.Listener.GetContextAsync();
				var q = ctx.Request.QueryString;
				if(ctx.Request.Url?.AbsolutePath != "/auth/callback") {
					ctx.Response.StatusCode = 404;
					ctx.Response.Close();
					continue;
				}
				if(!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(q["state"] ?? ""), Encoding.UTF8.GetBytes(login.State))) {
					await Respond(ctx, 400, "Invalid sign-in state. Start again from Marten.");
					continue;
				}
				try {
					await Finish(login, q["error"], q["code"], q["client_id"]);
					LastError = null;
					await Respond(ctx, 200, "Marten is connected to ChatGPT. You can close this tab.");
				} catch(Exception e) {
					LastError = e.Message;
					await Respond(ctx, 400, e.Message);
				}
				return;
			}
		} catch(Exception) when(timeout.IsCancellationRequested || pending != login) {
		} finally {
			if(pending == login) pending = null;
			login.Dispose();
		}
	}

	static async Task Respond(HttpListenerContext ctx, int status, string message) {
		ctx.Response.StatusCode = status;
		ctx.Response.ContentType = "text/html; charset=utf-8";
		ctx.Response.Headers["Cache-Control"] = "no-store";
		ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'";
		var body = Encoding.UTF8.GetBytes($"<!doctype html><title>Marten</title><p>{WebUtility.HtmlEncode(message)}</p>");
		await ctx.Response.OutputStream.WriteAsync(body);
		ctx.Response.Close();
	}

	async Task Finish(PendingLogin login, string? error, string? code, string? returnedClientId) {
		if(error != null) throw new InvalidOperationException("ChatGPT sign-in was declined.");
		if(code == null) throw new InvalidOperationException("No authorization code was returned.");
		var clientId = creds.ClientId ?? returnedClientId;
		if(clientId is null or "dynamic_agent_client" || clientId.Length > 256)
			throw new InvalidOperationException("Registration did not return a client ID. Sign in again.");
		if(creds.ClientId != null && returnedClientId != null && returnedClientId != creds.ClientId)
			throw new InvalidOperationException("The returned registration did not match this Marten install.");
		var tokens = await Exchange(new() {
			["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code,
			["code_verifier"] = login.Verifier, ["redirect_uri"] = login.Redirect, ["resource"] = Resource,
		});
		var idToken = tokens["id_token"]?.GetValue<string>() ?? throw new InvalidOperationException("ChatGPT did not return an identity token.");
		var identity = await VerifyIdToken(idToken, clientId);
		if(identity.GetClaim("nonce")?.Value != login.Nonce) throw new InvalidOperationException("ChatGPT identity verification failed.");
		var subject = identity.Subject;
		if(creds.Subject != null && creds.Subject != subject)
			throw new InvalidOperationException("That is a different ChatGPT account. Sign out first to switch.");
		var scope = tokens["scope"]?.GetValue<string>() ?? "";
		if(!scope.Split(' ').Contains(PlanScope))
			throw new InvalidOperationException("ChatGPT plan usage was not granted. Sign in again and allow it.");
		creds = creds with {
			ClientId = clientId, Subject = subject,
			Email = identity.TryGetClaim("email", out var email) ? email.Value : null,
			AccessToken = tokens["access_token"]!.GetValue<string>(),
			RefreshToken = tokens["refresh_token"]?.GetValue<string>(),
			IdToken = idToken,
			ExpiresAt = Now + tokens["expires_in"]!.GetValue<long>() * 1000,
			EarliestRefreshAt = tokens["earliest_refresh_at"]?.GetValue<long>(),
			Scope = scope,
		};
		Save();
	}

	async Task<JsonObject> Exchange(Dictionary<string, string> form, CancellationToken ct = default) {
		JsonObject tokens;
		try {
			using var content = new FormUrlEncodedContent(form);
			using var response = await http.PostAsync($"{Issuer}/api/accounts/oauth/token", content, ct);
			var status = (int) response.StatusCode;
			if(status is 408 or 429 or >= 500)
				throw new TransientModelException($"ChatGPT authorization is temporarily unavailable ({status}).");
			if(!response.IsSuccessStatusCode)
				throw new InvalidOperationException($"ChatGPT authorization failed ({status}). Sign in again.");
			tokens = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject
				?? throw new InvalidOperationException("ChatGPT returned an unreadable token response.");
		} catch(OperationCanceledException e) {
			ct.ThrowIfCancellationRequested();
			throw new TransientModelException("Timed out waiting for ChatGPT authorization.", e);
		} catch(Exception e) when(e is HttpRequestException or IOException) {
			ct.ThrowIfCancellationRequested();
			throw new TransientModelException("Could not reach ChatGPT authorization.", e);
		}
		if(tokens["access_token"] == null || tokens["expires_in"] == null ||
		   !string.Equals(tokens["token_type"]?.GetValue<string>(), "bearer", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("ChatGPT returned an incomplete token response.");
		return tokens;
	}

	async Task<JsonWebToken> VerifyIdToken(string token, string audience) {
		var jwks = new JsonWebKeySet(await http.GetStringAsync($"{Issuer}/.well-known/jwks.json"));
		var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters {
			ValidIssuer = Issuer, ValidAudience = audience, IssuerSigningKeys = jwks.GetSigningKeys(),
			ValidAlgorithms = ["RS256"],
		});
		if(!result.IsValid) throw new InvalidOperationException("ChatGPT identity token did not verify.");
		return (JsonWebToken) result.SecurityToken;
	}

	/// <summary>A current access token, refreshed when it is within a minute of expiry or when forced after a 401.</summary>
	public async Task<string> TokenAsync(bool forceRefresh, CancellationToken ct) {
		await refreshGate.WaitAsync(ct);
		try {
			if(creds.AccessToken == null) throw new NotConnectedException("Connect ChatGPT in the Marten UI first.");
			if(!forceRefresh && creds.ExpiresAt > Now + 60_000) return creds.AccessToken;
			if(creds.RefreshToken == null) throw new NotConnectedException("The ChatGPT session expired. Sign in again.");
			if(creds.EarliestRefreshAt is { } earliest && Now / 1000 < earliest) {
				if(creds.ExpiresAt > Now) return creds.AccessToken;
				throw new TransientModelException("ChatGPT renewal is not available yet.");
			}
			JsonObject tokens;
			try {
				tokens = await Exchange(new() {
					["grant_type"] = "refresh_token", ["client_id"] = creds.ClientId!,
					["refresh_token"] = creds.RefreshToken, ["resource"] = Resource,
				}, ct);
			} catch(InvalidOperationException e) {
				LastError = e.Message;
				throw new NotConnectedException(e.Message);
			}
			var scope = tokens["scope"]?.GetValue<string>() ?? creds.Scope ?? "";
			if(!scope.Split(' ').Contains(PlanScope)) throw new NotConnectedException("ChatGPT plan access was revoked. Sign in again.");
			creds = creds with {
				AccessToken = tokens["access_token"]!.GetValue<string>(),
				RefreshToken = tokens["refresh_token"]?.GetValue<string>() ?? creds.RefreshToken,
				IdToken = tokens["id_token"]?.GetValue<string>() ?? creds.IdToken,
				ExpiresAt = Now + tokens["expires_in"]!.GetValue<long>() * 1000,
				EarliestRefreshAt = tokens["earliest_refresh_at"]?.GetValue<long>(),
				Scope = scope,
			};
			Save();
			return creds.AccessToken;
		} finally {
			refreshGate.Release();
		}
	}

	/// <summary>Forget tokens locally, keeping only the host id; the next sign-in registers afresh.</summary>
	public void Logout() {
		creds = new Credentials { HostId = creds.HostId };
		Save();
	}
}
