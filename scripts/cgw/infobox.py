"""Depth-aware {{template}} field extraction.

WHY NOT A LINE REGEX
--------------------
`^\\s*\\|\\s*(key)\\s*=\\s*(.*)$` looks right and is wrong: not every page puts one field per
line. Some write several on a single line:

    {{FATE infobox| title = Now Fall| location =| location-x =| ...}}

and the line regex then captures "| location-x =" as the value of `location`. That shipped
into fates.json as zone="| location-x =" before it was caught.

Fields must be split on the pipes that belong to THIS template - not pipes inside a nested
{{template}} or an [[link|label]].
"""


def split_fields(s):
    """Split a template body on depth-0 '|'."""
    out, buf, i, depth = [], [], 0, 0
    n = len(s)
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
        if depth == 0 and s[i] == '|':
            out.append(''.join(buf))
            buf = []
            i += 1
            continue
        buf.append(s[i])
        i += 1
    out.append(''.join(buf))
    return out


def template_body(wt, name):
    """The inside of {{name ...}}, brace-balanced. '' if absent."""
    low = wt.lower()
    i = low.find('{{' + name.lower())
    if i < 0:
        return ''
    depth, j = 0, i
    while j < len(wt):
        if wt.startswith('{{', j):
            depth += 1
            j += 2
        elif wt.startswith('}}', j):
            depth -= 1
            j += 2
            if depth == 0:
                break
        else:
            j += 1
    return wt[i + 2:j - 2]


def fields(wt, name):
    """{lowercased key: raw value} for one template invocation."""
    body = template_body(wt, name)
    if not body:
        return {}
    out = {}
    for part in split_fields(body)[1:]:          # [0] is the template name
        if '=' not in part:
            continue
        k, _, v = part.partition('=')
        k = k.strip().lower()
        if k:
            out[k] = v.strip()
    return out
