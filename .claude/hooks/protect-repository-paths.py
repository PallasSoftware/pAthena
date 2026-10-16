#!/usr/bin/env python3
"""Claude Code PreToolUse guardrail for Athena.NET protected repository paths.

Blocks direct Edit/Write to generated output and pinned legacy references.
For Bash, allows read-only inspection and approved WorldDataImporter generation,
but blocks commands that mention protected paths and are not clearly read-only.
"""
import json, os, re, sys
from pathlib import Path

PROTECTED = ("src/MapServer/Generated/", "legacy/rathena/", "legacy/openkore/")

def norm(p: str) -> str:
    p = (p or "").replace("\\", "/")
    project = os.environ.get("CLAUDE_PROJECT_DIR", "").replace("\\", "/").rstrip("/")
    if project and p.startswith(project + "/"):
        p = p[len(project)+1:]
    while p.startswith("./"):
        p = p[2:]
    return p

def protected_path(p: str):
    n = norm(p)
    for pref in PROTECTED:
        if n == pref.rstrip("/") or n.startswith(pref):
            return pref
    return None

def deny(msg: str):
    print(msg, file=sys.stderr)
    raise SystemExit(2)

def approved_generator(cmd: str) -> bool:
    c = " ".join(cmd.split())
    return (
        "dotnet run" in c
        and "tools/WorldDataImporter/WorldDataImporter.csproj" in c
        and not re.search(r"\b(?:rm|mv|cp|sed|perl|python|python3|ruby|tee|truncate)\b", c)
    )

MUTATING = re.compile(
    r"\b(?:rm|rmdir|mv|cp|touch|truncate|install|chmod|chown|ln|patch|dd|rsync|mkdir|unlink|shred)\b"
    r"|\bsed\b[^\n]*\s-[A-Za-z]*i"
    r"|\bperl\b[^\n]*\s-[A-Za-z]*i"
    r"|\btee\b"
    r"|\bgit\s+(?:-C\s+\S+\s+)?(?:checkout|restore|reset|clean|apply|am|stash|rm|mv|add|commit|merge|rebase|cherry-pick|revert|pull|push|fetch|switch|config)\b"
    r"|\bgit\s+(?:-C\s+\S+\s+)?submodule\s+(?:update|add|deinit|sync|init|set-url|set-branch)\b"
    r"|\b(?:curl|wget)\b[^\n]*\s(?:-o|-O|--output)\b"
    r"|\b(?:python3?|perl|ruby|node|sh|bash|zsh)\b[^\n]*(?:open\([^)]*['\"][wax+]|write_text|write_bytes|shutil\.|os\.(?:remove|unlink|rename|replace|rmdir|makedirs)|\.unlink\(|\.rename\(|fs\.write|File\.write)"
)
REDIRECT = re.compile(r"(?<![<>&\d])(?:\d?>{1,2})(?!&)\s*([^\s&|;<>]+)")
SPLIT = re.compile(r"&&|\|\||;|\||\n")

def mentions_protected(text: str) -> bool:
    t = text.replace("\\", "/")
    return any(p.rstrip("/") in t for p in PROTECTED)

def cd_target(stmt: str):
    m = re.match(r"\s*(?:cd|pushd)\s+(['\"]?)([^'\"\s]+)\1", stmt)
    return m.group(2) if m else None

def mutates_protected(cmd: str) -> bool:
    """True if a statement mutates (or may mutate) a protected path.

    Read-only use of protected paths (rg, cat, ls, git log, interpreters that
    only read, output redirected elsewhere) is allowed. Statements after a `cd`
    into a protected directory are treated as touching it. This is a static
    guardrail against accidents, not a sandbox.
    """
    cwd_protected = False
    for stmt in SPLIT.split(cmd):
        target = cd_target(stmt)
        if target is not None:
            cwd_protected = protected_path(target) is not None or mentions_protected(target)
            continue
        relevant = cwd_protected or mentions_protected(stmt)
        for m in REDIRECT.finditer(stmt):
            dest = m.group(1).strip("'\"")
            if dest == "/dev/null":
                continue
            if protected_path(dest) or (cwd_protected and not dest.startswith("/")):
                return True
        if relevant and MUTATING.search(stmt):
            return True
    return False

def main():
    data = json.load(sys.stdin)
    tool = data.get("tool_name", "")
    ti = data.get("tool_input") or {}

    if tool in {"Edit", "Write"}:
        path = ti.get("file_path") or ti.get("path") or ""
        pref = protected_path(path)
        if pref:
            if pref == "src/MapServer/Generated/":
                deny(f"Blocked direct {tool} of {norm(path)}. Generated content must be changed through WorldDataImporter/compiler/source selection and regenerated.")
            deny(f"Blocked direct {tool} of {norm(path)}. Pinned legacy reference repositories are read-only during normal Athena.NET work.")
        return 0

    if tool == "Bash":
        cmd = ti.get("command") or ""
        mentioned = [p for p in PROTECTED if p.rstrip("/") in cmd.replace("\\", "/")]
        if not mentioned:
            # Also protect routine submodule updates that can silently move pins.
            if "git submodule update --remote" in cmd:
                deny("Blocked `git submodule update --remote`. Athena.NET reference submodules are intentionally pinned; update gitlinks only as an explicit dependency-maintenance task.")
            return 0
        if approved_generator(cmd):
            return 0
        if not mutates_protected(cmd):
            return 0
        deny("Blocked Bash command that may modify a protected generated/legacy path. Read-only inspection is allowed; change the owning generator and regenerate instead.")

    return 0

if __name__ == "__main__":
    raise SystemExit(main())
