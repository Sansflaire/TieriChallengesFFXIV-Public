"""Turn the cached wiki enemy tables into curated overlays.

    scripts/wiki/cache/*.json
        |
        +--> data/curated/monsters.json      level, hp, abilities, family, zones, duties, ...
        +--> data/curated/duties.wiki.json   the 'monsters' column for each instance

Both are OVERLAYS: inputs to scripts/gen-datasets, never its output. Re-running this and then
regenerating is idempotent. duties gets a SEPARATE ".wiki." file so it cannot clobber the
Garland overlay (unlockQuest / itemsFound) sitting in data/curated/duties.json.

THE JOIN IS BY ROW ID, NOT BY NAME
----------------------------------
The wiki's "BNpc / Name" column is the BNpcName sheet RowId - the same key data/monsters.json
is built on. Verified: all 5,035 ids present in the tables exist in our dataset, and 98.3% of
them carry a matching name. So monsters join exactly and no fuzzy matching is involved.

Duties are the opposite case: the wiki writes them as free text ("the twinning"), so those DO
need normalised name matching, and every failure is reported rather than silently dropped.
"""
import collections
import glob
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from wikitable import (split_tables_pos, parse_table, clean_text, spawn_entries, level_hp)

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
CACHE = os.path.join(HERE, 'cache')
CURATED = os.path.join(ROOT, 'data', 'curated')

SOURCE = 'finalfantasy.fandom.com "Final Fantasy XIV enemies" subpages, swept 2026-08-27'


# --------------------------------------------------------------------------------------
# read the cache
# --------------------------------------------------------------------------------------

def load_pages():
    out = []
    for p in sorted(glob.glob(os.path.join(CACHE, '*.json'))):
        if os.path.basename(p).startswith('_'):
            continue
        out.append(json.load(open(p, encoding='utf-8')))
    return out


def section_map(wt):
    """Character offset -> nearest preceding == heading == (the creature family)."""
    marks = []
    for m in re.finditer(r'^(={2,4})\s*(.+?)\s*\1\s*$', wt, re.M):
        marks.append((m.start(), clean_text(m.group(2))))
    return marks


def section_at(marks, pos):
    name = ''
    for off, nm in marks:
        if off <= pos:
            name = nm
        else:
            break
    return name


def parse_rows():
    rows = []
    for d in load_pages():
        wt = d['wikitext']
        cls = d['title'].replace('Final Fantasy XIV enemies/', '')
        marks = section_map(wt)
        for tbl_off, tbl in split_tables_pos(wt):
            labels, data = parse_table(tbl)
            low = [l.lower() for l in labels]
            if not any('bnpc' in l and 'name' in l for l in low):
                continue
            if not any('spawn' in l for l in low):
                continue

            ci = {}
            ci['name'] = next((i for i, l in enumerate(low) if l.startswith('name')), None)
            ci['id'] = next(i for i, l in enumerate(low) if 'bnpc' in l and 'name' in l)
            ci['base'] = next((i for i, l in enumerate(low) if 'bnpc' in l and 'base' in l), None)
            ci['level'] = next((i for i, l in enumerate(low) if 'level' in l), None)
            ci['hp'] = next((i for i, l in enumerate(low) if l.strip() == 'hp'), None)
            ci['hitbox'] = next((i for i, l in enumerate(low) if 'hitbox' in l), None)
            ci['abil'] = next((i for i, l in enumerate(low) if 'abilit' in l), None)
            ci['spawn'] = next(i for i, l in enumerate(low) if 'spawn' in l)
            if ci['name'] is None:
                continue

            # The table's REAL offset, not a text search - see split_tables_pos.
            fam = section_at(marks, tbl_off)

            for r in data:
                nm = clean_text(r.get(ci['name'], ''))
                if not nm:
                    continue
                raw_id = clean_text(r.get(ci['id'], ''))
                levels, hps = level_hp(r.get(ci['level'], '') if ci['level'] is not None else '',
                                       r.get(ci['hp'], '') if ci['hp'] is not None else '')
                rows.append({
                    'class': cls,
                    'family': fam,
                    'name': nm,
                    'id': int(raw_id) if raw_id.isdigit() else None,
                    'levels': levels,
                    'hps': hps,
                    'hitbox': clean_text(r.get(ci['hitbox'], ''))[:16] if ci['hitbox'] is not None else '',
                    'abilities': clean_text(r.get(ci['abil'], '')) if ci['abil'] is not None else '',
                    'spawn': spawn_entries(r.get(ci['spawn'], '')),
                })
    return rows


# --------------------------------------------------------------------------------------
# name normalisation for the duty join
# --------------------------------------------------------------------------------------

def norm(s):
    s = (s or '').lower().strip()
    s = s.replace('–', '-').replace('—', '-').replace('’', "'")
    s = re.sub(r'[^a-z0-9]+', ' ', s)
    return re.sub(r'\s+', ' ', s).strip()


def main():
    rows = parse_rows()
    print('parsed rows: %d' % len(rows))

    cats = {}
    cpath = os.path.join(CACHE, '_categories.json')
    if os.path.exists(cpath):
        cats = json.load(open(cpath, encoding='utf-8'))
    dungeon_enemy = {norm(x) for x in cats.get('Category:Dungeon enemies in Final Fantasy XIV', [])}
    dungeon_boss = {norm(x) for x in cats.get('Category:Dungeon bosses in Final Fantasy XIV', [])}

    # ---------------- aggregate per BNpcName id ----------------
    mons = {}
    for r in rows:
        if not r['id']:
            continue
        m = mons.setdefault(r['id'], {
            'names': [], 'levels': [], 'hp': [], 'hitbox': [], 'abil': [],
            'family': set(), 'cls': set(), 'spawn': collections.defaultdict(list),
        })
        if r['name'] not in m['names']:
            m['names'].append(r['name'])
        m['levels'].extend(r['levels'])
        m['hp'].extend(r['hps'])
        if r['hitbox']:
            m['hitbox'].append(r['hitbox'])
        if r['abilities']:
            m['abil'].append(r['abilities'])
        if r['family']:
            m['family'].add(r['family'])
        if r['class']:
            m['cls'].add(r['class'])
        for kind, val in r['spawn']:
            if val not in m['spawn'][kind]:
                m['spawn'][kind].append(val)

    print('distinct BNpcName ids: %d' % len(mons))

    # ---------------- monsters overlay ----------------
    def joined(seq):
        return ', '.join(seq)

    mon_entries = {}
    for mid, m in sorted(mons.items()):
        e = {}
        e['wikiName'] = m['names'][0]
        # Levels above 100 are not levels - they are stray numbers from a malformed cell.
        lv = [x for x in m['levels'] if 1 <= x <= 100]
        if lv:
            lo, hi = min(lv), max(lv)
            e['level'] = lo if lo == hi else '%d-%d' % (lo, hi)
        if m['hp']:
            hp = sorted({x for x in m['hp']}, key=lambda s: int(s.replace(',', '')))
            e['hp'] = hp[0] if len(hp) == 1 else '%s-%s' % (hp[0], hp[-1])
        if m['hitbox']:
            e['hitbox'] = m['hitbox'][0]
        if m['abil']:
            # longest variant carries the fullest kit
            e['abilities'] = max(m['abil'], key=len)
        if m['family']:
            e['family'] = joined(sorted(m['family']))
        if m['cls']:
            e['creatureClass'] = joined(sorted(m['cls']))

        sp = m['spawn']
        if sp.get('zone'):
            e['zones'] = joined(sorted(sp['zone']))
            e['zonesSource'] = 'Final Fantasy Wiki'
        if sp.get('duty'):
            e['duties'] = joined(sorted(sp['duty']))
        if sp.get('fate'):
            e['fates'] = joined(sorted(sp['fate']))
        q = sorted(set(sp.get('quest', []) + sp.get('quest battle', [])))
        if q:
            e['quests'] = joined(q)
        n = norm(m['names'][0])
        if n in dungeon_boss:
            e['dungeonBoss'] = True
        if n in dungeon_enemy:
            e['dungeonEnemy'] = True
        mon_entries[str(mid)] = e

    write_overlay('monsters.json', 'monsters', 'id', mon_entries,
                  ('CURATED overlay for monsters.json. NOT from game files. Joined by BNpcName '
                   'row id, which the wiki publishes directly - no name matching involved.'),
                  ("Community-maintained and openly described as part conjecture. Levels, HP and "
                   "abilities are as recorded by editors, not extracted from the client. Coverage "
                   "is partial: the wiki documents the notable mobs, not all 14,560."))

    # ---------------- duties overlay ----------------
    duties = json.load(open(os.path.join(ROOT, 'data', 'duties.json'), encoding='utf-8'))
    inv = {v: k for k, v in duties['fieldAliases'].items()}
    dk_id, dk_name = inv['id'], inv['name']
    by_name = {}
    for ent in duties['entries']:
        by_name.setdefault(norm(ent.get(dk_name, '')), ent.get(dk_id))

    per_duty = collections.defaultdict(set)
    seen_names = collections.Counter()
    for r in rows:
        for kind, val in r['spawn']:
            if kind != 'duty':
                continue
            seen_names[val] += 1
            key = by_name.get(norm(val))
            if key is None:
                key = by_name.get(norm(re.sub(r'\s*\(original\)\s*$', '', val)))
            if key is not None:
                per_duty[key].add(r['name'])

    matched = {n for n in seen_names if norm(n) in by_name
               or norm(re.sub(r'\s*\(original\)\s*$', '', n)) in by_name}
    unmatched = sorted(set(seen_names) - matched)

    duty_entries = {str(k): {'monsters': ', '.join(sorted(v))}
                    for k, v in sorted(per_duty.items()) if v}

    write_overlay('duties.wiki.json', 'duties', 'id', duty_entries,
                  ('CURATED overlay for duties.json - the "monsters" column only. Separate file '
                   'from curated/duties.json (Garland) so the two sources never overwrite each '
                   'other. Built from the Spawn column of the wiki enemy tables.'),
                  ('Only duties the wiki names in a Spawn cell get a monster list. A duty absent '
                   'here keeps ??? and is genuinely undocumented, not empty.'))

    # ---------------- report ----------------
    print()
    print('duty names referenced by the wiki : %d' % len(seen_names))
    print('  matched to a duties.json row    : %d' % len(matched))
    print('  UNMATCHED                       : %d' % len(unmatched))
    print('duties receiving a monster list   : %d of %d' % (len(duty_entries), len(duties['entries'])))
    if unmatched:
        print()
        print('--- unmatched duty names (top 40 by mention count) ---')
        for n in sorted(unmatched, key=lambda x: -seen_names[x])[:40]:
            print('   %-58s %3d' % (n[:58], seen_names[n]))


def write_overlay(fname, dataset, key, entries, description, warning):
    os.makedirs(CURATED, exist_ok=True)
    doc = {
        'schemaVersion': 1,
        'dataset': dataset,
        'keyField': key,
        'source': SOURCE,
        'description': description,
        'warning': warning,
        'entryCount': len(entries),
        'entries': entries,
    }
    path = os.path.join(CURATED, fname)
    json.dump(doc, open(path, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    print('wrote %-34s %6d entries  %6.0f KB'
          % (os.path.relpath(path, ROOT), len(entries), os.path.getsize(path) / 1024))


if __name__ == '__main__':
    main()
