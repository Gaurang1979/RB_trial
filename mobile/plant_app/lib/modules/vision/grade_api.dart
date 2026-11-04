import 'dart:convert';

import 'package:http/http.dart' as http;

import 'api_auth.dart';
import 'vision_api.dart';

/// The grade letter of a grade value (4 = A .. 0 = F).
String gradeLetter(double g) => 'FDCBA'[g.floor().clamp(0, 4)];

/// One time bucket of a tool's grades (the server leaves empty buckets out).
class GradeBucket {
  GradeBucket({
    required this.start,
    required this.samples,
    required this.ungraded,
    this.mean,
    this.worst,
  });
  final int start, samples, ungraded;
  final double? mean, worst;
}

/// The drift status of a tool's latest samples.
class GradeDrift {
  GradeDrift({
    required this.state,
    required this.samples,
    required this.lowest,
    required this.lowestFromStation,
    this.mean,
    this.slope,
  });
  final String state;
  final int samples;
  final double lowest;
  final bool lowestFromStation;
  final double? mean, slope;
}

/// One station's tool, from /vision/grades/trend. [drift] is null when the
/// tool has no graded part in the recent window.
class GradeSeries {
  GradeSeries({
    required this.line,
    required this.station,
    required this.tool,
    required this.buckets,
    this.drift,
  });
  final String line, station, tool;
  final List<GradeBucket> buckets;
  final GradeDrift? drift;

  int get parts => buckets.fold(0, (n, b) => n + b.samples);
  int get ungraded => buckets.fold(0, (n, b) => n + b.ungraded);
}

/// The print-quality trend as the plant server gives it.
class GradeTrend {
  GradeTrend({
    required this.since,
    required this.until,
    required this.lowestGrade,
    required this.series,
  });
  final int since, until;
  final double lowestGrade;
  final List<GradeSeries> series;

  static double? _d(Object? v) => v is num ? v.toDouble() : null;
  static int _i(Object? v) => v is num ? v.toInt() : 0;

  /// Throws a [TypeError] for a shape that is not a trend; [GradeApi] reports it.
  static GradeTrend fromJson(Object? j) {
    final m = j as Map;
    final rule = m['rule'];
    return GradeTrend(
      since: _i(m['since']),
      until: _i(m['until']),
      lowestGrade: _d(rule is Map ? rule['lowest_grade'] : null) ?? 2.0,
      series: [
        for (final s in (m['series'] as List).whereType<Map>())
          GradeSeries(
            line: '${s['line']}',
            station: '${s['station']}',
            tool: '${s['tool']}',
            buckets: [
              for (final b in (s['buckets'] as List).whereType<Map>())
                GradeBucket(
                  start: _i(b['start']),
                  samples: _i(b['samples']),
                  ungraded: _i(b['ungraded']),
                  mean: _d(b['mean']),
                  worst: _d(b['worst']),
                ),
            ],
            drift: s['drift'] is Map ? _drift(s['drift'] as Map) : null,
          ),
      ],
    );
  }

  static GradeDrift _drift(Map d) => GradeDrift(
    state: '${d['state']}',
    samples: _i(d['samples']),
    lowest: _d(d['lowest']) ?? 2.0,
    lowestFromStation: d['lowest_from'] == 'station',
    mean: _d(d['mean']),
    slope: _d(d['slope']),
  );
}

class GradeApiException extends VisionApiException {
  GradeApiException(super.message, this.code);
  final int code;
}

/// Reads the print-quality trend. Read only: the key or a signed-in session.
class GradeApi {
  GradeApi(this.client, Uri base, String? key, {ApiAuth? auth})
    : auth = auth ?? ApiAuth(client, base, key: key),
      base = base.path.endsWith('/')
          ? base
          : base.replace(path: '${base.path}/');
  GradeApi.of(VisionApi api)
    : this(api.client, api.base, api.key, auth: api.auth);
  final http.Client client;
  final Uri base;
  final ApiAuth auth;

  /// The last [span] of grades for every line the caller may see, or for one
  /// [line] of a [plant], in buckets of [bucketSeconds].
  Future<GradeTrend> trend({
    String? plant,
    String? line,
    Duration span = const Duration(hours: 24),
    int bucketSeconds = 3600,
    DateTime? now,
  }) async {
    final until = (now ?? DateTime.now()).millisecondsSinceEpoch;
    final uri = base
        .resolve('vision/grades/trend')
        .replace(
          queryParameters: {
            'since': '${until - span.inMilliseconds}',
            'until': '$until',
            'bucket_s': '$bucketSeconds',
            if (plant != null && plant.isNotEmpty) 'plant': plant,
            if (line != null && line.isNotEmpty) 'line': line,
          },
        );
    final sent = auth.headers;
    http.Response r;
    try {
      r = await client
          .get(uri, headers: sent)
          .timeout(const Duration(seconds: 15));
    } catch (_) {
      throw GradeApiException('Cannot reach the plant server.', 0);
    }
    if (r.statusCode == 401) {
      throw GradeApiException(auth.onUnauthorized(sent), 401);
    }
    if (r.statusCode == 403) {
      throw GradeApiException('Your account cannot see this line.', 403);
    }
    if (r.statusCode != 200) {
      throw GradeApiException(
        'The plant server answered ${r.statusCode}.',
        r.statusCode,
      );
    }
    try {
      return GradeTrend.fromJson(jsonDecode(utf8.decode(r.bodyBytes)));
    } on FormatException {
      // Not JSON: reported below like any other unexpected answer.
    } on TypeError {
      // A shape the reader did not expect: the same.
    }
    throw GradeApiException('The plant server sent something unexpected.', 200);
  }
}
