import 'package:flutter/material.dart';
import 'package:plant_core/plant_core.dart';

import 'add_server.dart';
import 'app.dart';

/// The person's servers, one entry per product server; tapping one opens
/// that product's module.
class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final scope = PlantScope.of(context);
    final c = context.pc;
    return ListenableBuilder(
      listenable: scope.servers,
      builder: (context, _) {
        final list = scope.servers.list;
        return ListView(
          padding: const EdgeInsets.all(16),
          children: [
            Text('Plants', style: TextStyle(fontSize: 26, fontWeight: FontWeight.w700, color: c.ink)),
            const SizedBox(height: 12),
            if (list.isEmpty)
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(20),
                  child: Text('Add your plant server to see your lines live.', style: TextStyle(color: c.inkMuted, fontSize: 16)),
                ),
              ),
            for (final s in list) _ServerCard(server: s),
            const SizedBox(height: 16),
            FilledButton.icon(
              icon: const Icon(Icons.add),
              label: const Text('Add a server'),
              onPressed: () {
                scope.feedback.tap();
                Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const AddServerScreen()));
              },
            ),
          ],
        );
      },
    );
  }
}

enum _ServerAction { changeKey, fingerprint, remove }

class _ServerCard extends StatelessWidget {
  const _ServerCard({required this.server});
  final PcServer server;

  /// A wrong or replaced key is fixed here: the same server, a new key.
  Future<void> _changeKey(BuildContext context, PlantScope scope) async {
    final key = await showDialog<String>(context: context, builder: (_) => const _KeyDialog());
    if (key == null) return;
    await scope.servers.add(server, token: key);
    await scope.feedback.confirm();
  }

  /// A renewed self-signed certificate is pinned here: the same server and
  /// key, a new fingerprint, used from the next connection (requirements
  /// 17.3, MOB-TLS-05).
  Future<void> _changeFingerprint(BuildContext context, PlantScope scope) async {
    final r = await showDialog<({String? fingerprint})>(context: context, builder: (_) => _FingerprintDialog(current: server.fingerprint));
    if (r == null || r.fingerprint == server.fingerprint) return;
    await scope.servers.add(
      PcServer(id: server.id, product: server.product, name: server.name, baseUrl: server.baseUrl, fingerprint: r.fingerprint),
    );
    await scope.feedback.confirm();
  }

  Future<void> _remove(BuildContext context, PlantScope scope) async {
    final yes = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text('Remove ${server.name}?'),
        content: const Text('Its key is deleted from this phone. You can add the server again later.'),
        actions: [
          TextButton(onPressed: () => Navigator.of(ctx).pop(false), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.of(ctx).pop(true), child: const Text('Remove')),
        ],
      ),
    );
    if (yes != true) return;
    await scope.servers.remove(server.id);
    await scope.feedback.confirm();
  }

  @override
  Widget build(BuildContext context) {
    final scope = PlantScope.of(context);
    final m = scope.modules[server.product]!;
    final c = context.pc;
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Card(
        child: ListTile(
          minVerticalPadding: 14,
          leading: Icon(m.icon, color: c.brand, size: 30),
          title: Text(server.name, style: TextStyle(fontWeight: FontWeight.w600, color: c.ink)),
          subtitle: Text(m.available ? m.title : '${m.title} · coming soon', style: TextStyle(color: c.inkMuted)),
          trailing: Row(mainAxisSize: MainAxisSize.min, children: [
            PopupMenuButton<_ServerAction>(
              tooltip: 'Options for ${server.name}',
              onSelected: (a) => switch (a) {
                _ServerAction.changeKey => _changeKey(context, scope),
                _ServerAction.fingerprint => _changeFingerprint(context, scope),
                _ServerAction.remove => _remove(context, scope),
              },
              itemBuilder: (_) => const [
                PopupMenuItem(value: _ServerAction.changeKey, child: Text('Change key')),
                PopupMenuItem(value: _ServerAction.fingerprint, child: Text('Certificate fingerprint')),
                PopupMenuItem(value: _ServerAction.remove, child: Text('Remove')),
              ],
            ),
            const Icon(Icons.chevron_right),
          ]),
          onTap: () async {
            scope.feedback.tap();
            final token = await scope.servers.token(server.id);
            if (!context.mounted) return;
            Navigator.of(context).push(MaterialPageRoute<void>(builder: (ctx) => m.home(ctx, server, token)));
          },
        ),
      ),
    );
  }
}

/// Asks for a new key; the key stays hidden as it is typed.
class _KeyDialog extends StatefulWidget {
  const _KeyDialog();
  @override
  State<_KeyDialog> createState() => _KeyDialogState();
}

class _KeyDialogState extends State<_KeyDialog> {
  final _key = TextEditingController();

  @override
  void dispose() {
    _key.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Change key'),
      content: TextField(
        controller: _key,
        obscureText: true,
        autofocus: true,
        decoration: const InputDecoration(labelText: 'New key', helperText: 'From your plant administrator'),
        onChanged: (_) => setState(() {}),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.of(context).pop(), child: const Text('Cancel')),
        FilledButton(
          onPressed: _key.text.trim().isEmpty ? null : () => Navigator.of(context).pop(_key.text.trim()),
          child: const Text('Save'),
        ),
      ],
    );
  }
}

/// Shows a server's certificate fingerprint, shortened unless asked for in
/// full, and takes a new one, checked as on the add-server screen. Pops
/// with the fingerprint to keep (null: none), or nothing when cancelled.
class _FingerprintDialog extends StatefulWidget {
  const _FingerprintDialog({required this.current});
  final String? current;
  @override
  State<_FingerprintDialog> createState() => _FingerprintDialogState();
}

class _FingerprintDialogState extends State<_FingerprintDialog> {
  final _new = TextEditingController();
  bool _full = false;

  @override
  void dispose() {
    _new.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final cur = widget.current;
    final typed = pcFingerprint(_new.text);
    return AlertDialog(
      title: const Text('Certificate fingerprint'),
      content: SingleChildScrollView(
        child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
          if (cur == null)
            Text('None. Only a server with its own certificate needs one.', style: TextStyle(color: c.inkMuted))
          else ...[
            SelectableText(
              _full ? pcFingerprintColons(cur) : pcFingerprintShort(cur),
              key: const ValueKey('current-fingerprint'),
              style: TextStyle(color: c.ink, fontFamily: 'monospace'),
            ),
            TextButton(
              onPressed: () => setState(() => _full = !_full),
              child: Text(_full ? 'Show less' : 'Show in full'),
            ),
          ],
          const SizedBox(height: 8),
          TextFormField(
            key: const ValueKey('new-fingerprint'),
            controller: _new,
            autocorrect: false,
            autovalidateMode: AutovalidateMode.onUserInteraction,
            decoration: const InputDecoration(
              labelText: 'New fingerprint (SHA-256)',
              helperText: 'For a renewed certificate. Used from the next connection.',
              helperMaxLines: 2,
            ),
            validator: pcFingerprintProblem,
            onChanged: (_) => setState(() {}),
          ),
        ]),
      ),
      actions: [
        if (cur != null)
          TextButton(onPressed: () => Navigator.of(context).pop((fingerprint: null)), child: const Text('Remove fingerprint')),
        TextButton(onPressed: () => Navigator.of(context).pop(), child: const Text('Cancel')),
        FilledButton(
          onPressed: typed == null || typed == cur ? null : () => Navigator.of(context).pop((fingerprint: typed)),
          child: const Text('Save'),
        ),
      ],
    );
  }
}
