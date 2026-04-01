#region Using declarations
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Windows;

using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.RedTail
{
    #region TypeConverters for dropdown properties

    public class BOSConfirmationConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return true; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        { return new StandardValuesCollection(new[] { "Candle Close", "Wicks" }); }
    }

    public class LineStyleConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return true; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        { return new StandardValuesCollection(new[] { "Solid", "Dashed", "Dotted" }); }
    }

    public class ZoneInvalidationConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return true; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        { return new StandardValuesCollection(new[] { "Wick", "Close" }); }
    }

    public class OBDrawStyleConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return true; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        { return new StandardValuesCollection(new[] { "Full Range", "Wick Only" }); }
    }

    public class ZoneCountConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return true; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        { return new StandardValuesCollection(new[] { "One", "Low", "Medium", "High" }); }
    }

    public class StrongLevelMitigationConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return true; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        { return new StandardValuesCollection(new[] { "Remove", "Keep (Fade)", "Keep (Fade) + Clear New Session" }); }
    }

    public class TrendPositionConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return true; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        { return new StandardValuesCollection(new[] { "Top Left", "Top Right" }); }
    }

    public class VPAlignmentConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return true; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        { return new StandardValuesCollection(new[] { "Left", "Right" }); }
    }

    public class FRVPTriggerConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return true; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        { return new StandardValuesCollection(new[] { "CHoCH", "BOS (Confirmed Trend)" }); }
    }

    #endregion

    public enum MSVolumeType
    {
        Standard,
        Bullish,
        Bearish,
        Both
    }

    public enum MSRenderQuality
    {
        Manual,
        Adaptive
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Transfer structs — used by RedTailMarketStructureCompanion to receive
    // snapshots of live data without coupling to internal private classes.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Plain-old-data snapshot of one strong level.</summary>
    public class MSStrongLevelInfo
    {
        public double Price;
        public bool   IsHigh;     // true = resistance (swing high), false = support (swing low)
        public bool   Mitigated;  // level has been touched / faded
        public int    BarIndex;   // absolute bar index on the source chart where this level was formed
    }

    /// <summary>Plain-old-data snapshot of one order block or breaker block zone.</summary>
    public class MSOBZoneInfo
    {
        public double Top;
        public double Bottom;
        public bool   IsBull;     // true = bull OB / bear breaker (green side)
        public bool   IsBreaker;  // true = this OB has flipped into a breaker
        public bool   Disabled;   // broken / invalidated (shown only when ShowHistoricZones = true)
        public int    StartBar;   // absolute bar index where the OB was formed on the source chart
        public int    BreakBar;   // absolute bar index where it became a breaker (0 if still active OB)
    }

    public class RedTailMarketStructureV2 : Indicator
    {
        #region Private Classes

        private class OBSwing { public int X; public double Y; public double SwingVolume; public bool Crossed; }
        private class OBInfo
        {
            public double Top, Bottom;
            public string OBType, Tag;
            public int StartBar, BreakBar;
            public bool Breaker, Disabled, Swept;
        }

        private class FRVPZone
        {
            public int StartBar;
            public int EndBar;
            public double StartPrice;   // fib origin price (swing low for bull, swing high for bear)
            public double EndPrice;     // fib target price (swing high for bull, swing low for bear)
            public double HighPrice;    // dynamically tracked highest price in range
            public double LowPrice;     // dynamically tracked lowest price in range
            public int Direction;       // 1=bull CHoCH, -1=bear CHoCH
            public bool IsActive;
            public int AvwapAnchorBar;  // bar where AVWAP starts (the swing point)
            public List<double> Volumes;
            public double MaxVolume;
            public int PocIndex, VaUpIndex, VaDownIndex;
            public double ProfileLowest, ProfileInterval;
            public List<KeyValuePair<int, double>> AvwapPoints;
            public List<ClusterLevelInfo> ClusterLevels;
            public List<bool> VolumePolarities;
            public bool Dirty;
            public int LastProcessedBarIdx;  // for incremental FRVP calculation
            public double[] IncrBullVol;     // running bullish volume per row
            public double[] IncrBearVol;     // running bearish volume per row
        }

        private struct ClusterLevelInfo
        {
            public double POCPrice;
            public double POCVolume;
            public double ClusterHigh;
            public double ClusterLow;
            public double TotalVolume;
            public int BarCount;
        }

        private struct FibLevel { public double Ratio; public System.Windows.Media.Brush Color; }

        // ── SharpDX-only render data (replaces Draw.* objects) ──

        private struct BOSRenderInfo
        {
            public int StartBar;   // absolute bar index of the swing point
            public int EndBar;     // absolute bar index of the break bar (CurrentBar at detection)
            public double Price;
            public bool IsCHoCH;
            public bool IsBull;    // direction of the break
        }

        private struct SwingLabelInfo
        {
            public int BarIndex;   // absolute bar index
            public double Price;
            public string Label;
            public bool IsHigh;
        }

        private struct HalfRetraceInfo
        {
            public int StartBar;   // absolute bar index
            public int EndBar;     // absolute bar index
            public double Price;
        }

        private struct DisplacementInfo
        {
            public int BarIndex;
            public double Price;
            public bool IsBull;
        }

        private class StrongLevel
        {
            public double Price;
            public int BarIndex;
            public int BarIndex2;       // second swing bar (used by equal levels)
            public bool IsHigh;
            public bool Mitigated;
            public bool Swept;
            public string Tag;
        }

        #endregion

        #region Private Variables

        // Market Structure
        private double _prevHigh, _prevLow;
        private int _prevHighIndex, _prevLowIndex;
        private bool _highActive, _lowActive;
        private int _prevBreakoutDir, _prevSwingType, _msTagCounter;

        // Trend State: 0=none, 1=bull confirmed, -1=bear confirmed
        private int _trendState;
        // Pending CHoCH: 0=none, 1=bull choch pending BOS, -1=bear choch pending BOS
        private int _pendingChoch;

        // Leg origin tracking: remembers where each directional leg started
        private double _legOriginHigh;
        private int _legOriginHighIndex;
        private double _legOriginLow;
        private int _legOriginLowIndex;

        // Strong/Weak Levels
        private List<StrongLevel> _strongLevels;
        private DateTime _lastSessionDate;

        // Equal Highs/Lows
        private List<StrongLevel> _equalLevels;
        private List<KeyValuePair<int, double>> _swingHighHistory, _swingLowHistory;
        private int _eqTagCounter;

        // Displacement
        private System.Windows.Media.Brush _cachedBullDispBrush, _cachedBearDispBrush;
        private System.Windows.Media.Brush _cachedEqHighBrush, _cachedEqLowBrush;
        private System.Windows.Media.Brush _cachedBullSweepBrush, _cachedBearSweepBrush;
        private SimpleFont _eqFont;
        private int _sweepTagCounter;

        // FRVP
        private FRVPZone _activeFrvp;
        private List<FRVPZone> _historicFrvps;

        // Volumized OB
        private List<OBInfo> _bullOBList, _bearOBList;
        private OBSwing _obSwingTop, _obSwingBottom;
        private int _obSwingType, _obTagCounter;
        private double _atrValue;

        // Cached brushes
        private System.Windows.Media.Brush _bullFill, _bearFill, _bullBreakerFill, _bearBreakerFill;
        private System.Windows.Media.Brush _transparentBrush;
        private System.Windows.Media.Brush _cachedBOSBrush, _cachedRetraceBrush;
        private System.Windows.Media.Brush _cachedStrongHighBrush, _cachedStrongLowBrush;
        private SimpleFont _swingFont, _bosFont, _strongFont;
        private List<FibLevel> _cachedFibLevels;
        private bool _brushesCached;
        private int _lastFrvpBarCount;  // track when FRVP actually needs recalc

        // Cached SharpDX stroke styles (recreated only when render target changes)
        private SharpDX.Direct2D1.StrokeStyle _ssSolid, _ssDash, _ssDot, _ssDashDot;
        private SharpDX.Direct2D1.RenderTarget _cachedRT;

        // Voice Alert System
        private Dictionary<string, string> voiceAlertPaths = new Dictionary<string, string>();
        private string instrumentName = "";
        private Dictionary<string, DateTime> lastAlertTime;
        private Dictionary<string, bool> lastTouchState;

        // OB touch tracking (to avoid repeated alerts for same OB)
        private HashSet<string> _obTouchAlerted;

        // SharpDX-only render data lists (replaces Draw.* objects for speed)
        private List<BOSRenderInfo> _bosRenders;
        private List<SwingLabelInfo> _swingLabelRenders;
        private List<HalfRetraceInfo> _halfRetraceRenders;
        private List<DisplacementInfo> _displacementRenders;

        #endregion

        #region Cross-chart registry (used by RedTailMarketStructureCompanion)

        /// <summary>
        /// Global registry: key → running instance of RedTailMarketStructureV2.
        /// Allows the Companion indicator on any chart to locate this instance
        /// without requiring a data-series link.
        /// Key format: "INSTRUMENT|PERIODTYPE|PERIODVALUE"  e.g. "MNQ|Minute|5"
        /// </summary>
        public static readonly ConcurrentDictionary<string, RedTailMarketStructureV2>
            Registry = new ConcurrentDictionary<string, RedTailMarketStructureV2>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Build the registry lookup key from an instrument + bar period.</summary>
        public static string BuildRegistryKey(Instrument instrument, BarsPeriod period)
        {
            if (instrument == null || period == null) return string.Empty;
            return instrument.MasterInstrument.Name
                   + "|" + period.BarsPeriodType
                   + "|" + period.Value;
        }

        /// <summary>Expose the instrument name so the Companion can display it in its status label.</summary>
        public string InstrumentName => instrumentName;

        /// <summary>Expose current bar count so the Companion can compute OB right-edge pixel positions.</summary>
        public int SourceCurrentBar => CurrentBar;

        /// <summary>Expose BoxExtendBars so the Companion renders OBs with the same extension as the source.</summary>
        public int SourceBoxExtendBars => BoxExtendBars;

        /// <summary>
        /// Register this instance in the shared registry. Safe to call multiple times —
        /// uses Bars.BarsPeriod which is guaranteed valid from State.Historical onward.
        /// </summary>
        private void TryRegister()
        {
            try
            {
                if (Instrument == null || Bars == null || Bars.BarsPeriod == null) return;
                string regKey = BuildRegistryKey(Instrument, Bars.BarsPeriod);
                if (string.IsNullOrEmpty(regKey)) return;
                Registry[regKey] = this;
                Print("RedTail MS: Registered in companion registry as '" + regKey + "'");
            }
            catch (Exception ex)
            {
                Print("RedTail MS: Registry error: " + ex.Message);
            }
        }

        #endregion

        #region Public data accessors for RedTailMarketStructureCompanion

        /// <summary>
        /// Returns a snapshot of all current strong levels.
        /// Called each bar by the Companion to refresh its cached copy.
        /// Thread-safe: returns a new list so the Companion can iterate freely.
        /// </summary>
        public List<MSStrongLevelInfo> GetStrongLevels()
        {
            var result = new List<MSStrongLevelInfo>();
            if (_strongLevels == null) return result;
            foreach (var sl in _strongLevels)
            {
                result.Add(new MSStrongLevelInfo
                {
                    Price     = sl.Price,
                    IsHigh    = sl.IsHigh,
                    Mitigated = sl.Mitigated,
                    BarIndex  = sl.BarIndex
                });
            }
            return result;
        }

        /// <summary>
        /// Returns a snapshot of all current order block and breaker block zones.
        /// Called each bar by the Companion to refresh its cached copy.
        /// </summary>
        public List<MSOBZoneInfo> GetOBZones()
        {
            var result = new List<MSOBZoneInfo>();
            if (_bullOBList != null)
            {
                foreach (var ob in _bullOBList)
                {
                    result.Add(new MSOBZoneInfo
                    {
                        Top       = ob.Top,
                        Bottom    = ob.Bottom,
                        IsBull    = true,
                        IsBreaker = ob.Breaker,
                        Disabled  = ob.Disabled,
                        StartBar  = ob.StartBar,
                        BreakBar  = ob.BreakBar
                    });
                }
            }
            if (_bearOBList != null)
            {
                foreach (var ob in _bearOBList)
                {
                    result.Add(new MSOBZoneInfo
                    {
                        Top       = ob.Top,
                        Bottom    = ob.Bottom,
                        IsBull    = false,
                        IsBreaker = ob.Breaker,
                        Disabled  = ob.Disabled,
                        StartBar  = ob.StartBar,
                        BreakBar  = ob.BreakBar
                    });
                }
            }
            return result;
        }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "RedTail Market Structure - BOS/CHoCH, Volumized OBs, Integrated FRVP Fib";
                Name = "RedTail Market Structure";
                Calculate = Calculate.OnPriceChange;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;
                ZOrder = int.MinValue; // Render behind everything to avoid intercepting mouse events

                // Market Structure
                SwingLength = 20; BOSConfirmation = "Candle Close";
                ShowCHoCH = true; ShowSwingLabels = true;
                SwingLabelFontSize = 10; BOSLabelFontSize = 10; BOSOpacity = 80;
                ShowTrendDisplay = true; TrendDisplayFontSize = 12;
                TrendDisplayPosition = "Top Right"; TrendDisplayOffsetX = 15; TrendDisplayOffsetY = 15;
                TrendBullColor = System.Windows.Media.Brushes.Lime;
                TrendBearColor = System.Windows.Media.Brushes.OrangeRed;
                TrendWarningColor = System.Windows.Media.Brushes.Gold;
                TrendNeutralColor = System.Windows.Media.Brushes.Gray;
                ShowHalfRetracement = false; HalfRetracementColor = System.Windows.Media.Brushes.RoyalBlue;
                HalfRetracementStyle = "Solid"; HalfRetracementWidth = 1; HalfRetracementOpacity = 80;
                BOSColor = System.Windows.Media.Brushes.Gray; BOSStyle = "Dashed"; BOSWidth = 1;

                // FRVP on CHoCH
                EnableFRVP = true; KeepPreviousFRVP = false; FRVPTrigger = "BOS (Confirmed Trend)";
                FRVPRows = 250; FRVPProfileWidth = 30; FRVPVPAlignment = "Left";
                FRVPBarColor = System.Windows.Media.Brushes.Gray; FRVPBarOpacity = 40; FRVPBarThickness = 2;
                FRVPVolumeType = MSVolumeType.Standard;
                FRVPBullishBarColor = System.Windows.Media.Brushes.Green;
                FRVPBearishBarColor = System.Windows.Media.Brushes.Red;
                FRVPEnableGradientFill = false; FRVPGradientIntensity = 70;
                FRVPRenderQuality = MSRenderQuality.Adaptive;
                FRVPSmoothingPasses = 2; FRVPMinBarPixelHeight = 2.0f; FRVPMaxBarPixelHeight = 8.0f;
                FRVPDisplayPoC = true; FRVPPoCColor = System.Windows.Media.Brushes.Red; FRVPPoCWidth = 2;
                FRVPPoCStyle = DashStyleHelper.Solid; FRVPPoCOpacity = 100; FRVPExtendPoCVA = false;
                FRVPDisplayVA = true; FRVPValueAreaPct = 68;
                FRVPVABarColor = System.Windows.Media.Brushes.RoyalBlue; FRVPDisplayVALines = true;
                FRVPVALineColor = System.Windows.Media.Brushes.Gold; FRVPVALineWidth = 1;
                FRVPVALineStyle = DashStyleHelper.Dash; FRVPVALineOpacity = 80;
                FRVPShowLabels = true; FRVPLabelFontSize = 10; FRVPShowPrice = true;
                FRVPBoundaryColor = System.Windows.Media.Brushes.White; FRVPBoundaryOpacity = 30; FRVPBoundaryWidth = 1;
                FRVPDisplayFibs = true; FRVPFibLineWidth = 1;
                FRVPFibStyle = DashStyleHelper.Dot; FRVPFibOpacity = 80;
                FRVPExtendFibs = false; FRVPFibLabelSize = 10; FRVPFibShowPrice = true;
                FibLevel1 = 0; FibLevel1Color = System.Windows.Media.Brushes.Gray;
                FibLevel2 = 23.6; FibLevel2Color = System.Windows.Media.Brushes.DodgerBlue;
                FibLevel3 = 38.2; FibLevel3Color = System.Windows.Media.Brushes.DodgerBlue;
                FibLevel4 = 50; FibLevel4Color = System.Windows.Media.Brushes.Gold;
                FibLevel5 = 61.8; FibLevel5Color = System.Windows.Media.Brushes.Red;
                FibLevel6 = 78.6; FibLevel6Color = System.Windows.Media.Brushes.OrangeRed;
                FibLevel7 = 100; FibLevel7Color = System.Windows.Media.Brushes.Gray;
                FibLevel8 = -1; FibLevel8Color = System.Windows.Media.Brushes.Cyan;
                FibLevel9 = -1; FibLevel9Color = System.Windows.Media.Brushes.Magenta;
                FibLevel10 = -1; FibLevel10Color = System.Windows.Media.Brushes.LimeGreen;
                FRVPDisplayAVWAP = true; FRVPAVWAPColor = System.Windows.Media.Brushes.DodgerBlue;
                FRVPAVWAPWidth = 2; FRVPAVWAPStyle = DashStyleHelper.Solid;
                FRVPAVWAPOpacity = 100; FRVPExtendAVWAP = true; FRVPShowAVWAPLabel = true;

                // Cluster Levels
                FRVPDisplayClusters = false; FRVPClusterCount = 5; FRVPClusterIterations = 50;
                FRVPClusterRowsPerLevel = 20; FRVPClusterLineWidth = 2;
                FRVPClusterLineStyle = DashStyleHelper.Dash; FRVPClusterOpacity = 80;
                FRVPExtendClusters = false; FRVPShowClusterLabels = true;
                FRVPCluster1Color = System.Windows.Media.Brushes.DodgerBlue;
                FRVPCluster2Color = System.Windows.Media.Brushes.Tomato;
                FRVPCluster3Color = System.Windows.Media.Brushes.LimeGreen;
                FRVPCluster4Color = System.Windows.Media.Brushes.Orange;
                FRVPCluster5Color = System.Windows.Media.Brushes.MediumPurple;

                // Strong/Weak Levels
                ShowStrongWeakLevels = true;
                StrongLevelVolumeMultiplier = 1.5;
                StrongLevelMinScore = 15;
                StrongHighColor = System.Windows.Media.Brushes.OrangeRed;
                StrongLowColor = System.Windows.Media.Brushes.DodgerBlue;
                StrongLevelWidth = 1;
                StrongLevelStyle = DashStyleHelper.Dash;
                StrongLevelFontSize = 8; StrongLevelOpacity = 80;
                StrongLevelMitigation = "Remove";

                // Order Blocks
                OBSwingLength = 10; MaxATRMultiplier = 3.5;
                ShowHistoricZones = true;
                ZoneInvalidation = "Wick"; ZoneCount = "Low";
                OBDrawStyle = "Full Range";
                BoxExtendBars = 15; DeleteBrokenBoxes = true;
                ConvertToBreaker = true;
                BullOBColor = System.Windows.Media.Brushes.SeaGreen; BullOBOpacity = 50;
                BearOBColor = System.Windows.Media.Brushes.Crimson; BearOBOpacity = 50;
                BullBreakerColor = System.Windows.Media.Brushes.Crimson; BullBreakerOpacity = 35;
                BearBreakerColor = System.Windows.Media.Brushes.SeaGreen; BearBreakerOpacity = 35;
                OBBorderOpacity = 100;
                SwingLabelColor = System.Windows.Media.Brushes.Silver;
                

                // Alerts
                AlertOnBOS = false; AlertOnCHoCH = true;
                AlertSoundBOS = "Alert2.wav"; AlertSoundCHoCH = "Alert1.wav";
                AlertOnOBCreation = true; AlertOnOBTouch = true;
                AlertOnAVWAPTouch = true; AlertOnFibTouch = true;
                AlertSoundOB = "Alert3.wav"; AlertSoundAVWAP = "Alert4.wav"; AlertSoundFib = "Alert4.wav";

                // Voice Alerts
                EnableVoiceAlerts = true;
                VoiceAlertRate = 2;
                AlertCooldownSeconds = 30;
                AlertFallbackSound = "Alert1.wav";

                // Displacement Candles
                ShowDisplacement = false; DisplacementATRMultiplier = 1.5;
                DisplacementMinBodyPct = 60;
                BullDisplacementColor = System.Windows.Media.Brushes.Lime;
                BearDisplacementColor = System.Windows.Media.Brushes.Magenta;
                DisplacementMarkerOpacity = 60;

                // Equal Highs / Lows
                ShowEqualLevels = true; EqualLevelTolerance = 3;
                EqualHighColor = System.Windows.Media.Brushes.OrangeRed;
                EqualLowColor = System.Windows.Media.Brushes.DodgerBlue;
                EqualLevelWidth = 2; EqualLevelStyle = DashStyleHelper.Dot;
                EqualLevelOpacity = 70; EqualLevelFontSize = 8;

                // Liquidity Sweeps
                ShowLiquiditySweeps = false;
                SweepMinDepthTicks = 2;
                SweepMinWickPct = 40;
                SweepVolumeFilter = false;
                SweepVolumeMultiplier = 1.2;
                SweepMitigatedLevels = false;
                SweepOrderBlocks = true;
                BullSweepColor = System.Windows.Media.Brushes.DodgerBlue;
                BearSweepColor = System.Windows.Media.Brushes.OrangeRed;
                SweepOpacity = 40;
                AlertOnSweep = true;
                AlertSoundSweep = "Alert3.wav";
            }
            else if (State == State.DataLoaded)
            {
                _prevHigh = double.MinValue; _prevLow = double.MaxValue;
                _prevHighIndex = 0; _prevLowIndex = 0;
                _highActive = false; _lowActive = false;
                _prevBreakoutDir = 0; _prevSwingType = 0; _msTagCounter = 0;
                _trendState = 0; _pendingChoch = 0;
                _legOriginHigh = double.MinValue; _legOriginHighIndex = 0;
                _legOriginLow = double.MaxValue; _legOriginLowIndex = 0;
                _strongLevels = new List<StrongLevel>();
                _lastSessionDate = DateTime.MinValue;
                _equalLevels = new List<StrongLevel>(); _eqTagCounter = 0;
                _swingHighHistory = new List<KeyValuePair<int, double>>();
                _swingLowHistory = new List<KeyValuePair<int, double>>();
                _sweepTagCounter = 0;
                _sweepRenders = new List<SweepRenderInfo>();
                _activeFrvp = null; _historicFrvps = new List<FRVPZone>();
                _bullOBList = new List<OBInfo>(); _bearOBList = new List<OBInfo>();
                _obSwingTop = new OBSwing { Y = double.MinValue };
                _obSwingBottom = new OBSwing { Y = double.MaxValue };
                _obSwingType = 0; _obTagCounter = 0; _atrValue = 0;
                _brushesCached = false;
                _swingFont = new SimpleFont("Arial", SwingLabelFontSize);
                _bosFont = new SimpleFont("Arial", BOSLabelFontSize);
                _strongFont = new SimpleFont("Arial", StrongLevelFontSize);
                _eqFont = new SimpleFont("Arial", EqualLevelFontSize);
                _lastFrvpBarCount = -1; _cachedFibLevels = null;

                // Initialize voice alert system
                lastAlertTime = new Dictionary<string, DateTime>();
                lastTouchState = new Dictionary<string, bool>();
                _obTouchAlerted = new HashSet<string>();

                // Initialize SharpDX render data lists
                _bosRenders = new List<BOSRenderInfo>();
                _swingLabelRenders = new List<SwingLabelInfo>();
                _halfRetraceRenders = new List<HalfRetraceInfo>();
                _displacementRenders = new List<DisplacementInfo>();

                if (EnableVoiceAlerts)
                {
                    try
                    {
                        instrumentName = Instrument != null ? Instrument.MasterInstrument.Name : "Unknown";
                        GenerateVoiceAlerts();
                    }
                    catch (Exception ex)
                    {
                        Print("RedTail MS: Voice alert generation error: " + ex.Message);
                    }
                }
                else
                {
                    instrumentName = Instrument != null ? Instrument.MasterInstrument.Name : "Unknown";
                }

                // Also clear historic FRVPs on reload to prevent stale references
                if (_historicFrvps != null) _historicFrvps.Clear();

                // Attempt registration now — Bars may already be valid at DataLoaded
                TryRegister();
            }
            else if (State == State.Historical)
            {
                // Re-register at Historical — Bars.BarsPeriod is guaranteed valid here
                TryRegister();
            }
            else if (State == State.Realtime)
            {
                // Re-register at Realtime as a final safety net
                TryRegister();
            }
            else if (State == State.Terminated)
            {
                // Deregister so the Companion never holds a stale reference
                if (Instrument != null)
                {
                    BarsPeriod bp = (Bars != null) ? Bars.BarsPeriod : new BarsPeriod();
                    string regKey = BuildRegistryKey(Instrument, bp);
                    if (!string.IsNullOrEmpty(regKey))
                    {
                        RedTailMarketStructureV2 removed;
                        Registry.TryRemove(regKey, out removed);
                    }
                }
            }
        }

        protected override void OnBarUpdate()
        {
            int minBars = Math.Max(SwingLength * 2 + 1, OBSwingLength * 2 + 1);
            if (CurrentBar < minBars) return;

            // All heavy structure/rendering logic is bar-close only
            if (!IsFirstTickOfBar) return;

            if (!_brushesCached) CacheBrushes();
            ProcessMarketStructure();
            ManageStrongLevels();

            // Active FRVP only needs recalc when a new bar forms
            if (_activeFrvp != null && _activeFrvp.IsActive && CurrentBar != _lastFrvpBarCount)
            {
                _activeFrvp.Dirty = true;
                _lastFrvpBarCount = CurrentBar;
            }

            UpdateATR(); FindOBSwings(); ProcessOrderBlocks(); RenderAllOBs();
            ProcessDisplacementCandles();
            ProcessEqualLevels();
            ProcessLiquiditySweeps();
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last) return;
            CheckLevelAlerts();
        }

        #region Level Touch Alerts

        private void CheckLevelAlerts()
        {
            if (State != State.Realtime) return;
            if (!AlertOnOBTouch && !AlertOnAVWAPTouch && !AlertOnFibTouch) return;

            double closePrice = Close[0];
            double highPrice = High[0];
            double lowPrice = Low[0];

            // ── OB Touch Alerts ──
            if (AlertOnOBTouch)
            {
                foreach (var ob in _bullOBList)
                {
                    if (ob.Disabled || ob.Breaker) continue;
                    bool isTouching = lowPrice <= ob.Top && highPrice >= ob.Bottom;
                    string touchKey = "OBTouch_" + ob.Tag;
                    if (isTouching && !_obTouchAlerted.Contains(touchKey))
                    {
                        if (CanAlert(touchKey))
                        {
                            string msg = instrumentName + " touching Bullish OB at " + ob.Bottom.ToString("F2") + " - " + ob.Top.ToString("F2");
                            Alert("RT_OBTouch_" + ob.Tag + "_" + CurrentBar, Priority.Medium, msg,
                                GetVoiceAlertPath("BullOBTouch", AlertSoundOB), 10,
                                System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.SeaGreen);
                            RecordAlert(touchKey);
                            _obTouchAlerted.Add(touchKey);
                        }
                    }
                }
                foreach (var ob in _bearOBList)
                {
                    if (ob.Disabled || ob.Breaker) continue;
                    bool isTouching = lowPrice <= ob.Top && highPrice >= ob.Bottom;
                    string touchKey = "OBTouch_" + ob.Tag;
                    if (isTouching && !_obTouchAlerted.Contains(touchKey))
                    {
                        if (CanAlert(touchKey))
                        {
                            string msg = instrumentName + " touching Bearish OB at " + ob.Bottom.ToString("F2") + " - " + ob.Top.ToString("F2");
                            Alert("RT_OBTouch_" + ob.Tag + "_" + CurrentBar, Priority.Medium, msg,
                                GetVoiceAlertPath("BearOBTouch", AlertSoundOB), 10,
                                System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Crimson);
                            RecordAlert(touchKey);
                            _obTouchAlerted.Add(touchKey);
                        }
                    }
                }
            }

            // ── AVWAP Touch Alerts ──
            if (AlertOnAVWAPTouch && FRVPDisplayAVWAP)
            {
                // Check active FRVP zone AVWAP
                CheckAvwapTouch(_activeFrvp, "ActiveAVWAP", closePrice, highPrice, lowPrice);
                // Check historic FRVP zone AVWAPs
                if (_historicFrvps != null)
                {
                    for (int i = 0; i < _historicFrvps.Count; i++)
                        CheckAvwapTouch(_historicFrvps[i], "HistAVWAP_" + i, closePrice, highPrice, lowPrice);
                }
            }

            // ── Fib Touch Alerts ──
            if (AlertOnFibTouch && FRVPDisplayFibs)
            {
                CheckFibTouch(_activeFrvp, "ActiveFib", closePrice, highPrice, lowPrice);
                if (_historicFrvps != null)
                {
                    for (int i = 0; i < _historicFrvps.Count; i++)
                        CheckFibTouch(_historicFrvps[i], "HistFib_" + i, closePrice, highPrice, lowPrice);
                }
            }
        }

        private void CheckAvwapTouch(FRVPZone zone, string key, double close, double high, double low)
        {
            if (zone == null || zone.AvwapPoints == null || zone.AvwapPoints.Count == 0) return;

            // Get the most recent AVWAP value
            double avwapValue = zone.AvwapPoints[zone.AvwapPoints.Count - 1].Value;
            if (avwapValue <= 0) return;

            bool isTouching = low <= avwapValue && high >= avwapValue;
            string touchKey = key + "_Touch";

            if (!lastTouchState.ContainsKey(touchKey)) lastTouchState[touchKey] = false;

            if (isTouching && !lastTouchState[touchKey])
            {
                if (CanAlert(touchKey))
                {
                    string msg = instrumentName + " touching AVWAP at " + avwapValue.ToString("F2");
                    Alert("RT_AVWAP_" + key + "_" + CurrentBar, Priority.Medium, msg,
                        GetVoiceAlertPath("AVWAPTouch", AlertSoundAVWAP), 10,
                        System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.DodgerBlue);
                    RecordAlert(touchKey);
                }
            }
            lastTouchState[touchKey] = isTouching;
        }

        private void CheckFibTouch(FRVPZone zone, string key, double close, double high, double low)
        {
            if (zone == null) return;
            double fibRange = zone.StartPrice - zone.EndPrice;
            if (Math.Abs(fibRange) < double.Epsilon) return;

            if (_cachedFibLevels == null) _cachedFibLevels = GetFibLevels();

            double tolerance = _atrValue * 0.05; // tight tolerance for fib touch
            if (tolerance <= 0) tolerance = TickSize * 2;

            foreach (var lv in _cachedFibLevels)
            {
                double fibPrice = zone.EndPrice + fibRange * lv.Ratio;
                string fibPct = (lv.Ratio * 100).ToString("F1");
                string touchKey = key + "_Fib_" + fibPct + "_Touch";

                bool isTouching = low <= fibPrice + tolerance && high >= fibPrice - tolerance;

                if (!lastTouchState.ContainsKey(touchKey)) lastTouchState[touchKey] = false;

                if (isTouching && !lastTouchState[touchKey])
                {
                    if (CanAlert(touchKey))
                    {
                        string msg = instrumentName + " touching " + fibPct + "% fib at " + fibPrice.ToString("F2");
                        Alert("RT_Fib_" + key + "_" + fibPct + "_" + CurrentBar, Priority.Low, msg,
                            GetVoiceAlertPath("FibTouch_" + fibPct, AlertSoundFib), 10,
                            System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Gold);
                        RecordAlert(touchKey);
                    }
                }
                lastTouchState[touchKey] = isTouching;
            }
        }

        #endregion

        #region Voice Alert Methods

        private bool CanAlert(string key)
        {
            if (lastAlertTime == null) return true;
            if (!lastAlertTime.ContainsKey(key)) return true;
            return (DateTime.Now - lastAlertTime[key]).TotalSeconds >= AlertCooldownSeconds;
        }

        private void RecordAlert(string key)
        {
            if (lastAlertTime == null) lastAlertTime = new Dictionary<string, DateTime>();
            lastAlertTime[key] = DateTime.Now;
        }

        private void GenerateVoiceAlerts()
        {
            string soundDir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "sounds");
            if (!Directory.Exists(soundDir))
                Directory.CreateDirectory(soundDir);

            // Define all alert phrases
            var alerts = new Dictionary<string, string>
            {
                // CHoCH & BOS
                { "BullCHoCH",       instrumentName + " Bullish change of character" },
                { "BearCHoCH",       instrumentName + " Bearish change of character" },
                { "BullBOS",         instrumentName + " Bullish break of structure" },
                { "BearBOS",         instrumentName + " Bearish break of structure" },
                // Order Block creation
                { "BullOBCreated",   instrumentName + " Bullish order block formed" },
                { "BearOBCreated",   instrumentName + " Bearish order block formed" },
                // Order Block touches
                { "BullOBTouch",     instrumentName + " touching bullish order block" },
                { "BearOBTouch",     instrumentName + " touching bearish order block" },
                // AVWAP
                { "AVWAPTouch",      instrumentName + " touching the anchored vee-wop" },
            };

            // Fib level alerts
            double[] fibVals = { FibLevel1, FibLevel2, FibLevel3, FibLevel4, FibLevel5, FibLevel6, FibLevel7, FibLevel8, FibLevel9, FibLevel10 };
            foreach (double fv in fibVals)
            {
                if (fv >= 0)
                {
                    string pct = fv.ToString("F1");
                    string key = "FibTouch_" + pct;
                    if (!alerts.ContainsKey(key))
                        alerts[key] = instrumentName + " touching the " + pct + " percent fib level";
                }
            }

            int totalAlerts = alerts.Count;

            // Use a marker file to track voice settings
            string markerPath = Path.Combine(soundDir, "RTMS_" + instrumentName + "_voicesettings.txt");
            string currentSettings = "rate=" + VoiceAlertRate + "|neural=true|phonetic=v2";
            bool settingsChanged = true;

            if (File.Exists(markerPath))
            {
                try
                {
                    string savedSettings = File.ReadAllText(markerPath).Trim();
                    if (savedSettings == currentSettings)
                        settingsChanged = false;
                }
                catch { }
            }

            // Check if all files exist
            bool allExist = true;
            foreach (var kvp in alerts)
            {
                string fileName = "RTMS_" + instrumentName + "_" + kvp.Key + ".wav";
                if (!File.Exists(Path.Combine(soundDir, fileName)))
                {
                    allExist = false;
                    break;
                }
            }

            if (settingsChanged || !allExist)
            {
                // Delete old files so they regenerate cleanly
                foreach (var kvp in alerts)
                {
                    string fileName = "RTMS_" + instrumentName + "_" + kvp.Key + ".wav";
                    string filePath = Path.Combine(soundDir, fileName);
                    try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                }

                Print("RedTail MS: Generating voice alerts for " + instrumentName + "...");

                // Try neural voices first via edge-tts
                bool neuralSuccess = TryGenerateNeuralVoiceAlerts(soundDir, alerts);

                if (!neuralSuccess)
                {
                    Print("RedTail MS: Neural voices not available, using SAPI5 with SSML.");
                    GenerateSAPIVoiceAlerts(soundDir, alerts);
                }

                // Save marker
                try { File.WriteAllText(markerPath, currentSettings); } catch { }
            }
            else
            {
                Print("RedTail MS: Voice alert files already cached for " + instrumentName + ".");
            }

            // Register all paths
            foreach (var kvp in alerts)
            {
                string fileName = "RTMS_" + instrumentName + "_" + kvp.Key + ".wav";
                string filePath = Path.Combine(soundDir, fileName);
                if (File.Exists(filePath))
                    voiceAlertPaths[kvp.Key] = filePath;
            }

            Print("RedTail MS: Voice alerts ready for " + instrumentName + " (" + voiceAlertPaths.Count + "/" + totalAlerts + " files)");
        }

        private bool TryGenerateNeuralVoiceAlerts(string soundDir, Dictionary<string, string> alerts)
        {
            try
            {
                Print("RedTail MS: Checking for edge-tts...");

                var checkPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "pip",
                    Arguments = "show edge-tts",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                bool edgeTtsInstalled = false;
                try
                {
                    using (var checkProc = System.Diagnostics.Process.Start(checkPsi))
                    {
                        string checkOut = checkProc.StandardOutput.ReadToEnd();
                        checkProc.WaitForExit(15000);
                        edgeTtsInstalled = checkOut.Contains("edge-tts");
                    }
                }
                catch { }

                if (!edgeTtsInstalled)
                {
                    Print("RedTail MS: Installing edge-tts via pip...");
                    var installPsi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "pip",
                        Arguments = "install edge-tts",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var installProc = System.Diagnostics.Process.Start(installPsi))
                    {
                        string installOut = installProc.StandardOutput.ReadToEnd();
                        string installErr = installProc.StandardError.ReadToEnd();
                        installProc.WaitForExit(60000);
                        Print("RedTail MS: pip install result: " + installOut);
                        if (!string.IsNullOrEmpty(installErr) && installErr.Contains("error"))
                        {
                            Print("RedTail MS: pip install error: " + installErr);
                            return false;
                        }
                    }
                }
                else
                {
                    Print("RedTail MS: edge-tts already installed.");
                }

                string voice = "en-US-JennyNeural";
                string rateStr = GetEdgeTtsRate();
                int successCount = 0;

                foreach (var kvp in alerts)
                {
                    string fileName = "RTMS_" + instrumentName + "_" + kvp.Key + ".wav";
                    string mp3Path = Path.Combine(soundDir, "RTMS_" + instrumentName + "_" + kvp.Key + ".mp3");
                    string wavPath = Path.Combine(soundDir, fileName);
                    string phrase = kvp.Value;

                    try
                    {
                        var ttsPsi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "edge-tts",
                            Arguments = "--voice " + voice + " --rate=" + rateStr + " --text \"" + phrase.Replace("\"", "'") + "\" --write-media \"" + mp3Path + "\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        using (var ttsProc = System.Diagnostics.Process.Start(ttsPsi))
                        {
                            string ttsOut = ttsProc.StandardOutput.ReadToEnd();
                            string ttsErr = ttsProc.StandardError.ReadToEnd();
                            ttsProc.WaitForExit(30000);

                            if (File.Exists(mp3Path) && new FileInfo(mp3Path).Length > 500)
                            {
                                ConvertMp3ToWav(mp3Path, wavPath);

                                if (File.Exists(wavPath) && new FileInfo(wavPath).Length > 1000)
                                {
                                    Print("RedTail MS Neural: OK: " + kvp.Key);
                                    successCount++;
                                }
                                else
                                {
                                    try { File.Copy(mp3Path, wavPath, true); } catch { }
                                    if (File.Exists(wavPath))
                                    {
                                        successCount++;
                                        Print("RedTail MS Neural: OK: " + kvp.Key + " (mp3 fallback)");
                                    }
                                }

                                try { File.Delete(mp3Path); } catch { }
                            }
                            else
                            {
                                Print("RedTail MS Neural: FAIL: " + kvp.Key + " - edge-tts did not produce output");
                                if (!string.IsNullOrEmpty(ttsErr))
                                    Print("RedTail MS Neural Err: " + ttsErr.Substring(0, Math.Min(ttsErr.Length, 200)));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Print("RedTail MS Neural: FAIL: " + kvp.Key + ": " + ex.Message);
                    }
                }

                if (successCount > 0)
                {
                    Print("RedTail MS: Neural voice generation complete (" + successCount + "/" + alerts.Count + " files using " + voice + ").");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Print("RedTail MS neural voice error: " + ex.Message);
                return false;
            }
        }

        private void ConvertMp3ToWav(string mp3Path, string wavPath)
        {
            try
            {
                string psScript = @"
Add-Type -AssemblyName PresentationCore
$mediaPlayer = New-Object System.Windows.Media.MediaPlayer
$mediaPlayer.Open([Uri]::new('" + mp3Path.Replace("\\", "\\\\").Replace("'", "''") + @"'))
Start-Sleep -Milliseconds 500
$mediaPlayer.Close()

$ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
if ($ffmpeg) {
    & ffmpeg -y -i '" + mp3Path.Replace("\\", "\\\\").Replace("'", "''") + @"' -acodec pcm_s16le -ar 22050 -ac 1 '" + wavPath.Replace("\\", "\\\\").Replace("'", "''") + @"' 2>&1 | Out-Null
    if (Test-Path '" + wavPath.Replace("\\", "\\\\").Replace("'", "''") + @"') {
        Write-Host 'CONVERTED_FFMPEG'
        exit 0
    }
}

Copy-Item '" + mp3Path.Replace("\\", "\\\\").Replace("'", "''") + @"' '" + wavPath.Replace("\\", "\\\\").Replace("'", "''") + @"'
Write-Host 'COPIED_MP3'
";
                string scriptPath = Path.Combine(Path.GetTempPath(), "rtms_convert.ps1");
                File.WriteAllText(scriptPath, psScript);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(15000);
                    if (output.Contains("CONVERTED_FFMPEG"))
                        Print("RedTail MS: Converted to WAV via ffmpeg");
                    else
                        Print("RedTail MS: Using mp3 directly (ffmpeg not found)");
                }

                try { File.Delete(scriptPath); } catch { }
            }
            catch (Exception ex)
            {
                Print("RedTail MS mp3->wav conversion error: " + ex.Message);
                try { File.Copy(mp3Path, wavPath, true); } catch { }
            }
        }

        private string GetEdgeTtsRate()
        {
            int pct = VoiceAlertRate * 10;
            if (pct >= 0)
                return "+" + pct + "%";
            else
                return pct + "%";
        }

        private void GenerateSAPIVoiceAlerts(string soundDir, Dictionary<string, string> alerts)
        {
            using (var synth = new SpeechSynthesizer())
            {
                Print("RedTail MS SAPI voices available:");
                foreach (var voice in synth.GetInstalledVoices())
                {
                    if (voice.Enabled)
                        Print("  - " + voice.VoiceInfo.Name + " (" + voice.VoiceInfo.Gender + ", " + voice.VoiceInfo.Culture + ")");
                }

                foreach (var voice in synth.GetInstalledVoices())
                {
                    if (voice.VoiceInfo.Gender == VoiceGender.Female && voice.Enabled)
                    {
                        synth.SelectVoice(voice.VoiceInfo.Name);
                        Print("RedTail MS: Selected SAPI voice: " + voice.VoiceInfo.Name);
                        break;
                    }
                }

                synth.Rate = Math.Max(-10, Math.Min(10, VoiceAlertRate));

                foreach (var kvp in alerts)
                {
                    string fileName = "RTMS_" + instrumentName + "_" + kvp.Key + ".wav";
                    string filePath = Path.Combine(soundDir, fileName);

                    if (File.Exists(filePath))
                        continue;

                    try
                    {
                        synth.SetOutputToWaveFile(filePath);
                        synth.SpeakSsml(BuildSSML(kvp.Value));
                        synth.SetOutputToNull();
                        Print("RedTail MS SAPI: Generated " + fileName);
                    }
                    catch (Exception ex)
                    {
                        Print("RedTail MS SAPI failed '" + kvp.Key + "': " + ex.Message);
                        continue;
                    }
                }
            }
        }

        private string BuildSSML(string phrase)
        {
            string[] words = phrase.Split(' ');
            string instrument = words[0];
            string action = string.Join(" ", words, 1, words.Length - 1);

            return "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>"
                + "<prosody rate='" + GetSSMLRate() + "' pitch='+5%'>"
                + "<emphasis level='moderate'>" + instrument + "</emphasis>"
                + "<break time='350ms'/>"
                + action
                + "</prosody></speak>";
        }

        private string GetSSMLRate()
        {
            if (VoiceAlertRate <= -5) return "x-slow";
            if (VoiceAlertRate <= -2) return "slow";
            if (VoiceAlertRate <= 2)  return "medium";
            if (VoiceAlertRate <= 5)  return "fast";
            return "x-fast";
        }

        private string GetVoiceAlertPath(string alertKey, string fallbackSoundFile)
        {
            if (EnableVoiceAlerts && voiceAlertPaths != null && voiceAlertPaths.ContainsKey(alertKey))
                return voiceAlertPaths[alertKey];

            return ResolveSoundPath(fallbackSoundFile);
        }

        private string ResolveSoundPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (Path.IsPathRooted(raw)) return raw;

            var install = Path.Combine(NinjaTrader.Core.Globals.InstallDir, "sounds", raw);
            if (File.Exists(install)) return install;

            var user = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "sounds", raw);
            if (File.Exists(user)) return user;

            return raw;
        }

        #endregion

        #region OnRender — FRVP Drawing

        public override void OnRenderTargetChanged()
        {
            // Dispose old stroke styles
            _ssSolid?.Dispose(); _ssSolid = null;
            _ssDash?.Dispose(); _ssDash = null;
            _ssDot?.Dispose(); _ssDot = null;
            _ssDashDot?.Dispose(); _ssDashDot = null;
            _cachedRT = null;
        }

        private void EnsureStrokeStyles(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (_cachedRT == rt && _ssSolid != null) return;
            _ssSolid?.Dispose(); _ssDash?.Dispose(); _ssDot?.Dispose(); _ssDashDot?.Dispose();

            _ssSolid = new SharpDX.Direct2D1.StrokeStyle(rt.Factory,
                new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Solid });
            _ssDash = new SharpDX.Direct2D1.StrokeStyle(rt.Factory,
                new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Custom, DashCap = SharpDX.Direct2D1.CapStyle.Round, StartCap = SharpDX.Direct2D1.CapStyle.Round, EndCap = SharpDX.Direct2D1.CapStyle.Round },
                new[] { 4f, 3f });
            _ssDot = new SharpDX.Direct2D1.StrokeStyle(rt.Factory,
                new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Custom, DashCap = SharpDX.Direct2D1.CapStyle.Round, StartCap = SharpDX.Direct2D1.CapStyle.Round, EndCap = SharpDX.Direct2D1.CapStyle.Round },
                new[] { 0.5f, 2f });
            _ssDashDot = new SharpDX.Direct2D1.StrokeStyle(rt.Factory,
                new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Custom, DashCap = SharpDX.Direct2D1.CapStyle.Round, StartCap = SharpDX.Direct2D1.CapStyle.Round, EndCap = SharpDX.Direct2D1.CapStyle.Round },
                new[] { 4f, 2f, 0.5f, 2f });
            _cachedRT = rt;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (chartControl == null || chartScale == null) return;
            var rt = RenderTarget; if (rt == null) return;
            EnsureStrokeStyles(rt);
            var cp = chartControl.ChartPanels[chartScale.PanelIndex]; if (cp == null) return;
            if (chartControl.BarsArray == null || chartControl.BarsArray.Count == 0) return;
            var chartBars = chartControl.BarsArray[0];

            // Render OB zone fills + borders via SharpDX (no click capture)
            RenderOBFills(rt, chartControl, chartScale, chartBars);
            RenderOBBorders(rt, chartControl, chartScale, chartBars);

            // Render sweep rectangles via SharpDX (no click capture)
            RenderSweepRects(rt, chartControl, chartScale, chartBars);

            // Render strong levels via SharpDX
            RenderStrongLevels(rt, chartControl, chartScale, chartBars);

            // Render equal levels via SharpDX
            RenderEqualLevels(rt, chartControl, chartScale, chartBars);

            // Render BOS/CHoCH lines + labels via SharpDX
            RenderBOSLines(rt, chartControl, chartScale, chartBars);

            // Render swing labels via SharpDX
            RenderSwingLabels(rt, chartControl, chartScale, chartBars);

            // Render half-retracement lines via SharpDX
            RenderHalfRetrace(rt, chartControl, chartScale, chartBars);

            // Render displacement markers via SharpDX
            RenderDisplacement(rt, chartControl, chartScale, chartBars);

            // Render Trend Display
            if (ShowTrendDisplay)
                RenderTrendDisplay(rt, cp);

            // Render FRVP
            if (EnableFRVP)
            {
                if (KeepPreviousFRVP)
                    foreach (var z in _historicFrvps) RenderFRVPZone(z, rt, chartControl, chartScale, cp, chartBars);

                if (_activeFrvp != null && _activeFrvp.IsActive)
                    RenderFRVPZone(_activeFrvp, rt, chartControl, chartScale, cp, chartBars);
            }
        }

        private void RenderBOSLines(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc, ChartScale cs, ChartBars chartBars)
        {
            if (_bosRenders == null || _bosRenders.Count == 0) return;

            var bosC4 = B2C4(BOSColor, BOSOpacity / 100f);
            var bosStroke = MakeSS(rt, GetDS(BOSStyle));

            using (var lineBr = new SharpDX.Direct2D1.SolidColorBrush(rt, bosC4))
            using (var fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
                SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, BOSLabelFontSize))
            {
                // Only render BOS lines whose start or end bar is within the visible range
                int firstVisBar = chartBars.FromIndex;
                int lastVisBar = chartBars.ToIndex;

                for (int i = 0; i < _bosRenders.Count; i++)
                {
                    var bos = _bosRenders[i];
                    // Skip if entirely off-screen
                    if (bos.StartBar > lastVisBar && bos.EndBar > lastVisBar) continue;
                    if (bos.StartBar < firstVisBar && bos.EndBar < firstVisBar) continue;

                    float xLeft, xRight;
                    try
                    {
                        xLeft = cc.GetXByBarIndex(chartBars, bos.StartBar);
                        xRight = cc.GetXByBarIndex(chartBars, bos.EndBar);
                    }
                    catch { continue; }

                    float y = cs.GetYByValue(bos.Price);
                    rt.DrawLine(new SharpDX.Vector2(xLeft, y), new SharpDX.Vector2(xRight, y), lineBr, BOSWidth, bosStroke);

                    // Label at midpoint
                    try
                    {
                        string label = bos.IsCHoCH ? "CHoCH" : "BOS";
                        float mx = (xLeft + xRight) / 2f;
                        float ly = bos.IsBull ? y - BOSLabelFontSize - 4 : y + 2;
                        using (var tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, label, fmt, 100, 20))
                        {
                            float lx = mx - tl.Metrics.Width / 2;
                            rt.DrawTextLayout(new SharpDX.Vector2(lx, ly), tl, lineBr);
                        }
                    }
                    catch { }
                }
            }
        }

        private void RenderSwingLabels(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc, ChartScale cs, ChartBars chartBars)
        {
            if (!ShowSwingLabels || _swingLabelRenders == null || _swingLabelRenders.Count == 0) return;

            var labelC4 = B2C4(SwingLabelColor, 1.0f);
            int firstVisBar = chartBars.FromIndex;
            int lastVisBar = chartBars.ToIndex;

            using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, labelC4))
            using (var fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
                SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, SwingLabelFontSize))
            {
                for (int i = 0; i < _swingLabelRenders.Count; i++)
                {
                    var sl = _swingLabelRenders[i];
                    if (sl.BarIndex < firstVisBar || sl.BarIndex > lastVisBar) continue;

                    float x;
                    try { x = cc.GetXByBarIndex(chartBars, sl.BarIndex); } catch { continue; }
                    float y = cs.GetYByValue(sl.Price);

                    try
                    {
                        using (var tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, sl.Label, fmt, 100, 20))
                        {
                            float lx = x - tl.Metrics.Width / 2;
                            float ly = sl.IsHigh ? y - tl.Metrics.Height : y;
                            rt.DrawTextLayout(new SharpDX.Vector2(lx, ly), tl, br);
                        }
                    }
                    catch { }
                }
            }
        }

        private void RenderHalfRetrace(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc, ChartScale cs, ChartBars chartBars)
        {
            if (!ShowHalfRetracement || _halfRetraceRenders == null || _halfRetraceRenders.Count == 0) return;

            var hrC4 = B2C4(HalfRetracementColor, HalfRetracementOpacity / 100f);
            var hrStroke = MakeSS(rt, GetDS(HalfRetracementStyle));
            int firstVisBar = chartBars.FromIndex;
            int lastVisBar = chartBars.ToIndex;

            using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, hrC4))
            {
                for (int i = 0; i < _halfRetraceRenders.Count; i++)
                {
                    var hr = _halfRetraceRenders[i];
                    if (hr.StartBar > lastVisBar && hr.EndBar > lastVisBar) continue;
                    if (hr.StartBar < firstVisBar && hr.EndBar < firstVisBar) continue;

                    float xLeft, xRight;
                    try
                    {
                        xLeft = cc.GetXByBarIndex(chartBars, hr.StartBar);
                        xRight = cc.GetXByBarIndex(chartBars, hr.EndBar);
                    }
                    catch { continue; }

                    float y = cs.GetYByValue(hr.Price);
                    rt.DrawLine(new SharpDX.Vector2(xLeft, y), new SharpDX.Vector2(xRight, y), br, HalfRetracementWidth, hrStroke);
                }
            }
        }

        private void RenderDisplacement(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc, ChartScale cs, ChartBars chartBars)
        {
            if (!ShowDisplacement || _displacementRenders == null || _displacementRenders.Count == 0) return;

            int firstVisBar = chartBars.FromIndex;
            int lastVisBar = chartBars.ToIndex;

            var bullC4 = B2C4(BullDisplacementColor, DisplacementMarkerOpacity / 100f);
            var bearC4 = B2C4(BearDisplacementColor, DisplacementMarkerOpacity / 100f);

            using (var bullBr = new SharpDX.Direct2D1.SolidColorBrush(rt, bullC4))
            using (var bearBr = new SharpDX.Direct2D1.SolidColorBrush(rt, bearC4))
            {
                float halfSize = 4f;
                for (int i = 0; i < _displacementRenders.Count; i++)
                {
                    var d = _displacementRenders[i];
                    if (d.BarIndex < firstVisBar || d.BarIndex > lastVisBar) continue;

                    float x;
                    try { x = cc.GetXByBarIndex(chartBars, d.BarIndex); } catch { continue; }
                    float y = cs.GetYByValue(d.Price);

                    // Draw diamond shape
                    var brush = d.IsBull ? bullBr : bearBr;
                    using (var path = new SharpDX.Direct2D1.PathGeometry(rt.Factory))
                    {
                        using (var sink = path.Open())
                        {
                            sink.BeginFigure(new SharpDX.Vector2(x, y - halfSize), SharpDX.Direct2D1.FigureBegin.Filled);
                            sink.AddLine(new SharpDX.Vector2(x + halfSize, y));
                            sink.AddLine(new SharpDX.Vector2(x, y + halfSize));
                            sink.AddLine(new SharpDX.Vector2(x - halfSize, y));
                            sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                            sink.Close();
                        }
                        rt.FillGeometry(path, brush);
                    }
                }
            }
        }

        private void RenderOBBorders(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc, ChartScale cs, ChartBars chartBars)
        {
            if (_bullOBList == null || _bearOBList == null) return;

            int mx = GetMaxOB();
            float borderOp = OBBorderOpacity / 100f;

            foreach (var obList in new[] { _bullOBList, _bearOBList })
            {
                int count = 0;
                foreach (var ob in obList)
                {
                    if (ob.Disabled || ob.Tag == null) continue;
                    if (!ShowHistoricZones && ob.Breaker) continue;
                    if (count >= mx) break;
                    count++;

                    bool ib = (ob.OBType == "Bull");
                    int sa = CurrentBar - ob.StartBar;
                    if (sa < 0 || sa > CurrentBar) continue;
                    double mp = (ob.Top + ob.Bottom) / 2.0;

                    float yTop = cs.GetYByValue(ob.Top);
                    float yBot = cs.GetYByValue(ob.Bottom);
                    float yMid = cs.GetYByValue(mp);

                    if (ob.Breaker && ConvertToBreaker && ob.BreakBar > 0)
                    {
                        float xLeft, xBreak, xRight;
                        try
                        {
                            xLeft = cc.GetXByBarIndex(chartBars, ob.StartBar);
                            xBreak = cc.GetXByBarIndex(chartBars, ob.BreakBar);
                            xRight = cc.GetXByBarIndex(chartBars, CurrentBar + BoxExtendBars);
                        }
                        catch { continue; }

                        // OB segment (solid)
                        var obC4 = B2C4(ib ? BullOBColor : BearOBColor, borderOp);
                        using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, obC4))
                        {
                            rt.DrawLine(new SharpDX.Vector2(xLeft, yTop), new SharpDX.Vector2(xBreak, yTop), br, 1, _ssSolid);
                            rt.DrawLine(new SharpDX.Vector2(xLeft, yBot), new SharpDX.Vector2(xBreak, yBot), br, 1, _ssSolid);
                            rt.DrawLine(new SharpDX.Vector2(xLeft, yMid), new SharpDX.Vector2(xBreak, yMid), br, 1, _ssDot);
                        }

                        // Breaker segment (dashed)
                        var brkC4 = B2C4(ib ? BullBreakerColor : BearBreakerColor, borderOp);
                        using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, brkC4))
                        {
                            rt.DrawLine(new SharpDX.Vector2(xBreak, yTop), new SharpDX.Vector2(xRight, yTop), br, 1, _ssDash);
                            rt.DrawLine(new SharpDX.Vector2(xBreak, yBot), new SharpDX.Vector2(xRight, yBot), br, 1, _ssDash);
                            rt.DrawLine(new SharpDX.Vector2(xBreak, yMid), new SharpDX.Vector2(xRight, yMid), br, 1, _ssDot);
                        }
                    }
                    else
                    {
                        float xLeft, xRight;
                        try
                        {
                            xLeft = cc.GetXByBarIndex(chartBars, ob.StartBar);
                            xRight = cc.GetXByBarIndex(chartBars, CurrentBar + BoxExtendBars);
                        }
                        catch { continue; }

                        var lineC4 = B2C4(ib ? BullOBColor : BearOBColor, borderOp);
                        var lineStroke = ob.Breaker ? _ssDash : _ssSolid;
                        using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, lineC4))
                        {
                            rt.DrawLine(new SharpDX.Vector2(xLeft, yTop), new SharpDX.Vector2(xRight, yTop), br, 1, lineStroke);
                            rt.DrawLine(new SharpDX.Vector2(xLeft, yBot), new SharpDX.Vector2(xRight, yBot), br, 1, lineStroke);
                            rt.DrawLine(new SharpDX.Vector2(xLeft, yMid), new SharpDX.Vector2(xRight, yMid), br, 1, _ssDot);
                        }
                    }
                }
            }
        }

        private void RenderOBFills(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc, ChartScale cs, ChartBars chartBars)
        {
            if (_bullOBList == null || _bearOBList == null) return;

            foreach (var obList in new[] { _bullOBList, _bearOBList })
            {
                foreach (var ob in obList)
                {
                    if (ob.Disabled || ob.Tag == null) continue;
                    bool ib = (ob.OBType == "Bull");
                    int sa = CurrentBar - ob.StartBar;
                    if (sa < 0 || sa > CurrentBar) continue;

                    float yTop = cs.GetYByValue(ob.Top);
                    float yBot = cs.GetYByValue(ob.Bottom);

                    if (ob.Breaker && ConvertToBreaker && ob.BreakBar > 0)
                    {
                        // Split fill: OB portion (start -> break) + Breaker portion (break -> extend)
                        float xLeft, xBreak, xRight;
                        try
                        {
                            xLeft = cc.GetXByBarIndex(chartBars, ob.StartBar);
                            xBreak = cc.GetXByBarIndex(chartBars, ob.BreakBar);
                            xRight = cc.GetXByBarIndex(chartBars, CurrentBar + BoxExtendBars);
                        }
                        catch { continue; }

                        // OB segment fill (original color)
                        if (xBreak > xLeft)
                        {
                            int obOp = ib ? BullOBOpacity : BearOBOpacity;
                            var obFill = ib ? _bullFill : _bearFill;
                            var obC4 = B2C4(obFill, obOp / 100f);
                            using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, obC4))
                                rt.FillRectangle(new SharpDX.RectangleF(xLeft, yTop, xBreak - xLeft, yBot - yTop), br);
                        }

                        // Breaker segment fill (breaker color)
                        if (xRight > xBreak)
                        {
                            int brkOp = ib ? BullBreakerOpacity : BearBreakerOpacity;
                            var brkFill = ib ? _bullBreakerFill : _bearBreakerFill;
                            var brkC4 = B2C4(brkFill, brkOp / 100f);
                            using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, brkC4))
                                rt.FillRectangle(new SharpDX.RectangleF(xBreak, yTop, xRight - xBreak, yBot - yTop), br);
                        }
                    }
                    else
                    {
                        // Standard single fill
                        int ea = (ob.Breaker && ob.BreakBar > 0) ? CurrentBar - ob.BreakBar : -BoxExtendBars;
                        float xLeft, xRight;
                        try
                        {
                            xLeft = cc.GetXByBarIndex(chartBars, CurrentBar - sa);
                            xRight = ea < 0 ? cc.GetXByBarIndex(chartBars, CurrentBar - ea) : cc.GetXByBarIndex(chartBars, CurrentBar - ea);
                        }
                        catch { continue; }
                        if (xRight < xLeft) { float t = xLeft; xLeft = xRight; xRight = t; }

                        int op = ib ? BullOBOpacity : BearOBOpacity;
                        var fillBrush = ib ? (ob.Breaker ? _bullBreakerFill : _bullFill) : (ob.Breaker ? _bearBreakerFill : _bearFill);
                        var c4 = B2C4(fillBrush, op / 100f);
                        using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, c4))
                            rt.FillRectangle(new SharpDX.RectangleF(xLeft, yTop, xRight - xLeft, yBot - yTop), br);
                    }
                }
            }
        }

        private struct SweepRenderInfo
        {
            public int BarIndex;
            public double High, Low;
            public bool IsBullSweep;
        }

        private List<SweepRenderInfo> _sweepRenders = new List<SweepRenderInfo>();

        private void RenderSweepRects(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc, ChartScale cs, ChartBars chartBars)
        {
            if (_sweepRenders == null || _sweepRenders.Count == 0) return;
            foreach (var sw in _sweepRenders)
            {
                try
                {
                    float x1 = cc.GetXByBarIndex(chartBars, sw.BarIndex - 1);
                    float x2 = cc.GetXByBarIndex(chartBars, sw.BarIndex);
                    float yTop = cs.GetYByValue(sw.High);
                    float yBot = cs.GetYByValue(sw.Low);
                    var c4 = B2C4(sw.IsBullSweep ? BullSweepColor : BearSweepColor, SweepOpacity / 100f);
                    using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, c4))
                        rt.FillRectangle(new SharpDX.RectangleF(x1, yTop, x2 - x1, yBot - yTop), br);
                }
                catch { }
            }
        }

        private void RenderStrongLevels(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc, ChartScale cs, ChartBars chartBars)
        {
            if (!ShowStrongWeakLevels || _strongLevels == null || _strongLevels.Count == 0) return;

            int fadedOpacity = Math.Max((int)(StrongLevelOpacity * 0.65), 5);

            using (var fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
                SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, StrongLevelFontSize))
            {
                foreach (var sl in _strongLevels)
                {
                    int startBarsAgo = CurrentBar - sl.BarIndex;
                    if (startBarsAgo < 0 || startBarsAgo > CurrentBar) continue;

                    float xLeft;
                    try { xLeft = cc.GetXByBarIndex(chartBars, sl.BarIndex); } catch { continue; }
                    float xRight = (float)cc.ChartPanels[cs.PanelIndex].W;
                    float y = cs.GetYByValue(sl.Price);

                    int op = sl.Mitigated ? fadedOpacity : StrongLevelOpacity;
                    var color = sl.IsHigh ? B2C4(StrongHighColor, op / 100f) : B2C4(StrongLowColor, op / 100f);
                    string label = sl.IsHigh ? "Strong High" : "Strong Low";
                    if (sl.Mitigated) label += " (Mitigated)";

                    using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, color))
                    {
                        var ss = MakeSS(rt, StrongLevelStyle);
                        rt.DrawLine(new SharpDX.Vector2(xLeft, y), new SharpDX.Vector2(xRight, y), br, StrongLevelWidth, ss);

                        try
                        {
                            using (var tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, label, fmt, 200, 20))
                            {
                                float lx = xRight - tl.Metrics.Width - 5;
                                float ly = sl.IsHigh ? y - tl.Metrics.Height - 2 : y + 2;
                                rt.DrawTextLayout(new SharpDX.Vector2(lx, ly), tl, br);
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        private void RenderEqualLevels(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc, ChartScale cs, ChartBars chartBars)
        {
            if (!ShowEqualLevels || _equalLevels == null || _equalLevels.Count == 0) return;

            using (var fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
                SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, EqualLevelFontSize))
            {
                foreach (var el in _equalLevels)
                {
                    float xLeft, xRight;
                    try
                    {
                        xLeft = cc.GetXByBarIndex(chartBars, el.BarIndex);
                        xRight = cc.GetXByBarIndex(chartBars, el.BarIndex2);
                    }
                    catch { continue; }
                    if (xRight < xLeft) { float t = xLeft; xLeft = xRight; xRight = t; }

                    float y = cs.GetYByValue(el.Price);
                    var color = el.IsHigh ? B2C4(EqualHighColor, EqualLevelOpacity / 100f) : B2C4(EqualLowColor, EqualLevelOpacity / 100f);
                    string label = el.IsHigh ? "EQH" : "EQL";

                    using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, color))
                    {
                        var ss = MakeSS(rt, EqualLevelStyle);
                        rt.DrawLine(new SharpDX.Vector2(xLeft, y), new SharpDX.Vector2(xRight, y), br, EqualLevelWidth, ss);

                        try
                        {
                            float mx = (xLeft + xRight) / 2f;
                            using (var tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, label, fmt, 60, 20))
                            {
                                float lx = mx - tl.Metrics.Width / 2;
                                float ly = el.IsHigh ? y - tl.Metrics.Height - 2 : y + 2;
                                rt.DrawTextLayout(new SharpDX.Vector2(lx, ly), tl, br);
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        private void RenderTrendDisplay(SharpDX.Direct2D1.RenderTarget rt, ChartPanel cp)
        {
            string trendText;
            SharpDX.Color4 textColor;

            if (_trendState == 1)
            {
                if (_pendingChoch == -1)
                {
                    trendText = "Trend: Bullish  (Bearish CHoCH)";
                    textColor = WpfToColor4(TrendWarningColor);
                }
                else
                {
                    trendText = "Trend: Bullish";
                    textColor = WpfToColor4(TrendBullColor);
                }
            }
            else if (_trendState == -1)
            {
                if (_pendingChoch == 1)
                {
                    trendText = "Trend: Bearish  (Bullish CHoCH)";
                    textColor = WpfToColor4(TrendWarningColor);
                }
                else
                {
                    trendText = "Trend: Bearish";
                    textColor = WpfToColor4(TrendBearColor);
                }
            }
            else
            {
                if (_pendingChoch == 1)
                {
                    trendText = "Trend: —  (Bullish CHoCH)";
                    textColor = WpfToColor4(TrendBullColor);
                }
                else if (_pendingChoch == -1)
                {
                    trendText = "Trend: —  (Bearish CHoCH)";
                    textColor = WpfToColor4(TrendBearColor);
                }
                else
                {
                    trendText = "Trend: —";
                    textColor = WpfToColor4(TrendNeutralColor);
                }
            }

            using (var tf = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
                SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, TrendDisplayFontSize))
            using (var tl = new SharpDX.DirectWrite.TextLayout(
                NinjaTrader.Core.Globals.DirectWriteFactory, trendText, tf, 500, 50))
            using (var brush = new SharpDX.Direct2D1.SolidColorBrush(rt, textColor))
            {
                float x, y;
                bool leftSide = (TrendDisplayPosition == "Top Left");
                var metrics = tl.Metrics;

                if (leftSide)
                {
                    x = TrendDisplayOffsetX;
                    y = TrendDisplayOffsetY;
                }
                else
                {
                    x = cp.W - metrics.Width - TrendDisplayOffsetX;
                    y = TrendDisplayOffsetY;
                }

                rt.DrawTextLayout(new SharpDX.Vector2(x, y), tl, brush);
            }
        }

        private SharpDX.Color4 WpfToColor4(System.Windows.Media.Brush wpfBrush)
        {
            if (wpfBrush is System.Windows.Media.SolidColorBrush scb)
            {
                var c = scb.Color;
                return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
            }
            return new SharpDX.Color4(1f, 1f, 1f, 1f);
        }

        private void RenderFRVPZone(FRVPZone z, SharpDX.Direct2D1.RenderTarget rt, ChartControl cc, ChartScale cs, ChartPanel cp, ChartBars chartBars)
        {
            if (z == null || chartBars == null || chartBars.Bars == null) return;

            // Recalculate if dirty
            if (z.Dirty) CalcVolumeProfile(z, chartBars.Bars);

            if (z.Volumes == null || z.Volumes.Count == 0 || z.MaxVolume <= 0) return;

            // Get pixel coords
            float xLeft, xRight;
            bool pinnedToLeft = false;
            try
            {
                xLeft = cc.GetXByBarIndex(chartBars, z.StartBar);
                xRight = z.IsActive ? (float)cp.W : cc.GetXByBarIndex(chartBars, z.EndBar);
            }
            catch { return; }
            if (xRight < xLeft) { float t = xLeft; xLeft = xRight; xRight = t; }

            // Pin profile to left edge of panel when origin bar is scrolled off-screen
            if (xLeft < 0 && z.IsActive)
            {
                pinnedToLeft = true;
                xLeft = 0;
            }

            float rangeW = Math.Max(xRight - xLeft, 5f);
            float yTop = cs.GetYByValue(z.HighPrice);
            float yBot = cs.GetYByValue(z.LowPrice);
            float profilePx = rangeW * (FRVPProfileWidth / 100f);

            // 1. Boundary (outline only — fill removed to prevent chart interaction issues)
            float bOp = FRVPBoundaryOpacity / 100f;
            var bC4 = B2C4(FRVPBoundaryColor, bOp);
            using (var bBr = new SharpDX.Direct2D1.SolidColorBrush(rt, bC4))
            {
                if (FRVPBoundaryWidth > 0)
                    rt.DrawRectangle(new SharpDX.RectangleF(xLeft, yTop, rangeW, yBot - yTop), bBr, FRVPBoundaryWidth);
            }

            // 2. Volume Profile bars (adaptive, gradient, polarity)
            float barOp = FRVPBarOpacity / 100f;
            bool alignLeft = (FRVPVPAlignment == "Left");
            float pLeft = alignLeft ? xLeft : xRight - profilePx;
            float pRight = alignLeft ? xLeft + profilePx : xRight;

            // Adaptive rendering: smooth volumes and auto-size bars
            bool useAdaptive = FRVPRenderQuality == MSRenderQuality.Adaptive;
            double[] renderVolumes = useAdaptive && FRVPSmoothingPasses > 0
                ? GetSmoothedVolumes(z.Volumes, FRVPSmoothingPasses)
                : z.Volumes.ToArray();

            // Find max of (possibly smoothed) volumes for width scaling
            double renderMaxVol = 0;
            for (int i = 0; i < renderVolumes.Length; i++)
                if (renderVolumes[i] > renderMaxVol) renderMaxVol = renderVolumes[i];
            if (renderMaxVol <= 0) renderMaxVol = z.MaxVolume;

            // Calculate adaptive bar thickness
            float adaptiveThickness = useAdaptive
                ? CalcAdaptiveBarThickness(cs, z.ProfileLowest, z.ProfileLowest + z.ProfileInterval * (z.Volumes.Count - 1), z.Volumes.Count)
                : 0;

            for (int i = 0; i < renderVolumes.Length; i++)
            {
                double vol = renderVolumes[i]; if (vol <= 0) continue;
                double price = z.ProfileLowest + z.ProfileInterval * i;
                float y = cs.GetYByValue(price);
                float barW = (float)(vol / renderMaxVol * profilePx);
                bool isPoc = (i == z.PocIndex && FRVPDisplayPoC);
                bool isVA = (FRVPDisplayVA && i >= z.VaDownIndex && i <= z.VaUpIndex);

                // Determine source color based on volume type and polarity
                System.Windows.Media.Brush sourceColor;
                if (isPoc)
                    sourceColor = FRVPPoCColor;
                else if (isVA && FRVPVolumeType == MSVolumeType.Standard)
                    sourceColor = FRVPVABarColor;
                else if (FRVPVolumeType == MSVolumeType.Standard)
                    sourceColor = FRVPBarColor;
                else
                {
                    if (FRVPVolumeType == MSVolumeType.Bullish)
                        sourceColor = FRVPBullishBarColor;
                    else if (FRVPVolumeType == MSVolumeType.Bearish)
                        sourceColor = FRVPBearishBarColor;
                    else // Both - show dominant polarity
                        sourceColor = (z.VolumePolarities != null && i < z.VolumePolarities.Count && z.VolumePolarities[i]) ? FRVPBullishBarColor : FRVPBearishBarColor;
                }

                float sourceOpacity = isPoc ? (FRVPPoCOpacity / 100f) : barOp;
                float bL = alignLeft ? pLeft : pRight - barW;
                float bR = alignLeft ? pLeft + barW : pRight;

                // Apply gradient or solid fill
                SharpDX.Direct2D1.SolidColorBrush solidBarBr = null;
                SharpDX.Direct2D1.LinearGradientBrush gradientBrush = null;
                SharpDX.Direct2D1.Brush barBrush = null;

                if (FRVPEnableGradientFill)
                {
                    gradientBrush = CreateGradientBrush(rt, sourceColor, bL, bR, y, sourceOpacity);
                    if (gradientBrush != null) barBrush = gradientBrush;
                }

                if (barBrush == null)
                {
                    solidBarBr = new SharpDX.Direct2D1.SolidColorBrush(rt, B2C4(sourceColor, sourceOpacity));
                    barBrush = solidBarBr;
                }

                float effectiveThickness;
                float gapSize;
                if (useAdaptive)
                {
                    effectiveThickness = adaptiveThickness;
                    gapSize = Math.Max(0.5f, adaptiveThickness * 0.1f);
                }
                else
                {
                    gapSize = 1.0f;
                    effectiveThickness = Math.Max(1, FRVPBarThickness - gapSize);
                }

                float adjustedY = y + (gapSize / 2.0f);
                rt.DrawLine(new SharpDX.Vector2(bL, adjustedY), new SharpDX.Vector2(bR, adjustedY), barBrush, effectiveThickness);

                gradientBrush?.Dispose();
                solidBarBr?.Dispose();
            }

            // POC line
            if (FRVPDisplayPoC && z.PocIndex >= 0 && z.PocIndex < z.Volumes.Count)
            {
                double pocP = z.ProfileLowest + z.ProfileInterval * z.PocIndex;
                float pocY = cs.GetYByValue(pocP);
                float pocXEnd = FRVPExtendPoCVA ? (float)cp.W : pRight;
                using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, B2C4(FRVPPoCColor, FRVPPoCOpacity / 100f)))
                    rt.DrawLine(new SharpDX.Vector2(xLeft, pocY), new SharpDX.Vector2(pocXEnd, pocY), br, FRVPPoCWidth, MakeSS(rt, FRVPPoCStyle));
            }

            // VA lines
            if (FRVPDisplayVA && FRVPDisplayVALines)
            {
                float vaOp = FRVPVALineOpacity / 100f;
                float vaXEnd = FRVPExtendPoCVA ? (float)cp.W : pRight;
                using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, B2C4(FRVPVALineColor, vaOp)))
                {
                    var ss = MakeSS(rt, FRVPVALineStyle);
                    if (z.VaUpIndex >= 0 && z.VaUpIndex < z.Volumes.Count)
                    { float y = cs.GetYByValue(z.ProfileLowest + z.ProfileInterval * z.VaUpIndex); rt.DrawLine(new SharpDX.Vector2(xLeft, y), new SharpDX.Vector2(vaXEnd, y), br, FRVPVALineWidth, ss); }
                    if (z.VaDownIndex >= 0 && z.VaDownIndex < z.Volumes.Count)
                    { float y = cs.GetYByValue(z.ProfileLowest + z.ProfileInterval * z.VaDownIndex); rt.DrawLine(new SharpDX.Vector2(xLeft, y), new SharpDX.Vector2(vaXEnd, y), br, FRVPVALineWidth, ss); }
                }
            }

            // Labels
            if (FRVPShowLabels)
            {
                using (var bgBr = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.05f, 0.05f, 0.1f, 0.8f)))
                using (var fmt = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Consolas", SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, (float)FRVPLabelFontSize))
                {
                    if (FRVPDisplayPoC && z.PocIndex >= 0)
                    { double p = z.ProfileLowest + z.ProfileInterval * z.PocIndex; DL(rt, fmt, bgBr, B2C4(FRVPPoCColor, FRVPPoCOpacity / 100f), FRVPShowPrice ? "POC " + p.ToString("F2") : "POC", xLeft, cs.GetYByValue(p)); }
                    if (FRVPDisplayVA && FRVPDisplayVALines && z.VaUpIndex >= 0)
                    { double p = z.ProfileLowest + z.ProfileInterval * z.VaUpIndex; DL(rt, fmt, bgBr, B2C4(FRVPVALineColor, FRVPVALineOpacity / 100f), FRVPShowPrice ? "VAH " + p.ToString("F2") : "VAH", xLeft, cs.GetYByValue(p)); }
                    if (FRVPDisplayVA && FRVPDisplayVALines && z.VaDownIndex >= 0)
                    { double p = z.ProfileLowest + z.ProfileInterval * z.VaDownIndex; DL(rt, fmt, bgBr, B2C4(FRVPVALineColor, FRVPVALineOpacity / 100f), FRVPShowPrice ? "VAL " + p.ToString("F2") : "VAL", xLeft, cs.GetYByValue(p)); }
                }
            }

            // 3. Fibs
            if (FRVPDisplayFibs)
            {
                double fibRange = z.StartPrice - z.EndPrice;
                if (Math.Abs(fibRange) > double.Epsilon)
                {
                    float fOp = FRVPFibOpacity / 100f;
                    float fxEnd = FRVPExtendFibs ? (float)cp.W : xRight;
                    using (var bgBr = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.05f, 0.05f, 0.1f, 0.8f)))
                    using (var fmt = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Consolas", SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, (float)FRVPFibLabelSize))
                    {
                        if (_cachedFibLevels == null) _cachedFibLevels = GetFibLevels();
                        foreach (var lv in _cachedFibLevels)
                        {
                            double price = z.EndPrice + fibRange * lv.Ratio;
                            float y = cs.GetYByValue(price);
                            var lc = B2C4(lv.Color, fOp);
                            using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, lc))
                                rt.DrawLine(new SharpDX.Vector2(xLeft, y), new SharpDX.Vector2(fxEnd, y), br, FRVPFibLineWidth, MakeSS(rt, FRVPFibStyle));
                            try
                            {
                                string txt = FRVPFibShowPrice ? (lv.Ratio * 100).ToString("F1") + "% [" + price.ToString("F2") + "]" : (lv.Ratio * 100).ToString("F1") + "%";
                                using (var tl = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, txt, fmt, 400, 20))
                                {
                                    float tw = tl.Metrics.Width, th = tl.Metrics.Height;
                                    float lx = fxEnd - tw - 8, ly = y - th - 2;
                                    rt.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = new SharpDX.RectangleF(lx - 2, ly, tw + 8, th + 4), RadiusX = 2, RadiusY = 2 }, bgBr);
                                    using (var tb = new SharpDX.Direct2D1.SolidColorBrush(rt, lc)) rt.DrawTextLayout(new SharpDX.Vector2(lx + 2, ly + 1), tl, tb);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }

            // 4. AVWAP
            if (FRVPDisplayAVWAP && z.AvwapPoints != null && z.AvwapPoints.Count >= 2)
            {
                float aOp = FRVPAVWAPOpacity / 100f;
                var oldAA = rt.AntialiasMode; rt.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
                using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, B2C4(FRVPAVWAPColor, aOp)))
                {
                    var ss = MakeSS(rt, FRVPAVWAPStyle);
                    var pts = new List<SharpDX.Vector2>();
                    double lastVal = 0;
                    foreach (var kvp in z.AvwapPoints)
                    {
                        try
                        {
                            float bx = cc.GetXByBarIndex(chartBars, kvp.Key);
                            float by = cs.GetYByValue(kvp.Value);
                            lastVal = kvp.Value;
                            if (!FRVPExtendAVWAP && bx > xRight) break;
                            pts.Add(new SharpDX.Vector2(bx, by));
                        }
                        catch { }
                    }
                    if (pts.Count >= 2)
                    {
                        using (var path = new SharpDX.Direct2D1.PathGeometry(rt.Factory))
                        {
                            using (var sink = path.Open())
                            {
                                sink.BeginFigure(pts[0], SharpDX.Direct2D1.FigureBegin.Hollow);
                                for (int p = 1; p < pts.Count; p++) sink.AddLine(pts[p]);
                                sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Open); sink.Close();
                            }
                            rt.DrawGeometry(path, br, FRVPAVWAPWidth, ss);
                        }
                        if (FRVPShowAVWAPLabel && FRVPShowLabels)
                        {
                            try
                            {
                                var lp = pts[pts.Count - 1];
                                using (var bgBr = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.05f, 0.05f, 0.1f, 0.8f)))
                                using (var fmt = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Consolas", SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, (float)FRVPLabelFontSize))
                                    DL(rt, fmt, bgBr, B2C4(FRVPAVWAPColor, aOp), FRVPShowPrice ? "AVWAP " + lastVal.ToString("F2") : "AVWAP", lp.X - 60, lp.Y);
                            }
                            catch { }
                        }
                    }
                }
                rt.AntialiasMode = oldAA;
            }

            // 5. Cluster Levels
            if (FRVPDisplayClusters && z.ClusterLevels != null && z.ClusterLevels.Count > 0)
            {
                float clOp = FRVPClusterOpacity / 100f;
                float clXEnd = FRVPExtendClusters ? (float)cp.W : xRight;
                System.Windows.Media.Brush[] clColors = { FRVPCluster1Color, FRVPCluster2Color, FRVPCluster3Color, FRVPCluster4Color, FRVPCluster5Color };

                using (var bgBr = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.05f, 0.05f, 0.1f, 0.8f)))
                using (var fmt = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Consolas", SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, (float)FRVPLabelFontSize))
                {
                    for (int ci = 0; ci < z.ClusterLevels.Count; ci++)
                    {
                        var cl = z.ClusterLevels[ci];
                        var clBrush = clColors[ci % clColors.Length];
                        var clC4 = B2C4(clBrush, clOp);
                        float y = cs.GetYByValue(cl.POCPrice);

                        using (var lineBr = new SharpDX.Direct2D1.SolidColorBrush(rt, clC4))
                            rt.DrawLine(new SharpDX.Vector2(xLeft, y), new SharpDX.Vector2(clXEnd, y), lineBr, FRVPClusterLineWidth, MakeSS(rt, FRVPClusterLineStyle));

                        if (FRVPShowClusterLabels)
                        {
                            try
                            {
                                string lbl = FRVPShowPrice ? "C" + (ci + 1) + " POC " + cl.POCPrice.ToString("F2") : "C" + (ci + 1) + " POC";
                                DL(rt, fmt, bgBr, clC4, lbl, clXEnd - 120, y);
                            }
                            catch { }
                        }
                    }
                }
            }
        }

        #endregion

        #region FRVP Calculation

        private void CalcVolumeProfile(FRVPZone z, Bars bars)
        {
            z.Dirty = false;

            int sb = z.StartBar, eb = z.IsActive ? bars.Count - 1 : z.EndBar;
            if (sb >= bars.Count || eb < sb) return;
            int sIdx = Math.Max(0, sb);
            int eIdx = Math.Min(eb, bars.Count - 1);
            if (sIdx > eIdx) return;

            // ── Determine if we can do an incremental update ──
            // Incremental is possible when:
            //  1. We have existing volume data from a previous calc
            //  2. The price range hasn't expanded (bins haven't changed)
            //  3. LastProcessedBarIdx is valid
            bool canIncremental = false;
            int incrStartIdx = sIdx;

            if (z.LastProcessedBarIdx > 0 && z.Volumes != null && z.Volumes.Count == FRVPRows
                && z.IncrBullVol != null && z.IncrBearVol != null
                && z.IncrBullVol.Length == FRVPRows && z.IncrBearVol.Length == FRVPRows)
            {
                // Check if new bars expand the price range
                double newHigh = z.HighPrice, newLow = z.LowPrice;
                for (int i = z.LastProcessedBarIdx + 1; i <= eIdx; i++)
                {
                    double hi = bars.GetHigh(i), lo = bars.GetLow(i);
                    if (hi > newHigh) newHigh = hi;
                    if (lo < newLow) newLow = lo;
                }

                // If range hasn't expanded, we can do incremental
                if (newHigh <= z.HighPrice && newLow >= z.LowPrice && z.ProfileInterval > 0)
                {
                    canIncremental = true;
                    incrStartIdx = z.LastProcessedBarIdx + 1;
                }
                else
                {
                    // Range expanded — update tracked range and fall through to full recalc
                    z.HighPrice = newHigh;
                    z.LowPrice = newLow;
                }
            }

            if (canIncremental && incrStartIdx <= eIdx)
            {
                // ── INCREMENTAL PATH: only process new bars ──
                for (int i = incrStartIdx; i <= eIdx; i++)
                {
                    double lo = bars.GetLow(i), hi = bars.GetHigh(i), vol = bars.GetVolume(i);
                    double op = bars.GetOpen(i), cl = bars.GetClose(i);
                    bool isBullish = cl >= op;
                    int minI = Math.Max(0, Math.Min((int)Math.Floor((lo - z.ProfileLowest) / z.ProfileInterval), FRVPRows - 1));
                    int maxI = Math.Max(0, Math.Min((int)Math.Ceiling((hi - z.ProfileLowest) / z.ProfileInterval), FRVPRows - 1));
                    int touched = maxI - minI + 1;
                    if (touched > 0)
                    {
                        double vpl = vol / touched;
                        bool includeVol = FRVPVolumeType == MSVolumeType.Standard ||
                                         FRVPVolumeType == MSVolumeType.Both ||
                                         (FRVPVolumeType == MSVolumeType.Bullish && isBullish) ||
                                         (FRVPVolumeType == MSVolumeType.Bearish && !isBullish);
                        if (includeVol)
                        {
                            for (int j = minI; j <= maxI; j++)
                            {
                                z.Volumes[j] += vpl;
                                if (isBullish) z.IncrBullVol[j] += vpl;
                                else z.IncrBearVol[j] += vpl;
                            }
                        }
                    }
                }

                // Update polarity
                for (int i = 0; i < FRVPRows; i++)
                    z.VolumePolarities[i] = z.IncrBullVol[i] >= z.IncrBearVol[i];

                // Recompute POC from accumulated volumes (fast scan)
                z.MaxVolume = 0; z.PocIndex = 0;
                for (int i = 0; i < FRVPRows; i++)
                    if (z.Volumes[i] > z.MaxVolume) { z.MaxVolume = z.Volumes[i]; z.PocIndex = i; }

                // Recompute Value Area
                z.VaUpIndex = z.PocIndex; z.VaDownIndex = z.PocIndex;
                double sumVol = 0; for (int i = 0; i < z.Volumes.Count; i++) sumVol += z.Volumes[i];
                double vaTarget = sumVol * FRVPValueAreaPct / 100.0;
                double vaSum = z.MaxVolume;
                while (vaSum < vaTarget)
                {
                    double vUp = (z.VaUpIndex < FRVPRows - 1) ? z.Volumes[z.VaUpIndex + 1] : 0;
                    double vDn = (z.VaDownIndex > 0) ? z.Volumes[z.VaDownIndex - 1] : 0;
                    if (vUp == 0 && vDn == 0) break;
                    if (vUp >= vDn) { vaSum += vUp; z.VaUpIndex++; } else { vaSum += vDn; z.VaDownIndex--; }
                }

                // Recompute AVWAP (must be full — cumulative from anchor)
                if (FRVPDisplayAVWAP)
                {
                    if (z.AvwapPoints == null) z.AvwapPoints = new List<KeyValuePair<int, double>>(256);
                    else z.AvwapPoints.Clear();
                    int avwapStart = Math.Max(z.AvwapAnchorBar, sIdx);
                    double cumVol = 0, cumTV = 0;
                    for (int i = avwapStart; i < bars.Count; i++)
                    {
                        double vol = bars.GetVolume(i);
                        double src = (bars.GetOpen(i) + bars.GetHigh(i) + bars.GetLow(i) + bars.GetClose(i)) / 4.0;
                        cumVol += vol; cumTV += src * vol;
                        if (cumVol > 0) z.AvwapPoints.Add(new KeyValuePair<int, double>(i, cumTV / cumVol));
                    }
                }

                // Update dynamic fib endpoints for active zones
                if (z.IsActive)
                {
                    if (z.Direction == 1) { z.StartPrice = z.LowPrice; z.EndPrice = z.HighPrice; }
                    else                  { z.StartPrice = z.HighPrice; z.EndPrice = z.LowPrice; }
                }

                // Cluster Levels (full recalc — these are cheap relative to volume profile)
                if (FRVPDisplayClusters)
                    CalcClusterLevels(z, bars, sIdx, eIdx);

                z.LastProcessedBarIdx = eIdx;
                return;
            }

            // ── FULL RECALC PATH (first calc or range expanded) ──
            z.MaxVolume = 0; z.PocIndex = -1; z.VaUpIndex = -1; z.VaDownIndex = -1;

            // Reuse volume list instead of allocating new
            if (z.Volumes == null || z.Volumes.Count != FRVPRows)
            {
                z.Volumes = new List<double>(FRVPRows);
                for (int i = 0; i < FRVPRows; i++) z.Volumes.Add(0);
            }
            else
                for (int i = 0; i < FRVPRows; i++) z.Volumes[i] = 0;

            if (z.VolumePolarities == null || z.VolumePolarities.Count != FRVPRows)
            {
                z.VolumePolarities = new List<bool>(FRVPRows);
                for (int i = 0; i < FRVPRows; i++) z.VolumePolarities.Add(true);
            }
            else
                for (int i = 0; i < FRVPRows; i++) z.VolumePolarities[i] = true;

            // Allocate or reset incremental tracking arrays
            if (z.IncrBullVol == null || z.IncrBullVol.Length != FRVPRows)
                z.IncrBullVol = new double[FRVPRows];
            else
                Array.Clear(z.IncrBullVol, 0, FRVPRows);

            if (z.IncrBearVol == null || z.IncrBearVol.Length != FRVPRows)
                z.IncrBearVol = new double[FRVPRows];
            else
                Array.Clear(z.IncrBearVol, 0, FRVPRows);

            // Reuse AVWAP list
            if (z.AvwapPoints == null) z.AvwapPoints = new List<KeyValuePair<int, double>>(256);
            else z.AvwapPoints.Clear();

            z.HighPrice = double.MinValue; z.LowPrice = double.MaxValue;

            for (int i = sIdx; i <= eIdx; i++)
            {
                z.HighPrice = Math.Max(z.HighPrice, bars.GetHigh(i));
                z.LowPrice = Math.Min(z.LowPrice, bars.GetLow(i));
            }
            if (z.HighPrice <= z.LowPrice) return;

            if (z.IsActive)
            {
                if (z.Direction == 1) { z.StartPrice = z.LowPrice; z.EndPrice = z.HighPrice; }
                else                  { z.StartPrice = z.HighPrice; z.EndPrice = z.LowPrice; }
            }

            z.ProfileLowest = z.LowPrice;
            z.ProfileInterval = (z.HighPrice - z.LowPrice) / (FRVPRows - 1);
            if (z.ProfileInterval <= 0) return;

            for (int i = sIdx; i <= eIdx; i++)
            {
                double lo = bars.GetLow(i), hi = bars.GetHigh(i), vol = bars.GetVolume(i);
                double op = bars.GetOpen(i), cl = bars.GetClose(i);
                bool isBullish = cl >= op;
                int minI = Math.Max(0, Math.Min((int)Math.Floor((lo - z.ProfileLowest) / z.ProfileInterval), FRVPRows - 1));
                int maxI = Math.Max(0, Math.Min((int)Math.Ceiling((hi - z.ProfileLowest) / z.ProfileInterval), FRVPRows - 1));
                int touched = maxI - minI + 1;
                if (touched > 0)
                {
                    double vpl = vol / touched;
                    bool includeVol = FRVPVolumeType == MSVolumeType.Standard ||
                                     FRVPVolumeType == MSVolumeType.Both ||
                                     (FRVPVolumeType == MSVolumeType.Bullish && isBullish) ||
                                     (FRVPVolumeType == MSVolumeType.Bearish && !isBullish);
                    if (includeVol)
                    {
                        for (int j = minI; j <= maxI; j++)
                        {
                            z.Volumes[j] += vpl;
                            if (isBullish) z.IncrBullVol[j] += vpl;
                            else z.IncrBearVol[j] += vpl;
                        }
                    }
                }
            }

            // Set polarity for each row
            for (int i = 0; i < FRVPRows; i++)
                z.VolumePolarities[i] = z.IncrBullVol[i] >= z.IncrBearVol[i];

            z.PocIndex = 0;
            for (int i = 0; i < FRVPRows; i++) if (z.Volumes[i] > z.MaxVolume) { z.MaxVolume = z.Volumes[i]; z.PocIndex = i; }

            // Value Area
            z.VaUpIndex = z.PocIndex; z.VaDownIndex = z.PocIndex;
            double fullSumVol = 0; for (int i = 0; i < z.Volumes.Count; i++) fullSumVol += z.Volumes[i];
            double fullVaTarget = fullSumVol * FRVPValueAreaPct / 100.0;
            double fullVaSum = z.MaxVolume;
            while (fullVaSum < fullVaTarget)
            {
                double vUp = (z.VaUpIndex < FRVPRows - 1) ? z.Volumes[z.VaUpIndex + 1] : 0;
                double vDn = (z.VaDownIndex > 0) ? z.Volumes[z.VaDownIndex - 1] : 0;
                if (vUp == 0 && vDn == 0) break;
                if (vUp >= vDn) { fullVaSum += vUp; z.VaUpIndex++; } else { fullVaSum += vDn; z.VaDownIndex--; }
            }

            // AVWAP
            if (FRVPDisplayAVWAP)
            {
                int avwapStart = Math.Max(z.AvwapAnchorBar, sIdx);
                double cumVol = 0, cumTV = 0;
                for (int i = avwapStart; i < bars.Count; i++)
                {
                    double vol = bars.GetVolume(i);
                    double src = (bars.GetOpen(i) + bars.GetHigh(i) + bars.GetLow(i) + bars.GetClose(i)) / 4.0;
                    cumVol += vol; cumTV += src * vol;
                    if (cumVol > 0) z.AvwapPoints.Add(new KeyValuePair<int, double>(i, cumTV / cumVol));
                }
            }

            // Cluster Levels (K-Means)
            if (FRVPDisplayClusters)
                CalcClusterLevels(z, bars, sIdx, eIdx);

            z.LastProcessedBarIdx = eIdx;
        }

        private void CalcClusterLevels(FRVPZone z, Bars bars, int sIdx, int eIdx)
        {
            if (z.ClusterLevels == null) z.ClusterLevels = new List<ClusterLevelInfo>();
            else z.ClusterLevels.Clear();

            var prices = new List<double>();
            var volList = new List<double>();
            var highList = new List<double>();
            var lowList = new List<double>();

            for (int i = sIdx; i <= eIdx; i++)
            {
                double h = bars.GetHigh(i), l = bars.GetLow(i), v = bars.GetVolume(i);
                prices.Add((h + l) / 2.0); volList.Add(v); highList.Add(h); lowList.Add(l);
            }

            int n = prices.Count;
            if (n < 2) return;

            int k = Math.Min(FRVPClusterCount, n);
            double minP = double.MaxValue, maxP = double.MinValue;
            for (int i = 0; i < n; i++) { if (prices[i] < minP) minP = prices[i]; if (prices[i] > maxP) maxP = prices[i]; }
            if (maxP <= minP) return;

            // Init centroids evenly spaced
            double[] cents = new double[k];
            double step = (maxP - minP) / (k + 1);
            for (int i = 0; i < k; i++) cents[i] = minP + (i + 1) * step;

            // K-Means iterations (volume-weighted)
            int[] assign = new int[n];
            for (int iter = 0; iter < FRVPClusterIterations; iter++)
            {
                for (int i = 0; i < n; i++)
                {
                    int bestK = 0; double minDist = double.MaxValue;
                    for (int j = 0; j < k; j++) { double dist = Math.Abs(prices[i] - cents[j]); if (dist < minDist) { minDist = dist; bestK = j; } }
                    assign[i] = bestK;
                }
                double[] sumPV = new double[k]; double[] sumV = new double[k];
                for (int i = 0; i < n; i++) { int c = assign[i]; sumPV[c] += prices[i] * volList[i]; sumV[c] += volList[i]; }
                for (int j = 0; j < k; j++) { if (sumV[j] > 0) cents[j] = sumPV[j] / sumV[j]; }
            }

            // Build per-cluster volume profiles for POC
            int rows = FRVPClusterRowsPerLevel;
            for (int cId = 0; cId < k; cId++)
            {
                double cMin = double.MaxValue, cMax = double.MinValue;
                double cTotalVol = 0; int cBarCount = 0;
                var cHighs = new List<double>(); var cLows = new List<double>(); var cVols = new List<double>();

                for (int i = 0; i < n; i++)
                {
                    if (assign[i] != cId) continue;
                    cHighs.Add(highList[i]); cLows.Add(lowList[i]); cVols.Add(volList[i]);
                    if (lowList[i] < cMin) cMin = lowList[i]; if (highList[i] > cMax) cMax = highList[i];
                    cTotalVol += volList[i]; cBarCount++;
                }
                if (cBarCount == 0 || cMax <= cMin) continue;

                double binSize = (cMax - cMin) / rows; if (binSize <= 0) continue;
                double[] binVols = new double[rows];
                for (int i = 0; i < cHighs.Count; i++)
                {
                    double bH = cHighs[i], bL = cLows[i], bV = cVols[i];
                    double wickRange = Math.Max(bH - bL, z.ProfileInterval > 0 ? z.ProfileInterval : 0.01);
                    for (int bIdx = 0; bIdx < rows; bIdx++)
                    {
                        double binBot = cMin + bIdx * binSize, binTop = binBot + binSize;
                        double intersectL = Math.Max(bL, binBot), intersectH = Math.Min(bH, binTop);
                        if (intersectH > intersectL) binVols[bIdx] += bV * (intersectH - intersectL) / wickRange;
                    }
                }

                double maxBinVol = 0; int pocIdx = 0;
                for (int bIdx = 0; bIdx < rows; bIdx++) { if (binVols[bIdx] > maxBinVol) { maxBinVol = binVols[bIdx]; pocIdx = bIdx; } }
                double pocPrice = cMin + pocIdx * binSize + binSize / 2.0;

                z.ClusterLevels.Add(new ClusterLevelInfo { POCPrice = pocPrice, POCVolume = maxBinVol, ClusterHigh = cMax, ClusterLow = cMin, TotalVolume = cTotalVol, BarCount = cBarCount });
            }
            z.ClusterLevels.Sort((a, b) => a.POCPrice.CompareTo(b.POCPrice));
        }

        private void CreateFRVPZone(int direction)
        {
            if (!KeepPreviousFRVP && _activeFrvp != null)
                _activeFrvp.IsActive = false;
            else if (KeepPreviousFRVP && _activeFrvp != null)
            {
                _activeFrvp.IsActive = false;
                _activeFrvp.EndBar = CurrentBar;
                _historicFrvps.Add(_activeFrvp);
            }

            int startB, avwapBar;
            double startP, endP;

            if (direction == 1)
            {
                // Bullish zone: need the absolute low of the entire bearish leg
                // Scan from the leg origin high (top of the move) down to _prevLowIndex
                int scanFrom = _legOriginHighIndex;
                int scanTo = Math.Min(CurrentBar, _prevLowIndex + SwingLength);
                if (scanFrom > scanTo) { scanFrom = Math.Max(0, _legOriginHighIndex); }

                double absLow = _prevLow;
                int absLowBar = _prevLowIndex;
                for (int i = scanFrom; i <= scanTo; i++)
                {
                    int barsAgo = CurrentBar - i;
                    if (barsAgo < 0 || barsAgo >= Count) continue;
                    double lo = Low[barsAgo];
                    if (lo < absLow)
                    {
                        absLow = lo;
                        absLowBar = i;
                    }
                }

                startB = absLowBar;
                avwapBar = absLowBar;
                startP = absLow;
                endP = _legOriginHigh != double.MinValue ? _legOriginHigh : _prevHigh;
            }
            else
            {
                // Bearish zone: need the absolute high of the entire bullish leg
                int scanFrom = _legOriginLowIndex;
                int scanTo = Math.Min(CurrentBar, _prevHighIndex + SwingLength);
                if (scanFrom > scanTo) { scanFrom = Math.Max(0, _legOriginLowIndex); }

                double absHigh = _prevHigh;
                int absHighBar = _prevHighIndex;
                for (int i = scanFrom; i <= scanTo; i++)
                {
                    int barsAgo = CurrentBar - i;
                    if (barsAgo < 0 || barsAgo >= Count) continue;
                    double hi = High[barsAgo];
                    if (hi > absHigh)
                    {
                        absHigh = hi;
                        absHighBar = i;
                    }
                }

                startB = absHighBar;
                avwapBar = absHighBar;
                startP = absHigh;
                endP = _legOriginLow != double.MaxValue ? _legOriginLow : _prevLow;
            }

            _activeFrvp = new FRVPZone
            {
                StartBar = startB, EndBar = CurrentBar,
                StartPrice = startP, EndPrice = endP,
                Direction = direction, IsActive = true,
                AvwapAnchorBar = avwapBar, Dirty = true
            };
        }

        private List<FibLevel> GetFibLevels()
        {
            var list = new List<FibLevel>();
            double[] vals = { FibLevel1, FibLevel2, FibLevel3, FibLevel4, FibLevel5, FibLevel6, FibLevel7, FibLevel8, FibLevel9, FibLevel10 };
            System.Windows.Media.Brush[] cols = { FibLevel1Color, FibLevel2Color, FibLevel3Color, FibLevel4Color, FibLevel5Color, FibLevel6Color, FibLevel7Color, FibLevel8Color, FibLevel9Color, FibLevel10Color };
            for (int i = 0; i < 10; i++) if (vals[i] >= 0) list.Add(new FibLevel { Ratio = vals[i] / 100.0, Color = cols[i] ?? System.Windows.Media.Brushes.DodgerBlue });
            return list;
        }

        #endregion

        #region Market Structure

        private void ProcessMarketStructure()
        {
            int len = SwingLength;
            double cH = High[len]; bool isPH = true;
            for (int i = 0; i <= len * 2; i++) { if (i == len) continue; if (High[i] > cH) { isPH = false; break; } }
            double cL = Low[len]; bool isPL = true;
            for (int i = 0; i <= len * 2; i++) { if (i == len) continue; if (Low[i] < cL) { isPL = false; break; } }

            int psb = _prevSwingType;
            if (isPH)
            {
                bool isHH = (cH >= _prevHigh); _prevSwingType = isHH ? 2 : 1;
                if (isHH && psb == -1 && ShowHalfRetracement)
                { double h = (_prevLow + cH) / 2; _halfRetraceRenders.Add(new HalfRetraceInfo { StartBar = _prevLowIndex, EndBar = CurrentBar - len, Price = h }); }

                // Strong/Weak evaluation — multi-factor
                bool isStrong = false;
                if (ShowStrongWeakLevels)
                {
                    isStrong = EvaluateSwingStrength(len, true);
                }

                if (ShowSwingLabels)
                {
                    string swingLbl = isHH ? "HH" : "LH";
                    if (ShowStrongWeakLevels) swingLbl += isStrong ? " (S)" : " (W)";
                    _swingLabelRenders.Add(new SwingLabelInfo { BarIndex = CurrentBar - len, Price = cH + TickSize * 3, Label = swingLbl, IsHigh = true });
                }

                // Add strong level ray
                if (isStrong && ShowStrongWeakLevels)
                {
                    _msTagCounter++;
                    string tag = "RT_SL_" + _msTagCounter;
                    _strongLevels.Add(new StrongLevel { Price = cH, BarIndex = CurrentBar - len, IsHigh = true, Tag = tag });
                }

                _prevHigh = cH; _prevHighIndex = CurrentBar - len; _highActive = true;

                // Track swing high history and detect equal highs
                if (ShowEqualLevels)
                {
                    double tol = TickSize * EqualLevelTolerance;
                    int newBar = CurrentBar - len;
                    foreach (var sh in _swingHighHistory)
                    {
                        if (Math.Abs(cH - sh.Value) <= tol && sh.Key != newBar)
                        {
                            // Check not already tracked at this price
                            bool exists = false;
                            foreach (var el in _equalLevels) if (Math.Abs(el.Price - ((cH + sh.Value) / 2.0)) <= TickSize) { exists = true; break; }
                            if (!exists)
                            {
                                _eqTagCounter++;
                                string tag = "RT_EQ_" + _eqTagCounter;
                                _equalLevels.Add(new StrongLevel { Price = (cH + sh.Value) / 2.0, BarIndex = sh.Key, BarIndex2 = newBar, IsHigh = true, Tag = tag });
                            }
                        }
                    }
                    _swingHighHistory.Add(new KeyValuePair<int, double>(newBar, cH));
                    if (_swingHighHistory.Count > 30) _swingHighHistory.RemoveAt(0);
                }
            }
            if (isPL)
            {
                bool isHL = (cL >= _prevLow); _prevSwingType = isHL ? -1 : -2;
                if (!isHL && psb == 1 && ShowHalfRetracement)
                { double h = (_prevHigh + cL) / 2; _halfRetraceRenders.Add(new HalfRetraceInfo { StartBar = _prevHighIndex, EndBar = CurrentBar - len, Price = h }); }

                // Strong/Weak evaluation — multi-factor
                bool isStrong = false;
                if (ShowStrongWeakLevels)
                {
                    isStrong = EvaluateSwingStrength(len, false);
                }

                if (ShowSwingLabels)
                {
                    string swingLbl = isHL ? "HL" : "LL";
                    if (ShowStrongWeakLevels) swingLbl += isStrong ? " (S)" : " (W)";
                    _swingLabelRenders.Add(new SwingLabelInfo { BarIndex = CurrentBar - len, Price = cL - TickSize * 3, Label = swingLbl, IsHigh = false });
                }

                // Add strong level ray
                if (isStrong && ShowStrongWeakLevels)
                {
                    _msTagCounter++;
                    string tag = "RT_SL_" + _msTagCounter;
                    _strongLevels.Add(new StrongLevel { Price = cL, BarIndex = CurrentBar - len, IsHigh = false, Tag = tag });
                }

                _prevLow = cL; _prevLowIndex = CurrentBar - len; _lowActive = true;

                // Track swing low history and detect equal lows
                if (ShowEqualLevels)
                {
                    double tol = TickSize * EqualLevelTolerance;
                    int newBar = CurrentBar - len;
                    foreach (var sl in _swingLowHistory)
                    {
                        if (Math.Abs(cL - sl.Value) <= tol && sl.Key != newBar)
                        {
                            bool exists = false;
                            foreach (var el in _equalLevels) if (Math.Abs(el.Price - ((cL + sl.Value) / 2.0)) <= TickSize) { exists = true; break; }
                            if (!exists)
                            {
                                _eqTagCounter++;
                                string tag = "RT_EQ_" + _eqTagCounter;
                                _equalLevels.Add(new StrongLevel { Price = (cL + sl.Value) / 2.0, BarIndex = sl.Key, BarIndex2 = newBar, IsHigh = false, Tag = tag });
                            }
                        }
                    }
                    _swingLowHistory.Add(new KeyValuePair<int, double>(newBar, cL));
                    if (_swingLowHistory.Count > 30) _swingLowHistory.RemoveAt(0);
                }
            }

            double hSrc = (BOSConfirmation == "Candle Close") ? Close[0] : High[0];
            double lSrc = (BOSConfirmation == "Candle Close") ? Close[0] : Low[0];

            if (hSrc > _prevHigh && _highActive && _prevHigh != double.MinValue)
            {
                _highActive = false; int ba = CurrentBar - _prevHighIndex;
                bool choch = (_prevBreakoutDir == -1 && ShowCHoCH);

                // When direction changes bear->bull, save the leg origin
                if (choch || _prevBreakoutDir == -1)
                {
                    _legOriginLow = _prevLow;
                    _legOriginLowIndex = _prevLowIndex;
                    _legOriginHigh = _prevHigh;
                    _legOriginHighIndex = _prevHighIndex;
                }
                
                _bosRenders.Add(new BOSRenderInfo { StartBar = _prevHighIndex, EndBar = CurrentBar, Price = _prevHigh, IsCHoCH = choch, IsBull = true });
                if (choch && EnableFRVP && FRVPTrigger == "CHoCH") CreateFRVPZone(1);

                // Trend state tracking
                if (choch)
                    _pendingChoch = 1;
                else
                {
                    if (_pendingChoch == 1)
                    {
                        bool trendActuallyChanged = (_trendState != 1);
                        _trendState = 1;
                        _pendingChoch = 0;
                        if (trendActuallyChanged && EnableFRVP && FRVPTrigger == "BOS (Confirmed Trend)") CreateFRVPZone(1);
                    }
                    else if (_trendState == 1)
                        _pendingChoch = 0;
                }

                if (State == State.Realtime)
                {
                    if (choch && AlertOnCHoCH)
                    {
                        string msg = instrumentName + " Bullish CHoCH at " + _prevHigh.ToString("F2");
                        Alert("RT_CHoCH_" + CurrentBar, Priority.High, msg,
                            GetVoiceAlertPath("BullCHoCH", AlertSoundCHoCH), 10, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Lime);
                    }
                    else if (!choch && AlertOnBOS)
                    {
                        string msg = instrumentName + " Bullish BOS at " + _prevHigh.ToString("F2");
                        Alert("RT_BOS_" + CurrentBar, Priority.Medium, msg,
                            GetVoiceAlertPath("BullBOS", AlertSoundBOS), 10, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.DodgerBlue);
                    }
                }
                _prevBreakoutDir = 1;
            }
            if (lSrc < _prevLow && _lowActive && _prevLow != double.MaxValue)
            {
                _lowActive = false; int ba = CurrentBar - _prevLowIndex;
                bool choch = (_prevBreakoutDir == 1 && ShowCHoCH);

                // When direction changes bull->bear, save the leg origin
                if (choch || _prevBreakoutDir == 1)
                {
                    _legOriginHigh = _prevHigh;
                    _legOriginHighIndex = _prevHighIndex;
                    _legOriginLow = _prevLow;
                    _legOriginLowIndex = _prevLowIndex;
                }
                
                _bosRenders.Add(new BOSRenderInfo { StartBar = _prevLowIndex, EndBar = CurrentBar, Price = _prevLow, IsCHoCH = choch, IsBull = false });
                if (choch && EnableFRVP && FRVPTrigger == "CHoCH") CreateFRVPZone(-1);

                // Trend state tracking
                if (choch)
                    _pendingChoch = -1;
                else
                {
                    if (_pendingChoch == -1)
                    {
                        bool trendActuallyChanged = (_trendState != -1);
                        _trendState = -1;
                        _pendingChoch = 0;
                        if (trendActuallyChanged && EnableFRVP && FRVPTrigger == "BOS (Confirmed Trend)") CreateFRVPZone(-1);
                    }
                    else if (_trendState == -1)
                        _pendingChoch = 0;
                }

                if (State == State.Realtime)
                {
                    if (choch && AlertOnCHoCH)
                    {
                        string msg = instrumentName + " Bearish CHoCH at " + _prevLow.ToString("F2");
                        Alert("RT_CHoCH_" + CurrentBar, Priority.High, msg,
                            GetVoiceAlertPath("BearCHoCH", AlertSoundCHoCH), 10, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.OrangeRed);
                    }
                    else if (!choch && AlertOnBOS)
                    {
                        string msg = instrumentName + " Bearish BOS at " + _prevLow.ToString("F2");
                        Alert("RT_BOS_" + CurrentBar, Priority.Medium, msg,
                            GetVoiceAlertPath("BearBOS", AlertSoundBOS), 10, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.DodgerBlue);
                    }
                }
                _prevBreakoutDir = -1;
            }
        }

        #endregion

        #region Order Blocks

        private void UpdateATR()
        {
            double s = 0; int p = Math.Min(10, CurrentBar);
            for (int i = 0; i < p; i++) { double tr = High[i] - Low[i]; if (i + 1 <= CurrentBar) { tr = Math.Max(tr, Math.Abs(High[i] - Close[i + 1])); tr = Math.Max(tr, Math.Abs(Low[i] - Close[i + 1])); } s += tr; }
            _atrValue = p > 0 ? s / p : 0;
        }

        private void FindOBSwings()
        {
            int len = OBSwingLength; if (CurrentBar < len * 2) return;
            double u = double.MinValue, l = double.MaxValue;
            for (int i = 0; i < len; i++) { if (High[i] > u) u = High[i]; if (Low[i] < l) l = Low[i]; }
            int prev = _obSwingType;
            if (High[len] > u) _obSwingType = 0; else if (Low[len] < l) _obSwingType = 1;
            if (_obSwingType == 0 && prev != 0) _obSwingTop = new OBSwing { X = CurrentBar - len, Y = High[len], SwingVolume = Volume[len] };
            if (_obSwingType == 1 && prev != 1) _obSwingBottom = new OBSwing { X = CurrentBar - len, Y = Low[len], SwingVolume = Volume[len] };
        }

        private void ProcessOrderBlocks()
        {
            bool w = (ZoneInvalidation == "Wick");

            // Invalidate / break bull OBs
            for (int i = _bullOBList.Count - 1; i >= 0; i--)
            {
                var ob = _bullOBList[i];
                if (!ob.Breaker)
                {
                    if ((w ? Low[0] : Math.Min(Open[0], Close[0])) < ob.Bottom)
                    {
                        if (ConvertToBreaker)
                        {
                            ob.Breaker = true;
                            ob.BreakBar = CurrentBar;
                        }
                        else
                        {
                            ob.Breaker = true; ob.BreakBar = CurrentBar;
                            if (DeleteBrokenBoxes) { RmOB(ob); _bullOBList.RemoveAt(i); }
                        }
                    }
                }
                else
                {
                    // Breaker block: broken bull OB acts as bearish resistance
                    double inv = w ? High[0] : Math.Max(Open[0], Close[0]);
                    if (inv > ob.Top) { RmOB(ob); _bullOBList.RemoveAt(i); }
                }
            }

            // Detect new bull OB
            if (_obSwingTop.Y != double.MinValue && Close[0] > _obSwingTop.Y && !_obSwingTop.Crossed)
            {
                _obSwingTop.Crossed = true;
                double bb = High[1], bt = Low[1]; int bl = CurrentBar - 1;
                int lb = Math.Min(CurrentBar - _obSwingTop.X, CurrentBar); if (lb < 2) lb = 2;
                int obBarsAgo = 1;
                for (int i = 1; i < lb && i <= CurrentBar; i++)
                    if (Low[i] < bb) { bb = Low[i]; bt = High[i]; bl = CurrentBar - i; obBarsAgo = i; }

                double drawTop = bt, drawBottom = bb;
                if (OBDrawStyle == "Wick Only")
                {
                    // Bull OB: just the lower wick — from wick low up to bottom of candle body
                    drawTop = Math.Min(Open[obBarsAgo], Close[obBarsAgo]);
                }

                if (Math.Abs(drawTop - drawBottom) <= _atrValue * MaxATRMultiplier && Math.Abs(drawTop - drawBottom) > 0)
                {
                    _obTagCounter++;
                    _bullOBList.Insert(0, new OBInfo
                    {
                        Top = drawTop, Bottom = drawBottom,
                        OBType = "Bull", StartBar = bl,
                        Tag = "RT_V_" + _obTagCounter
                    });
                    // Remove older bull OBs that overlap the new one
                    for (int i = _bullOBList.Count - 1; i > 0; i--)
                    {
                        var older = _bullOBList[i];
                        if (!older.Breaker && older.Top >= drawBottom && older.Bottom <= drawTop)
                        { RmOB(older); _bullOBList.RemoveAt(i); }
                    }
                    if (_bullOBList.Count > 30) { RmOB(_bullOBList[_bullOBList.Count - 1]); _bullOBList.RemoveAt(_bullOBList.Count - 1); }

                    // Alert on bull OB creation
                    if (AlertOnOBCreation && State == State.Realtime)
                    {
                        string msg = instrumentName + " Bullish Order Block formed at " + drawBottom.ToString("F2") + " - " + drawTop.ToString("F2");
                        Alert("RT_OB_Bull_" + CurrentBar, Priority.Medium, msg,
                            GetVoiceAlertPath("BullOBCreated", AlertSoundOB), 10, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.SeaGreen);
                    }
                }
            }

            // Invalidate / break bear OBs
            for (int i = _bearOBList.Count - 1; i >= 0; i--)
            {
                var ob = _bearOBList[i];
                if (!ob.Breaker)
                {
                    if ((w ? High[0] : Math.Max(Open[0], Close[0])) > ob.Top)
                    {
                        if (ConvertToBreaker)
                        {
                            ob.Breaker = true;
                            ob.BreakBar = CurrentBar;
                        }
                        else
                        {
                            ob.Breaker = true; ob.BreakBar = CurrentBar;
                            if (DeleteBrokenBoxes) { RmOB(ob); _bearOBList.RemoveAt(i); }
                        }
                    }
                }
                else
                {
                    // Breaker block: broken bear OB acts as bullish support
                    double inv = w ? Low[0] : Math.Min(Open[0], Close[0]);
                    if (inv < ob.Bottom) { RmOB(ob); _bearOBList.RemoveAt(i); }
                }
            }

            // Detect new bear OB
            if (_obSwingBottom.Y != double.MaxValue && Close[0] < _obSwingBottom.Y && !_obSwingBottom.Crossed)
            {
                _obSwingBottom.Crossed = true;
                double bt = Low[1], bb = High[1]; int bl = CurrentBar - 1;
                int lb = Math.Min(CurrentBar - _obSwingBottom.X, CurrentBar); if (lb < 2) lb = 2;
                int obBarsAgo = 1;
                for (int i = 1; i < lb && i <= CurrentBar; i++)
                    if (High[i] > bt) { bt = High[i]; bb = Low[i]; bl = CurrentBar - i; obBarsAgo = i; }

                double drawTop = bt, drawBottom = bb;
                if (OBDrawStyle == "Wick Only")
                {
                    // Bear OB: just the upper wick — from top of candle body up to wick high
                    drawBottom = Math.Max(Open[obBarsAgo], Close[obBarsAgo]);
                }

                if (Math.Abs(drawTop - drawBottom) <= _atrValue * MaxATRMultiplier && Math.Abs(drawTop - drawBottom) > 0)
                {
                    _obTagCounter++;
                    _bearOBList.Insert(0, new OBInfo
                    {
                        Top = drawTop, Bottom = drawBottom,
                        OBType = "Bear", StartBar = bl,
                        Tag = "RT_V_" + _obTagCounter
                    });
                    // Remove older bear OBs that overlap the new one
                    for (int i = _bearOBList.Count - 1; i > 0; i--)
                    {
                        var older = _bearOBList[i];
                        if (!older.Breaker && older.Top >= drawBottom && older.Bottom <= drawTop)
                        { RmOB(older); _bearOBList.RemoveAt(i); }
                    }
                    if (_bearOBList.Count > 30) { RmOB(_bearOBList[_bearOBList.Count - 1]); _bearOBList.RemoveAt(_bearOBList.Count - 1); }

                    // Alert on bear OB creation
                    if (AlertOnOBCreation && State == State.Realtime)
                    {
                        string msg = instrumentName + " Bearish Order Block formed at " + drawBottom.ToString("F2") + " - " + drawTop.ToString("F2");
                        Alert("RT_OB_Bear_" + CurrentBar, Priority.Medium, msg,
                            GetVoiceAlertPath("BearOBCreated", AlertSoundOB), 10, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Crimson);
                    }
                }
            }
        }

        private void RenderAllOBs()
        {
            // OB borders are now rendered entirely via SharpDX in OnRender.
            // This method only prunes disabled/excess OBs from the lists.
            int mx = GetMaxOB(); int bc = 0, rc = 0;
            for (int i = _bullOBList.Count - 1; i >= 0; i--) { var ob = _bullOBList[i]; if (ob.Disabled) continue; if (!ShowHistoricZones && ob.Breaker) continue; bc++; }
            for (int i = _bearOBList.Count - 1; i >= 0; i--) { var ob = _bearOBList[i]; if (ob.Disabled) continue; if (!ShowHistoricZones && ob.Breaker) continue; rc++; }
        }

        private void DrawOB(OBInfo ob) { /* no-op: rendered via SharpDX */ }
        private void RmOB(OBInfo ob) { /* no-op: no Draw objects to remove */ }
        private int GetMaxOB() { switch (ZoneCount) { case "One": return 1; case "Low": return 3; case "Medium": return 5; case "High": return 10; default: return 3; } }

        #endregion

        #region Displacement Candles

        private void ProcessDisplacementCandles()
        {
            if (!ShowDisplacement || CurrentBar < 2) return;

            double body = Math.Abs(Close[0] - Open[0]);
            double range = High[0] - Low[0];
            if (range <= 0) return;

            double bodyPct = (body / range) * 100.0;
            bool isBull = Close[0] > Open[0];
            bool isDisplacement = (range >= _atrValue * DisplacementATRMultiplier) && (bodyPct >= DisplacementMinBodyPct);

            if (isDisplacement)
            {
                _displacementRenders.Add(new DisplacementInfo { BarIndex = CurrentBar, Price = isBull ? Low[0] - TickSize * 4 : High[0] + TickSize * 4, IsBull = isBull });
            }
        }

        #endregion

        #region Equal Highs / Lows

        private void ProcessEqualLevels()
        {
            if (!ShowEqualLevels || _equalLevels == null) return;

            // Remove broken levels
            for (int i = _equalLevels.Count - 1; i >= 0; i--)
            {
                var el = _equalLevels[i];
                if (CurrentBar - el.BarIndex < SwingLength + 2) continue;
                bool broken = el.IsHigh ? (Close[0] > el.Price) : (Close[0] < el.Price);
                if (broken)
                {
                    _equalLevels.RemoveAt(i);
                }
            }

            while (_equalLevels.Count > 30)
                _equalLevels.RemoveAt(0);
        }

        #endregion

        #region Liquidity Sweeps

        private void ProcessLiquiditySweeps()
        {
            if (!ShowLiquiditySweeps) return;

            double range = High[0] - Low[0];
            if (range <= 0) return;

            double minDepth = TickSize * SweepMinDepthTicks;

            // Optional volume filter
            bool volOk = true;
            if (SweepVolumeFilter)
            {
                double avgVol = GetAverageVolume(20, 1);
                volOk = (Volume[0] >= avgVol * SweepVolumeMultiplier);
            }
            if (!volOk) return;

            // Check all strong levels for sweeps
            if (_strongLevels != null)
            {
                foreach (var sl in _strongLevels)
                {
                    if (sl.Swept) continue;
                    if (!SweepMitigatedLevels && sl.Mitigated) continue;
                    if (CurrentBar - sl.BarIndex < SwingLength + 2) continue;

                    CheckAndDrawSweep(sl);
                }
            }

            // Check equal levels for sweeps
            if (_equalLevels != null)
            {
                foreach (var el in _equalLevels)
                {
                    if (el.Swept) continue;
                    if (CurrentBar - el.BarIndex < SwingLength + 2) continue;

                    CheckAndDrawSweep(el);
                }
            }

            // Check order blocks for sweeps
            if (SweepOrderBlocks)
            {
                if (_bullOBList != null)
                {
                    foreach (var ob in _bullOBList)
                    {
                        if (ob.Swept || ob.Disabled) continue;
                        CheckAndDrawOBSweep(ob);
                    }
                }
                if (_bearOBList != null)
                {
                    foreach (var ob in _bearOBList)
                    {
                        if (ob.Swept || ob.Disabled) continue;
                        CheckAndDrawOBSweep(ob);
                    }
                }
            }
        }

        private void CheckAndDrawSweep(StrongLevel level)
        {
            double range = High[0] - Low[0];
            if (range <= 0) return;

            double minDepth = TickSize * SweepMinDepthTicks;
            bool swept = false;
            bool isBullSweep = false; // bull sweep = sweep of lows (bullish signal)

            if (level.IsHigh)
            {
                // Sweep of a high: wick above level, close below
                double sweepDepth = High[0] - level.Price;
                double upperWick = High[0] - Math.Max(Open[0], Close[0]);
                double wickPct = (upperWick / range) * 100.0;

                if (sweepDepth >= minDepth && Close[0] < level.Price && wickPct >= SweepMinWickPct)
                {
                    swept = true;
                    isBullSweep = false; // bearish sweep (swept highs)
                }
            }
            else
            {
                // Sweep of a low: wick below level, close above
                double sweepDepth = level.Price - Low[0];
                double lowerWick = Math.Min(Open[0], Close[0]) - Low[0];
                double wickPct = (lowerWick / range) * 100.0;

                if (sweepDepth >= minDepth && Close[0] > level.Price && wickPct >= SweepMinWickPct)
                {
                    swept = true;
                    isBullSweep = true; // bullish sweep (swept lows)
                }
            }

            if (swept)
            {
                level.Swept = true;
                _sweepTagCounter++;
                _sweepRenders.Add(new SweepRenderInfo { BarIndex = CurrentBar, High = High[0], Low = Low[0], IsBullSweep = isBullSweep });
                while (_sweepRenders.Count > 100)
                    _sweepRenders.RemoveAt(0);

                // Alert
                if (AlertOnSweep && State == State.Realtime)
                {
                    string dir = isBullSweep ? "Bullish" : "Bearish";
                    string lvl = level.IsHigh ? "High" : "Low";
                    Alert("RT_SWEEP", Priority.High,
                        dir + " Liquidity Sweep of " + lvl + " at " + level.Price.ToString("F2"),
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + AlertSoundSweep,
                        10, System.Windows.Media.Brushes.Transparent,
                        isBullSweep ? System.Windows.Media.Brushes.DodgerBlue : System.Windows.Media.Brushes.OrangeRed);
                }
            }
        }

        private void CheckAndDrawOBSweep(OBInfo ob)
        {
            double range = High[0] - Low[0];
            if (range <= 0) return;

            double minDepth = TickSize * SweepMinDepthTicks;
            bool swept = false;
            bool isBullSweep = false;

            if (ob.OBType == "Bull")
            {
                // Bull OB = support zone. Sweep = wick below Bottom, close above Bottom
                double sweepDepth = ob.Bottom - Low[0];
                double lowerWick = Math.Min(Open[0], Close[0]) - Low[0];
                double wickPct = (lowerWick / range) * 100.0;

                if (sweepDepth >= minDepth && Close[0] > ob.Bottom && wickPct >= SweepMinWickPct)
                {
                    swept = true;
                    isBullSweep = true; // swept support = bullish signal
                }
            }
            else
            {
                // Bear OB = resistance zone. Sweep = wick above Top, close below Top
                double sweepDepth = High[0] - ob.Top;
                double upperWick = High[0] - Math.Max(Open[0], Close[0]);
                double wickPct = (upperWick / range) * 100.0;

                if (sweepDepth >= minDepth && Close[0] < ob.Top && wickPct >= SweepMinWickPct)
                {
                    swept = true;
                    isBullSweep = false; // swept resistance = bearish signal
                }
            }

            if (swept)
            {
                ob.Swept = true;
                _sweepTagCounter++;
                _sweepRenders.Add(new SweepRenderInfo { BarIndex = CurrentBar, High = High[0], Low = Low[0], IsBullSweep = isBullSweep });
                while (_sweepRenders.Count > 100)
                    _sweepRenders.RemoveAt(0);

                if (AlertOnSweep && State == State.Realtime)
                {
                    string dir = isBullSweep ? "Bullish" : "Bearish";
                    string obType = ob.OBType + " OB";
                    double price = ob.OBType == "Bull" ? ob.Bottom : ob.Top;
                    Alert("RT_SWEEP", Priority.High,
                        dir + " Liquidity Sweep of " + obType + " at " + price.ToString("F2"),
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + AlertSoundSweep,
                        10, System.Windows.Media.Brushes.Transparent,
                        isBullSweep ? System.Windows.Media.Brushes.DodgerBlue : System.Windows.Media.Brushes.OrangeRed);
                }
            }
        }

        #endregion

        #region Helpers

        private void ManageStrongLevels()
        {
            if (!ShowStrongWeakLevels || _strongLevels == null) return;

            bool keepMode = StrongLevelMitigation.StartsWith("Keep");
            bool clearOnSession = (StrongLevelMitigation == "Keep (Fade) + Clear New Session");
            int fadedOpacity = Math.Max((int)(StrongLevelOpacity * 0.65), 5);

            // Clear mitigated levels on new session
            if (clearOnSession && Bars.IsFirstBarOfSession)
            {
                DateTime sessionDate = Time[0].Date;
                if (sessionDate != _lastSessionDate)
                {
                    _lastSessionDate = sessionDate;
                    for (int i = _strongLevels.Count - 1; i >= 0; i--)
                        if (_strongLevels[i].Mitigated) _strongLevels.RemoveAt(i);
                }
            }

            // Check for mitigation
            for (int i = _strongLevels.Count - 1; i >= 0; i--)
            {
                var sl = _strongLevels[i];
                if (CurrentBar - sl.BarIndex < SwingLength + 2) continue;
                if (sl.Mitigated) continue;

                bool broken = sl.IsHigh ? (Close[0] > sl.Price) : (Close[0] < sl.Price);
                if (broken)
                {
                    if (keepMode)
                        sl.Mitigated = true;
                    else
                        _strongLevels.RemoveAt(i);
                }
            }

            // Cap to avoid memory bloat
            while (_strongLevels.Count > 50)
                _strongLevels.RemoveAt(0);
        }

        private bool EvaluateSwingStrength(int swingBarsAgo, bool isSwingHigh)
        {
            // Scoring system: each factor adds points. Must exceed StrongLevelMinScore.
            // This allows users to tune selectivity directly.

            if (CurrentBar < swingBarsAgo + 22) return false;

            double score = 0;

            // ── Factor 1: Volume (0-40 pts) ──
            // 3-bar window around swing vs 20-bar average
            double swingVol = 0;
            int barCount = 0;
            for (int i = Math.Max(swingBarsAgo - 1, 0); i <= Math.Min(swingBarsAgo + 1, CurrentBar); i++)
            {
                swingVol += Volume[i];
                barCount++;
            }
            double avgVol = GetAverageVolume(20, swingBarsAgo + 2);
            if (avgVol > 0 && barCount > 0)
            {
                double volRatio = swingVol / (avgVol * barCount);
                // 1.0 = average → 0 pts,  1.5 = +20 pts,  2.0 = +40 pts (capped)
                score += Math.Min(40, Math.Max(0, (volRatio - 1.0) * 40.0));
            }

            // ── Factor 2: Swing bar displacement (0-30 pts) ──
            double swingRange = High[swingBarsAgo] - Low[swingBarsAgo];
            if (_atrValue > 0)
            {
                double dispRatio = swingRange / _atrValue;
                // 1.0x ATR → 0 pts,  1.5x → 15 pts,  2.0x → 30 pts (capped)
                score += Math.Min(30, Math.Max(0, (dispRatio - 1.0) * 30.0));
            }

            // ── Factor 3: Rejection move after swing (0-30 pts) ──
            // How far price moved away in the 3 bars after the swing
            int lookBars = Math.Min(3, swingBarsAgo);
            double rejectionMove = 0;

            if (isSwingHigh)
            {
                double lowestAfter = double.MaxValue;
                for (int i = swingBarsAgo - lookBars; i < swingBarsAgo; i++)
                    if (i >= 0) lowestAfter = Math.Min(lowestAfter, Low[i]);
                if (lowestAfter < double.MaxValue)
                    rejectionMove = High[swingBarsAgo] - lowestAfter;
            }
            else
            {
                double highestAfter = double.MinValue;
                for (int i = swingBarsAgo - lookBars; i < swingBarsAgo; i++)
                    if (i >= 0) highestAfter = Math.Max(highestAfter, High[i]);
                if (highestAfter > double.MinValue)
                    rejectionMove = highestAfter - Low[swingBarsAgo];
            }

            if (_atrValue > 0)
            {
                double rejRatio = rejectionMove / _atrValue;
                // 1.0x ATR → 0 pts,  2.0x → 15 pts,  3.0x → 30 pts (capped)
                score += Math.Min(30, Math.Max(0, (rejRatio - 1.0) * 15.0));
            }

            return score >= StrongLevelMinScore;
        }

        private double GetAverageVolume(int periods, int offset)
        {
            double sum = 0;
            int count = Math.Min(periods, CurrentBar - offset);
            if (count <= 0) return 1;
            for (int i = offset; i < offset + count && i <= CurrentBar; i++)
                sum += Volume[i];
            return sum / count;
        }

        private void CacheBrushes()
        {
            _bullFill = MB(BullOBColor, BullOBOpacity); _bearFill = MB(BearOBColor, BearOBOpacity);
            _bullBreakerFill = MB(BullBreakerColor, BullBreakerOpacity); _bearBreakerFill = MB(BearBreakerColor, BearBreakerOpacity);
            _cachedBOSBrush = MB(BOSColor, BOSOpacity);
            _cachedRetraceBrush = MB(HalfRetracementColor, HalfRetracementOpacity);
            _cachedStrongHighBrush = MB(StrongHighColor, StrongLevelOpacity);
            _cachedStrongLowBrush = MB(StrongLowColor, StrongLevelOpacity);
            _cachedBullDispBrush = MB(BullDisplacementColor, DisplacementMarkerOpacity);
            _cachedBearDispBrush = MB(BearDisplacementColor, DisplacementMarkerOpacity);
            _cachedEqHighBrush = MB(EqualHighColor, EqualLevelOpacity);
            _cachedEqLowBrush = MB(EqualLowColor, EqualLevelOpacity);
            _cachedBullSweepBrush = MB(BullSweepColor, SweepOpacity);
            _cachedBearSweepBrush = MB(BearSweepColor, SweepOpacity);
            _transparentBrush = System.Windows.Media.Brushes.Transparent; _brushesCached = true;
        }

        private double[] GetSmoothedVolumes(List<double> rawVolumes, int passes)
        {
            if (rawVolumes == null || rawVolumes.Count == 0) return new double[0];
            double[] current = rawVolumes.ToArray();
            double[] buffer = new double[current.Length];
            for (int pass = 0; pass < passes; pass++)
            {
                for (int i = 0; i < current.Length; i++)
                {
                    double sum = current[i] * 4.0; double weightSum = 4.0;
                    if (i - 1 >= 0) { sum += current[i - 1] * 2.0; weightSum += 2.0; }
                    if (i + 1 < current.Length) { sum += current[i + 1] * 2.0; weightSum += 2.0; }
                    if (i - 2 >= 0) { sum += current[i - 2] * 1.0; weightSum += 1.0; }
                    if (i + 2 < current.Length) { sum += current[i + 2] * 1.0; weightSum += 1.0; }
                    buffer[i] = sum / weightSum;
                }
                double[] temp = current; current = buffer; buffer = temp;
            }
            return current;
        }

        private float CalcAdaptiveBarThickness(ChartScale cs, double lowPrice, double highPrice, int rowCount)
        {
            float lowY = cs.GetYByValue(lowPrice);
            float highY = cs.GetYByValue(highPrice);
            float totalPixelHeight = Math.Abs(lowY - highY);
            float pixelsPerRow = totalPixelHeight / Math.Max(1, rowCount);
            float idealThickness = pixelsPerRow * 0.85f;
            return Math.Max(FRVPMinBarPixelHeight, Math.Min(idealThickness, FRVPMaxBarPixelHeight));
        }

        private SharpDX.Direct2D1.LinearGradientBrush CreateGradientBrush(SharpDX.Direct2D1.RenderTarget rt, System.Windows.Media.Brush baseColor, float startX, float endX, float y, float baseOpacity)
        {
            if (!FRVPEnableGradientFill || FRVPGradientIntensity <= 0) return null;
            try
            {
                System.Windows.Media.Color mediaColor;
                if (baseColor is System.Windows.Media.SolidColorBrush solidBrush) mediaColor = solidBrush.Color;
                else mediaColor = System.Windows.Media.Colors.Gray;
                float intensityFactor = FRVPGradientIntensity / 100.0f;
                float startOpacity = baseOpacity * (1.0f - intensityFactor);
                float endOpacity = baseOpacity;
                var gradientStops = new SharpDX.Direct2D1.GradientStop[2];
                gradientStops[0] = new SharpDX.Direct2D1.GradientStop { Position = 0.0f, Color = new SharpDX.Color4(mediaColor.R / 255f, mediaColor.G / 255f, mediaColor.B / 255f, startOpacity) };
                gradientStops[1] = new SharpDX.Direct2D1.GradientStop { Position = 1.0f, Color = new SharpDX.Color4(mediaColor.R / 255f, mediaColor.G / 255f, mediaColor.B / 255f, endOpacity) };
                var gsc = new SharpDX.Direct2D1.GradientStopCollection(rt, gradientStops);
                var gb = new SharpDX.Direct2D1.LinearGradientBrush(rt, new SharpDX.Direct2D1.LinearGradientBrushProperties { StartPoint = new SharpDX.Vector2(startX, y), EndPoint = new SharpDX.Vector2(endX, y) }, gsc);
                gsc.Dispose();
                return gb;
            }
            catch { return null; }
        }
        private System.Windows.Media.Brush MB(System.Windows.Media.Brush src, int op) { if (src is System.Windows.Media.SolidColorBrush s) { var b = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(255 * Math.Min(op, 100) / 100), s.Color.R, s.Color.G, s.Color.B)); b.Freeze(); return b; } return src; }
        private DashStyleHelper GetDS(string s) { switch (s) { case "Dashed": return DashStyleHelper.Dash; case "Dotted": return DashStyleHelper.Dot; default: return DashStyleHelper.Solid; } }
        private SharpDX.Color4 B2C4(System.Windows.Media.Brush b, float op) { if (b is System.Windows.Media.SolidColorBrush s) { var c = s.Color; return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, op); } return new SharpDX.Color4(1, 1, 1, op); }
        private SharpDX.Direct2D1.StrokeStyle MakeSS(SharpDX.Direct2D1.RenderTarget rt, DashStyleHelper ds)
        {
            switch (ds)
            {
                case DashStyleHelper.Dash: return _ssDash ?? _ssSolid;
                case DashStyleHelper.Dot: return _ssDot ?? _ssSolid;
                case DashStyleHelper.DashDot: return _ssDashDot ?? _ssSolid;
                default: return _ssSolid;
            }
        }
        private void DL(SharpDX.Direct2D1.RenderTarget rt, SharpDX.DirectWrite.TextFormat fmt, SharpDX.Direct2D1.SolidColorBrush bg, SharpDX.Color4 tc, string txt, float x, float y)
        { try { using (var tl = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, txt, fmt, 400, 20)) { float tw = tl.Metrics.Width, th = tl.Metrics.Height; rt.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = new SharpDX.RectangleF(x + 2, y - th - 2, tw + 8, th + 4), RadiusX = 2, RadiusY = 2 }, bg); using (var tb = new SharpDX.Direct2D1.SolidColorBrush(rt, tc)) rt.DrawTextLayout(new SharpDX.Vector2(x + 6, y - th - 1), tl, tb); } } catch { } }

        #endregion

        #region Properties

        // ══════════════════════════════════════════════════════════════
        // 1. MARKET STRUCTURE
        // ══════════════════════════════════════════════════════════════

        [NinjaScriptProperty][Range(3, 100)]
        [Display(Name = "Swing Length", Order = 1, GroupName = "01. Market Structure")]
        public int SwingLength { get; set; }

        [NinjaScriptProperty][TypeConverter(typeof(BOSConfirmationConverter))]
        [Display(Name = "BOS Confirmation", Order = 2, GroupName = "01. Market Structure")]
        public string BOSConfirmation { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show CHoCH", Order = 3, GroupName = "01. Market Structure")]
        public bool ShowCHoCH { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Swing Labels", Order = 4, GroupName = "01. Market Structure")]
        public bool ShowSwingLabels { get; set; }

        [XmlIgnore]
        [Display(Name = "Swing Label Color", Order = 5, GroupName = "01. Market Structure")]
        public System.Windows.Media.Brush SwingLabelColor { get; set; }
        [Browsable(false)] public string SwingLabelColorS { get { return Serialize.BrushToString(SwingLabelColor); } set { SwingLabelColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "BOS / CHoCH Color", Order = 6, GroupName = "01. Market Structure")]
        public System.Windows.Media.Brush BOSColor { get; set; }
        [Browsable(false)] public string BOSColorS { get { return Serialize.BrushToString(BOSColor); } set { BOSColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty][TypeConverter(typeof(LineStyleConverter))]
        [Display(Name = "BOS / CHoCH Style", Order = 7, GroupName = "01. Market Structure")]
        public string BOSStyle { get; set; }

        [NinjaScriptProperty][Range(1, 5)]
        [Display(Name = "BOS / CHoCH Width", Order = 8, GroupName = "01. Market Structure")]
        public int BOSWidth { get; set; }

        [Display(Name = "Swing Label Font Size", Order = 9, GroupName = "01. Market Structure")][Range(6, 20)]
        public int SwingLabelFontSize { get; set; }

        [Display(Name = "BOS / CHoCH Label Font Size", Order = 10, GroupName = "01. Market Structure")][Range(6, 20)]
        public int BOSLabelFontSize { get; set; }

        [Display(Name = "BOS / CHoCH Opacity %", Order = 11, GroupName = "01. Market Structure")][Range(10, 100)]
        public int BOSOpacity { get; set; }

        [Display(Name = "Show Trend Display", Order = 12, GroupName = "01. Market Structure")]
        public bool ShowTrendDisplay { get; set; }

        [Display(Name = "Trend Display Font Size", Order = 13, GroupName = "01. Market Structure")][Range(8, 24)]
        public int TrendDisplayFontSize { get; set; }

        [NinjaScriptProperty][TypeConverter(typeof(TrendPositionConverter))]
        [Display(Name = "Trend Display Position", Order = 14, GroupName = "01. Market Structure")]
        public string TrendDisplayPosition { get; set; }

        [Display(Name = "Trend Display X Offset", Order = 15, GroupName = "01. Market Structure")][Range(0, 500)]
        public int TrendDisplayOffsetX { get; set; }

        [Display(Name = "Trend Display Y Offset", Order = 16, GroupName = "01. Market Structure")][Range(0, 500)]
        public int TrendDisplayOffsetY { get; set; }

        [XmlIgnore]
        [Display(Name = "Trend Bullish Color", Order = 17, GroupName = "01. Market Structure")]
        public System.Windows.Media.Brush TrendBullColor { get; set; }
        [Browsable(false)] public string TrendBullColorS { get { return Serialize.BrushToString(TrendBullColor); } set { TrendBullColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Trend Bearish Color", Order = 18, GroupName = "01. Market Structure")]
        public System.Windows.Media.Brush TrendBearColor { get; set; }
        [Browsable(false)] public string TrendBearColorS { get { return Serialize.BrushToString(TrendBearColor); } set { TrendBearColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Trend Warning Color", Description = "Color when opposing CHoCH fires", Order = 19, GroupName = "01. Market Structure")]
        public System.Windows.Media.Brush TrendWarningColor { get; set; }
        [Browsable(false)] public string TrendWarningColorS { get { return Serialize.BrushToString(TrendWarningColor); } set { TrendWarningColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Trend Neutral Color", Description = "Color when no trend established", Order = 20, GroupName = "01. Market Structure")]
        public System.Windows.Media.Brush TrendNeutralColor { get; set; }
        [Browsable(false)] public string TrendNeutralColorS { get { return Serialize.BrushToString(TrendNeutralColor); } set { TrendNeutralColor = Serialize.StringToBrush(value); } }

        // ══════════════════════════════════════════════════════════════
        // 2. 0.5 RETRACEMENT
        // ══════════════════════════════════════════════════════════════

        [NinjaScriptProperty]
        [Display(Name = "Show 0.5 Retracement", Order = 1, GroupName = "02. Retracement")]
        public bool ShowHalfRetracement { get; set; }

        [XmlIgnore]
        [Display(Name = "Color", Order = 2, GroupName = "02. Retracement")]
        public System.Windows.Media.Brush HalfRetracementColor { get; set; }
        [Browsable(false)] public string HalfRetracementColorS { get { return Serialize.BrushToString(HalfRetracementColor); } set { HalfRetracementColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty][TypeConverter(typeof(LineStyleConverter))]
        [Display(Name = "Style", Order = 3, GroupName = "02. Retracement")]
        public string HalfRetracementStyle { get; set; }

        [NinjaScriptProperty][Range(1, 5)]
        [Display(Name = "Width", Order = 4, GroupName = "02. Retracement")]
        public int HalfRetracementWidth { get; set; }

        [Display(Name = "Opacity %", Order = 5, GroupName = "02. Retracement")][Range(10, 100)]
        public int HalfRetracementOpacity { get; set; }

        // ══════════════════════════════════════════════════════════════
        // 3. STRONG / WEAK LEVELS
        // ══════════════════════════════════════════════════════════════

        [NinjaScriptProperty]
        [Display(Name = "Show Strong/Weak Levels", Description = "Label swings and draw rays for unmitigated strong levels", Order = 1, GroupName = "03. Strong/Weak Levels")]
        public bool ShowStrongWeakLevels { get; set; }

        [NinjaScriptProperty][Range(0.1, 5.0)]
        [Display(Name = "Volume Multiplier", Description = "Swing volume >= avg * multiplier = Strong", Order = 2, GroupName = "03. Strong/Weak Levels")]
        public double StrongLevelVolumeMultiplier { get; set; }

        [NinjaScriptProperty][Range(5, 80)]
        [Display(Name = "Min Strength Score", Description = "0-100 composite score threshold (volume + displacement + rejection). Lower = more strong levels, higher = fewer.", Order = 3, GroupName = "03. Strong/Weak Levels")]
        public int StrongLevelMinScore { get; set; }

        [XmlIgnore]
        [Display(Name = "Strong High Color", Order = 4, GroupName = "03. Strong/Weak Levels")]
        public System.Windows.Media.Brush StrongHighColor { get; set; }
        [Browsable(false)] public string StrongHighColorS { get { return Serialize.BrushToString(StrongHighColor); } set { StrongHighColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Strong Low Color", Order = 5, GroupName = "03. Strong/Weak Levels")]
        public System.Windows.Media.Brush StrongLowColor { get; set; }
        [Browsable(false)] public string StrongLowColorS { get { return Serialize.BrushToString(StrongLowColor); } set { StrongLowColor = Serialize.StringToBrush(value); } }

        [Display(Name = "Line Width", Order = 6, GroupName = "03. Strong/Weak Levels")][Range(1, 5)]
        public int StrongLevelWidth { get; set; }

        [Display(Name = "Line Style", Order = 7, GroupName = "03. Strong/Weak Levels")]
        public DashStyleHelper StrongLevelStyle { get; set; }

        [Display(Name = "Label Font Size", Order = 8, GroupName = "03. Strong/Weak Levels")][Range(6, 20)]
        public int StrongLevelFontSize { get; set; }

        [Display(Name = "Line Opacity %", Order = 9, GroupName = "03. Strong/Weak Levels")][Range(10, 100)]
        public int StrongLevelOpacity { get; set; }

        [NinjaScriptProperty][TypeConverter(typeof(StrongLevelMitigationConverter))]
        [Display(Name = "When Mitigated", Description = "Remove level or keep with faded appearance", Order = 10, GroupName = "03. Strong/Weak Levels")]
        public string StrongLevelMitigation { get; set; }

        // ══════════════════════════════════════════════════════════════
        // 4. ORDER BLOCKS
        // ══════════════════════════════════════════════════════════════

        [NinjaScriptProperty][Range(3, 50)]
        [Display(Name = "OB Swing Length", Order = 1, GroupName = "04. Order Blocks")]
        public int OBSwingLength { get; set; }

        [NinjaScriptProperty][Range(0.5, 20.0)]
        [Display(Name = "Max ATR Multiplier", Order = 2, GroupName = "04. Order Blocks")]
        public double MaxATRMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Historic Zones", Order = 3, GroupName = "04. Order Blocks")]
        public bool ShowHistoricZones { get; set; }

        [NinjaScriptProperty][TypeConverter(typeof(ZoneInvalidationConverter))]
        [Display(Name = "Zone Invalidation", Order = 4, GroupName = "04. Order Blocks")]
        public string ZoneInvalidation { get; set; }

        [NinjaScriptProperty][TypeConverter(typeof(OBDrawStyleConverter))]
        [Display(Name = "OB Draw Style", Description = "Full Range draws the full candle. Wick Only draws just the rejection wick (wick extreme to nearest body edge).", Order = 5, GroupName = "04. Order Blocks")]
        public string OBDrawStyle { get; set; }

        [NinjaScriptProperty][TypeConverter(typeof(ZoneCountConverter))]
        [Display(Name = "Zone Count", Order = 6, GroupName = "04. Order Blocks")]
        public string ZoneCount { get; set; }

        [NinjaScriptProperty][Range(1, 100)]
        [Display(Name = "Box Extend Bars", Order = 7, GroupName = "04. Order Blocks")]
        public int BoxExtendBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Delete Broken Boxes", Order = 8, GroupName = "04. Order Blocks")]
        public bool DeleteBrokenBoxes { get; set; }

        [XmlIgnore]
        [Display(Name = "Bullish OB Color", Order = 8, GroupName = "04. Order Blocks")]
        public System.Windows.Media.Brush BullOBColor { get; set; }
        [Browsable(false)] public string BullOBColorS { get { return Serialize.BrushToString(BullOBColor); } set { BullOBColor = Serialize.StringToBrush(value); } }

        [Display(Name = "Bullish OB Opacity %", Order = 11, GroupName = "04. Order Blocks")][Range(0, 100)]
        public int BullOBOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Bearish OB Color", Order = 12, GroupName = "04. Order Blocks")]
        public System.Windows.Media.Brush BearOBColor { get; set; }
        [Browsable(false)] public string BearOBColorS { get { return Serialize.BrushToString(BearOBColor); } set { BearOBColor = Serialize.StringToBrush(value); } }

        [Display(Name = "Bearish OB Opacity %", Order = 13, GroupName = "04. Order Blocks")][Range(0, 100)]
        public int BearOBOpacity { get; set; }

        [Display(Name = "Border Opacity %", Order = 13, GroupName = "04. Order Blocks")][Range(10, 100)]
        public int OBBorderOpacity { get; set; }

        [Display(Name = "Convert to Breaker Block", Description = "When an OB is broken, convert it to a breaker block that persists with changed color instead of being removed.", Order = 14, GroupName = "04. Order Blocks")]
        public bool ConvertToBreaker { get; set; }

        [XmlIgnore]
        [Display(Name = "Bull Breaker Color", Description = "Color for broken bullish OBs (now bearish resistance breakers).", Order = 15, GroupName = "04. Order Blocks")]
        public System.Windows.Media.Brush BullBreakerColor { get; set; }
        [Browsable(false)] public string BullBreakerColorS { get { return Serialize.BrushToString(BullBreakerColor); } set { BullBreakerColor = Serialize.StringToBrush(value); } }

        [Display(Name = "Bull Breaker Opacity %", Order = 16, GroupName = "04. Order Blocks")][Range(0, 100)]
        public int BullBreakerOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Bear Breaker Color", Description = "Color for broken bearish OBs (now bullish support breakers).", Order = 17, GroupName = "04. Order Blocks")]
        public System.Windows.Media.Brush BearBreakerColor { get; set; }
        [Browsable(false)] public string BearBreakerColorS { get { return Serialize.BrushToString(BearBreakerColor); } set { BearBreakerColor = Serialize.StringToBrush(value); } }

        [Display(Name = "Bear Breaker Opacity %", Order = 18, GroupName = "04. Order Blocks")][Range(0, 100)]
        public int BearBreakerOpacity { get; set; }

        // ══════════════════════════════════════════════════════════════
        // 5. FRVP — VOLUME PROFILE
        // ══════════════════════════════════════════════════════════════

        [NinjaScriptProperty]
        [Display(Name = "Enable FRVP", Order = 1, GroupName = "05. FRVP Volume Profile")]
        public bool EnableFRVP { get; set; }

        [NinjaScriptProperty][TypeConverter(typeof(FRVPTriggerConverter))]
        [Display(Name = "FRVP Trigger", Description = "Draw on CHoCH immediately, or wait for confirming BOS", Order = 2, GroupName = "05. FRVP Volume Profile")]
        public string FRVPTrigger { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Keep Previous FRVP", Order = 3, GroupName = "05. FRVP Volume Profile")]
        public bool KeepPreviousFRVP { get; set; }

        [NinjaScriptProperty][Range(50, 500)]
        [Display(Name = "Number of Rows", Order = 4, GroupName = "05. FRVP Volume Profile")]
        public int FRVPRows { get; set; }

        [NinjaScriptProperty][Range(5, 80)]
        [Display(Name = "Profile Width %", Order = 5, GroupName = "05. FRVP Volume Profile")]
        public int FRVPProfileWidth { get; set; }

        [NinjaScriptProperty][TypeConverter(typeof(VPAlignmentConverter))]
        [Display(Name = "VP Alignment", Order = 6, GroupName = "05. FRVP Volume Profile")]
        public string FRVPVPAlignment { get; set; }

        [XmlIgnore]
        [Display(Name = "Bar Color", Order = 7, GroupName = "05. FRVP Volume Profile")]
        public System.Windows.Media.Brush FRVPBarColor { get; set; }
        [Browsable(false)] public string FRVPBarColorS { get { return Serialize.BrushToString(FRVPBarColor); } set { FRVPBarColor = Serialize.StringToBrush(value); } }

        [Display(Name = "Bar Opacity %", Order = 8, GroupName = "05. FRVP Volume Profile")][Range(10, 100)]
        public int FRVPBarOpacity { get; set; }

        [Display(Name = "Bar Thickness", Order = 9, GroupName = "05. FRVP Volume Profile")][Range(1, 10)]
        public int FRVPBarThickness { get; set; }

        [Display(Name = "Volume Type", Description = "Standard, Bullish, Bearish, or Both (polarity coloring)", Order = 10, GroupName = "05. FRVP Volume Profile")]
        public MSVolumeType FRVPVolumeType { get; set; }

        [XmlIgnore]
        [Display(Name = "Bullish Bar Color", Order = 11, GroupName = "05. FRVP Volume Profile")]
        public System.Windows.Media.Brush FRVPBullishBarColor { get; set; }
        [Browsable(false)] public string FRVPBullishBarColorS { get { return Serialize.BrushToString(FRVPBullishBarColor); } set { FRVPBullishBarColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Bearish Bar Color", Order = 12, GroupName = "05. FRVP Volume Profile")]
        public System.Windows.Media.Brush FRVPBearishBarColor { get; set; }
        [Browsable(false)] public string FRVPBearishBarColorS { get { return Serialize.BrushToString(FRVPBearishBarColor); } set { FRVPBearishBarColor = Serialize.StringToBrush(value); } }

        [Display(Name = "Enable Gradient Fill", Order = 13, GroupName = "05. FRVP Volume Profile")]
        public bool FRVPEnableGradientFill { get; set; }

        [Display(Name = "Gradient Intensity %", Order = 14, GroupName = "05. FRVP Volume Profile")][Range(10, 100)]
        public int FRVPGradientIntensity { get; set; }

        [Display(Name = "Render Quality", Description = "Adaptive auto-sizes bars; Manual uses fixed thickness", Order = 15, GroupName = "05. FRVP Volume Profile")]
        public MSRenderQuality FRVPRenderQuality { get; set; }

        [Display(Name = "Smoothing Passes", Description = "Volume smoothing passes (0=none)", Order = 16, GroupName = "05. FRVP Volume Profile")][Range(0, 5)]
        public int FRVPSmoothingPasses { get; set; }

        [Display(Name = "Min Bar Pixel Height", Order = 17, GroupName = "05. FRVP Volume Profile")][Range(0.5f, 10.0f)]
        public float FRVPMinBarPixelHeight { get; set; }

        [Display(Name = "Max Bar Pixel Height", Order = 18, GroupName = "05. FRVP Volume Profile")][Range(2.0f, 20.0f)]
        public float FRVPMaxBarPixelHeight { get; set; }

        [XmlIgnore]
        [Display(Name = "Boundary Color", Order = 20, GroupName = "05. FRVP Volume Profile")]
        public System.Windows.Media.Brush FRVPBoundaryColor { get; set; }
        [Browsable(false)] public string FRVPBoundaryColorS { get { return Serialize.BrushToString(FRVPBoundaryColor); } set { FRVPBoundaryColor = Serialize.StringToBrush(value); } }

        [Display(Name = "Boundary Opacity %", Order = 21, GroupName = "05. FRVP Volume Profile")][Range(0, 100)]
        public int FRVPBoundaryOpacity { get; set; }

        [Display(Name = "Boundary Width", Order = 22, GroupName = "05. FRVP Volume Profile")][Range(0, 5)]
        public int FRVPBoundaryWidth { get; set; }

        [Display(Name = "Show Labels (POC/VA)", Order = 23, GroupName = "05. FRVP Volume Profile")]
        public bool FRVPShowLabels { get; set; }

        [Display(Name = "Label Font Size", Order = 24, GroupName = "05. FRVP Volume Profile")][Range(8, 20)]
        public int FRVPLabelFontSize { get; set; }

        [Display(Name = "Show Price on Labels", Order = 25, GroupName = "05. FRVP Volume Profile")]
        public bool FRVPShowPrice { get; set; }

        // ══════════════════════════════════════════════════════════════
        // 6. FRVP — POC & VALUE AREA
        // ══════════════════════════════════════════════════════════════

        [Display(Name = "Display POC", Order = 1, GroupName = "06. FRVP POC & Value Area")]
        public bool FRVPDisplayPoC { get; set; }

        [XmlIgnore]
        [Display(Name = "POC Color", Order = 2, GroupName = "06. FRVP POC & Value Area")]
        public System.Windows.Media.Brush FRVPPoCColor { get; set; }
        [Browsable(false)] public string FRVPPoCColorS { get { return Serialize.BrushToString(FRVPPoCColor); } set { FRVPPoCColor = Serialize.StringToBrush(value); } }

        [Display(Name = "POC Width", Order = 3, GroupName = "06. FRVP POC & Value Area")][Range(1, 5)]
        public int FRVPPoCWidth { get; set; }

        [Display(Name = "POC Style", Order = 4, GroupName = "06. FRVP POC & Value Area")]
        public DashStyleHelper FRVPPoCStyle { get; set; }

        [Display(Name = "POC Opacity %", Order = 5, GroupName = "06. FRVP POC & Value Area")][Range(10, 100)]
        public int FRVPPoCOpacity { get; set; }

        [Display(Name = "Display Value Area", Order = 6, GroupName = "06. FRVP POC & Value Area")]
        public bool FRVPDisplayVA { get; set; }

        [Display(Name = "Value Area %", Order = 7, GroupName = "06. FRVP POC & Value Area")][Range(50, 95)]
        public int FRVPValueAreaPct { get; set; }

        [XmlIgnore]
        [Display(Name = "VA Bar Color", Order = 8, GroupName = "06. FRVP POC & Value Area")]
        public System.Windows.Media.Brush FRVPVABarColor { get; set; }
        [Browsable(false)] public string FRVPVABarColorS { get { return Serialize.BrushToString(FRVPVABarColor); } set { FRVPVABarColor = Serialize.StringToBrush(value); } }

        [Display(Name = "Display VA Lines", Order = 9, GroupName = "06. FRVP POC & Value Area")]
        public bool FRVPDisplayVALines { get; set; }

        [XmlIgnore]
        [Display(Name = "VA Line Color", Order = 10, GroupName = "06. FRVP POC & Value Area")]
        public System.Windows.Media.Brush FRVPVALineColor { get; set; }
        [Browsable(false)] public string FRVPVALineColorS { get { return Serialize.BrushToString(FRVPVALineColor); } set { FRVPVALineColor = Serialize.StringToBrush(value); } }

        [Display(Name = "VA Line Width", Order = 11, GroupName = "06. FRVP POC & Value Area")][Range(1, 5)]
        public int FRVPVALineWidth { get; set; }

        [Display(Name = "VA Line Style", Order = 12, GroupName = "06. FRVP POC & Value Area")]
        public DashStyleHelper FRVPVALineStyle { get; set; }

        [Display(Name = "VA Line Opacity %", Order = 13, GroupName = "06. FRVP POC & Value Area")][Range(10, 100)]
        public int FRVPVALineOpacity { get; set; }

        [Display(Name = "Extend POC/VA Right", Description = "Extend POC and VA lines beyond the FRVP zone to chart edge", Order = 14, GroupName = "06. FRVP POC & Value Area")]
        public bool FRVPExtendPoCVA { get; set; }

        // ══════════════════════════════════════════════════════════════
        // 7. FRVP — FIBONACCI
        // ══════════════════════════════════════════════════════════════

        [Display(Name = "Display Fibs", Order = 1, GroupName = "07. FRVP Fibonacci")]
        public bool FRVPDisplayFibs { get; set; }

        [Display(Name = "Fib Line Width", Order = 2, GroupName = "07. FRVP Fibonacci")][Range(1, 5)]
        public int FRVPFibLineWidth { get; set; }

        [Display(Name = "Fib Style", Order = 3, GroupName = "07. FRVP Fibonacci")]
        public DashStyleHelper FRVPFibStyle { get; set; }

        [Display(Name = "Fib Opacity %", Order = 4, GroupName = "07. FRVP Fibonacci")][Range(10, 100)]
        public int FRVPFibOpacity { get; set; }

        [Display(Name = "Extend Fibs Right", Order = 5, GroupName = "07. FRVP Fibonacci")]
        public bool FRVPExtendFibs { get; set; }

        [Display(Name = "Fib Label Size", Order = 6, GroupName = "07. FRVP Fibonacci")][Range(8, 20)]
        public int FRVPFibLabelSize { get; set; }

        [Display(Name = "Show Fib Price", Order = 7, GroupName = "07. FRVP Fibonacci")]
        public bool FRVPFibShowPrice { get; set; }

        // ══════════════════════════════════════════════════════════════
        // 8. FRVP — FIB LEVELS
        // ══════════════════════════════════════════════════════════════

        [Display(Name = "Level 1 (%)", Description = "-1 to disable", Order = 1, GroupName = "08. Fib Levels")]
        public double FibLevel1 { get; set; }
        [XmlIgnore][Display(Name = "Level 1 Color", Order = 2, GroupName = "08. Fib Levels")]
        public System.Windows.Media.Brush FibLevel1Color { get; set; }
        [Browsable(false)] public string FL1CS { get { return Serialize.BrushToString(FibLevel1Color); } set { FibLevel1Color = Serialize.StringToBrush(value); } }

        [Display(Name = "Level 2 (%)", Order = 3, GroupName = "08. Fib Levels")]
        public double FibLevel2 { get; set; }
        [XmlIgnore][Display(Name = "Level 2 Color", Order = 4, GroupName = "08. Fib Levels")]
        public System.Windows.Media.Brush FibLevel2Color { get; set; }
        [Browsable(false)] public string FL2CS { get { return Serialize.BrushToString(FibLevel2Color); } set { FibLevel2Color = Serialize.StringToBrush(value); } }

        [Display(Name = "Level 3 (%)", Order = 5, GroupName = "08. Fib Levels")]
        public double FibLevel3 { get; set; }
        [XmlIgnore][Display(Name = "Level 3 Color", Order = 6, GroupName = "08. Fib Levels")]
        public System.Windows.Media.Brush FibLevel3Color { get; set; }
        [Browsable(false)] public string FL3CS { get { return Serialize.BrushToString(FibLevel3Color); } set { FibLevel3Color = Serialize.StringToBrush(value); } }

        [Display(Name = "Level 4 (%)", Order = 7, GroupName = "08. Fib Levels")]
        public double FibLevel4 { get; set; }
        [XmlIgnore][Display(Name = "Level 4 Color", Order = 8, GroupName = "08. Fib Levels")]
        public System.Windows.Media.Brush FibLevel4Color { get; set; }
        [Browsable(false)] public string FL4CS { get { return Serialize.BrushToString(FibLevel4Color); } set { FibLevel4Color = Serialize.StringToBrush(value); } }

        [Display(Name = "Level 5 (%)", Order = 9, GroupName = "08. Fib Levels")]
        public double FibLevel5 { get; set; }
        [XmlIgnore][Display(Name = "Level 5 Color", Order = 10, GroupName = "08. Fib Levels")]
        public System.Windows.Media.Brush FibLevel5Color { get; set; }
        [Browsable(false)] public string FL5CS { get { return Serialize.BrushToString(FibLevel5Color); } set { FibLevel5Color = Serialize.StringToBrush(value); } }

        [Display(Name = "Level 6 (%)", Order = 11, GroupName = "08. Fib Levels")]
        public double FibLevel6 { get; set; }
        [XmlIgnore][Display(Name = "Level 6 Color", Order = 12, GroupName = "08. Fib Levels")]
        public System.Windows.Media.Brush FibLevel6Color { get; set; }
        [Browsable(false)] public string FL6CS { get { return Serialize.BrushToString(FibLevel6Color); } set { FibLevel6Color = Serialize.StringToBrush(value); } }

        [Display(Name = "Level 7 (%)", Order = 13, GroupName = "08. Fib Levels")]
        public double FibLevel7 { get; set; }
        [XmlIgnore][Display(Name = "Level 7 Color", Order = 14, GroupName = "08. Fib Levels")]
        public System.Windows.Media.Brush FibLevel7Color { get; set; }
        [Browsable(false)] public string FL7CS { get { return Serialize.BrushToString(FibLevel7Color); } set { FibLevel7Color = Serialize.StringToBrush(value); } }

        [Display(Name = "Level 8 (%)", Order = 15, GroupName = "08. Fib Levels")]
        public double FibLevel8 { get; set; }
        [XmlIgnore][Display(Name = "Level 8 Color", Order = 16, GroupName = "08. Fib Levels")]
        public System.Windows.Media.Brush FibLevel8Color { get; set; }
        [Browsable(false)] public string FL8CS { get { return Serialize.BrushToString(FibLevel8Color); } set { FibLevel8Color = Serialize.StringToBrush(value); } }

        [Display(Name = "Level 9 (%)", Order = 17, GroupName = "08. Fib Levels")]
        public double FibLevel9 { get; set; }
        [XmlIgnore][Display(Name = "Level 9 Color", Order = 18, GroupName = "08. Fib Levels")]
        public System.Windows.Media.Brush FibLevel9Color { get; set; }
        [Browsable(false)] public string FL9CS { get { return Serialize.BrushToString(FibLevel9Color); } set { FibLevel9Color = Serialize.StringToBrush(value); } }

        [Display(Name = "Level 10 (%)", Order = 19, GroupName = "08. Fib Levels")]
        public double FibLevel10 { get; set; }
        [XmlIgnore][Display(Name = "Level 10 Color", Order = 20, GroupName = "08. Fib Levels")]
        public System.Windows.Media.Brush FibLevel10Color { get; set; }
        [Browsable(false)] public string FL10CS { get { return Serialize.BrushToString(FibLevel10Color); } set { FibLevel10Color = Serialize.StringToBrush(value); } }

        // ══════════════════════════════════════════════════════════════
        // 9. FRVP — AVWAP
        // ══════════════════════════════════════════════════════════════

        [Display(Name = "Display AVWAP", Order = 1, GroupName = "09. FRVP AVWAP")]
        public bool FRVPDisplayAVWAP { get; set; }

        [XmlIgnore]
        [Display(Name = "AVWAP Color", Order = 2, GroupName = "09. FRVP AVWAP")]
        public System.Windows.Media.Brush FRVPAVWAPColor { get; set; }
        [Browsable(false)] public string FRVPAVWAPColorS { get { return Serialize.BrushToString(FRVPAVWAPColor); } set { FRVPAVWAPColor = Serialize.StringToBrush(value); } }

        [Display(Name = "AVWAP Width", Order = 3, GroupName = "09. FRVP AVWAP")][Range(1, 5)]
        public int FRVPAVWAPWidth { get; set; }

        [Display(Name = "AVWAP Style", Order = 4, GroupName = "09. FRVP AVWAP")]
        public DashStyleHelper FRVPAVWAPStyle { get; set; }

        [Display(Name = "AVWAP Opacity %", Order = 5, GroupName = "09. FRVP AVWAP")][Range(10, 100)]
        public int FRVPAVWAPOpacity { get; set; }

        [Display(Name = "Extend AVWAP Right", Order = 6, GroupName = "09. FRVP AVWAP")]
        public bool FRVPExtendAVWAP { get; set; }

        [Display(Name = "Show AVWAP Label", Order = 7, GroupName = "09. FRVP AVWAP")]
        public bool FRVPShowAVWAPLabel { get; set; }

        // ══════════════════════════════════════════════════════════════
        // 10. FRVP CLUSTER LEVELS
        // ══════════════════════════════════════════════════════════════

        [Display(Name = "Display Cluster Levels", Order = 1, GroupName = "10. FRVP Cluster Levels")]
        public bool FRVPDisplayClusters { get; set; }

        [Display(Name = "Number of Clusters", Description = "K-Means cluster count", Order = 2, GroupName = "10. FRVP Cluster Levels")][Range(2, 10)]
        public int FRVPClusterCount { get; set; }

        [Display(Name = "K-Means Iterations", Order = 3, GroupName = "10. FRVP Cluster Levels")][Range(5, 50)]
        public int FRVPClusterIterations { get; set; }

        [Display(Name = "Rows per Cluster", Description = "VP resolution per cluster for POC detection", Order = 4, GroupName = "10. FRVP Cluster Levels")][Range(5, 100)]
        public int FRVPClusterRowsPerLevel { get; set; }

        [Display(Name = "Line Width", Order = 5, GroupName = "10. FRVP Cluster Levels")][Range(1, 5)]
        public int FRVPClusterLineWidth { get; set; }

        [Display(Name = "Line Style", Order = 6, GroupName = "10. FRVP Cluster Levels")]
        public DashStyleHelper FRVPClusterLineStyle { get; set; }

        [Display(Name = "Opacity %", Order = 7, GroupName = "10. FRVP Cluster Levels")][Range(10, 100)]
        public int FRVPClusterOpacity { get; set; }

        [Display(Name = "Extend Right", Order = 8, GroupName = "10. FRVP Cluster Levels")]
        public bool FRVPExtendClusters { get; set; }

        [Display(Name = "Show Labels", Order = 9, GroupName = "10. FRVP Cluster Levels")]
        public bool FRVPShowClusterLabels { get; set; }

        // ══════════════════════════════════════════════════════════════
        // 11. CLUSTER COLORS
        // ══════════════════════════════════════════════════════════════

        [XmlIgnore][Display(Name = "Cluster 1 Color", Order = 1, GroupName = "11. Cluster Colors")]
        public System.Windows.Media.Brush FRVPCluster1Color { get; set; }
        [Browsable(false)] public string FRVPCluster1ColorS { get { return Serialize.BrushToString(FRVPCluster1Color); } set { FRVPCluster1Color = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Cluster 2 Color", Order = 2, GroupName = "11. Cluster Colors")]
        public System.Windows.Media.Brush FRVPCluster2Color { get; set; }
        [Browsable(false)] public string FRVPCluster2ColorS { get { return Serialize.BrushToString(FRVPCluster2Color); } set { FRVPCluster2Color = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Cluster 3 Color", Order = 3, GroupName = "11. Cluster Colors")]
        public System.Windows.Media.Brush FRVPCluster3Color { get; set; }
        [Browsable(false)] public string FRVPCluster3ColorS { get { return Serialize.BrushToString(FRVPCluster3Color); } set { FRVPCluster3Color = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Cluster 4 Color", Order = 4, GroupName = "11. Cluster Colors")]
        public System.Windows.Media.Brush FRVPCluster4Color { get; set; }
        [Browsable(false)] public string FRVPCluster4ColorS { get { return Serialize.BrushToString(FRVPCluster4Color); } set { FRVPCluster4Color = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Cluster 5 Color", Order = 5, GroupName = "11. Cluster Colors")]
        public System.Windows.Media.Brush FRVPCluster5Color { get; set; }
        [Browsable(false)] public string FRVPCluster5ColorS { get { return Serialize.BrushToString(FRVPCluster5Color); } set { FRVPCluster5Color = Serialize.StringToBrush(value); } }

        // ══════════════════════════════════════════════════════════════
        // 12. ALERTS
        // ══════════════════════════════════════════════════════════════

        [Display(Name = "Alert on BOS", Order = 1, GroupName = "12. Alerts — Structure")]
        public bool AlertOnBOS { get; set; }

        [Display(Name = "Alert on CHoCH", Order = 2, GroupName = "12. Alerts — Structure")]
        public bool AlertOnCHoCH { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BOS Sound File", Description = "WAV file name in NinjaTrader sounds folder", Order = 3, GroupName = "12. Alerts — Structure")]
        public string AlertSoundBOS { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "CHoCH Sound File", Description = "WAV file name in NinjaTrader sounds folder", Order = 4, GroupName = "12. Alerts — Structure")]
        public string AlertSoundCHoCH { get; set; }

        [Display(Name = "Alert on OB Creation", Description = "Alert when a new order block is detected", Order = 5, GroupName = "12. Alerts — Structure")]
        public bool AlertOnOBCreation { get; set; }

        [Display(Name = "Alert on OB Touch", Description = "Alert when price touches an active order block", Order = 6, GroupName = "12. Alerts — Structure")]
        public bool AlertOnOBTouch { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "OB Sound File", Description = "WAV file for order block alerts", Order = 7, GroupName = "12. Alerts — Structure")]
        public string AlertSoundOB { get; set; }

        [Display(Name = "Alert on AVWAP Touch", Description = "Alert when price touches the anchored VWAP", Order = 8, GroupName = "12. Alerts — Structure")]
        public bool AlertOnAVWAPTouch { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AVWAP Sound File", Description = "WAV file for AVWAP touch alerts", Order = 9, GroupName = "12. Alerts — Structure")]
        public string AlertSoundAVWAP { get; set; }

        [Display(Name = "Alert on Fib Touch", Description = "Alert when price touches a fib retracement level", Order = 10, GroupName = "12. Alerts — Structure")]
        public bool AlertOnFibTouch { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fib Sound File", Description = "WAV file for fib touch alerts", Order = 11, GroupName = "12. Alerts — Structure")]
        public string AlertSoundFib { get; set; }

        // ═══ Voice Alert Settings ═══

        [Display(Name = "Enable Voice Alerts", Description = "Auto-generate spoken alerts with instrument name (e.g., 'MNQ Bullish CHoCH'). Uses edge-tts neural voice with SAPI5 fallback.", Order = 1, GroupName = "12b. Voice Alerts")]
        public bool EnableVoiceAlerts { get; set; }

        [Range(-10, 10)]
        [Display(Name = "Voice Speed", Description = "Speech rate (-10 slowest to 10 fastest, 0 normal, 2 recommended)", Order = 2, GroupName = "12b. Voice Alerts")]
        public int VoiceAlertRate { get; set; }

        [Range(5, 300)]
        [Display(Name = "Alert Cooldown (Seconds)", Description = "Minimum time between repeated alerts for the same level", Order = 3, GroupName = "12b. Voice Alerts")]
        public int AlertCooldownSeconds { get; set; }

        [Display(Name = "Fallback Sound File", Description = "Sound file used if voice generation fails (e.g., 'Alert1.wav')", Order = 4, GroupName = "12b. Voice Alerts")]
        public string AlertFallbackSound { get; set; }

        // ══════════════════════════════════════════════════════════════
        // 13. DISPLACEMENT CANDLES
        // ══════════════════════════════════════════════════════════════

        [Display(Name = "Show Displacement Candles", Order = 1, GroupName = "13. Displacement Candles")]
        public bool ShowDisplacement { get; set; }

        [NinjaScriptProperty][Range(0.5, 5.0)]
        [Display(Name = "ATR Multiplier", Description = "Candle range >= ATR * this = displacement", Order = 2, GroupName = "13. Displacement Candles")]
        public double DisplacementATRMultiplier { get; set; }

        [Display(Name = "Min Body %", Description = "Minimum body as % of range", Order = 3, GroupName = "13. Displacement Candles")][Range(30, 95)]
        public int DisplacementMinBodyPct { get; set; }

        [XmlIgnore]
        [Display(Name = "Bullish Color", Order = 4, GroupName = "13. Displacement Candles")]
        public System.Windows.Media.Brush BullDisplacementColor { get; set; }
        [Browsable(false)] public string BullDisplacementColorS { get { return Serialize.BrushToString(BullDisplacementColor); } set { BullDisplacementColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Bearish Color", Order = 5, GroupName = "13. Displacement Candles")]
        public System.Windows.Media.Brush BearDisplacementColor { get; set; }
        [Browsable(false)] public string BearDisplacementColorS { get { return Serialize.BrushToString(BearDisplacementColor); } set { BearDisplacementColor = Serialize.StringToBrush(value); } }

        [Display(Name = "Marker Opacity %", Order = 6, GroupName = "13. Displacement Candles")][Range(10, 100)]
        public int DisplacementMarkerOpacity { get; set; }

        // ══════════════════════════════════════════════════════════════
        // 14. EQUAL HIGHS / LOWS
        // ══════════════════════════════════════════════════════════════

        [Display(Name = "Show Equal Highs/Lows", Order = 1, GroupName = "14. Equal Highs / Lows")]
        public bool ShowEqualLevels { get; set; }

        [Display(Name = "Tolerance (Ticks)", Description = "Max distance between swings to be considered equal", Order = 2, GroupName = "14. Equal Highs / Lows")][Range(1, 20)]
        public int EqualLevelTolerance { get; set; }

        [XmlIgnore]
        [Display(Name = "Equal High Color", Order = 3, GroupName = "14. Equal Highs / Lows")]
        public System.Windows.Media.Brush EqualHighColor { get; set; }
        [Browsable(false)] public string EqualHighColorS { get { return Serialize.BrushToString(EqualHighColor); } set { EqualHighColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Equal Low Color", Order = 4, GroupName = "14. Equal Highs / Lows")]
        public System.Windows.Media.Brush EqualLowColor { get; set; }
        [Browsable(false)] public string EqualLowColorS { get { return Serialize.BrushToString(EqualLowColor); } set { EqualLowColor = Serialize.StringToBrush(value); } }

        [Display(Name = "Line Width", Order = 5, GroupName = "14. Equal Highs / Lows")][Range(1, 5)]
        public int EqualLevelWidth { get; set; }

        [Display(Name = "Line Style", Order = 6, GroupName = "14. Equal Highs / Lows")]
        public DashStyleHelper EqualLevelStyle { get; set; }

        [Display(Name = "Opacity %", Order = 7, GroupName = "14. Equal Highs / Lows")][Range(10, 100)]
        public int EqualLevelOpacity { get; set; }

        [Display(Name = "Label Font Size", Order = 8, GroupName = "14. Equal Highs / Lows")][Range(6, 20)]
        public int EqualLevelFontSize { get; set; }

        // ══════════════════════════════════════════════════════════════
        // 15. LIQUIDITY SWEEPS
        // ══════════════════════════════════════════════════════════════

        [Display(Name = "Show Liquidity Sweeps", Order = 1, GroupName = "15. Liquidity Sweeps")]
        public bool ShowLiquiditySweeps { get; set; }

        [Display(Name = "Min Sweep Depth (Ticks)", Description = "Wick must extend this many ticks past level", Order = 2, GroupName = "15. Liquidity Sweeps")][Range(1, 20)]
        public int SweepMinDepthTicks { get; set; }

        [Display(Name = "Min Rejection Wick %", Description = "Sweep-side wick as % of candle range", Order = 3, GroupName = "15. Liquidity Sweeps")][Range(10, 80)]
        public int SweepMinWickPct { get; set; }

        [Display(Name = "Volume Filter", Description = "Require above-avg volume (disable for tick charts)", Order = 4, GroupName = "15. Liquidity Sweeps")]
        public bool SweepVolumeFilter { get; set; }

        [NinjaScriptProperty][Range(0.5, 5.0)]
        [Display(Name = "Volume Multiplier", Description = "Bar volume >= avg * this (when volume filter enabled)", Order = 5, GroupName = "15. Liquidity Sweeps")]
        public double SweepVolumeMultiplier { get; set; }

        [Display(Name = "Sweep Mitigated Levels", Description = "Also detect sweeps of already-mitigated strong levels", Order = 6, GroupName = "15. Liquidity Sweeps")]
        public bool SweepMitigatedLevels { get; set; }

        [Display(Name = "Sweep Order Blocks", Description = "Detect sweeps of OB edges", Order = 7, GroupName = "15. Liquidity Sweeps")]
        public bool SweepOrderBlocks { get; set; }

        [XmlIgnore]
        [Display(Name = "Bullish Sweep Color", Description = "Sweep of lows (bullish signal)", Order = 8, GroupName = "15. Liquidity Sweeps")]
        public System.Windows.Media.Brush BullSweepColor { get; set; }
        [Browsable(false)] public string BullSweepColorS { get { return Serialize.BrushToString(BullSweepColor); } set { BullSweepColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Bearish Sweep Color", Description = "Sweep of highs (bearish signal)", Order = 9, GroupName = "15. Liquidity Sweeps")]
        public System.Windows.Media.Brush BearSweepColor { get; set; }
        [Browsable(false)] public string BearSweepColorS { get { return Serialize.BrushToString(BearSweepColor); } set { BearSweepColor = Serialize.StringToBrush(value); } }

        [Display(Name = "Rectangle Opacity %", Order = 10, GroupName = "15. Liquidity Sweeps")][Range(10, 100)]
        public int SweepOpacity { get; set; }

        [Display(Name = "Alert on Sweep", Order = 11, GroupName = "15. Liquidity Sweeps")]
        public bool AlertOnSweep { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sweep Sound File", Order = 12, GroupName = "15. Liquidity Sweeps")]
        public string AlertSoundSweep { get; set; }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private RedTail.RedTailMarketStructureV2[] cacheRedTailMarketStructureV2;
		public RedTail.RedTailMarketStructureV2 RedTailMarketStructureV2(int swingLength, string bOSConfirmation, bool showCHoCH, bool showSwingLabels, string bOSStyle, int bOSWidth, string trendDisplayPosition, bool showHalfRetracement, string halfRetracementStyle, int halfRetracementWidth, bool showStrongWeakLevels, double strongLevelVolumeMultiplier, int strongLevelMinScore, string strongLevelMitigation, int oBSwingLength, double maxATRMultiplier, bool showHistoricZones, string zoneInvalidation, string oBDrawStyle, string zoneCount, int boxExtendBars, bool deleteBrokenBoxes, bool enableFRVP, string fRVPTrigger, bool keepPreviousFRVP, int fRVPRows, int fRVPProfileWidth, string fRVPVPAlignment, string alertSoundBOS, string alertSoundCHoCH, string alertSoundOB, string alertSoundAVWAP, string alertSoundFib, double displacementATRMultiplier, double sweepVolumeMultiplier, string alertSoundSweep)
		{
			return RedTailMarketStructureV2(Input, swingLength, bOSConfirmation, showCHoCH, showSwingLabels, bOSStyle, bOSWidth, trendDisplayPosition, showHalfRetracement, halfRetracementStyle, halfRetracementWidth, showStrongWeakLevels, strongLevelVolumeMultiplier, strongLevelMinScore, strongLevelMitigation, oBSwingLength, maxATRMultiplier, showHistoricZones, zoneInvalidation, oBDrawStyle, zoneCount, boxExtendBars, deleteBrokenBoxes, enableFRVP, fRVPTrigger, keepPreviousFRVP, fRVPRows, fRVPProfileWidth, fRVPVPAlignment, alertSoundBOS, alertSoundCHoCH, alertSoundOB, alertSoundAVWAP, alertSoundFib, displacementATRMultiplier, sweepVolumeMultiplier, alertSoundSweep);
		}

		public RedTail.RedTailMarketStructureV2 RedTailMarketStructureV2(ISeries<double> input, int swingLength, string bOSConfirmation, bool showCHoCH, bool showSwingLabels, string bOSStyle, int bOSWidth, string trendDisplayPosition, bool showHalfRetracement, string halfRetracementStyle, int halfRetracementWidth, bool showStrongWeakLevels, double strongLevelVolumeMultiplier, int strongLevelMinScore, string strongLevelMitigation, int oBSwingLength, double maxATRMultiplier, bool showHistoricZones, string zoneInvalidation, string oBDrawStyle, string zoneCount, int boxExtendBars, bool deleteBrokenBoxes, bool enableFRVP, string fRVPTrigger, bool keepPreviousFRVP, int fRVPRows, int fRVPProfileWidth, string fRVPVPAlignment, string alertSoundBOS, string alertSoundCHoCH, string alertSoundOB, string alertSoundAVWAP, string alertSoundFib, double displacementATRMultiplier, double sweepVolumeMultiplier, string alertSoundSweep)
		{
			if (cacheRedTailMarketStructureV2 != null)
				for (int idx = 0; idx < cacheRedTailMarketStructureV2.Length; idx++)
					if (cacheRedTailMarketStructureV2[idx] != null && cacheRedTailMarketStructureV2[idx].SwingLength == swingLength && cacheRedTailMarketStructureV2[idx].BOSConfirmation == bOSConfirmation && cacheRedTailMarketStructureV2[idx].ShowCHoCH == showCHoCH && cacheRedTailMarketStructureV2[idx].ShowSwingLabels == showSwingLabels && cacheRedTailMarketStructureV2[idx].BOSStyle == bOSStyle && cacheRedTailMarketStructureV2[idx].BOSWidth == bOSWidth && cacheRedTailMarketStructureV2[idx].TrendDisplayPosition == trendDisplayPosition && cacheRedTailMarketStructureV2[idx].ShowHalfRetracement == showHalfRetracement && cacheRedTailMarketStructureV2[idx].HalfRetracementStyle == halfRetracementStyle && cacheRedTailMarketStructureV2[idx].HalfRetracementWidth == halfRetracementWidth && cacheRedTailMarketStructureV2[idx].ShowStrongWeakLevels == showStrongWeakLevels && cacheRedTailMarketStructureV2[idx].StrongLevelVolumeMultiplier == strongLevelVolumeMultiplier && cacheRedTailMarketStructureV2[idx].StrongLevelMinScore == strongLevelMinScore && cacheRedTailMarketStructureV2[idx].StrongLevelMitigation == strongLevelMitigation && cacheRedTailMarketStructureV2[idx].OBSwingLength == oBSwingLength && cacheRedTailMarketStructureV2[idx].MaxATRMultiplier == maxATRMultiplier && cacheRedTailMarketStructureV2[idx].ShowHistoricZones == showHistoricZones && cacheRedTailMarketStructureV2[idx].ZoneInvalidation == zoneInvalidation && cacheRedTailMarketStructureV2[idx].OBDrawStyle == oBDrawStyle && cacheRedTailMarketStructureV2[idx].ZoneCount == zoneCount && cacheRedTailMarketStructureV2[idx].BoxExtendBars == boxExtendBars && cacheRedTailMarketStructureV2[idx].DeleteBrokenBoxes == deleteBrokenBoxes && cacheRedTailMarketStructureV2[idx].EnableFRVP == enableFRVP && cacheRedTailMarketStructureV2[idx].FRVPTrigger == fRVPTrigger && cacheRedTailMarketStructureV2[idx].KeepPreviousFRVP == keepPreviousFRVP && cacheRedTailMarketStructureV2[idx].FRVPRows == fRVPRows && cacheRedTailMarketStructureV2[idx].FRVPProfileWidth == fRVPProfileWidth && cacheRedTailMarketStructureV2[idx].FRVPVPAlignment == fRVPVPAlignment && cacheRedTailMarketStructureV2[idx].AlertSoundBOS == alertSoundBOS && cacheRedTailMarketStructureV2[idx].AlertSoundCHoCH == alertSoundCHoCH && cacheRedTailMarketStructureV2[idx].AlertSoundOB == alertSoundOB && cacheRedTailMarketStructureV2[idx].AlertSoundAVWAP == alertSoundAVWAP && cacheRedTailMarketStructureV2[idx].AlertSoundFib == alertSoundFib && cacheRedTailMarketStructureV2[idx].DisplacementATRMultiplier == displacementATRMultiplier && cacheRedTailMarketStructureV2[idx].SweepVolumeMultiplier == sweepVolumeMultiplier && cacheRedTailMarketStructureV2[idx].AlertSoundSweep == alertSoundSweep && cacheRedTailMarketStructureV2[idx].EqualsInput(input))
						return cacheRedTailMarketStructureV2[idx];
			return CacheIndicator<RedTail.RedTailMarketStructureV2>(new RedTail.RedTailMarketStructureV2(){ SwingLength = swingLength, BOSConfirmation = bOSConfirmation, ShowCHoCH = showCHoCH, ShowSwingLabels = showSwingLabels, BOSStyle = bOSStyle, BOSWidth = bOSWidth, TrendDisplayPosition = trendDisplayPosition, ShowHalfRetracement = showHalfRetracement, HalfRetracementStyle = halfRetracementStyle, HalfRetracementWidth = halfRetracementWidth, ShowStrongWeakLevels = showStrongWeakLevels, StrongLevelVolumeMultiplier = strongLevelVolumeMultiplier, StrongLevelMinScore = strongLevelMinScore, StrongLevelMitigation = strongLevelMitigation, OBSwingLength = oBSwingLength, MaxATRMultiplier = maxATRMultiplier, ShowHistoricZones = showHistoricZones, ZoneInvalidation = zoneInvalidation, OBDrawStyle = oBDrawStyle, ZoneCount = zoneCount, BoxExtendBars = boxExtendBars, DeleteBrokenBoxes = deleteBrokenBoxes, EnableFRVP = enableFRVP, FRVPTrigger = fRVPTrigger, KeepPreviousFRVP = keepPreviousFRVP, FRVPRows = fRVPRows, FRVPProfileWidth = fRVPProfileWidth, FRVPVPAlignment = fRVPVPAlignment, AlertSoundBOS = alertSoundBOS, AlertSoundCHoCH = alertSoundCHoCH, AlertSoundOB = alertSoundOB, AlertSoundAVWAP = alertSoundAVWAP, AlertSoundFib = alertSoundFib, DisplacementATRMultiplier = displacementATRMultiplier, SweepVolumeMultiplier = sweepVolumeMultiplier, AlertSoundSweep = alertSoundSweep }, input, ref cacheRedTailMarketStructureV2);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.RedTail.RedTailMarketStructureV2 RedTailMarketStructureV2(int swingLength, string bOSConfirmation, bool showCHoCH, bool showSwingLabels, string bOSStyle, int bOSWidth, string trendDisplayPosition, bool showHalfRetracement, string halfRetracementStyle, int halfRetracementWidth, bool showStrongWeakLevels, double strongLevelVolumeMultiplier, int strongLevelMinScore, string strongLevelMitigation, int oBSwingLength, double maxATRMultiplier, bool showHistoricZones, string zoneInvalidation, string oBDrawStyle, string zoneCount, int boxExtendBars, bool deleteBrokenBoxes, bool enableFRVP, string fRVPTrigger, bool keepPreviousFRVP, int fRVPRows, int fRVPProfileWidth, string fRVPVPAlignment, string alertSoundBOS, string alertSoundCHoCH, string alertSoundOB, string alertSoundAVWAP, string alertSoundFib, double displacementATRMultiplier, double sweepVolumeMultiplier, string alertSoundSweep)
		{
			return indicator.RedTailMarketStructureV2(Input, swingLength, bOSConfirmation, showCHoCH, showSwingLabels, bOSStyle, bOSWidth, trendDisplayPosition, showHalfRetracement, halfRetracementStyle, halfRetracementWidth, showStrongWeakLevels, strongLevelVolumeMultiplier, strongLevelMinScore, strongLevelMitigation, oBSwingLength, maxATRMultiplier, showHistoricZones, zoneInvalidation, oBDrawStyle, zoneCount, boxExtendBars, deleteBrokenBoxes, enableFRVP, fRVPTrigger, keepPreviousFRVP, fRVPRows, fRVPProfileWidth, fRVPVPAlignment, alertSoundBOS, alertSoundCHoCH, alertSoundOB, alertSoundAVWAP, alertSoundFib, displacementATRMultiplier, sweepVolumeMultiplier, alertSoundSweep);
		}

		public Indicators.RedTail.RedTailMarketStructureV2 RedTailMarketStructureV2(ISeries<double> input , int swingLength, string bOSConfirmation, bool showCHoCH, bool showSwingLabels, string bOSStyle, int bOSWidth, string trendDisplayPosition, bool showHalfRetracement, string halfRetracementStyle, int halfRetracementWidth, bool showStrongWeakLevels, double strongLevelVolumeMultiplier, int strongLevelMinScore, string strongLevelMitigation, int oBSwingLength, double maxATRMultiplier, bool showHistoricZones, string zoneInvalidation, string oBDrawStyle, string zoneCount, int boxExtendBars, bool deleteBrokenBoxes, bool enableFRVP, string fRVPTrigger, bool keepPreviousFRVP, int fRVPRows, int fRVPProfileWidth, string fRVPVPAlignment, string alertSoundBOS, string alertSoundCHoCH, string alertSoundOB, string alertSoundAVWAP, string alertSoundFib, double displacementATRMultiplier, double sweepVolumeMultiplier, string alertSoundSweep)
		{
			return indicator.RedTailMarketStructureV2(input, swingLength, bOSConfirmation, showCHoCH, showSwingLabels, bOSStyle, bOSWidth, trendDisplayPosition, showHalfRetracement, halfRetracementStyle, halfRetracementWidth, showStrongWeakLevels, strongLevelVolumeMultiplier, strongLevelMinScore, strongLevelMitigation, oBSwingLength, maxATRMultiplier, showHistoricZones, zoneInvalidation, oBDrawStyle, zoneCount, boxExtendBars, deleteBrokenBoxes, enableFRVP, fRVPTrigger, keepPreviousFRVP, fRVPRows, fRVPProfileWidth, fRVPVPAlignment, alertSoundBOS, alertSoundCHoCH, alertSoundOB, alertSoundAVWAP, alertSoundFib, displacementATRMultiplier, sweepVolumeMultiplier, alertSoundSweep);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.RedTail.RedTailMarketStructureV2 RedTailMarketStructureV2(int swingLength, string bOSConfirmation, bool showCHoCH, bool showSwingLabels, string bOSStyle, int bOSWidth, string trendDisplayPosition, bool showHalfRetracement, string halfRetracementStyle, int halfRetracementWidth, bool showStrongWeakLevels, double strongLevelVolumeMultiplier, int strongLevelMinScore, string strongLevelMitigation, int oBSwingLength, double maxATRMultiplier, bool showHistoricZones, string zoneInvalidation, string oBDrawStyle, string zoneCount, int boxExtendBars, bool deleteBrokenBoxes, bool enableFRVP, string fRVPTrigger, bool keepPreviousFRVP, int fRVPRows, int fRVPProfileWidth, string fRVPVPAlignment, string alertSoundBOS, string alertSoundCHoCH, string alertSoundOB, string alertSoundAVWAP, string alertSoundFib, double displacementATRMultiplier, double sweepVolumeMultiplier, string alertSoundSweep)
		{
			return indicator.RedTailMarketStructureV2(Input, swingLength, bOSConfirmation, showCHoCH, showSwingLabels, bOSStyle, bOSWidth, trendDisplayPosition, showHalfRetracement, halfRetracementStyle, halfRetracementWidth, showStrongWeakLevels, strongLevelVolumeMultiplier, strongLevelMinScore, strongLevelMitigation, oBSwingLength, maxATRMultiplier, showHistoricZones, zoneInvalidation, oBDrawStyle, zoneCount, boxExtendBars, deleteBrokenBoxes, enableFRVP, fRVPTrigger, keepPreviousFRVP, fRVPRows, fRVPProfileWidth, fRVPVPAlignment, alertSoundBOS, alertSoundCHoCH, alertSoundOB, alertSoundAVWAP, alertSoundFib, displacementATRMultiplier, sweepVolumeMultiplier, alertSoundSweep);
		}

		public Indicators.RedTail.RedTailMarketStructureV2 RedTailMarketStructureV2(ISeries<double> input , int swingLength, string bOSConfirmation, bool showCHoCH, bool showSwingLabels, string bOSStyle, int bOSWidth, string trendDisplayPosition, bool showHalfRetracement, string halfRetracementStyle, int halfRetracementWidth, bool showStrongWeakLevels, double strongLevelVolumeMultiplier, int strongLevelMinScore, string strongLevelMitigation, int oBSwingLength, double maxATRMultiplier, bool showHistoricZones, string zoneInvalidation, string oBDrawStyle, string zoneCount, int boxExtendBars, bool deleteBrokenBoxes, bool enableFRVP, string fRVPTrigger, bool keepPreviousFRVP, int fRVPRows, int fRVPProfileWidth, string fRVPVPAlignment, string alertSoundBOS, string alertSoundCHoCH, string alertSoundOB, string alertSoundAVWAP, string alertSoundFib, double displacementATRMultiplier, double sweepVolumeMultiplier, string alertSoundSweep)
		{
			return indicator.RedTailMarketStructureV2(input, swingLength, bOSConfirmation, showCHoCH, showSwingLabels, bOSStyle, bOSWidth, trendDisplayPosition, showHalfRetracement, halfRetracementStyle, halfRetracementWidth, showStrongWeakLevels, strongLevelVolumeMultiplier, strongLevelMinScore, strongLevelMitigation, oBSwingLength, maxATRMultiplier, showHistoricZones, zoneInvalidation, oBDrawStyle, zoneCount, boxExtendBars, deleteBrokenBoxes, enableFRVP, fRVPTrigger, keepPreviousFRVP, fRVPRows, fRVPProfileWidth, fRVPVPAlignment, alertSoundBOS, alertSoundCHoCH, alertSoundOB, alertSoundAVWAP, alertSoundFib, displacementATRMultiplier, sweepVolumeMultiplier, alertSoundSweep);
		}
	}
}

#endregion
