import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plant_core/plant_core.dart';

class _Throwing implements PcSoundPlayer {
  int calls = 0;
  @override
  Future<void> play(PcSound sound) async {
    calls++;
    throw PlatformException(code: 'no_audio');
  }
}

class _Recording implements PcSoundPlayer {
  final played = <PcSound>[];
  @override
  Future<void> play(PcSound sound) async => played.add(sound);
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  final messenger = TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;

  // Plain tests on real time: the vibration pattern waits 120 ms between pulses.
  tearDown(() => messenger.setMockMethodCallHandler(SystemChannels.platform, null));

  test('an alarm completes without error when the player throws', () async {
    messenger.setMockMethodCallHandler(SystemChannels.platform, (_) async => null);
    final player = _Throwing();
    final f = PcFeedback(PcLook(), player: player);
    for (final p in ['high', 'medium', 'low']) {
      await f.alarm(p);
    }
    await f.tap();
    expect(player.calls, 4);
  });

  test('an alarm completes when the vibrator fails', () async {
    messenger.setMockMethodCallHandler(SystemChannels.platform, (_) async => throw PlatformException(code: 'no_vibrator'));
    final player = _Recording();
    await PcFeedback(PcLook(), player: player).alarm('high');
    expect(player.played, [PcSound.critical]);
  });

  test('the sound starts while the vibration pattern is still running', () async {
    messenger.setMockMethodCallHandler(SystemChannels.platform, (_) async => null);
    final player = _Recording();
    var patternDone = false;
    final done = PcFeedback(PcLook(), player: player).alarm('high').then((_) => patternDone = true);
    await Future<void>.delayed(Duration.zero);
    expect(player.played, [PcSound.critical]);
    expect(patternDone, isFalse); // three pulses take at least 240 ms
    await done;
    expect(patternDone, isTrue);
  });

  test('sounds off: no sound, no error', () async {
    messenger.setMockMethodCallHandler(SystemChannels.platform, (_) async => null);
    final player = _Recording();
    await PcFeedback(PcLook(sound: false), player: player).alarm('medium');
    expect(player.played, isEmpty);
  });
}
