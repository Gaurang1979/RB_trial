import 'dart:io';

import 'package:crypto/crypto.dart';

import 'modules.dart';

/// [v] as a SHA-256 certificate fingerprint: 64 lowercase hex digits, with
/// the colons, spaces and case people copy it with taken out. Null when it
/// is not one.
String? pcFingerprint(String? v) {
  final s = (v ?? '').replaceAll(RegExp(r'[\s:]'), '').toLowerCase();
  return RegExp(r'^[0-9a-f]{64}$').hasMatch(s) ? s : null;
}

/// Null when [v] is empty or a usable SHA-256 fingerprint; otherwise what to fix.
String? pcFingerprintProblem(String? v) {
  if ((v ?? '').trim().isEmpty) return null;
  return pcFingerprint(v) == null ? 'A SHA-256 fingerprint is 64 hex digits (0-9, A-F); colons are fine' : null;
}

/// The fingerprint [fp] (as [pcFingerprint] keeps it) the way certificate
/// viewers show it: uppercase pairs with colons.
String pcFingerprintColons(String fp) =>
    [for (var i = 0; i + 1 < fp.length; i += 2) fp.substring(i, i + 2)].join(':').toUpperCase();

/// [fp] shortened to its first and last four bytes, enough to tell two
/// certificates apart at a glance (requirements 17.3, MOB-TLS-05).
String pcFingerprintShort(String fp) {
  final pairs = pcFingerprintColons(fp).split(':');
  return pairs.length <= 8 ? pairs.join(':') : '${pairs.take(4).join(':')} … ${pairs.skip(pairs.length - 4).join(':')}';
}

/// Trust for plant servers with a private or self-signed certificate
/// (requirements 17.3, MOB-TLS-01..03). A certificate the phone does not
/// trust is accepted only when its SHA-256 equals the fingerprint of a
/// server added at that host and port; everything else still fails. A
/// certificate the phone trusts never reaches this check, so it works with
/// or without a fingerprint.
class PcCertTrust {
  /// [servers] is read at each connection, so servers added or removed
  /// later count at once.
  PcCertTrust(this.servers);

  final Iterable<PcServer> Function() servers;

  /// Whether the untrusted certificate [der] (its DER bytes) from [host]:[port] is pinned.
  bool accepts(List<int> der, String host, int port) {
    final h = host.toLowerCase();
    String? got;
    for (final s in servers()) {
      final want = s.fingerprint;
      if (want == null || s.baseUrl.scheme != 'https' || s.baseUrl.host.toLowerCase() != h || s.baseUrl.port != port) continue;
      got ??= sha256.convert(der).toString();
      if (got == want) return true;
    }
    return false;
  }

  /// An [HttpClient] that applies this trust; [context] for tests.
  HttpClient client({SecurityContext? context}) =>
      HttpClient(context: context)..badCertificateCallback = (cert, host, port) => accepts(cert.der, host, port);
}
