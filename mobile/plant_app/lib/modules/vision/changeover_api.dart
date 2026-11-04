import 'dart:convert';

import 'package:http/http.dart' as http;

import 'api_auth.dart';
import 'vision_api.dart';

/// What a supervisor can answer; [name] is what the server takes.
enum ChangeoverDecision { accept, refuse }

/// One field of the batch as the operator entered it.
class ChangeoverField {
  ChangeoverField(this.key, this.label, this.value);
  final String key, label, value;
}

/// What the operator set up: the job (product), its version, the variant and the batch fields.
class ChangeoverRecord {
  ChangeoverRecord({required this.job, required this.jobVersion, required this.variant, required this.batch});
  final String job, variant;
  final int jobVersion;
  final List<ChangeoverField> batch;

  static ChangeoverRecord? fromJson(Object? j) {
    if (j is! Map) return null;
    final batch = j['batch'];
    return ChangeoverRecord(
      job: '${j['job'] ?? ''}',
      jobVersion: ChangeoverItem._n(j['job_version']) ?? 0,
      variant: '${j['variant'] ?? ''}',
      batch: [
        for (final f in batch is List ? batch.whereType<Map>() : const <Map>[])
          if (f['key'] != null) ChangeoverField('${f['key']}', '${f['label'] ?? f['key']}', '${f['value'] ?? ''}'),
      ],
    );
  }
}

/// One difference from the last accepted record of the station; [old] is null
/// for a field that is new, [now] null for one that is gone.
class ChangeoverChange {
  ChangeoverChange(this.key, this.label, this.old, this.now);
  final String key, label;
  final String? old, now;
}

/// One request from /vision/changeover (snake_case JSON).
class ChangeoverItem {
  ChangeoverItem({
    required this.id, required this.station, required this.plant, required this.line, required this.code, required this.record,
    required this.operator, required this.changedAt, required this.askedAt, required this.state, required this.changes,
    this.ask, this.decidedName, this.decidedAt, this.note, this.previous,
  });

  /// The server's own name for the request, kept as text.
  final String id;
  final String station, plant, line, code, operator, state;
  final int changedAt, askedAt;
  final int? ask, decidedAt;
  final String? decidedName, note;
  final ChangeoverRecord record;
  final ChangeoverRecord? previous;
  final List<ChangeoverChange> changes;

  /// The first 8 characters of the code, which both screens show.
  String get shortCode => code.length > 8 ? code.substring(0, 8) : code;
  bool get pending => state == 'pending';

  static int? _n(Object? v) => v is num && v.isFinite ? v.toInt() : null;
  static String? _s(Object? v) => v is String && v.isNotEmpty ? v : null;
  static String? _text(Object? v) => v == null ? null : '$v';

  /// Reads one request; null when it has no usable id (it could not be
  /// answered). Other fields that are missing or of the wrong type fall back
  /// to empty values instead of failing the whole list.
  static ChangeoverItem? fromJson(Object? j) {
    if (j is! Map) return null;
    final raw = j['id'];
    final id = raw is num && raw.isFinite ? '${raw.toInt()}' : _s(raw);
    if (id == null) return null;
    final changes = j['changes'];
    return ChangeoverItem(
      id: id, station: '${j['station'] ?? ''}', plant: '${j['plant'] ?? ''}', line: '${j['line'] ?? ''}', code: '${j['code'] ?? ''}',
      ask: _n(j['ask']),
      record: ChangeoverRecord.fromJson(j['record']) ?? ChangeoverRecord(job: '', jobVersion: 0, variant: '', batch: []),
      // The server sends the last accepted request of the station, with its record inside.
      previous: ChangeoverRecord.fromJson(j['previous'] is Map ? (j['previous'] as Map)['record'] : null),
      operator: '${j['operator'] ?? ''}', changedAt: _n(j['changed_at']) ?? 0, askedAt: _n(j['asked_at']) ?? 0,
      state: _s(j['state']) ?? 'pending',
      decidedName: _s(j['decided_name']), decidedAt: _n(j['decided_at']), note: _s(j['note']),
      changes: [
        for (final c in changes is List ? changes.whereType<Map>() : const <Map>[])
          if (c['key'] != null) ChangeoverChange('${c['key']}', '${c['label'] ?? c['key']}', _text(c['old']), _text(c['new'])),
      ],
    );
  }

  /// The requests of an answer, a bare list or `{"items": [...]}`; the ones
  /// without a usable id are left out.
  static List<ChangeoverItem> listFromJson(Object? j) {
    final items = j is Map ? j['items'] : j;
    if (items is! List) throw const FormatException('no list');
    return [for (final i in items) ?ChangeoverItem.fromJson(i)];
  }
}

/// A failed call with a message fit to show; [code] is the HTTP status, 0 when
/// the server could not be reached (or the call was refused before it was sent).
class ChangeoverApiException extends VisionApiException {
  ChangeoverApiException(super.message, this.code);
  final int code;
}

/// The plant server's changeover verification calls. A decision is only ever
/// sent now, never queued: it names a person and needs a live connection.
class ChangeoverApi {
  /// [base] keeps its path, as in [VisionApi]. [auth] says who is asking; the
  /// reads use a signed-in person's session or else the key, and a decision
  /// only ever a person's session.
  ChangeoverApi(this.client, Uri base, String? key, {ApiAuth? auth})
      : auth = auth ?? ApiAuth(client, base, key: key),
        base = base.path.endsWith('/') ? base : base.replace(path: '${base.path}/');
  ChangeoverApi.of(VisionApi api) : this(api.client, api.base, api.key, auth: api.auth);
  final http.Client client;
  final Uri base;
  final ApiAuth auth;

  static const refusalNeedsNote = 'A refusal needs a note.';

  Future<http.Response> _send(Future<http.Response> Function() call) async {
    try {
      return await call().timeout(const Duration(seconds: 15));
    } catch (_) {
      // No answer, a timeout or a dropped connection: one plain message.
      throw ChangeoverApiException('Cannot reach the plant server.', 0);
    }
  }

  /// The requests waiting for a decision (`state` pending), newest first.
  /// [all] reads every state instead.
  Future<List<ChangeoverItem>> list({String? plant, String? line, bool all = false, int limit = 100}) async {
    final uri = base.resolve('vision/changeover').replace(queryParameters: {
      'state': all ? 'all' : 'pending',
      'limit': '$limit',
      if (plant != null && plant.isNotEmpty) 'plant': plant,
      if (line != null && line.isNotEmpty) 'line': line,
    });
    final sent = auth.headers;
    final r = await _send(() => client.get(uri, headers: sent));
    if (r.statusCode == 401) throw ChangeoverApiException(auth.onUnauthorized(sent), 401);
    if (r.statusCode != 200) throw ChangeoverApiException('The plant server answered ${r.statusCode}.', r.statusCode);
    try {
      return ChangeoverItem.listFromJson(jsonDecode(utf8.decode(r.bodyBytes)));
    } on FormatException {
      // Not JSON, or not a list: reported below like any other unexpected answer.
    } on TypeError {
      // A shape the reader did not expect: the same.
    }
    throw ChangeoverApiException('The plant server sent something unexpected.', 200);
  }

  /// Answers [id] as the signed-in person: a decision names a person, so the
  /// key is never used for it. A refusal needs a [note]; this is checked
  /// before anything is sent.
  Future<void> decide(String id, ChangeoverDecision decision, {String note = ''}) async {
    if (!auth.signedIn) throw ChangeoverApiException('Sign in with your own account to accept or refuse.', 401);
    final text = note.trim();
    if (decision == ChangeoverDecision.refuse && text.isEmpty) throw ChangeoverApiException(refusalNeedsNote, 400);
    final sent = auth.headers;
    final r = await _send(() => client.post(
          base.resolve('vision/changeover/${Uri.encodeComponent(id)}/decide'),
          headers: {...sent, 'Content-Type': 'application/json'},
          body: jsonEncode({'decision': decision.name, 'note': text}),
        ));
    if (r.statusCode >= 200 && r.statusCode < 300) return;
    throw ChangeoverApiException(switch (r.statusCode) {
      401 => auth.onUnauthorized(sent),
      403 => _detail(r) ?? 'Your account may not decide on this line, or you made this change yourself.',
      409 => 'Already decided, or replaced by a newer request.',
      400 => _detail(r) ?? refusalNeedsNote,
      404 => 'This request is no longer on the server.',
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
