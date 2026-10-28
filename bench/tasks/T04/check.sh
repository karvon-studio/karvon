#!/usr/bin/env bash
# Passes when PriceFormatter.Format takes a string `currency`, no caller calls it with one argument,
# and the solution builds and its tests pass.
set -uo pipefail
fail() { echo "$1"; exit 1; }

python3 - <<'PY' || exit 1
import os, re, sys

def fail(message):
    print(message)
    sys.exit(1)

def is_call(before):
    """Whether `Format(` after this text is a call rather than a declaration."""
    if not before or before.endswith(("=>", "return", "await", "else")):
        return True
    if re.search(r"\w\?$", before):
        return False  # string? Format(
    return before[-1] in "(,=+-*/%?:[{!&|^~;@$."

def lex(text, start=0, stop_at_close=False):
    """Walks C# from start. Returns (masked, end): masked has comments and the text of string and char
    literals blanked (interpolation holes kept), end is where it stopped (the ')' closing the argument
    list when stop_at_close, else the end of the text)."""
    out = list(text)
    i, n = start, len(text)
    depth = 0          # brackets opened since start
    holes = []         # for each open interpolated string: [brace depth, verbatim, raw quote count, dollars]

    def blank(a, b):
        for k in range(a, b):
            if out[k] != "\n":
                out[k] = " "

    def string_end(i, verbatim, raw, dollars, interpolated):
        """From just inside a literal, returns the index after it, or the index of a hole's '{'."""
        while i < n:
            c = text[i]
            if raw:
                if text.startswith('"' * raw, i):
                    return i + raw, False
                if interpolated and text.startswith("{" * dollars, i):
                    return i, True
                i += 1
                continue
            if interpolated and c == "{":
                if text.startswith("{{", i):
                    i += 2
                    continue
                return i, True
            if verbatim:
                if c == '"':
                    if text.startswith('""', i):
                        i += 2
                        continue
                    return i + 1, False
            else:
                if c == "\\":
                    i += 2
                    continue
                if c == '"' or c == "\n":
                    return i + 1, False
            i += 1
        return n, False

    def open_string(i):
        """If a string literal starts at i, returns (index after its opening, verbatim, raw, dollars, interpolated)."""
        m = re.compile(r'(\$*)(@?)(\$*)("+)').match(text, i)
        if not m:
            return None
        dollars = len(m.group(1)) + len(m.group(3))
        verbatim = bool(m.group(2))
        quotes = len(m.group(4))
        if quotes >= 3:
            return m.start(4) + quotes, False, quotes, max(dollars, 1), dollars > 0
        return m.start(4) + 1, verbatim, 0, 1, dollars > 0

    while i < n:
        c = text[i]
        if text.startswith("//", i):
            j = text.find("\n", i)
            j = n if j < 0 else j
            blank(i, j)
            i = j
            continue
        if text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            blank(i, j)
            i = j
            continue
        if c == "'":
            m = re.compile(r"'(\\.[^']*|[^'\\\n])'").match(text, i)
            if m:
                blank(i + 1, m.end() - 1)
                i = m.end()
                continue
        if c in '$@"':
            s = open_string(i)
            if s:
                j, verbatim, raw, dollars, interpolated = s
                k, in_hole = string_end(j, verbatim, raw, dollars, interpolated)
                blank(j, k if in_hole else max(j, k - (raw or 1)))
                if in_hole:
                    holes.append([depth, verbatim, raw, dollars])
                    depth += 1
                    i = k + (dollars if raw else 1)
                else:
                    i = k
                continue
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
            if c == "}" and holes and holes[-1][0] == depth:
                _, verbatim, raw, dollars = holes.pop()
                k, in_hole = string_end(i + 1, verbatim, raw, dollars, True)
                blank(i + 1, k if in_hole else max(i + 1, k - (raw or 1)))
                if in_hole:
                    holes.append([depth, verbatim, raw, dollars])
                    depth += 1
                    i = k + (dollars if raw else 1)
                else:
                    i = k
                continue
            if stop_at_close and depth < 0:
                return "".join(out), i
        i += 1
    return "".join(out), n

def arguments(text, open_paren):
    """The top-level arguments of the call whose '(' is at open_paren."""
    masked, close = lex(text, open_paren + 1, stop_at_close=True)
    inner = masked[open_paren + 1:close]
    args, depth, start = [], 0, 0
    for k, c in enumerate(inner):
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif c == "," and depth == 0:
            args.append(inner[start:k])
            start = k + 1
    args.append(inner[start:])
    return [a.strip() for a in args if a.strip() or len(args) > 1]

def razor_mask(text):
    """Razor: only its comments (@* *@, <!-- -->) are blanked; markup is left as it is."""
    for pattern in (r"@\*.*?\*@", r"<!--.*?-->"):
        text = re.sub(pattern, lambda m: re.sub(r"[^\n]", " ", m.group(0)), text, flags=re.S)
    return text

files = []
for root, dirs, names in os.walk("."):
    dirs[:] = [d for d in dirs if d not in ("bin", "obj") and not d.startswith(".")]
    files += [os.path.join(root, f) for f in names if f.endswith((".cs", ".razor", ".cshtml"))]

sources = {}
for path in sorted(files):
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        text = f.read()
    masked = lex(text)[0] if path.endswith(".cs") else razor_mask(text)
    sources[path] = (text, masked)

# using static Shop.Core.PriceFormatter; and aliases such as using PF = Shop.Core.PriceFormatter;
static_cs = static_razor = False
global_aliases, aliases = set(), {}
for path, (_, masked) in sources.items():
    razor = not path.endswith(".cs")
    for m in re.finditer(r"(?:\b(global)\s+)?(?:@|\b)using\s+static\s+(?:global::)?(?:Shop\s*\.\s*Core\s*\.\s*)?PriceFormatter\b", masked):
        if razor:
            static_razor = True
        elif m.group(1):
            static_cs = True
        else:
            aliases.setdefault(path, set()).add(None)
    for m in re.finditer(r"(?:\b(global)\s+)?(?:@|\b)using\s+(\w+)\s*=\s*(?:global::)?(?:Shop\s*\.\s*Core\s*\.\s*)?PriceFormatter\b", masked):
        (global_aliases if m.group(1) or razor else aliases.setdefault(path, set())).add(m.group(2))

# The declarations.
declared, one_argument_overload = [], False
for path, (text, masked) in sources.items():
    if not path.endswith(".cs") or not re.search(r"\bclass\s+PriceFormatter\b", masked):
        continue
    for m in re.finditer(r"(?<![\w.])Format\s*\(", masked):
        if is_call(masked[:m.start()].rstrip()):
            continue
        params = arguments(text, m.end() - 1)
        declared.append(params)
        if len(params) == 1:
            one_argument_overload = True
if not declared:
    fail("no PriceFormatter.Format declaration found")
string_currency = re.compile(
    r"^(\[[^\]]*\]\s*)*(?:(?:this|in|ref|scoped|params)\s+)*(?:(?:global::)?System\s*\.\s*)?(string|String)\s*\??\s+currency\b")
if not any(string_currency.match(p) for params in declared for p in params):
    fail("PriceFormatter.Format has no string `currency` parameter: " +
             "; ".join("(" + ", ".join(params) + ")" for params in declared))

# The calls.
old = []
for path, (text, masked) in sources.items():
    razor = not path.endswith(".cs")
    names = {"PriceFormatter"} | global_aliases | {a for a in aliases.get(path, ()) if a}
    pattern = r"\b(?:" + "|".join(map(re.escape, sorted(names))) + r")\s*\.\s*Format\b"
    hits = [(m, True) for m in re.finditer(pattern, masked)]
    if (static_razor if razor else static_cs or None in aliases.get(path, ())):
        for m in re.finditer(r"(?<![\w.])Format\s*\(", masked):
            if is_call(masked[:m.start()].rstrip()):
                hits.append((m, False))
    for m, qualified in hits:
        rest = masked[m.end():] if qualified else masked[m.end() - 1:]
        line = text.count("\n", 0, m.start()) + 1
        where = f"{path[2:]}:{line}"
        if not rest.lstrip().startswith("("):
            if one_argument_overload:
                old.append(where + " (method group)")
            continue
        paren = m.end() + (len(rest) - len(rest.lstrip())) if qualified else m.end() - 1
        if len(arguments(text, paren)) == 1:
            old.append(where)
if old:
    fail("PriceFormatter.Format is still called with one argument at " + ", ".join(old))
PY

log=$(mktemp) || fail "no temp file"
trap 'rm -f "$log"' EXIT
dotnet build Shop.sln >"$log" 2>&1 \
    || fail "the build fails: $(grep -m1 -E ': error [A-Z]+[0-9]+:' "$log" | sed -E 's/ \[[^]]*\]$//')"
timeout 120 dotnet test Shop.sln --no-build >"$log" 2>&1 \
    || fail "the tests fail: $(grep -m1 -E '^\s*Failed [A-Za-z_]' "$log" | sed -E 's/^\s+//')"
echo "Format takes a currency, every caller passes one, and the solution builds and its tests pass"
