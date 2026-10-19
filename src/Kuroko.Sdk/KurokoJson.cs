using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Kuroko;

// Kuroko の JSON（manifest/disc/title…）の直列化・復元。camelCase・整形・null省略。
// バイト列⇔モデルのみを扱い、容器（ZIP）への読み書きは KurokoContainerReader・KurokoZipWriter が行う。
//
// 型の情報は**ソース生成**（KurokoJsonContext）から取る。NativeAOT（macOS のファサード）では反射の JSON が
// 無効で、JsonSerializer.Deserialize<T>(…, options) は「Reflection-based serialization has been disabled」で
// 失敗していた（Mac 側で発見）。**読み書きする型はすべてコンテキストに載せる**（反射の補いは持たない：
// 持つと AOT の解析が警告を出す）。新しい型を KurokoJson へ通すときは、下の [JsonSerializable] に足すこと
//（足し忘れは Options.GetTypeInfo の例外で分かる）。
public static class KurokoJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = KurokoJsonContext.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 既定エンコーダは & < > や非ASCII（日本語・ニーモニックの &）を \uXXXX へ数値参照化する。
        // JSON 文字列として素の文字で出す（ニーモニックの "GPRM0 & 0x..." 等を読める形に）。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new KurokoVmCommandJsonConverter() },
    };

    // 型の情報は Options の TypeInfoResolver から引く（反射の汎用オーバーロードを通さない）。
    private static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    public static byte[] Serialize<T>(T value)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Info<T>()));

    public static T? Deserialize<T>(byte[] utf8)
        => JsonSerializer.Deserialize(utf8, Info<T>());

    public static T? Deserialize<T>(string json)
        => JsonSerializer.Deserialize(json, Info<T>());
}

// KurokoJson が読み書きする型（再生＝KurokoTitleSource・KurokoInfoAnalyzer が読む型と、変換器が書く型）。
// 命名・null 省略は Options と同じにしておく（実行時は Options の設定が使われる）。
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = new[] { typeof(KurokoVmCommandJsonConverter) })]
[JsonSerializable(typeof(KurokoManifest))]
[JsonSerializable(typeof(KurokoDisc))]
[JsonSerializable(typeof(KurokoVmDisc))]
[JsonSerializable(typeof(KurokoTitle))]
[JsonSerializable(typeof(KurokoVmTitle))]
[JsonSerializable(typeof(KurokoNoneTitle))]
[JsonSerializable(typeof(KurokoSegment))]
[JsonSerializable(typeof(KurokoNoneSegment))]
[JsonSerializable(typeof(KurokoSubsImage))]
[JsonSerializable(typeof(KurokoSubsText))]
[JsonSerializable(typeof(KurokoButtons))]
[JsonSerializable(typeof(KurokoMenuPalette))]
[JsonSerializable(typeof(KurokoMenuV3))]
[JsonSerializable(typeof(KurokoNoneDisc))]
[JsonSerializable(typeof(KurokoLoudnessFile))]
[JsonSerializable(typeof(KurokoTinyNav))]
[JsonSerializable(typeof(KurokoTinyMenu))]
public sealed partial class KurokoJsonContext : JsonSerializerContext
{
}

// §20「VM 命令の JSON 表現」：VM 命令は { "hex", "asm" } オブジェクトで書き出す。
// 読み出しは後方互換で「文字列(hex のみ)」も受ける（旧 menu.json は commands を hex 文字列配列で持っていた）。
public sealed class KurokoVmCommandJsonConverter : JsonConverter<KurokoVmCommand>
{
    public override KurokoVmCommand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new KurokoVmCommand { Hex = reader.GetString() ?? "", Asm = "" };
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            string hex = "", asm = "";
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                string prop = reader.GetString() ?? "";
                reader.Read();
                if (string.Equals(prop, "hex", StringComparison.OrdinalIgnoreCase)) hex = reader.GetString() ?? "";
                else if (string.Equals(prop, "asm", StringComparison.OrdinalIgnoreCase)) asm = reader.GetString() ?? "";
                else reader.Skip();
            }
            return new KurokoVmCommand { Hex = hex, Asm = asm };
        }
        throw new JsonException("KurokoVmCommand は文字列(hex) かオブジェクト { hex, asm } で表す");
    }

    public override void Write(Utf8JsonWriter writer, KurokoVmCommand value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("hex", value.Hex);
        writer.WriteString("asm", value.Asm);
        writer.WriteEndObject();
    }
}

// パレット（24bit の Y・Cr・Cb）の並び。レイアウト version 5 から**6 桁の 16 進の文字列**で書く（title.json と
// menu.json でそろえる）。読み出しは旧 menu.json の数値の並びも受ける。内部は uint のまま。
public sealed class KurokoHexPaletteListConverter : JsonConverter<List<uint>>
{
    public override List<uint> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = new List<uint>();
        if (reader.TokenType == JsonTokenType.Null) return list;
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("palette は配列で表す");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Number) list.Add(reader.GetUInt32());
            else if (reader.TokenType == JsonTokenType.String) list.Add(KurokoPalette.Parse(reader.GetString()));
            else reader.Skip();
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<uint> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var v in value) writer.WriteStringValue(KurokoPalette.Format(v));
        writer.WriteEndArray();
    }
}

// メニューの種類（DVD-Video の値 2〜7）。レイアウト version 5 から disc.json と同じ**名前の文字列**で書く
//（"Title"／"Root"／"Subpicture"／"Audio"／"Angle"／"Chapter"。それ以外は空文字列）。読み出しは旧 menu.json の数値も受ける。
public sealed class KurokoMenuTypeConverter : JsonConverter<int>
{
    private static readonly string[] Names = { "", "", "Title", "Root", "Subpicture", "Audio", "Angle", "Chapter" };

    public static string NameOf(int type) => type >= 0 && type < Names.Length ? Names[type] : "";

    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return reader.GetInt32();
        if (reader.TokenType == JsonTokenType.String)
        {
            string s = reader.GetString() ?? "";
            for (int i = 2; i < Names.Length; i++)
                if (string.Equals(Names[i], s, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }
        reader.Skip();
        return 0;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteStringValue(NameOf(value));
}

// 時刻文字列（TimeSpan の "c" 形式＝完全往復）の変換。JSON に人が読める形で持たせる。
public static class KurokoTime
{
    public static string Format(TimeSpan t) => t.ToString("c", CultureInfo.InvariantCulture);

    public static TimeSpan Parse(string? s)
        => TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var t) ? t : TimeSpan.Zero;
}

// パレット（24bit YUV）の hex 文字列⇔数値変換。
public static class KurokoPalette
{
    public static string Format(uint yuv) => yuv.ToString("X6", CultureInfo.InvariantCulture);

    public static uint Parse(string? hex)
        => uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0u;

    public static List<string> Format(IReadOnlyList<uint> palette)
    {
        var list = new List<string>(palette.Count);
        foreach (var c in palette) list.Add(Format(c));
        return list;
    }

    public static uint[] Parse(IReadOnlyList<string>? palette)
    {
        if (palette is null) return System.Array.Empty<uint>();
        var arr = new uint[palette.Count];
        for (int i = 0; i < palette.Count; i++) arr[i] = Parse(palette[i]);
        return arr;
    }
}
