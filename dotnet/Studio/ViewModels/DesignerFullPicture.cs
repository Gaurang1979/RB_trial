using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// The full-resolution picture for zooming in during job setup (requirements AS-17, UI-UX-SPEC.md 4.19
/// "Centre, zoom and pan"). A try sends a reduced JPEG; above Fit the tried picture is fetched without
/// loss (GetImage, PNG) and swapped in, so clarity is judged on real pixels. Once it is shown, the
/// picture, its graphics and the area drawing are all in camera pixels; before, all in JPEG pixels, so
/// each stays registered to the picture under it. Advice only: what is shown never changes a result.
/// </summary>
public sealed partial class DesignerViewModel
{
    /// <summary>The full-resolution picture of <see cref="_last"/>, when it has been fetched.</summary>
    private (IImage Image, int Width, int Height)? _full;
    /// <summary>The try whose full picture was asked for: asked once per picture.</summary>
    private TryOutcome? _fullAsked;
    /// <summary>When the last fetch failed: asked again on a zoom after <see cref="FullPictureRetry"/>.</summary>
    private DateTime _fullFailedAt = DateTime.MinValue;
    public static readonly TimeSpan FullPictureRetry = TimeSpan.FromSeconds(5);
    private double _zoom = 1;
    /// <summary>Screen pixels per camera pixel at this zoom; 0 when not known.</summary>
    private double _screenPerCamera;

    /// <summary>"Loading full picture…", or why the reduced picture is shown zoomed in; empty otherwise.</summary>
    [ObservableProperty] private string _fullPictureText = "";
    /// <summary>The whole picture as tried (the reduced one), for the overview in the corner.</summary>
    [ObservableProperty] private IImage? _overviewImage;

    /// <summary>The full-resolution picture is on screen.</summary>
    public bool ShowsFullPicture => _full is not null;

    /// <summary>The camera's pixels across and down (for 100 %); 0 when no picture is shown.</summary>
    public double CameraWidth => _last is { } r ? (r.ImageWidth > 0 ? r.ImageWidth : r.Width / ShownScaleOf(r)) : 0;
    public double CameraHeight => _last is { } r ? (r.ImageHeight > 0 ? r.ImageHeight : r.Height / ShownScaleOf(r)) : 0;

    /// <summary>Pixels of the picture shown per camera pixel: the try's JPEG scale, or 1 once the full picture is shown.</summary>
    public double ShownScale => _full is not null ? 1 : _last is { } r ? ShownScaleOf(r) : 1;

    private static double ShownScaleOf(TryOutcome r) => r.Scale > 0 ? r.Scale : 1;

    /// <summary>A try's picture is shown (or none): the reduced one, until the engineer zooms in.</summary>
    private void ShowPicture(TryOutcome? r)
    {
        var full = _full?.Image;
        _full = null;
        _fullAsked = null;
        _fullFailedAt = DateTime.MinValue;
        FullPictureText = "";
        Image = r?.Jpeg is { Length: > 0 } jpeg ? _decode(jpeg) : null;
        OverviewImage = Image;
        Drop(full);   // a full picture is tens of MB of native memory: freed now, not at the next collection
    }

    private static void Drop(IImage? image) => (image as IDisposable)?.Dispose();

    /// <summary>The result graphics of the try (in JPEG pixels) in the pixels of the picture shown.</summary>
    private IEnumerable<ResultGraphic> ShownGraphics()
    {
        if (_last is not { } r) return [];
        var k = ShownScale / ShownScaleOf(r);
        return k == 1 ? r.Graphics ?? []
            : (r.Graphics ?? []).Select(g => g with { Points = g.Points.Select(p => new Point2(p.X * k, p.Y * k)).ToArray() });
    }

    /// <summary>The zoom changed (1 = fit): the label, and above fit the full picture. The label is in camera
    /// pixels (100 % = one camera pixel per screen pixel, as the 100 % button) when the view knows them
    /// (<paramref name="screenPerCamera"/>, screen pixels per camera pixel), else in times the fitted picture.</summary>
    public void ShowZoom(double zoom, double screenPerCamera = 0)
    {
        _zoom = zoom;
        _screenPerCamera = screenPerCamera;
        var percent = ((screenPerCamera > 0 ? screenPerCamera : zoom) * 100).ToString("0", CultureInfo.CurrentCulture);
        ZoomText = _strings.Format(ShowsFullPicture ? "ws.zoom.full" : "ws.zoom", percent);
        if (zoom > 1) AskFullPicture();
        else if (FullPictureText == _strings["ws.full.none"]) FullPictureText = "";   // at fit the reduced picture is enough
    }

    /// <summary>The full picture being fetched, if any (done when nothing is).</summary>
    public Task FullPictureFetch { get; private set; } = Task.CompletedTask;

    private void AskFullPicture()
    {
        var fetch = FetchFullPictureAsync();
        if (!fetch.IsCompleted) FullPictureFetch = fetch;
    }

    /// <summary>Fetch the shown try's picture at full resolution and swap it in; the reduced one stays when the
    /// runtime does not keep it (an older runtime, or a picture tried long ago) or it cannot be read.</summary>
    private async Task FetchFullPictureAsync()
    {
        if (_last is not { } r || _full is not null || Image is null) return;
        if (ReferenceEquals(_fullAsked, r) && (r.ImageId.Length == 0 || DateTime.UtcNow - _fullFailedAt < FullPictureRetry)) return;
        _fullAsked = r;
        _fullFailedAt = DateTime.MaxValue;   // asking
        if (r.ImageId.Length == 0) { FullPictureText = _strings["ws.full.none"]; return; }
        FullPictureText = _strings["ws.full.loading"];
        var (reached, picture) = await _calls.TryAsync((c, ct) => c.GetImageAsync(r.ImageId, ct), TryLimit);
        IImage? image = null;
        if (picture is { Data.Length: > 0 } p && SameSize(p, r))
            image = await Task.Run(() => { try { return _decode(p.Data); } catch (Exception) { return null; } });
        if (!ReferenceEquals(_last, r)) { Drop(image); return; }   // another picture was shown meanwhile
        if (!reached || picture is null || image is null)
        {
            FullPictureText = _strings["ws.full.none"];
            _fullFailedAt = DateTime.UtcNow;   // a slow link or a busy runtime: zooming again later asks again
            return;
        }
        _full = (image, picture.Width, picture.Height);
        Image = image;
        ImageWidth = picture.Width;
        ImageHeight = picture.Height;
        FullPictureText = "";
        DrawGraphics();
        ShowZoom(_zoom, _screenPerCamera);
    }

    /// <summary>The full picture is the tried one: as many camera pixels as the try said.</summary>
    private static bool SameSize(FullImage p, TryOutcome r) =>
        p.Width == r.ImageWidth && p.Height == r.ImageHeight && p.Width > 0 && p.Height > 0;
}
