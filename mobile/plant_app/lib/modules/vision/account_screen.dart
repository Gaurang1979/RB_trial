import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:plant_core/plant_core.dart';

import 'api_auth.dart';

/// Opens the sign-in screen for one plant server.
Future<void> openAccount(BuildContext context, ApiAuth auth, PcFeedback feedback) {
  feedback.tap();
  return Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => AccountScreen(auth: auth, feedback: feedback)));
}

/// Who is signed in on this plant server, with Lock and Sign out; or, on
/// a shared floor phone, the people signed in here, each opened with their
/// own PIN; and the user name, password and a new PIN to sign in with a
/// local account. The password is typed hidden, sent once and never kept;
/// the PIN is kept only as a hash. A sign-in shows the NG review's verdict
/// actions and makes every call follow the person's roles.
class AccountScreen extends StatefulWidget {
  const AccountScreen({super.key, required this.auth, required this.feedback});
  final ApiAuth auth;
  final PcFeedback feedback;

  @override
  State<AccountScreen> createState() => _AccountScreenState();
}

class _AccountScreenState extends State<AccountScreen> {
  final _user = TextEditingController(), _password = TextEditingController(), _pin = TextEditingController(), _pin2 = TextEditingController();
  final _unlockPin = TextEditingController();
  bool _busy = false;
  String? _error;

  /// The person whose PIN is being typed, and what came of the last try.
  String? _who;
  String? _unlockError;

  @override
  void dispose() {
    for (final c in [_user, _password, _pin, _pin2, _unlockPin]) {
      c.dispose();
    }
    super.dispose();
  }

  bool get _canSignIn => !_busy && _user.text.trim().isNotEmpty && _password.text.isNotEmpty && (!widget.auth.usesPin || (_pin.text.isNotEmpty && _pin2.text.isNotEmpty));

  Future<void> _signIn() async {
    if (!_canSignIn) return;
    if (widget.auth.usesPin && _pin.text != _pin2.text) {
      setState(() => _error = 'The two PINs differ.');
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await widget.auth.login(_user.text, _password.text, pin: widget.auth.usesPin ? _pin.text : null);
      // The screen may have been closed while signing in: its fields are gone then.
      if (!mounted) return;
      _clearSecrets();
      widget.feedback.confirm();
      Navigator.of(context).pop();
    } on AuthException catch (e) {
      if (!mounted) return;
      _clearSecrets();
      setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _clearSecrets() {
    _password.clear();
    _pin.clear();
    _pin2.clear();
  }

  Future<void> _unlock(PcSession p) async {
    if (_busy || _unlockPin.text.isEmpty) return;
    setState(() => _busy = true);
    final PcUnlock r;
    try {
      r = await widget.auth.unlock(p.user, _unlockPin.text);
    } catch (_) {
      // The phone could not save: nothing was opened.
      if (mounted) {
        setState(() {
          _busy = false;
          _unlockError = 'This phone could not save. Try again.';
        });
      }
      return;
    }
    if (!mounted) return;
    _unlockPin.clear();
    final left = widget.auth.servers!.triesLeft(widget.auth.serverId!, p.user);
    setState(() {
      _busy = false;
      _unlockError = switch (r) {
        PcUnlock.open => null,
        PcUnlock.wrong => 'Wrong PIN. $left ${left == 1 ? 'try' : 'tries'} left.',
        PcUnlock.signedOut => 'Too many wrong PINs: ${p.name} is signed out. Sign in with the password.',
        PcUnlock.expired => '${p.name} was signed out after ${PcServers.signOutAfter.inMinutes} minutes without use. Sign in with the password.',
        PcUnlock.gone => 'That sign-in is no longer on this phone.',
      };
      if (r != PcUnlock.wrong) _who = null;
    });
    if (r == PcUnlock.open) {
      widget.feedback.confirm();
      Navigator.of(context).pop();
    } else {
      widget.feedback.undo();
    }
  }

  Future<void> _lock() async {
    if (_busy) return;
    await widget.auth.servers!.lock();
    widget.feedback.confirm();
  }

  Future<void> _signOut() async {
    if (_busy) return;
    setState(() => _busy = true);
    await widget.auth.logout();
    if (mounted) setState(() => _busy = false);
    widget.feedback.confirm();
  }

  TextField _pinField(TextEditingController c, String label, {TextInputAction action = TextInputAction.next, ValueChanged<String>? onSubmitted, bool autofocus = false}) => TextField(
        controller: c,
        enabled: !_busy,
        autofocus: autofocus,
        obscureText: true,
        autocorrect: false,
        enableSuggestions: false,
        keyboardType: TextInputType.number,
        inputFormatters: [FilteringTextInputFormatter.digitsOnly, LengthLimitingTextInputFormatter(pcPinMax)],
        textInputAction: action,
        decoration: InputDecoration(labelText: label, border: const OutlineInputBorder()),
        onChanged: (_) => setState(() {}),
        onSubmitted: onSubmitted,
      );

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final auth = widget.auth;
    final listen = Listenable.merge([auth, ?auth.servers]);
    return Scaffold(
      appBar: AppBar(title: const Text('Account')),
      body: ListenableBuilder(
        listenable: listen,
        builder: (context, _) {
          final s = auth.session;
          final people = auth.usesPin ? auth.servers!.people(auth.serverId!) : const <PcSession>[];
          return ListView(padding: const EdgeInsets.all(16), children: [
            if (s != null) ...[
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text('Signed in as ${s.name}', style: TextStyle(color: c.ink, fontSize: 18, fontWeight: FontWeight.w700)),
                    if (s.expires != null) Text('Until ${s.expires}', style: TextStyle(color: c.inkMuted)),
                    const SizedBox(height: 8),
                    Text('Everything this app shows from ${auth.base.host} follows your roles.', style: TextStyle(color: c.inkMuted)),
                    if (auth.usesPin) ...[
                      const SizedBox(height: 8),
                      Text(
                        'The phone locks when you leave the app or after ${PcServers.lockAfter.inMinutes} minutes untouched; your PIN opens it again. '
                        'After ${PcServers.signOutAfter.inMinutes} minutes without use you are signed out.',
                        style: TextStyle(color: c.inkMuted),
                      ),
                    ],
                  ]),
                ),
              ),
              const SizedBox(height: 16),
              if (auth.usesPin) ...[
                FilledButton.icon(icon: const Icon(Icons.lock_outline), label: const Text('Lock or switch person'), onPressed: _busy ? null : _lock),
                const SizedBox(height: 8),
              ],
              OutlinedButton.icon(icon: const Icon(Icons.logout), label: const Text('Sign out'), onPressed: _busy ? null : _signOut),
            ] else ...[
              if (auth.ended)
                Padding(
                  padding: const EdgeInsets.only(bottom: 12),
                  child: Text('! ${ApiAuth.endedMessage}', style: TextStyle(color: c.warn, fontWeight: FontWeight.w600)),
                ),
              if (_unlockError != null && _who == null)
                Padding(
                  padding: const EdgeInsets.only(bottom: 12),
                  child: Text('✗ $_unlockError', style: TextStyle(color: c.ng, fontWeight: FontWeight.w600)),
                ),
              if (people.isNotEmpty) ...[
                Text('Signed in on this phone', style: TextStyle(color: c.ink, fontSize: 18, fontWeight: FontWeight.w700)),
                const SizedBox(height: 8),
                for (final p in people)
                  Card(
                    child: Column(children: [
                      ListTile(
                        leading: const Icon(Icons.lock_outline),
                        title: Text(p.name),
                        subtitle: const Text('Open with your PIN'),
                        onTap: _busy
                            ? null
                            : () {
                                widget.feedback.tap();
                                _unlockPin.clear();
                                setState(() {
                                  _who = p.user;
                                  _unlockError = null;
                                });
                              },
                      ),
                      if (_who == p.user)
                        Padding(
                          padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
                          child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                            _pinField(_unlockPin, 'PIN', action: TextInputAction.done, autofocus: true, onSubmitted: (_) => _unlock(p)),
                            if (_unlockError != null)
                              Padding(
                                padding: const EdgeInsets.only(top: 8),
                                child: Text('✗ $_unlockError', style: TextStyle(color: c.ng, fontWeight: FontWeight.w600)),
                              ),
                            const SizedBox(height: 8),
                            FilledButton(onPressed: _busy || _unlockPin.text.isEmpty ? null : () => _unlock(p), child: const Text('Open')),
                          ]),
                        ),
                    ]),
                  ),
                const SizedBox(height: 24),
                Text('Someone else', style: TextStyle(color: c.ink, fontSize: 18, fontWeight: FontWeight.w700)),
                const SizedBox(height: 8),
              ],
              Text(
                auth.usesPin
                    ? 'Sign in with your own account to give verdicts on NG parts. Your password is not kept on this phone; the PIN you choose opens your sign-in here when the phone is locked.'
                    : 'Sign in with your own account to give verdicts on NG parts. Your password is not kept on this phone.',
                style: TextStyle(color: c.inkMuted),
              ),
              const SizedBox(height: 16),
              TextField(
                controller: _user,
                enabled: !_busy,
                autocorrect: false,
                enableSuggestions: false,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(labelText: 'User name', border: OutlineInputBorder()),
                onChanged: (_) => setState(() {}),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _password,
                enabled: !_busy,
                obscureText: true,
                autocorrect: false,
                enableSuggestions: false,
                textInputAction: auth.usesPin ? TextInputAction.next : TextInputAction.done,
                decoration: const InputDecoration(labelText: 'Password', border: OutlineInputBorder()),
                onChanged: (_) => setState(() {}),
                onSubmitted: auth.usesPin ? null : (_) => _signIn(),
              ),
              if (auth.usesPin) ...[
                const SizedBox(height: 12),
                _pinField(_pin, 'New PIN ($pcPinMin to $pcPinMax digits)'),
                const SizedBox(height: 12),
                _pinField(_pin2, 'New PIN again', action: TextInputAction.done, onSubmitted: (_) => _signIn()),
              ],
              if (_error != null)
                Padding(
                  padding: const EdgeInsets.only(top: 12),
                  child: Text('✗ $_error', style: TextStyle(color: c.ng, fontWeight: FontWeight.w600)),
                ),
              const SizedBox(height: 16),
              FilledButton(
                onPressed: _canSignIn ? _signIn : null,
                child: _busy ? const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2)) : const Text('Sign in'),
              ),
            ],
          ]);
        },
      ),
    );
  }
}
