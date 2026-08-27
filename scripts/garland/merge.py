"""Fold the Garland sweep cache into data/duties.json.

Curated third-party data is merged into the generated file here rather than fetched by the
generator: the generator must stay runnable offline and must not depend on someone else's
service. Re-running gen-datasets will overwrite these fields, which is exactly the problem
TODO A10 (generated vs curated split) exists to solve - until it is decided, run this after
every regeneration.
"""
import json
import os

ROOT = r'C:\Users\trist\AppData\Roaming\XIVLauncher\devPlugins\TieriChallengesFFXIV'
HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, 'garland-instances.json')
DUTIES = os.path.join(ROOT, 'data', 'duties.json')

cache = json.load(open(CACHE, encoding='utf-8'))
d = json.load(open(DUTIES, encoding='utf-8'))

alias = d['fieldAliases']                      # alias -> real
inv = {v: k for k, v in alias.items()}         # real  -> alias
nxt = [0]


def new_alias():
    """Continue the generator's a..z, aa.. scheme from wherever it stopped."""
    used = set(alias.keys())
    i = 0
    while True:
        s, n = '', i
        while True:
            s = chr(ord('a') + n % 26) + s
            n = n // 26 - 1
            if n < 0:
                break
        if s not in used:
            used.add(s)
            return s
        i += 1


def ensure(real):
    if real in inv:
        return inv[real]
    a = new_alias()
    alias[a] = real
    inv[real] = a
    return a


# New curated columns
A_UNLOCK_Q = ensure('unlockQuest')
A_ITEMS = ensure('itemsFound')
A_COFFERS = ensure('cofferCount')
A_FIGHTS = ensure('fightCount')
A_TIME = ensure('timeLimitMinutes')
A_PATCH = ensure('patch')
A_SRC = ensure('curatedSource')

gid_key = inv['garlandId']
name_key = inv['name']

filled = {'unlock': 0, 'items': 0, 'fights': 0, 'time': 0, 'missing': 0}

for e in d['entries']:
    gid = e.get(gid_key)
    g = cache.get(str(gid)) if gid else None
    if not g:
        filled['missing'] += 1
        continue

    uq = g.get('unlockQuestName') or ''
    if uq:
        e[A_UNLOCK_Q] = uq
        filled['unlock'] += 1

    # Every item obtainable inside: the general reward pool plus each coffer and fight chest,
    # de-duplicated by id and kept as "name" strings so the grid is readable.
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
        # ONE comma-separated block rather than an array: the grid shows it as a single
        # searchable cell, and the plugin can substring-match it without walking a list.
        e[A_ITEMS] = ', '.join(sorted(items.values()))
        filled['items'] += 1

    if g.get('coffers'):
        e[A_COFFERS] = len(g['coffers'])
    if g.get('fights'):
        e[A_FIGHTS] = len(g['fights'])
        filled['fights'] += 1
    if g.get('timeLimitMinutes'):
        e[A_TIME] = g['timeLimitMinutes']
        filled['time'] += 1
    if g.get('patch') is not None:
        e[A_PATCH] = g['patch']
    e[A_SRC] = 'garlandtools.org'

# The header must stop claiming these are unknown.
d['unknownFields'] = ['monsters (Garland does not expose mob lists)',
                      'unlock for duties with no unlockQuest on either source']
d['needsVerification'] = (
    'PARTIAL - unlock, itemsFound, fightCount, timeLimitMinutes and patch are CURATED from '
    'garlandtools.org, not from game files, and are only as current as the sweep date. '
    "'monsters' remains ??? for every entry: Garland does not publish mob lists, so it needs a "
    'different source entirely (TODO A6). Re-running scripts/gen-datasets OVERWRITES all curated '
    'fields - re-run scripts/merge-garland afterwards until TODO A10 splits generated from curated.')
d['curatedFrom'] = 'garlandtools.org instance docs'
d['omittedAlwaysUnknown'] = [x for x in d.get('omittedAlwaysUnknown', []) if x != 'itemsFound']

json.dump(d, open(DUTIES, 'w', encoding='utf-8'), ensure_ascii=False)

print('merged %d cached instances into %d duties' % (len(cache), len(d['entries'])))
for k, v in filled.items():
    print('   %-8s %d' % (k, v))
print('file size: %.0f KB' % (os.path.getsize(DUTIES) / 1024))
