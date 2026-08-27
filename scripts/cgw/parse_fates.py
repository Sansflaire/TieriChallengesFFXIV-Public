"""Cached FATE pages -> data/curated/fates.cgw.json.

Fills the four gaps that were previously called irreducible or absent:

  monsters      <- {{FATE infobox | enemies }}
  bosses        <- {{FATE infobox | boss }}      OWN COLUMN, never mixed with the enemy list
  rewards       <- exp / gil / seals / bicolor gemstone / mettle / item-reward(1-4)
  chain order   <- prev-fate / next-fate.  The game's FATEChain GROUPS a chain but never
                   SEQUENCES it; these two fields are the sequence.

Matching is by name, disambiguated by zone where a name is shared. Our own zone column is now
populated (by the Fandom overlay) for exactly that purpose.

Overlay order note: 'fates.cgw.json' sorts before 'fates.wiki.json', so where BOTH wikis give
a field (zone, coordinates, type, duration) the Fandom values land last and win. That is
harmless - they agree in kind - and everything unique to this source is additive.
"""
import collections
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from infobox import fields as tpl_fields

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
CACHE = os.path.join(HERE, 'cache')
CURATED = os.path.join(ROOT, 'data', 'curated')

SOURCE = 'ffxiv.consolegameswiki.com Category:FATEs, swept 2026-08-27'


def norm(s):
    s = (s or '').lower().strip().replace('–', '-').replace('—', '-').replace('’', "'")
    s = re.sub(r'[^a-z0-9]+', ' ', s)
    return re.sub(r'\s+', ' ', s).strip()


def clean(s):
    if not s:
        return ''
    s = re.sub(r'\{\{i(?:tem icon)?\|([^{}|]*)[^{}]*\}\}', r'\1', s, flags=re.I)
    s = re.sub(r'\[\[[^\]|]*\|([^\]]*)\]\]', r'\1', s)
    s = re.sub(r'\[\[([^\]]*)\]\]', r'\1', s)
    s = re.sub(r'\{\{[^{}]*\}\}', '', s)
    s = re.sub(r'<[^>]+>', '', s)
    s = s.replace("'''", '').replace("''", '')
    return re.sub(r'\s+', ' ', s).strip()


def infobox(wt):
    """Depth-aware, because some pages put several fields on ONE line."""
    return tpl_fields(wt, 'fate infobox')


def main():
    pages = json.load(open(os.path.join(CACHE, '_fates.json'), encoding='utf-8'))
    print('cached FATE pages: %d' % len(pages))

    ours = json.load(open(os.path.join(ROOT, 'data', 'fates.json'), encoding='utf-8'))
    inv = {v: k for k, v in ours['fieldAliases'].items()}
    FID, FNAME, FZONE = inv['id'], inv['name'], inv.get('zone')

    by_name = collections.defaultdict(list)
    for e in ours['entries']:
        by_name[norm(e[FNAME])].append(e)

    entries = {}
    stats = collections.Counter()
    ambiguous = missing = 0

    for title, wt in sorted(pages.items()):
        if wt.strip().upper().startswith('#REDIRECT'):
            continue
        fb = infobox(wt)
        if not fb:
            continue
        name = clean(fb.get('title', '')) or title
        cands = by_name.get(norm(name)) or by_name.get(norm(title))
        if not cands:
            missing += 1
            continue

        zone = clean(fb.get('location', ''))
        if len(cands) > 1:
            # Disambiguate on zone. Our zone column is populated for exactly this.
            narrowed = [c for c in cands
                        if FZONE and isinstance(c.get(FZONE), str)
                        and norm(c[FZONE]) == norm(zone)]
            if len(narrowed) != 1:
                ambiguous += 1
                continue
            cands = narrowed
        row = cands[0]

        e = {}
        if zone:
            e['zone'] = zone
        x, y = clean(fb.get('location-x', '')), clean(fb.get('location-y', ''))
        if x and y:
            try:
                e['mapX'], e['mapY'] = float(x), float(y)
            except ValueError:
                pass
        t = clean(fb.get('type', ''))
        if t:
            e['fateType'] = t
        dur = clean(fb.get('duration', ''))
        if dur:
            e['timeLimitMinutes'] = dur

        # Bosses in their OWN column - the standing rule for every dataset holding both.
        boss = clean(fb.get('boss', ''))
        if boss:
            e['bosses'] = ', '.join(p.strip() for p in re.split(r'[,;]', boss) if p.strip())
            stats['bosses'] += 1
        foes = clean(fb.get('enemies', ''))
        if foes:
            e['monsters'] = ', '.join(p.strip() for p in re.split(r'[,;]', foes) if p.strip())
            stats['monsters'] += 1

        # Rewards, flattened into one readable column rather than a nested blob.
        rw = []
        for key, label in (('exp', 'EXP'), ('gil', 'Gil'), ('seals', 'Seals'),
                           ('bicolor gemstone', 'Bicolor Gemstones'), ('mettle', 'Mettle')):
            v = clean(fb.get(key, ''))
            if v and v not in ('0',):
                rw.append('%s %s' % (label, v))
        items = []
        for k, v in fb.items():
            if k.startswith('item-reward'):
                iv = clean(v)
                if iv:
                    items.append(iv)
        if items:
            rw.append('Items: ' + ', '.join(items))
        if rw:
            e['rewards'] = '; '.join(rw)
            stats['rewards'] += 1

        prev, nxt = clean(fb.get('prev-fate', '')), clean(fb.get('next-fate', ''))
        if prev:
            e['chainPrevious'] = prev
        if nxt:
            e['chainNext'] = nxt
        if prev or nxt:
            stats['chainOrder'] += 1
            e['chainOrder'] = 'after: %s' % prev if prev else 'first in chain'
            if nxt:
                e['chainOrder'] += ' | before: %s' % nxt

        patch = clean(fb.get('patch', ''))
        if patch:
            e['patch'] = patch

        if e:
            entries[str(row[FID])] = e

    print('matched FATEs : %d' % len(entries))
    print('  ambiguous   : %d  (shared name, zone did not resolve it)' % ambiguous)
    print('  not in ours : %d' % missing)
    for k in ('bosses', 'monsters', 'rewards', 'chainOrder'):
        print('  with %-11s: %d' % (k, stats[k]))

    os.makedirs(CURATED, exist_ok=True)
    doc = {
        'schemaVersion': 1, 'dataset': 'fates', 'keyField': 'id', 'source': SOURCE,
        'description': ('CURATED overlay for fates.json from the FFXIV Console Games Wiki. '
                        'Supplies boss, enemies, rewards and CHAIN ORDER (prev-fate/next-fate), '
                        'none of which the client sheets contain.'),
        'warning': ('Matched by name, disambiguated by zone. FATEs whose name is shared and '
                    'whose zone does not resolve it are skipped rather than guessed. '
                    'chainOrder/chainPrevious/chainNext come from the wiki, not from FATEChain '
                    '- the sheet groups a chain but never sequences it.'),
        'entryCount': len(entries), 'entries': entries,
    }
    p = os.path.join(CURATED, 'fates.cgw.json')
    json.dump(doc, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    print('wrote %s  %.0f KB' % (os.path.relpath(p, ROOT), os.path.getsize(p) / 1024))


if __name__ == '__main__':
    main()
