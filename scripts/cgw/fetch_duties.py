"""Cache the Console Games Wiki DUTY pages (dungeons, trials, raids, ...).

This is what fills duties.bosses properly. The earlier attempt derived duty bosses from the
Fandom enemy tables' Spawn column, which documents trash mobs well and Trial/Raid bosses barely
at all - so 217 of 373 duties had bosses=???. These pages carry the answer outright:

    {{Duty infobox | id-gt = 13 | req-quest = Fort of Fear | time-limit = 90
                   | entrance = Coerthas Central Highlands | entrance-coordinates = 20,28 }}
    ==Objectives==   #Defeat [[Batraal]]: 0/1
    ==Enemies==      *[[Diamond-tooth Hedgemole]] ...
    ==Bosses==       ===[[File:...]] [[All-seeing Eye]]===

AND THE JOIN IS EXACT. `id-gt` is the Garland Tools id, which duties.json already carries as
`garlandId` from its own Garland sweep. No name matching, no "(Savage)" suffix guessing.
"""
import json
import os
import time
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, 'cache')

API = 'https://ffxiv.consolegameswiki.com/mediawiki/api.php'
UA = ('TieriChallengesFFXIV-dataset/1.0 (personal Dalamud plugin dataset; '
      'github.com/Sansflaire)')

CATEGORIES = [
    'Category:Dungeons', 'Category:Trials', 'Category:Raids',
    'Category:Alliance Raids', 'Category:Ultimate Raids', 'Category:Chaotic Alliance Raids',
    'Category:Deep Dungeons', 'Category:Variant Dungeons', 'Category:Criterion Dungeons',
    'Category:Guildhests', 'Category:Field Operations', 'Category:Treasure Dungeons',
]

DELAY = 1.2
MAX_REQUESTS = 200
MAX_CONSECUTIVE_FAILURES = 5
TIMEOUT = 60
BATCH = 50

_requests = 0
_consecutive = 0


def api(**params):
    global _requests, _consecutive
    if _requests >= MAX_REQUESTS:
        raise SystemExit('HARD CAP: %d requests already made' % _requests)
    params.setdefault('format', 'json')
    params.setdefault('formatversion', '2')
    req = urllib.request.Request(API + '?' + urllib.parse.urlencode(params),
                                 headers={'User-Agent': UA})
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


def main():
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, '_duties.json')
    if os.path.exists(path):
        print('duties: cached, delete scripts/cgw/cache/_duties.json to refresh')
        return

    titles, membership = set(), {}
    for cat in CATEGORIES:
        cont, got = {}, 0
        while True:
            time.sleep(DELAY)
            d = api(action='query', list='categorymembers', cmtitle=cat, cmlimit='500', **cont)
            if not d:
                break
            ms = [m['title'] for m in d['query']['categorymembers']
                  if not m['title'].startswith('Category:')]
            for m in ms:
                membership.setdefault(m, []).append(cat.replace('Category:', ''))
            titles.update(ms)
            got += len(ms)
            if 'continue' not in d:
                break
            cont = d['continue']
        print('  %-34s %4d' % (cat.replace('Category:', ''), got))

    titles = sorted(titles)
    print('distinct duty pages: %d' % len(titles))

    pages = {}
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
                pages[p['title']] = p['revisions'][0]['slots']['main']['content']
            except (KeyError, IndexError):
                pass
        print('    %d/%d requested, %d held' % (min(i + BATCH, len(titles)), len(titles), len(pages)))

    json.dump({'membership': membership, 'pages': pages},
              open(path, 'w', encoding='utf-8'), ensure_ascii=False)
    missing = [t for t in titles if t not in pages]
    print()
    print('requests made: %d' % _requests)
    print('pages cached : %d of %d' % (len(pages), len(titles)))
    if missing:
        print('!! %d returned no content (first 5: %s)' % (len(missing), missing[:5]))


if __name__ == '__main__':
    main()
