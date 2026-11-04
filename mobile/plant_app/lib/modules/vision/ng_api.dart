import 'dart:convert';
import 'dart:typed_data';

import 'package:http/http.dart' as http;

import 'api_auth.dart';
import 'vision_api.dart';

/// The verdicts a reviewer can give; [name] is what the server takes.
enum NgVerdict { confirmed, overruled, unsure }

class NgReasonItem {
  NgReasonItem(this.tool, this.reason);
  final String tool, reason;
}

/// One camera image of a part: the camera and where the server has the JPEG.
class NgImage {
  NgImage(this.camera, this.url);
  final String camera, url;
}

/// One NG part waiting for (or holding) a verdict, from /vision/ng (snake_case JSON).
class NgPart {
  NgPart({
    required this.id, required this.plant, required this.line, required this.station, required this.ts,
    required this.camera, this.partId, this.imageId, this.job, required this.reasons, this.cycleMs,
    required this.images, required this.imageState, this.verdict, this.verdictBy, this.verdictAt, this.note,
  });

  final int id, ts;
  final String plant, line, station, camera, imageState;
  final String? partId, imageId, job, verdict, verdictBy, note;
  final int? cycleMs, verdictAt;
  final List<NgReasonItem> reasons;
  final List<NgImage> images;

  static int? _n(Object? v) => v is num && v.isFinite ? v.toInt() : null;
  static String? _s(Object? v) => v is String && v.isNotEmpty ? v : null;

  /// Reads one part; null when it has no usable id (it could not be given a
  /// verdict). Other fields that are missing or of the wrong type fall back
  /// to empty values instead of failing the whole queue.
  static NgPart? fromJson(Object? j) {
    if (j is! Map) return null;
    final rawId = j['id'];
    final id = rawId is num && rawId.isFinite ? rawId.toInt() : (rawId is String ? int.tryParse(rawId) : null);
    if (id == null) return null;
    final reasons = j['reasons'], images = j['images'];
    return NgPart(
      id: id, plant: '${j['plant'] ?? ''}', line: '${j['line'] ?? ''}', station: '${j['station'] ?? ''}',
      ts: _n(j['ts']) ?? 0, camera: '${j['camera'] ?? ''}', partId: _s(j['part_id']), imageId: _s(j['image_id']), job: _s(j['job']),
      reasons: [
        for (final r in reasons is List ? reasons.whereType<Map>() : const <Map>[]) NgReasonItem('${r['tool'] ?? ''}', '${r['reason'] ?? ''}'),
      ],
      cycleMs: _n(j['cycle_ms']),
      images: [
        for (final i in images is List ? images.whereType<Map>() : const <Map>[])
          if (_s(i['url']) != null) NgImage('${i['camera'] ?? ''}', i['url'] as String),
      ],
      imageState: _s(j['image_state']) ?? 'none',
      // The server names an unreviewed part's verdict "pending".
      verdict: _s(j['verdict']) == 'pending' ? null : _s(j['verdict']),
      verdictBy: _s(j['reviewed_by']), verdictAt: _n(j['reviewed_at']), note: _s(j['note']),
    );
  }
}

/// One page of the queue; [nextBeforeId] is null on the last page.
class NgPage {
  NgPage(this.items, this.nextBeforeId);
  final List<NgPart> items;
  final int? nextBeforeId;

  factory NgPage.fromJson(Object? j) {
    if (j is! Map) throw const FormatException('not an object');
    final items = j['items'];
    if (items is! List) throw const FormatException('no items');
    final next = j['next_before_id'];
    return NgPage([for (final i in items) ?NgPart.fromJson(i)], next is num && next.isFinite ? next.toInt() : null);
  }
}

/// A failed call with a message fit to show; [code] is the HTTP status, 0 when
/// the server could not be reached.
class NgApiException extends VisionApiException {
  NgApiException(super.message, this.code);
  final int code;
}

/// The plant server's NG review calls. A verdict is only ever sent now,
/// never queued: it names a person and needs a live connection.
class NgApi {
  /// [base] keeps its path, as in [VisionApi]. [auth] says who is asking;
  /// the reads use a signed-in person's session or else the key, and a
  /// verdict only ever a person's session.
  NgApi(this.client, Uri base, String? key, {ApiAuth? auth})
      : auth = auth ?? ApiAuth(client, base, key: key),
        base = base.path.endsWith('/') ? base : base.replace(path: '${base.path}/');
  NgApi.of(VisionApi api) : this(api.client, api.base, api.key, auth: api.auth);
  final http.Client client;
  final Uri base;
  final ApiAuth auth;

  /// The longest note the server keeps with a verdict (review.MAX_NOTE).
  static const maxNote = 500;

  Future<http.Response> _send(Future<http.Response> Function() call) async {
    try {
      return await call().timeout(const Duration(seconds: 15));
    } catch (_) {
      // No answer, a timeout or a dropped connection: one plain message.
      throw NgApiException('Cannot reach the plant server.', 0);
    }
  }

  /// The parts waiting for a verdict, newest first. [beforeId] continues after a page.
  Future<NgPage> pending({String? plant, String? line, int? beforeId, int limit = 50}) async {
    final uri = base.resolve('vision/ng').replace(queryParameters: {
      'verdict': 'pending',
      'limit': '$limit',
      if (plant != null && plant.isNotEmpty) 'plant': plant,
      if (line != null && line.isNotEmpty) 'line': line,
      if (beforeId != null) 'before_id': '$beforeId',
    });
    final sent = auth.headers;
    final r = await _send(() => client.get(uri, headers: sent));
    if (r.statusCode == 401) throw NgApiException(auth.onUnauthorized(sent), 401);
    if (r.statusCode != 200) throw NgApiException('The plant server answered ${r.statusCode}.', r.statusCode);
    try {
      return NgPage.fromJson(jsonDecode(utf8.decode(r.bodyBytes)));
    } on FormatException {
      // Not JSON, or not a queue: reported below like any other unexpected answer.
    } on TypeError {
      // A shape the reader did not expect: the same.
    }
    throw NgApiException('The plant server sent something unexpected.', 200);
  }

  /// One NG part by its id, with its verdict when it has one (opened from the line view's NG strip).
  Future<NgPart> part(int id) async {
    final sent = auth.headers;
    final r = await _send(() => client.get(base.resolve('vision/ng/$id'), headers: sent));
    if (r.statusCode == 401) throw NgApiException(auth.onUnauthorized(sent), 401);
    if (r.statusCode == 404) throw NgApiException('This part is not on the plant server.', 404);
    if (r.statusCode != 200) throw NgApiException('The plant server answered ${r.statusCode}.', r.statusCode);
    try {
      final p = NgPart.fromJson(jsonDecode(utf8.decode(r.bodyBytes)));
      if (p != null && p.id == id) return p;
    } on FormatException {
      // Not JSON: reported below like any other unexpected answer.
    } on TypeError {
      // A shape the reader did not expect: the same.
    }
    throw NgApiException('The plant server sent something unexpected.', 200);
  }

  /// The JPEG at [url], a path relative to the server (the same key as other calls).
  /// The key goes only to the plant server: an address with a scheme or an
  /// authority (`//host/x`) or a backslash is refused, and what is left is
  /// resolved against the base and checked to be on the base's scheme, host and port.
  Future<Uint8List> image(String url) async {
    const refused = 'The image address is not usable.';
    final u = url.contains('\\') ? null : Uri.tryParse(url);
    if (u == null || url.isEmpty || u.hasScheme || u.hasAuthority) throw NgApiException(refused, 0);
    final uri = base.resolveUri(u.replace(path: u.path.replaceFirst(RegExp(r'^/+'), '')));
    if (uri.scheme != base.scheme || uri.host != base.host || uri.port != base.port) throw NgApiException(refused, 0);
    final sent = auth.headers;
    final r = await _send(() => client.get(uri, headers: sent));
    if (r.statusCode == 401) throw NgApiException(auth.onUnauthorized(sent), 401);
    if (r.statusCode != 200 || r.bodyBytes.isEmpty) throw NgApiException('The plant server answered ${r.statusCode}.', r.statusCode);
    return r.bodyBytes;
  }

  /// Gives [id] its verdict, as the signed-in person: a verdict names a
  /// person, so the key is never used for it.
  Future<void> review(int id, NgVerdict verdict, {String note = ''}) async {
    if (!auth.signedIn) throw NgApiException('Sign in with your own account to give verdicts.', 401);
    // The server counts characters as code points; an emoji can be several.
    if (note.runes.length > maxNote) throw NgApiException('The note is longer than $maxNote characters.', 0);
    final sent = auth.headers;
    final r = await _send(() => client.post(
          base.resolve('vision/ng/$id/review'),
          headers: {...sent, 'Content-Type': 'application/json'},
          body: jsonEncode({'verdict': verdict.name, 'note': note}),
        ));
    if (r.statusCode >= 200 && r.statusCode < 300) return;
    throw NgApiException(switch (r.statusCode) {
      401 => auth.onUnauthorized(sent),
      403 => _detail(r) ?? 'Your account may not give verdicts on this line.',
      404 => 'This part is no longer waiting for review.',
      422 => 'The plant server did not take the note.',
      _ => 'The plant server answered ${r.statusCode}.',
    }, r.statusCode);
  }

  /// The text of a FastAPI error body (`{"detail": "..."}`), when it has one.
  static String? _detail(http.Response r) {
    try {
      final j = jsonDecode(utf8.decode(r.bodyBytes));
      final d = j is Map ? j['detail'] : null;
      return d is String && d.trim().isNotEmpty ? d.trim() : null;
    } on FormatException {
      return null;
    }
  }
}
