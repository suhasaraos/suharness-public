using Suharness.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;

namespace Suharness.Infrastructure;

public sealed class ImageAttachmentStore(string root) : IImageAttachmentStore
{
	public const int MaxImageBytes = 10 * 1024 * 1024;
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly string _root = Path.GetFullPath(root);

	public async Task<ImageAttachmentRef> SaveAsync(
		string name,
		string mediaType,
		ReadOnlyMemory<byte> bytes,
		CancellationToken cancellationToken = default)
	{
		if (bytes.Length == 0) throw new InvalidDataException("The selected image is empty.");
		if (bytes.Length > MaxImageBytes) throw new InvalidDataException($"Images may not exceed {MaxImageBytes / 1024 / 1024} MB.");
		var detected = DetectMediaType(bytes.Span)
			?? throw new InvalidDataException("Only PNG, JPEG, WebP, and GIF images are supported.");
		if (!string.IsNullOrWhiteSpace(mediaType) && !detected.Equals(mediaType, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException($"The image bytes do not match the declared media type '{mediaType}'.");

		var id = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
		var safeName = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? $"image.{Extension(detected)}" : name.Trim());
		var reference = new ImageAttachmentRef(id, safeName, detected, bytes.Length);
		Directory.CreateDirectory(_root);
		var dataPath = DataPath(id);
		if (!File.Exists(dataPath))
		{
			var temporary = dataPath + $".{Guid.NewGuid():N}.tmp";
			await File.WriteAllBytesAsync(temporary, bytes.ToArray(), cancellationToken);
			try { File.Move(temporary, dataPath, overwrite: false); }
			catch (IOException) when (File.Exists(dataPath)) { File.Delete(temporary); }
		}
		var metadataPath = MetadataPath(id);
		if (!File.Exists(metadataPath))
		{
			var temporary = metadataPath + $".{Guid.NewGuid():N}.tmp";
			await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(reference, JsonOptions), cancellationToken);
			try { File.Move(temporary, metadataPath, overwrite: false); }
			catch (IOException) when (File.Exists(metadataPath)) { File.Delete(temporary); }
		}
		return reference;
	}

	public ImageAttachmentData Read(ImageAttachmentRef reference)
	{
		ValidateId(reference.Id);
		var bytes = File.ReadAllBytes(DataPath(reference.Id));
		if (bytes.LongLength != reference.Length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != reference.Id)
			throw new InvalidDataException($"Image attachment '{reference.Id}' failed integrity verification.");
		var mediaType = DetectMediaType(bytes);
		if (mediaType is null || !mediaType.Equals(reference.MediaType, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException($"Image attachment '{reference.Id}' has invalid content.");
		return new ImageAttachmentData(reference, bytes);
	}

	public ImageAttachmentData? Find(string id)
	{
		try
		{
			ValidateId(id);
			var metadataPath = MetadataPath(id);
			if (!File.Exists(metadataPath) || !File.Exists(DataPath(id))) return null;
			var reference = JsonSerializer.Deserialize<ImageAttachmentRef>(File.ReadAllText(metadataPath), JsonOptions);
			return reference is null ? null : Read(reference);
		}
		catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static string? DetectMediaType(ReadOnlySpan<byte> bytes)
	{
		if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
		if (bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
		if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
		if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
		return null;
	}

	private static string Extension(string mediaType) => mediaType switch
	{
		"image/png" => "png",
		"image/jpeg" => "jpg",
		"image/gif" => "gif",
		_ => "webp",
	};

	private static void ValidateId(string id)
	{
		if (id.Length != 64 || id.Any(character => !char.IsAsciiHexDigit(character)))
			throw new InvalidDataException("Invalid image attachment id.");
	}

	private string DataPath(string id) => Path.Combine(_root, id.ToLowerInvariant() + ".bin");
	private string MetadataPath(string id) => Path.Combine(_root, id.ToLowerInvariant() + ".json");
}
