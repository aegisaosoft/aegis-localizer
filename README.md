<!--
  Copyright (c) 2025-2026 Aegis AO Soft LLC and Alexander Orlov.
  34 Middletown Ave, Atlantic Highlands, NJ 07716

  THIS SOFTWARE IS THE CONFIDENTIAL AND PROPRIETARY INFORMATION OF
  Aegis AO Soft LLC and Alexander Orlov.

  This code may be used, reproduced, modified, or distributed ONLY with the
  prior written permission of Aegis AO Soft LLC / Alexander Orlov.

  Author: Alexander Orlov
  Aegis AO Soft LLC
-->

# aegis-localizer

Point it at a codebase and a language. It finds the hardcoded UI strings, works out which ones a
user actually sees, translates them, writes the resource files your stack expects, and — when you
ask it to — rewrites the sources to read from those resources.

Works on .NET, web, Android, Apple and Flutter projects. Bring your own Anthropic API key.

```powershell
aegis-localizer --path ./my-app --lang ru,es,de --apply
```

## Supported stacks

| `--platform` | Sources | Resources written | Rewrites to |
|---|---|---|---|
| `dotnet` | `.cs` `.cshtml` `.razor` `.xaml` `.axaml` | `.resx` | `L.Key`, `L.Format("Key", …)` |
| `web` | `.tsx` `.jsx` `.ts` `.js` `.vue` `.svelte` `.html` | i18next JSON | `{t("Key")}`, `{{ $t("Key") }}` |
| `android` | `.kt` `.java` layout `.xml` | `res/values-<loc>/strings.xml` | `@string/key`, `getString(R.string.key)`, `stringResource(…)` |
| `apple` | `.swift` `.m` `.h` `.storyboard` `.xib` | `<loc>.lproj/Localizable.strings` | `String(localized: "Key")`, `NSLocalizedString` |
| `flutter` | `.dart` | `app_<loc>.arb` | `AppLocalizations.of(context)!.key` |

`--platform auto` (the default) picks one by sniffing the tree.

## Install

**As a .NET tool** (needs the .NET SDK):

```powershell
dotnet tool install -g Aegis.Localizer
```

**As a standalone binary** — download the archive for your platform from the release page and run
it. No runtime required. Build them yourself with `build/publish.ps1` or `build/publish.sh`.

## Your API key

The tool talks to the Anthropic API with **your** key; nothing is proxied through us and no code
leaves your machine except the extracted strings themselves. Get a key at
[console.anthropic.com](https://console.anthropic.com/settings/keys), then either:

```powershell
$env:ANTHROPIC_API_KEY = "sk-ant-..."     # or pass --api-key, or put Claude:ApiKey in appsettings.json
```

## The graphical interface

```powershell
aegis-localizer ui
```

Starts the interface on a free local port and opens it in your browser: pick a folder, pick
languages, watch the run stream, review the table of source-to-translation pairs, download the
result. It picks up `ANTHROPIC_API_KEY` from your environment if you have it set.

The same application is what runs as a hosted service. In that mode it never accepts a path — only
an uploaded `.zip`, which is extracted into a per-request sandbox, and it ignores any API key in the
server's environment so a visitor cannot spend the operator's credit.

## Using the command line

The safe order, and the one worth following the first time:

```powershell
# 1. Free: what would even be considered? Nothing is sent to the API.
aegis-localizer --path ./my-app --lang ru --scan-only

# 2. Ask Claude, write the resource bundles and a report. Sources untouched.
aegis-localizer --path ./my-app --lang ru,es --context "a car rental marketplace"

# 3. Read .aegis-localizer/report.ru-es.md, then commit to the rewrite.
aegis-localizer --path ./my-app --lang ru,es --apply
```

**Every run is a dry run until you pass `--apply`.** When you do, originals are copied to
`.aegis-localizer/backup/` first.

`--context` is worth the two seconds it takes to type: telling the model what the product is
measurably improves word choice. `--keep "Acme,ProDrive"` pins brand names that must not be
translated.

### Running it again

Localizing an app is not a one-off. Run the tool as often as you like — it only does the work that
is actually outstanding, and a run with nothing to do costs nothing.

```powershell
# Someone added new copy: only the new strings are translated.
aegis-localizer --path ./my-app --lang ru,es --apply

# Add a language months later: the whole app is translated, not just what is still in the source.
aegis-localizer --path ./my-app --lang de --apply

# Redo everything, e.g. after changing --context or the model.
aegis-localizer --path ./my-app --lang ru --retranslate
```

What each run picks up:

| Situation | What happens |
|---|---|
| A string was added to the code | Translated into every configured language |
| A string was reworded | Its translations are marked stale and redone |
| A translation is missing or was skipped | Filled in; nothing else is touched |
| A new language is added | Translated from the **source bundle**, so it covers the whole app |
| Nothing changed | No API call, no cost |

The bundle — not your source code — is the source of truth. Once `--apply` has rewritten a literal
into a lookup, the English lives in `AppResources.resx` (or the equivalent), and that is where you
edit it. The tool notices the edit and retranslates that key.

A translation that could not be produced — the model dropped a placeholder, or a request failed —
leaves the key **absent** from the target bundle rather than filled with English. That is how every
one of these formats spells "not translated yet": your app falls back to the source language, and
the next run sees a gap and retries. A bundle full of English that claims to be German would look
finished for ever.

### Config file

Drop `aegis-localizer.json` in your project root so a team (and CI) does not retype flags:

```json
{
  "languages": ["ru", "es", "de", "pt-BR"],
  "context": "a car rental marketplace, informal tone",
  "doNotTranslate": ["Acme", "ProDrive"],
  "exclude": ["src/legacy", "src/vendor"]
}
```

### In CI

`--json` prints a machine-readable summary. Combined with `--scan-only` it makes a useful gate:
fail the build when someone adds a hardcoded string.

### Full options

Run `aegis-localizer --help`.

## How it works

1. **Scan.** C# is parsed with Roslyn, so verbatim, raw and interpolated strings decode correctly
   and every candidate carries an exact source span. Other stacks use tolerant lexers that mask
   comments, script blocks and existing lookups before matching, preserving byte offsets.
2. **Pre-filter.** Machine strings are dropped before anything is sent anywhere: URLs, paths,
   GUIDs, MIME types, SQL, date patterns, identifiers, `SCREAMING_SNAKE`. Structural context drops
   more — `nameof`, equality comparisons, `switch` patterns, `StartsWith` arguments, wiring
   attributes like `[Route]` or `android:id`. Every string dropped here is one you do not pay for.
3. **Classify.** The survivors go to Claude in batches with a forced tool call, so the answer is
   schema-validated JSON rather than prose: user-facing yes/no, a reason, and a key. This pass is
   language-independent and cached, so adding a language later never re-pays for it.
4. **Translate.** Once per target language, cached per language.
5. **Write.** Bundles are **merged**, never overwritten — hand-edited translations and keys from
   earlier runs survive, because your already-rewritten code depends on them.
6. **Rewrite** (`--apply` only). Backed up first, and a span is edited only while its text still
   matches what the scanner saw.

## What it will not do

Some constructs are reported but deliberately left for a human, because a mechanical edit would not
compile or would fail at run time. The report lists each one with a reason. Examples:

- **.NET attribute arguments** (`[Display(Name = "…")]`) — attributes need compile-time constants.
- **XAML, storyboards and XIBs** — these need per-file markup wiring, not a text substitution.
- **Flutter `const` expressions and anything outside a verified `build(BuildContext context)`** —
  including `MaterialApp`'s own arguments, which are evaluated before localizations exist.
- **Android code with no `Context` in reach**, and Compose lookups outside a `@Composable`.
- **Interpolated strings with alignment or format clauses** (`{total:C}`).
- **Plain HTML** — there is no module to import a lookup into.

The rule throughout: when the tool is not certain a rewrite is correct, it reports instead of
guessing. A missed string costs you a minute; a broken build costs you an afternoon.

## Safety

This tool edits source code, so the guarantees matter more than the features:

- Dry run by default; `--apply` is always explicit.
- Every touched file is backed up before it is written.
- A span is rewritten only if it still matches what the scanner saw — a file edited since the scan
  is skipped, not corrupted.
- Translations that lose or invent a placeholder (`{0}`, `%s`, `{{count}}`) are rejected and fall
  back to the source string, because that failure only surfaces as a crash in production.
- Running twice is a no-op: existing lookups are not re-extracted, keys are stable, bundles merge.
- If every request to the model fails, the run stops with an error rather than reporting that your
  strings were all rejected.

## Extending it

Adding a stack means implementing `ISourceAdapter` (which files, how to extract, how to rewrite)
and registering it in `AdapterRegistry`. A new resource layout means `IResourceStore` and
`ResourceStoreRegistry`. Scanning, batching, caching, translation, reporting and the backup-safe
rewrite are shared and need no changes.

```
src/Aegis.Localizer.Core/
  LocalizationRunner.cs   the pipeline; every front end calls this
  Platforms/              one folder per stack, behind ISourceAdapter
  Resources/              one file per resource format, behind IResourceStore
  Ai/                     prompts, batching, caches
  Filtering/              NoiseFilter, KeyNamer
  Emit/                   source rewriting, reports, generated glue
src/Aegis.Localizer.Cli/  the command line front end
src/Aegis.Localizer.Web/  the graphical front end; runs locally and as a hosted service
tests/                    offline suite; a fake model drives the whole pipeline
samples/                  one small project per stack
```

## Licence

**Proprietary — all rights reserved.** Copyright (c) 2025-2026 Aegis AO Soft LLC and Alexander Orlov.

This source is published so it can be read. It is **not** open source. You may look at it; you may
not use, run, copy, modify or redistribute it — in whole or in part — without prior written
permission from the copyright holders. The repository being public is not permission, and is not an
implied licence of any kind.

To ask for permission, contact Alexander Orlov / Aegis AO Soft LLC. Full terms in [LICENSE](LICENSE).
