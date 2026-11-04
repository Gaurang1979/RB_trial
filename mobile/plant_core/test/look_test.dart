import 'dart:io';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plant_core/plant_core.dart';

class _Recorder implements PcSoundPlayer {
  final played = <PcSound>[];
  @override
  Future<void> play(PcSound sound) async => played.add(sound);
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  test('dark and sound on by default; choices are saved and read back', () async {
    final store = MemoryStore();
    final look = PcLook();
    await look.load(store);
    expect((look.mode, look.sound), (ThemeMode.dark, true));
    await look.setMode(ThemeMode.light);
    await look.setSound(false);
    final again = PcLook();
    await again.load(store);
    expect((again.mode, again.sound), (ThemeMode.light, false));
  });

  test('an unknown saved value keeps the default', () async {
    final store = MemoryStore()..values['look.theme'] = 'purple';
    final look = PcLook();
    await look.load(store);
    expect(look.mode, ThemeMode.dark);
  });

  test('sounds follow the setting; alarms pick their level', () async {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(SystemChannels.platform, (_) async => null);
    final look = PcLook(), rec = _Recorder();
    final fb = PcFeedback(look, player: rec);
    await fb.tap();
    await fb.alarm('high');
    await fb.alarm('medium');
    await fb.alarm('low');
    expect(rec.played, [PcSound.tick, PcSound.critical, PcSound.attention, PcSound.notice]);
    await look.setSound(false);
    await fb.confirm();
    await fb.alarm('high');
    expect(rec.played, hasLength(4));
  });

  test('every sound has its clip', () {
    for (final s in PcSound.values) {
      expect(TestAssets.exists('assets/sounds/${s.name}.wav'), isTrue, reason: s.name);
    }
  });
}

abstract final class TestAssets {
  static bool exists(String path) => File(path).existsSync();
}
