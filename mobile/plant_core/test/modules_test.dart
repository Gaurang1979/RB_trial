import 'package:flutter_test/flutter_test.dart';
import 'package:plant_core/plant_core.dart';

void main() {
  test('servers are kept with their product; tokens apart from the list', () async {
    final settings = MemoryStore(), secrets = MemoryStore();
    final s = PcServers(settings, secrets);
    await s.load();
    await s.add(PcServer(id: 'a', product: PcProduct.vision, name: 'Unit 1', baseUrl: Uri.parse('https://plant.local')), token: 'secret-token');
    expect(settings.values['servers'], isNot(contains('secret-token')));
    final again = PcServers(settings, secrets);
    await again.load();
    expect(again.list.single.product, PcProduct.vision);
    expect(await again.token('a'), 'secret-token');
    await again.remove('a');
    expect(again.list, isEmpty);
    expect(await again.token('a'), isNull);
  });

  test('a damaged or unknown entry is skipped, not fatal', () async {
    final settings = MemoryStore()
      ..values['servers'] = '[{"id":"x","product":"telepathy","name":"n","base_url":"https://a"},'
          '{"id":"y","product":"production","name":"Line data","base_url":"https://pm.local"}, 42]';
    final s = PcServers(settings, MemoryStore());
    await s.load();
    expect(s.list.single.product, PcProduct.production);
    settings.values['servers'] = 'not json';
    await s.load();
    expect(s.list, isEmpty);
  });
}
