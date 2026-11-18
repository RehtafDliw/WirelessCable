using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WirelessCable.Protocol.Json;

/// <summary>
/// 双端唯一的 JSON 约定：
/// <list type="bullet">
/// <item>属性名 camelCase；</item>
/// <item>枚举序列化为小写字符串；</item>
/// <item>null 字段不输出（客户端反序列化一律给默认值）；</item>
/// <item>UTF-8，非 ASCII 字符不转义；</item>
/// <item>时间统一用 Unix 毫秒整数。</item>
/// </list>
/// Android 端 WcJson.kt 必须与此一致，改动需同步 docs/protocol.md。
/// </summary>
public static class WcJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T DeserializeRequired<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options)
           ?? throw new JsonException($"JSON 反序列化为 {typeof(T).Name} 得到 null。");
}
