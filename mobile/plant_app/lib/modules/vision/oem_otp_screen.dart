import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:plant_core/plant_core.dart';

/// Opens the OEM Service Daily Rolling OTP lookup screen (Req 17.10 REQ-14 / PROT-29).
Future<void> openOemOtpScreen(BuildContext context, PcFeedback feedback, {OemOtpSource? source, String initialLicence = ''}) {
  feedback.tap();
  return Navigator.of(context).push(MaterialPageRoute<void>(
    builder: (_) => OemOtpScreen(feedback: feedback, source: source, initialLicence: initialLicence),
  ));
}

/// Screen allowing OEM support engineers to fetch today's rolling OTP for a
/// customer line License ID from the licence server. The app holds no secret;
/// without a [source] (no licence-server sign-in) it shows no code.
class OemOtpScreen extends StatefulWidget {
  const OemOtpScreen({super.key, required this.feedback, this.source, this.initialLicence = ''});
  final PcFeedback feedback;
  final OemOtpSource? source;
  final String initialLicence;

  @override
  State<OemOtpScreen> createState() => _OemOtpScreenState();
}

class _OemOtpScreenState extends State<OemOtpScreen> {
  late final TextEditingController _licenceCtrl;
  String? _currentOtp;
  String? _error;
  bool _busy = false;
  bool _copied = false;

  @override
  void initState() {
    super.initState();
    _licenceCtrl = TextEditingController(text: widget.initialLicence);
  }

  @override
  void dispose() {
    _licenceCtrl.dispose();
    super.dispose();
  }

  Future<void> _fetchOtp() async {
    final source = widget.source;
    final lic = _licenceCtrl.text.trim();
    if (source == null || lic.isEmpty || _busy) return;
    setState(() {
      _busy = true;
      _error = null;
      _currentOtp = null;
      _copied = false;
    });
    try {
      final code = await source.fetch(lic);
      if (mounted) setState(() => _currentOtp = code.otp);
    } catch (e) {
      if (mounted) setState(() => _error = 'The licence server did not issue a code: $e');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _copyCode() async {
    if (_currentOtp == null) return;
    await Clipboard.setData(ClipboardData(text: _currentOtp!));
    widget.feedback.tap();
    setState(() => _copied = true);
    if (mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text('Today\'s OEM OTP copied to clipboard'),
          duration: Duration(seconds: 2),
        ),
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final todayStr = OemOtp.todayUtc();

    return Scaffold(
      appBar: AppBar(
        title: const Text('OEM Service OTP (REQ-14)'),
      ),
      body: SingleChildScrollView(
        padding: const EdgeInsets.all(20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Card(
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        const Icon(Icons.security, color: Colors.blueAccent),
                        const SizedBox(width: 8),
                        Text(
                          'OEM Daily Rolling Token',
                          style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.bold),
                        ),
                      ],
                    ),
                    const SizedBox(height: 8),
                    Text(
                      'Access to Tier-3 OEM Service Mode requires a daily OTP that changes at 00:00 UTC. Valid only for the given machine License ID.',
                      style: theme.textTheme.bodyMedium?.copyWith(color: theme.textTheme.bodySmall?.color),
                    ),
                  ],
                ),
              ),
            ),
            const SizedBox(height: 20),
            TextField(
              controller: _licenceCtrl,
              decoration: const InputDecoration(
                labelText: 'Machine License ID',
                hintText: 'e.g. VS-PRO-GJ04-98214',
                border: OutlineInputBorder(),
                prefixIcon: Icon(Icons.vpn_key),
              ),
              onChanged: (_) => setState(() {
                _currentOtp = null;
                _error = null;
              }),
            ),
            const SizedBox(height: 12),
            if (widget.source == null)
              const Text('Not connected to the licence server: sign in to ERPNext to get codes.')
            else
              FilledButton(
                onPressed: _busy ? null : _fetchOtp,
                child: Text(_busy ? 'Asking the licence server…' : 'Get today\'s OTP'),
              ),
            if (_error != null) ...[
              const SizedBox(height: 8),
              Text(_error!, style: TextStyle(color: theme.colorScheme.error)),
            ],
            const SizedBox(height: 20),
            if (_currentOtp != null) ...[
              Container(
                padding: const EdgeInsets.symmetric(vertical: 24, horizontal: 16),
                decoration: BoxDecoration(
                  color: theme.colorScheme.surfaceContainerHighest,
                  borderRadius: BorderRadius.circular(12),
                  border: Border.all(color: Colors.blueAccent.withValues(alpha: 0.5)),
                ),
                child: Column(
                  children: [
                    Text(
                      'VALID FOR TODAY ($todayStr)',
                      style: theme.textTheme.labelSmall?.copyWith(letterSpacing: 1.5, fontWeight: FontWeight.bold),
                    ),
                    const SizedBox(height: 12),
                    Text(
                      _currentOtp!,
                      style: theme.textTheme.displayMedium?.copyWith(
                        letterSpacing: 8,
                        fontWeight: FontWeight.bold,
                        color: Colors.blueAccent,
                      ),
                    ),
                    const SizedBox(height: 12),
                    FilledButton.icon(
                      onPressed: _copyCode,
                      icon: Icon(_copied ? Icons.check : Icons.copy),
                      label: Text(_copied ? 'Copied' : 'Copy 6-Digit OTP'),
                    ),
                    const SizedBox(height: 12),
                    Text(OemOtp.auditWarningNotice, textAlign: TextAlign.center, style: theme.textTheme.bodySmall),
                  ],
                ),
              ),
            ],
            const SizedBox(height: 24),
            ListTile(
              leading: const Icon(Icons.info_outline),
              title: const Text('Security Isolation'),
              subtitle: const Text(
                'Codes are issued only by the licence server, to signed-in staff with the right role. This app holds no secret.',
              ),
            ),
          ],
        ),
      ),
    );
  }
}
