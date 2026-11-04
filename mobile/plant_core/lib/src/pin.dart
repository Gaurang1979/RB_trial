import 'dart:convert';
import 'dart:isolate';
import 'dart:math';

import 'package:crypto/crypto.dart';
import 'package:flutter/foundation.dart';

/// A PIN as it is kept: a salted PBKDF2-HMAC-SHA256 hash, never the digits
/// (requirements PT11-07). It sits in the platform's secure store next to
/// the session it unlocks. A short PIN cannot stand up to an offline search
/// on its own; what guards it is that store, the 5-try limit and the idle
/// sign-out (PcServers).
@immutable
class PcPinHash {
  const PcPinHash({required this.salt, required this.hash, required this.rounds});

  /// Rounds for a new PIN: about a fifth of a second on a mid-range phone.
  static const defaultRounds = 30000;

  final Uint8List salt, hash;
  final int rounds;

  /// Hashes [pin] with a new random salt.
  factory PcPinHash.make(String pin, {int rounds = defaultRounds, Random? random}) {
    final r = random ?? Random.secure();
    final salt = Uint8List.fromList(List.generate(16, (_) => r.nextInt(256)));
    return PcPinHash(salt: salt, hash: _pbkdf2(pin, salt, rounds), rounds: rounds);
  }

  /// Whether [pin] is the PIN this was made from (compared in constant time).
  bool matches(String pin) {
    final got = _pbkdf2(pin, salt, rounds);
    if (got.length != hash.length) return false;
    var diff = 0;
    for (var i = 0; i < got.length; i++) {
      diff |= got[i] ^ hash[i];
    }
    return diff == 0;
  }

  /// [matches] on another isolate, so the hash does not hold up the UI. The
  /// salt, hash and rounds are copied across; the PIN is not kept there.
  Future<bool> matchesAsync(String pin) {
    final salt = this.salt, hash = this.hash, rounds = this.rounds;
    return Isolate.run(() => PcPinHash(salt: salt, hash: hash, rounds: rounds).matches(pin));
  }

  Map<String, Object> toJson() => {'salt': base64Encode(salt), 'hash': base64Encode(hash), 'rounds': rounds};

  /// Null for anything that is not a whole hash.
  static PcPinHash? fromJson(Object? j) {
    if (j is! Map) return null;
    final salt = j['salt'], hash = j['hash'], rounds = j['rounds'];
    if (salt is! String || hash is! String || rounds is! int || rounds < 1) return null;
    try {
      final s = base64Decode(salt), h = base64Decode(hash);
      if (s.length < 16 || h.length != 32) return null;
      return PcPinHash(salt: s, hash: h, rounds: rounds);
    } on FormatException {
      return null;
    }
  }

  // PBKDF2 (RFC 8018) with one 32-byte block, which is all a PIN check needs.
  static Uint8List _pbkdf2(String pin, Uint8List salt, int rounds) {
    final mac = Hmac(sha256, utf8.encode(pin));
    var u = mac.convert([...salt, 0, 0, 0, 1]).bytes;
    final out = Uint8List.fromList(u);
    for (var i = 1; i < rounds; i++) {
      u = mac.convert(u).bytes;
      for (var k = 0; k < out.length; k++) {
        out[k] ^= u[k];
      }
    }
    return out;
  }
}

/// PINs are 4 to 8 digits.
const pcPinMin = 4, pcPinMax = 8;

/// Why [pin] cannot be used, in words to show; null when it can. Refused:
/// other than 4 to 8 digits, one digit repeated (0000) and a straight run
/// up or down (1234, 9876), the PINs tried first.
String? pcPinProblem(String pin) {
  if (pin.length < pcPinMin || pin.length > pcPinMax || !RegExp(r'^[0-9]+$').hasMatch(pin)) return 'A PIN is $pcPinMin to $pcPinMax digits.';
  final d = pin.codeUnits;
  bool all(int step) => [for (var i = 1; i < d.length; i++) d[i] - d[i - 1]].every((x) => x == step);
  if (all(0) || all(1) || all(-1)) return 'Choose a PIN that is harder to guess.';
  return null;
}
