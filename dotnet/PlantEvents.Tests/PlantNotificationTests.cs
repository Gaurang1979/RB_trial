using System.Text.Json;
using PlantEvents;
using Xunit;

public class PlantNotificationTests
{
    [Fact]
    public void NotificationRoundTripsAndValidates()
    {
        var notif = new PlantNotification
        {
            Id = "NOTIF-2026-TTO-001",
            Ts = 1_793_280_000_000,
            SourceProcess = "Sundaram.PackagingEngine.TTO",
            Plant = "Plant-GJ04",
            Line = "Line-01",
            Station = "Station-Carton-01",
            Camera = "cam1",
            Category = NotificationCategory.PrinterDiagnostic,
            Severity = NotificationSeverity.Warning,
            Printer = PrinterType.TTO,
            Code = "TTO_ELEMENT_BURNOUT",
            Title = "TTO Printhead Element Void Detected",
            MessageEn = "Printhead element row 84 burnout detected across DataMatrix.",
            LocalizedGuidance = new Dictionary<string, string>
            {
                ["en"] = "Printhead Element Burnout: Replace TTO thermal printhead (Dot row 84).",
                ["hi"] = "प्रिंटहेड तत्व विफलता: TTO थर्मल प्रिंटहेड बदलें (डॉट रो 84)।",
                ["gu"] = "પ્રિન્ટહેડ એલિમેન્ટ ખામી: TTO થર્મલ પ્રિન્ટહેડ બદલો (ડોટ રો 84).",
                ["ta"] = "அச்சு முனை உறுப்பு குறைபாடு: TTO அச்சு முனையை மாற்றவும் (புள்ளி வரிசை 84)."
            },
            Metrics = new Dictionary<string, double>
            {
                ["dead_dot_row"] = 84,
                ["aspect_ratio"] = 1.02
            },
            RecommendedAction = "Replace TTO printhead (Row 84)",
            AckRequired = true,
            State = "Active"
        };

        Assert.Empty(notif.Validate());

        var json = PlantNotificationJson.Serialize(notif);
        Assert.Contains("\"id\":\"NOTIF-2026-TTO-001\"", json);
        Assert.Contains("\"category\":\"printer_diagnostic\"", json);
        Assert.Contains("\"printer\":\"tto\"", json);
        Assert.Contains("\"severity\":\"warning\"", json);

        var roundTrip = PlantNotificationJson.Parse(json);
        Assert.Equal(notif.Id, roundTrip.Id);
        Assert.Equal(notif.Category, roundTrip.Category);
        Assert.Equal(notif.Printer, roundTrip.Printer);
        Assert.Equal(notif.LocalizedGuidance["gu"], roundTrip.LocalizedGuidance["gu"]);
        Assert.Equal(84, roundTrip.Metrics["dead_dot_row"]);
    }

    [Fact]
    public void ExternalProcessPublisherPipeConstantsAreExposed()
    {
        Assert.Equal("SundaramNotificationBus", PlantNotification.DefaultPipeName);
        Assert.Equal("sundaram_notif_bus.sock", PlantNotification.DefaultSocketName);
        Assert.Equal(52199, PlantNotification.DefaultRestPort);
    }
}
