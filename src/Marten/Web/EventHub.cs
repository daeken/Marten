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

	public async Task Serve(WebSocket socket, CancellationToken ct) {
		var id = Guid.NewGuid();
		// A slow client drops its oldest events rather than stalling agents; it resyncs on reconnect.
		var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });
		clients[id] = channel;
		using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var reader = ReadUntilClosed();
		try {
			await foreach(var json in channel.Reader.ReadAllAsync(lifetime.Token)) {
				if(socket.State != WebSocketState.Open) break;
				await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, lifetime.Token);
			}
		} catch(Exception e) when(e is OperationCanceledException or WebSocketException) {
		} finally {
			clients.TryRemove(id, out _);
			lifetime.Cancel();
			await reader;
		}

		async Task ReadUntilClosed() {
			try {
				var buffer = new byte[1024];
				while(socket.State == WebSocketState.Open) {
					var r = await socket.ReceiveAsync(buffer, lifetime.Token);
					if(r.MessageType == WebSocketMessageType.Close) break;
				}
			} catch(Exception e) when(e is OperationCanceledException or WebSocketException) {
			} finally {
				lifetime.Cancel();
			}
		}
	}
}
