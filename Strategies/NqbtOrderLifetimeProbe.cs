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
	/// Records the full state timeline of resting entry orders so the four order-lifetime
	/// questions in issue #67 can be answered by measurement. Places no bracket.
	///
	/// Reflection over StrategyBase settled the API -- the long-form Enter*StopMarket overload
	/// carrying isLiveUntilCancelled, CancelOrder(Order), and OrderState's 16 members. It
	/// settles nothing about behaviour, which is what this exists for.
	///
	/// A Trades export cannot answer three of the four, because they are questions about
	/// cancels and a trade list only carries fills. So the probe writes its own event log from
	/// OnOrderUpdate instead: every callback, with the bar it arrived on, NinjaTrader's own
	/// event stamp, and the order's IsLiveUntilCancelled read back off the order rather than
	/// assumed from the overload that placed it.
	///
	/// Three scenarios, one per run, selected by Scenario:
	///
	///   1  Lifetime          One unreachable LUC buy stop. Resubmitted only once the previous
	///                        one reaches a terminal state, so the pattern of terminations is
	///                        the answer: one order for the whole run means LUC is honoured,
	///                        one per bar means it is not, one per session says the boundary
	///                        ends it. Run twice, IsExitOnSessionClose true and false.
	///   2  CancelTiming      An LUC buy stop and a plain three-argument one at the same
	///                        trigger, with CancelOrder called on the LUC one CancelAfterBars
	///                        later. Whether either fills on the bar its cancel was issued is
	///                        read off the bar log; the plain order is the control that
	///                        reproduces the one-bar expiry already reconciled.
	///   3  OppositeDirection An LUC buy stop above and an LUC sell stop below. When one fills,
	///                        the other's next state distinguishes "cancels the resting
	///                        opposite entry" from "refuses the second fill".
	///   5  SessionCross      The same order, submitted only on the session's last bar, so it
	///                        rests into the next session's opening bar. Scenario 4 reached
	///                        that case twice in 49,898 trials because its two-bar cadence
	///                        has fixed parity against a constant session length.
	///   4  SessionEdge       A plain three-argument buy stop resubmitted on every flat bar, so
	///                        that orders rest into the session's last bar and across the
	///                        boundary -- which scenarios 1 to 3 never did, because something
	///                        was always still live there. At HoldBars 0 it measures whether a
	///                        resting entry can fill on a force-flat bar, which is the gate
	///                        `deadcat.py` already gates fills behind. At a large HoldBars the
	///                        position is carried to the close instead, so the bar
	///                        IsExitOnSessionCloseStrategy flattens on is observed directly.
	///
	/// **Order callbacks report a bar index one behind the bar being processed.** Measured at
	/// 535 fills out of 535: the bar an ORDER_UPDATE or EXECUTION row carries never reaches the
	/// trigger and the next one always does, because Strategy Analyzer processes historical
	/// fills and cancels for bar i+1 before calling OnBarUpdate(i+1), while CurrentBar still
	/// reads i. SUBMIT and CANCEL_REQUEST rows come from OnBarUpdate and carry no such lag, so
	/// add one bar to the callback rows before comparing the two.
	///
	/// Run in Strategy Analyzer over one contract, 1 minute, Standard fill resolution; the
	/// settings that produced each stored run are in docs/nt8-fidelity.md.
	/// </summary>
	public class NqbtOrderLifetimeProbe : Strategy
	{
		/// <summary>Where the two CSVs are written. The one thing to edit.</summary>
		private const string OutputFolder = @"C:\Users\matty\Documents\Trading Tools\verification\nt8_order_lifetime";

		private const int ScenarioLifetime = 1;
		private const int ScenarioCancelTiming = 2;
		private const int ScenarioOppositeDirection = 3;
		private const int ScenarioSessionEdge = 4;
		private const int ScenarioSessionCross = 5;

		private const string EventHeader =
			"kind;trial;bar;bar_utc;bar_local;is_first_bar_of_session;signal_name;submitted_luc;" +
			"order_luc;order_id;order_action;order_type;stop_price;limit_price;quantity;filled;" +
			"average_fill_price;order_state;event_utc;event_local;error;comment;is_last_bar_of_session";

		/// <summary>What the strategy properties actually read at run time.
		///
		/// Strategy Analyzer carries its own Exit-on-close fields separate from the strategy's,
		/// so a property assigned in State.Configure cannot be assumed to be the one in force.
		/// Recording the effective values settles that from the run itself, rather than from a
		/// second run whose only difference is which control was touched.</summary>
		private const string ConfigHeader =
			"stage;is_exit_on_session_close_strategy;exit_on_session_close_seconds;" +
			"entries_per_direction;entry_handling;calculate;order_fill_resolution;slippage;" +
			"time_in_force;bars_required_to_trade";

		private const string BarHeader =
			"bar;utc;local;is_first_bar_of_session;open;high;low;close;volume;" +
			"market_position;position_quantity;primary_state;secondary_state;is_last_bar_of_session";

		private const string Submit = "SUBMIT";
		private const string CancelRequest = "CANCEL_REQUEST";
		private const string OrderUpdate = "ORDER_UPDATE";
		private const string Execution = "EXECUTION";

		private const string PrimaryName = "probe1";
		private const string SecondaryName = "probe2";

		private StringBuilder eventRows;
		private StringBuilder barRows;
		private StringBuilder configRows;

		/// <summary>The order placed by the long-form overload in every scenario.</summary>
		private Order primary;

		/// <summary>Scenario 2's plain three-argument control, scenario 3's opposite side.</summary>
		private Order secondary;

		private int trial;
		private int primarySubmitBar = -1;

		/// <summary>Bar the current position was first seen open on, for HoldBars. -1 when flat.</summary>
		private int positionOpenedBar = -1;
		private bool cancelIssued;
		private bool exitSubmitted;

		/// <summary>NinjaTrader's *display* zone, which is what bar and event times are in.
		/// Reading the trading-hours zone instead put every bar of an earlier export 5 hours
		/// out -- see NqbtHistoricalExporter, which this conversion is lifted from.</summary>
		private TimeZoneInfo displayZone;

		private DateTime previousBarUtc = DateTime.MinValue;
		private DateTime firstUtc = DateTime.MinValue;
		private DateTime lastUtc = DateTime.MinValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Records the state timeline of resting entry orders -- nqbt issue #67";
				Name										= "NqbtOrderLifetimeProbe";
				Calculate									= Calculate.OnBarClose;
				EntryHandling								= EntryHandling.AllEntries;
				IsFillLimitOnTouch							= false;
				MaximumBarsLookBack							= MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution							= OrderFillResolution.Standard;
				Slippage									= 0;
				StartBehavior								= StartBehavior.WaitUntilFlat;
				TimeInForce									= TimeInForce.Gtc;
				RealtimeErrorHandling						= RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling							= StopTargetHandling.PerEntryExecution;
				// Nothing here reads an indicator, and the warm-up bars are as good a place to
				// rest an order as any.
				BarsRequiredToTrade							= 0;
				IsInstantiatedOnEachOptimizationIteration	= false;

				Scenario = ScenarioLifetime;
				IsExitOnSessionClose = true;
				// Beyond the whole window's range, so nothing but NinjaTrader can end the order.
				LifetimeOffsetPoints = 2000;
				TriggerOffsetTicks = 20;
				CancelAfterBars = 2;
				MaxEntriesPerDirection = 2;
				SessionCloseSeconds = 30;
				MaxTrials = 500;
				HoldBars = 0;
				TraceProbeOrders = false;
			}
			else if (State == State.Configure)
			{
				// All three are set here rather than in SetDefaults because all three are answers
				// under test rather than settings: the flag and the seconds separate "the
				// session-close handler cancels it" from "the boundary ends it regardless", and
				// the entry count is the confound in the opposite-direction refusal.
				IsExitOnSessionCloseStrategy = IsExitOnSessionClose;
				ExitOnSessionCloseSeconds = SessionCloseSeconds;
				EntriesPerDirection = MaxEntriesPerDirection;
				TraceOrders = TraceProbeOrders;
			}
			else if (State == State.DataLoaded)
			{
				displayZone = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;

				// Reset rather than rely on the field initialisers: the instance is reused
				// between runs at IsInstantiatedOnEachOptimizationIteration = false.
				primary = null;
				secondary = null;
				trial = 0;
				primarySubmitBar = -1;
				positionOpenedBar = -1;
				cancelIssued = false;
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

			// Written after the bar's work rather than before it, and on every path. Recording
			// first skipped each bar the probe was flat on with nothing live, and those are exactly
			// the bars following a session-close cancellation -- the session edge being measured.
			RecordBar(utc);
		}

		/// <summary>The bar's decision: leave a position, cancel, or open the next trial.</summary>
		private void Advance(DateTime utc)
		{
			ReleaseTerminatedOrders();

			if (Position.MarketPosition != MarketPosition.Flat)
			{
				if (positionOpenedBar < 0)
					positionOpenedBar = CurrentBar;

				// At HoldBars 0 the position leaves at the first opportunity, which is what
				// scenarios 1 to 3 were measured under. Above that it is carried, so that the
				// session-close handler is what ends it rather than the probe.
				if (CurrentBar - positionOpenedBar >= HoldBars)
					ExitPosition();

				return;
			}
			exitSubmitted = false;
			positionOpenedBar = -1;

			if (Scenario == ScenarioCancelTiming && ShouldCancelNow())
			{
				RecordEvent(CancelRequest, primary, PrimaryName, true, utc);
				CancelOrder(primary);
				cancelIssued = true;
				return;
			}

			if (!IsOrderTerminal(primary) || !IsOrderTerminal(secondary))
				return;

			if (trial >= MaxTrials)
				return;

			// Scenario 5 exists because scenario 4's cadence never lands here: submit, rest,
			// cancel is a two-bar cycle and a session is a constant 1,380 bars, so the parity
			// is fixed and the close bar was a resting bar every time.
			if (Scenario == ScenarioSessionCross && !Bars.IsLastBarOfSession)
				return;

			SubmitTrial(utc);
		}

		/// <summary>True once the LUC order has rested for CancelAfterBars and is still live.</summary>
		private bool ShouldCancelNow()
		{
			return !cancelIssued
				&& primary != null
				&& primarySubmitBar >= 0
				&& CurrentBar - primarySubmitBar >= CancelAfterBars
				&& !IsOrderTerminal(primary);
		}

		private void ExitPosition()
		{
			if (exitSubmitted)
				return;

			if (Position.MarketPosition == MarketPosition.Long)
				ExitLong();
			else
				ExitShort();

			exitSubmitted = true;
		}

		private void SubmitTrial(DateTime utc)
		{
			trial++;
			primarySubmitBar = CurrentBar;
			cancelIssued = false;

			if (Scenario == ScenarioLifetime)
			{
				// Deliberately unreachable, so that anything ending this order is NinjaTrader
				// and not the market.
				double unreachable = Close[0] + LifetimeOffsetPoints;
				RecordSubmit(PrimaryName, true, OrderAction.Buy, unreachable, utc);
				primary = EnterLongStopMarket(0, true, 1, unreachable, PrimaryName);
				return;
			}

			double offset = TriggerOffsetTicks * TickSize;

			if (Scenario == ScenarioCancelTiming)
			{
				double trigger = High[0] + offset;
				RecordSubmit(PrimaryName, true, OrderAction.Buy, trigger, utc);
				primary = EnterLongStopMarket(0, true, 1, trigger, PrimaryName);
				RecordSubmit(SecondaryName, false, OrderAction.Buy, trigger, utc);
				secondary = EnterLongStopMarket(1, trigger, SecondaryName);
				return;
			}

			if (Scenario == ScenarioSessionEdge || Scenario == ScenarioSessionCross)
			{
				double edge = High[0] + offset;
				RecordSubmit(PrimaryName, false, OrderAction.Buy, edge, utc);
				primary = EnterLongStopMarket(1, edge, PrimaryName);
				return;
			}

			double above = High[0] + offset;
			double below = Low[0] - offset;
			RecordSubmit(PrimaryName, true, OrderAction.Buy, above, utc);
			primary = EnterLongStopMarket(0, true, 1, above, PrimaryName);
			RecordSubmit(SecondaryName, true, OrderAction.SellShort, below, utc);
			secondary = EnterShortStopMarket(0, true, 1, below, SecondaryName);
		}

		/// <summary>Terminal is enumerated rather than inferred from "not Filled".
		///
		/// OrderState has 16 members and treating everything that is not Filled as still live
		/// is how a stale reference gets cancelled after it already went -- docs/roadmap.md
		/// § "Order lifetime in NT8", route 1.</summary>
		private static bool IsTerminalOrderState(OrderState state)
		{
			return state == OrderState.Filled || state == OrderState.Cancelled || state == OrderState.Rejected;
		}

		private static bool IsOrderTerminal(Order order)
		{
			return order == null || IsTerminalOrderState(order.OrderState);
		}

		private void ReleaseTerminatedOrders()
		{
			if (primary != null && IsOrderTerminal(primary))
				primary = null;

			if (secondary != null && IsOrderTerminal(secondary))
				secondary = null;
		}

		protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
			int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
		{
			if (eventRows == null || CurrentBar < 0)
				return;

			// The Enter methods return the order, but the callback can arrive before that
			// assignment lands, so the reference is claimed by name here as well.
			if (order.Name == PrimaryName && primary == null && !IsTerminalOrderState(orderState))
				primary = order;
			else if (order.Name == SecondaryName && secondary == null && !IsTerminalOrderState(orderState))
				secondary = order;

			RecordEvent(OrderUpdate, order, order.Name, order.IsLiveUntilCancelled, time, error, comment);
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
			int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (eventRows == null || CurrentBar < 0 || execution.Order == null)
				return;

			RecordEvent(Execution, execution.Order, execution.Order.Name, execution.Order.IsLiveUntilCancelled,
				time, ErrorCode.NoError, executionId);
		}

		private void RecordSubmit(string signalName, bool luc, OrderAction action, double stopPrice, DateTime utc)
		{
			eventRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1};{2};{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};{5};{6};{7};;;{8};StopMarket;{9};;1;;;;{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};;{10}\n",
				Submit,
				trial,
				CurrentBar,
				utc,
				Time[0],
				Bars.IsFirstBarOfSession ? 1 : 0,
				signalName,
				luc ? 1 : 0,
				action,
				stopPrice,
				Bars.IsLastBarOfSession ? 1 : 0));
		}

		private void RecordEvent(string kind, Order order, string signalName, bool luc, DateTime eventTime)
		{
			RecordEvent(kind, order, signalName, luc, eventTime, ErrorCode.NoError, string.Empty);
		}

		private void RecordEvent(string kind, Order order, string signalName, bool luc, DateTime eventTime,
			ErrorCode error, string comment)
		{
			if (order == null)
				return;

			eventRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1};{2};{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};{5};{6};;{7};{8};{9};{10};{11};{12};{13};{14};{15};{16};{17:yyyyMMdd HHmmss};{18:yyyyMMdd HHmmss};{19};{20};{21}\n",
				kind,
				trial,
				CurrentBar,
				ToUtcUnordered(Time[0]),
				Time[0],
				Bars.IsFirstBarOfSession ? 1 : 0,
				signalName,
				luc ? 1 : 0,
				order.Id,
				order.OrderAction,
				order.OrderType,
				order.StopPrice,
				order.LimitPrice,
				order.Quantity,
				order.Filled,
				order.AverageFillPrice,
				order.OrderState,
				ToUtcUnordered(eventTime),
				eventTime,
				error,
				Clean(comment),
				Bars.IsLastBarOfSession ? 1 : 0));
		}

		/// <summary>Bars where the probe had something live, which is all the analysis needs and
		/// keeps the file three orders of magnitude smaller than one row per bar.</summary>
		private void RecordBar(DateTime utc)
		{
			barRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1:yyyyMMdd HHmmss};{2:yyyyMMdd HHmmss};{3};{4};{5};{6};{7};{8};{9};{10};{11};{12};{13}\n",
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
				primary == null ? "none" : primary.OrderState.ToString(),
				secondary == null ? "none" : secondary.OrderState.ToString(),
				Bars.IsLastBarOfSession ? 1 : 0));
		}

		private void RecordConfig(string stage)
		{
			if (configRows == null)
				return;

			configRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1};{2};{3};{4};{5};{6};{7};{8};{9}\n",
				stage,
				IsExitOnSessionCloseStrategy,
				ExitOnSessionCloseSeconds,
				EntriesPerDirection,
				EntryHandling,
				Calculate,
				OrderFillResolution,
				Slippage,
				TimeInForce,
				BarsRequiredToTrade));
		}

		/// <summary>A semicolon or newline inside a comment would shift every later column.</summary>
		private static string Clean(string text)
		{
			if (string.IsNullOrEmpty(text))
				return string.Empty;

			return text.Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');
		}

		/// <summary>Display-zone time to UTC, resolving both DST edges.
		///
		/// Lifted from NqbtHistoricalExporter, where each half was paid for: the hour a
		/// spring-forward skips does not exist locally and ConvertTimeToUtc throws on it rather
		/// than coping, and the autumn repeated hour resolves to standard time, which puts its
		/// first pass an hour late. Bar times increase strictly, so a step backwards identifies
		/// that second case -- but only trust it inside the ambiguous hour, so an unrelated
		/// ordering fault stays visible rather than being nudged away.</summary>
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

		/// <summary>The same conversion where the caller has no ordering to lean on.
		///
		/// Event stamps repeat and interleave within a bar, so the ambiguous-hour rule above
		/// cannot apply. One event per autumn changeover is therefore an hour late here; join on
		/// the bar file, which is ordered, rather than trusting this column across that
		/// boundary.</summary>
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
			string stem = string.Format(CultureInfo.InvariantCulture,
				"{0}_s{1}_eosc{2}_secs{3}_epd{4}_hold{5}_off{6}_{7:yyyyMMdd}_{8:yyyyMMdd}",
				Instrument.FullName.Replace(' ', '-'),
				Scenario,
				IsExitOnSessionClose ? 1 : 0,
				SessionCloseSeconds,
				MaxEntriesPerDirection,
				HoldBars,
				TriggerOffsetTicks,
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
				"NqbtOrderLifetimeProbe: {0} trials, wrote {1}_events.csv and {1}_bars.csv to {2}",
				trial, stem, OutputFolder));

			eventRows = null;
			barRows = null;
			configRows = null;
		}

		#region Properties

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "Scenario (1 lifetime, 2 cancel, 3 opposite, 4 session edge, 5 session cross)", Order = 1, GroupName = "Parameters")]
		public int Scenario { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "IsExitOnSessionClose", Order = 2, GroupName = "Parameters")]
		public bool IsExitOnSessionClose { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "LifetimeOffsetPoints", Order = 3, GroupName = "Parameters")]
		public double LifetimeOffsetPoints { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "TriggerOffsetTicks", Order = 4, GroupName = "Parameters")]
		public int TriggerOffsetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "CancelAfterBars", Order = 5, GroupName = "Parameters")]
		public int CancelAfterBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MaxTrials", Order = 6, GroupName = "Parameters")]
		public int MaxTrials { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "HoldBars", Order = 7, GroupName = "Parameters")]
		public int HoldBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MaxEntriesPerDirection", Order = 8, GroupName = "Parameters")]
		public int MaxEntriesPerDirection { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "SessionCloseSeconds", Order = 9, GroupName = "Parameters")]
		public int SessionCloseSeconds { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "TraceProbeOrders", Order = 10, GroupName = "Parameters")]
		public bool TraceProbeOrders { get; set; }

		#endregion
	}
}
