import 'package:flutter/material.dart';

/// Where the app keeps small settings (secure storage in the app, a map
/// in tests).
abstract interface class KeyValueStore {
  Future<String?> read(String key);
  Future<void> write(String key, String value);
}

class MemoryStore implements KeyValueStore {
  final Map<String, String> values = {};
  @override
  Future<String?> read(String key) async => values[key];
  @override
  Future<void> write(String key, String value) async => values[key] = value;
}

/// The look the person chose: dark (the default), light or the phone's
/// own setting, and whether the app makes sounds (on from install).
/// One for the whole app; every module follows it.
class PcLook extends ChangeNotifier {
  PcLook({ThemeMode mode = ThemeMode.dark, bool sound = true})
      : _mode = mode,
        _sound = sound;

  static const _modeKey = 'look.theme', _soundKey = 'look.sound';

  KeyValueStore? _store;
  ThemeMode _mode;
  bool _sound;

  ThemeMode get mode => _mode;
  bool get sound => _sound;

  /// Reads the saved choices; anything missing or unknown keeps the default.
  Future<void> load(KeyValueStore store) async {
    _store = store;
    final m = await store.read(_modeKey), s = await store.read(_soundKey);
    _mode = switch (m) { 'light' => ThemeMode.light, 'system' => ThemeMode.system, _ => ThemeMode.dark };
    _sound = s != 'off';
    notifyListeners();
  }

  Future<void> setMode(ThemeMode m) async {
    if (m == _mode) return;
    _mode = m;
    notifyListeners();
    await _store?.write(_modeKey, switch (m) { ThemeMode.light => 'light', ThemeMode.system => 'system', _ => 'dark' });
  }

  Future<void> setSound(bool on) async {
    if (on == _sound) return;
    _sound = on;
    notifyListeners();
    await _store?.write(_soundKey, on ? 'on' : 'off');
  }
}
