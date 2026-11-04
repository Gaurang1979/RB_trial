using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// One camera's live image on the Run screen (UI-UX-SPEC.md 4.1): its own picture, result graphics,
/// verdict frame and tooltip, side by side with the other cameras' tiles. Line safety (requirements
/// section 12): a tile with no picture, or a stale one, is dimmed and its frame is never green.
/// </summary>
public sealed partial class CameraTile(string name, int index) : ObservableObject
{
    /// <summary>The camera's name in the job; "" while the runtime has named none.</summary>
    public string Name { get; } = name;
    /// <summary>Position in the job's camera order; C1, C2 on the bad parts' thumbnails.</summary>
    public int Index { get; } = index;

    [ObservableProperty] private IImage? _liveImage;
    [ObservableProperty] private double _imageWidth = 1;
    [ObservableProperty] private double _imageHeight = 1;
    [ObservableProperty] private IReadOnlyList<ResultGraphic> _graphics = [];
    [ObservableProperty] private bool _dimmed = true;
    /// <summary>No picture and nothing drawn yet (the runtime may send graphics without a JPEG).</summary>
    [ObservableProperty] private bool _showNoPicture = true;
    [ObservableProperty] private Tone _frameTone = Tone.Neutral;
    /// <summary>The camera's name and its result over the picture; only with two or more cameras.</summary>
    [ObservableProperty] private bool _showHeader;
    [ObservableProperty] private string _resultIcon = Icons.Camera;
    /// <summary>The tapped result graphic's text, shown for a few seconds over the picture.</summary>
    [ObservableProperty] private string? _tooltipText;
    [ObservableProperty] private Point2? _tooltipAt;

    /// <summary>This camera's results, so its frame holds a bad part as the result block does.</summary>
    internal ResultHold Hold { get; } = new();
    /// <summary>The picture is from before a stop, product change or drop.</summary>
    internal bool Stale { get; set; } = true;
    /// <summary>When this camera's last picture came; MinValue before the first.</summary>
    internal DateTimeOffset FrameAt { get; set; } = DateTimeOffset.MinValue;
    /// <summary>The part shown, for its "This picture" lines and the codes in a tooltip; null when cleared.</summary>
    internal LiveFrame? Frame { get; set; }
    internal Point2 TapAt { get; set; } = new(0, 0);
    internal double TapSlop { get; set; }
    internal DateTimeOffset TooltipUntil { get; set; }

    /// <summary>Forgets the part shown: what is on screen came from before (a drop, a stop, another product).</summary>
    internal void Forget()
    {
        Hold.Clear();
        Stale = true;
        Frame = null;
        HideTooltip();
    }

    internal void HideTooltip()
    {
        TooltipText = null;
        TooltipAt = null;
    }
}

/// <summary>One line of the Batch and PLC card: a batch value or a PLC signal with its state.</summary>
public sealed record SignalLine(Tone Tone, string Icon, string Label, string Value);
