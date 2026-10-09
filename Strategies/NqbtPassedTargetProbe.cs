#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

//This namespace holds Strategies in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Strategies
{
	/// <summary>
	/// Records where NinjaTrader fills a profit target the market has already passed, so nqbt
	/// issues #452 and #244 can be answered by measurement. Places no stop.
	///
	/// No reconciled trade list holds such a target, because front-month 1-minute bars rarely
	/// open past one, so the probe puts the target there on purpose. Two scenarios, one per run,
	/// selected by Scenario. Trials alternate long and short.
	///
	///   1  EntryBar  (#452) At a flat bar's close, a target PassedOffsetTicks behind that close,
	///                then a market entry. The entry fills at the next bar's open, which the
	///                target is already behind unless that bar opened the other way by more than
	///                the offset.
	///   2  Resting   (#244) A market entry with its target out of reach, then, at the close of
	///                every bar the position is held, the target moved to GapOffsetTicks beyond
	///                that close. A bar opening further out than the target has gapped through
	///                it; a bar opening short of it and trading through is the control.
	///
	/// A position still open HoldBars bars after its entry is exited at market, so a target
	/// NinjaTrader refuses or leaves resting cannot stall the run.
	///
	/// Order callbacks report a bar one behind the bar they filled on -- NqbtOrderLifetimeProbe
	/// measured it. tools/reconcile_passed_target.py re-measures it on every run.
	///
	/// Run in Strategy Analyzer over one contract, 1 minute, Standard fill resolution, zero costs.
	/// </summary>
	public class NqbtPassedTargetProbe : Strategy
	{
		/// <summary>Where the three CSVs are written. The one thing to edit.</summary>
		private const string OutputFolder = @"C:\Users\matty\Documents\Trading Tools\verification\nt8_passed_target";

		private const int ScenarioEntryBar = 1;
		private const int ScenarioResting = 2;

		private const string EventHeader =
			"kind;trial;bar;bar_utc;bar_local;signal_name;from_entry_signal;order_id;order_action;" +
			"order_type;limit_price;quantity;filled;average_fill_price;execution_price;order_state;" +
			"event_utc;event_local;error;comment;is_last_bar_of_session";

		private const string ConfigHeader =
			"stage;is_exit_on_session_close_strategy;exit_on_session_close_seconds;" +
			"entries_per_direction;entry_handling;stop_target_handling;calculate;" +
			"order_fill_resolution;is_fill_limit_on_touch;slippage;bars_required_to_trade";

		private const string BarHeader =
			"bar;utc;local;is_first_bar_of_session;open;high;low;close;volume;" +
			"market_position;position_quantity;is_last_bar_of_session";

		private const string Submit = "SUBMIT";
		private const string TargetSet = "TARGET_SET";
		private const string OrderUpdate = "ORDER_UPDATE";
		private const string Execution = "EXECUTION";

		private const string LongName = "probeLong";
		private const string ShortName = "probeShort";
		private const string ExitName = "probeExit";

		private StringBuilder eventRows;
		private StringBuilder barRows;
		private StringBuilder configRows;

		private int trial;
		private string entryName;

		/// <summary>+1 for a long trial, -1 for a short one.</summary>
		private double entrySide;

		/// <summary>Bar the current position was first seen open on, for HoldBars. -1 when flat.</summary>
		private int positionOpenedBar = -1;
		private bool exitSubmitted;

		/// <summary>NinjaTrader's *display* zone, which is what bar and event times are in --
		/// see NqbtOrderLifetimeProbe, which this conversion is lifted from.</summary>
		private TimeZoneInfo displayZone;

		private DateTime previousBarUtc = DateTime.MinValue;
		private DateTime firstUtc = DateTime.MinValue;
		private DateTime lastUtc = DateTime.MinValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Records where a profit target the market has passed fills -- nqbt issues #452 and #244";
				Name										= "NqbtPassedTargetProbe";
				Calculate									= Calculate.OnBarClose;
				EntriesPerDirection							= 1;
				EntryHandling								= EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy				= true;
				ExitOnSessionCloseSeconds					= 30;
				IsFillLimitOnTouch							= false;
				MaximumBarsLookBack							= MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution							= OrderFillResolution.Standard;
				Slippage									= 0;
				StartBehavior								= StartBehavior.WaitUntilFlat;
				TimeInForce									= TimeInForce.Gtc;
				RealtimeErrorHandling						= RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling							= StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade							= 0;
				IsInstantiatedOnEachOptimizationIteration	= false;

				Scenario = ScenarioEntryBar;
				PassedOffsetTicks = 4;
				GapOffsetTicks = 1;
				// Beyond the whole window's range, so the entry bar never reaches it.
				OutOfReachPoints = 2000;
				HoldBars = 30;
				MaxTrials = 5000;
			}
			else if (State == State.DataLoaded)
			{
				displayZone = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;

				// Reset rather than rely on the field initialisers: the instance is reused
				// between runs at IsInstantiatedOnEachOptimizationIteration = false.
				trial = 0;
				entryName = null;
				entrySide = 0;
				positionOpenedBar = -1;
				exitSubmitted = false;
				previousBarUtc = DateTime.MinValue;
				firstUtc = DateTime.MinValue;
				lastUtc = DateTime.MinValue;

				eventRows = new StringBuilder(1 << 20);
				eventRows.Append(EventHeader).Append('\n');
				barRows = new StringBuilder(1 << 20);
				barRows.Append(BarHeader).Append('\n');
				configRows = new StringBuilder(1 << 10);
				configRows.Append(ConfigHeader).Append('\n');
				RecordConfig("DataLoaded");
			}
			else if (State == State.Terminated)
			{
				Write();
			}
		}

		protected override void OnBarUpdate()
		{
			if (eventRows == null || BarsInProgress != 0 || CurrentBar < 0)
				return;

			DateTime utc = ToUtc(Time[0], ref previousBarUtc);
			if (firstUtc == DateTime.MinValue)
				firstUtc = utc;
			lastUtc = utc;

			if (CurrentBar == 0)
				RecordConfig("FirstBar");

			Advance(utc);
			RecordBar(utc);
		}

		/// <summary>The bar's decision: leave a position, move its target, or open the next trial.</summary>
		private void Advance(DateTime utc)
		{
			if (Position.MarketPosition != MarketPosition.Flat)
			{
				if (positionOpenedBar < 0)
					positionOpenedBar = CurrentBar;

				// The session-close handler flattens at this bar's close, so an exit sent here would
				// be left working into the next session.
				if (CurrentBar - positionOpenedBar >= HoldBars && !Bars.IsLastBarOfSession)
				{
					ExitPosition(utc);
					return;
				}

				if (Scenario == ScenarioResting)
					SetTarget(Close[0] + entrySide * GapOffsetTicks * TickSize, utc);

				return;
			}
			positionOpenedBar = -1;
			exitSubmitted = false;

			if (trial >= MaxTrials)
				return;

			// A market order sent here fills in the next session, so the trial would straddle
			// the boundary the session-close handler acts on.
			if (Bars.IsLastBarOfSession)
				return;

			SubmitTrial(utc);
		}

		private void SubmitTrial(DateTime utc)
		{
			trial++;
			bool isLong = trial % 2 == 1;
			entryName = isLong ? LongName : ShortName;
			entrySide = isLong ? 1 : -1;

			// The target has to be set before the entry it belongs to is sent, and reset on every
			// trial, or the previous trial's price is applied to the new position.
			double target = Scenario == ScenarioEntryBar
				? Close[0] - entrySide * PassedOffsetTicks * TickSize
				: Close[0] + entrySide * OutOfReachPoints;
			SetTarget(target, utc);

			RecordSubmit(entryName, isLong ? OrderAction.Buy : OrderAction.SellShort, utc);
			if (isLong)
				EnterLong(1, entryName);
			else
				EnterShort(1, entryName);
		}

		private void SetTarget(double price, DateTime utc)
		{
			SetProfitTarget(entryName, CalculationMode.Price, price);
			RecordTargetSet(price, utc);
		}

		private void ExitPosition(DateTime utc)
		{
			if (exitSubmitted)
				return;

			bool isLong = Position.MarketPosition == MarketPosition.Long;
			RecordSubmit(ExitName, isLong ? OrderAction.Sell : OrderAction.BuyToCover, utc);
			if (isLong)
				ExitLong(ExitName, entryName);
			else
				ExitShort(ExitName, entryName);

			exitSubmitted = true;
		}

		protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
			int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
		{
			if (eventRows == null || CurrentBar < 0)
				return;

			RecordEvent(OrderUpdate, order, double.NaN, time, error, comment);
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
			int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (eventRows == null || CurrentBar < 0 || execution.Order == null)
				return;

			RecordEvent(Execution, execution.Order, price, time, ErrorCode.NoError, executionId);
		}

		private void RecordSubmit(string signalName, OrderAction action, DateTime utc)
		{
			eventRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1};{2};{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};{5};{6};;{7};Market;;1;;;;;{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};;;{8}\n",
				Submit,
				trial,
				CurrentBar,
				utc,
				Time[0],
				signalName,
				signalName == ExitName ? entryName : string.Empty,
				action,
				Bars.IsLastBarOfSession ? 1 : 0));
		}

		private void RecordTargetSet(double price, DateTime utc)
		{
			eventRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1};{2};{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};;{5};;;Limit;{6};1;;;;;{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};;;{7}\n",
				TargetSet,
				trial,
				CurrentBar,
				utc,
				Time[0],
				entryName,
				price,
				Bars.IsLastBarOfSession ? 1 : 0));
		}

		/// <summary>One callback row. executionPrice is NaN on an order update, which has none.</summary>
		private void RecordEvent(string kind, Order order, double executionPrice, DateTime eventTime,
			ErrorCode error, string comment)
		{
			if (order == null)
				return;

			eventRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1};{2};{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};{5};{6};{7};{8};{9};{10};{11};{12};{13};{14};{15};{16:yyyyMMdd HHmmss};{17:yyyyMMdd HHmmss};{18};{19};{20}\n",
				kind,
				trial,
				CurrentBar,
				ToUtcUnordered(Time[0]),
				Time[0],
				Clean(order.Name),
				Clean(order.FromEntrySignal),
				order.Id,
				order.OrderAction,
				order.OrderType,
				order.LimitPrice,
				order.Quantity,
				order.Filled,
				order.AverageFillPrice,
				double.IsNaN(executionPrice) ? string.Empty : executionPrice.ToString("R", CultureInfo.InvariantCulture),
				order.OrderState,
				ToUtcUnordered(eventTime),
				eventTime,
				error,
				Clean(comment),
				Bars.IsLastBarOfSession ? 1 : 0));
		}

		private void RecordBar(DateTime utc)
		{
			barRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1:yyyyMMdd HHmmss};{2:yyyyMMdd HHmmss};{3};{4};{5};{6};{7};{8};{9};{10};{11}\n",
				CurrentBar,
				utc,
				Time[0],
				Bars.IsFirstBarOfSession ? 1 : 0,
				Open[0],
				High[0],
				Low[0],
				Close[0],
				Volume[0],
				Position.MarketPosition,
				Position.Quantity,
				Bars.IsLastBarOfSession ? 1 : 0));
		}

		private void RecordConfig(string stage)
		{
			if (configRows == null)
				return;

			configRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1};{2};{3};{4};{5};{6};{7};{8};{9};{10}\n",
				stage,
				IsExitOnSessionCloseStrategy,
				ExitOnSessionCloseSeconds,
				EntriesPerDirection,
				EntryHandling,
				StopTargetHandling,
				Calculate,
				OrderFillResolution,
				IsFillLimitOnTouch,
				Slippage,
				BarsRequiredToTrade));
		}

		/// <summary>A semicolon or newline inside a field would shift every later column.</summary>
		private static string Clean(string text)
		{
			if (string.IsNullOrEmpty(text))
				return string.Empty;

			return text.Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');
		}

		/// <summary>Display-zone time to UTC, resolving both DST edges -- lifted unchanged from
		/// NqbtOrderLifetimeProbe, whose summary says why each half is there.</summary>
		private DateTime ToUtc(DateTime barTime, ref DateTime previousUtc)
		{
			DateTime local = DateTime.SpecifyKind(barTime, DateTimeKind.Unspecified);
			if (displayZone.IsInvalidTime(local))
				local = local.AddHours(1);

			DateTime utc = TimeZoneInfo.ConvertTimeToUtc(local, displayZone);
			if (displayZone.IsAmbiguousTime(local) && previousUtc != DateTime.MinValue && utc <= previousUtc)
				utc = utc.AddHours(1);

			previousUtc = utc;
			return utc;
		}

		/// <summary>The same conversion where the caller has no ordering to lean on. One event per
		/// autumn changeover is an hour late here; join on the bar file instead.</summary>
		private DateTime ToUtcUnordered(DateTime barTime)
		{
			DateTime local = DateTime.SpecifyKind(barTime, DateTimeKind.Unspecified);
			if (displayZone.IsInvalidTime(local))
				local = local.AddHours(1);
			return TimeZoneInfo.ConvertTimeToUtc(local, displayZone);
		}

		private void Write()
		{
			if (eventRows == null || firstUtc == DateTime.MinValue)
				return;

			Directory.CreateDirectory(OutputFolder);

			int offsetTicks = Scenario == ScenarioEntryBar ? PassedOffsetTicks : GapOffsetTicks;
			string stem = string.Format(CultureInfo.InvariantCulture,
				"{0}_s{1}_off{2}_hold{3}_{4:yyyyMMdd}_{5:yyyyMMdd}",
				Instrument.FullName.Replace(' ', '-'),
				Scenario,
				offsetTicks,
				HoldBars,
				firstUtc,
				lastUtc);

			File.WriteAllText(Path.Combine(OutputFolder, stem + "_events.csv"),
				eventRows.ToString(), new UTF8Encoding(false));
			File.WriteAllText(Path.Combine(OutputFolder, stem + "_bars.csv"),
				barRows.ToString(), new UTF8Encoding(false));
			RecordConfig("Terminated");
			File.WriteAllText(Path.Combine(OutputFolder, stem + "_config.csv"),
				configRows.ToString(), new UTF8Encoding(false));

			Print(string.Format(CultureInfo.InvariantCulture,
				"NqbtPassedTargetProbe: {0} trials, wrote {1}_events.csv, _bars.csv and _config.csv to {2}",
				trial, stem, OutputFolder));

			eventRows = null;
			barRows = null;
			configRows = null;
		}

		#region Properties

		[NinjaScriptProperty]
		[Range(1, 2)]
		[Display(Name = "Scenario (1 entry bar, 2 resting)", Order = 1, GroupName = "Parameters")]
		public int Scenario { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "PassedOffsetTicks", Order = 2, GroupName = "Parameters")]
		public int PassedOffsetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "GapOffsetTicks", Order = 3, GroupName = "Parameters")]
		public int GapOffsetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "OutOfReachPoints", Order = 4, GroupName = "Parameters")]
		public double OutOfReachPoints { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "HoldBars", Order = 5, GroupName = "Parameters")]
		public int HoldBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MaxTrials", Order = 6, GroupName = "Parameters")]
		public int MaxTrials { get; set; }

		#endregion
	}
}
