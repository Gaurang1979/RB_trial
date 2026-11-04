import 'package:audioplayers/audioplayers.dart';

import 'feedback.dart';

/// Plays the clips with the audioplayers plugin (as secure_me's console
/// does). Quiet on silent or vibrate: on Android the clips use the
/// ringtone volume, which those modes mute, and take no audio focus; on
/// iOS the ambient category, which the silent switch mutes.
class PcAudioPlayer implements PcSoundPlayer {
  static final _context = AudioContext(
    android: const AudioContextAndroid(
      usageType: AndroidUsageType.notificationRingtone,
      contentType: AndroidContentType.sonification,
      audioFocus: AndroidAudioFocus.none,
    ),
    iOS: AudioContextIOS(category: AVAudioSessionCategory.ambient),
  );
  static final _cache = AudioCache(prefix: 'packages/plant_core/assets/sounds/');

  // One low-latency player per sound, made on first use.
  final Map<PcSound, Future<AudioPlayer?>> _players = {};
  bool _globalSet = false;
  DateTime? _failedAt;
  static const _retryAfter = Duration(seconds: 30);

  @override
  Future<void> play(PcSound sound) async {
    // After a failure, try again at most every 30 s; a critical alarm always tries.
    final failed = _failedAt;
    if (failed != null && sound != PcSound.critical && DateTime.now().difference(failed) < _retryAfter) return;
    final p = await (_players[sound] ??= _make());
    if (p == null) {
      _players.remove(sound); // do not keep a failed attempt
      return;
    }
    await p.stop();
    await p.play(AssetSource('${sound.name}.wav'), volume: .8);
  }

  Future<AudioPlayer?> _make() async {
    AudioPlayer? p;
    try {
      if (!_globalSet) {
        await AudioPlayer.global.setAudioContext(_context);
        _globalSet = true;
      }
      p = AudioPlayer()..audioCache = _cache;
      await p.setAudioContext(_context);
      await p.setPlayerMode(PlayerMode.lowLatency);
      await p.setReleaseMode(ReleaseMode.stop);
      _failedAt = null;
      return p;
    } catch (_) {
      // The platform refused: stay quiet for a while instead of retrying every tap.
      _failedAt = DateTime.now();
      await p?.dispose();
      return null;
    }
  }
}
