import 'package:flutter/material.dart';
import 'package:http/http.dart' as http;
import 'package:plant_core/plant_core.dart';

import 'api_auth.dart';
import 'changeover_api.dart';
import 'floor_screen.dart';
import 'vision_api.dart';

class VisionModule extends PcModule {
  VisionModule(this.client, this.feedback, this.servers, {this.live});
  final http.Client client;
  final PcFeedback feedback;

  /// Opens the vision floor's live socket (requirements 17.3, LV-05); null: the floor is polled.
  final PcSocketConnect? live;

  /// Where each server's sign-ins are kept.
  final PcServers servers;

  @override
  PcProduct get product => PcProduct.vision;
  @override
  String get title => 'Vision';
  @override
  IconData get icon => Icons.center_focus_strong;

  @override
  Widget home(BuildContext context, PcServer server, String? token) => _VisionHome(module: this, server: server, token: token);

  @override
  Future<void> revoke(PcServer server, PcSession session) => ApiAuth.revoke(client, server.baseUrl, session.token);
}

/// Holds the server's sign-in state for as long as its screens are open.
class _VisionHome extends StatefulWidget {
  const _VisionHome({required this.module, required this.server, required this.token});
  final VisionModule module;
  final PcServer server;
  final String? token;

  @override
  State<_VisionHome> createState() => _VisionHomeState();
}

class _VisionHomeState extends State<_VisionHome> {
  late final ApiAuth _auth = ApiAuth(
    widget.module.client,
    widget.server.baseUrl,
    key: widget.token,
    servers: widget.module.servers,
    serverId: widget.server.id,
  );

  @override
  void dispose() {
    _auth.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final api = VisionApi(widget.module.client, widget.server.baseUrl, widget.token, auth: _auth);
    return FloorScreen(server: widget.server, api: api, feedback: widget.module.feedback, changeover: ChangeoverApi.of(api), live: widget.module.live);
  }
}
