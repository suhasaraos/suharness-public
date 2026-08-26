namespace Suharness.Abstractions;

public sealed record ImageAttachmentRef(string Id, string Name, string MediaType, long Length);

public sealed record ImageAttachmentData(ImageAttachmentRef Reference, ReadOnlyMemory<byte> Bytes);

public interface IImageAttachmentStore
{
	Task<ImageAttachmentRef> SaveAsync(
		string name,
		string mediaType,
		ReadOnlyMemory<byte> bytes,
		CancellationToken cancellationToken = default);

	ImageAttachmentData Read(ImageAttachmentRef reference);

	ImageAttachmentData? Find(string id);
}
