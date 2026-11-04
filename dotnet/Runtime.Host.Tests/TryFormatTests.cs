using BarcodeReading;
using VisionCore;
using VisionSystem.Runtime;
using Xunit;

/// <summary>What a try tells the "Areas and values" panel (requirement AS-15): each text line's suggested format and
/// field type, as Teach and auto find would write them, and a GS1 code's elements. Advice only: nothing in the job changes.</summary>
public sealed class TryFormatTests
{
    static readonly DateTime When = new(2026, 11, 1, 10, 30, 0);

    [Fact]
    public void A_Read_text_line_suggests_its_format_and_field_type()
    {
        var read = new TextToolConfig { Name = "Dates", Mode = TextMode.Read };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (format, field) = JobDesigner.LineFormat(read, "EXP 10/2027", used, When);
        Assert.Contains("{EXP", format);
        Assert.Equal("expiry date", field);
        Assert.Equal(("", ""), JobDesigner.LineFormat(read, "  ", used, When));   // nothing read: nothing suggested
    }

    [Fact]
    public void A_Check_text_line_suggests_the_text_as_its_template()
    {
        var check = new TextToolConfig { Name = "Batch", Mode = TextMode.Check };
        var (format, field) = JobDesigner.LineFormat(check, "B.NO A123", new HashSet<string>(), When);
        Assert.Equal("B.NO A123", format);
        Assert.Equal("lot / batch", field);
    }

    [Fact]
    public void A_GS1_code_suggests_its_elements_and_a_plain_code_none()
    {
        var gs1 = CodeTeach.Gs1Data(CodeFormat.DataMatrix, "010890123456789017271031" + "10A123\u001d21" + "00042");
        Assert.NotNull(gs1);
        var field = JobDesigner.Gs1Field(gs1);
        Assert.StartsWith("GS1: ", field);
        Assert.Contains("(01)", field);
        Assert.Contains("(17)", field);
        Assert.Contains("(10)", field);
        Assert.Contains("(21)", field);
        Assert.Null(CodeTeach.Gs1Data(CodeFormat.Ean13, "8901234567890"));   // an EAN-13 is not GS1 element data
        Assert.Null(CodeTeach.Gs1Data(CodeFormat.QrCode, "https://x.in/abc"));
    }
}
