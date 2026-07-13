# Plannit

A self-hosted personal financial planning web app: track net worth across every account, import real bank and brokerage statements, analyze spending, and model retirement with deterministic and Monte Carlo projections.

Built with **.NET 10 / ASP.NET Core MVC**, EF Core + SQLite, ASP.NET Core Identity, Bootstrap 5, and Chart.js — no SPA framework, no CDN dependencies, deployable as a single Docker container.

## Features

**Net worth tracking**
- Accounts of every type (checking, savings, credit cards, 401(k), Roth/Traditional IRA, brokerage) with dated balance snapshots
- Liability-aware math — credit cards count against net worth, with sign normalization no matter how the data arrives
- Dashboard cockpit: net worth with 1-month/12-month deltas, asset allocation, stale-balance nudges, recent activity

**Statement imports**
- CSV with per-account column-mapping profiles (map once, one-click thereafter), multi-file upload, mixed formats per batch
- OFX/QFX with `FITID`-based dedup and automatic balance-snapshot refresh from `LEDGERBAL`
- Brokerage *positions* exports (e.g. Fidelity) summed into balance snapshots
- PDF statements (e.g. TIAA quarterly) parsed for ending balance via PdfPig
- SHA-256 row hashing makes every import idempotent — re-importing overlapping statements skips duplicates
- Per-account polarity handling for banks that export charges as positive amounts

**Expense analytics**
- Rule-based auto-categorization (contains/starts-with/regex) with a rule tester and "create rule from transaction"
- Reports with arbitrary date ranges and presets: category breakdown, monthly trend, income vs. expenses, top merchants
- Budgets with per-category progress tracking; recurring-charge detection surfaces subscriptions and their annualized cost
- Transfer pair-matching so moving money between accounts never reads as spending
- Bulk operations, transaction splits, notes, CSV export, and one-click undo of any import batch

**Retirement projections**
- Deterministic year-by-year engine: contributions, employer match, per-account return rates, inflation-adjusted retirement spending, tax-ordered withdrawals (taxable → traditional → Roth)
- Monte Carlo mode: 1,000-iteration simulation with percentile fan charts and a success-probability headline
- Life events (home sale, college years), FIRE-number tracking, and side-by-side scenario comparison ("retire at 60 vs 65")

## Engineering notes

- **Multi-tenant by construction** — every entity is scoped to its owner through EF Core global query filters set from middleware; isolation is proven by a two-user integration test suite, not convention
- **Pure, tested domain logic** — the projection engine is a static, side-effect-free function covered by known-answer unit tests (fixed-rate growth, depletion age, withdrawal ordering, inflation effects)
- **Defensive import pipeline** — untrusted statement files are size-capped, extension-whitelisted, staged under generated GUID names, and parsed with per-row error reporting that never fails an entire batch
- **Security hardening** — anti-forgery on all mutating endpoints, non-backtracking regex evaluation for user-authored rules (ReDoS-safe), path-traversal-proof temp file handling, sanitized reflected input
- **Ops-ready** — multi-stage Dockerfile, data-protection keys and SQLite persisted to a mounted volume, automatic migrations in production, hot backups via `sqlite3 .backup`, documented PostgreSQL migration path

## Running locally

```bash
cd Plannit
dotnet run
```

App runs at http://localhost:5103 — register an account and start importing.

Database migrations:

```bash
dotnet ef migrations add <Name>
dotnet ef database update
```

Tests:

```bash
dotnet test
```

## Deployment

```bash
docker build -t plannit .
docker run -d -p 8080:8080 -v plannit-data:/data plannit
```

Data (SQLite database + data-protection keys) lives in `/data` — always mount a volume. Registration can be disabled in production config for private instances.
