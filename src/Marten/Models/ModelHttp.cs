using System.Net;

namespace Marten.Models;

/// <summary>Only model API traffic uses this transport, not OAuth or any other HTTP calls.</summary>
internal static class ModelHttp {
	internal const int DefaultIdleTimeoutSeconds = 300;
	internal static readonly HttpClient DefaultClient = CreateClient();

	internal static HttpClient CreateClient(Func<int>? idleTimeoutSeconds = null) => CreateClient(
		new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) },
		TimeSpan.FromSeconds(120),
		() => {
			var seconds = idleTimeoutSeconds?.Invoke() ?? DefaultIdleTimeoutSeconds;
			// CancelAfter accepts at most uint.MaxValue - 1 milliseconds.
			return TimeSpan.FromSeconds(seconds > 0 ? Math.Min(seconds, 4_294_967) : DefaultIdleTimeoutSeconds);
		});

	internal static HttpClient CreateClient(HttpMessageHandler transport, TimeSpan headersTimeout, Func<TimeSpan> idleTimeout) =>
		new(new DeadlineHandler(transport, headersTimeout, idleTimeout)) { Timeout = Timeout.InfiniteTimeSpan };

	sealed class DeadlineHandler(HttpMessageHandler transport, TimeSpan headersTimeout, Func<TimeSpan> idleTimeout) : DelegatingHandler(transport) {
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
			var idle = idleTimeout();
			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
			deadline.CancelAfter(headersTimeout);
			HttpResponseMessage response;
			try {
				response = await base.SendAsync(request, deadline.Token);
			} catch(OperationCanceledException e) {
				ct.ThrowIfCancellationRequested();
				// SocketsHttpHandler also reports its own connect deadline as cancellation.
				throw new TransientModelException(deadline.IsCancellationRequested
					? "Timed out waiting for model response headers."
					: "Timed out connecting to the model provider.", e);
			}
			response.Content = new IdleContent(response.Content, idle);
			return response;
		}
	}

	// The SDK controls its own SSE parsing and also reads JSON bodies. Wrap content at the
	// transport boundary so every async read gets the same policy, before any line/event buffering.
	sealed class IdleContent : HttpContent {
		readonly HttpContent inner;
		readonly TimeSpan idle;

		internal IdleContent(HttpContent inner, TimeSpan idle) {
			this.inner = inner;
			this.idle = idle;
			foreach(var header in inner.Headers.NonValidated) Headers.TryAddWithoutValidation(header.Key, header.Value);
		}

		protected override bool TryComputeLength(out long length) {
			length = inner.Headers.ContentLength.GetValueOrDefault(-1);
			return length >= 0;
		}

		protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);
		protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) =>
			new IdleStream(await inner.ReadAsStreamAsync(ct), idle);

		protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
			SerializeToStreamAsync(stream, context, CancellationToken.None);
		protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) {
			using var source = await CreateContentReadStreamAsync(ct);
			await source.CopyToAsync(stream, ct);
		}

		protected override void Dispose(bool disposing) {
			if(disposing) inner.Dispose();
			base.Dispose(disposing);
		}
	}

	sealed class IdleStream(Stream inner, TimeSpan idle) : Stream {
		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
			deadline.CancelAfter(idle);
			try {
				return await inner.ReadAsync(buffer, deadline.Token);
			} catch(OperationCanceledException e) when(deadline.IsCancellationRequested) {
				ct.ThrowIfCancellationRequested();
				throw new TransientModelException("Timed out waiting for bytes from the model provider.", e);
			}
		}

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
			ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
		public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
		public override bool CanRead => inner.CanRead;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		protected override void Dispose(bool disposing) {
			if(disposing) inner.Dispose();
			base.Dispose(disposing);
		}
	}
}
