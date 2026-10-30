using System.Globalization;
using System.Text.Json.Nodes;
using ReTran_App.Core;
using ReTran_App.Services;
using Xunit;

public class OcrConfigValidationTests
{
    private static OcrParamSpec Spec(string type, double? min = null, double? max = null,
        string[]? values = null) =>
        new("k", "det", type, null, false, values ?? Array.Empty<string>(), min, max, null, null);

    [Theory]
    [InlineData("float", 0.0, 1.0, 0.9, null)]
    [InlineData("float", 0.0, 1.0, 1.5, "range")]
    [InlineData("int", 1, 64, 0, "range")]
    [InlineData("int", 1, 64, 1.5, "type")]
    public void Validate_Numbers(string type, double min, double max, double value, string? expected)
    {
        Assert.Equal(expected, OcrConfigValidation.Validate(Spec(type, min, max), JsonValue.Create(value)));
    }

    [Fact]
    public void Validate_BoolRejectsNumbers()
        => Assert.Equal("type", OcrConfigValidation.Validate(Spec("bool"), JsonValue.Create(1)));

    [Fact]
    public void Validate_EnumUsesValues()
    {
        var spec = Spec("enum", values: ["min", "max"]);
        Assert.Null(OcrConfigValidation.Validate(spec, JsonValue.Create("max")));
        Assert.Equal("enum", OcrConfigValidation.Validate(spec, JsonValue.Create("mid")));
    }

    [Fact]
    public void ParseShape_RejectsWrongArity()
    {
        Assert.True(OcrConfigValidation.TryParseShape("1,3,640,640", out JsonArray? ok, out _));
        Assert.Equal(640, ok![3]!.GetValue<int>());
        Assert.False(OcrConfigValidation.TryParseShape("640,640", out _, out string? error));
        Assert.Equal("shape", error);
    }

    [Fact]
    public void ParseJsonObject_ReportsInvalid()
    {
        Assert.True(OcrConfigValidation.TryParseJsonObject("""{"run_mode":"trt_fp16"}""", out JsonObject? ok, out _));
        Assert.Equal("trt_fp16", ok!["run_mode"]!.GetValue<string>());
        Assert.False(OcrConfigValidation.TryParseJsonObject("{oops", out _, out string? error));
        Assert.Equal("json", error);
    }

    [Theory]
    [InlineData("bool")]
    [InlineData("int")]
    [InlineData("float")]
    [InlineData("string")]
    [InlineData("enum")]
    [InlineData("json")]
    [InlineData("shape")]
    public void Validate_NullValue_MeansUseDefault(string type)
        => Assert.Null(OcrConfigValidation.Validate(Spec(type), null));

    [Fact]
    public void Validate_Bool_AcceptsOnlyBool()
    {
        Assert.Null(OcrConfigValidation.Validate(Spec("bool"), JsonValue.Create(true)));
        Assert.Null(OcrConfigValidation.Validate(Spec("bool"), JsonValue.Create(false)));
        Assert.Equal("type", OcrConfigValidation.Validate(Spec("bool"), JsonValue.Create("true")));
    }

    [Fact]
    public void Validate_String_AcceptsOnlyString()
    {
        Assert.Null(OcrConfigValidation.Validate(Spec("string"), JsonValue.Create("gpu:0")));
        Assert.Equal("type", OcrConfigValidation.Validate(Spec("string"), JsonValue.Create(1)));
    }

    [Fact]
    public void Validate_Numbers_RejectWrongKinds()
    {
        Assert.Equal("type", OcrConfigValidation.Validate(Spec("float"), JsonValue.Create("0.9")));
        Assert.Equal("type", OcrConfigValidation.Validate(Spec("int"), JsonValue.Create(true)));
    }

    [Fact]
    public void Validate_Numbers_AcceptParsedNodes()
    {
        // Nút đọc từ JSON (element-backed) cũng chịu đúng luật kiểu/khoảng.
        Assert.Null(OcrConfigValidation.Validate(Spec("float", 0.0, 1.0), JsonNode.Parse("0.9")));
        Assert.Equal("range", OcrConfigValidation.Validate(Spec("int", 1, 64), JsonNode.Parse("0")));
        Assert.Equal("type", OcrConfigValidation.Validate(Spec("int", 1, 64), JsonNode.Parse("1.5")));
    }

    [Fact]
    public void Validate_Enum_RejectsWrongKind()
        => Assert.Equal("type", OcrConfigValidation.Validate(Spec("enum", values: ["min"]), JsonValue.Create(1)));

    [Fact]
    public void Validate_Json_RequiresObject()
    {
        Assert.Null(OcrConfigValidation.Validate(Spec("json"), JsonNode.Parse("""{"a":1}""")));
        Assert.Equal("json", OcrConfigValidation.Validate(Spec("json"), JsonNode.Parse("[1,2]")));
        Assert.Equal("json", OcrConfigValidation.Validate(Spec("json"), JsonValue.Create(1)));
    }

    [Fact]
    public void Validate_Shape_RequiresFourPositiveIntegers()
    {
        Assert.True(OcrConfigValidation.TryParseShape("1,3,640,640", out JsonArray? shape, out _));
        Assert.Null(OcrConfigValidation.Validate(Spec("shape"), shape));
        Assert.Equal("shape", OcrConfigValidation.Validate(Spec("shape"), JsonNode.Parse("[1,3,640]")));
        Assert.Equal("shape", OcrConfigValidation.Validate(Spec("shape"), JsonNode.Parse("[1,3,0,640]")));
        Assert.Equal("shape", OcrConfigValidation.Validate(Spec("shape"), JsonNode.Parse("[1,3,-2,640]")));
        Assert.Equal("shape", OcrConfigValidation.Validate(Spec("shape"), JsonNode.Parse("[1,3,640.5,640]")));
        Assert.Equal("shape", OcrConfigValidation.Validate(Spec("shape"), JsonNode.Parse("""["1",3,640,640]""")));
        Assert.Equal("shape", OcrConfigValidation.Validate(Spec("shape"), JsonValue.Create("1,3,640,640")));
    }

    [Fact]
    public void ParseShape_AllowsSpacesAndRejectsNonPositiveOrFractional()
    {
        Assert.True(OcrConfigValidation.TryParseShape("1, 3, 640, 640", out JsonArray? shape, out _));
        Assert.Equal(new[] { 1, 3, 640, 640 }, shape!.Select(n => n!.GetValue<int>()).ToArray());
        Assert.False(OcrConfigValidation.TryParseShape("1,3,0,640", out _, out string? zeroError));
        Assert.Equal("shape", zeroError);
        Assert.False(OcrConfigValidation.TryParseShape("1,3,-2,640", out _, out _));
        Assert.False(OcrConfigValidation.TryParseShape("1,3,640.5,640", out _, out _));
        Assert.False(OcrConfigValidation.TryParseShape("", out _, out string? emptyError));
        Assert.Equal("shape", emptyError);
    }

    [Fact]
    public void ParseJsonObject_RejectsNonObjectAndEmpty()
    {
        Assert.False(OcrConfigValidation.TryParseJsonObject("[1,2]", out _, out string? arrayError));
        Assert.Equal("json", arrayError);
        Assert.False(OcrConfigValidation.TryParseJsonObject("42", out _, out _));
        Assert.False(OcrConfigValidation.TryParseJsonObject("null", out _, out _));
        Assert.False(OcrConfigValidation.TryParseJsonObject("   ", out _, out string? emptyError));
        Assert.Equal("json", emptyError);
    }

    [Fact]
    public void FormatDefault_FormatsEverySchemaType()
    {
        Assert.Equal("true", OcrConfigValidation.FormatDefault(Spec("bool") with { Default = JsonValue.Create(true) }));
        Assert.Equal("false", OcrConfigValidation.FormatDefault(Spec("bool") with { Default = JsonValue.Create(false) }));
        Assert.Equal("32", OcrConfigValidation.FormatDefault(Spec("int") with { Default = JsonValue.Create(32) }));
        Assert.Equal("0.9", OcrConfigValidation.FormatDefault(Spec("float") with { Default = JsonValue.Create(0.9) }));
        Assert.Equal("abc", OcrConfigValidation.FormatDefault(Spec("string") with { Default = JsonValue.Create("abc") }));
        Assert.Equal("max", OcrConfigValidation.FormatDefault(
            Spec("enum", values: ["min", "max"]) with { Default = JsonValue.Create("max") }));
        Assert.Equal("1,3,640,640", OcrConfigValidation.FormatDefault(
            Spec("shape") with { Default = JsonNode.Parse("[1,3,640,640]") }));
        Assert.Equal("""{"run_mode":"trt_fp16"}""", OcrConfigValidation.FormatDefault(
            Spec("json") with { Default = JsonNode.Parse("""{"run_mode": "trt_fp16"}""") }));
    }

    [Fact]
    public void FormatDefault_NullDefault_ReturnsNull()
        => Assert.Null(OcrConfigValidation.FormatDefault(Spec("float", 0.0, 1.0)));

    [Fact]
    public void FormatDefault_Numbers_AreInvariantCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            // de-DE viết 0.9 thành "0,9" — placeholder phải luôn là dạng invariant "0.9".
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("0.9", OcrConfigValidation.FormatDefault(Spec("float") with { Default = JsonValue.Create(0.9) }));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
