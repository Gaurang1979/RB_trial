import 'package:flutter/services.dart';

import 'look.dart';

/// The app's sounds, each a short bundled clip (`assets/sounds/<name>.wav`,
/// made by tool/make_sounds.py; the same tones as secure_me's console).
enum PcSound {
  /// Any tap on a control.
  tick,
  /// A main action is done (acknowledge, approve, save).
  confirm,
  /// Undo.
  undo,
  /// A QR code was read; a check started.
  scan,
  /// Alarm, low priority: one soft note, no vibration.
  notice,
  /// Alarm, medium priority: two rising notes and a double vibration.
  attention,
  /// Alarm, high priority: three short pulses and three firm vibrations.
  critical,
}

/// Plays a [PcSound]. The app sets [PcFeedback.player] to the audio
/// player at start; until then, and in tests, nothing plays.
abstract interface class PcSoundPlayer {
  Future<void> play(PcSound sound);
}

class PcSilentPlayer implements PcSoundPlayer {
  const PcSilentPlayer();
  @override
  Future<void> play(PcSound sound) async {}
}

/// Sound and touch feedback for every control and alarm. Sounds follow
/// [PcLook.sound]; the player keeps them quiet when the phone is on silent
/// or vibrate. Unlike secure_me there is no night rule: plants run night
/// shifts, so an alarm sounds at any hour. An NG part never makes a sound
/// on the phone; only alarms do, by priority.
class PcFeedback {
  PcFeedback(this.look, {PcSoundPlayer? player}) : player = player ?? const PcSilentPlayer();

  final PcLook look;
  PcSoundPlayer player;

  Future<void> tap() => _do(PcSound.tick, HapticFeedback.selectionClick);
  Future<void> confirm() => _do(PcSound.confirm, HapticFeedback.mediumImpact);
  Future<void> undo() => _do(PcSound.undo, HapticFeedback.lightImpact);
  Future<void> scan() => _do(PcSound.scan, HapticFeedback.lightImpact);

  /// An alarm by priority: "high", "medium" or anything else (low).
  Future<void> alarm(String priority) async {
    switch (priority) {
      case 'high':
        await _do(PcSound.critical, () async {
          for (var i = 0; i < 3; i++) {
            await HapticFeedback.heavyImpact();
            await Future<void>.delayed(const Duration(milliseconds: 120));
          }
        });
      case 'medium':
        await _do(PcSound.attention, () async {
          await HapticFeedback.mediumImpact();
          await Future<void>.delayed(const Duration(milliseconds: 120));
          await HapticFeedback.mediumImpact();
        });
      default:
        await _do(PcSound.notice, () async {});
    }
  }

  /// The vibration pattern and the sound start together: a pattern takes
  /// a third of a second and must not hold the sound back. Nothing a
  /// vibrator or the player throws escapes: feedback never breaks the screen.
  Future<void> _do(PcSound s, Future<void> Function() haptic) async {
    final vibrating = Future<void>.sync(haptic).catchError((Object _) {
      // No vibrator (tablets, desktops): sound only.
    });
    if (look.sound) {
      try {
        await player.play(s);
      } catch (_) {
        // The platform refused the sound: stay silent, keep the vibration.
      }
    }
    await vibrating;
  }
}
