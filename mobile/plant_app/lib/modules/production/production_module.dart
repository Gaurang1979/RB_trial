import 'package:flutter/material.dart';
import 'package:plant_core/plant_core.dart';

/// Production Measurement (the production_measurement backend: /auth/login,
/// /plants, /lines/{id}/status, /alerts). Owner, 2026-10-29: it joins this
/// app later; until then it is listed but cannot be added.
class ProductionModule extends PcModule {
  @override
  PcProduct get product => PcProduct.production;
  @override
  String get title => 'Production';
  @override
  IconData get icon => Icons.precision_manufacturing_outlined;
  @override
  bool get available => false;

  @override
  Widget home(BuildContext context, PcServer server, String? token) => Scaffold(
        appBar: AppBar(title: Text(server.name)),
        body: Center(child: Text('Production Measurement comes to this app soon.', style: TextStyle(color: context.pc.inkMuted))),
      );
}
