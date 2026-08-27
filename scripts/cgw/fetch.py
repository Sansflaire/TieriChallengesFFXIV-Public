"""Cache the Final Fantasy XIV Online Wiki (ffxiv.consolegameswiki.com) enemy pages.

This is the ONLY source found for monster LOOT. Drop tables are server-side and absent from
every client sheet (settled exhaustively at TODO Q11/R6), and the Fandom enemy tables do not
carry them either. This wiki gives one page per enemy with a machine-readable body:

    {{NPC infobox | location = South Shroud | coordinates = 17,22 | race = Beastkin
                  | clan = Antelope | level = 20-23 | aggression = p1 | patch = 2.0 }}
    ==Loot==
    {{Drops table row|Beast Sinew}}
    {{Drops table row|Antelope Shank}}
    ==Locations==
    {{NPC location info|South Shroud| 17,22 |20-23}}

NOTE THE API PATH: /mediawiki/api.php, not /api.php (which 404s).

Titles are taken from Category:Enemies rather than guessed from our own names. Our names are
lowercase internal ones ("antelope doe") and MediaWiki titles are case-sensitive after the
first letter, so title-casing would silently miss every "Dorgono the Bedeviled".

Guards (devPlugins/CLAUDE.md - bounded sweeps): hard request cap, consecutive-failure
circuit breaker, resumable cache. ~9,200 pages cost ~200 batched requests, not 9,200.
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
MAX_REQUESTS = 400
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


def main():
    os.makedirs(CACHE, exist_ok=True)
    mpath = os.path.join(CACHE, '_members.json')
    ppath = os.path.join(CACHE, '_pages.json')

    if os.path.exists(mpath):
        members = json.load(open(mpath, encoding='utf-8'))
        print('members: cached (%d)' % len(members))
    else:
        members, cont = [], {}
        while True:
            time.sleep(DELAY)
            d = api(action='query', list='categorymembers', cmtitle='Category:Enemies',
                    cmlimit='500', **cont)
            if not d:
                break
            members += [m['title'] for m in d['query']['categorymembers']]
            if 'continue' not in d:
                break
            cont = d['continue']
        members = sorted(set(members))
        json.dump(members, open(mpath, 'w', encoding='utf-8'), ensure_ascii=False)
        print('members: %d enemy pages' % len(members))

    pages = {}
    if os.path.exists(ppath):
        pages = json.load(open(ppath, encoding='utf-8'))
        print('pages: %d already cached' % len(pages))

    todo = [t for t in members if t not in pages]
    print('to fetch: %d' % len(todo))

    for i in range(0, len(todo), BATCH):
        chunk = todo[i:i + BATCH]
        time.sleep(DELAY)
        # No redirects=1: it collapses a batch server-side and returns fewer pages with no
        # error - 681 titles once came back as 51. See scripts/wiki/fetch_more.py.
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
        if (i // BATCH) % 20 == 0 or i + BATCH >= len(todo):
            print('    %d/%d requested, %d held' % (min(i + BATCH, len(todo)), len(todo), len(pages)))
            json.dump(pages, open(ppath, 'w', encoding='utf-8'), ensure_ascii=False)

    json.dump(pages, open(ppath, 'w', encoding='utf-8'), ensure_ascii=False)
    missing = [t for t in members if t not in pages]
    print()
    print('requests made : %d' % _requests)
    print('pages cached  : %d of %d' % (len(pages), len(members)))
    if missing:
        print('!! %d titles returned no content (first 5: %s)' % (len(missing), missing[:5]))
    print('cache size    : %.1f MB' % (os.path.getsize(ppath) / 1048576))


if __name__ == '__main__':
    main()
