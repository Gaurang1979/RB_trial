import 'dart:async';
import 'dart:convert';

import 'package:flutter/widgets.dart';

import 'cert_trust.dart';
import 'look.dart';
import 'pin.dart';
import 'pin_events.dart';

/// The products the app can show. Each has its own server and module
/// (docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md 6.1).
enum PcProduct {
  /// Plant Insight: dotnet/PlantServer in the Vision_System repository.
  vision,
  /// Production Measurement: the production_measurement backend (later).
  production,
}

/// A server the person added: which product, a name they recognise, its
/// address and, for a server with a private or self-signed certificate, that
/// certificate's SHA-256 [fingerprint] (64 lowercase hex digits; see
/// [PcCertTrust]). The sign-in token is kept apart, in the secret store.
@immutable
class PcServer {
  const PcServer({required this.id, required this.product, required this.name, required this.baseUrl, this.fingerprint});

  final String id;
  final PcProduct product;
  final String name;
  final Uri baseUrl;
  final String? fingerprint;

  Map<String, Object> toJson() =>
      {'id': id, 'product': product.name, 'name': name, 'base_url': baseUrl.toString(), 'fingerprint': ?fingerprint};

  /// Null for an entry this version cannot use (an unknown product, a bad address).
  /// A damaged fingerprint is dropped: that server's private certificate then fails, never passes.
  static PcServer? fromJson(Object? j) {
    if (j is! Map) return null;
    final product = PcProduct.values.where((p) => p.name == j['product']).firstOrNull;
    final url = Uri.tryParse('${j['base_url']}');
    final id = j['id'], name = j['name'], fp = j['fingerprint'];
    if (product == null || url == null || !url.hasScheme || id is! String || name is! String) return null;
    return PcServer(id: id, product: product, name: name, baseUrl: url, fingerprint: fp is String ? pcFingerprint(fp) : null);
  }
}

/// What a product module gives the app: its name, icon and home screen for
/// one of its servers. The app lists one entry per added server.
abstract class PcModule {
  PcProduct get product;
  String get title;
  IconData get icon;

  /// Whether this version of the app can open the product yet.
  bool get available => true;

  Widget home(BuildContext context, PcServer server, String? token);

  /// Ends [session] on [server], for a sign-in the app dropped by itself
  /// (idle too long, too many wrong PINs). Best effort: the session runs
  /// out on the server anyway.
  Future<void> revoke(PcServer server, PcSession session) async {}
}

/// A person's sign-in on one server: the session token the server gave, the
/// name to show, when it ends (as the server wrote it), the login name it
/// is kept under ([user]), the hash of the PIN that unlocks it on this phone,
/// when it was last used and how many wrong PINs were typed since. The
/// password and the PIN digits are never kept.
@immutable
class PcSession {
  const PcSession({required this.token, required this.name, this.expires, this.user = '', this.pin, this.lastActive, this.wrongPins = 0});

  final String token, name, user;
  final String? expires;
  final PcPinHash? pin;
  final DateTime? lastActive;
  final int wrongPins;

  PcSession copyWith({DateTime? lastActive, int? wrongPins}) =>
      PcSession(token: token, name: name, expires: expires, user: user, pin: pin, lastActive: lastActive ?? this.lastActive, wrongPins: wrongPins ?? this.wrongPins);

  Map<String, Object?> toMap() => {
        'token': token,
        'name': name,
        'expires': expires,
        'user': user,
        if (pin != null) 'pin': pin!.toJson(),
        if (lastActive != null) 'last_active': lastActive!.toUtc().toIso8601String(),
        'wrong_pins': wrongPins,
      };

  String toJson() => jsonEncode(toMap());

  /// Null for anything that is not a usable session (empty, damaged, no token).
  static PcSession? parse(String? raw) {
    if (raw == null || raw.isEmpty) return null;
    try {
      return fromMap(jsonDecode(raw));
    } on FormatException {
      return null;
    }
  }

  static PcSession? fromMap(Object? j) {
    if (j is! Map) return null;
    final token = j['token'], name = j['name'], expires = j['expires'], user = j['user'], wrong = j['wrong_pins'], last = j['last_active'];
    if (token is! String || token.isEmpty) return null;
    return PcSession(
      token: token,
      name: name is String ? name : '',
      expires: expires is String ? expires : null,
      user: user is String ? user : '',
      pin: PcPinHash.fromJson(j['pin']),
      lastActive: last is String ? DateTime.tryParse(last) : null,
      wrongPins: wrong is int && wrong > 0 ? wrong : 0,
    );
  }
}

/// What came of typing a PIN.
enum PcUnlock {
  /// The PIN was right: the person's sign-in is open.
  open,
  /// Wrong PIN; [PcServers.triesLeft] says how many are left.
  wrong,
  /// The last try was wrong: the sign-in is gone, the password is needed.
  signedOut,
  /// Unused for longer than [PcServers.signOutAfter] (or the clock went
  /// back): the sign-in is gone, the password is needed.
  expired,
  /// No such person is signed in on this phone (any more).
  gone,
}

/// The person's servers, kept in the settings store; tokens in the secret
/// store under `token.<server id>`, never in the settings.
///
/// Sign-ins on a shared floor phone (PLANT-DASHBOARD-AND-MOBILE.md 6): each
/// person signs in once with their password and chooses a PIN; the session
/// is kept in the secret store under `signins.<server id>`, one entry per
/// person, and only one sign-in is open on the whole phone at a time. The
/// phone locks after [lockAfter] without a touch and whenever the app is
/// hidden; the person, or the next one, opens their own sign-in with their
/// PIN. [maxWrongPins] wrong PINs in a row, or [signOutAfter] without use
/// (requirements PT11-06), end that sign-in: the password is needed again
/// and [onDropped] tells the server. A sign-in without a PIN is never kept.
class PcServers extends ChangeNotifier {
  PcServers(this._settings, this._secrets, {DateTime Function()? now}) : _now = now ?? DateTime.now;

  static const lockAfter = Duration(minutes: 2), signOutAfter = Duration(minutes: 15);
  static const maxWrongPins = 5;

  final KeyValueStore _settings, _secrets;
  final DateTime Function() _now;
  static const _key = 'servers';
  List<PcServer> _list = const [];

  /// Everyone signed in per server (open or locked), by login name.
  final _people = <String, List<PcSession>>{};

  /// The one open sign-in: server id and login name.
  ({String server, String user})? _open;
  DateTime _lastTouch = DateTime.fromMillisecondsSinceEpoch(0);

  /// Sign-ins read at [load] in the old one-per-server form, without a PIN:
  /// they are dropped at the next [tick], when [onDropped] can tell the server.
  final _legacy = <(String, PcSession)>[];

  /// Called for a sign-in the app dropped by itself, so that the server can
  /// end it too. Not called for a sign-out or an ended session.
  void Function(String serverId, PcSession session)? onDropped;

  /// PIN unlocks, wrong PINs and lockouts waiting to be reported to the plant
  /// server (requirements PT11-08a); the app sets how they are sent.
  late final PcPinReports reports = PcPinReports(_secrets)..onEnded = endedByServer;

  /// This phone's name for the audit trail, sent at sign-in and with each report.
  String? device;

  final _ended = <String>{};

  /// Whether a sign-in on server [id] was ended by the server (a 401 to a
  /// PIN report) and nobody has signed in since.
  bool endedOn(String id) => _ended.contains(id);

  List<PcServer> get list => _list;

  void _report(String id, PcSession p, PcPinEvent e) {
    // Reporting is on its own: an unlock or a lockout never waits for it.
    unawaited(reports.add(PcPinReport(serverId: id, token: p.token, event: e, device: device, at: _now().toUtc())).catchError((_) {}));
  }

  /// The server answered 401 to a report for [token]: that sign-in is over,
  /// on the phone too, and the password is needed again.
  Future<void> endedByServer(String id, String token) async {
    final p = _people[id]?.where((x) => x.token == token).firstOrNull;
    if (p == null) return;
    _ended.add(id);
    await clearSession(id, p.user);
  }

  Future<void> load() async {
    final raw = await _settings.read(_key);
    var items = const <PcServer>[];
    if (raw != null) {
      try {
        final j = jsonDecode(raw);
        if (j is List) items = j.map(PcServer.fromJson).whereType<PcServer>().toList();
      } on FormatException {
        // A damaged list is treated as empty rather than stopping the app.
      }
    }
    _list = List.unmodifiable(items);
    await reports.load();
    _people.clear();
    _open = null;
    for (final x in items) {
      try {
        final raw = await _secrets.read('signins.${x.id}');
        final j = raw == null || raw.isEmpty ? null : jsonDecode(raw);
        final people = j is List ? j.map(PcSession.fromMap).whereType<PcSession>().where((p) => p.pin != null && p.user.isNotEmpty).toList() : <PcSession>[];
        if (people.isNotEmpty) _people[x.id] = people;
      } catch (_) {
        // Unreadable sign-ins are no sign-ins: each person signs in again.
      }
      try {
        final old = PcSession.parse(await _secrets.read('session.${x.id}'));
        if (old != null) {
          _legacy.add((x.id, old));
          await _secrets.write('session.${x.id}', '');
        }
      } catch (_) {}
    }
    notifyListeners();
  }

  /// The open sign-in on server [id], if it has one. Locked sign-ins are not
  /// returned: calls then go with the server's key, and no verdicts.
  PcSession? session(String id) {
    final o = _open;
    if (o == null || o.server != id) return null;
    return _people[id]?.where((p) => p.user == o.user).firstOrNull;
  }

  /// Everyone signed in on server [id] on this phone, open or locked.
  List<PcSession> people(String id) => List.unmodifiable(_people[id] ?? const <PcSession>[]);

  /// Wrong PINs [user] may still type on server [id].
  int triesLeft(String id, String user) => maxWrongPins - (_find(id, user)?.wrongPins ?? 0);

  PcSession? _find(String id, String user) => _people[id]?.where((p) => p.user == user).firstOrNull;

  /// Keeps a new sign-in (replacing that person's older one) and opens it,
  /// locking any other. It must carry a login name and a PIN.
  Future<void> setSession(String id, PcSession s) async {
    if (s.pin == null || s.user.isEmpty) throw ArgumentError('a kept sign-in needs a login name and a PIN');
    _closeOpen();
    final t = _now();
    final before = _people[id];
    final replaced = before?.where((p) => p.user == s.user && p.token != s.token).firstOrNull;
    _people[id] = [...?before?.where((p) => p.user != s.user), s.copyWith(lastActive: t, wrongPins: 0)];
    try {
      await _save(id);
    } catch (_) {
      // Not kept, so not opened: the caller ends it on the server.
      if (before == null) {
        _people.remove(id);
      } else {
        _people[id] = before;
      }
      notifyListeners();
      rethrow;
    }
    _open = (server: id, user: s.user);
    _ended.remove(id);
    _lastTouch = t;
    notifyListeners();
    // The person's older sign-in is ended on the server too.
    if (replaced != null) onDropped?.call(id, replaced);
  }

  /// Forgets [user]'s sign-in on server [id], open or locked (signed out,
  /// or ended on the server); without [user], the open one.
  Future<void> clearSession(String id, [String? user]) async {
    final who = user ?? session(id)?.user;
    if (who == null || _find(id, who) == null) return;
    if (_open?.server == id && _open?.user == who) _open = null;
    _people[id]!.removeWhere((p) => p.user == who);
    notifyListeners();
    await _save(id);
  }

  /// Opens [user]'s sign-in on server [id] with [pin], locking any other.
  Future<PcUnlock> unlock(String id, String user, String pin) async {
    final p = _find(id, user);
    if (p == null) return PcUnlock.gone;
    if (_expired(p, _now())) {
      await _drop(id, p);
      return PcUnlock.expired;
    }
    // The wrong try is counted before the hash is awaited, so a phone killed
    // mid-check cannot skip it; a right PIN gives the try back.
    final wrong = p.wrongPins + 1;
    if (wrong >= maxWrongPins) {
      // Last try: the hash decides whether this is a drop or an unlock.
      if (!await p.pin!.matchesAsync(pin)) {
        _report(id, p, PcPinEvent.lockout);
        await _drop(id, p);
        return PcUnlock.signedOut;
      }
    } else {
      _replace(id, p.copyWith(wrongPins: wrong));
      await _save(id);
      if (!await p.pin!.matchesAsync(pin)) {
        _report(id, p, PcPinEvent.wrong);
        notifyListeners();
        return PcUnlock.wrong;
      }
    }
    // Ended while the hash ran (signed out, dropped): nothing to open.
    final cur = _find(id, user);
    if (cur == null) return PcUnlock.gone;
    _closeOpen();
    final t = _now();
    _replace(id, cur.copyWith(lastActive: t, wrongPins: 0));
    _open = (server: id, user: user);
    _ended.remove(id);
    _lastTouch = t;
    notifyListeners();
    _report(id, cur, PcPinEvent.unlock);
    await _save(id);
    return PcUnlock.open;
  }

  /// Locks the open sign-in, if any (the Lock button, the app going to the
  /// background, idle time).
  Future<void> lock() async {
    final id = _closeOpen();
    if (id == null) return;
    notifyListeners();
    await _save(id);
  }

  /// A touch anywhere in the app: the open sign-in is in use.
  void touch() => _lastTouch = _now();

  /// Run every few seconds: locks after [lockAfter] without a touch, and
  /// drops every sign-in unused for [signOutAfter].
  Future<void> tick() async {
    // Timer ticks do not wait for each other: one at a time.
    if (_ticking) return;
    _ticking = true;
    try {
      await _tick();
    } finally {
      _ticking = false;
    }
  }

  bool _ticking = false;

  Future<void> _tick() async {
    final t = _now();
    // Reports that could not be sent earlier (offline) go out again.
    if (reports.pending.isNotEmpty) unawaited(reports.flush());
    for (final (id, s) in List.of(_legacy)) {
      onDropped?.call(id, s);
    }
    _legacy.clear();
    if (_open != null && !t.difference(_lastTouch).isNegative && t.difference(_lastTouch) >= lockAfter) await lock();
    for (final id in List.of(_people.keys)) {
      for (final p in List.of(_people[id] ?? const <PcSession>[])) {
        final open = _open?.server == id && _open?.user == p.user;
        if (_expired(open ? p.copyWith(lastActive: _lastTouch) : p, t)) await _drop(id, p);
      }
    }
  }

  // Unused too long, or a clock that went back more than a minute: in doubt, signed out.
  bool _expired(PcSession p, DateTime t) {
    final last = p.lastActive;
    if (last == null) return true;
    final d = t.difference(last);
    return d >= signOutAfter || d < const Duration(minutes: -1);
  }

  /// Locks the open sign-in in memory, noting when it was last used; the
  /// server id to save, or null when none was open.
  String? _closeOpen() {
    final o = _open;
    if (o == null) return null;
    _open = null;
    final p = _find(o.server, o.user);
    if (p != null) _replace(o.server, p.copyWith(lastActive: _lastTouch));
    return o.server;
  }

  void _replace(String id, PcSession p) => _people[id] = [for (final x in _people[id]!) x.user == p.user ? p : x];

  Future<void> _drop(String id, PcSession p) async {
    if (_open?.server == id && _open?.user == p.user) _open = null;
    _people[id]?.removeWhere((x) => x.user == p.user);
    notifyListeners();
    onDropped?.call(id, p);
    await _save(id);
  }

  Future<void> _save(String id) async {
    final people = _people[id] ?? const <PcSession>[];
    if (people.isEmpty) _people.remove(id);
    await _secrets.write('signins.$id', people.isEmpty ? '' : jsonEncode(people.map((p) => p.toMap()).toList()));
  }

  Future<void> add(PcServer s, {String? token}) async {
    _list = List.unmodifiable([..._list.where((x) => x.id != s.id), s]);
    await _settings.write(_key, jsonEncode(_list.map((x) => x.toJson()).toList()));
    if (token != null) await _secrets.write('token.${s.id}', token);
    notifyListeners();
  }

  /// Removes the server with everyone's sign-in on it (each is ended on the server).
  Future<void> remove(String id) async {
    // Dropped while the server is still listed, so that [onDropped] can reach it.
    for (final p in people(id)) {
      await _drop(id, p);
    }
    _list = List.unmodifiable(_list.where((x) => x.id != id));
    await _settings.write(_key, jsonEncode(_list.map((x) => x.toJson()).toList()));
    await _secrets.write('token.$id', '');
    await _secrets.write('signins.$id', '');
    notifyListeners();
  }

  Future<String?> token(String id) async {
    final t = await _secrets.read('token.$id');
    return t == null || t.isEmpty ? null : t;
  }
}
