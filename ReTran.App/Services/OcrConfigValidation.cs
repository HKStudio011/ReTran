using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReTran_App.Core;

namespace ReTran_App.Services;

/// <summary>
/// Pure validation/parsing helpers for the schema-driven OCR parameter block in settings
/// (spec §8): checks a wire value against one <see cref="OcrParamSpec"/> and parses the two
/// free-text controls (<c>N,C,H,W</c> shape, JSON object). No UI or DI dependencies — the
/// Razor component only maps the returned error codes to i18n strings.
/// Trợ giúp kiểm tra/phân tích thuần cho khối tham số OCR theo schema trong cài đặt
/// (spec §8): kiểm tra một giá trị wire với một <see cref="OcrParamSpec"/> và phân tích hai ô
/// nhập chữ (shape <c>N,C,H,W</c>, object JSON). Không phụ thuộc UI/DI — component Razor chỉ
/// ánh xạ mã lỗi trả về sang chuỗi i18n.
/// </summary>
public static class OcrConfigValidation
{
    /// <summary>
    /// Validates one wire value for <paramref name="spec"/>. <see langword="null"/> is always
    /// valid — it means "use the schema default" and the UI omits the key. Error codes are
    /// stable strings the UI maps to i18n: <c>type</c> for a wrong JSON kind (bool, int, float,
    /// string, and enum values that are not strings), including a non-integral <c>int</c>;
    /// <c>range</c> when a number falls outside
    /// the inclusive <see cref="OcrParamSpec.Min"/>/<see cref="OcrParamSpec.Max"/> bounds;
    /// <c>enum</c> when a string is not in <see cref="OcrParamSpec.Values"/>; <c>shape</c> when
    /// the value is not an array of four positive integers; <c>json</c> when the value is not
    /// an object.
    /// Kiểm tra một giá trị wire cho <paramref name="spec"/>. <see langword="null"/> luôn hợp lệ
    /// — nghĩa là "dùng mặc định schema" và UI sẽ bỏ key. Mã lỗi là chuỗi ổn định để UI ánh xạ
    /// i18n: <c>type</c> khi sai kiểu JSON (bool, int, float, string, và enum không phải chuỗi),
    /// kể cả <c>int</c> không nguyên; <c>range</c> khi số nằm ngoài biên <see cref="OcrParamSpec.Min"/>/
    /// <see cref="OcrParamSpec.Max"/> (bao gồm hai biên); <c>enum</c> khi chuỗi không thuộc
    /// <see cref="OcrParamSpec.Values"/>; <c>shape</c> khi giá trị không phải mảng bốn số nguyên
    /// dương; <c>json</c> khi giá trị không phải object.
    /// </summary>
    /// <param name="spec">Schema entry the value must satisfy. Mục schema mà giá trị phải thỏa.</param>
    /// <param name="value">Wire value from the UI, or null for "use default". Giá trị wire từ UI, hoặc null nghĩa là "dùng mặc định".</param>
    /// <returns>Null when valid; otherwise the stable error code. Null khi hợp lệ; ngược lại là mã lỗi ổn định.</returns>
    public static string? Validate(OcrParamSpec spec, JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        switch (spec.Type)
        {
            case "bool":
                return TryGetBool(value, out _) ? null : "type";
            case "int":
                if (!TryGetNumber(value, out double intValue) || !IsIntegral(intValue))
                {
                    return "type";
                }
                return CheckRange(spec, intValue);
            case "float":
                // TryGetNumber chỉ nhận số hữu hạn (NaN/Infinity bị coi là sai kiểu).
                if (!TryGetNumber(value, out double floatValue))
                {
                    return "type";
                }
                return CheckRange(spec, floatValue);
            case "string":
                return value.GetValueKind() == JsonValueKind.String ? null : "type";
            case "enum":
                if (value.GetValueKind() != JsonValueKind.String)
                {
                    return "type";
                }
                return spec.Values.Contains(value.GetValue<string>()) ? null : "enum";
            case "json":
                return value.GetValueKind() == JsonValueKind.Object ? null : "json";
            case "shape":
                return IsShape(value) ? null : "shape";
            default:
                // Kiểu schema lạ: không biết luật kiểm tra nên coi là sai kiểu.
                return "type";
        }
    }

    /// <summary>
    /// Parses the <c>N,C,H,W</c> shape text control ("1,3,640,640"; spaces after commas are
    /// allowed) into a JSON array of four positive integers. Blank text is invalid as well
    /// (a blank shape field means "no override", so the UI skips the key).
    /// Phân tích ô nhập shape <c>N,C,H,W</c> ("1,3,640,640"; cho phép khoảng trắng sau dấu phẩy)
    /// thành mảng JSON bốn số nguyên dương. Chuỗi trống cũng không hợp lệ (ô shape trống nghĩa là
    /// "không ép", nên UI bỏ qua key).
    /// </summary>
    /// <param name="text">Shape text to parse. Chuỗi shape cần phân tích.</param>
    /// <param name="shape">Parsed array on success, otherwise null. Mảng đã phân tích khi thành công, ngược lại là null.</param>
    /// <param name="error">Null on success, otherwise <c>shape</c>. Null khi thành công, ngược lại là <c>shape</c>.</param>
    /// <returns>True when the text is a valid shape. True khi chuỗi là shape hợp lệ.</returns>
    public static bool TryParseShape(string text, out JsonArray? shape, out string? error)
    {
        shape = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "shape";
            return false;
        }

        string[] parts = text.Split(',');
        if (parts.Length != 4)
        {
            error = "shape";
            return false;
        }

        var array = new JsonArray();
        foreach (string part in parts)
        {
            if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dimension)
                || dimension <= 0)
            {
                error = "shape";
                return false;
            }
            array.Add(JsonValue.Create(dimension));
        }

        shape = array;
        return true;
    }

    /// <summary>
    /// Parses the JSON text control into a <see cref="JsonObject"/>. Blank text, malformed JSON
    /// and valid non-object JSON (array, number, <c>null</c>) are all invalid — the setting only
    /// accepts an object.
    /// Phân tích ô nhập JSON thành <see cref="JsonObject"/>. Chuỗi trống, JSON hỏng và JSON hợp
    /// lệ nhưng không phải object (mảng, số, <c>null</c>) đều không hợp lệ — cài đặt chỉ nhận
    /// object.
    /// </summary>
    /// <param name="text">JSON text to parse. Chuỗi JSON cần phân tích.</param>
    /// <param name="obj">Parsed object on success, otherwise null. Object đã phân tích khi thành công, ngược lại là null.</param>
    /// <param name="error">Null on success, otherwise <c>json</c>. Null khi thành công, ngược lại là <c>json</c>.</param>
    /// <returns>True when the text is a valid JSON object. True khi chuỗi là object JSON hợp lệ.</returns>
    public static bool TryParseJsonObject(string text, out JsonObject? obj, out string? error)
    {
        obj = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "json";
            return false;
        }

        try
        {
            obj = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            // JSON hỏng — rơi xuống nhánh báo lỗi bên dưới.
        }

        if (obj is null)
        {
            error = "json";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Formats the schema default of <paramref name="spec"/> for display, e.g. as the control
    /// placeholder: bool as <c>true</c>/<c>false</c>, int/float with invariant culture, string
    /// and enum as the plain text, shape as <c>N,C,H,W</c>, json as compact JSON.
    /// Định dạng giá trị mặc định schema của <paramref name="spec"/> để hiển thị, ví dụ làm
    /// placeholder cho control: bool thành <c>true</c>/<c>false</c>, int/float theo invariant
    /// culture, string và enum thành chuỗi trần, shape thành <c>N,C,H,W</c>, json thành JSON
    /// compact.
    /// </summary>
    /// <param name="spec">Schema entry whose default to format. Mục schema cần định dạng mặc định.</param>
    /// <returns>
    /// The formatted default, or <see langword="null"/> when the schema has no default so the UI
    /// falls back to <see cref="OcrParamSpec.Placeholder"/>.
    /// Chuỗi mặc định đã định dạng, hoặc <see langword="null"/> khi schema không có mặc định để
    /// UI dùng <see cref="OcrParamSpec.Placeholder"/> thay thế.
    /// </returns>
    public static string? FormatDefault(OcrParamSpec spec)
    {
        JsonNode? value = spec.Default;
        if (value is null)
        {
            return null;
        }

        return spec.Type switch
        {
            "bool" => TryGetBool(value, out bool flag) ? (flag ? "true" : "false") : null,
            "int" or "float" => TryGetNumber(value, out double number) ? FormatNumber(number) : null,
            "string" or "enum" => value is JsonValue json && json.TryGetValue(out string? text) ? text : null,
            "shape" => value is JsonArray array ? string.Join(",", array.Select(FormatShapeElement)) : null,
            "json" => value.ToJsonString(),
            _ => null,
        };
    }

    /// <summary>
    /// Reads a JSON number as <see cref="double"/> whatever the CLR backing of the node
    /// (<c>int</c>/<c>long</c>/<c>float</c>/<c>decimal</c> or a parsed <c>JsonElement</c>).
    /// Non-number kinds and non-finite values (NaN/Infinity) are rejected.
    /// Đọc một số JSON dưới dạng <see cref="double"/> bất kể backing CLR của node
    /// (<c>int</c>/<c>long</c>/<c>float</c>/<c>decimal</c> hay <c>JsonElement</c> đã parse).
    /// Kiểu không phải số và giá trị không hữu hạn (NaN/Infinity) bị từ chối.
    /// </summary>
    private static bool TryGetNumber(JsonNode? node, out double number)
    {
        number = 0;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        if (value.TryGetValue(out double direct))
        {
            if (!double.IsFinite(direct))
            {
                return false;
            }
            number = direct;
            return true;
        }

        // Float là backing số duy nhất còn có thể không hữu hạn; kiểm trước khi ghi literal
        // (Utf8JsonWriter ném lỗi khi ghi NaN/Infinity).
        if (value.TryGetValue(out float single))
        {
            if (!float.IsFinite(single))
            {
                return false;
            }
            number = single;
            return true;
        }

        // Các backing số còn lại (int, long, decimal, ...) luôn ghi ra literal JSON đọc được
        // bằng double.TryParse bất biến.
        return double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);
    }

    /// <summary>Whether a JSON boolean node holds <see langword="true"/>. Node boolean JSON có giữ <see langword="true"/> hay không.</summary>
    private static bool TryGetBool(JsonNode? node, out bool flag)
    {
        flag = false;
        return node is JsonValue value
            && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            && value.TryGetValue(out flag);
    }

    /// <summary>Whether a finite number has no fractional part. Số hữu hạn có phần thập phân bằng không hay không.</summary>
    private static bool IsIntegral(double number) => number == Math.Floor(number);

    /// <summary>Inclusive min/max check for a numeric value; null when in bounds or unbounded. Kiểm tra biên min/max bao gồm hai đầu; null khi trong biên hoặc không ràng buộc.</summary>
    private static string? CheckRange(OcrParamSpec spec, double number)
    {
        if (spec.Min is double min && number < min)
        {
            return "range";
        }
        if (spec.Max is double max && number > max)
        {
            return "range";
        }
        return null;
    }

    /// <summary>Whether a node is a JSON array of exactly four positive integers. Node có phải mảng JSON đúng bốn số nguyên dương hay không.</summary>
    private static bool IsShape(JsonNode value)
    {
        if (value is not JsonArray array || array.Count != 4)
        {
            return false;
        }

        foreach (JsonNode? element in array)
        {
            if (element is null
                || !TryGetNumber(element, out double number)
                || !IsIntegral(number)
                || number <= 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Plain-number display: integral values keep no ".0" tail ("32", "640"), fractions use invariant culture ("0.9"). Hiển thị số cho placeholder: giá trị nguyên không có đuôi ".0", phần thập phân dùng invariant culture.</summary>
    private static string FormatNumber(double number)
    {
        // Chỉ cast sang long khi chắc chắn nằm trong phạm vi biểu diễn.
        if (number == Math.Floor(number) && number >= long.MinValue && number < (double)long.MaxValue)
        {
            return ((long)number).ToString(CultureInfo.InvariantCulture);
        }
        return number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Formats one shape array element as a plain number; non-numeric elements fall back to raw JSON. Định dạng một phần tử mảng shape thành số trần; phần tử không phải số rơi về JSON thô.</summary>
    private static string FormatShapeElement(JsonNode? element) =>
        element is not null && TryGetNumber(element, out double number)
            ? FormatNumber(number)
            : element?.ToJsonString() ?? string.Empty;
}
