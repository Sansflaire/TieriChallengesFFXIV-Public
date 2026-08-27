"""Cache the Final Fantasy Wiki enemy subpages.

WHY THIS IS 20 REQUESTS AND NOT 14,000
--------------------------------------
The wiki has ~9,000 individual enemy pages, but almost all of them are REDIRECTS into a
handful of family subpages:

    12th Legion Armored Weapon  ->  #REDIRECT [[Final Fantasy XIV enemies/Forgekin#...]]

The real tables live on the 19 subpages under "Final Fantasy XIV enemies/". Fetching those
gets every enemy in one sweep, so the polite thing and the cheap thing are the same thing.

The MediaWiki API is used rather than HTML scraping: it is the sanctioned interface and
returns the wikitext, which is far more parseable than Fandom's rendered DOM.

Guards (devPlugins/CLAUDE.md - bounded sweeps):
  * hard request cap                     - MAX_REQUESTS
  * consecutive-failure circuit breaker  - MAX_CONSECUTIVE_FAILURES
  * resumable disk cache                 - re-running skips what already succeeded

Output: scripts/wiki/cache/<page>.json   (one file per subpage, wikitext + revision id)
"""
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, 'cache')

API = 'https://finalfantasy.fandom.com/api.php'
UA = ('TieriChallengesFFXIV-dataset/1.0 (personal Dalamud plugin dataset; '
      'github.com/Sansflaire)')

DELAY = 2.0                      # seconds between requests - deliberately unhurried
MAX_REQUESTS = 200               # hard cap; this sweep needs ~25
MAX_CONSECUTIVE_FAILURES = 5     # circuit breaker
TIMEOUT = 30

_requests = 0
_consecutive = 0


def api(**params):
    """One API call, counted against the cap and the breaker."""
    global _requests, _consecutive
    if _requests >= MAX_REQUESTS:
        raise SystemExit('HARD CAP: %d requests already made, refusing more' % _requests)

    params.setdefault('format', 'json')
    params.setdefault('formatversion', '2')
    url = API + '?' + urllib.parse.urlencode(params)
    req = urllib.request.Request(url, headers={'User-Agent': UA})

    _requests += 1
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
            data = json.loads(r.read().decode('utf-8'))
        _consecutive = 0
        return data
    except Exception as ex:
        _consecutive += 1
        if _consecutive >= MAX_CONSECUTIVE_FAILURES:
            raise SystemExit('CIRCUIT BREAKER: %d consecutive failures, last: %s'
                             % (_consecutive, ex))
        raise


def slug(title):
    return title.replace('Final Fantasy XIV enemies/', '').replace(' ', '_').replace('/', '_')


def main():
    os.makedirs(CACHE, exist_ok=True)

    # 1. Enumerate the subpages rather than hard-coding them, so a wiki reorganisation
    #    shows up as a diff in the cache instead of silently dropping a whole class.
    d = api(action='query', list='allpages',
            apprefix='Final Fantasy XIV enemies/', aplimit='500')
    subs = sorted(p['title'] for p in d['query']['allpages'])
    print('subpages found: %d' % len(subs))

    # 2. The category tells us which enemies the wiki considers DUNGEON enemies. Kept as a
    #    cross-check on the Spawn column, not as the source of truth for location.
    cats = {}
    for cat in ('Category:Dungeon enemies in Final Fantasy XIV',
                'Category:Dungeon bosses in Final Fantasy XIV'):
        members, cont = [], {}
        while True:
            time.sleep(DELAY)
            r = api(action='query', list='categorymembers', cmtitle=cat, cmlimit='500', **cont)
            members += [m['title'] for m in r['query']['categorymembers']]
            if 'continue' not in r:
                break
            cont = r['continue']
        cats[cat] = members
        print('  %-52s %d members' % (cat.replace('Category:', ''), len(members)))

    with open(os.path.join(CACHE, '_categories.json'), 'w', encoding='utf-8') as f:
        json.dump(cats, f, ensure_ascii=False, indent=1)

    # 3. The subpages themselves.
    fetched = failed = skipped = 0
    for title in subs:
        path = os.path.join(CACHE, slug(title) + '.json')
        if os.path.exists(path):
            skipped += 1
            continue
        time.sleep(DELAY)
        try:
            r = api(action='parse', page=title, prop='wikitext|revid')
            doc = {
                'title': title,
                'revid': r['parse'].get('revid'),
                'wikitext': r['parse']['wikitext'],
            }
            with open(path, 'w', encoding='utf-8') as f:
                json.dump(doc, f, ensure_ascii=False)
            fetched += 1
            print('  ok   %-46s %7d chars' % (title, len(doc['wikitext'])))
        except Exception as ex:
            failed += 1
            print('  FAIL %-46s %s' % (title, ex))

    print()
    print('requests made : %d' % _requests)
    print('fetched       : %d' % fetched)
    print('cached already: %d' % skipped)
    print('failed        : %d' % failed)


if __name__ == '__main__':
    main()
