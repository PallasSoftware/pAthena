---
name: verifying-reference-coverage
description: Independently reconstructs expected behavior and compares it with a completed Athena.NET feature to find omissions in reference-relevant behavior, side effects, validation, timers, persistence, scripts, variants, and failure paths. Use when implementation is complete or prior work missed hidden behavior and an independent omission audit is needed. Do not use as a general code-quality review or to define expectations from the implementation itself.
---

# Verifying Reference Coverage

## Use when

Use after implementation, or when prior work has revealed missed behavior, to independently determine whether all relevant expected behavior was covered.

## Do not use when

Do not use as a general maintainability/style review, and do not derive the expected-behavior checklist from the implementation being audited.

## Escalate to

- `triangulating-ro-evidence` if the independent expectation itself hits conflicting evidence.
- `reverse-engineering-iro-packets` if a missing/uncertain item is fundamentally unknown wire behavior.

## Hand off to

- `implementing-athena-features` if the task includes fixing confirmed omissions.
- `writing-regression-tests` for every material omission that is corrected.
- `reviewing-athena-changes` after coverage is established for code-quality review.


## Purpose

Find what the implementation **forgot**.

This is deliberately different from checking whether the code looks reasonable. Reconstruct the expected behavior independently, then compare.

The project does not seek generic rAthena parity. “Coverage” means coverage of behavior relevant to the requested Athena.NET/iRO feature.

## Independence rule

Do not use the implementation itself as the definition of expected behavior.

Start from:

1. user requirement;
2. stock-iRO evidence/project docs when client-facing;
3. pinned reference sources;
4. existing architectural invariants.

Only after rebuilding that expectation should you compare it to Athena.NET.

## Expectation-first gate

Build and record the expected-behavior matrix **before inspecting the implementation diff in detail**. The first-pass expectation must be source/evidence-derived, not implementation-derived. After that matrix exists, inspect Athena.NET and classify coverage. This ordering is mandatory for substantial audits because it reduces confirmation bias.

## Fresh-pass audit

Repeat a targeted source investigation and explicitly search for:

- additional entry points;
- secondary callers;
- helper-side packet emission;
- helper-side persistence;
- alternate failure paths;
- compile-time branches;
- regional/PACKETVER/Renewal differences;
- timers and delayed callbacks;
- inter-server request/reply completion;
- cleanup on disconnect/map change/death;
- duplicate request/idempotency paths;
- observer/nearby-player notifications;
- duplicate/template script placements;
- source-data/config-driven behavior;
- state changes that occur before/after the obvious handler.

Use Graphify/LSP for discovery if available, but verify against actual source.

## Compare by behavior, not file shape

For each expected behavior, locate:

- Athena.NET implementation symbol;
- authoritative state owner;
- test proving it;
- evidence status;
- any intentional project divergence.

Do not require Athena.NET to have the same number of functions/classes or same call graph as rAthena.

## Coverage statuses

Use only:

- `COVERED` — relevant behavior is implemented and backed by a useful test or strong direct evidence.
- `COVERED_UNTESTED` — implementation exists but lacks a meaningful regression test.
- `INTENTIONAL_DIVERGENCE` — Athena.NET intentionally differs, with project/iRO justification.
- `MISSING` — relevant behavior is absent.
- `UNVERIFIED` — cannot prove whether behavior applies or is implemented.
- `NOT_APPLICABLE` — legacy/reference behavior does not apply to the current iRO/Athena.NET target.

## High-risk omissions

Treat these as high severity unless proven irrelevant:

- missing ownership/auth validation;
- trusting client-supplied gameplay state;
- reporting success before required persistence;
- missing rollback/idempotency;
- duplicated authoritative state;
- missing death/disconnect/map-transition cleanup;
- incorrect packet ordering;
- missing source-data variants;
- missing generic/base member of an rAthena `duplicate(...)` family;
- manually edited generated output;
- a lower-authority reference overriding verified iRO behavior.

## Review output

For substantial or reusable work, use [COVERAGE-MATRIX.md](COVERAGE-MATRIX.md). For a small focused task, preserve the same reasoning structure but report directly unless a persistent artifact is useful.

Lead with findings, ordered by severity. Give exact source and Athena.NET symbols/paths.

Do not automatically “fix while reviewing” unless the task explicitly asks for a fix. A clean independent audit is more valuable than silently changing the target during review.

## Exit condition

Pass only when:

- no material `MISSING` item remains;
- every `UNVERIFIED` item is either resolved or explicitly accepted as an unknown;
- intentional divergences have evidence/rationale;
- critical behaviors have regression coverage.
