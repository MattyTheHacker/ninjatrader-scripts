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
	public class InsideBar : Strategy
	{
		private SessionIterator sessionIterator;
		private EMA ema;
		private SMA smaFast;
		private SMA smaSlow;
		private DateTime now = Core.Globals.Now;
		private DateTime Now
		{
			get
			{
				now = Connection.PlaybackConnection != null ? Connection.PlaybackConnection.Now : Core.Globals.Now;

				if (now.Millisecond > 0)
					now = Core.Globals.MinDate.AddSeconds((long)Math.Floor(now.Subtract(Core.Globals.MinDate).TotalSeconds));

				return now;
			}
		}

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = @"InsideBar strategy";
				Name = "InsideBar";
				Calculate = Calculate.OnBarClose;
				EntriesPerDirection = 1;
				EntryHandling = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds = 180;
				IsFillLimitOnTouch = true;
				MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution = OrderFillResolution.Standard;
				Slippage = 0;
				StartBehavior = StartBehavior.WaitUntilFlat;
				TimeInForce = TimeInForce.Gtc;
				TraceOrders = false;
				RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade = 5;
				IsInstantiatedOnEachOptimizationIteration = true;

				ATRLength = 3;
				ATRMultiplier = 10.0;
				OrderQuantity = 4;

				EmaPeriod = 22;
				SmaFastPeriod = 35;
				SmaSlowPeriod = 200;
				ErrorMargin = 0.01;
			}
			else if (State == State.DataLoaded)
			{
				sessionIterator = new SessionIterator(Bars);
				ema = EMA(EmaPeriod);
				smaFast = SMA(SmaFastPeriod);
				smaSlow = SMA(SmaSlowPeriod);
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBars[0] <= BarsRequiredToTrade) return;

			if (PositionAccount.MarketPosition != MarketPosition.Flat) return;

			sessionIterator.GetNextSession(Now, true);

			if ((sessionIterator.ActualSessionEnd - Now).TotalHours <= 1) return;

			bool inside = (High[1] < High[2]) && (Low[1] > Low[2]);

			if (!inside) return;

			bool upTrend = Close[0] > ema[0] && Close[0] > smaFast[0] && Close[0] > smaSlow[0];
			bool downTrend = Close[0] < ema[0] && Close[0] < smaFast[0] && Close[0] < smaSlow[0];

			if (Close[0] > (High[2] + (High[2] - Low[2]) * ErrorMargin) && upTrend)
			{
				EnterLong(0, OrderQuantity, "entry"); 
				return;
			}

			if (Close[0] < (Low[2] - (High[2] - Low[2]) * ErrorMargin) && downTrend)
			{
				EnterShort(0, OrderQuantity, "entry"); 
				return;
			}
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (marketPosition == MarketPosition.Flat) return;

			if (execution.Name != "entry") return;

			double atr = ATR(ATRLength)[0];

			if (marketPosition == MarketPosition.Long)
			{
				double stop = Low[1] - ATRMultiplier * atr;
				double target = price + atr;

				SetStopLoss("entry", CalculationMode.Price, stop, false);
				SetProfitTarget("entry", CalculationMode.Price, target);
			}

			if (marketPosition == MarketPosition.Short)
			{
				double stop = High[1] + ATRMultiplier * atr;
				double target = price - atr;

				SetStopLoss("entry", CalculationMode.Price, stop, false);
				SetProfitTarget("entry", CalculationMode.Price, target);
			}
		}

		#region Properties

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "OrderQuantity", Order = 1, GroupName = "Parameters")]
		public int OrderQuantity { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "EMA Period", Order = 10, GroupName = "Moving Averages")]
		public int EmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "SMA Fast Period", Order = 11, GroupName = "Moving Averages")]
		public int SmaFastPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "SMA Slow Period", Order = 12, GroupName = "Moving Averages")]
		public int SmaSlowPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, 1)]
		[Display(Name = "Error Margin", Order = 13, GroupName = "Parameters")]
		public double ErrorMargin { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "ATR Length", Order = 20, GroupName = "ATR Settings")]
		public int ATRLength { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "ATR Multiplier", Order = 21, GroupName = "ATR Settings")]
		public double ATRMultiplier { get; set; }

		#endregion
	}
}
