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
	public class InsideBarTrailing : Strategy
	{
		private EMA ema;
		private SMA smaFast;
		private SMA smaSlow;
		private int firstLotQuantity;
		private int secondLotQuantity;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Inside Bar Strategy but utilising much more complex logic with partial TPs and trailing stops.";
				Name										= "InsideBarTrailing";
				Calculate = Calculate.OnBarClose;
				EntriesPerDirection = 2;
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
				IgnoreAccountPosition = false;
				PartialTakeProfitPercentage = 0.6;
				TrailingStopMultiplier = 5;
				MaximumLossPerTrade = 0;

				ATRLength = 3;
				ATRMultiplier = 10.0;
				OrderQuantity = 6;

				EmaPeriod = 22;
				SmaFastPeriod = 35;
				SmaSlowPeriod = 125;
				ErrorMargin = 0.1;
			}
			else if (State == State.DataLoaded)
			{
				ema = EMA(EmaPeriod);
				smaFast = SMA(SmaFastPeriod);
				smaSlow = SMA(SmaSlowPeriod);

				AddChartIndicator(ema);
				AddChartIndicator(smaFast);
				AddChartIndicator(smaSlow);

				firstLotQuantity = (int) Math.Ceiling(OrderQuantity * PartialTakeProfitPercentage);
				secondLotQuantity = OrderQuantity - firstLotQuantity;
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBars[0] <= BarsRequiredToTrade) return;
			if (PositionAccount.MarketPosition != MarketPosition.Flat && !IgnoreAccountPosition) return;
			if (Position.MarketPosition != MarketPosition.Flat) return;

			bool inside = (High[1] < High[2]) && (Low[1] > Low[2]);
			if (!inside) return;

			bool upTrend = Close[0] > ema[0] && Close[0] > smaFast[0] && Close[0] > smaSlow[0];
			bool downTrend = Close[0] < ema[0] && Close[0] < smaFast[0] && Close[0] < smaSlow[0];

			if ((Close[0] > (High[2] + (High[2] - Low[2]) * ErrorMargin)) && upTrend)
			{
				EnterLong(0, firstLotQuantity, "entry1");
				EnterLong(0, secondLotQuantity, "entry2");
				return;
			}

			if ((Close[0] < (Low[2] - (High[2] - Low[2]) * ErrorMargin)) && downTrend)
			{
				EnterShort(0, firstLotQuantity, "entry1");
				EnterShort(0, secondLotQuantity, "entry2");
				return;
			}
		}

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (marketPosition == MarketPosition.Flat) return;
			if (execution.Name != "entry1" && execution.Name != "entry2" && execution.Name != "Take profit") return;

			double atr = ATR(ATRLength)[0];
			double trailingStopDistance = (High[1] - Low[1]) / TickSize * TrailingStopMultiplier;

			if (marketPosition == MarketPosition.Long)
			{
				double stopLossPrice = Low[1] - ATRMultiplier * atr;
				double targetPrice = price + atr;

				SetStopLoss("entry1", CalculationMode.Price, stopLossPrice, false);
				SetTrailStop("entry2", CalculationMode.Ticks, trailingStopDistance, false);
				SetProfitTarget("entry1", CalculationMode.Price, targetPrice, false);
				return;
			}

			if (marketPosition == MarketPosition.Short)
			{
				double stopLossPrice = High[1] + ATRMultiplier * atr;
				double targetPrice = price - atr;

				SetStopLoss("entry1", CalculationMode.Price, stopLossPrice, false);
				SetTrailStop("entry2", CalculationMode.Ticks, trailingStopDistance, false);
				SetProfitTarget("entry1", CalculationMode.Price, targetPrice, false);
				return;
			}
		}

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
		{
			if (marketPosition == MarketPosition.Flat) return;

			if (position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]) > -200) return;

			if (position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0] ) < -MaximumLossPerTrade && MaximumLossPerTrade > 0)
			{
				if (position.MarketPosition == MarketPosition.Long)
				{
					ExitLong("Exit Long Max Loss", "entry1");
					ExitLong("Exit Long Max Loss", "entry2");
					return;
				}

				if (position.MarketPosition == MarketPosition.Short)
				{
					ExitShort("Exit Short Max Loss", "entry1");
					ExitShort("Exit Short Max Loss", "entry2");
					return;
				}
			}

			if (position.MarketPosition == MarketPosition.Long && (ema[0] < smaFast[0]))
			{
				ExitLong("Exit Long Trend Violation", "entry1");
				ExitLong("Exit Long Trend Violation", "entry2");
				return;
			}

			if (position.MarketPosition == MarketPosition.Short && (ema[0] > smaFast[0]))
			{
				ExitShort("Exit Short Trend Violation", "entry1");
				ExitShort("Exit Short Trend Violation", "entry2");
				return;
			}
		}

		#region Properties

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
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

		[NinjaScriptProperty]
		[Display(Name = "Ignore Account Position", Order = 22, GroupName = "Parameters")]
		public bool IgnoreAccountPosition { get; set; }

		[NinjaScriptProperty]
		[Range(0, 0.9)]
		[Display(Name = "Partial Take Profit Percentage", Order = 23, GroupName = "Parameters")]
		public double PartialTakeProfitPercentage { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "Trailing Stop Multiplier", Order = 24, GroupName = "Parameters")]
		public double TrailingStopMultiplier { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Maximum Loss Per Trade", Order = 25, GroupName = "Parameters")]
		public double MaximumLossPerTrade { get; set; }

		#endregion
	}
}
