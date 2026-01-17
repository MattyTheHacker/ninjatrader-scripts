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
	public class SessionCountdown : Indicator
	{
		private DateTime now = Core.Globals.Now;
		private bool connected, hasRealTimeData;
		private SessionIterator sessionIterator;
		private System.Windows.Threading.DispatcherTimer timer;

		private SessionIterator SessionIterator => sessionIterator ??= new SessionIterator(Bars);

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

		private bool DisplayTime() => ChartControl != null && Bars?.Instrument.MarketData != null && IsVisible;

		private void OnTimerTick(object sender, EventArgs eventArgs)
		{
			ForceRefresh();

			if (DisplayTime())
			{
				if (timer is { IsEnabled: false }) timer.IsEnabled = true;

				if (!connected)
				{
					Draw.TextFixedFine(
						this,
						"SessionTimerInfo",
						"Market Data Disconnected.",
						TextPositionFine,
						ChartControl.Properties.ChartText,
						ChartControl.Properties.LabelFont,
						Brushes.Transparent,
						Brushes.Transparent,
						0
					);

					if (timer != null)
						timer.IsEnabled = false;

					return;
				}

				bool inSession = SessionIterator.IsInSession(Now, false, true);

				SessionIterator.GetNextSession(Now, inSession);

				DateTime targetTime = inSession ? SessionIterator.ActualSessionEnd : SessionIterator.ActualSessionBegin;
				TimeSpan remainingTime = targetTime - Now;

				string prefix = inSession ? "Time to session close: " : "Time to session open: ";

				string remainingTimeString = remainingTime.Ticks < 0
					? "00:00:00:00"
					: remainingTime.Days.ToString("00") + ":" + 
					  remainingTime.Hours.ToString("00") + ":" +
					  remainingTime.Minutes.ToString("00") + ":" +
					  remainingTime.Seconds.ToString("00");

				Draw.TextFixedFine(
					this,
					"SessionTimerInfo",
					prefix + remainingTimeString,
					TextPositionFine,
					ChartControl.Properties.ChartText,
					ChartControl.Properties.LabelFont,
					Brushes.Transparent,
					Brushes.Transparent,
					0
				);
			}
		}

		#region Properties
		[Display(ResourceType = typeof(Custom.Resource), Name = "GuiPropertyNameTextPosition", GroupName = "PropertyCategoryVisual", Order = 70)]
		public TextPositionFine TextPositionFine { get; set; }
		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Displays time to session close and re-open on the chart.";
				Name										= "SessionCountdown";
				Calculate									= Calculate.OnEachTick;
				IsOverlay									= true;
				IsChartOnly									= true;
				DisplayInDataBox							= false;
				DrawOnPricePanel							= false;
				DrawHorizontalGridLines						= true;
				DrawVerticalGridLines						= true;
				PaintPriceMarkers							= false;
				ScaleJustification							= NinjaTrader.Gui.Chart.ScaleJustification.Left;
				IsSuspendedWhileInactive					= true;
			}

			else if (State == State.Realtime)
			{
				if (timer == null && IsVisible)
				{
					lock (Connection.Connections)
					{
						if (Connection.Connections.ToList()
							.FirstOrDefault(c => c.Status == ConnectionStatus.Connected &&
								c.InstrumentTypes.Contains(Instrument.MasterInstrument.InstrumentType)) == null)
						{
							Draw.TextFixedFine(this, "SessionTimerInfo",
								"Disconnected",
								TextPositionFine,
								ChartControl.Properties.ChartText,
								ChartControl.Properties.LabelFont,
								Brushes.Transparent,
								Brushes.Transparent,
								0);
						}
					}
				}
			}

			else if (State == State.Terminated)
			{
				if (timer != null)
				{
					timer.IsEnabled = false;
					timer = null;
				}
			}
		}

		protected override void OnBarUpdate()
		{
			if (State == State.Realtime)
			{
				hasRealTimeData = true;
				connected = true;
			}
		}

        protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs connectionStatusUpdate)
		{
			if (
				connectionStatusUpdate.PriceStatus == ConnectionStatus.Connected
				&& connectionStatusUpdate.Connection.InstrumentTypes.Contains(Instrument.MasterInstrument.InstrumentType)
				&& Bars.BarsType.IsIntraday
			)
			{
				connected = true;

				if (DisplayTime() && timer == null)
				{
					ChartControl.Dispatcher.InvokeAsync(() =>
					{
						timer = new System.Windows.Threading.DispatcherTimer { Interval = new TimeSpan(0,0,1), IsEnabled = true };
						timer.Tick += OnTimerTick;
					});
				}
			} else if (connectionStatusUpdate.PriceStatus == ConnectionStatus.Disconnected)
			{
				connected = false;
			}
		}
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SessionCountdown[] cacheSessionCountdown;
		public SessionCountdown SessionCountdown()
		{
			return SessionCountdown(Input);
		}

		public SessionCountdown SessionCountdown(ISeries<double> input)
		{
			if (cacheSessionCountdown != null)
				for (int idx = 0; idx < cacheSessionCountdown.Length; idx++)
					if (cacheSessionCountdown[idx] != null &&  cacheSessionCountdown[idx].EqualsInput(input))
						return cacheSessionCountdown[idx];
			return CacheIndicator<SessionCountdown>(new SessionCountdown(), input, ref cacheSessionCountdown);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SessionCountdown SessionCountdown()
		{
			return indicator.SessionCountdown(Input);
		}

		public Indicators.SessionCountdown SessionCountdown(ISeries<double> input )
		{
			return indicator.SessionCountdown(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SessionCountdown SessionCountdown()
		{
			return indicator.SessionCountdown(Input);
		}

		public Indicators.SessionCountdown SessionCountdown(ISeries<double> input )
		{
			return indicator.SessionCountdown(input);
		}
	}
}

#endregion
