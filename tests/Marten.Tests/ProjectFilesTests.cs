using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Marten.Projects;
using Marten.Storage;
using Marten.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Marten.Tests;

public class ProjectFilesTests {
	[DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "link")]
	static extern int Link(string existing, string path);
	[DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mkfifo")]
	static extern int Fifo(string path, ushort mode);

	[Fact]
	public async Task PairedFileBrowserIsOffByDefaultAndNeverAcceptsOutsideOrOperatorCredentials() {
		await using var server = await Fixture.Start();
		Assert.Equal(HttpStatusCode.NotFound, (await server.Get("roots")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await server.Get("roots", token: "")).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await server.Get("roots", token: server.Operator, host: server.LocalHost)).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await server.Get("roots", token: "outside", host: server.LocalHost)).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await server.Get("roots?extra=1")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await server.Get("roots", host: "alias.example")).StatusCode);
	}

	[Fact]
	public async Task ApprovedBindingListsOnlySafeEntriesAndUsesScopedRevisionHandles() {
		if(!ProjectFileSystem.Supported) return;
		await using var server = await Fixture.Start();
		var root = server.Grant();
		var dir = Directory.CreateDirectory(Path.Combine(root, "folder")).FullName;
		await File.WriteAllTextAsync(Path.Combine(dir, "nested.txt"), "nested");
		await File.WriteAllTextAsync(Path.Combine(root, "plain.txt"), "hello");
		await File.WriteAllTextAsync(Path.Combine(root, ".env"), "hidden");
		File.CreateSymbolicLink(Path.Combine(root, "link.txt"), "plain.txt");
		Directory.CreateSymbolicLink(Path.Combine(root, "link-dir"), dir);
		var linked = Path.Combine(root, "linked.txt");
		await File.WriteAllTextAsync(linked, "must not be listed");
		Assert.Equal(0, Link(linked, Path.Combine(root, "hardlink.txt")));
		Assert.Equal(0, Fifo(Path.Combine(root, "queue"), 0x180));
		var roots = await Json(await server.Get("roots"));
		var rootId = roots.EnumerateArray().Single().GetProperty("id").GetString();
		var rows = (await Json(await server.Get($"entries/{rootId}"))).GetProperty("entries").EnumerateArray().ToArray();
		Assert.Equal(new[] { "folder", "plain.txt" }, rows.Select(e => e.GetProperty("name").GetString()));
		var subId = rows[0].GetProperty("id").GetString();
		Assert.Equal("nested.txt", (await Json(await server.Get($"entries/{subId}"))).GetProperty("entries")[0].GetProperty("name").GetString());
		var id = rows[1].GetProperty("id").GetString();
		var bytes = await server.Get($"bytes/{id}/share");
		Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
		Assert.Equal("attachment", bytes.Content.Headers.ContentDisposition?.DispositionType);
		Assert.Equal("hello", await bytes.Content.ReadAsStringAsync());
		Assert.Equal(HttpStatusCode.NotFound, (await server.Get($"bytes/{id}/share", token: server.Other)).StatusCode);
		await File.WriteAllTextAsync(Path.Combine(root, "plain.txt"), "changed length");
		Assert.Equal(HttpStatusCode.Conflict, (await server.Get($"bytes/{id}/share")).StatusCode);
		Directory.Move(root, root + "-old");
		Directory.CreateDirectory(root);
		Assert.Empty((await Json(await server.Get("roots"))).EnumerateArray());
	}

	[Fact]
	public async Task PaginatesAndCapsTextAndFullFileBytes() {
		if(!ProjectFileSystem.Supported) return;
		await using var server = await Fixture.Start();
		var root = server.Grant();
		for(var i = 0; i < 100; i++) File.WriteAllText(Path.Combine(root, $"a{i:000}"), "");
		using(var file = File.Create(Path.Combine(root, "z.txt"))) file.SetLength(33 * 1024 * 1024);
		var rootId = (await Json(await server.Get("roots")))[0].GetProperty("id").GetString();
		var first = await Json(await server.Get($"entries/{rootId}"));
		Assert.Equal(100, first.GetProperty("entries").GetArrayLength());
		var next = first.GetProperty("next").GetString();
		var row = (await Json(await server.Get($"entries/{next}"))).GetProperty("entries").EnumerateArray().Single();
		var id = row.GetProperty("id").GetString();
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await server.Get($"bytes/{id}/share")).StatusCode);
		var prefix = await server.Get($"bytes/{id}/text");
		Assert.Equal(HttpStatusCode.OK, prefix.StatusCode);
		Assert.Equal(256 * 1024, (await prefix.Content.ReadAsByteArrayAsync()).Length);
	}

	static async Task<JsonElement> Json(HttpResponseMessage r) {
		Assert.Equal(HttpStatusCode.OK, r.StatusCode);
		Assert.True(r.Headers.CacheControl?.NoStore);
		using var d = JsonDocument.Parse(await r.Content.ReadAsByteArrayAsync());
		return d.RootElement.Clone();
	}

	sealed class Fixture(TempDir dir, WebApplication app, string endpoint, Project project,
		string primary, string other, string op, List<ProjectFileGrant> grants) : IAsyncDisposable {
		readonly HttpClient http = new() { BaseAddress = new Uri(endpoint) };
		public string LocalHost => new Uri(endpoint).Authority;
		public string Other => other;
		public string Operator => op;
		public static async Task<Fixture> Start() {
			var dir = new TempDir();
			var home = new Home(dir.File("home"));
			var root = Directory.CreateDirectory(dir.File("binding")).FullName;
			var project = Project.Create(home.Projects, "scratch", [root], null, false);
			var cfg = new Config { PairingOrigin = "https://paired.example", RemoteHosts = ["alias.example"] };
			var pairing = new DevicePairing(home);
			var token = pairing.Redeem(pairing.Create()!.Code, "primary")!;
			var other = pairing.Redeem(pairing.Create()!.Code, "other")!;
			var security = new Security(home, cfg) { Pairing = pairing, ClientAuth = t => t == "outside" ? t : null };
			var builder = WebApplication.CreateSlimBuilder();
			builder.Logging.ClearProviders();
			builder.WebHost.UseUrls("http://127.0.0.1:0");
			var app = builder.Build();
			app.UseRouting();
			app.Use(security.Middleware);
			new ProjectFiles(id => id == project.Info.Id ? project : null, () => cfg, home.Root, () => dir.File("backups"),
				app.Lifetime.ApplicationStopping).Map(app.MapGroup("/api"));
			await app.StartAsync();
			return new Fixture(dir, app, app.Urls.Single(), project, token, other, security.Token, cfg.ProjectFiles);
		}
		public string Grant() {
			var binding = project.Info.Bindings.Single();
			var physical = ProjectFileSystem.Physical(binding)!;
			using var fd = ProjectFileSystem.Root(physical);
			grants.Add(new ProjectFileGrant(project.Info.Id, binding, physical, ProjectFileSystem.Stat(fd).Identity, "Fixture"));
			return binding;
		}
		public async Task<HttpResponseMessage> Get(string tail, string? token = null, string? host = null) {
			using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/projects/{project.Info.Id}/files/{tail}");
			request.Headers.Host = host ?? "paired.example";
			var bearer = token ?? primary;
			if(bearer.Length > 0) request.Headers.Authorization = new("Bearer", bearer);
			return await http.SendAsync(request);
		}
		public async ValueTask DisposeAsync() { http.Dispose(); await app.DisposeAsync(); dir.Dispose(); }
	}
}
