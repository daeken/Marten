using System.Buffers.Text;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using Marten.Storage;

namespace Marten.Web;

public sealed class DevicePairing(Home home, TimeProvider? time = null) {
	public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
	const int MaxPending = 8, MaxDevices = 64;
	public const int AttemptsPerMinute = 60;
	readonly TimeProvider clock = time ?? TimeProvider.System;
	readonly string path = Path.Combine(home.Secrets, "paired-devices.json");
	readonly object gate = new();
	readonly Dictionary<string, long> pending = new();
	readonly Queue<long> attempts = new();
	readonly Dictionary<string, Credential> devices = JsonIO.Load(Path.Combine(home.Secrets, "paired-devices.json"), () => new List<Stored>())
		.ToDictionary(s => s.Hash, s => new Credential(s));
	int inFlight;

	public sealed record Device(Guid Id, string Name, DateTimeOffset CreatedAt);
	sealed record Stored(Device Device, string Hash);
	sealed class Credential(Stored stored) {
		public Stored Stored { get; } = stored;
		public CancellationTokenSource Revoked { get; } = new();
	}
	public sealed record Invitation(string Code, DateTimeOffset ExpiresAt);

	static string Secret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
	static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(secret)));
	static bool ValidSecret(string? value) => value is { Length: 43 } &&
		value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

	public Invitation? Create() {
		lock(gate) {
			var now = clock.GetTimestamp();
			foreach(var key in pending.Where(p => clock.GetElapsedTime(p.Value, now) >= CodeLifetime).Select(p => p.Key).ToList())
				pending.Remove(key);
			if(pending.Count >= MaxPending || devices.Count >= MaxDevices) return null;
			var code = Secret();
			pending.Add(Hash(code), now);
			return new(code, clock.GetUtcNow() + CodeLifetime);
		}
	}

	// Called before parsing any body; all attempts share the budget, even behind a single proxy IP.
	public IDisposable? BeginAttempt() {
		lock(gate) {
			var now = clock.GetTimestamp();
			while(attempts.TryPeek(out var at) && clock.GetElapsedTime(at, now) >= TimeSpan.FromMinutes(1)) attempts.Dequeue();
			if(attempts.Count >= AttemptsPerMinute || inFlight >= 8) return null;
			attempts.Enqueue(now);
			inFlight++;
			return new Attempt(this);
		}
	}

	sealed class Attempt(DevicePairing owner) : IDisposable {
		public void Dispose() { lock(owner.gate) owner.inFlight--; }
	}

	public string? Redeem(string? code, string? deviceName) {
		if(!ValidSecret(code) || deviceName?.Trim() is not { Length: > 0 and <= 80 } name || name.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)) return null;
		lock(gate) {
			if(!pending.Remove(Hash(code!), out var at) || clock.GetElapsedTime(at) >= CodeLifetime || devices.Count >= MaxDevices) return null;
			var token = Secret();
			var stored = new Stored(new Device(Guid.NewGuid(), name, clock.GetUtcNow()), Hash(token));
			Save(devices.Values.Select(d => d.Stored).Append(stored));
			devices.Add(stored.Hash, new Credential(stored));
			return token;
		}
	}

	public Device[] List() {
		lock(gate) return devices.Values.Select(d => d.Stored.Device).OrderBy(d => d.CreatedAt).ToArray();
	}

	public bool Revoke(Guid id) {
		Credential credential;
		lock(gate) {
			var match = devices.Values.FirstOrDefault(d => d.Stored.Device.Id == id);
			if(match == null) return false;
			credential = match;
			Save(devices.Values.Where(d => d != credential).Select(d => d.Stored));
			devices.Remove(credential.Stored.Hash);
		}
		credential.Revoked.Cancel();
		return true;
	}

	public CancellationToken? Authenticate(string? token) {
		if(!ValidSecret(token)) return null;
		lock(gate) return devices.TryGetValue(Hash(token!), out var device) ? device.Revoked.Token : null;
	}

	void Save(IEnumerable<Stored> entries) => JsonIO.Save(path, entries.ToArray());
}
