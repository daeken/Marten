using System.Runtime.CompilerServices;

namespace Marten.Models;

internal enum ModelTransportPhase { AwaitingHeaders, ReadingBody }

/// <summary>Annotates the original exception out of band, without changing its type, data, or stack.</summary>
internal static class ModelTransportFailure {
	sealed record Marker(ModelTransportPhase Phase);
	static readonly ConditionalWeakTable<Exception, Marker> phases = new();

	internal static Exception Mark(Exception error, ModelTransportPhase phase) {
		phases.GetValue(error, _ => new Marker(phase));
		return error;
	}

	internal static ModelTransportPhase? Phase(Exception error) =>
		phases.TryGetValue(error, out var marker) ? marker.Phase : null;
}
