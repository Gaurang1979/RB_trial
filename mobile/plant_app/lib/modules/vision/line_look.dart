import 'package:plant_core/plant_core.dart';

import 'vision_api.dart';

/// How a line's state is shown: its status (colour and shape) and words.
(PcStatus, String) lineLook(String state) => switch (state) {
      'running' => (PcStatus.ok, 'Running'),
      'stopped' => (PcStatus.ng, 'Stopped'),
      'fault' => (PcStatus.ng, 'Fault'),
      'starved' => (PcStatus.warn, 'Waiting for parts'),
      'blocked' => (PcStatus.warn, 'Blocked'),
      'changeover' => (PcStatus.warn, 'Changeover'),
      'offline' => (PcStatus.idle, 'No signal'),
      _ => (PcStatus.idle, 'Unknown'),
    };

/// The worst first: stopped and faulted lines, then warnings, then the rest.
int lineRank(LineLive l) => switch (lineLook(l.state).$1) { PcStatus.ng => 0, PcStatus.warn => 1, PcStatus.idle => 2, PcStatus.ok => 3 } * 10 - (l.alarms.isEmpty ? 0 : 1);

/// A time of day as "14:05".
String hhmm(DateTime t) => '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';

/// Words for an NG reason key (`result.<tool>.reason.<key>`); the key itself
/// until the string tables come.
String reasonWords(String key) => key.replaceAll('_', ' ').replaceFirstMapped(RegExp(r'^\w'), (m) => m[0]!.toUpperCase());
