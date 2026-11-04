import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plant_core/plant_core.dart';

// Requirements 17.3, MOB-TLS-01..03: a local TLS server on a throw-away
// certificate from a throw-away CA, made with openssl for this run only (no
// key in the repository). Without the CA it is as untrusted as a
// self-signed one.
void main() {
  final dir = Directory.systemTemp.createTempSync('cert_trust_test');
  String? noOpenssl;
  try {
    // A throw-away CA (the "company CA" a phone may trust) signs the server's certificate.
    final d = dir.path;
    File('$d/ext.cnf').writeAsStringSync('subjectAltName=DNS:localhost,IP:127.0.0.1\nextendedKeyUsage=serverAuth\nbasicConstraints=CA:FALSE\n');
    for (final args in [
      ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=Test CA', '-keyout', '$d/ca.key', '-out', '$d/ca.pem'],
      ['req', '-newkey', 'rsa:2048', '-nodes', '-subj', '/CN=localhost', '-keyout', '$d/key.pem', '-out', '$d/req.pem'],
      ['x509', '-req', '-in', '$d/req.pem', '-CA', '$d/ca.pem', '-CAkey', '$d/ca.key', '-CAcreateserial', '-days', '1', '-extfile', '$d/ext.cnf', '-out', '$d/cert.pem'],
    ]) {
      final r = Process.runSync('openssl', args);
      if (r.exitCode != 0) {
        noOpenssl = 'openssl failed: ${r.stderr}';
        break;
      }
    }
  } on ProcessException catch (e) {
    noOpenssl = 'no openssl: $e';
  }
  late List<int> certPem;
  late String fingerprint;
  HttpServer? server;

  setUpAll(() async {
    if (noOpenssl != null) return;
    certPem = await File('${dir.path}/cert.pem').readAsBytes();
    final b64 = utf8.decode(certPem).split('\n').where((l) => l.isNotEmpty && !l.startsWith('-----')).join();
    fingerprint = sha256.convert(base64.decode(b64)).toString();
    final ctx = SecurityContext()
      ..useCertificateChainBytes(certPem)
      ..usePrivateKeyBytes(await File('${dir.path}/key.pem').readAsBytes());
    server = await HttpServer.bindSecure(InternetAddress.loopbackIPv4, 0, ctx);
    server!.listen((req) => req.response
      ..write('ok')
      ..close());
  });

  tearDownAll(() async {
    await server?.close(force: true);
    await dir.delete(recursive: true);
  });

  PcServer at(String host, int port, String? fp) => PcServer(id: 'a', product: PcProduct.vision, name: 'Unit 1', baseUrl: Uri.parse('https://$host:$port'), fingerprint: fp);

  Future<String> get(PcCertTrust trust, {SecurityContext? context}) async {
    final c = trust.client(context: context);
    try {
      final res = await (await c.getUrl(Uri.parse('https://localhost:${server!.port}/'))).close();
      return await res.transform(utf8.decoder).join();
    } finally {
      c.close(force: true);
    }
  }

  test('the pinned certificate connects', () async {
    expect(await get(PcCertTrust(() => [at('localhost', server!.port, fingerprint)])), 'ok');
  }, skip: noOpenssl);

  test('no fingerprint, a wrong one, or one pinned for another port or host fails', () async {
    final wrong = '0' * 64;
    for (final list in [
      [at('localhost', server!.port, null)],
      [at('localhost', server!.port, wrong)],
      [at('localhost', server!.port + 1, fingerprint)],
      [at('plant.local', server!.port, fingerprint)],
      <PcServer>[],
    ]) {
      await expectLater(get(PcCertTrust(() => list)), throwsA(isA<HandshakeException>()), reason: '$list');
    }
  }, skip: noOpenssl);

  test('a certificate the phone trusts works even with a wrong fingerprint pinned', () async {
    final trusted = SecurityContext()..setTrustedCertificatesBytes(File('${dir.path}/ca.pem').readAsBytesSync());
    expect(await get(PcCertTrust(() => [at('localhost', server!.port, '0' * 64)]), context: trusted), 'ok');
  }, skip: noOpenssl);

  test('servers added later count at the next connection', () async {
    final list = <PcServer>[];
    final trust = PcCertTrust(() => list);
    await expectLater(get(trust), throwsA(isA<HandshakeException>()));
    list.add(at('localhost', server!.port, fingerprint));
    expect(await get(trust), 'ok');
  }, skip: noOpenssl);

  test('a saved server given a new fingerprint counts at the next connection; its key stays (MOB-TLS-05)', () async {
    final servers = PcServers(MemoryStore(), MemoryStore());
    final trust = PcCertTrust(() => servers.list);
    await servers.add(at('localhost', server!.port, 'f' * 64), token: 'key');
    await expectLater(get(trust), throwsA(isA<HandshakeException>())); // the old certificate's fingerprint
    await servers.add(at('localhost', server!.port, fingerprint)); // as the home screen saves it
    expect(await get(trust), 'ok');
    expect(await servers.token('a'), 'key');
  }, skip: noOpenssl);

  test('fingerprints are read with colons, spaces and either case', () {
    final hex = List.generate(32, (i) => i.toRadixString(16).padLeft(2, '0'));
    expect(pcFingerprint(hex.join(':').toUpperCase()), hex.join());
    expect(pcFingerprint(' ${hex.join(' ')} '), hex.join());
    expect(pcFingerprint(hex.join().substring(2)), isNull);
    expect(pcFingerprint('${hex.join()}zz'.substring(2)), isNull);
    expect(pcFingerprintProblem(''), isNull);
    expect(pcFingerprintProblem('abc'), isNotNull);
  });

  test('a server keeps its fingerprint; a damaged one is dropped', () {
    final s = at('plant.local', 443, 'a' * 64);
    expect(PcServer.fromJson(s.toJson())!.fingerprint, 'a' * 64);
    expect(PcServer.fromJson({...s.toJson(), 'fingerprint': 'nope'})!.fingerprint, isNull);
    expect(at('plant.local', 443, null).toJson().containsKey('fingerprint'), isFalse);
  });

  test('a fingerprint shows as colon pairs, shortened to its first and last four bytes (MOB-TLS-05)', () {
    final fp = pcFingerprint('${'ab' * 4}${'00' * 20}0123456789abcdef')!;
    expect(pcFingerprintColons(fp), '${List.filled(4, 'AB').join(':')}:${List.filled(20, '00').join(':')}:01:23:45:67:89:AB:CD:EF');
    expect(pcFingerprintShort(fp), 'AB:AB:AB:AB … 89:AB:CD:EF');
  });
}
