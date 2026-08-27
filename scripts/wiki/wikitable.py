"""A rowspan-aware MediaWiki table parser, plus the cleaners for this wiki's templates.

WHY A REAL PARSER AND NOT REGEX PER ROW
---------------------------------------
The enemy tables lean on rowspan heavily - 3,869 data cells carry one, up to rowspan="14".
A mob that appears in five duties is written ONCE with rowspan="5" on its name/id/level and
five rows of spawn cells beneath. A naive line-based parser reads rows 2-5 as nameless
monsters and drops four of the five locations, which is precisely the data we came for.

So the table is expanded into a real grid first, exactly as a browser would, and only then
read by column.

The header is expanded through the same grid, because the header itself uses colspan:

    !rowspan="2"|Name  !rowspan="2"|Pic  !colspan="2"|BNpc  !rowspan="2"|Level ...
    |-
    !Name  !Base            <- these two land under the BNpc colspan

which is what makes column 2 "BNpc Name" and column 3 "BNpc Base".
"""
import re

# --------------------------------------------------------------------------------------
# depth-aware splitting - '|' means something different inside {{templates}} and [[links]]
# --------------------------------------------------------------------------------------


def _depth_split(s, sep):
    """Split on `sep` only where template/link nesting depth is zero."""
    out, buf, i, depth = [], [], 0, 0
    n, ln = len(s), len(sep)
    while i < n:
        if s.startswith('{{', i) or s.startswith('[[', i):
            depth += 1
            buf.append(s[i:i + 2])
            i += 2
            continue
        if s.startswith('}}', i) or s.startswith(']]', i):
            depth = max(0, depth - 1)
            buf.append(s[i:i + 2])
            i += 2
            continue
        if depth == 0 and s.startswith(sep, i):
            out.append(''.join(buf))
            buf = []
            i += ln
            continue
        buf.append(s[i])
        i += 1
    out.append(''.join(buf))
    return out


_ATTR_RE = re.compile(r'^\s*(?:[a-zA-Z-]+\s*=\s*(?:"[^"]*"|\'[^\']*\'|[^\s|]+)\s*)+$')


def _split_attrs(raw):
    """Separate 'rowspan="5"|content' into ('rowspan="5"', 'content').

    Only the FIRST depth-0 pipe is a candidate, and only when what precedes it is
    unambiguously an attribute list. '{{A|Foo}}' must never be read as attrs.
    """
    parts = _depth_split(raw, '|')
    if len(parts) >= 2 and _ATTR_RE.match(parts[0]):
        return parts[0], '|'.join(parts[1:])
    return '', raw


def split_tables_pos(wt):
    """Split into top-level tables as (offset, text). Wiki tables nest, so this counts depth.

    The OFFSET matters: these tables are near-identical for their first few hundred
    characters (41 tables on the Beastkin page share just 2 distinct 200-char prefixes), so
    locating a table afterwards with wt.find(table[:200]) returns the first one every time.
    That silently filed every Beastkin creature under the page's first family heading.
    """
    out, depth, start = [], 0, None
    for m in re.finditer(r'^(\{\||\|\})', wt, re.M):
        if m.group(1) == '{|':
            if depth == 0:
                start = m.start()
            depth += 1
        else:
            if depth > 0:
                depth -= 1
                if depth == 0 and start is not None:
                    out.append((start, wt[start:m.end()]))
                    start = None
    return out


def split_tables(wt):
    return [t for _, t in split_tables_pos(wt)]


def parse_table(tbl):
    """-> (labels, data_rows) where data_rows is a list of {col_index: content}."""
    lines = tbl.splitlines()
    rows = []            # list of (is_header, [(attrs, content), ...])
    cur = None
    nest = 0             # depth of NESTED tables inside a cell

    def _append(txt):
        """Glue a line onto the cell currently being built."""
        if cur is not None and cur[1]:
            a, c = cur[1][-1]
            cur[1][-1] = (a, c + '\n' + txt)

    for line in lines[1:]:                    # skip the '{|' opener
        ls = line.rstrip()
        st = ls.strip()

        # ---- nested tables are CELL CONTENT, never structure -------------------------
        # 525 of them across the corpus: collapsible level/HP variant tables sitting in a
        # colspan="2" cell. Treating their rows as parent rows corrupts the grid, and
        # breaking on their '|}' truncates the parent - one such table cut 303 rows to 13.
        if st.startswith('{|'):
            nest += 1
            _append(ls)
            continue
        if nest > 0:
            _append(ls)
            if st.startswith('|}'):
                nest -= 1
            continue

        if st.startswith('|}'):
            break
        if st.startswith('|-'):
            cur = None
            continue

        # ONLY '|-' starts a row. A '!' cell does NOT.
        #
        # A row may legitimately MIX header and data cells, and the FATE tables do exactly
        # that - the name is a '!' cell followed by four '|' cells:
        #
        #     |-
        #     !rowspan="2"|[[File:...]] {{A|On the Lamb}}
        #     |3-8
        #     |Zephyr Drift (x22 y24)
        #
        # Splitting on the !/| switch tore every FATE into two rows, put all 2,000 names into
        # the header, and matched zero of them. A row is a HEADER row only when every one of
        # its cells is a '!' cell (decided below), not because it happens to contain one.
        if st.startswith('!'):
            if cur is None:
                cur = [[], []]          # [cell_is_header flags, cells]
                rows.append(cur)
            for c in _depth_split(st[1:], '!!'):
                cur[0].append(True)
                cur[1].append(_split_attrs(c))
            continue
        if st.startswith('|'):
            if cur is None:
                cur = [[], []]
                rows.append(cur)
            for c in _depth_split(st[1:], '||'):
                cur[0].append(False)
                cur[1].append(_split_attrs(c))
            continue
        # continuation of the previous cell (multi-line cell content)
        if cur is not None and cur[1] and st:
            a, c = cur[1][-1]
            cur[1][-1] = (a, c + '\n' + st)

    # ---- expand into a grid, honouring rowspan / colspan ----
    grid = []
    active = {}          # col -> [content, rows_remaining]
    kinds = []
    seen_data = False
    for flags, cells in rows:
        # A header row is one whose cells are ALL header cells, and only while we are still in
        # the table's leading header block. After real data starts, a stray all-'!' row is a
        # sub-heading inside the body, not a column definition.
        is_header = bool(flags) and all(flags) and not seen_data
        if not is_header:
            seen_data = True
        rowmap = {}
        for c in sorted(active):
            rowmap[c] = active[c][0]
            active[c][1] -= 1
            if active[c][1] <= 0:
                del active[c]
        col = 0
        for attrs, content in cells:
            rs = 1
            cs = 1
            m = re.search(r'rowspan\s*=\s*"?(\d+)', attrs)
            if m:
                rs = max(1, int(m.group(1)))
            m = re.search(r'colspan\s*=\s*"?(\d+)', attrs)
            if m:
                cs = max(1, int(m.group(1)))
            for _ in range(cs):
                while col in rowmap:
                    col += 1
                rowmap[col] = content
                if rs > 1:
                    active[col] = [content, rs - 1]
                col += 1
        grid.append(rowmap)
        kinds.append(is_header)

    # ---- column labels from the header rows ----
    ncols = max((max(r) + 1 for r in grid if r), default=0)
    labels = []
    for c in range(ncols):
        seen = []
        for i, r in enumerate(grid):
            if not kinds[i]:
                continue
            v = clean_text(r.get(c, ''))
            if v and v not in seen:
                seen.append(v)
        labels.append(' '.join(seen))

    data = [r for i, r in enumerate(grid) if not kinds[i]]
    return labels, data


# --------------------------------------------------------------------------------------
# template / markup cleaners
# --------------------------------------------------------------------------------------

_J_RE = re.compile(r'\{\{J\|[^{}]*\}\}', re.I)
_NOTE_RE = re.compile(r'\{\{(?:note|foot|annotations|etym)\|.*?\}\}', re.I | re.S)


def _strip_nested(s, name):
    """Remove {{name|...}} including nested braces."""
    out, i, n = [], 0, len(s)
    low = s.lower()
    tag = '{{' + name.lower() + '|'
    while i < n:
        if low.startswith(tag, i):
            depth, j = 0, i
            while j < n:
                if s.startswith('{{', j):
                    depth += 1
                    j += 2
                elif s.startswith('}}', j):
                    depth -= 1
                    j += 2
                    if depth == 0:
                        break
                else:
                    j += 1
            i = j
            continue
        out.append(s[i])
        i += 1
    return ''.join(out)


def clean_text(s):
    """Wikitext cell -> plain text."""
    if not s:
        return ''
    s = _J_RE.sub('', s)
    for t in ('note', 'foot', 'annotations', 'etym', 'ref'):
        s = _strip_nested(s, t)
    # {{A|page}} / {{A|page|display}} -> display
    s = re.sub(r'\{\{A\|([^{}|]*?)\|([^{}|]*?)\}\}', r'\2', s, flags=re.I)
    s = re.sub(r'\{\{A\|([^{}|]*?)\}\}', r'\1', s, flags=re.I)
    s = re.sub(r'\{\{LA\|([^{}|]*?)\|([^{}|]*?)\}\}', r'\2', s, flags=re.I)
    s = re.sub(r'\{\{LA\|([^{}|]*?)\}\}', r'\1', s, flags=re.I)
    # icons: keep the label, drop the machinery
    s = re.sub(r'\{\{icon\|ffxiv\|rank\|\d+\}\}', '', s, flags=re.I)
    s = re.sub(r'\{\{icon\|ffxiv\|[^|{}]+\|([^{}|]*?)\}\}', r'\1', s, flags=re.I)
    # Image/file links are markup, not text. Left in, '[[File:x.png|20px|Battle FATE.]]'
    # cleans to "20px|Battle FATE." and glues itself onto every FATE name.
    s = re.sub(r'\[\[(?:File|Image):[^\[\]]*(?:\[\[[^\[\]]*\]\][^\[\]]*)*\]\]', '', s, flags=re.I)
    # links
    s = re.sub(r'\[\[[^\]|]*\|([^\]]*)\]\]', r'\1', s)
    s = re.sub(r'\[\[([^\]]*)\]\]', r'\1', s)
    # any surviving template
    s = re.sub(r'\{\{[^{}]*\}\}', '', s)
    # html
    s = re.sub(r'<br\s*/?>', ' ', s, flags=re.I)
    s = re.sub(r'<[^>]+>', '', s)
    s = s.replace("'''", '').replace("''", '')
    s = s.replace('&nbsp;', ' ').replace('&amp;', '&')
    return re.sub(r'\s+', ' ', s).strip()


def spawn_entries(raw):
    """Spawn cell -> [(kind, name)] e.g. [('duty', 'the twinning')].

    Kinds seen across the corpus: duty, zone, fate, levequest, quest, quest battle,
    activity, gold saucer. 'rank' is a difficulty badge, not a location, and is excluded.
    """
    out = []
    for m in re.finditer(r'\{\{icon\|ffxiv\|([^|{}]+)\|([^{}]*?)\}\}', raw or '', re.I):
        kind = m.group(1).strip().lower()
        if kind in ('rank', 'action', 'status'):
            continue
        name = clean_text(m.group(2))
        if name:
            out.append((kind, name))
    return out


def level_of(raw):
    """'{{icon|ffxiv|rank|3}} 60' -> (60, 3). Ranges like '60-63' keep the first number."""
    if not raw:
        return None, None
    rank = None
    m = re.search(r'\{\{icon\|ffxiv\|rank\|(\d+)', raw, re.I)
    if m:
        rank = int(m.group(1))
    txt = clean_text(re.sub(r'\{\{icon\|ffxiv\|rank\|[^{}]*\}\}', '', raw, flags=re.I))
    m = re.search(r'(\d+)', txt)
    return (int(m.group(1)) if m else None), rank


def level_hp(raw_level, raw_hp):
    """-> (levels[], hps[]) handling the collapsible VARIANT tables.

    A creature with several level/HP variants does not get a scalar in those columns. It gets
    one cell with colspan="2" holding a nested collapsible table:

        |rowspan="8" colspan="2" style="padding: 0px"|
        {|class="mw-collapsible mw-collapsed" ...
        |{{icon|ffxiv|rank|1}} 20-24
        |
        |-
        |{{icon|ffxiv|rank|1}} 42
        |1,789
        |}

    Because the colspan covers BOTH Level and HP, the grid hands the identical blob to each -
    so reading either as a scalar produced junk (level 1 / HP "1" for a level 20-24 mob).
    The nested table is parsed with the same machinery: its column 0 is level, column 1 is HP.
    """
    src = raw_level or ''
    if '{|' in src:
        levels, hps = [], []
        for inner in split_tables(src):
            _labels, rows = parse_table(inner)
            for r in rows:
                txt = clean_text(re.sub(r'\{\{icon\|ffxiv\|rank\|[^{}]*\}\}', '',
                                        r.get(0, ''), flags=re.I))
                for n in re.findall(r'\d+', txt):
                    levels.append(int(n))
                h = re.search(r'(\d[\d,]*)', clean_text(r.get(1, '')))
                if h:
                    hps.append(h.group(1))
        return levels, hps

    lv, _ = level_of(src)
    txt = clean_text(re.sub(r'\{\{icon\|ffxiv\|rank\|[^{}]*\}\}', '', src, flags=re.I))
    levels = [int(n) for n in re.findall(r'\d+', txt)] if txt else []
    if not levels and lv:
        levels = [lv]
    h = re.search(r'(\d[\d,]*)', clean_text(raw_hp or ''))
    return levels, ([h.group(1)] if h else [])
