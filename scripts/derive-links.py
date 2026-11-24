#!/usr/bin/env python3
"""Rewrites the relative links of ONE Markdown file for the repository it is being derived into.

Called by scripts/derive.sh, once per Markdown file, BEFORE the heuristic text rewrite (which would
otherwise mangle an absolute URL that contains `src/backend/`). Not shipped to derived repositories.

    derive-links.py <map.tsv> <source-root> <fs-url> <source-rel> <out-rel> <dest-file> <promoted>

map.tsv       lines of `<source path>\t<derived path>`: every file that exists in the derived repository.
source-rel    where the file was in aj-boilerplate-fs; out-rel where it lands in the derived one.

For each inline link or reference definition whose target is a relative path:
  1. resolve it against the file's location in aj-boilerplate-fs (that is where it was written);
  2. if that target exists in the derived repository, point at its derived path, relative to the
     file's new location. This is what fixes `../../docs/x.md` in a promoted src/backend file;
  3. else, if the target is a tracked file or directory in aj-boilerplate-fs only (the other stack, a
     dropped workflow, scripts/derive.sh), point at it on GitHub: <fs-url>/blob/main/<path>;
  4. else leave it alone — a link that was already broken in aj-boilerplate-fs is not ours to hide.
Links inside fenced code blocks and inline code spans, absolute URLs, mailto:, root-relative `/x`
and pure `#anchor` links are never touched.
"""
import os
import posixpath
import re
import sys

map_path, src_root, fs_url, src_rel, out_rel, dest, promoted = sys.argv[1:8]

mapping = {}
with open(map_path, encoding="utf8") as fh:
    for line in fh:
        line = line.rstrip("\n")
        if line:
            s, d = line.split("\t")
            mapping[s] = d
derived_dirs = {posixpath.dirname(d) for d in mapping.values()}
for d in list(derived_dirs):
    while d and d != ".":
        derived_dirs.add(d)
        d = posixpath.dirname(d)

LINK = re.compile(r"(\]\(|^\[[^\]]+\]:\s*)(<?)([^)\s>]+)")


def rewrite_target(target):
    if re.match(r"^([a-z][a-z0-9+.-]*:|/|#)", target, re.I) or "<" in target:
        return target
    path, hash_, frag = target.partition("#")
    if not path:
        return target
    resolved = posixpath.normpath(posixpath.join(posixpath.dirname(src_rel), path))
    if resolved.startswith(".."):
        return target
    is_dir = os.path.isdir(os.path.join(src_root, resolved))
    new = None
    if not is_dir and resolved in mapping:
        new = mapping[resolved]
    elif is_dir:
        cand = resolved
        if resolved == promoted:
            cand = "."
        elif resolved.startswith(promoted + "/"):
            cand = resolved[len(promoted) + 1:]
        if cand in derived_dirs or cand == ".":
            new = cand
    if new is not None:
        rel = posixpath.relpath(new, posixpath.dirname(out_rel) or ".")
        if is_dir and not rel.endswith("/"):
            rel += "/"
        return rel + hash_ + frag
    if os.path.exists(os.path.join(src_root, resolved)):
        kind = "tree" if is_dir else "blob"
        return f"{fs_url}/{kind}/main/{resolved}" + hash_ + frag
    return target


def code_spans(line):
    spans, i = [], 0
    while True:
        a = line.find("`", i)
        if a < 0:
            return spans
        b = line.find("`", a + 1)
        if b < 0:
            return spans
        spans.append((a, b))
        i = b + 1


out, fence = [], None
with open(dest, encoding="utf8") as fh:
    for line in fh:
        m = re.match(r"^\s*(```|~~~)", line)
        if m:
            fence = None if fence == m.group(1) else (fence or m.group(1))
            out.append(line)
            continue
        if fence:
            out.append(line)
            continue
        spans = code_spans(line)

        def sub(mm):
            if any(a < mm.start() < b for a, b in spans):
                return mm.group(0)
            return mm.group(1) + mm.group(2) + rewrite_target(mm.group(3))

        out.append(LINK.sub(sub, line))
with open(dest, "w", encoding="utf8") as fh:
    fh.write("".join(out))
