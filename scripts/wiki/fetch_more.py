"""Cache the three additional wiki sources: bosses, FATEs, zone pages.

    cache/_bosses.json      665 boss pages (15 subcategories), wikitext each
    cache/_fates.json       the "List of FATEs" page
    cache/_zones.json       136 zone/area pages from "Final Fantasy XIV locations"

BATCHED, NOT ONE-AT-A-TIME. The MediaWiki API takes up to 50 titles per query via
`prop=revisions`, so 665 boss pages cost ~14 requests instead of 665. Same guards as
fetch.py: hard cap, consecutive-failure breaker, resumable cache.

Boss pages matter because `scripts/wiki/parse.py` cannot fill Trials and Raids - the enemy
family subpages document trash mobs, while primal/raid bosses live on their own pages with an
{{FFXIV Enemy}} infobox carrying `location`, `family`, `genus` and HP.
"""
import json
import os
import re
import time
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, 'cache')

API = 'https://finalfantasy.fandom.com/api.php'
UA = ('TieriChallengesFFXIV-dataset/1.0 (personal Dalamud plugin dataset; '
      'github.com/Sansflaire)')

DELAY = 1.5
MAX_REQUESTS = 300
MAX_CONSECUTIVE_FAILURES = 5
TIMEOUT = 45
BATCH = 50

BOSS_CATEGORIES = [
    'Category:Bosses in Final Fantasy XIV',
    'Category:Alliance raid bosses in Final Fantasy XIV',
    'Category:Deep dungeon bosses in Final Fantasy XIV',
    'Category:Dungeon bosses in Final Fantasy XIV',
    'Category:Elite marks in Final Fantasy XIV',
    'Category:FATE bosses in Final Fantasy XIV',
    'Category:Field operation bosses in Final Fantasy XIV',
    'Category:Guildhest bosses in Final Fantasy XIV',
    'Category:Levequest bosses in Final Fantasy XIV',
    'Category:Quest bosses in Final Fantasy XIV',
    'Category:Raid bosses in Final Fantasy XIV',
    'Category:Treasure dungeon bosses in Final Fantasy XIV',
    'Category:Trial bosses in Final Fantasy XIV',
    'Category:Ultimate Raid bosses in Final Fantasy XIV',
]

_requests = 0
_consecutive = 0


def api(**params):
    global _requests, _consecutive
    if _requests >= MAX_REQUESTS:
        raise SystemExit('HARD CAP: %d requests already made' % _requests)
    params.setdefault('format', 'json')
    params.setdefault('formatversion', '2')
    url = API + '?' + urllib.parse.urlencode(params)
    req = urllib.request.Request(url, headers={'User-Agent': UA})
    _requests += 1
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
            d = json.loads(r.read().decode('utf-8'))
        _consecutive = 0
        return d
    except Exception as ex:
        _consecutive += 1
        if _consecutive >= MAX_CONSECUTIVE_FAILURES:
            raise SystemExit('CIRCUIT BREAKER: %d consecutive failures, last: %s'
                             % (_consecutive, ex))
        print('    request failed: %s' % ex)
        return None


def fetch_titles(titles):
    """{title: wikitext} for any number of titles, 50 per request.

    DO NOT pass redirects=1. Most boss-category members are redirects into the enemy family
    subpages, so resolving them server-side COLLAPSES the batch: 681 boss titles came back as
    51 pages, and 121 zone titles as 97, with no error and every batch reporting success. The
    raw page is what we want anyway - a redirect's body names its target section, which is how
    a boss is tied to its family, and a real boss page carries the {{FFXIV Enemy}} infobox.
    """
    out = {}
    got = 0
    for i in range(0, len(titles), BATCH):
        chunk = titles[i:i + BATCH]
        time.sleep(DELAY)
        d = api(action='query', prop='revisions', rvprop='content', rvslots='main',
                titles='|'.join(chunk))
        if not d:
            continue
        for p in d.get('query', {}).get('pages', []):
            if 'revisions' not in p:
                continue
            try:
                out[p['title']] = p['revisions'][0]['slots']['main']['content']
                got += 1
            except (KeyError, IndexError):
                pass
        print('    %d/%d titles requested, %d pages held' % (min(i + BATCH, len(titles)), len(titles), got))

    # Loud check: a batch API that silently returns fewer pages than asked is the exact
    # failure this comment block exists for. Never let it pass as success.
    missing = [t for t in titles if t not in out]
    if missing:
        print('    !! %d of %d titles returned NO content (first 5: %s)'
              % (len(missing), len(titles), missing[:5]))
    return out


def members(cat):
    out, cont = [], {}
    while True:
        time.sleep(DELAY)
        d = api(action='query', list='categorymembers', cmtitle=cat, cmlimit='500', **cont)
        if not d:
            break
        out += [m['title'] for m in d['query']['categorymembers']]
        if 'continue' not in d:
            break
        cont = d['continue']
    return out


def save(name, obj):
    p = os.path.join(CACHE, name)
    with open(p, 'w', encoding='utf-8') as f:
        json.dump(obj, f, ensure_ascii=False)
    print('  wrote %-22s %6.0f KB' % (name, os.path.getsize(p) / 1024))


def main():
    os.makedirs(CACHE, exist_ok=True)

    # ---------------- 1. bosses ----------------
    path = os.path.join(CACHE, '_bosses.json')
    if os.path.exists(path):
        print('bosses: cached, skipping')
    else:
        print('bosses: enumerating %d categories' % len(BOSS_CATEGORIES))
        titles, membership = set(), {}
        for cat in BOSS_CATEGORIES:
            ms = [m for m in members(cat) if not m.startswith('Category:')]
            short = cat.replace('Category:', '').replace(' in Final Fantasy XIV', '')
            for m in ms:
                membership.setdefault(m, []).append(short)
            titles.update(ms)
            print('  %-46s %4d' % (short, len(ms)))
        titles = sorted(titles)
        print('  distinct boss pages: %d' % len(titles))
        pages = fetch_titles(titles)
        save('_bosses.json', {'membership': membership, 'pages': pages})

    # ---------------- 2. FATEs ----------------
    path = os.path.join(CACHE, '_fates.json')
    if os.path.exists(path):
        print('fates: cached, skipping')
    else:
        time.sleep(DELAY)
        d = api(action='parse', page='List of FATEs', prop='wikitext|revid')
        save('_fates.json', {'title': 'List of FATEs',
                             'revid': d['parse'].get('revid'),
                             'wikitext': d['parse']['wikitext']})

    # ---------------- 3. zone pages ----------------
    path = os.path.join(CACHE, '_zones.json')
    if os.path.exists(path):
        print('zones: cached, skipping')
    else:
        time.sleep(DELAY)
        d = api(action='parse', page='Final Fantasy XIV locations', prop='wikitext')
        idx = d['parse']['wikitext']
        # The index is a heading hierarchy of landmass / region with '*[[Zone]]' bullets.
        titles, hierarchy = [], {}
        h2 = h3 = h4 = ''
        for line in idx.splitlines():
            ls = line.strip()
            m = re.match(r'^(={2,5})\s*(.+?)\s*\1$', ls)
            if m:
                depth, txt = len(m.group(1)), m.group(2)
                txt = re.sub(r'\[\[[^\]|]*\|([^\]]*)\]\]', r'\1', txt)
                txt = re.sub(r'\[\[([^\]]*)\]\]', r'\1', txt).strip()
                if depth == 3:
                    h2, h3, h4 = txt, '', ''
                elif depth == 4:
                    h3, h4 = txt, ''
                elif depth == 5:
                    h4 = txt
                continue
            m = re.match(r'^\*\s*\[\[([^\]|]+)(?:\|([^\]]*))?\]\]', ls)
            if m:
                raw = m.group(1).strip()
                # 'Azys Lla#Castrum Solus' is a SECTION of a page, not a page. The API cannot
                # fetch an anchor as a title and returns nothing for it - silently, which is
                # how 23 of 121 zones went missing on the first pass.
                t = raw.split('#')[0].strip()
                if not t:
                    continue
                titles.append(t)
                hierarchy[raw] = {'world': h2, 'group': h3, 'region': h4,
                                  'page': t, 'section': (raw.split('#')[1] if '#' in raw else ''),
                                  'display': (m.group(2) or raw).strip()}
        titles = sorted(set(titles))
        print('zones: %d linked from the index' % len(titles))
        pages = fetch_titles(titles)
        save('_zones.json', {'hierarchy': hierarchy, 'index': idx, 'pages': pages})

    print()
    print('requests made: %d' % _requests)


if __name__ == '__main__':
    main()
