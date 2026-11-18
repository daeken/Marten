using Marten.Storage;

namespace Marten.Agents;

public static class ImageFiles {
	public const long MaxBytes = 20 << 20;

	public static string? MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
		".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => null,
	};

	public static string ExtensionFor(string mime) => mime switch {
		"image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".bin",
	};
}

public sealed partial class Agent {
	readonly List<ImageRef> viewedImages = new();

	/// <summary>View-Image: show an image file to the model with the current tool result.</summary>
	public ImageRef ViewImage(string path) {
		var full = Path.GetFullPath(path, WorkingDirectory);
		if(!File.Exists(full)) throw new FileNotFoundException($"No such file: {full}");
		var mime = ImageFiles.MimeFor(full) ?? throw new ArgumentException("View-Image takes .png, .jpg, .gif or .webp files.");
		if(new FileInfo(full).Length > ImageFiles.MaxBytes) throw new ArgumentException("That image is over 20 MB.");
		// Copy it, so the log still shows what the model saw if the file changes.
		var saved = SaveImage(File.ReadAllBytes(full), mime);
		lock(viewedImages) viewedImages.Add(saved);
		return saved;
	}

	/// <summary>Keep an image an agent was shown with its other outputs.</summary>
	public ImageRef SaveImage(byte[] bytes, string mime) {
		var dir = Path.Combine(Directory, "images");
		System.IO.Directory.CreateDirectory(dir);
		var path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}{ImageFiles.ExtensionFor(mime)}");
		AtomicFile.Write(path, bytes);
		return new ImageRef(path, mime);
	}

	List<ImageRef> TakeViewedImages() {
		lock(viewedImages) {
			var taken = viewedImages.ToList();
			viewedImages.Clear();
			return taken;
		}
	}
}
