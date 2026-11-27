using System.Buffers.Binary;
using System.IO.Compression;

namespace Marten.Models;

/// <summary>A bounded, privately owned static PNG snapshot. No filenames or network fetches.</summary>
internal sealed class JevPreviewImage {
	internal const int MaxBytes = 1_048_576;
	internal const int MinDimension = 280;
	internal const int MaxDimension = 1_024;
	static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];
	static readonly uint[] CrcTable = BuildCrcTable();
	readonly byte[] bytes;

	JevPreviewImage(byte[] bytes) => this.bytes = bytes;

	/// <summary>Stage an already normalized, non-interlaced 8-bit RGB/RGBA PNG. Metadata and animated PNG are refused.</summary>
	public static JevPreviewImage Stage(ReadOnlySpan<byte> png, bool allowPreview) {
		if(!allowPreview || png.Length > MaxBytes || png.Length < Signature.Length) throw JevRequest.Invalid();
		ReadOnlySpan<byte> snapshot = png.ToArray();
		if(!snapshot.StartsWith(Signature)) throw JevRequest.Invalid();
		try { return new(Normalize(snapshot[Signature.Length..])); }
		catch(InvalidDataException) { throw JevRequest.Invalid(); }
	}

	/// <summary>Copy exactly the normalized PNG bytes later sent, for a broker-owned preview.</summary>
	public byte[] CopyPngBytes() => (byte[])bytes.Clone();

	internal HttpContent CreateContent() {
		var content = new ByteArrayContent(bytes);
		content.Headers.ContentType = new("image/png");
		return content;
	}

	static byte[] Normalize(ReadOnlySpan<byte> png) {
		using var compressed = new MemoryStream();
		var header = ReadChunk(ref png, out var kind);
		if(!kind.SequenceEqual("IHDR"u8) || header.Length != 13) throw JevRequest.Invalid();
		int width = BinaryPrimitives.ReadInt32BigEndian(header);
		int height = BinaryPrimitives.ReadInt32BigEndian(header[4..]);
		if(width is < MinDimension or > MaxDimension || height is < MinDimension or > MaxDimension ||
			header[8] != 8 || header[9] is not (2 or 6) || header[10] != 0 || header[11] != 0 || header[12] != 0) throw JevRequest.Invalid();
		while(true) {
			var data = ReadChunk(ref png, out kind);
			if(kind.SequenceEqual("IDAT"u8)) compressed.Write(data);
			else {
				// No ancillary chunks: in particular no APNG control chunks, EXIF, profiles or textual metadata.
				if(!kind.SequenceEqual("IEND"u8) || !data.IsEmpty || !png.IsEmpty || compressed.Length == 0) throw JevRequest.Invalid();
				break;
			}
		}
		compressed.Position = 0;
		using var encoded = new MemoryStream();
		var row = new byte[1 + width * (header[9] == 2 ? 3 : 4)];
		// Re-encode only the expected scanlines, never any trailing compressed data or source metadata.
		using(var decoder = new ZLibStream(compressed, CompressionMode.Decompress))
		using(var encoder = new ZLibStream(encoded, CompressionLevel.Fastest, leaveOpen: true)) {
			for(int y = 0; y < height; y++) {
				if(decoder.ReadAtLeast(row, row.Length, false) != row.Length || row[0] > 4) throw JevRequest.Invalid();
				encoder.Write(row);
				if(encoded.Length > MaxBytes) throw JevRequest.Invalid();
			}
			if(decoder.ReadByte() != -1) throw JevRequest.Invalid();
		}
		if(encoded.Length > MaxBytes - Signature.Length - 3 * 12 - header.Length) throw JevRequest.Invalid();
		using var normalized = new MemoryStream();
		normalized.Write(Signature);
		WriteChunk(normalized, "IHDR"u8, header);
		WriteChunk(normalized, "IDAT"u8, encoded.GetBuffer().AsSpan(0, (int)encoded.Length));
		WriteChunk(normalized, "IEND"u8, []);
		return normalized.ToArray();
	}

	static ReadOnlySpan<byte> ReadChunk(ref ReadOnlySpan<byte> png, out ReadOnlySpan<byte> kind) {
		if(png.Length < 12) throw JevRequest.Invalid();
		int n = BinaryPrimitives.ReadInt32BigEndian(png);
		if(n < 0 || n > png.Length - 12) throw JevRequest.Invalid();
		kind = png.Slice(4, 4);
		var data = png.Slice(8, n);
		if(Crc(kind, data) != BinaryPrimitives.ReadUInt32BigEndian(png[(8 + n)..])) throw JevRequest.Invalid();
		png = png[(n + 12)..];
		return data;
	}

	static void WriteChunk(Stream target, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> data) {
		Span<byte> number = stackalloc byte[4];
		BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
		target.Write(number);
		target.Write(kind);
		target.Write(data);
		BinaryPrimitives.WriteUInt32BigEndian(number, Crc(kind, data));
		target.Write(number);
	}

	static uint Crc(ReadOnlySpan<byte> kind, ReadOnlySpan<byte> data) {
		uint crc = uint.MaxValue;
		foreach(byte b in kind) crc = CrcTable[(byte)(crc ^ b)] ^ (crc >> 8);
		foreach(byte b in data) crc = CrcTable[(byte)(crc ^ b)] ^ (crc >> 8);
		return ~crc;
	}

	static uint[] BuildCrcTable() {
		var table = new uint[256];
		for(uint i = 0; i < table.Length; i++) {
			uint c = i;
			for(int j = 0; j < 8; j++) c = (c & 1) == 0 ? c >> 1 : (c >> 1) ^ 0xedb88320;
			table[i] = c;
		}
		return table;
	}
}
