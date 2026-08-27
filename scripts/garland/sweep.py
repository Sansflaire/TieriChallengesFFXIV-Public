"""Fetch every duty's Garland Tools instance doc, to fill unlock / items / fight structure.

Guards required by devPlugins/CLAUDE.md ("Bounded Sweeps"):
  * hard cap on total requests
  * consecutive-failure circuit breaker
  * durable progress - the cache is written as it goes and re-running resumes,
    so an interruption never means re-fetching everything from a third party.

One process, sequential, deliberately slow. This is someone else's free service.
"""
import json
import os
import sys
import time
import urllib.request

ROOT = r'C:\Users\trist\AppData\Roaming\XIVLauncher\devPlugins\TieriChallengesFFXIV'
CACHE = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'garland-instances.json')

UA = 'TieriChallengesFFXIV-dev/1.0 (dataset cross-check; contact: Sansflaire on GitHub)'
DELAY = 2.0          # seconds between requests
HARD_CAP = 400       # refuse #401 outright
FAIL_STOP = 5        # consecutive failures -> stop, do not hammer a broken sink

# ---- our duties, de-aliased ----
d = json.load(open(os.path.join(ROOT, 'data', 'duties.json'), encoding='utf-8'))
inv = {v: k for k, v in d['fieldAliases'].items()}
duties = []
for e in d['entries']:
    gid = e.get(inv.get('garlandId', ''))
    if gid:
        duties.append((int(gid), e.get(inv['name'], ''), e.get(inv['kind'], '')))

cache = {}
if os.path.exists(CACHE):
    cache = json.load(open(CACHE, encoding='utf-8'))
    print('resuming: %d already cached' % len(cache))

todo = [x for x in duties if str(x[0]) not in cache]
print('%d duties, %d still to fetch, ~%.0f min at %.1fs apart'
      % (len(duties), len(todo), len(todo) * DELAY / 60.0, DELAY))
sys.stdout.flush()


def fetch(url):
    req = urllib.request.Request(url, headers={'User-Agent': UA})
    with urllib.request.urlopen(req, timeout=25) as r:
        return json.loads(r.read().decode('utf-8'))


calls = 0
fails = 0
added = 0

for gid, name, kind in todo:
    if calls >= HARD_CAP:
        print('HARD CAP %d reached - refusing further requests' % HARD_CAP)
        break
    if fails >= FAIL_STOP:
        print('CIRCUIT BREAKER: %d consecutive failures - stopping' % fails)
        break

    try:
        calls += 1
        doc = fetch('https://www.garlandtools.org/db/doc/instance/en/2/%d.json' % gid)
        fails = 0

        inst = doc.get('instance', {})
        partials = {p.get('id'): p for p in doc.get('partials', [])}

        def pname(pid):
            p = partials.get(str(pid))
            return (p or {}).get('obj', {}).get('n', '')

        rewards = [{'id': i, 'name': pname(i)} for i in inst.get('rewards', []) or []]
        coffers = []
        for c in inst.get('coffers', []) or []:
            coffers.append({
                'coords': c.get('coords'),
                'items': [{'id': i, 'name': pname(i)} for i in c.get('items', []) or []],
            })
        fights = []
        for f in inst.get('fights', []) or []:
            fights.append({
                'type': f.get('type', ''),
                'name': f.get('name', ''),
                'items': [{'id': i, 'name': pname(i)}
                          for i in (f.get('coffer', {}) or {}).get('items', []) or []],
            })

        uq = inst.get('unlockedByQuest')
        cache[str(gid)] = {
            'garlandId': gid,
            'ourName': name,
            'garlandName': inst.get('name', ''),
            'patch': inst.get('patch'),
            'timeLimitMinutes': inst.get('time'),
            'minIlvl': inst.get('min_ilvl'),
            'maxIlvl': inst.get('max_ilvl'),
            'minLvl': inst.get('min_lvl'),
            'maxLvl': inst.get('max_lvl'),
            'unlockQuestId': uq,
            'unlockQuestName': pname(uq) if uq else '',
            'requiredForQuest': inst.get('requiredForQuest'),
            'rewards': rewards,
            'coffers': coffers,
            'fights': fights,
        }
        added += 1

        if added % 25 == 0:
            json.dump(cache, open(CACHE, 'w', encoding='utf-8'))
            print('  ... %d/%d fetched' % (added, len(todo)))
            sys.stdout.flush()

    except Exception as ex:
        fails += 1
        print('FAIL %s (%s): %s' % (gid, name, ex))
        sys.stdout.flush()

    time.sleep(DELAY)

json.dump(cache, open(CACHE, 'w', encoding='utf-8'))
print()
print('requests made : %d' % calls)
print('cached total  : %d of %d duties' % (len(cache), len(duties)))
with_unlock = sum(1 for v in cache.values() if v.get('unlockQuestId'))
with_items = sum(1 for v in cache.values() if v.get('rewards') or v.get('coffers'))
with_fights = sum(1 for v in cache.values() if v.get('fights'))
named_mobs = sum(1 for v in cache.values() if any(f.get('name') for f in v.get('fights', [])))
print('with unlock quest : %d' % with_unlock)
print('with any items    : %d' % with_items)
print('with fights       : %d' % with_fights)
print('with NAMED mobs   : %d' % named_mobs)
