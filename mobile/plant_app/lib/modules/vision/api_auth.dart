import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;
import 'package:plant_core/plant_core.dart';

/// Why a sign-in or sign-out could not be done, in words to show.
class AuthException implements Exception {
  AuthException(this.message);
  final String message;
  @override
  String toString() => message;
}

/// How one plant server is told who is asking. While a person's sign-in is
/// open every call carries their session (`Authorization: Bearer`), so
/// access follows their roles; otherwise (nobody signed in, or the phone
/// locked) it carries the server's key (`X-Api-Key`). Tokens and keys go
/// only to [base], the server's own address: every path is resolved below
/// it. The password is used for one call and never kept.
///
/// With [servers] (the app) the sign-ins live there, one per person, each
/// opened with its PIN (PcServers); without it (a screen on its own, tests)
/// a sign-in lives only in this object and is never stored.
class ApiAuth extends ChangeNotifier {
  /// [base] keeps its path (`https://host/plant`): it gets a trailing slash
  /// so that relative paths resolve below it, not at the host root.
  ApiAuth(this.client, Uri base, {this.key, PcSession? session, this.servers, this.serverId})
      : assert(servers == null || serverId != null),
        base = base.path.endsWith('/') ? base : base.replace(path: '${base.path}/'),
        _session = session {
    servers?.addListener(_changed);
  }

  static const endedMessage = 'Your sign-in has ended — sign in again.';
  static const keyRefused = 'The key was not accepted. Ask your plant administrator.';

  final http.Client client;
  final Uri base;
  final String? key;

  /// Where the sign-ins are kept, and which server this is there.
  final PcServers? servers;
  final String? serverId;

  PcSession? _session;
  bool _ended = false, _disposed = false;

  @override
  void dispose() {
    servers?.removeListener(_changed);
    _disposed = true;
    super.dispose();
  }

  // A call that ends after its screen is gone must not notify a disposed object.
  void _changed() {
    if (!_disposed) notifyListeners();
  }

  PcSession? get session => servers != null ? servers!.session(serverId!) : _session;
  bool get signedIn => session != null;

  /// True from the moment a session was found to have ended until the next sign-in.
  bool get ended => _ended || (servers?.endedOn(serverId!) ?? false);

  /// The headers for one call: the session when signed in, else the key.
  Map<String, String> get headers {
    final s = session;
    return s != null ? {'Authorization': 'Bearer ${s.token}'} : {'X-Api-Key': ?key};
  }

  /// Whether a PIN is chosen at sign-in and opens a kept sign-in.
  bool get usesPin => servers != null;

  /// A 401 to a call that sent [sent]: when it carried a session, that
  /// session has ended (it is deleted, and reads fall back to the key);
  /// the message to show either way.
  String onUnauthorized(Map<String, String> sent) {
    final bearer = sent['Authorization'];
    if (bearer == null) return keyRefused;
    // Only the session that was refused is dropped: a newer sign-in stays.
    final s = session;
    if (s != null && 'Bearer ${s.token}' == bearer) {
      _ended = true;
      _clear(s);
      _changed();
    }
    return endedMessage;
  }

  // Forgets the sign-in [s]. Storage that fails must not break the sign-out itself.
  Future<void> _clear(PcSession s) async {
    _session = null;
    try {
      await servers?.clearSession(serverId!, s.user);
    } catch (_) {}
  }

  /// Signs in with a local account. Nothing is kept unless the server
  /// accepts it. With [servers] a [pin] is needed: it is checked before
  /// anything is sent and only its hash is kept.
  Future<void> login(String username, String password, {String? pin}) async {
    if (usesPin) {
      final problem = pin == null ? 'Choose a PIN.' : pcPinProblem(pin);
      if (problem != null) throw AuthException(problem);
    }
    final http.Response r;
    try {
      r = await client
          .post(base.resolve('auth/login'), headers: {'Content-Type': 'application/json'}, body: jsonEncode({'username': username.trim(), 'password': password, 'device': ?servers?.device}))
          .timeout(const Duration(seconds: 15));
    } catch (_) {
      throw AuthException('Cannot reach the plant server.');
    }
    if (r.statusCode == 401) throw AuthException('Wrong user name or password.');
    if (r.statusCode == 429) throw AuthException('Too many wrong passwords. Try again in 15 minutes.');
    if (r.statusCode != 200) throw AuthException('The plant server answered ${r.statusCode}.');
    PcSession? s;
    try {
      final j = jsonDecode(utf8.decode(r.bodyBytes));
      if (j is Map && j['token'] is String && (j['token'] as String).isNotEmpty) {
        final user = j['user'];
        String? text(Object? v) => v is String && v.isNotEmpty ? v : null;
        final u = user is Map ? user : const {};
        s = PcSession(
          token: j['token'] as String,
          name: text(u['name']) ?? text(u['username']) ?? username.trim(),
          expires: text(j['expires_at']),
          // Login names are case-sensitive on the server: kept as it wrote it.
          user: text(u['username']) ?? username.trim(),
        );
      }
    } on FormatException {
      // Not JSON: reported below like any other unexpected answer.
    }
    if (s == null) throw AuthException('The plant server sent something unexpected.');
    _ended = false;
    if (servers == null) {
      _session = s;
    } else {
      final kept = PcSession(token: s.token, name: s.name, expires: s.expires, user: s.user, pin: PcPinHash.make(pin!));
      try {
        await servers!.setSession(serverId!, kept);
      } catch (_) {
        // Not kept, so not usable: end it on the server rather than leave it open.
        await revoke(client, base, s.token);
        throw AuthException('This phone could not keep the sign-in. Try again.');
      }
    }
    _changed();
  }

  /// Signs out: tells the server, then deletes the session here even when
  /// that call failed (the session then simply runs out on the server).
  Future<void> logout() async {
    final s = session;
    if (s == null) return;
    // Forgotten here first: a lock or a slow server must not leave it kept.
    _ended = false;
    await _clear(s);
    _changed();
    await revoke(client, base, s.token);
  }

  /// Opens [user]'s kept sign-in with [pin] (see PcServers.unlock).
  Future<PcUnlock> unlock(String user, String pin) async {
    final r = await servers!.unlock(serverId!, user, pin);
    if (r == PcUnlock.open && _ended) {
      _ended = false;
      _changed();
    }
    return r;
  }

  /// Ends [token] on the server at [base]. Offline or refused: the session
  /// then simply runs out on the server.
  static Future<void> revoke(http.Client client, Uri base, String token) async {
    final below = base.path.endsWith('/') ? base : base.replace(path: '${base.path}/');
    try {
      await client.post(below.resolve('auth/logout'), headers: {'Authorization': 'Bearer $token'}).timeout(const Duration(seconds: 10));
    } catch (_) {}
  }
}
