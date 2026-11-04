import 'package:flutter/material.dart';
import 'package:plant_core/plant_core.dart';

import 'app.dart';

/// Adds a product server: which product, a name, its https address, its key
/// and, for a server with a private or self-signed certificate, that
/// certificate's SHA-256 fingerprint (requirements 17.3, MOB-TLS-04).
/// (Scanning the server's QR code, which will carry the fingerprint, and
/// signing in with a user name come with Plant Insight's user accounts.)
class AddServerScreen extends StatefulWidget {
  const AddServerScreen({super.key});
  @override
  State<AddServerScreen> createState() => _AddServerScreenState();
}

class _AddServerScreenState extends State<AddServerScreen> {
  final _form = GlobalKey<FormState>();
  final _name = TextEditingController(), _url = TextEditingController(), _key = TextEditingController(), _fingerprint = TextEditingController();
  PcProduct _product = PcProduct.vision;
  // Set on the first tap: a second tap before the screen closes must not add a second server.
  bool _saving = false;

  @override
  void dispose() {
    _name.dispose();
    _url.dispose();
    _key.dispose();
    _fingerprint.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final scope = PlantScope.of(context);
    return Scaffold(
      appBar: AppBar(title: const Text('Add a server')),
      body: Form(
        key: _form,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            SegmentedButton<PcProduct>(
              segments: [
                for (final m in scope.modules.values)
                  ButtonSegment(value: m.product, label: Text(m.title), icon: Icon(m.icon), enabled: m.available),
              ],
              selected: {_product},
              onSelectionChanged: (s) {
                scope.feedback.tap();
                setState(() => _product = s.first);
              },
            ),
            const SizedBox(height: 16),
            TextFormField(
              controller: _name,
              decoration: const InputDecoration(labelText: 'Name', hintText: 'Unit 1'),
              validator: (v) => (v ?? '').trim().isEmpty ? 'Give it a name you will recognise' : null,
            ),
            const SizedBox(height: 12),
            TextFormField(
              controller: _url,
              keyboardType: TextInputType.url,
              decoration: const InputDecoration(labelText: 'Address', hintText: 'https://plant.local'),
              validator: serverAddressProblem,
            ),
            const SizedBox(height: 12),
            TextFormField(
              controller: _key,
              obscureText: true,
              decoration: const InputDecoration(labelText: 'Key', helperText: 'From your plant administrator'),
              validator: (v) => (v ?? '').trim().isEmpty ? 'Enter the key' : null,
            ),
            const SizedBox(height: 12),
            TextFormField(
              key: const ValueKey('fingerprint'),
              controller: _fingerprint,
              autocorrect: false,
              decoration: const InputDecoration(
                labelText: 'Certificate fingerprint (SHA-256)',
                helperText: 'Only for a server with its own certificate; leave empty otherwise',
                helperMaxLines: 2,
              ),
              validator: pcFingerprintProblem,
            ),
            const SizedBox(height: 20),
            FilledButton(
              onPressed: _saving
                  ? null
                  : () async {
                      if (_saving || !_form.currentState!.validate()) return;
                      setState(() => _saving = true);
                      try {
                        final id = DateTime.now().microsecondsSinceEpoch.toRadixString(36);
                        await scope.servers.add(
                          PcServer(
                            id: id,
                            product: _product,
                            name: _name.text.trim(),
                            baseUrl: Uri.parse(_url.text.trim()),
                            fingerprint: pcFingerprint(_fingerprint.text),
                          ),
                          token: _key.text.trim(),
                        );
                        await scope.feedback.confirm();
                      } catch (_) {
                        if (mounted) setState(() => _saving = false); // let the person try again
                        rethrow;
                      }
                      if (context.mounted) Navigator.of(context).pop();
                    },
              child: const Text('Save'),
            ),
          ],
        ),
      ),
    );
  }
}

/// Null when [v] is a usable server address: https, or http only for this
/// PC itself (an emulator's 10.0.2.2 included); otherwise what to fix.
String? serverAddressProblem(String? v) {
  final u = Uri.tryParse((v ?? '').trim());
  if (u == null || !u.hasScheme || u.host.isEmpty) return 'Enter the address, like https://plant.local';
  final local = u.host == 'localhost' || u.host == '127.0.0.1' || u.host == '10.0.2.2';
  if (u.scheme != 'https' && !(u.scheme == 'http' && local)) return 'The address must start with https://';
  return null;
}
