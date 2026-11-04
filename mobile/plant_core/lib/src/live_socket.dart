import 'dart:async';
import 'dart:io';

import 'cert_trust.dart';

/// One open socket to a plant server, as the live client sees it: the
/// dart:io WebSocket in the app, a fake in tests.
abstract class PcSocket {
  /// Text messages; done when the socket closes.
  Stream<dynamic> get messages;

  /// The server's close code, once [messages] is done (4401: the credential was refused).
  int? get closeCode;

  Future<void> close();
}

/// Opens a socket to [url], or throws.
typedef PcSocketConnect = Future<PcSocket> Function(Uri url);

/// Sockets that trust certificates as the app's HTTP client does: a
/// certificate the phone does not trust only by its pinned fingerprint
/// (requirements 17.3, MOB-TLS-02). A ping every [ping] closes a link that
/// died without a close (a phone that lost its network).
PcSocketConnect pcSocketConnect(PcCertTrust trust, {Duration ping = const Duration(seconds: 10)}) => (url) async {
      final client = trust.client();
      try {
        final ws = await WebSocket.connect(url.toString(), customClient: client).timeout(const Duration(seconds: 15));
        ws.pingInterval = ping;
        return _IoSocket(ws);
      } finally {
        // The upgraded connection is detached from the client: closing it only frees the client.
        client.close();
      }
    };

class _IoSocket implements PcSocket {
  _IoSocket(this._ws);
  final WebSocket _ws;
  @override
  Stream<dynamic> get messages => _ws;
  @override
  int? get closeCode => _ws.closeCode;
  @override
  Future<void> close() => _ws.close();
}

/// A socket that stays open (requirements 17.3, LV-05): it reconnects after
/// [first], twice that, and so on up to [most], and the wait starts again
/// from [first] once a message came in, so a server that accepts and then
/// closes at once is not hammered. [url] is read at every attempt (null:
/// do not connect), so a new credential is used as soon as [start] is called.
/// [live] is true from the first message until the socket closes.
class PcLiveSocket {
  PcLiveSocket({
    required this.connect,
    required this.url,
    required this.onMessage,
    this.onClose,
    this.first = const Duration(seconds: 2),
    this.most = const Duration(seconds: 60),
  });

  final PcSocketConnect connect;
  final Uri? Function() url;
  final void Function(String message) onMessage;

  /// After a socket that had opened closes: the [Uri] it was opened with and the server's close code.
  final void Function(Uri url, int? code)? onClose;
  final Duration first, most;

  PcSocket? _socket;
  StreamSubscription<dynamic>? _sub;
  Timer? _retry;
  int _failures = 0, _gen = 0;
  bool _live = false, _stopped = true;

  bool get live => _live;

  /// The address of the socket open or being opened, null when none.
  Uri? get current => _current;
  Uri? _current;

  /// Connects now, dropping any socket open with an older address.
  void start() {
    _stopped = false;
    _failures = 0;
    _open();
  }

  /// Closes the socket and stops reconnecting.
  void stop() {
    _stopped = true;
    _drop();
  }

  void _drop() {
    _gen++;
    _retry?.cancel();
    _retry = null;
    _sub?.cancel();
    _sub = null;
    _socket?.close().catchError((_) {});
    _socket = null;
    _live = false;
    _current = null;
  }

  Future<void> _open() async {
    _drop();
    final my = _gen, u = url();
    if (u == null) return;
    _current = u;
    final PcSocket s;
    try {
      s = await connect(u);
    } catch (_) {
      if (my == _gen) _later();
      return;
    }
    if (my != _gen) {
      s.close().catchError((_) {});
      return;
    }
    _socket = s;
    _sub = s.messages.listen(
      (m) {
        if (my != _gen || m is! String) return;
        _live = true;
        _failures = 0;
        onMessage(m);
      },
      onDone: () => _closed(my, s, u),
      onError: (_) {}, // the close follows
      cancelOnError: false,
    );
  }

  void _closed(int my, PcSocket s, Uri u) {
    if (my != _gen) return;
    _socket = null;
    _sub = null;
    _live = false;
    onClose?.call(u, s.closeCode);
    // onClose may have started a new socket (a new credential).
    if (my == _gen) _later();
  }

  void _later() {
    if (_stopped) return;
    _current = null;
    final wait = first * (1 << _failures.clamp(0, 10));
    _failures++;
    _retry = Timer(wait < most ? wait : most, _open);
  }
}
