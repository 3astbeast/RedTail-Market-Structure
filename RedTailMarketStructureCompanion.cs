#region Using declarations
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.Direct2D1;
#endregion

// Designed by RedTail Indicators - https://github.com/3astbeast/RedTailIndicators

namespace NinjaTrader.NinjaScript.Indicators.RedTail
{
    /// <summary>
    /// Companion to RedTailMarketStructure.
    /// Add to any chart (e.g. 40 Range / tick) and it mirrors the Strong Levels
    /// and Order Block / Breaker Block zones computed by RedTailMarketStructure
    /// running on any open chart for the same instrument.
    /// No data-series linking required — the source registers itself in a static registry.
    /// Multiple instances can each point to a different source timeframe.
    /// </summary>
    public class RedTailMarketStructureCompanion : Indicator
    {
        #region Private fields

        private NinjaTrader.NinjaScript.Indicators.RedTail.RedTailMarketStructureV2 _source;
        private List<MSStrongLevelInfo>         _cachedStrong   = new List<MSStrongLevelInfo>();
        private List<MSOBZoneInfo>              _cachedZones    = new List<MSOBZoneInfo>();
        private bool                            _sourceFound;
        private DateTime                        _nextSearchTime     = DateTime.MinValue;
        private DateTime                        _lastRenderSnapshot = DateTime.MinValue;
        private static readonly TimeSpan        SearchInterval      = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan        SnapshotInterval    = TimeSpan.FromMilliseconds(250);
        private string                          _resolvedKey;
        private bool                            _debugPrinted;  // suppress repeated registry dumps once connected

        // SharpDX brushes / formats / stroke styles
        private SharpDX.Direct2D1.Brush         _dxStrongHigh;
        private SharpDX.Direct2D1.Brush         _dxStrongLow;
        private SharpDX.Direct2D1.Brush         _dxBullOBFill;
        private SharpDX.Direct2D1.Brush         _dxBearOBFill;
        private SharpDX.Direct2D1.Brush         _dxBullBreakerFill;
        private SharpDX.Direct2D1.Brush         _dxBearBreakerFill;
        private SharpDX.Direct2D1.Brush         _dxBullOBBorder;
        private SharpDX.Direct2D1.Brush         _dxBearOBBorder;
        private SharpDX.Direct2D1.Brush         _dxMitigatedBrush;
        private SharpDX.Direct2D1.Brush         _dxStatusOk;
        private SharpDX.Direct2D1.Brush         _dxStatusErr;
        private SharpDX.Direct2D1.StrokeStyle   _dxStrongStroke;
        private SharpDX.DirectWrite.TextFormat   _dxStatusFmt;
        private SharpDX.DirectWrite.TextFormat   _dxLabelFmt;

        #endregion

        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = "Mirrors RedTailMarketStructure Strong Levels and OB/Breaker zones onto this chart.";
                Name                        = "RedTail Market Structure Companion";
                Calculate                   = Calculate.OnBarClose;
                IsOverlay                   = true;
                DisplayInDataBox            = false;
                DrawOnPricePanel            = true;
                IsSuspendedWhileInactive    = false;
                MaximumBarsLookBack         = MaximumBarsLookBack.Infinite;

                // Source
                SourceBarsPeriodType        = BarsPeriodType.Minute;
                SourceBarsPeriodValue       = 5;

                // Strong levels
                ShowStrongLevels            = true;
                StrongHighColor             = Brushes.OrangeRed;
                StrongLowColor              = Brushes.DodgerBlue;
                StrongLevelWidth            = 1;
                StrongLevelStyle            = DashStyleHelper.Dash;
                StrongLevelOpacity          = 80;
                ShowMitigatedLevels         = false;

                // Order blocks
                ShowOrderBlocks             = true;
                ShowBreakerBlocks           = true;
                ShowMitigatedZones          = false;
                BullOBColor                 = Brushes.SeaGreen;
                BullOBOpacity               = 50;
                BearOBColor                 = Brushes.Crimson;
                BearOBOpacity               = 50;
                BullBreakerColor            = Brushes.Crimson;
                BullBreakerOpacity          = 35;
                BearBreakerColor            = Brushes.SeaGreen;
                BearBreakerOpacity          = 35;
                OBBorderOpacity             = 100;

                ShowStatusLabel             = true;
            }
            else if (State == State.DataLoaded)
            {
                _cachedStrong   = new List<MSStrongLevelInfo>();
                _cachedZones    = new List<MSOBZoneInfo>();
                _sourceFound    = false;
                _nextSearchTime = DateTime.MinValue; // fire immediately on first opportunity
                _debugPrinted   = false;

                if (Instrument != null)
                {
                    var fakePeriod = new BarsPeriod
                    {
                        BarsPeriodType  = SourceBarsPeriodType,
                        Value           = SourceBarsPeriodValue
                    };
                    _resolvedKey = NinjaTrader.NinjaScript.Indicators.RedTail.RedTailMarketStructureV2.BuildRegistryKey(Instrument, fakePeriod);
                }

                // Try immediately — the source chart may already be loaded and registered
                FindSource();
                _nextSearchTime = DateTime.Now + SearchInterval;
            }
            else if (State == State.Terminated)
            {
                DisposeResources();
            }
        }

        #endregion

        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;

            if (!_sourceFound || _source == null)
            {
                if (DateTime.Now >= _nextSearchTime)
                {
                    FindSource();
                    _nextSearchTime = DateTime.Now + SearchInterval;
                }
            }

            if (_sourceFound && _source != null)
            {
                _cachedStrong = _source.GetStrongLevels();
                _cachedZones  = _source.GetOBZones();
            }
        }

        #endregion

        #region Source discovery

        private void FindSource()
        {
            _source      = null;
            _sourceFound = false;

            if (Instrument == null || string.IsNullOrEmpty(_resolvedKey)) return;

            var registry = NinjaTrader.NinjaScript.Indicators.RedTail.RedTailMarketStructureV2.Registry;

            // Print registry snapshot only on first search so the output window doesn't flood.
            // Reset if registry is empty so we print again once the source registers.
            string companionId = "[" + (Instrument?.MasterInstrument?.Name ?? "?") + " Companion] ";
            if (!_debugPrinted || registry.Count == 0)
            {
                Print(companionId + "Searching for '" + _resolvedKey + "'. Registry (" + registry.Count + " entries):");
                foreach (var kvp in registry)
                    Print(companionId + "  key='" + kvp.Key + "'");
                if (registry.Count > 0) _debugPrinted = true;
            }

            // 1. Exact key match
            NinjaTrader.NinjaScript.Indicators.RedTail.RedTailMarketStructureV2 found;
            if (registry.TryGetValue(_resolvedKey, out found) && found != null)
            {
                _source      = found;
                _sourceFound = true;
                Print(companionId + "Connected via exact key match.");
                return;
            }

            // 2. Fallback: same instrument + period value, any BarsPeriodType
            // Uses StartsWith(instrName+"|") AND EndsWith("|"+value) to avoid cross-instrument hits
            string instrName = Instrument.MasterInstrument.Name.ToUpperInvariant();
            string suffix    = "|" + SourceBarsPeriodValue;
            foreach (var kvp in registry)
            {
                if (kvp.Value == null) continue;
                string key = kvp.Key.ToUpperInvariant();
                if (!key.StartsWith(instrName + "|")) continue;
                // EndsWith "|5" would match "|Minute|5" and "|15" — guard against that
                // by requiring the character before the value to be "|"
                int lastPipe = key.LastIndexOf('|');
                if (lastPipe < 0) continue;
                string keyValue = key.Substring(lastPipe + 1);
                if (keyValue != SourceBarsPeriodValue.ToString()) continue;
                _source      = kvp.Value;
                _sourceFound = true;
                Print(companionId + "Connected via fallback scan, matched '" + kvp.Key + "'");
                return;
            }

            Print(companionId + "No match found.");
        }

        #endregion

        #region SharpDX rendering

        public override void OnRenderTargetChanged()
        {
            DisposeResources();
            if (RenderTarget == null) return;

            // Strong levels
            _dxStrongHigh      = WpfToDx(StrongHighColor, StrongLevelOpacity);
            _dxStrongLow       = WpfToDx(StrongLowColor,  StrongLevelOpacity);
            _dxMitigatedBrush  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                                     new SharpDX.Color((byte)180, (byte)180, (byte)180, (byte)120));

            // OB fills
            _dxBullOBFill      = WpfToDx(BullOBColor,      BullOBOpacity);
            _dxBearOBFill      = WpfToDx(BearOBColor,      BearOBOpacity);
            _dxBullBreakerFill = WpfToDx(BullBreakerColor, BullBreakerOpacity);
            _dxBearBreakerFill = WpfToDx(BearBreakerColor, BearBreakerOpacity);

            // OB borders (full opacity version of fill colours, capped to OBBorderOpacity)
            _dxBullOBBorder    = WpfToDx(BullOBColor, OBBorderOpacity);
            _dxBearOBBorder    = WpfToDx(BearOBColor, OBBorderOpacity);

            // Status
            _dxStatusOk  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                               new SharpDX.Color((byte)100, (byte)200, (byte)100, (byte)220));
            _dxStatusErr = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                               new SharpDX.Color((byte)255, (byte)80, (byte)80, (byte)220));

            // Stroke style for strong levels
            var factory = Core.Globals.D2DFactory;
            var srProps = new StrokeStyleProperties();
            switch (StrongLevelStyle)
            {
                case DashStyleHelper.Dash:       srProps.DashStyle = SharpDX.Direct2D1.DashStyle.Dash;       break;
                case DashStyleHelper.Dot:        srProps.DashStyle = SharpDX.Direct2D1.DashStyle.Dot;        break;
                case DashStyleHelper.DashDot:    srProps.DashStyle = SharpDX.Direct2D1.DashStyle.DashDot;    break;
                case DashStyleHelper.DashDotDot: srProps.DashStyle = SharpDX.Direct2D1.DashStyle.DashDotDot; break;
                default:                         srProps.DashStyle = SharpDX.Direct2D1.DashStyle.Solid;      break;
            }
            _dxStrongStroke = new StrokeStyle(factory, srProps);
            _dxStatusFmt    = new SharpDX.DirectWrite.TextFormat(
                                  Core.Globals.DirectWriteFactory, "Arial", 10f);
            _dxLabelFmt     = new SharpDX.DirectWrite.TextFormat(
                                  Core.Globals.DirectWriteFactory, "Arial",
                                  SharpDX.DirectWrite.FontWeight.Bold,
                                  SharpDX.DirectWrite.FontStyle.Normal, 9f);
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (chartControl == null || chartScale == null) return;

            // Retry source discovery from OnRender so we don't depend on bars forming.
            // Covers pre-market, slow instruments, and the initial load window.
            if (!_sourceFound || _source == null)
            {
                if (DateTime.Now >= _nextSearchTime)
                {
                    FindSource();
                    _nextSearchTime = DateTime.Now + SearchInterval;
                    if (_sourceFound && _source != null)
                    {
                        _cachedStrong = _source.GetStrongLevels();
                        _cachedZones  = _source.GetOBZones();
                        _lastRenderSnapshot = DateTime.Now;
                    }
                }
            }
            else
            {
                // Source is connected — keep cache fresh even when this chart's tab is inactive
                // (IsSuspendedWhileInactive=false keeps OnRender firing, but OnBarUpdate may not).
                if (DateTime.Now - _lastRenderSnapshot >= SnapshotInterval)
                {
                    _cachedStrong       = _source.GetStrongLevels();
                    _cachedZones        = _source.GetOBZones();
                    _lastRenderSnapshot = DateTime.Now;
                }
            }

            float w    = (float)ChartPanel.W;
            float pTop = (float)ChartPanel.Y;
            float pBot = pTop + (float)ChartPanel.H;

            if (!_sourceFound || _source == null)
            {
                if (ShowStatusLabel)
                    DrawStatus("MS Companion: no source found – is RedTail Market Structure open on the "
                               + SourceBarsPeriodValue + " " + SourceBarsPeriodType + " chart?",
                               w, pTop, ok: false);
                return;
            }

            // Right edge = current bar's right pixel + a small extension, clamped to panel width.
            // This matches how the source draws OBs ending near the current bar, not at panel edge.
            float currentBarX;
            try { currentBarX = (float)chartControl.GetXByBarIndex(ChartBars, CurrentBar); }
            catch { currentBarX = w; }
            // Extend a fixed ~20px past the current bar to match "BoxExtendBars" visual feel.
            float rightEdge = Math.Min(currentBarX + 20f, w);

            // ── Order Block / Breaker Block zones ──────────────────────
            if (ShowOrderBlocks || ShowBreakerBlocks)
            {
                double vaFilterVAH = 0, vaFilterVAL = 0;
                bool vaFilterActive = _source != null && _source.GetValueAreaFilter(out vaFilterVAH, out vaFilterVAL);

                foreach (var z in _cachedZones)
                {
                    if (z.Disabled && !ShowMitigatedZones) continue;
                    if (z.IsBreaker  && !ShowBreakerBlocks) continue;
                    if (!z.IsBreaker && !ShowOrderBlocks)   continue;

                    // Mirror the source's value-area filter: bull OBs must be fully below VAL,
                    // bear OBs must be fully above VAH.
                    if (vaFilterActive)
                    {
                        if (z.IsBull  && z.Top    >= vaFilterVAL) continue;
                        if (!z.IsBull && z.Bottom <= vaFilterVAH) continue;
                    }

                    float yTop = chartScale.GetYByValue(z.Top);
                    float yBot = chartScale.GetYByValue(z.Bottom);
                    if (yTop > pBot || yBot < pTop) continue;

                    yTop = Math.Max(yTop, pTop);
                    yBot = Math.Min(yBot, pBot);
                    float height = yBot - yTop;
                    if (height <= 0) continue;

                    // Pick fill / border brush based on direction + type
                    SharpDX.Direct2D1.Brush fill, border;
                    if (z.IsBreaker)
                    {
                        fill   = z.IsBull ? _dxBearBreakerFill : _dxBullBreakerFill;
                        border = z.IsBull ? _dxBearOBBorder    : _dxBullOBBorder;
                    }
                    else
                    {
                        fill   = z.IsBull ? _dxBullOBFill   : _dxBearOBFill;
                        border = z.IsBull ? _dxBullOBBorder : _dxBearOBBorder;
                    }

                    var rect = new SharpDX.RectangleF(0, yTop, rightEdge, height);
                    RenderTarget.FillRectangle(rect, fill);
                    RenderTarget.DrawRectangle(rect, border, 1f);
                }
            }

            // ── Strong Levels ───────────────────────────────────────────
            if (ShowStrongLevels && _dxLabelFmt != null)
            {
                int fadedOpacity = Math.Max((int)(StrongLevelOpacity * 0.65), 5);

                foreach (var sl in _cachedStrong)
                {
                    if (sl.Mitigated && !ShowMitigatedLevels) continue;

                    float y = chartScale.GetYByValue(sl.Price);
                    if (y < pTop || y > pBot) continue;

                    SharpDX.Direct2D1.Brush lineBrush;
                    string label;
                    if (sl.Mitigated)
                    {
                        lineBrush = _dxMitigatedBrush;
                        label     = sl.IsHigh ? "Strong High (Mitigated)" : "Strong Low (Mitigated)";
                    }
                    else
                    {
                        lineBrush = sl.IsHigh ? _dxStrongHigh : _dxStrongLow;
                        label     = sl.IsHigh ? "Strong High" : "Strong Low";
                    }

                    // Draw line from left to right edge (pinned right, same as source)
                    RenderTarget.DrawLine(
                        new Vector2(0, y), new Vector2(w, y),
                        lineBrush, StrongLevelWidth, _dxStrongStroke);

                    // Draw label just left of the right edge, above/below the line
                    try
                    {
                        using (var tl = new SharpDX.DirectWrite.TextLayout(
                            Core.Globals.DirectWriteFactory, label, _dxLabelFmt, 220f, 20f))
                        {
                            float lx = w - tl.Metrics.Width - 6f;
                            float ly = sl.IsHigh ? y - tl.Metrics.Height - 1f : y + 2f;
                            RenderTarget.DrawTextLayout(new Vector2(lx, ly), tl, lineBrush);
                        }
                    }
                    catch { }
                }
            }

            // ── Status label ────────────────────────────────────────────
            if (ShowStatusLabel)
            {
                string src = SourceBarsPeriodValue + " " + SourceBarsPeriodType;
                DrawStatus("MS Companion \u2713 " + (_source.InstrumentName ?? "") + " (" + src + ")",
                           w, pTop, ok: true);
            }
        }

        private void DrawStatus(string msg, float chartW, float pTop, bool ok)
        {
            if (_dxStatusFmt == null) return;
            var brush = ok ? _dxStatusOk : _dxStatusErr;
            if (brush == null) return;
            using (var tl = new SharpDX.DirectWrite.TextLayout(
                Core.Globals.DirectWriteFactory, msg, _dxStatusFmt, chartW, 20))
            {
                float tx = chartW - tl.Metrics.Width - 10;
                RenderTarget.DrawTextLayout(new Vector2(tx, pTop + 6), tl, brush);
            }
        }

        private void DisposeResources()
        {
            SafeDispose(ref _dxStrongHigh);
            SafeDispose(ref _dxStrongLow);
            SafeDispose(ref _dxBullOBFill);
            SafeDispose(ref _dxBearOBFill);
            SafeDispose(ref _dxBullBreakerFill);
            SafeDispose(ref _dxBearBreakerFill);
            SafeDispose(ref _dxBullOBBorder);
            SafeDispose(ref _dxBearOBBorder);
            SafeDispose(ref _dxMitigatedBrush);
            SafeDispose(ref _dxStatusOk);
            SafeDispose(ref _dxStatusErr);
            SafeDispose(ref _dxStrongStroke);
            SafeDispose(ref _dxStatusFmt);
            SafeDispose(ref _dxLabelFmt);
        }

        private void SafeDispose<T>(ref T obj) where T : class, IDisposable
        {
            if (obj != null) { obj.Dispose(); obj = null; }
        }

        /// <summary>Convert a WPF brush to a SharpDX SolidColorBrush, applying an opacity 0-100 override.</summary>
        private SharpDX.Direct2D1.SolidColorBrush WpfToDx(System.Windows.Media.Brush wpf, int opacityPct)
        {
            byte a = (byte)Math.Max(0, Math.Min(255, opacityPct * 255 / 100));
            var  scb = wpf as System.Windows.Media.SolidColorBrush;
            if (scb == null)
                return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)255, (byte)255, (byte)255, a));
            var c = scb.Color;
            return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)c.R, (byte)c.G, (byte)c.B, a));
        }

        #endregion

        #region Properties

        // ── Source ──────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Source Timeframe Type",
                 Description = "Bar period type of the source RedTailMarketStructure chart",
                 Order = 1, GroupName = "Source")]
        public BarsPeriodType SourceBarsPeriodType { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Source Timeframe Value",
                 Description = "Bar period value of the source chart (e.g. 5 for 5-minute, 786 for 786-tick)",
                 Order = 2, GroupName = "Source")]
        public int SourceBarsPeriodValue { get; set; }

        // ── Strong Levels ────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show Strong Levels", Order = 1, GroupName = "Strong Levels")]
        public bool ShowStrongLevels { get; set; }

        [XmlIgnore]
        [Display(Name = "Strong High Color", Order = 2, GroupName = "Strong Levels")]
        public System.Windows.Media.Brush StrongHighColor { get; set; }
        [Browsable(false)]
        public string StrongHighColorS
        {
            get { return Serialize.BrushToString(StrongHighColor); }
            set { StrongHighColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Strong Low Color", Order = 3, GroupName = "Strong Levels")]
        public System.Windows.Media.Brush StrongLowColor { get; set; }
        [Browsable(false)]
        public string StrongLowColorS
        {
            get { return Serialize.BrushToString(StrongLowColor); }
            set { StrongLowColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Line Width", Order = 4, GroupName = "Strong Levels")]
        [Range(1, 5)]
        public int StrongLevelWidth { get; set; }

        [Display(Name = "Line Style", Order = 5, GroupName = "Strong Levels")]
        public DashStyleHelper StrongLevelStyle { get; set; }

        [Display(Name = "Opacity %", Order = 6, GroupName = "Strong Levels")]
        [Range(10, 100)]
        public int StrongLevelOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Mitigated Levels",
                 Description = "Show faded/mitigated strong levels (drawn in grey)",
                 Order = 7, GroupName = "Strong Levels")]
        public bool ShowMitigatedLevels { get; set; }

        // ── Order Blocks / Breaker Blocks ────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show Order Blocks", Order = 1, GroupName = "Order Blocks")]
        public bool ShowOrderBlocks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Breaker Blocks", Order = 2, GroupName = "Order Blocks")]
        public bool ShowBreakerBlocks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Mitigated Zones",
                 Description = "Show invalidated / disabled order block zones",
                 Order = 3, GroupName = "Order Blocks")]
        public bool ShowMitigatedZones { get; set; }

        [XmlIgnore]
        [Display(Name = "Bull OB Color", Order = 4, GroupName = "Order Blocks")]
        public System.Windows.Media.Brush BullOBColor { get; set; }
        [Browsable(false)]
        public string BullOBColorS
        {
            get { return Serialize.BrushToString(BullOBColor); }
            set { BullOBColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Bull OB Opacity %", Order = 5, GroupName = "Order Blocks")]
        [Range(10, 100)]
        public int BullOBOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Bear OB Color", Order = 6, GroupName = "Order Blocks")]
        public System.Windows.Media.Brush BearOBColor { get; set; }
        [Browsable(false)]
        public string BearOBColorS
        {
            get { return Serialize.BrushToString(BearOBColor); }
            set { BearOBColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Bear OB Opacity %", Order = 7, GroupName = "Order Blocks")]
        [Range(10, 100)]
        public int BearOBOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Bull Breaker Color", Order = 8, GroupName = "Order Blocks")]
        public System.Windows.Media.Brush BullBreakerColor { get; set; }
        [Browsable(false)]
        public string BullBreakerColorS
        {
            get { return Serialize.BrushToString(BullBreakerColor); }
            set { BullBreakerColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Bull Breaker Opacity %", Order = 9, GroupName = "Order Blocks")]
        [Range(10, 100)]
        public int BullBreakerOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Bear Breaker Color", Order = 10, GroupName = "Order Blocks")]
        public System.Windows.Media.Brush BearBreakerColor { get; set; }
        [Browsable(false)]
        public string BearBreakerColorS
        {
            get { return Serialize.BrushToString(BearBreakerColor); }
            set { BearBreakerColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Bear Breaker Opacity %", Order = 11, GroupName = "Order Blocks")]
        [Range(10, 100)]
        public int BearBreakerOpacity { get; set; }

        [Display(Name = "Border Opacity %", Order = 12, GroupName = "Order Blocks")]
        [Range(10, 100)]
        public int OBBorderOpacity { get; set; }

        // ── Display ──────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show Status Label", Order = 1, GroupName = "Display")]
        public bool ShowStatusLabel { get; set; }

        #endregion
    }
}
