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
user actually sees, translates them with Claude, writes the resource files your stack expects, and —
when you ask it to — rewrites the sources to read from those resources.

Works on .NET, web, Android, Apple and Flutter projects. Bring your own Anthropic API key.

```powershell
aegis-localizer --path ./my-app --lang ru,es,de --apply
```

> **Licence.** This source is published so it can be read. It is **not** open source. Using, running,
> copying or modifying it requires prior written permission — see [Licence](#licence).

**Contents** · [Stacks](#supported-stacks) · [Prerequisites](#prerequisites) ·
[Build from source](#build-from-source) · [Run it](#run-it) · [Build the artifacts](#build-the-artifacts) ·
[Everyday use](#everyday-use) · [Running it again](#running-it-again) · [How it works](#how-it-works) ·
[Safety](#safety) · [Extending](#extending-it) · [Troubleshooting](#troubleshooting)

## Supported stacks

| `--platform` | Sources it reads | Resources it writes | What it rewrites literals to |
|---|---|---|---|
| `dotnet` | `.cs` `.cshtml` `.razor` `.xaml` `.axaml` | `.resx` | `L.Key`, `L.Format("Key", …)` |
| `web` | `.tsx` `.jsx` `.ts` `.js` `.vue` `.svelte` `.html` | i18next JSON | `{t("Key")}`, `{{ $t("Key") }}` |
| `android` | `.kt` `.java`, layout `.xml` | `res/values-<loc>/strings.xml` | `@string/key`, `getString(R.string.key)`, `stringResource(…)` |
| `apple` | `.swift` `.m` `.h` `.storyboard` `.xib` | `<loc>.lproj/Localizable.strings` | `String(localized: "Key")`, `NSLocalizedString` |
| `flutter` | `.dart` | `app_<loc>.arb` | `AppLocalizations.of(context)!.key` |

`--platform auto` (the default) picks one by sniffing the tree.

---

## Prerequisites

| | |
|---|---|
| **.NET SDK 9.0 or later** | Everything targets `net9.0`. Get it from [dotnet.microsoft.com](https://dotnet.microsoft.com/download). Check with `dotnet --version`. |
| **An Anthropic API key** | Only needed to translate. Scanning is free and offline. Get one at [console.anthropic.com](https://console.anthropic.com/settings/keys). |
| **Git** | To clone. |

Nothing else — no Node, no Python, no database. The web interface is plain HTML and JavaScript with
no build step.

Builds and runs on Windows, macOS and Linux.

## Build from source

```bash
git clone https://github.com/aegisaosoft/aegis-localizer.git
cd aegis-localizer

dotnet build
```

That restores packages and builds all four projects. First run takes a minute or so while NuGet
fetches Roslyn; after that it is a couple of seconds.

Run the test suite:

```bash
dotnet test
```

146 tests, all offline. They drive the entire pipeline — scan, classify, translate, write, rewrite —
against a fake model, so **no API key is needed and no request is ever sent**. A green run takes
about a second. If anything here fails, stop and fix it before running the tool against real code.

<details>
<summary>Building a single project</summary>

```bash
dotnet build src/Aegis.Localizer.Core     # the pipeline library
dotnet build src/Aegis.Localizer.Cli      # the command line front end
dotnet build src/Aegis.Localizer.Web      # the graphical / hosted front end
dotnet test  tests/Aegis.Localizer.Tests  # the suite
```
</details>

## Run it

### Your API key

Resolved in this order — the first one that is set wins:

1. `--api-key sk-ant-...` on the command line
2. the `ANTHROPIC_API_KEY` environment variable
3. `Claude:ApiKey` in `appsettings.json` next to the executable

```powershell
# Windows (PowerShell)
$env:ANTHROPIC_API_KEY = "sk-ant-..."
```

```bash
# macOS / Linux
export ANTHROPIC_API_KEY="sk-ant-..."
```

For a permanent local setting, copy `src/Aegis.Localizer.Cli/appsettings.json` to
`appsettings.local.json` and put the key there — that filename is gitignored so it cannot be
committed by accident.

### The command line, from source

```bash
dotnet run --project src/Aegis.Localizer.Cli -- --help
```

Everything after `--` goes to the tool. Try it on one of the bundled samples, which costs nothing:

```bash
dotnet run --project src/Aegis.Localizer.Cli -- --path samples/DemoApp --lang ru --scan-only
```

Then a real run against the same sample — this one calls the API and costs a few cents:

```bash
dotnet run --project src/Aegis.Localizer.Cli -- --path samples/DemoApp --lang ru
```

It writes `samples/DemoApp/Localization/*.resx` and a report under
`samples/DemoApp/.aegis-localizer/`. Nothing in the sample's own source is touched unless you add
`--apply`. To undo an `--apply`, copy the files back from `.aegis-localizer/backup/`.

The other samples exercise the other adapters:

```bash
dotnet run --project src/Aegis.Localizer.Cli -- --path samples/WebApp     --lang ru --scan-only
dotnet run --project src/Aegis.Localizer.Cli -- --path samples/AndroidApp --lang ru --scan-only
dotnet run --project src/Aegis.Localizer.Cli -- --path samples/SwiftApp   --lang ru --scan-only
dotnet run --project src/Aegis.Localizer.Cli -- --path samples/FlutterApp --lang ru --scan-only
```

### The graphical interface, from source

```bash
dotnet run --project src/Aegis.Localizer.Cli -- ui
```

Picks a free port, starts the interface and opens your browser: choose a folder, choose languages,
watch the run stream, review a table of source-to-translation pairs, download the result. If
`ANTHROPIC_API_KEY` is set it is picked up automatically and you never type the key into a browser.

```bash
dotnet run --project src/Aegis.Localizer.Cli -- ui --port 5099 --no-browser
```

You can also start the web host directly, which is handy when you are changing it:

```bash
dotnet run --project src/Aegis.Localizer.Web
```

That listens on `http://localhost:5099` (see `Properties/launchSettings.json`).

### As a hosted service

The same web app, told not to trust the machine it runs on:

```bash
ASPNETCORE_URLS=http://0.0.0.0:8080 \
Localizer__LocalMode=false \
dotnet run --project src/Aegis.Localizer.Web
```

In hosted mode it refuses to open a path on the server — the only input is an uploaded `.zip`, which
is extracted into a per-request sandbox — and it ignores any API key in the server's environment, so
a visitor cannot spend the operator's credit.

| Setting | Env var | Default | Meaning |
|---|---|---|---|
| `Localizer:LocalMode` | `Localizer__LocalMode` | `true` | `true` = desktop use, local folders allowed. `false` = hosted, uploads only. |
| `Localizer:MaxUploadMegabytes` | `Localizer__MaxUploadMegabytes` | `100` | Largest archive accepted in hosted mode. |
| — | `ASPNETCORE_URLS` | `http://localhost:5099` | What to listen on. |

## Build the artifacts

Two distribution channels, because they serve different people:

```powershell
.\build\publish.ps1              # Windows
```

```bash
./build/publish.sh               # macOS / Linux
```

Each script runs the tests first and refuses to package if they fail, then produces, in
`artifacts/`:

- **`Aegis.Localizer.<version>.nupkg`** — a .NET tool, for people who already have the SDK
- **`aegis-localizer-<rid>.zip`** for `win-x64`, `osx-arm64`, `osx-x64` and `linux-x64` — a
  self-contained binary that needs no runtime, with the graphical interface in a `ui/` subfolder

Build for one platform only, or stamp a version:

```powershell
.\build\publish.ps1 -Runtimes win-x64 -Version 1.2.0
```

```bash
./build/publish.sh linux-x64
```

Trimming is deliberately off: culture lookup goes through ICU and C# is parsed with Roslyn, both of
which use reflection, so a trimmed build would fail at run time rather than at build time.

### Installing the tool you just built

```bash
dotnet tool install --global --add-source ./artifacts Aegis.Localizer
aegis-localizer --help
```

Or unzip the archive for your platform and run `aegis-localizer` from it directly.

---

## Everyday use

The safe order, and the one worth following the first time:

```bash
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

### Options

| Flag | Meaning |
|---|---|
| `--path <dir>` | Project root. Defaults to the current directory. |
| `--lang <a,b,c>` | Target cultures: `ru`, or `ru,es,pt-BR`. |
| `--scan-only` | List what the scanner found and exit. No API call, no cost. |
| `--apply` | Rewrite the sources. Without it every run is a dry run. |
| `--setup` | Add the localization support the project is missing, before rewriting. |
| `--context "<text>"` | What the product is, for better wording. |
| `--keep <a,b>` | Terms never to translate. |
| `--retranslate` | Redo translations that already exist, not just the missing ones. |
| `--platform <name>` | `auto` (default), or `dotnet` / `web` / `android` / `apple` / `flutter`. |
| `--format <name>` | `resx` / `json` / `android` / `apple` / `arb` / `po`. Adapter default if unset. |
| `--out <dir>` | Resource folder. Adapter convention if unset. |
| `--resource-name <n>` | Bundle base name. Default `AppResources`. |
| `--namespace <ns>` | Namespace for generated glue. Inferred if unset. |
| `--source-lang <c>` | Language the source strings are written in. Default `en`. |
| `--api-key <key>` | Overrides the environment and appsettings. |
| `--model <id>` | Default `claude-sonnet-5`. |
| `--no-cache` | Ignore `.aegis-localizer/cache.json`. |
| `--batch-size <n>` | Strings per request. Default 25. |
| `--concurrency <n>` | Parallel requests. Default 4. |
| `--exclude <a;b>` | Extra path fragments to skip. |
| `--max-files <n>` | Stop after N files. 0 means no limit. |
| `--include-diagnostics` | Treat log and exception text as localizable too. |
| `--json` | Emit the run summary as JSON, for CI. |
| `--verbose` | Per-stage detail. |
| `--config <file>` | Defaults to `aegis-localizer.json` in the project root. |

Full list: `aegis-localizer --help`.

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

`--json` prints a machine-readable summary. Combined with `--scan-only` it makes a useful gate: fail
the build when someone adds a hardcoded string.

## If the app was never built for localization

Most apps have not been. They have no i18n dependency, no generated-localizations config, and
nothing that ever picks a culture. Translating such an app's strings and rewriting its code would
leave it **worse than it started** — not compiling, or compiling and stubbornly English.

So every run inspects the project first and says exactly what is missing, including `--scan-only`,
which costs nothing:

```bash
aegis-localizer --path ./my-app --lang ru --scan-only
```

```
This project is missing localization support:
  ! Add flutter_localizations to pubspec.yaml
      dependencies:
        flutter_localizations:
          sdk: flutter
      Without it the generated AppLocalizations class has nothing to build on.
  ! Register the localization delegates on your app widget
      ...
  Run again with --setup to add the parts that can be added automatically.
```

`--setup` adds what it can. It works on its own, without translating anything:

```bash
aegis-localizer --path ./my-app --lang ru --scan-only --setup
```

It creates files the tool owns (`l10n.yaml`, the i18n bootstrap module) and makes **additive** edits
to manifests (`pubspec.yaml`, `package.json`, the `.csproj`) — nothing is reordered or reformatted.
It will **never** restructure your own code: a step like registering Flutter's localization delegates
inside your widget tree is reported with the exact snippet, for you to place.

**`--apply` refuses to rewrite while anything required is still missing.** The translations are still
produced and written to the bundles, so no work is lost — you deal with the remaining steps and run
again. Shipping a project that no longer builds is the one outcome this tool will not risk.

## Running it again

Localizing an app is not a one-off. Run the tool as often as you like — it only does the work that is
actually outstanding, and a run with nothing to do costs nothing.

```bash
# Someone added new copy: only the new strings are translated.
aegis-localizer --path ./my-app --lang ru,es --apply

# Add a language months later: the whole app is translated, not just what is still in the source.
aegis-localizer --path ./my-app --lang de --apply

# Redo everything, e.g. after changing --context or the model.
aegis-localizer --path ./my-app --lang ru --retranslate
```

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
one of these formats spells "not translated yet": your app falls back to the source language, and the
next run sees a gap and retries. A bundle full of English that claims to be German would look
finished for ever.

### What the tool leaves behind

```
your-project/
  Localization/                  resource bundles + generated accessor (path varies by stack)
  .aegis-localizer/
    report.<langs>.md            human-readable review of the run
    report.<langs>.json          the same, machine-readable
    cache.json                   verdicts and translations, to avoid paying twice
    state.json                   which English each translation was made from
    backup/                      originals of every file --apply touched
```

`.aegis-localizer/` is working state. Add it to your `.gitignore`; commit the resource bundles.

## How it works

1. **Scan.** C# is parsed with Roslyn, so verbatim, raw and interpolated strings decode correctly and
   every candidate carries an exact source span. Other stacks use tolerant lexers that mask comments,
   script blocks and existing lookups before matching, preserving byte offsets.
2. **Pre-filter.** Machine strings are dropped before anything is sent anywhere: URLs, paths, GUIDs,
   MIME types, SQL, date patterns, identifiers, `SCREAMING_SNAKE`. Structural context drops more —
   `nameof`, equality comparisons, `switch` patterns, `StartsWith` arguments, wiring attributes like
   `[Route]` or `android:id`. Every string dropped here is one you do not pay for.
3. **Classify.** The survivors go to Claude in batches with a forced tool call, so the answer is
   schema-validated JSON rather than prose: user-facing yes/no, a reason, and a key. This pass is
   language-independent and cached, so adding a language later never re-pays for it.
4. **Translate.** Once per target language, cached per language, and only for what is missing or
   stale.
5. **Write.** Bundles are **merged**, never overwritten — hand-edited translations and keys from
   earlier runs survive, because your already-rewritten code depends on them.
6. **Rewrite** (`--apply` only). Backed up first, and a span is edited only while its text still
   matches what the scanner saw.

## What it will not do

Some constructs are reported but deliberately left for a human, because a mechanical edit would not
compile or would fail at run time. The report lists each one with a reason.

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
- Every touched file is backed up **byte for byte** before it is written.
- A span is rewritten only if it still matches what the scanner saw — a file edited since the scan is
  skipped, not corrupted.
- Files that are not valid UTF-8 are left alone entirely rather than being re-encoded and mangled.
- Directory symlinks are never followed, so a link back to an ancestor cannot make a scan run for
  ever.
- Translations that lose or invent a placeholder (`{0}`, `%s`, `{{count}}`) are rejected, because
  that failure only surfaces as a crash in production.
- Running twice is a no-op: existing lookups are not re-extracted, keys are stable, bundles merge.
- If every request to the model fails, the run stops with an error rather than reporting that your
  strings were all rejected.

## Extending it

Adding a stack means implementing `ISourceAdapter` (which files, how to extract, how to rewrite) and
registering it in `AdapterRegistry`. A new resource layout means `IResourceStore` and
`ResourceStoreRegistry`. Scanning, batching, caching, translation, reporting and the backup-safe
rewrite are shared and need no changes.

The test suite is generic over both: add your adapter to `AdapterContractTests.Samples` and it
inherits the whole contract suite — span accuracy, line numbers, idempotency, bundle format. A guard
test fails if a registered adapter or format has no coverage, so a new stack cannot ship untested.

```
src/Aegis.Localizer.Core/       the pipeline; every front end calls LocalizationRunner
  Platforms/                    one folder per stack, behind ISourceAdapter
  Resources/                    one file per resource format, behind IResourceStore
  Ai/                           prompts, batching, caches, staleness tracking
  Filtering/                    NoiseFilter, KeyNamer
  Emit/                         source rewriting, reports, generated glue
  Scanning/                     safe tree walking
  Io/                           encoding-preserving file access
src/Aegis.Localizer.Cli/        the command line front end
src/Aegis.Localizer.Web/        the graphical front end; runs locally and as a hosted service
tests/Aegis.Localizer.Tests/    offline suite; a fake model drives the whole pipeline
samples/                        one small project per stack, for trying things out
build/                          packaging scripts
```

## Troubleshooting

**`dotnet build` fails with `error CS0579: Duplicate ... attribute`**
You redirected `BaseIntermediateOutputPath` and un-excluded the default `obj/`. `Directory.Build.props`
handles the usual cases; if you invented a new output folder, add it there too.

**`aegis-localizer ui` says the UI component was not found**
The web host has to sit next to the CLI. From source, run `dotnet build` first. From a package, make
sure you unzipped the whole archive and not just the executable.

**"No Anthropic API key"**
Set `ANTHROPIC_API_KEY`, or pass `--api-key`. `--scan-only` needs no key at all.

**The scan finds nothing**
Check the detected platform in the first lines of output. If it guessed wrong, pass `--platform`
explicitly. Generated files, `node_modules`, `bin`, `obj` and friends are skipped on purpose.

**A run reported success but translated nothing**
Look at the "rejected as non-UI" count in the report — it lists every string and why the model ruled
it out. If they look wrong, `--context` usually fixes it; then re-run with `--retranslate`.

**Everything is suddenly being retranslated**
The cache key includes the model, `--context`, `--keep` and `--source-lang`. Changing any of them is
meant to invalidate it.

## Licence

**Proprietary — all rights reserved.** Copyright (c) 2025-2026 Aegis AO Soft LLC and Alexander Orlov.

This source is published so it can be read. It is **not** open source. You may look at it; you may
not use, run, copy, modify or redistribute it — in whole or in part — without prior written
permission from the copyright holders. The repository being public is not permission, and is not an
implied licence of any kind.

To ask for permission, contact Alexander Orlov / Aegis AO Soft LLC. Full terms in [LICENSE](LICENSE).
