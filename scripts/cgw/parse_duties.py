"""Cached duty pages -> data/curated/duties.cgw.json.

Fills duties.bosses properly, plus enemies, objectives, unlock quest and entrance.

THE JOIN IS EXACT, NOT BY NAME.
{{Duty infobox | id-gt = 13}} is the Garland Tools id, and duties.json already carries it as
`garlandId` from the Garland sweep. Name matching would have to cope with "(Savage)",
"(Extreme)", "(Unreal)" and the "(Duty)" disambiguator; the id sidesteps all of it. Name is
kept only as a fallback for pages with no id-gt, and every failure is reported.

Bosses come from the '===[[Boss Name]]===' subheadings under ==Bosses==, which is where this
wiki actually records them. The Fandom enemy tables barely cover Trial and Raid bosses at all,
which is why 217 of 373 duties had bosses=??? before this.
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

SOURCE = 'ffxiv.consolegameswiki.com duty pages (dungeons/trials/raids/...), swept 2026-08-27'


def norm(s):
    s = (s or '').lower().strip().replace('–', '-').replace('—', '-').replace('’', "'")
    s = re.sub(r'[^a-z0-9]+', ' ', s)
    return re.sub(r'\s+', ' ', s).strip()


def clean(s):
    if not s:
        return ''
    s = re.sub(r'\[\[File:[^\]]*\]\]', '', s, flags=re.I)
    s = re.sub(r'\{\{[^{}]*\}\}', '', s)
    s = re.sub(r'\[\[[^\]|]*\|([^\]]*)\]\]', r'\1', s)
    s = re.sub(r'\[\[([^\]]*)\]\]', r'\1', s)
    s = re.sub(r'<[^>]+>', '', s)
    s = s.replace("'''", '').replace("''", '')
    return re.sub(r'\s+', ' ', s).strip()


def section(wt, name):
    """Body of ==name== up to the next heading of the same or higher level."""
    m = re.search(r'^==\s*%s\s*==\s*$' % re.escape(name), wt, re.M | re.I)
    if not m:
        return ''
    start = m.end()
    nxt = re.search(r'^==[^=]', wt[start:], re.M)
    return wt[start:start + nxt.start()] if nxt else wt[start:]


def main():
    doc = json.load(open(os.path.join(CACHE, '_duties.json'), encoding='utf-8'))
    pages = doc['pages']
    print('cached duty pages: %d' % len(pages))

    ours = json.load(open(os.path.join(ROOT, 'data', 'duties.json'), encoding='utf-8'))
    inv = {v: k for k, v in ours['fieldAliases'].items()}
    DID, DNAME, DGID = inv['id'], inv['name'], inv.get('garlandId')

    by_gid, by_name = {}, collections.defaultdict(list)
    for e in ours['entries']:
        if DGID and isinstance(e.get(DGID), int) and e[DGID]:
            by_gid.setdefault(e[DGID], e[DID])
        by_name[norm(e[DNAME])].append(e[DID])

    entries = {}
    stats = collections.Counter()
    matched_gid = matched_name = unmatched = 0

    for title, wt in sorted(pages.items()):
        if wt.strip().upper().startswith('#REDIRECT'):
            continue
        fb = tpl_fields(wt, 'duty infobox')
        if not fb:
            continue

        key = None
        gid = clean(fb.get('id-gt', ''))
        if gid.isdigit() and int(gid) in by_gid:
            key = by_gid[int(gid)]
            matched_gid += 1
        else:
            cands = by_name.get(norm(clean(fb.get('name', '')) or title)) or by_name.get(norm(title))
            if cands and len(cands) == 1:
                key = cands[0]
                matched_name += 1
        if key is None:
            unmatched += 1
            continue

        e = {}

        # ---- bosses ----
        # A boss is marked by its DIFFICULTY ICON in the heading, not by living under a
        # ==Bosses== section. Only 137 of 529 pages have that section, but 230 have boss
        # headings and the icon appears on all of them:
        #
        #   ===[[File:Aggressive difficulty r5.png|32px|link=]] [[All-seeing Eye]]===
        #
        # Keying on the section alone found bosses for 121 duties; keying on the icon finds
        # them wherever the page puts them.
        bosses = []
        for m in re.finditer(
                r'^==+[^\n=]*\[\[File:\s*(?:Aggressive|Passive)\s+difficulty[^\]]*\]\]\s*'
                r'(.+?)\s*==+\s*$', wt, re.M | re.I):
            nm = clean(m.group(1))
            if nm:
                bosses.append(nm)

        bs = section(wt, 'Bosses')
        if bs:
            for m in re.finditer(r'^===+\s*(.+?)\s*===+\s*$', bs, re.M):
                nm = clean(m.group(1))
                if nm and nm.lower() not in ('adds', 'abilities', 'strategy', 'notes', 'phase 1',
                                             'phase 2', 'phase 3', 'loot', 'trivia'):
                    bosses.append(nm)
            if not bosses:
                bosses += [clean(m.group(1))
                           for m in re.finditer(r'^\*\s*\[\[([^\]|]+)', bs, re.M)]
        bosses = [b for b in dict.fromkeys(bosses) if b]
        if bosses:
            e['bosses'] = ', '.join(bosses)
            stats['bosses'] += 1

        # ---- enemies ----
        es = section(wt, 'Enemies')
        foes = [clean(m.group(1)) for m in re.finditer(r'^\*+\s*\[\[([^\]|]+)', es, re.M)]
        foes = [f for f in dict.fromkeys(foes) if f and f not in bosses]
        if foes:
            e['monsters'] = ', '.join(foes)
            stats['monsters'] += 1

        # ---- objectives ----
        obs = section(wt, 'Objectives')
        objs = [clean(re.sub(r':\s*0/\d+\s*$', '', m.group(1)))
                for m in re.finditer(r'^#\s*(.+)$', obs, re.M)]
        objs = [o for o in objs if o]
        if objs:
            e['objectives'] = ' | '.join(objs)
            stats['objectives'] += 1

        # ---- infobox extras ----
        rq = clean(fb.get('req-quest', ''))
        if rq:
            e['unlockQuest'] = rq
            stats['unlockQuest'] += 1
        tl = clean(fb.get('time-limit', ''))
        if tl:
            e['timeLimitMinutes'] = tl
        ent = clean(fb.get('entrance', ''))
        ec = clean(fb.get('entrance-coordinates', ''))
        if ent:
            e['entrance'] = '%s (%s)' % (ent, ec) if ec else ent
            stats['entrance'] += 1
        exp = clean(fb.get('base-exp', ''))
        if exp:
            e['baseExp'] = exp
        rl = clean(fb.get('roulette', ''))
        if rl:
            e['roulette'] = rl

        if e:
            entries[str(key)] = e

    print('matched by garlandId : %d' % matched_gid)
    print('matched by name      : %d' % matched_name)
    print('unmatched pages      : %d  (content types duties.json does not carry)' % unmatched)
    print('overlay entries      : %d' % len(entries))
    for k in ('bosses', 'monsters', 'objectives', 'unlockQuest', 'entrance'):
        print('  with %-12s: %d' % (k, stats[k]))

    os.makedirs(CURATED, exist_ok=True)
    out = {
        'schemaVersion': 1, 'dataset': 'duties', 'keyField': 'id', 'source': SOURCE,
        'description': ('CURATED overlay for duties.json from the FFXIV Console Games Wiki: '
                        'bosses, enemies, objectives, unlock quest, entrance and time limit. '
                        'Joined on id-gt == garlandId, an exact id match.'),
        'warning': ('Bosses come from the ==Bosses== subheadings, which is where this wiki '
                    'records them; the Fandom enemy tables barely cover Trial/Raid bosses. '
                    'Applied AFTER duties.wiki.json alphabetically, so where both know a duty '
                    'this source wins - it is the better one for bosses.'),
        'entryCount': len(entries), 'entries': entries,
    }
    p = os.path.join(CURATED, 'duties.zcgw.json')
    json.dump(out, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    print('wrote %s  %.0f KB' % (os.path.relpath(p, ROOT), os.path.getsize(p) / 1024))


if __name__ == '__main__':
    main()
