using System.Buffers.Binary;
using System.Text.Json;

namespace AlienFxLite.Contracts;

public static class PipeProtocol
{
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, ServiceJson.Options);
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = await ReadExactlyAsync(stream, sizeof(int), cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > 1024 * 1024)
        {
            throw new InvalidOperationException($"Invalid pipe payload length: {length}.");
        }

        byte[] payload = await ReadExactlyAsync(stream, length, cancellationToken).ConfigureAwait(false);
        T? result = JsonSerializer.Deserialize<T>(payload, ServiceJson.Options);
        if (result is null)
        {
            throw new InvalidOperationException($"Unable to deserialize pipe message into {typeof(T).Name}.");
        }

        return result;
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Pipe closed before the full payload was read.");
            }

            offset += read;
        }

        return buffer;
    }
}
