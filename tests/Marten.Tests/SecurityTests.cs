using Marten.Storage;
using Marten.Web;
using Microsoft.AspNetCore.Http;

namespace Marten.Tests;

public class SecurityTests {
	static HttpContext Request(string host, string? origin = null) {
		var ctx = new DefaultHttpContext();
		ctx.Request.Host = new HostString(host);
		if(origin != null) ctx.Request.Headers.Origin = origin;
		return ctx;
	}

	[Fact]
	public void RemoteHostsAdmitTheirProxyNameOnly() {
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var plain = new Security(home, new Config());
		Assert.True(plain.HostAllowed(Request("127.0.0.1:7450")));
		Assert.False(plain.HostAllowed(Request("mac.tail.ts.net")));

		var remote = new Security(home, new Config { RemoteHosts = ["https://mac.tail.ts.net/"] });
		Assert.True(remote.HostAllowed(Request("mac.tail.ts.net")));
		Assert.True(remote.OriginAllowed(Request("mac.tail.ts.net", "https://mac.tail.ts.net")));
		Assert.False(remote.OriginAllowed(Request("mac.tail.ts.net", "https://evil.example")));
		Assert.False(remote.HostAllowed(Request("evil.example")));
	}
}
