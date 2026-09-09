using System.Text.Json;
using Picer.Core.Search;

namespace Picer.Core.Gateway;

public static class GatewayCursors
{
    private sealed record CursorPayload(string Kind, JsonElement Value, long ImageId);

    public static string Encode(SearchCursor cursor) =>
        JsonSerializer.Serialize(new CursorPayload(KindOf(cursor.SortValue), JsonSerializer.SerializeToElement(cursor.SortValue), cursor.ImageId));

    public static SearchCursor? Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        var payload = JsonSerializer.Deserialize<CursorPayload>(encoded);
        if (payload is null) throw new FormatException("Invalid gateway cursor.");
        return new SearchCursor(ReadValue(payload.Kind, payload.Value), payload.ImageId);
    }

    private static string KindOf(object value) => value switch
    {
        string => "string",
        long or int => "int64",
        double or float or decimal => "double",
        DateTime => "datetime",
        bool => "bool",
        _ => "string"
    };

    private static object ReadValue(string kind, JsonElement value) => kind switch
    {
        "int64" => value.GetInt64(),
        "double" => value.GetDouble(),
        "datetime" => value.GetDateTime(),
        "bool" => value.GetBoolean(),
        _ => value.GetString() ?? ""
    };
}
