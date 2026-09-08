using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenGepa.McpProtocol;

public sealed class IpcRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public JsonElement Payload { get; set; }
}

public sealed class IpcResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public JsonElement? Data { get; set; }

    public static IpcResponse Ok(object value) => new() { Success = true, Data = JsonSerializer.SerializeToElement(value, Wire.Options) };
    public static IpcResponse Fail(string error, string message) => new() { Error = error, Message = message };
}

public static class Wire
{
    public const int MaximumMessageBytes = 1024 * 1024;
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (payload.Length > MaximumMessageBytes) throw new InvalidDataException("IPCメッセージが上限を超えています。");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await TryReadExactlyAsync(stream, header, cancellationToken)) return default;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumMessageBytes) throw new InvalidDataException("IPCメッセージ長が不正です。");
        var payload = new byte[length];
        if (!await TryReadExactlyAsync(stream, payload, cancellationToken)) throw new EndOfStreamException("IPCメッセージが途中で切断されました。");
        return JsonSerializer.Deserialize<T>(payload, Options);
    }

    private static async Task<bool> TryReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
