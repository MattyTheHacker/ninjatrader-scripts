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
	public class CustomBarTimer : Indicator
	{
		private DateTime now = Core.Globals.Now;
		private bool isConnected, hasRealTimeData;
		private SessionIterator sessionIterator;
		private SessionIterator SessionIterator => sessionIterator ??= new SessionIterator(Bars);
		private System.Windows.Threading.DispatcherTimer timer;
		private string lastTime = string.Empty;
		private string timeLeft = string.Empty;
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
				Description									= @"Improved BarTimer for efficiency and improved UX.";
				Name										= "CustomBarTimer";
				Calculate									= Calculate.OnEachTick;
				IsOverlay									= false;
				DisplayInDataBox							= false;
				DrawOnPricePanel							= false;
				DrawHorizontalGridLines						= true;
				DrawVerticalGridLines						= true;
				PaintPriceMarkers							= false;
				ScaleJustification							= NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive					= true;
			}
			else if (State == State.Realtime)
			{
				if (timer == null && IsVisible)
				{
					lock (Connection.Connections)
					{
						if (Connection.Connections.ToList().FirstOrDefault(
							connection => connection.Status == ConnectionStatus.Connected && 
							connection.InstrumentTypes.Contains(Instrument.MasterInstrument.InstrumentType)) == null)
						{
							Draw.TextFixedFine(
								this,
								"BarTimerInfo",
								"Disconnected",
								TextPositionFine,
								ChartControl.Properties.ChartText,
								ChartControl.Properties.LabelFont,
								Brushes.Transparent,
								Brushes.Transparent,
								0
							);

							isConnected = false;
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
				isConnected = true;
			}
		}

		private bool DisplayTime() => ChartControl != null && Bars?.Instrument.MarketData != null && IsVisible;

		private void OnTimerTick(object sender, EventArgs eventArgs)
		{
			if (DisplayTime())
			{
				if (!isConnected)
				{
					Draw.TextFixedFine(
						this,
						"BarTimerInfo",
						"Market Data Disconnected",
						TextPositionFine,
						ChartControl.Properties.ChartText,
						ChartControl.Properties.LabelFont,
						Brushes.Transparent,
						Brushes.Transparent,
						0
					);

					if (timer != null) timer.IsEnabled = false;

					return;
				}

				if(!SessionIterator.IsInSession(Now, false, true))
				{
					Draw.TextFixedFine(
						this,
						"BarTimerInfo",
						"Session Closed",
						TextPositionFine,
						ChartControl.Properties.ChartText,
						ChartControl.Properties.LabelFont,
						Brushes.Transparent,
						Brushes.Transparent,
						0
					);

					return;
				}

				if(!hasRealTimeData)
				{
					Draw.TextFixedFine(
						this,
						"BarTimerInfo",
						"Waiting for data",
						TextPositionFine,
						ChartControl.Properties.ChartText,
						ChartControl.Properties.LabelFont,
						Brushes.Transparent,
						Brushes.Transparent,
						0
					);

					return;
				}

				TimeSpan barTimeLeft = Bars.GetTime(Bars.Count - 1).Subtract(Now);

				if (barTimeLeft.Days > 0)
				{
					Draw.TextFixedFine(
						this,
						"BarTimerInfo",
						"Time to bar close exceeds 1 day",
						TextPositionFine,
						ChartControl.Properties.ChartText,
						ChartControl.Properties.LabelFont,
						Brushes.Transparent,
						Brushes.Transparent,
						0
					);
					return;
				}

				timeLeft = barTimeLeft.Ticks < 0
					? "00:00:00" 
					: barTimeLeft.ToString(@"hh\:mm\:ss");

				if (timeLeft == lastTime) return;

				Draw.TextFixedFine(
					this,
					"BarTimerInfo",
					"Time to bar close: " + timeLeft,
					TextPositionFine,
					ChartControl.Properties.ChartText,
					ChartControl.Properties.LabelFont,
					Brushes.Transparent,
					Brushes.Transparent,
					0
				);

				lastTime = timeLeft;
			}
		}

        protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs connectionStatusUpdate)
		{
			if (
				connectionStatusUpdate.PriceStatus == ConnectionStatus.Connected &&
				connectionStatusUpdate.Connection.InstrumentTypes.Contains(Instrument.MasterInstrument.InstrumentType) &&
				Bars.BarsType.IsIntraday
			)
			{
				isConnected = true;

				if (DisplayTime() && timer == null)
				{
					ChartControl.Dispatcher.InvokeAsync(() =>
					{
						timer = new System.Windows.Threading.DispatcherTimer { Interval = new TimeSpan(0, 0, 1), IsEnabled = true };
						timer.Tick += OnTimerTick;
					});
				}
			} else if (connectionStatusUpdate.PriceStatus == ConnectionStatus.Disconnected) isConnected = false;
		}

		#region Properties
		[Display(ResourceType = typeof(Custom.Resource), Name = "GuiPropertyNameTextPosition", GroupName = "PropertyCategoryVisual", Order = 70)]
		public TextPositionFine TextPositionFine { get; set; }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private CustomBarTimer[] cacheCustomBarTimer;
		public CustomBarTimer CustomBarTimer()
		{
			return CustomBarTimer(Input);
		}

		public CustomBarTimer CustomBarTimer(ISeries<double> input)
		{
			if (cacheCustomBarTimer != null)
				for (int idx = 0; idx < cacheCustomBarTimer.Length; idx++)
					if (cacheCustomBarTimer[idx] != null &&  cacheCustomBarTimer[idx].EqualsInput(input))
						return cacheCustomBarTimer[idx];
			return CacheIndicator<CustomBarTimer>(new CustomBarTimer(), input, ref cacheCustomBarTimer);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.CustomBarTimer CustomBarTimer()
		{
			return indicator.CustomBarTimer(Input);
		}

		public Indicators.CustomBarTimer CustomBarTimer(ISeries<double> input )
		{
			return indicator.CustomBarTimer(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.CustomBarTimer CustomBarTimer()
		{
			return indicator.CustomBarTimer(Input);
		}

		public Indicators.CustomBarTimer CustomBarTimer(ISeries<double> input )
		{
			return indicator.CustomBarTimer(input);
		}
	}
}

#endregion
