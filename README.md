# Traitor1 (CLI + Webull Executor + Alpaca Market Data)

## BEN TODOS:

- keep fixing all the api calls/contracts
- ask chatGpt how it knows how many shares to place an order for once it identifies a stock & setup
- how do we limit it from placing too many trades? Ask chatgpt if that is already baked in somehow - probably in the RiskManager
- figure out how to use the paper trading stuff
- figure out how to test a theory in the past and see if it worked - will need chatGpt help
- connect it to Azure / OpenAI for the LLM part

- ENSURE there is a MAX order size on all trades BEFORE we go to the webull live trading and make it REALLY small, just to test and see what hapens
- rename everything that is OliverValez or ZStockinator to Traitor
- rename the Traitor1.Cli and Traitor1.Core stuff to Traitor.Cli & Traitor.Core
- extend functionality to work in extended and pre-hours
- ask chatGpt to add comments to all lines to explain what it is doing and why
- add a database so we can capture/log everything
  - every data pull and what we got
  - store historical data for ourselves
  - errors table in case anything goes wrong with details
  - every decision on every stock (ignore, watch, trade)
  - every order we try to place
  - every order that is filled
  - conputations on trades good or bad

## Background

This repo is a **starter** .NET 8 solution that:

- pulls market data from **Alpaca Data API**
- runs **pre-filters**
- computes indicators (EMA20/EMA200/VWAP/ATR14)
- scans for an _Oliver Velez–style_ pattern:
  - tight consolidation on 5-min bars
  - breakout with an "elephant" candle (large body + volume)
- optionally asks an **LLM proxy** to score candidates
- executes trades using either:
  - **Paper** executor (default)
  - **Webull OpenAPI** executor (polling + stop-first safety + exit-monitor cancel-other)

> This is for **education/testing**. It does **not** predict prices.

---

## Projects

- `src/Traitor1.Core` – library with market data client, indicators, scanner, risk, executors
- `src/Traitor1.Cli` – command-line runner
- `src/Traitor1LlmProxy` – a minimal HTTP server with `/score` (stub scorer by default)

---

## Prereqs

- .NET SDK **8.x**
- Alpaca API keys with market data access
- (Optional) Webull OpenAPI app keys + account id

---

## Quick start (Paper mode)

1. Edit `src/Traitor1.Cli/appsettings.json` and set Alpaca keys in:

   - `MarketData:ApiKey`
   - `MarketData:ApiSecret`

2. Run:

```bash
dotnet run --project src/Traitor1.Cli
```

It will:

- build the universe from your Watchlist + (optional) top gainers
- prefilter
- fetch 5-min bars for the lookback window
- scan for candidates
- use LLM scorer (disabled by default -> everything “Watch”)
- execute using Paper executor

---

## Enable the LLM Proxy (local)

In one terminal:

```bash
dotnet run --project src/Traitor1LlmProxy
```

In `src/Traitor1.Cli/appsettings.json` set:

```json
"Llm": {
  "Enabled": true,
  "BaseUrl": "http://localhost:5111/score",
  "ProxyKey": ""
}
```

Then run the CLI again. The proxy is a **stub scorer** you can replace with Azure OpenAI / OpenAI-compatible APIs.

---

## Enable Webull execution

In `src/Traitor1.Cli/appsettings.json` set:

```json
"Execution": { "Mode": "Webull" }
```

And fill:

- `Webull:AppKey`
- `Webull:AppSecret`
- `Webull:AccountId`

**Safety features included:**

- waits for entry fill
- places STOP first (required by default)
- verifies STOP is SUBMITTED
- optional panic MARKET exit if STOP fails
- if TP is used, monitors exits and cancels the other when one fills

---

## Backtesting

Run an offline historical replay that:

- fetches historical bars from Alpaca (e.g., 5-minute)
- runs the same Traidr setup scanner as live mode
- sizes trades with the RiskManager
- simulates limit entry fills, stop-loss, optional take-profit, and end-of-day flatten

Example:

```bash
dotnet run --project src/Traitor1.Cli -- backtest \
  --symbols SOXL,QBTS,PATH \
  --from 2025-01-01 \
  --to 2025-12-31 \
  --tpR 2.0 \
  --flatten 15:50 \
  --samebar conservative \
  --out out
```

Outputs:

- `out/trades.csv`
- `out/summary.json`

Flags:

- `--tpR <decimal>`: optional take-profit in R-multiples (e.g., 2.0)
- `--maxFillBars <int>`: how many bars after the signal we allow for the limit entry to fill
- `--slippagePct <decimal>`: applied on entry and exit
- `--commission <decimal>`: flat commission per trade
- `--samebar conservative|optimistic`: resolves same-bar stop+tp ambiguity

## Notes / Known gaps

- “Top gainers” uses a beta Alpaca screener endpoint that may not be available for all plans.
  - If you get errors, set `MarketData:EnableTopGainers=false` or `TopGainersCount=0`.
- The Traidr scanner rules here are **heuristics** that you’ll likely tune.
- Webull: This implementation is **best-effort OCO** (two independent exit orders + cancel-other monitoring).
  - In rare edge cases both exits can fill.

---

## License

MIT (add your own if desired).
