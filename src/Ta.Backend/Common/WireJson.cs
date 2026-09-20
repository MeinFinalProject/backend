using System.Text.Json;
using System.Text;

namespace Ta.Backend.Common;

public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false
    };

    public static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value)
        && Encoding.UTF8.GetByteCount(value) <= 256 && !value.Any(c => c is '\r' or '\n' or '\0');
}
