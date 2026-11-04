import 'package:flutter/material.dart';
import 'package:plant_core/plant_core.dart';

import 'app.dart';

/// Colour mode and sounds (docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md 6.2).
class SettingsScreen extends StatelessWidget {
  const SettingsScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final scope = PlantScope.of(context);
    final c = context.pc;
    return ListenableBuilder(
      listenable: scope.look,
      builder: (context, _) => ListView(
        padding: const EdgeInsets.all(16),
        children: [
          Text('Settings', style: TextStyle(fontSize: 26, fontWeight: FontWeight.w700, color: c.ink)),
          const SizedBox(height: 16),
          Text('Colour mode', style: TextStyle(fontWeight: FontWeight.w600, color: c.ink)),
          const SizedBox(height: 8),
          SegmentedButton<ThemeMode>(
            segments: const [
              ButtonSegment(value: ThemeMode.dark, label: Text('Dark'), icon: Icon(Icons.dark_mode_outlined)),
              ButtonSegment(value: ThemeMode.light, label: Text('Light'), icon: Icon(Icons.light_mode_outlined)),
              ButtonSegment(value: ThemeMode.system, label: Text('Phone'), icon: Icon(Icons.smartphone)),
            ],
            selected: {scope.look.mode},
            onSelectionChanged: (s) {
              scope.feedback.tap();
              scope.look.setMode(s.first);
            },
          ),
          const SizedBox(height: 24),
          Card(
            child: SwitchListTile(
              title: Text('Sounds', style: TextStyle(color: c.ink, fontWeight: FontWeight.w600)),
              subtitle: Text('Taps and alarms. Quiet when your phone is on silent.', style: TextStyle(color: c.inkMuted)),
              value: scope.look.sound,
              onChanged: (v) async {
                await scope.look.setSound(v);
                await scope.feedback.tap();
              },
            ),
          ),
          const SizedBox(height: 16),
          Text('Hear the alarm sounds', style: TextStyle(fontWeight: FontWeight.w600, color: c.ink)),
          const SizedBox(height: 8),
          Wrap(spacing: 8, runSpacing: 8, children: [
            for (final (label, p, s) in [('Low', 'low', PcStatus.idle), ('Medium', 'medium', PcStatus.warn), ('High', 'high', PcStatus.ng)])
              OutlinedButton.icon(
                icon: Text(s.shape, style: TextStyle(color: s.color(c), fontWeight: FontWeight.w700)),
                label: Text(label),
                onPressed: () => scope.feedback.alarm(p),
              ),
          ]),
        ],
      ),
    );
  }
}
