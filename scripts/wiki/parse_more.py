"""Build overlays from the bosses / FATEs / zone-page caches.

    cache/_bosses.json  -> data/curated/monsters.boss.json   isBoss + bossKind
    cache/_fates.json   -> data/curated/fates.wiki.json      zone, place, coords, time, type
    cache/_zones.json   -> data/curated/places-of-interest.wiki.json   descriptions

WHAT THE BOSS CATEGORY ACTUALLY ADDS
------------------------------------
Less than it looks. 677 of its 681 pages are REDIRECTS, and 638 of those point straight back
into the `Final Fantasy XIV enemies/<class>` subpages parse.py already reads - so the boss
STATS were never new. What is new is the CLASSIFICATION: the 14 subcategories say which mobs
are trial bosses, raid bosses, FATE bosses, quest bosses and so on, which no sheet records.
So this overlay carries flags, not stats.

WHY FATE COORDINATES COME FROM HERE AND NOT THE GAME
----------------------------------------------------
`Fate.Location` is NOT a Level row. All 1,697 values sit inside Level's RowId range and match
NOTHING in it - not RowId, not Object, not EventId. It is an LGB layer-object id, and LGB
files are not Excel sheets. That is why fates.json shipped with zone=??? for all 1,712 rows
without anyone noticing: the lookup silently resolved to nothing. The wiki states the zone and
the coordinates outright, so it is the source.
"""
import collections
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from wikitable import split_tables_pos, parse_table, clean_text

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
CACHE = os.path.join(HERE, 'cache')
CURATED = os.path.join(ROOT, 'data', 'curated')

SOURCE = 'finalfantasy.fandom.com (boss categories, List of FATEs, zone pages), swept 2026-08-27'

DISAMBIG = re.compile(
    r'\s*\((?:final fantasy xiv(?: boss| enemy)?|boss|enemy|dungeon|trial|raid)\)\s*$', re.I)


def norm(s):
    s = (s or '').lower().strip().replace('–', '-').replace('—', '-').replace('’', "'")
    s = re.sub(r'[^a-z0-9]+', ' ', s)
    return re.sub(r'\s+', ' ', s).strip()


def write_overlay(fname, dataset, key, entries, description, warning):
    os.makedirs(CURATED, exist_ok=True)
    doc = {'schemaVersion': 1, 'dataset': dataset, 'keyField': key, 'source': SOURCE,
           'description': description, 'warning': warning,
           'entryCount': len(entries), 'entries': entries}
    p = os.path.join(CURATED, fname)
    json.dump(doc, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    print('wrote %-42s %6d entries %6.0f KB'
          % (os.path.relpath(p, ROOT), len(entries), os.path.getsize(p) / 1024))


# =====================================================================================
# 1. BOSS FLAGS  ->  curated/monsters.boss.json
# =====================================================================================
def build_bosses():
    b = json.load(open(os.path.join(CACHE, '_bosses.json'), encoding='utf-8'))
    membership = b['membership']

    mon = json.load(open(os.path.join(ROOT, 'data', 'monsters.json'), encoding='utf-8'))
    inv = {v: k for k, v in mon['fieldAliases'].items()}
    MID, MNAME = inv['id'], inv['name']
    WNAME = inv.get('wikiName')

    # name -> id, from BOTH the internal name and the wiki display name
    by_name = {}
    for e in mon['entries']:
        for k in (MNAME, WNAME):
            if not k:
                continue
            v = e.get(k)
            if isinstance(v, str) and v and v != '???':
                by_name.setdefault(norm(v), e[MID])

    entries, unmatched = {}, []
    for title, cats in membership.items():
        base = DISAMBIG.sub('', title).strip()
        mid = by_name.get(norm(base)) or by_name.get(norm(title))
        if mid is None:
            unmatched.append(title)
            continue
        kinds = sorted({c.replace(' bosses', '').replace('Bosses', 'Boss').strip()
                        for c in cats})
        patch = entries.setdefault(str(mid), {'isBoss': True})
        prev = patch.get('bossKind', '')
        merged = sorted(set([x for x in prev.split(', ') if x] + kinds))
        patch['bossKind'] = ', '.join(merged)

    print('bosses: %d category pages, matched %d monsters, %d unmatched'
          % (len(membership), len(entries), len(unmatched)))
    if unmatched:
        print('  first 10 unmatched: %s' % ', '.join(unmatched[:10]))

    write_overlay('monsters.boss.json', 'monsters', 'id', entries,
                  'CURATED overlay: which monsters are BOSSES, and of what kind, from the 14 '
                  'boss subcategories. Flags only - 638 of 677 boss pages redirect into the '
                  'enemy subpages parse.py already reads, so the stats were never new.',
                  'Matched by NAME, not by id: boss pages carry no BNpcName. Unmatched names '
                  'are printed by parse_more.py rather than dropped silently.')
    return len(entries)


# =====================================================================================
# 2. FATES  ->  curated/fates.wiki.json
# =====================================================================================
FATE_ICON = re.compile(r'\[\[File:(\w[\w\' ]*?) FATE icon', re.I)
COORD = re.compile(r'\(\s*x\s*([\d.]+)\s*[, ]\s*y\s*([\d.]+)\s*\)', re.I)


def build_fates():
    doc = json.load(open(os.path.join(CACHE, '_fates.json'), encoding='utf-8'))
    wt = doc['wikitext']

    heads = []
    for m in re.finditer(r'^(={2,4})\s*(.+?)\s*\1\s*$', wt, re.M):
        t = re.sub(r'\[\[[^\]|]*\|([^\]]*)\]\]', r'\1', m.group(2))
        t = re.sub(r'\[\[([^\]]*)\]\]', r'\1', t).strip()
        heads.append((m.start(), len(m.group(1)), t))

    def zone_at(pos):
        z = ''
        for off, depth, t in heads:
            if off > pos:
                break
            if depth >= 3:
                z = t
        return z

    rows = []
    for off, tbl in split_tables_pos(wt):
        labels, data = parse_table(tbl)
        low = [l.lower() for l in labels]
        if not any('name' in l for l in low):
            continue
        ci_n = next((i for i, l in enumerate(low) if 'name' in l), None)
        ci_l = next((i for i, l in enumerate(low) if 'level' in l), None)
        ci_p = next((i for i, l in enumerate(low) if 'location' in l), None)
        ci_t = next((i for i, l in enumerate(low) if 'time' in l), None)
        ci_o = next((i for i, l in enumerate(low) if 'objective' in l), None)
        if None in (ci_n, ci_l, ci_p):
            continue
        zone = zone_at(off)

        pending = None
        for r in data:
            raw_name = r.get(ci_n, '')
            name = clean_text(raw_name)
            if not name:
                continue
            # The description sits in a colspan="4" row beneath, so columns 1..4 all carry
            # the same blob. That is how a description row is told from a data row.
            vals = [r.get(i, '') for i in (ci_l, ci_p, ci_t, ci_o) if i is not None]
            is_desc = len(set(vals)) == 1 and len(vals) > 1
            if is_desc:
                if pending is not None:
                    txt = clean_text(vals[0])
                    spawn = ''
                    m = re.search(r"'''Spawn conditions:'''(.*)$", vals[0], re.S)
                    if m:
                        spawn = clean_text(m.group(1))
                        txt = clean_text(vals[0][:m.start()])
                    pending['description'] = txt
                    pending['spawnConditions'] = spawn
                    rows.append(pending)
                    pending = None
                continue

            if pending is not None:
                rows.append(pending)
            place = clean_text(r.get(ci_p, ''))
            x = y = None
            mm = COORD.search(place)
            if mm:
                x, y = float(mm.group(1)), float(mm.group(2))
                place = COORD.sub('', place).strip()
            ftype = ''
            fm = FATE_ICON.search(raw_name)
            if fm:
                ftype = fm.group(1).strip()
            pending = {'name': name, 'zone': zone, 'place': place, 'x': x, 'y': y,
                       'levels': clean_text(r.get(ci_l, '')),
                       'timeLimit': clean_text(r.get(ci_t, '')) if ci_t is not None else '',
                       'objective': clean_text(r.get(ci_o, '')) if ci_o is not None else '',
                       'fateType': ftype}
        if pending is not None:
            rows.append(pending)

    print('fates: parsed %d rows from the wiki' % len(rows))

    ours = json.load(open(os.path.join(ROOT, 'data', 'fates.json'), encoding='utf-8'))
    inv = {v: k for k, v in ours['fieldAliases'].items()}
    FID, FNAME = inv['id'], inv['name']

    counts = collections.Counter(norm(e[FNAME]) for e in ours['entries'])
    by_name = {}
    for e in ours['entries']:
        by_name.setdefault(norm(e[FNAME]), []).append(e[FID])

    entries, ambiguous, missing = {}, 0, 0
    wiki_by_name = collections.defaultdict(list)
    for r in rows:
        wiki_by_name[norm(r['name'])].append(r)

    for key, group in wiki_by_name.items():
        ids = by_name.get(key)
        if not ids:
            missing += 1
            continue
        # A name shared by several FATEs cannot be told apart - our zone column is ??? for
        # every row, so there is nothing to disambiguate against. Patch only when BOTH sides
        # are unique; guessing would attach one zone's coordinates to another zone's FATE.
        if len(ids) != 1 or len(group) != 1:
            ambiguous += 1
            continue
        r = group[0]
        patch = {}
        if r['zone']:
            patch['zone'] = r['zone']
        if r['place']:
            patch['place'] = r['place']
        if r['x'] is not None:
            patch['mapX'] = r['x']
            patch['mapY'] = r['y']
        if r['timeLimit']:
            patch['timeLimitMinutes'] = r['timeLimit']
        if r['fateType']:
            patch['fateType'] = r['fateType']
        if r.get('spawnConditions'):
            patch['spawnConditions'] = r['spawnConditions']
        if patch:
            entries[str(ids[0])] = patch

    print('  matched uniquely : %d' % len(entries))
    print('  ambiguous (name shared, nothing to disambiguate on) : %d' % ambiguous)
    print('  in wiki but not in our dataset : %d' % missing)

    write_overlay('fates.wiki.json', 'fates', 'id', entries,
                  'CURATED overlay for fates.json: zone, place, map coordinates, time limit, '
                  'FATE type and spawn conditions.',
                  'Matched by NAME. 518 of our 1,712 FATEs share a name with another FATE and '
                  'our own zone column is ??? for every row, so there is nothing to '
                  'disambiguate against - those are deliberately left unpatched rather than '
                  'guessed. Fate.Location is an LGB object id, not a Level row, so the game '
                  'files cannot supply this.')
    return len(entries)


# =====================================================================================
# 3. PLACE DESCRIPTIONS  ->  curated/places-of-interest.wiki.json
# =====================================================================================
def build_places():
    z = json.load(open(os.path.join(CACHE, '_zones.json'), encoding='utf-8'))
    pages = z['pages']

    # zone page -> {place name: description}
    described = {}
    for title, wt in pages.items():
        if wt.strip().upper().startswith('#REDIRECT'):
            continue
        cur = None
        buf = []
        out = {}
        for line in wt.splitlines():
            m = re.match(r'^(={2,5})\s*(.+?)\s*\1\s*$', line.strip())
            if m:
                if cur and buf:
                    txt = clean_text(' '.join(buf)).strip()
                    if len(txt) > 25:
                        out[norm(cur)] = txt[:600]
                depth = len(m.group(1))
                t = re.sub(r'\[\[[^\]|]*\|([^\]]*)\]\]', r'\1', m.group(2))
                t = re.sub(r'\[\[([^\]]*)\]\]', r'\1', t).strip()
                cur = t if depth >= 3 else None
                buf = []
                continue
            if cur and line.strip() and not line.strip().startswith(('{{', '|', '{|', '!', '*')):
                buf.append(line.strip())
        if cur and buf:
            txt = clean_text(' '.join(buf)).strip()
            if len(txt) > 25:
                out[norm(cur)] = txt[:600]
        if out:
            described[norm(title)] = out

    poi = json.load(open(os.path.join(ROOT, 'data', 'places-of-interest.json'), encoding='utf-8'))
    inv = {v: k for k, v in poi['fieldAliases'].items()}
    PID, PNAME, PLOC = inv['id'], inv['name'], inv['location']

    entries = {}
    for e in poi['entries']:
        zone_key = norm(e.get(PLOC, ''))
        book = described.get(zone_key)
        if not book:
            continue
        d = book.get(norm(e.get(PNAME, '')))
        if d:
            entries[e[PID]] = {'description': d}

    print('places: %d zone pages with descriptions, %d POIs described'
          % (len(described), len(entries)))

    write_overlay('places-of-interest.wiki.json', 'places-of-interest', 'id', entries,
                  'CURATED overlay: prose description for a place of interest, taken from the '
                  "matching section heading on its zone's wiki page.",
                  'Only places the wiki gives a real section AND a paragraph for. Zone pages '
                  'are inconsistent - some use "Places of Interest", some "Geography", some '
                  '"Locations/Areas" - so coverage is partial by nature.')
    return len(entries)


if __name__ == '__main__':
    build_bosses()
    print()
    build_fates()
    print()
    build_places()
