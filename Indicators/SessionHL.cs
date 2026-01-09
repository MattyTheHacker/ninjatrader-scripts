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
	public class SessionHL : Indicator
	{
		private double currentHigh = double.MinValue;
		private double currentLow = double.MinValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= @"Shows a horizontal line at the session high and low";
				Name						= "SessionHL";
				IsAutoScale					= false;
				DrawOnPricePanel			= true;
				IsOverlay					= true;
				IsSuspendedWhileInactive	= true;
				BarsRequiredToPlot			= 0;

			}
			else if (State == State.Configure)
			{
				currentHigh = double.MinValue;
				currentLow  = double.MinValue;
			}
		}

		protected override void OnBarUpdate()
		{
			if (!Bars.BarsType.IsIntraday) return;

			if (Bars.IsFirstBarOfSession) 
			{
				currentHigh = High[0];
				currentLow = Low[0];
			}

			if (High[0] > currentHigh)
			{
				currentHigh = High[0];
				
				Draw.HorizontalLine(this, "Session High", currentHigh, Brushes.Orange, DashStyleHelper.Dash, 2);
			}
			
			if (Low[0] < currentLow)
			{
				currentLow = Low[0];
				
				Draw.HorizontalLine(this, "Session Low", currentLow, Brushes.Orange, DashStyleHelper.Dash, 2);
			}
		}
		public override string FormatPriceMarker(double price) => Instrument.MasterInstrument.FormatPrice(price);
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SessionHL[] cacheSessionHL;
		public SessionHL SessionHL()
		{
			return SessionHL(Input);
		}

		public SessionHL SessionHL(ISeries<double> input)
		{
			if (cacheSessionHL != null)
				for (int idx = 0; idx < cacheSessionHL.Length; idx++)
					if (cacheSessionHL[idx] != null &&  cacheSessionHL[idx].EqualsInput(input))
						return cacheSessionHL[idx];
			return CacheIndicator<SessionHL>(new SessionHL(), input, ref cacheSessionHL);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SessionHL SessionHL()
		{
			return indicator.SessionHL(Input);
		}

		public Indicators.SessionHL SessionHL(ISeries<double> input )
		{
			return indicator.SessionHL(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SessionHL SessionHL()
		{
			return indicator.SessionHL(Input);
		}

		public Indicators.SessionHL SessionHL(ISeries<double> input )
		{
			return indicator.SessionHL(input);
		}
	}
}

#endregion
