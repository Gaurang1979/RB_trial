import 'dart:io' show Platform;

import 'package:flutter/material.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:plant_core/plant_core.dart';

import 'src/app.dart';

/// Settings and tokens both live in the platform's secure storage: small,
/// and the tokens must never be in plain files.
class SecureStore implements KeyValueStore {
  const SecureStore();
  // Android: the package's own AES-GCM key store (v10+; Jetpack EncryptedSharedPreferences is gone in v11).
  // resetOnError: a key the system lost gives an empty store (sign in again), never a crash.
  static const _s = FlutterSecureStorage(aOptions: AndroidOptions(resetOnError: true));
  @override
  Future<String?> read(String key) => _s.read(key: key);
  @override
  Future<void> write(String key, String value) => _s.write(key: key, value: value);
}

String _system() => switch (Platform.operatingSystem) { 'android' => 'Android', 'ios' => 'iOS', final o => o };

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  const store = SecureStore();
  final look = PcLook();
  // A storage failure (a keystore that was reset, say) must not stop the app: start with the defaults.
  try {
    await look.load(store);
  } catch (_) {}
  final servers = PcServers(store, store);
  servers.device = await pcDeviceName(store, _system());
  try {
    await servers.load();
  } catch (_) {}
  final feedback = PcFeedback(look, player: PcAudioPlayer());
  runApp(PlantApp(look: look, servers: servers, feedback: feedback));
}
