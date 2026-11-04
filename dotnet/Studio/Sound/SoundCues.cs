using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VisionSystem.Studio.Sound;

/// <summary>The control sounds of the whole app (requirements 17.15 NXT-04): a blip on every button
/// press, from touch, mouse or keyboard alike, and a tick when the mouse moves onto an enabled button
/// (never from touch or pen, which have no hover).</summary>
public static class SoundCues
{
    private static ISoundService _sound = SoundService.None;
    private static bool _attached;

    public static void Attach(ISoundService sound)
    {
        _sound = sound;
        if (_attached) return;
        _attached = true;
        Button.ClickEvent.AddClassHandler<Button>((_, _) => _sound.Play(SoundKind.Tap), RoutingStrategies.Bubble, handledEventsToo: true);
        InputElement.PointerEnteredEvent.AddClassHandler<Button>((button, e) =>
        {
            if (button.IsEffectivelyEnabled) _sound.Play(SoundKind.Hover, e.Pointer.Type == PointerType.Mouse);
        });
    }
}
