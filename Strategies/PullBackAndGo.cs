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
	public class PullBackAndGo : Strategy
	{
		private double previousStop = double.NaN;
		private EMA ema;
		private SMA slowSMA;
		private SMA fastSMA;
		private OrderFlowVWAP vwap;
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Bullish pull back long entry";
				Name										= "PullBackAndGo";
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
				BarsRequiredToTrade							= 20;
				// Disable this property for performance gains in Strategy Analyzer optimizations
				// See the Help Guide for additional information
				IsInstantiatedOnEachOptimizationIteration	= true;

				EmaPeriod = 21;
				SlowSMAPeriod = 175;
				FastSMAPeriod = 60;
			}
			else if (State == State.DataLoaded)
			{
				ema = EMA(EmaPeriod);
				slowSMA = SMA(SlowSMAPeriod);
				fastSMA = SMA(FastSMAPeriod);
				vwap = OrderFlowVWAP(VWAPResolution.Standard, Bars.TradingHours, VWAPStandardDeviations.Three, 1.0, 2.0, 3.0);
			}
		}

		protected override void OnBarUpdate()
		{
			if (Position.MarketPosition == MarketPosition.Long)
			{
				double newStop = Low[1];
				if (previousStop > newStop) return;

				previousStop = newStop;

				SetStopLoss("L1", CalculationMode.Price, newStop, false);
				SetStopLoss("L2", CalculationMode.Price, newStop, false);
				SetStopLoss("L3", CalculationMode.Price, newStop, false);
				SetStopLoss("L4", CalculationMode.Price, newStop, false);
				return;
			}

			if (CurrentBar < BarsRequiredToTrade || Close[0] < ema[0] || Close[0] < slowSMA[0] || Close[0] < fastSMA[0] || Close[0] < vwap.VWAP[0]) return;

			// check that the current handle made a new low
			if (Low[0] >= Low[1]) return;

			// check that the previous candle was red
			if (Close[1] > Open[1]) return;

			// check for hammer
			double bodySize = Math.Abs(Close[0] - Open[0]);
			double upperWickSize = High[0] - Math.Max(Close[0], Open[0]);
			double lowerWickSize = Math.Min(Close[0], Open[0]) - Low[0];

			bool isHammer = (lowerWickSize >= (bodySize * 2)) && (upperWickSize <= bodySize) && (bodySize > 0);

			if (!isHammer) return;

			double sLPrice = Low[0] - (TickSize * 2);
			double entryPrice = High[0];
			double risk = entryPrice - sLPrice;
			previousStop = sLPrice;

			SetStopLoss("L1", CalculationMode.Price, sLPrice, false);
			SetStopLoss("L2", CalculationMode.Price, sLPrice, false);
			SetStopLoss("L3", CalculationMode.Price, sLPrice, false);
			SetStopLoss("L4", CalculationMode.Price, sLPrice, false);

			SetProfitTarget("L1", CalculationMode.Price, entryPrice + risk);
			SetProfitTarget("L2", CalculationMode.Price, entryPrice + (risk * 1.5));
			SetProfitTarget("L3", CalculationMode.Price, entryPrice + (risk * 2));

			EnterLongStopMarket(entryPrice, "L1");
			EnterLongStopMarket(entryPrice, "L2");
			EnterLongStopMarket(entryPrice, "L3");
			EnterLongStopMarket(entryPrice, "L4");
		}

		# region Properties

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

		#endregion
	}
}
