<p align="center">
  <img src="https://avatars.githubusercontent.com/u/209633456?v=4" width="160" alt="RedTail Indicators Logo"/>
</p>

<h1 align="center">RedTail Market Structure</h1>

<p align="center">
  <b>A complete market structure analysis indicator for NinjaTrader 8.</b><br>
  BOS/CHoCH detection, order blocks, FRVP integration, displacement candles, equal highs/lows, and liquidity sweeps — all in one tool.
</p>

<p align="center">
  <a href="https://buymeacoffee.com/dmwyzlxstj">
    <img src="https://img.shields.io/badge/☕_Buy_Me_a_Coffee-FFDD00?style=flat-square&logo=buy-me-a-coffee&logoColor=black" alt="Buy Me a Coffee"/>
  </a>
</p>

<p align="center">
  <img src="https://raw.githubusercontent.com/3astbeast/RedTail-Market-Structure/refs/heads/main/Screenshot%202026-03-03%20084922.png" width="800" alt="RedTail Market Structure Screenshot"/>
</p>

---

## Overview

RedTail Market Structure is a comprehensive market structure indicator that identifies Breaks of Structure (BOS), Changes of Character (CHoCH), order blocks, strong/weak swing levels, displacement candles, equal highs/lows, and liquidity sweeps. It also integrates a full Fixed Range Volume Profile (FRVP) engine with Fibonacci retracements, Anchored VWAP, and K-Means cluster detection — all triggered automatically from structure events.

---

## Market Structure Detection

The core engine identifies swing highs and swing lows using a configurable swing length, then classifies each structural break:

- **Break of Structure (BOS)** — Price breaks a prior swing in the direction of the existing trend, confirming continuation
- **Change of Character (CHoCH)** — Price breaks a prior swing against the trend, signaling a potential reversal
- **BOS Confirmation** — Configurable confirmation logic for when a BOS is validated
- Swing labels (HH, HL, LH, LL) with configurable font size and color
- BOS/CHoCH lines with independent color, dash style, width, and opacity

**Trend Display** — On-chart trend status panel showing the current bias (Bullish, Bearish, or Neutral) with a warning state when an opposing CHoCH fires. Configurable position (Top Left / Top Right), font size, X/Y offset, and independent colors for bullish, bearish, warning, and neutral states.

---

## 50% Retracement

Draws a 0.5 (50%) retracement level between swing points. Useful for identifying equilibrium and potential mean-reversion zones. Configurable color, style, width, and opacity.

---

## Strong / Weak Levels

Classifies each swing high and low as Strong or Weak based on a composite scoring system:

- **Volume Multiplier** — Swing volume relative to average (higher = stronger)
- **Min Strength Score** — Composite threshold (0–100) combining volume, displacement, and rejection. Lower values produce more strong levels; higher values are more selective.
- Strong levels draw as horizontal rays extending to the right until mitigated
- **Mitigation Options** — When price reaches a strong level, choose to remove it entirely or keep it with a faded appearance
- Independent colors for Strong Highs and Strong Lows
- Configurable line width, style, opacity, and label font size

---

## Order Blocks

Detects and draws order blocks (supply/demand zones) at key structural points.

- **OB Swing Length** — Configurable swing lookback for order block detection
- **Max ATR Multiplier** — Filters out oversized candles
- **Draw Style** — Full Range (entire candle) or Wick Only (rejection wick from wick extreme to nearest body edge)
- **Zone Invalidation** — Wick-based or body-based invalidation logic
- **Zone Count** — Low or High density setting
- **Box Extend Bars** — How far forward the OB rectangle extends
- **Delete Broken Boxes** — Automatically remove invalidated order blocks
- **Show Historic Zones** — Toggle visibility of already-mitigated zones
- Independent bullish/bearish OB colors with per-side opacity and border opacity

---

## Integrated FRVP (Fixed Range Volume Profile)

A full volume profile engine built into the market structure indicator, triggered automatically from structure events.

**FRVP Trigger Modes:**
- **CHoCH** — Draws the FRVP immediately when a Change of Character occurs
- **BOS** — Waits for a confirming Break of Structure before drawing

**Volume Profile Features:**
- Configurable number of rows, profile width %, and alignment (Left/Right)
- Volume types: Standard, Bullish, Bearish, or Both (polarity coloring)
- Point of Control (POC) with color, width, style, and opacity
- Value Area (VAH/VAL) with configurable percentage, bar color, VA lines, and right extension
- Gradient fill with configurable intensity
- Adaptive rendering with Gaussian smoothing and min/max bar pixel height
- Boundary outline with independent color, opacity, and width
- Labels for POC/VA with optional price display
- **Keep Previous FRVP** — Optionally retain the prior FRVP when a new structure event fires

**Fibonacci Retracements:**
- Up to 10 customizable Fib levels with per-level colors (set level to -1 to disable)
- Configurable line width, style, opacity, and label font size
- Optional right extension and price display on labels
- Default levels: 0, 23.6%, 38.2%, 50%, 61.8%, 78.6%, 100% with extension slots

**Anchored VWAP:**
- AVWAP anchored to the swing origin of the FRVP zone
- Configurable color, width, style, and opacity
- Optional right extension and label display

**K-Means Cluster Levels:**
- Segments the FRVP volume into 2–10 clusters using K-Means algorithm
- Detects the POC within each cluster to find high-volume nodes at different price regions
- Configurable iterations, rows per cluster, line width, style, and opacity
- Optional right extension and labels
- Up to 5 independently colored cluster levels

---

## Displacement Candles

Highlights candles with abnormally strong momentum — potential signals of institutional activity.

- **ATR Multiplier** — Candle range must exceed ATR × this value to qualify
- **Min Body %** — Minimum candle body as a percentage of total range (filters dojis and wicks)
- Independent bullish/bearish displacement colors
- Configurable marker opacity

---

## Equal Highs / Lows

Detects swing highs and lows at approximately the same price — areas of resting liquidity that are common targets for sweeps.

- **Tolerance** — Maximum distance in ticks between swings to be considered equal (1–20)
- Independent Equal High and Equal Low colors
- Configurable line width, style, opacity, and label font size

---

## Liquidity Sweeps

Detects when price wicks through a key level and reverses — a sign of liquidity being grabbed before a directional move.

- **Min Sweep Depth** — Wick must extend at least N ticks past the level (1–20)
- **Min Rejection Wick %** — Sweep-side wick as a percentage of candle range (filters weak sweeps)
- **Volume Filter** — Optionally require above-average volume on the sweep candle (with configurable multiplier). Can be disabled for tick charts.
- **Sweep Targets** — Detects sweeps of strong/weak levels and optionally mitigated levels
- **Sweep Order Blocks** — Also detect sweeps of active order block edges
- Bullish sweep (sweep of lows) and bearish sweep (sweep of highs) with independent colors
- Configurable rectangle opacity
- Sound alerts on sweep detection

---

## Alerts

**Structure Alerts**
- Alert on BOS and/or CHoCH with independent .wav sound files
- Alert on order block creation and order block touch
- Alert on AVWAP touch and Fibonacci level touch
- Each alert type has its own configurable sound file

**Voice Alerts**
- Auto-generated spoken alerts with instrument name (e.g., "NQ Bullish CHoCH")
- Uses edge-tts neural voice synthesis with SAPI5 fallback
- Configurable voice speed (-10 to +10)
- Alert cooldown timer to prevent spam
- Fallback sound file if voice generation fails

---

## Installation

1. Download the .cs file from the indicator's repository
2. Copy the .cs to documents\Ninja Trader 8\bin\custom\indicators
3. Open Ninja Trader (if not already open) 
4. In control center, go to New --> Ninja Script Editor
5. Expand the Indicator Tree, find your new indicator, double click to open it
6. At the top of the Editor window, click the "Compile" button
7. That's it!

---

## Part of the RedTail Indicators Suite

This indicator is part of the [RedTail Indicators](https://github.com/3astbeast/RedTailIndicators) collection — free NinjaTrader 8 tools built for futures traders who demand precision.

---

<p align="center">
  <a href="https://buymeacoffee.com/dmwyzlxstj">
    <img src="https://img.shields.io/badge/☕_Buy_Me_a_Coffee-Support_My_Work-FFDD00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black" alt="Buy Me a Coffee"/>
  </a>
</p>
