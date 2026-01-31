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
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

//This namespace holds Indicators in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Indicators
{
	public class RiskRewardMarkers : Indicator
	{
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Reads any RiskReward drawings on the chart and plots price markers for them. ";
				Name										= "RiskRewardMarkers";
				Calculate									= Calculate.OnEachTick;
				IsOverlay									= true;
				DisplayInDataBox							= false;
				DrawOnPricePanel							= true;
				DrawHorizontalGridLines						= true;
				DrawVerticalGridLines						= true;
				IsChartOnly									= true;
				PaintPriceMarkers							= true;
				ScaleJustification							= ScaleJustification.Right;
				IsSuspendedWhileInactive					= true;

				AddPlot(new Stroke(Brushes.Goldenrod, 1), PlotStyle.Hash, "Entry");
				AddPlot(new Stroke(Brushes.Crimson, 1), PlotStyle.Hash, "Stop");
				AddPlot(new Stroke(Brushes.SeaGreen, 1), PlotStyle.Hash, "Target");
				
				Plots[0].Width = 1;
				Plots[1].Width = 1;
				Plots[2].Width = 1;
			}
		}

		protected override void OnBarUpdate()
		{
			Entry[0] = double.NaN;
			Stop[0] = double.NaN;
			Target[0] = double.NaN;

			if (ChartControl == null) return;

			RiskRewardExtras rrDrawing = DrawObjects.OfType<RiskRewardExtras>().FirstOrDefault();

			if (rrDrawing == null) return;

			Entry[0] = rrDrawing.EntryAnchor.Price;
			Stop[0] = rrDrawing.RiskAnchor.Price;
			Target[0] = rrDrawing.RewardAnchor.Price;
		}

		#region Plots
		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Entry => Values[0];

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Stop => Values[1];

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Target => Values[2];
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private RiskRewardMarkers[] cacheRiskRewardMarkers;
		public RiskRewardMarkers RiskRewardMarkers()
		{
			return RiskRewardMarkers(Input);
		}

		public RiskRewardMarkers RiskRewardMarkers(ISeries<double> input)
		{
			if (cacheRiskRewardMarkers != null)
				for (int idx = 0; idx < cacheRiskRewardMarkers.Length; idx++)
					if (cacheRiskRewardMarkers[idx] != null &&  cacheRiskRewardMarkers[idx].EqualsInput(input))
						return cacheRiskRewardMarkers[idx];
			return CacheIndicator<RiskRewardMarkers>(new RiskRewardMarkers(), input, ref cacheRiskRewardMarkers);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.RiskRewardMarkers RiskRewardMarkers()
		{
			return indicator.RiskRewardMarkers(Input);
		}

		public Indicators.RiskRewardMarkers RiskRewardMarkers(ISeries<double> input )
		{
			return indicator.RiskRewardMarkers(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.RiskRewardMarkers RiskRewardMarkers()
		{
			return indicator.RiskRewardMarkers(Input);
		}

		public Indicators.RiskRewardMarkers RiskRewardMarkers(ISeries<double> input )
		{
			return indicator.RiskRewardMarkers(input);
		}
	}
}

#endregion
