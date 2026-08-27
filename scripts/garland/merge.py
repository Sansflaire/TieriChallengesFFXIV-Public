"""Turn the Garland sweep cache into data/curated/duties.json.

This writes a CURATED OVERLAY, never the generated dataset.

    scripts/gen-datasets   reads data/curated/duties.json  ->  data/duties.json

The overlay is an INPUT to generation, so regenerating is idempotent and can never destroy
curated work. The earlier design patched data/duties.json after the fact, which meant the next
regeneration silently wiped everything this script had done - see TODO A10.

Re-run this only when the sweep cache changes. Then re-run gen-datasets to fold it in.
"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
CACHE = os.path.join(HERE, 'garland-instances.json')
OUT_DIR = os.path.join(ROOT, 'data', 'curated')
OUT = os.path.join(OUT_DIR, 'duties.json')
DUTIES = os.path.join(ROOT, 'data', 'duties.json')

cache = json.load(open(CACHE, encoding='utf-8'))

# The overlay is keyed by the generated dataset's own key field ("id" = ContentFinderCondition
# row). The cache is keyed by garlandId, so the generated file supplies the mapping.
d = json.load(open(DUTIES, encoding='utf-8'))
inv = {v: k for k, v in d['fieldAliases'].items()}
gid_key, id_key = inv['garlandId'], inv['id']
gid_to_id = {}
for e in d['entries']:
    g, i = e.get(gid_key), e.get(id_key)
    if g is not None and i is not None:
        gid_to_id[str(g)] = i

entries = {}
stats = {'unlockQuest': 0, 'itemsFound': 0, 'fightCount': 0, 'timeLimitMinutes': 0, 'unmapped': 0}

for gid, g in cache.items():
    key = gid_to_id.get(str(gid))
    if key is None:
        stats['unmapped'] += 1
        continue

    patch = {}

    if g.get('unlockQuestName'):
        patch['unlockQuest'] = g['unlockQuestName']
        stats['unlockQuest'] += 1

    # Every item obtainable inside, as ONE comma-separated block: the grid shows a single
    # searchable cell and the plugin can substring-match without walking a list.
    items = {}
    for it in g.get('rewards') or []:
        if it.get('name'):
            items[it['id']] = it['name']
    for c in g.get('coffers') or []:
        for it in c.get('items') or []:
            if it.get('name'):
                items[it['id']] = it['name']
    for f in g.get('fights') or []:
        for it in f.get('items') or []:
            if it.get('name'):
                items[it['id']] = it['name']
    if items:
        patch['itemsFound'] = ', '.join(sorted(items.values()))
        stats['itemsFound'] += 1

    if g.get('fights'):
        patch['fightCount'] = len(g['fights'])
        stats['fightCount'] += 1
    if g.get('coffers'):
        patch['cofferCount'] = len(g['coffers'])
    if g.get('timeLimitMinutes'):
        patch['timeLimitMinutes'] = g['timeLimitMinutes']
        stats['timeLimitMinutes'] += 1
    if g.get('patch') is not None:
        patch['patch'] = g['patch']

    if patch:
        entries[str(key)] = patch

os.makedirs(OUT_DIR, exist_ok=True)
doc = {
    'schemaVersion': 1,
    'dataset': 'duties',
    'keyField': 'id',
    'source': 'garlandtools.org instance docs, swept 2026-08-26',
    'description': (
        'CURATED overlay for duties.json. NOT from game files. Read by scripts/gen-datasets '
        'during generation, never written by it. Safe to hand-edit: regenerating folds this in '
        'rather than overwriting it.'),
    'warning': (
        "'monsters' is deliberately absent: Garland exposes fight structure and chest contents "
        'but no creature names, confirmed across all 368 fetched instances. It needs a different '
        'source entirely - see TODO A6.'),
    'entryCount': len(entries),
    'entries': entries,
}
json.dump(doc, open(OUT, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)

print('wrote %s' % os.path.relpath(OUT, ROOT))
print('  overlay entries : %d' % len(entries))
for k, v in stats.items():
    print('  %-16s %d' % (k, v))
print('  size            : %.0f KB' % (os.path.getsize(OUT) / 1024))
print()
print('now re-run scripts/gen-datasets to fold this into data/duties.json')
