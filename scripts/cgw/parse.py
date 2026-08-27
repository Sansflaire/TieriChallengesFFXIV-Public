"""Turn the cached consolegameswiki enemy pages into data/curated/monsters.cgw.json.

THE ONLY SOURCE FOUND FOR MONSTER LOOT.
Drop tables are server-side: all 1,198 client sheet types were scanned and every BNpc* sheet
has zero item references (TODO Q11/R6, settled). The Fandom enemy tables do not carry drops
either. This wiki does.

COLUMN REUSE, NOT COLUMN DUPLICATION
------------------------------------
This wiki's `race`/`clan` are the same taxonomy as the Fandom tables' creatureClass/family
(Beastkin / Antelope for Antelope Doe). They are written into the SAME columns rather than
added as new ones - a second column holding the same fact is the thing that made
`unlockQuestFromGameData` useless.

Overlay order is bare -> .boss -> .cgw (alphabetical), so where both wikis know a field this
one wins. That is deliberate: it covers 8,570 monsters against Fandom's 755 for location.

AGGRESSION IS DECODED FROM THE TEMPLATE, NOT GUESSED
----------------------------------------------------
Template:NPC infobox does: first character 'p' => Passive, anything else => Aggressive; the
remaining character is an aggression RANK 1-6. Hence 'p1' = Passive rank 1, 'a4' = Aggressive
rank 4. The odd 'r5' renders as Aggressive by that same rule, so it is followed exactly.
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

SOURCE = 'ffxiv.consolegameswiki.com Category:Enemies, swept 2026-08-27'


def norm(s):
    s = (s or '').lower().strip().replace('–', '-').replace('—', '-').replace('’', "'")
    s = re.sub(r'[^a-z0-9]+', ' ', s)
    return re.sub(r'\s+', ' ', s).strip()


def clean(s):
    """Wiki value -> plain text."""
    if not s:
        return ''
    s = re.sub(r'\[\[[^\]|]*\|([^\]]*)\]\]', r'\1', s)
    s = re.sub(r'\[\[([^\]]*)\]\]', r'\1', s)
    s = re.sub(r'\{\{[^{}]*\}\}', '', s)
    s = re.sub(r'<[^>]+>', '', s)
    s = s.replace("'''", '').replace("''", '')
    return re.sub(r'\s+', ' ', s).strip()


def infobox_fields(wt):
    """Depth-aware, because some pages put several fields on ONE line."""
    return tpl_fields(wt, 'npc infobox')


def main():
    pages = json.load(open(os.path.join(CACHE, '_pages.json'), encoding='utf-8'))
    print('cached pages: %d' % len(pages))

    mon = json.load(open(os.path.join(ROOT, 'data', 'monsters.json'), encoding='utf-8'))
    inv = {v: k for k, v in mon['fieldAliases'].items()}
    MID, MNAME, WNAME = inv['id'], inv['name'], inv.get('wikiName')

    by_name = {}
    for e in mon['entries']:
        for k in (MNAME, WNAME):
            if not k:
                continue
            v = e.get(k)
            if isinstance(v, str) and v and v != '???':
                by_name.setdefault(norm(v), e[MID])

    entries = {}
    stats = collections.Counter()
    unmatched = 0

    for title, wt in sorted(pages.items()):
        if wt.strip().upper().startswith('#REDIRECT'):
            continue
        mid = by_name.get(norm(title))
        if mid is None:
            unmatched += 1
            continue

        fb = infobox_fields(wt)
        e = {}

        # ---- loot: the reason this source exists ----
        drops = []
        for m in re.finditer(r'\{\{Drops table row\s*\|([^}]*)\}\}', wt, re.I):
            parts = [p.strip() for p in m.group(1).split('|')]
            item = clean(parts[0])
            if not item:
                continue
            qty = ''
            for p in parts[1:]:
                if '=' in p:
                    continue
                if re.match(r'^\d+(-\d+)?$', p):
                    qty = p
            drops.append('%s x%s' % (item, qty) if qty and qty != '1' else item)
        # MOST MONSTERS DROP NOTHING, and "nothing" is not "unknown".
        #
        # ??? means we do not know. Leaving it on a mob that genuinely drops nothing would be
        # a false unknown, and a generator reading it would keep hunting for data that does
        # not exist. The page tells us which case we are in:
        #
        #   drop rows present            -> the list
        #   Loot section, but no rows    -> "None": documented, and it drops nothing
        #   no loot markup at all        -> leave ???, genuinely undocumented
        has_loot_section = bool(re.search(r'\{\{Drops table header|==\s*Loot\s*==', wt, re.I))
        if drops:
            seen, uniq = set(), []
            for d in drops:
                if d not in seen:
                    seen.add(d)
                    uniq.append(d)
            e['drops'] = ', '.join(uniq)
            stats['drops'] += 1
        elif has_loot_section:
            e['drops'] = 'None'
            stats['dropsNone'] += 1
        else:
            stats['dropsUnknown'] += 1

        # ---- locations: zone plus coordinates plus a level band, repeated ----
        zones, locs = [], []
        for m in re.finditer(r'\{\{NPC location info\s*\|([^}]*)\}\}', wt, re.I):
            parts = [clean(p) for p in m.group(1).split('|')]
            z = parts[0] if parts else ''
            if not z:
                continue
            if z not in zones:
                zones.append(z)
            co = parts[1] if len(parts) > 1 else ''
            locs.append('%s (%s)' % (z, co) if co else z)
        if not zones and clean(fb.get('location', '')):
            z = clean(fb['location'])
            zones.append(z)
            co = clean(fb.get('coordinates', ''))
            locs.append('%s (%s)' % (z, co) if co else z)
        if zones:
            e['zones'] = ', '.join(zones)
            e['zonesSource'] = 'FFXIV Console Games Wiki'
            stats['zones'] += 1
        if locs:
            e['mapLocation'] = ', '.join(dict.fromkeys(locs))
            stats['mapLocation'] += 1

        # ---- taxonomy: SAME columns the Fandom tables use, never parallel ones ----
        race = clean(fb.get('race', ''))
        clan = clean(fb.get('clan', ''))
        if race:
            e['creatureClass'] = race
        if clan:
            e['family'] = clan
        lvl = clean(fb.get('level', ''))
        if lvl:
            e['level'] = lvl
            stats['level'] += 1

        agg = clean(fb.get('aggression', ''))
        if agg:
            e['aggression'] = 'Passive' if agg[:1].lower() == 'p' else 'Aggressive'
            if len(agg) > 1 and agg[1:].isdigit():
                e['aggressionRank'] = int(agg[1:])
            stats['aggression'] += 1

        patch = clean(fb.get('patch', ''))
        if patch:
            e['patch'] = patch
        rank = clean(fb.get('rank', ''))
        if rank:
            e['huntRank'] = rank.upper()
            stats['huntRank'] += 1
        if clean(fb.get('objective', '')).lower() == 'boss':
            e['isBoss'] = True
            stats['isBoss'] += 1

        quests = [clean(m.group(1).split('|')[0])
                  for m in re.finditer(r'\{\{quest list row\s*\|([^}]*)\}\}', wt, re.I)]
        quests = [q for q in dict.fromkeys(quests) if q]
        if quests:
            e['quests'] = ', '.join(quests)

        if e:
            entries[str(mid)] = e

    print('matched monsters : %d' % len(entries))
    print('unmatched pages  : %d' % unmatched)
    for k in ('drops', 'dropsNone', 'dropsUnknown', 'zones', 'mapLocation', 'level',
              'aggression', 'huntRank', 'isBoss'):
        print('  with %-12s : %d' % (k, stats[k]))

    os.makedirs(CURATED, exist_ok=True)
    doc = {
        'schemaVersion': 1, 'dataset': 'monsters', 'keyField': 'id', 'source': SOURCE,
        'description': ('CURATED overlay for monsters.json from the FFXIV Console Games Wiki '
                        '(Gamer Escape). The ONLY source found for monster LOOT, plus much '
                        'wider location coverage than the Fandom tables.'),
        'warning': ('MOST MONSTERS DROP NOTHING, and that is recorded as drops="None", NOT as '
                    '???. ??? is reserved for monsters this wiki does not document at all. A '
                    'page with a Loot section and no rows is treated as documented-empty. '
                    'Locations are far better covered than loot. '
                    'race/clan are written into the EXISTING creatureClass/family columns, not '
                    'parallel ones. Aggression is decoded per Template:NPC infobox: leading "p" '
                    'is Passive, anything else Aggressive; the digit is a rank 1-6.'),
        'entryCount': len(entries), 'entries': entries,
    }
    p = os.path.join(CURATED, 'monsters.cgw.json')
    json.dump(doc, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    print('wrote %s  %.0f KB' % (os.path.relpath(p, ROOT), os.path.getsize(p) / 1024))


if __name__ == '__main__':
    main()
