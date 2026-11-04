namespace VisionStudio.Services;

/// <summary>The "Studio" section of appsettings.json. Relative folders are next to the program.</summary>
public sealed class StudioOptions
{
    public string JobsFolder { get; set; } = "jobs";
    public string DataFolder { get; set; } = "data";
    public string ModelsFolder { get; set; } = "models";
    /// <summary>UI language (i18n/&lt;code&gt;.json).</summary>
    public string Language { get; set; } = "en";
    /// <summary>No sign-in: everyone may run, teach and test (bench and development PCs only).</summary>
    public bool OpenMode { get; set; } = true;
    /// <summary>Time between images when replaying folders, and between software triggers in free run.</summary>
    public int FolderIntervalMs { get; set; } = 300;
    /// <summary>Production needs a licence checked with the licence server (false on development PCs).</summary>
    public bool LicenceRequired { get; set; } = true;
    /// <summary>Production needs the package integrity check to pass: every shipped file signed and unchanged
    /// (docs/vision-system/PACKAGE-PROTECTION.md). Off by default so a plain source build, which has no signed
    /// manifest, still runs; the shipped package turns it on.</summary>
    public bool IntegrityRequired { get; set; }
    /// <summary>NG images: NgOnly, All, EveryNthNg or None (counts are always kept).</summary>
    public string SaveImages { get; set; } = "NgOnly";
    public int SaveEveryNth { get; set; } = 10;
    /// <summary>Most NG images kept on disk, and their total size; the oldest go first.</summary>
    public int ImagesMaxFiles { get; set; } = 5000;
    public double ImagesMaxGb { get; set; } = 5;
    /// <summary>Memory this program may use before the guard starts clearing (MB); 0 = half the PC's memory.</summary>
    public int MemoryBudgetMb { get; set; }
    /// <summary>This line's number in image names and folders (L01, owner 2026-10-31), 1-99.</summary>
    public int LineNumber { get; set; } = 1;

    /// <summary>The plant's public key (PEM, ECDSA P-256) that a .vsrecipe bundle must be signed with to be
    /// imported (Req 17.11 AUD-06). Missing file = every import is refused.</summary>
    public string RecipePublicKeyFile { get; set; } = "recipe-public.pem";
    /// <summary>The plant's private key (PEM) that signs exported bundles; only on the PCs that export. Empty or
    /// missing = export is refused.</summary>
    public string RecipeSigningKeyFile { get; set; } = "";

    public void ResolvePaths(string baseDir)
    {
        JobsFolder = Path.GetFullPath(JobsFolder, baseDir);
        DataFolder = Path.GetFullPath(DataFolder, baseDir);
        ModelsFolder = Path.GetFullPath(ModelsFolder, baseDir);
        if (!string.IsNullOrWhiteSpace(RecipePublicKeyFile)) RecipePublicKeyFile = Path.GetFullPath(RecipePublicKeyFile, baseDir);
        if (!string.IsNullOrWhiteSpace(RecipeSigningKeyFile)) RecipeSigningKeyFile = Path.GetFullPath(RecipeSigningKeyFile, baseDir);
    }
}
