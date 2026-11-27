using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Marten.Storage;

namespace Marten.Web;

/// <summary>Fans runtime events out to every connected UI over WebSockets.</summary>
public sealed class EventHub {
	readonly ConcurrentDictionary<Guid, Channel<string>> clients = new();

	public void Broadcast(object ev) {
		var json = JsonIO.Serialize(ev);
		foreach(var client in clients.Values) client.Writer.TryWrite(json);
	}

	public async Task Serve(WebSocket socket, CancellationToken requestAborted, CancellationToken stopping, CancellationToken deviceRevoked = default) {
		using var session = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, stopping, deviceRevoked);
		var ct = session.Token;
		var id = Guid.NewGuid();
		// A slow client drops its oldest events rather than stalling agents; it resyncs on reconnect.
		var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });
		clients[id] = channel;
		var reader = Read();
		var writer = Write();
		try {
			await Task.WhenAny(reader, writer);
			// Either loop can end first. Abort instead of waiting on a peer's close handshake.
			socket.Abort();
			await session.CancelAsync();
			await Task.WhenAll(reader, writer);
		} catch(Exception e) when(e is OperationCanceledException or WebSocketException) {
		} finally {
			clients.TryRemove(id, out _);
		}

		async Task Read() {
			var buffer = new byte[1024];
			while(!ct.IsCancellationRequested && socket.State == WebSocketState.Open) {
				var r = await socket.ReceiveAsync(buffer, ct);
				if(r.MessageType == WebSocketMessageType.Close) break;
			}
		}

		async Task Write() {
			await foreach(var json in channel.Reader.ReadAllAsync(ct)) {
				if(socket.State != WebSocketState.Open) break;
				await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
			}
		}
	}
}
