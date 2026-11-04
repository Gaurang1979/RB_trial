namespace BarcodeReading.Dashboard.Services;

/// <summary>Settings from appsettings.json, section "Station".</summary>
public sealed class StationOptions
{
    /// <summary>Product master folder (ProductStore JSON files and tuning state).</summary>
    public string ProductsFolder { get; set; } = "data/products";
    /// <summary>Folder with codenet.onnx / enhancer.onnx, and candidates/ from ml/retrain.py.</summary>
    public string ModelsFolder { get; set; } = "models";
    /// <summary>SQLite case log, users file and hard cases live here.</summary>
    public string DataFolder { get; set; } = "data";
    /// <summary>Serial number of the top camera (child codes); empty = first USB camera.</summary>
    public string? ChildCameraSerial { get; set; }
    /// <summary>Serial number of the side camera (parent code); empty = no side camera.</summary>
    public string? ParentCameraSerial { get; set; }
    /// <summary>Default folder of saved images for Replay mode (testing without a camera).</summary>
    public string? ReplayFolder { get; set; }
    public int ReplayIntervalMs { get; set; } = 1000;
    /// <summary>Save every short case as BMP + sidecar for the retraining agent (data/hard-cases/&lt;product&gt;/).
    /// Off: cases are still counted by reason, no images are kept.</summary>
    public bool SaveHardCases { get; set; } = true;
    /// <summary>Most hard-case images kept, all products together (about 20 MB each); the oldest are deleted first.</summary>
    public int HardCaseLimit { get; set; } = 500;
    /// <summary>Most disk the hard-case images may take, in GB; the oldest are deleted first.</summary>
    public double HardCasesMaxGb { get; set; } = 10;
    /// <summary>Memory the station may use, in MB, for the resource guard; 0 = half the PC's memory.</summary>
    public int MemoryBudgetMb { get; set; }
    /// <summary>Vision Studio's data folder on this PC (licence.json and the installation key), read for the
    /// content key that decrypts the models and products (requirements 18.3, ENC-HOST-01). Set: a licence is
    /// needed and the station does not start without the key. Empty: a development PC, artifacts in the clear.</summary>
    public string? LicenceFolder { get; set; }
    /// <summary>API key for your own software (header X-Api-Key); empty = API needs a signed-in user.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Makes relative folders absolute, next to the program. A Windows service runs in
    /// System32, so a relative "data" would otherwise land there.
    /// </summary>
    public void ResolvePaths(string baseDir)
    {
        static string Abs(string baseDir, string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(baseDir, p));
        ProductsFolder = Abs(baseDir, ProductsFolder);
        ModelsFolder = Abs(baseDir, ModelsFolder);
        DataFolder = Abs(baseDir, DataFolder);
        if (!string.IsNullOrWhiteSpace(ReplayFolder)) ReplayFolder = Abs(baseDir, ReplayFolder);
        if (!string.IsNullOrWhiteSpace(LicenceFolder)) LicenceFolder = Abs(baseDir, LicenceFolder);
    }
}
