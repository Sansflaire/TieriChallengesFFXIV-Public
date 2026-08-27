"""Cache the consolegameswiki FATE pages (Category:FATEs).

The {{FATE infobox}} answers every remaining fates.json gap in one block:

    | boss = Cuachac              <- the named enemy
    | enemies =                   <- the rest
    | prev-fate = / next-fate =   <- CHAIN ORDERING, which FATEChain groups but never sequences
    | exp / gil / seals / bicolor gemstone / mettle / item-reward(1-4)   <- rewards
    | location / location-x / location-y / type / level / duration

Same guards and the same /mediawiki/api.php path as fetch.py. No redirects=1 - it collapses
batches server-side with no error.
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

DELAY = 1.2
MAX_REQUESTS = 250
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
    path = os.path.join(CACHE, '_fates.json')
    if os.path.exists(path):
        print('fates: cached, delete scripts/cgw/cache/_fates.json to refresh')
        return

    members, cont = [], {}
    while True:
        time.sleep(DELAY)
        d = api(action='query', list='categorymembers', cmtitle='Category:FATEs',
                cmlimit='500', **cont)
        if not d:
            break
        members += [m['title'] for m in d['query']['categorymembers']
                    if not m['title'].startswith('Category:')]
        if 'continue' not in d:
            break
        cont = d['continue']
    members = sorted(set(members))
    print('FATE pages: %d' % len(members))

    pages = {}
    for i in range(0, len(members), BATCH):
        chunk = members[i:i + BATCH]
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
        if (i // BATCH) % 10 == 0 or i + BATCH >= len(members):
            print('    %d/%d requested, %d held' % (min(i + BATCH, len(members)), len(members), len(pages)))

    json.dump(pages, open(path, 'w', encoding='utf-8'), ensure_ascii=False)
    missing = [t for t in members if t not in pages]
    print()
    print('requests made: %d' % _requests)
    print('pages cached : %d of %d' % (len(pages), len(members)))
    if missing:
        print('!! %d returned no content (first 5: %s)' % (len(missing), missing[:5]))


if __name__ == '__main__':
    main()
