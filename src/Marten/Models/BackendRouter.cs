namespace Marten.Models;

/// <summary>
/// Sends each request to the backend that owns its model: an explicit "provider/model" prefix,
/// or a known name prefix (claude-, glm-, deepseek-). Everything else goes to the primary backend.
/// </summary>
public sealed class BackendRouter(IModelBackend primary, IReadOnlyList<(IModelBackend Backend, IReadOnlyList<string> Prefixes)> others) : IModelBackend {
	public string Provider => "router";
	public bool Connected => primary.Connected || others.Any(o => o.Backend.Connected);
	public IEnumerable<IModelBackend> Backends => others.Select(o => o.Backend).Prepend(primary);

	public (IModelBackend Backend, string Model) Resolve(string model) {
		var slash = model.IndexOf('/');
		if(slash > 0) {
			var name = model[..slash];
			if(Backends.FirstOrDefault(b => b.Provider == name) is { } named) return (named, model[(slash + 1)..]);
		}
		foreach(var (backend, prefixes) in others)
			if(prefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return (backend, model);
		return (primary, model);
	}

	public Task<ModelResult> CompleteAsync(ModelRequest req, Action<string> onTextDelta, CancellationToken ct) {
		var (backend, model) = Resolve(req.Model);
		return backend.CompleteAsync(req with { Model = model }, onTextDelta, ct);
	}

	/// <summary>Models from every connected backend, primary first. Fails only if none of them can list.</summary>
	public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) {
		var models = new List<string>();
		Exception? first = null;
		foreach(var backend in Backends.Where(b => b.Connected)) {
			try {
				models.AddRange(await backend.ListModelsAsync(ct));
			} catch(Exception e) when(e is not OperationCanceledException) {
				first ??= e;
			}
		}
		if(models.Count == 0 && first != null) throw first;
		if(models.Count == 0 && !Connected) throw new NotConnectedException("No model backend is connected.");
		return models.Distinct().ToList();
	}
}
