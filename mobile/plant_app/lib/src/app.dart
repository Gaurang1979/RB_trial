import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:http/http.dart' as http;
import 'package:http/io_client.dart';
import 'package:plant_core/plant_core.dart';

import '../modules/production/production_module.dart';
import '../modules/vision/vision_module.dart';
import 'home.dart';
import 'settings.dart';

/// What every screen of the app can reach: the look, the servers, sound
/// and touch feedback, the product modules and the HTTP client.
class PlantScope extends InheritedWidget {
  const PlantScope({super.key, required this.look, required this.servers, required this.feedback, required this.modules, required this.client, required super.child});

  final PcLook look;
  final PcServers servers;
  final PcFeedback feedback;
  final Map<PcProduct, PcModule> modules;
  final http.Client client;

  static PlantScope of(BuildContext context) => context.dependOnInheritedWidgetOfExactType<PlantScope>()!;

  @override
  bool updateShouldNotify(PlantScope old) => false;
}

class PlantApp extends StatefulWidget {
  const PlantApp({super.key, required this.look, required this.servers, required this.feedback, this.client});

  final PcLook look;
  final PcServers servers;
  final PcFeedback feedback;
  final http.Client? client;

  @override
  State<PlantApp> createState() => _PlantAppState();
}

class _PlantAppState extends State<PlantApp> {
  // One client for every server: a server's private certificate is
  // accepted by its pinned fingerprint only (requirements 17.3, MOB-TLS-02).
  late final http.Client _client = widget.client ?? IOClient(PcCertTrust(() => widget.servers.list).client());
  // Live sockets trust certificates the same way; an app given its own client (tests) polls.
  late final Map<PcProduct, PcModule> _modules = {
    PcProduct.vision: VisionModule(_client, widget.feedback, widget.servers,
        live: widget.client == null ? pcSocketConnect(PcCertTrust(() => widget.servers.list)) : null),
    PcProduct.production: ProductionModule(),
  };

  // Shared floor phones (PcServers): the open sign-in locks when the app is
  // hidden and after idle time; sign-ins the app drops are ended on their server.
  late final AppLifecycleListener _life;
  Timer? _idle;

  @override
  void initState() {
    super.initState();
    widget.servers.onDropped = _revoke;
    widget.servers.reports.send = _sendReport;
    widget.servers.reports.flush();
    _life = AppLifecycleListener(onHide: widget.servers.lock);
    widget.servers.tick();
    _idle = Timer.periodic(const Duration(seconds: 10), (_) => widget.servers.tick());
  }

  void _revoke(String serverId, PcSession session) {
    final server = widget.servers.list.where((s) => s.id == serverId).firstOrNull;
    if (server == null) return;
    // A lockout is reported with the sign-in's token before that token is ended.
    widget.servers.reports.idle().timeout(const Duration(seconds: 5), onTimeout: () {}).whenComplete(() => _modules[server.product]?.revoke(server, session));
  }

  // One PIN report to its server. 401: the sign-in has ended there. 422 to
  // a report with a phone time: the clock is off, sent again without it
  // (PT11-08c). Any other 4xx is a report the server will not take
  // (dropped); no answer or a server fault is tried again later.
  Future<PcReportResult> _sendReport(PcPinReport r) async {
    final server = widget.servers.list.where((s) => s.id == r.serverId).firstOrNull;
    if (server == null) return PcReportResult.sent;
    final base = server.baseUrl.path.endsWith('/') ? server.baseUrl : server.baseUrl.replace(path: '${server.baseUrl.path}/');
    try {
      final res = await _client
          .post(base.resolve('auth/pin-events'),
              headers: {'Authorization': 'Bearer ${r.token}', 'Content-Type': 'application/json'},
              body: jsonEncode({'event': r.event.wire, 'device': ?r.device, 'at': ?r.atWire}))
          .timeout(const Duration(seconds: 10));
      if (res.statusCode == 401) return PcReportResult.ended;
      if (res.statusCode == 422 && r.at != null) return PcReportResult.badTime;
      if (res.statusCode == 408 || res.statusCode == 429 || res.statusCode >= 500) return PcReportResult.retry;
      return PcReportResult.sent;
    } catch (_) {
      return PcReportResult.retry;
    }
  }

  // Only a client made here is closed here; one passed in belongs to its owner.
  @override
  void dispose() {
    _idle?.cancel();
    _life.dispose();
    if (widget.servers.onDropped == _revoke) widget.servers.onDropped = null;
    if (widget.servers.reports.send == _sendReport) widget.servers.reports.send = null;
    if (widget.client == null) _client.close();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return PlantScope(
      look: widget.look,
      servers: widget.servers,
      feedback: widget.feedback,
      modules: _modules,
      client: _client,
      child: Listener(
        // Any touch keeps the open sign-in in use.
        behavior: HitTestBehavior.translucent,
        onPointerDown: (_) => widget.servers.touch(),
        child: ListenableBuilder(
          listenable: widget.look,
          builder: (context, _) => MaterialApp(
            title: 'Plant Insight',
            debugShowCheckedModeBanner: false,
            theme: pcTheme(Brightness.light),
            darkTheme: pcTheme(Brightness.dark),
            themeMode: widget.look.mode,
            // MaterialApp fades between the two themes (PcColors.lerp).
            themeAnimationDuration: const Duration(milliseconds: 300),
            home: const _Shell(),
          ),
        ),
      ),
    );
  }
}

class _Shell extends StatefulWidget {
  const _Shell();
  @override
  State<_Shell> createState() => _ShellState();
}

class _ShellState extends State<_Shell> {
  int _tab = 0;

  @override
  Widget build(BuildContext context) {
    final scope = PlantScope.of(context);
    return Scaffold(
      body: SafeArea(
        child: IndexedStack(index: _tab, children: const [HomeScreen(), SettingsScreen()]),
      ),
      bottomNavigationBar: NavigationBar(
        selectedIndex: _tab,
        onDestinationSelected: (i) {
          scope.feedback.tap();
          setState(() => _tab = i);
        },
        destinations: const [
          NavigationDestination(icon: Icon(Icons.factory_outlined), selectedIcon: Icon(Icons.factory), label: 'Plants'),
          NavigationDestination(icon: Icon(Icons.tune_outlined), selectedIcon: Icon(Icons.tune), label: 'Settings'),
        ],
      ),
    );
  }
}
