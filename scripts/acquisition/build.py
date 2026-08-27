"""Compose gear.acquisition by cross-referencing the datasets we already have.

NO FETCHING. This is a pure join over data already on disk:

    gear.craftable          (game truth)      -> "Crafted"
    duties.itemsFound       (Garland sweep)   -> "Duty: The Aurum Vale, ..."
    monsters.drops          (Console Games)   -> "Drops from: Antelope Doe, ..."
    fates.rewards           (Console Games)   -> "FATE: I Melt with You, ..."

Output: data/curated/gear.acquisition.json

WHY `craftable` IS A SEPARATE COLUMN
------------------------------------
This script READS gear.json and WRITES an overlay that gear.json then absorbs, so anything it
depends on must survive its own output. Craftability used to exist only as the string
"Crafted" inside `acquisition`; the first pass would overwrite that, and the second pass could
no longer tell a crafted item from any other. The generator now emits a `craftable` boolean
that no overlay touches, so the composed string rebuilds identically every time. Verified by
running the whole chain twice and diffing.

WHAT IS STILL ??? AFTERWARDS IS INFORMATIVE
-------------------------------------------
An item none of these sources mentions is not a hole in the join - it is an item acquired some
other way entirely: relic steps, tomestone and vendor purchases, seasonal events, Gold Saucer,
PvP, crafting-only intermediates. Those need their own source, not a better match.
"""
import collections
import json
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
DATA = os.path.join(ROOT, 'data')
CURATED = os.path.join(DATA, 'curated')

SOURCE = 'cross-reference of duties.itemsFound, monsters.drops and fates.rewards (local join)'
MAX_LISTED = 8      # then "+N more" - a 40-duty list is unreadable and unsearchable


def load(name):
    d = json.load(open(os.path.join(DATA, name + '.json'), encoding='utf-8'))
    return d, {v: k for k, v in d['fieldAliases'].items()}


def norm(s):
    s = (s or '').lower().strip().replace('’', "'")
    s = re.sub(r'\s*x\d+(-\d+)?\s*$', '', s)      # "Beast Sinew x2" -> "beast sinew"
    s = re.sub(r'^\s*\d+\s+', '', s)               # "1 Curtain Call" -> "curtain call"
    s = re.sub(r'[^a-z0-9]+', ' ', s)
    return re.sub(r'\s+', ' ', s).strip()


def joined(names):
    names = sorted(set(names))
    if len(names) <= MAX_LISTED:
        return ', '.join(names)
    return ', '.join(names[:MAX_LISTED]) + ' (+%d more)' % (len(names) - MAX_LISTED)


def main():
    gear, gi = load('gear')
    GID, GNAME, GCRAFT = gi['itemId'], gi['name'], gi.get('craftable')

    # item name -> [gear itemIds]. Names repeat across HQ/NQ and re-releases, so a name can
    # legitimately hit several rows; all of them get the same acquisition.
    by_name = collections.defaultdict(list)
    for e in gear['entries']:
        n = norm(e.get(GNAME, ''))
        if n:
            by_name[n].append(e[GID])
    print('gear entries: %d, distinct names: %d' % (len(gear['entries']), len(by_name)))

    from_duty = collections.defaultdict(set)
    from_mob = collections.defaultdict(set)
    from_fate = collections.defaultdict(set)

    # ---- duties ----
    duties, di = load('duties')
    DNAME, DITEMS = di['name'], di['itemsFound']
    hit = 0
    for e in duties['entries']:
        v = e.get(DITEMS)
        if not isinstance(v, str) or v in ('', '???'):
            continue
        for item in v.split(','):
            key = norm(item)
            if key in by_name:
                from_duty[key].add(e[DNAME])
                hit += 1
    print('duty item mentions matched to gear: %d, distinct items: %d' % (hit, len(from_duty)))

    # ---- monster drops ----
    mons, mi = load('monsters')
    MNAME, MDROPS, MWIKI = mi['name'], mi.get('drops'), mi.get('wikiName')

    def mob_label(e):
        """Prefer the wiki's proper-case name. str.title() mangles digits: our internal
        name '2nd cohort eques' becomes '2Nd Cohort Eques'."""
        if MWIKI:
            w = e.get(MWIKI)
            if isinstance(w, str) and w and w != '???':
                return w
        raw = str(e.get(MNAME, ''))
        return re.sub(r'(?<![0-9A-Za-z])([a-z])', lambda m: m.group(1).upper(), raw)
    if MDROPS:
        for e in mons['entries']:
            v = e.get(MDROPS)
            if not isinstance(v, str) or v in ('', '???', 'None'):
                continue
            for item in v.split(','):
                key = norm(item)
                if key in by_name:
                    from_mob[key].add(mob_label(e))
    print('items matched to a monster drop  : %d' % len(from_mob))

    # ---- FATE rewards ----
    fates, fi = load('fates')
    FNAME, FREW = fi['name'], fi.get('rewards')
    if FREW:
        for e in fates['entries']:
            v = e.get(FREW)
            if not isinstance(v, str) or v in ('', '???'):
                continue
            m = re.search(r'Items:\s*(.+)$', v)
            if not m:
                continue
            for item in m.group(1).split(','):
                key = norm(item)
                if key in by_name:
                    from_fate[key].add(e[FNAME])
    print('items matched to a FATE reward   : %d' % len(from_fate))

    # ---- compose ----
    entries = {}
    stats = collections.Counter()
    for e in gear['entries']:
        key = norm(e.get(GNAME, ''))
        parts = []
        if GCRAFT and e.get(GCRAFT) is True:
            parts.append('Crafted')
        if from_duty.get(key):
            parts.append('Duty: ' + joined(from_duty[key]))
        if from_mob.get(key):
            parts.append('Drops from: ' + joined(from_mob[key]))
        if from_fate.get(key):
            parts.append('FATE: ' + joined(from_fate[key]))
        if not parts:
            continue
        entries[str(e[GID])] = {'acquisition': ' | '.join(parts)}
        for p in parts:
            stats[p.split(':')[0]] += 1

    print()
    print('gear rows with an acquisition: %d of %d' % (len(entries), len(gear['entries'])))
    for k, v in stats.most_common():
        print('  %-14s %d' % (k, v))
    print('  still ???      %d' % (len(gear['entries']) - len(entries)))

    os.makedirs(CURATED, exist_ok=True)
    doc = {
        'schemaVersion': 1, 'dataset': 'gear', 'keyField': 'itemId', 'source': SOURCE,
        'description': ('CURATED overlay: gear.acquisition, composed by cross-referencing '
                        'duties.itemsFound, monsters.drops and fates.rewards against gear '
                        'names, plus the game-derived craftable flag.'),
        'warning': ('Matched by ITEM NAME, because that is what the duty/drop/reward lists '
                    'record. An item still ??? is not a failed match - it is acquired some '
                    'other way (relic, tomestone, vendor, seasonal, Gold Saucer, PvP), which '
                    'needs its own source. Duty lists come from Garland and are as complete as '
                    'that sweep; a missing duty here does not prove the item does not drop.'),
        'entryCount': len(entries), 'entries': entries,
    }
    p = os.path.join(CURATED, 'gear.acquisition.json')
    json.dump(doc, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    print('wrote %s  %.0f KB' % (os.path.relpath(p, ROOT), os.path.getsize(p) / 1024))


if __name__ == '__main__':
    main()
