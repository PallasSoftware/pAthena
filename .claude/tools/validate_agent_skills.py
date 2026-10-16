#!/usr/bin/env python3
from pathlib import Path
import re, sys, json

ROOT = Path(__file__).resolve().parents[2]
SKILLS = ROOT / ".claude" / "skills"
bundle_skills = [
    "auditing-cross-server-flows",
    "auditing-persistence-mutations",
    "debugging-stock-client-failures",
    "designing-athena-features",
    "implementing-athena-features",
    "investigating-rathena",
    "maintaining-generated-world-content",
    "reverse-engineering-iro-packets",
    "reviewing-athena-changes",
    "triangulating-ro-evidence",
    "verifying-iro-protocol",
    "verifying-reference-coverage",
    "writing-regression-tests",
]
errors=[]; warnings=[]; names={}
required_headings = ["## Use when", "## Do not use when", "## Escalate to", "## Hand off to"]
for skill in (SKILLS / name for name in sorted(bundle_skills)):
    p=skill/"SKILL.md"
    if not p.exists():
        errors.append(f"{skill.name}: missing SKILL.md"); continue
    text=p.read_text()
    lines=text.splitlines()
    m=re.match(r"---\n(.*?)\n---\n", text, re.S)
    if not m:
        errors.append(f"{skill.name}: missing/invalid YAML frontmatter"); continue
    fm=m.group(1)
    nm=re.search(r"^name:\s*(.+)$", fm, re.M)
    desc=re.search(r"^description:\s*(.+)$", fm, re.M)
    if not nm or not desc:
        errors.append(f"{skill.name}: frontmatter requires name and description"); continue
    name=nm.group(1).strip(); description=desc.group(1).strip()
    if name != skill.name:
        errors.append(f"{skill.name}: frontmatter name is {name!r}")
    if name in names:
        errors.append(f"duplicate skill name {name}: {names[name]} and {skill}")
    names[name]=skill
    if "Use when" not in description or "Do not use" not in description:
        errors.append(f"{skill.name}: description must contain explicit 'Use when' and 'Do not use' routing boundaries")
    for h in required_headings:
        if h not in text:
            errors.append(f"{skill.name}: missing routing heading {h}")
    if len(lines) >= 500:
        errors.append(f"{skill.name}: {len(lines)} lines (must stay below 500)")
    elif len(lines) > 200:
        warnings.append(f"{skill.name}: {len(lines)} lines; consider progressive disclosure")
    if re.search(r"Read\s+`AGENTS\.md`", text, re.I) or "`ai/README.md`" in text:
        warnings.append(f"{skill.name}: broad project-doc preload detected; prefer task-relevant docs only")
    if "implementing-rathena-features" in text:
        errors.append(f"{skill.name}: stale skill name implementing-rathena-features")
    # Validate sibling markdown links.
    for target in re.findall(r"\[[^\]]+\]\(([^)]+\.md)\)", text):
        if "://" not in target and not (skill/target).exists():
            errors.append(f"{skill.name}: missing referenced file {target}")

# Structural files.
for req in ["CLAUDE.md", ".claude/settings.json", ".claude/hooks/protect-repository-paths.py"]:
    if not (ROOT/req).exists(): errors.append(f"missing required bundle file {req}")
for req in ["generated-content.md","legacy-references.md","protocol-code.md","persistence.md","world-authority.md"]:
    if not (ROOT/".claude"/"rules"/req).exists(): errors.append(f"missing path rule {req}")

# settings.json syntax and hook registration.
try:
    settings=json.loads((ROOT/".claude"/"settings.json").read_text())
    entries=settings.get("hooks",{}).get("PreToolUse",[])
    if not any("Edit" in e.get("matcher","") and "Bash" in e.get("matcher","") for e in entries):
        errors.append("settings.json: expected PreToolUse protection for Edit/Write/Bash")
except Exception as e:
    errors.append(f"settings.json invalid: {e}")

print(f"Validated {len(names)} skills")
for w in warnings: print("WARNING:", w)
for e in errors: print("ERROR:", e)
if errors: sys.exit(1)
print("Bundle validation passed")
