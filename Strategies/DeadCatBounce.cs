#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

//This namespace holds Strategies in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Strategies
{
	public class DeadCatBounce : Strategy
	{
		private double previousStop = double.NaN;
		private EMA ema;
		private SMA slowSMA;
		private SMA fastSMA;
		private OrderFlowVWAP vwap;
		private int orderQuantity;
		private bool useEMA;
		private bool useSlowSMA;
		private bool useFastSMA;
		private bool useVWAP;
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Dead cat bounce short entry";
				Name										= "DeadCatBounce";
				Calculate									= Calculate.OnBarClose;
				EntriesPerDirection							= 4;
				EntryHandling								= EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy				= true;
				ExitOnSessionCloseSeconds					= 30;
				IsFillLimitOnTouch							= false;
				MaximumBarsLookBack							= MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution							= OrderFillResolution.Standard;
				Slippage									= 0;
				StartBehavior								= StartBehavior.WaitUntilFlat;
				TimeInForce									= TimeInForce.Gtc;
				TraceOrders									= false;
				RealtimeErrorHandling						= RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling							= StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade							= 200;
				// Disable this property for performance gains in Strategy Analyzer optimizations
				// See the Help Guide for additional information
				IsInstantiatedOnEachOptimizationIteration	= true;

				EmaPeriod = 21;
				SlowSMAPeriod = 175;
				FastSMAPeriod = 60;
				OrderQuantity = 4;
				
				UseEMA = true;
				UseSlowSMA = true;
				UseFastSMA = true;
				UseVWAP = true;
			}
			else if (State == State.DataLoaded)
			{
				ema = EMA(EmaPeriod);
				slowSMA = SMA(SlowSMAPeriod);
				fastSMA = SMA(FastSMAPeriod);
				vwap = OrderFlowVWAP(VWAPResolution.Standard, Bars.TradingHours, VWAPStandardDeviations.Three, 1.0, 2.0, 3.0);
				orderQuantity = OrderQuantity;

				useEMA = UseEMA;
				useSlowSMA = UseSlowSMA;
				useFastSMA = UseFastSMA;
				useVWAP = UseVWAP;

				if (useEMA) AddChartIndicator(ema);
				if (useSlowSMA) AddChartIndicator(slowSMA);
				if (useFastSMA) AddChartIndicator(fastSMA);
				if (useVWAP) AddChartIndicator(vwap);
			}
		}

		protected override void OnBarUpdate()
		{
			if (Position.MarketPosition == MarketPosition.Short)
			{
				double newStop = High[1] + (TickSize * 2);
				if (previousStop < newStop) return;

				previousStop = newStop;

				SetStopLoss("S1", CalculationMode.Price, newStop, false);
				SetStopLoss("S2", CalculationMode.Price, newStop, false);
				SetStopLoss("S3", CalculationMode.Price, newStop, false);
				SetStopLoss("S4", CalculationMode.Price, newStop, false);
				return;
			}

			// check we have enough bars, and in a downtrend
			if (CurrentBar < BarsRequiredToTrade) return;

			if (useEMA && Close[0] > ema[0]) return;
			if (useSlowSMA && Close[0] > slowSMA[0]) return;
			if (useFastSMA && Close[0] > fastSMA[0]) return;
			if (useVWAP && Close[0] > vwap.VWAP[0]) return;

			// check that the current candle made a new high
			if (High[0] <= High[1]) return;

			// check the previous candle was green
			if (Close[1] < Open[1]) return;

			// check for inverted hammer
			double bodySize = Math.Abs(Close[0] - Open[0]);
			double upperWickSize = High[0] - Math.Max(Close[0], Open[0]);
			double lowerWickSize = Math.Min(Close[0], Open[0]) - Low[0];

			bool isInvertedHammer = (upperWickSize >= (bodySize * 2)) && (lowerWickSize <= bodySize) && (bodySize > 0);

			if (!isInvertedHammer) return;

			int baseQuantity = orderQuantity / 4;
			int remainder = orderQuantity % 4;

			double sLPrice = High[0] + (TickSize * 2);
			double entryPrice = Low[0];
			double risk = sLPrice - entryPrice;
			previousStop = sLPrice;

			SetStopLoss("S1", CalculationMode.Price, sLPrice, false);
			SetStopLoss("S2", CalculationMode.Price, sLPrice, false);
			SetStopLoss("S3", CalculationMode.Price, sLPrice, false);
			SetStopLoss("S4", CalculationMode.Price, sLPrice, false);

			SetProfitTarget("S1", CalculationMode.Price, entryPrice - risk);
			SetProfitTarget("S2", CalculationMode.Price, entryPrice - (risk * 1.5));
			SetProfitTarget("S3", CalculationMode.Price, entryPrice - (risk * 2));

			EnterShortStopMarket(baseQuantity, entryPrice, "S1");
			EnterShortStopMarket(baseQuantity, entryPrice, "S2");
			EnterShortStopMarket(baseQuantity, entryPrice, "S3");
			EnterShortStopMarket(baseQuantity + remainder, entryPrice, "S4");
		}

		#region Properties

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "EmaPeriod", Order = 1, GroupName = "Parameters")]
		public int EmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "SlowSMAPeriod", Order = 2, GroupName = "Parameters")]
		public int SlowSMAPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "FastSMAPeriod", Order = 3, GroupName = "Parameters")]
		public int FastSMAPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(4, int.MaxValue)]
		[Display(Name = "OrderQuantity", Order = 4, GroupName = "Parameters")]
		public int OrderQuantity { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use EMA", Order = 5, GroupName = "Parameters")]
		public bool UseEMA { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use Slow SMA", Order = 6, GroupName = "Parameters")]
		public bool UseSlowSMA { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use Fast SMA", Order = 7, GroupName = "Parameters")]
		public bool UseFastSMA { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use VWAP", Order = 8, GroupName = "Parameters")]
		public bool UseVWAP { get; set; }

		#endregion
	}
}
