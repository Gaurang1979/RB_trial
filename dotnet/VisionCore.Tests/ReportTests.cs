using ClosedXML.Excel;
using VisionCore.Plc;
using VisionReports;
using Xunit;

namespace VisionCore.Tests;

public class PartLogTests
{
    [Fact]
    public void RecordsRoundTripAndComeInOrderAcrossTheWrap()
    {
        var b = new PlcBlock(PlcPartLog.Size, false);
        // ring holds seq 65534, 65535, 1, 2 (0 is skipped) in slots 4..7
        ushort[] seqs = { 65534, 65535, 1, 2 };
        for (int i = 0; i < 4; i++)
            PlcPartLog.Write(b, 4 + i, new PartRecord(seqs[i], (ushort)(100 + i), PartFlags.Rejected | PartFlags.Touching, 812, 0, 1, 0, 2, i % 2));
        var r = PlcPartLog.Read(b, 5)!;
        Assert.Equal(101, r.PartId);
        Assert.True(r.Rejected);
        Assert.Equal(2, r.GroupSize);
        Assert.Equal(1, r.GroupIndex);
        Assert.True(r.CameraAnswered(0));
        Assert.False(r.CameraOk(0));
        var newer = PlcPartLog.Newer(b, 65535);
        Assert.Equal(new ushort[] { 1, 2 }, newer.Select(x => x.Seq));
    }

    [Fact]
    public void LengthSettingsAreSentInPulsesPerMetreAndMm()
    {
        var s = new LineSettings { PulsesPerMetre = 6366, ProductLengthMm = 80, LengthTolerancePct = 15, MinGapMm = 20, RejectTouching = true, RejectLengthOut = false };
        var b = new PlcBlock(LineSettings.Size, false);
        s.Write(b, 1);
        Assert.Equal(64, LineSettings.Size);
        Assert.Equal(6366u, b.U32(44));
        Assert.Equal(80, b.U16(48));
        Assert.Equal(15, b.U16(50));
        Assert.Equal(20, b.U16(52));
        Assert.Equal(1, b.U16(54));
        Assert.Equal(6366, new LineSettings { EncoderPpr = 1000, RollerDiameterMm = 50 }.CalculatedPulsesPerMetre);
        Assert.Equal(80.0, s.MmOf(s.PulsesOf(80))!.Value, 1);
    }

    [Fact]
    public void TouchingProductsGetAReasonNote()
    {
        var p = new PartRecord(1, 7, PartFlags.Rejected | PartFlags.Touching | PartFlags.NotInspected, 400, 0, 0, 0, 2, 1);
        var (cat, reason) = PartReasons.Explain(p, 1, Array.Empty<CameraVerdict>(), 80, 80, 20, 0, null);
        Assert.Equal(PartReasons.TouchingCat, cat);
        Assert.Contains("product 2 of 2", reason);
        var ng = new PartRecord(2, 8, PartFlags.Rejected | PartFlags.CameraNg, 400, 300, 1, 0, 1, 0);
        var (cat2, reason2) = PartReasons.Explain(ng, 1, new[] { new CameraVerdict(0, false, "Batch text", "expected HWN937, found HWN881") }, 80, 80, 20, 60, null);
        Assert.Equal("Camera 1: Batch text", cat2);
        Assert.Contains("found HWN881", reason2);
    }
}

public class ReportTests
{
    [Fact]
    public void AResultsLogMadeBeforeThePartTextColumnGetsIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-oldlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "results.db")};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE inspections (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, time_utc TEXT NOT NULL, batch TEXT NOT NULL, job TEXT NOT NULL,
                    camera INTEGER NOT NULL, part_id INTEGER, ok INTEGER NOT NULL, fail_tool TEXT, reason TEXT,
                    text1 TEXT, text2 TEXT, ms REAL NOT NULL);
                INSERT INTO inspections (time_utc,batch,job,camera,part_id,ok,ms) VALUES ('2026-10-31 06:00:00.000','B1','J',0,7,1,5);
                """;
            cmd.ExecuteNonQuery();
        }
        var log = new ResultLog(dir);
        new ResultLog(dir);                                // opened again (or by a second program): no error
        var t = new DateTime(2026, 10, 31, 6, 1, 0, DateTimeKind.Utc);
        log.AddInspection(new InspectionRow(0, t, "B1", "J", 0, null, true, null, null, null, null, 5, "A12-0001"));
        var rows = log.Inspections(t.AddHours(-1), t.AddHours(1));
        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].PartText);
        Assert.Equal(7, rows[0].PartId);
        Assert.Equal("A12-0001", rows[1].PartText);
    }

    [Fact]
    public void TheImageNameIsKeptWithInspectionsAndPartsAndAnOlderLogGetsTheColumn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-imglog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "results.db")};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE parts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, time_utc TEXT NOT NULL, batch TEXT NOT NULL, job TEXT NOT NULL,
                    part_id INTEGER NOT NULL, rejected INTEGER NOT NULL, flags INTEGER NOT NULL, length_mm REAL, gap_mm REAL,
                    group_size INTEGER NOT NULL, group_index INTEGER NOT NULL, cam_answered INTEGER NOT NULL, cam_ok INTEGER NOT NULL,
                    category TEXT NOT NULL, reason TEXT NOT NULL);
                INSERT INTO parts (time_utc,batch,job,part_id,rejected,flags,group_size,group_index,cam_answered,cam_ok,category,reason)
                    VALUES ('2026-10-31 06:00:00.000','B1','J',3,0,0,1,0,1,1,'good','');
                """;
            cmd.ExecuteNonQuery();
        }
        var log = new ResultLog(dir);
        var t = new DateTime(2026, 10, 31, 6, 1, 0, DateTimeKind.Utc);
        const string name = "L01_C2_J045_261031-113100_000123";
        log.AddInspection(new InspectionRow(0, t, "B1", "J", 1, 4, false, "Read code", "no code", null, null, 5, null, name));
        log.AddPart(new PartRow(0, t, "B1", "J", 4, true, PartFlags.Rejected, null, null, 1, 0, 1, 0, "vision", "no code", name));
        var insp = log.Inspections(t.AddHours(-1), t.AddHours(1)).Single();
        Assert.Equal(name, insp.ImageId);
        var parts = log.Parts(t.AddHours(-1), t.AddHours(1));
        Assert.Equal(2, parts.Count);
        Assert.Null(parts[0].ImageId);                     // logged before the column
        Assert.Equal(name, parts[1].ImageId);
    }

    private static ResultLog Sample(out DateTime from, out DateTime to)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-report-" + Guid.NewGuid().ToString("N"));
        var log = new ResultLog(dir);
        from = new DateTime(2026, 10, 29, 6, 0, 0, DateTimeKind.Utc);
        to = from.AddHours(2);
        log.StartBatch("B2610", "Harpic 500 ml", "op1", "{\"BATCH\":\"HWN937\"}", from);
        var rnd = new Random(4);
        for (int i = 0; i < 600; i++)
        {
            var t = from.AddSeconds(i * 10);
            bool touching = i % 97 == 5, shortOne = i % 150 == 9, camNg = i % 50 == 3;
            var flags = PartFlags.None;
            if (touching) flags |= PartFlags.Touching | PartFlags.Rejected;
            if (shortOne) flags |= PartFlags.LengthOut | PartFlags.Rejected;
            if (camNg) flags |= PartFlags.CameraNg | PartFlags.Rejected;
            double len = shortOne ? 41 : 80 + rnd.NextDouble() * 2 - 1;
            var rec = new PartRecord((ushort)(i + 1), (ushort)(i + 1), flags, 0, 0, 1, (byte)(camNg ? 0 : 1), touching ? 2 : 1, 0);
            var (cat, reason) = PartReasons.Explain(rec, 1,
                camNg ? new[] { new CameraVerdict(0, false, "Batch text", "expected HWN937, found HWN881") } : Array.Empty<CameraVerdict>(),
                len, 80, 20, 70, null);
            log.AddPart(new PartRow(0, t, "B2610", "Harpic 500 ml", i + 1, rec.Rejected, flags, len, 70, rec.GroupSize, 0, 1, rec.CamOk, cat, reason));
            log.AddInspection(new InspectionRow(0, t, "B2610", "Harpic 500 ml", 0, i + 1, !camNg, camNg ? "Batch text" : null,
                camNg ? "expected HWN937, found HWN881" : null, "8901396152002", "HWN937", 18 + rnd.NextDouble() * 5));
        }
        log.AddEvent(from.AddMinutes(30), "jam", true, "jam sensor");
        log.AddEvent(from.AddMinutes(32), "jam", false, "");
        return log;
    }

    [Fact]
    public void BuildsSummaryWithReasonsAndFindings()
    {
        var log = Sample(out var from, out var to);
        var d = ReportBuilder.Build(log, new ReportQuery(from, to, null, "Shift A"), "Line 1",
            new LineSettings { ProductLengthMm = 80, LengthTolerancePct = 20 });
        Assert.Equal(600, d.Parts);
        Assert.True(d.FromPlc);
        Assert.Equal("Camera 1: Batch text", d.Reasons[0].Name);
        Assert.Contains(d.Reasons, r => r.Name == PartReasons.TouchingCat);
        Assert.Contains(d.Insights, s => s.Contains("without a gap"));
        Assert.Equal(120, d.StoppedSeconds, 0);
        Assert.NotNull(d.Cpk);
        Assert.Single(d.Batches);
    }

    [Fact]
    public void WritesPdfAndExcel()
    {
        var log = Sample(out var from, out var to);
        var d = ReportBuilder.Build(log, new ReportQuery(from, to, "B2610", "Batch B2610"), "Line 1");
        var pdf = PdfReport.Create(d);
        Assert.True(pdf.Length > 2000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        var xlsx = ExcelReport.Create(d);
        using var wb = new XLWorkbook(new MemoryStream(xlsx));
        Assert.Equal(600, wb.Worksheet("All products").LastRowUsed()!.RowNumber() - 1);
        Assert.Contains(wb.Worksheets, w => w.Name == "Rejected");
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "vs-sample-report.pdf"), pdf);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "vs-sample-report.xlsx"), xlsx);
    }
}
