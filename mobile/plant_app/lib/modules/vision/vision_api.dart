import 'dart:convert';

import 'package:http/http.dart' as http;

import 'api_auth.dart';

/// One NG part from the server's live state. [resultId] is the part's id in
/// the NG review (null when the server has none for it); it opens the part.
class NgItem {
  NgItem(this.ts, this.station, this.tool, this.reason, {this.camera, this.resultId});
  final int ts;
  final String station, tool, reason;
  final String? camera;
  final int? resultId;
}

class OpenAlarm {
  OpenAlarm(this.plant, this.line, this.station, this.code, this.priority, this.since, this.acknowledgedBy, {this.title, this.fix, this.stationName});
  final String plant, line, station, code, priority;
  final int since;
  final String? acknowledgedBy;

  /// The server's alarm catalogue text and the station's device name; null from a server that does not send them.
  final String? title, fix, stationName;

  /// Names this alarm across the floor: station names repeat between lines and plants.
  String get key => '$plant/$line/$station/$code/$since';
}

/// The floor as last read from the server and when, so a screen that
/// follows it can tell how old what it shows is.
class FloorSnapshot {
  FloorSnapshot(this.lines, this.at);
  final List<LineLive> lines;
  final DateTime at;
}

/// A line as the plant server's Vision module shows it at /vision/floor (snake_case JSON).
class LineLive {
  LineLive({
    required this.plant, required this.line, required this.state, this.stateReason, this.job,
    required this.good, required this.bad, required this.total, this.yieldPct, required this.partsPerMinute,
    this.topNg, required this.lastNg, required this.alarms, required this.stationsOnline, required this.stations,
  });

  final String plant, line, state;
  final String? stateReason, job, topNg;
  final int good, bad, total, partsPerMinute, stationsOnline, stations;
  final double? yieldPct;
  final List<NgItem> lastNg;
  final List<OpenAlarm> alarms;

  static int _i(Object? v) => v is num && v.isFinite ? v.toInt() : 0;
  static String? _s(Object? v) => v is String ? v : null;

  /// Reads one line; fields that are missing or of the wrong type fall
  /// back to empty values instead of failing the whole floor.
  factory LineLive.fromJson(Map<String, dynamic> j) {
    // `is List`, not `as List?`: a field of the wrong type is empty, not an exception.
    final stations = j['stations'], lastNg = j['last_ng'], openAlarms = j['alarms'];
    final st = stations is List ? stations.whereType<Map>().toList() : const <Map>[];
    final plant = '${j['plant'] ?? ''}', line = '${j['line'] ?? ''}';
    return LineLive(
      plant: plant, line: line, state: '${j['state'] ?? 'unknown'}',
      stateReason: _s(j['state_reason']), job: _s(j['job']), topNg: _s(j['top_ng']),
      good: _i(j['good']), bad: _i(j['bad']), total: _i(j['total']), partsPerMinute: _i(j['parts_per_minute']),
      yieldPct: j['yield_pct'] is num && (j['yield_pct'] as num).isFinite ? (j['yield_pct'] as num).toDouble() : null,
      lastNg: [
        for (final n in lastNg is List ? lastNg.whereType<Map>() : const <Map>[])
          NgItem(_i(n['ts']), '${n['station'] ?? ''}', '${n['tool'] ?? ''}', '${n['reason'] ?? ''}',
              camera: _s(n['camera']), resultId: n['result_id'] is int ? n['result_id'] as int : null),
      ],
      alarms: [
        for (final a in openAlarms is List ? openAlarms.whereType<Map>() : const <Map>[])
          OpenAlarm(plant, line, '${a['station'] ?? ''}', '${a['code'] ?? ''}', '${a['priority'] ?? 'low'}', _i(a['since']), _s(a['acknowledged_by']),
              title: _s(a['title']), fix: _s(a['fix']), stationName: _s(a['station_name'])),
      ],
      stations: st.length,
      stationsOnline: st.where((s) => s['connected'] == true).length,
    );
  }
}

class VisionApiException implements Exception {
  VisionApiException(this.message);
  final String message;
  @override
  String toString() => message;
}

/// The plant server's read API. [auth] says who is asking (a signed-in
/// person, else the key); without one the key is used.
class VisionApi {
  /// [base] keeps its path (`https://host/plant`): it gets a trailing
  /// slash so that relative paths resolve below it, not at the host root.
  VisionApi(this.client, Uri base, String? key, {ApiAuth? auth})
      : auth = auth ?? ApiAuth(client, base, key: key),
        base = base.path.endsWith('/') ? base : base.replace(path: '${base.path}/');
  final http.Client client;
  final Uri base;
  final ApiAuth auth;

  String? get key => auth.key;

  Future<List<LineLive>> floor() async {
    final sent = auth.headers;
    final r = await client.get(base.resolve('vision/floor'), headers: sent).timeout(const Duration(seconds: 15));
    if (r.statusCode == 401) throw VisionApiException(auth.onUnauthorized(sent));
    if (r.statusCode != 200) throw VisionApiException('The plant server answered ${r.statusCode}.');
    try {
      final j = jsonDecode(utf8.decode(r.bodyBytes));
      if (j is List) return _lines(j);
    } on FormatException {
      // Not JSON: reported below like any other unexpected answer.
    } on TypeError {
      // A shape the reader did not expect: the same.
    }
    throw VisionApiException('The plant server sent something unexpected.');
  }

  static List<LineLive> _lines(List j) => [for (final l in j.whereType<Map<String, dynamic>>()) LineLive.fromJson(l)];

  /// The live floor's socket (`WS /vision/ws`, requirements 17.3 LV-01, LV-05)
  /// with what it carries: the session while one is open, else the key, as
  /// [headers] of a call would. The server reads them from the query only.
  /// `wss` for an `https` server, below [base] like every other path.
  Uri liveUrl() {
    final u = base.resolve('vision/ws');
    final token = auth.session?.token;
    return u.replace(scheme: u.scheme == 'https' ? 'wss' : 'ws', queryParameters: {
      'lang': 'en',
      if (token != null) 'token': token else 'api_key': ?key,
    });
  }

  /// The headers a call with the credential of [url] (from [liveUrl]) would
  /// have sent, for [ApiAuth.onUnauthorized] when the socket is closed with 4401.
  static Map<String, String> sentWith(Uri url) {
    final token = url.queryParameters['token'];
    return token != null ? {'Authorization': 'Bearer $token'} : {'X-Api-Key': ?url.queryParameters['api_key']};
  }

  /// The lines of one socket message (`{"type": "floor", "lines": [...]}`);
  /// null for any other message or one that cannot be read.
  static List<LineLive>? pushedFloor(String message) {
    try {
      final j = jsonDecode(message);
      if (j is Map && j['type'] == 'floor' && j['lines'] is List) return _lines(j['lines'] as List);
    } on FormatException {
      // Not JSON: not a floor.
    } on TypeError {
      // Not a shape the reader expects: the same.
    }
    return null;
  }
}
