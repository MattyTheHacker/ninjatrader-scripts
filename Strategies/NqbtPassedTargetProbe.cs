#region Using declarations
using System;
using System.Collections.Generic;
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
	/// Records where NinjaTrader fills a limit order the market has already passed -- a profit
	/// target (nqbt issues #452 and #244) or a limit entry (#454) -- and what it does with a stop
	/// loss its entry bar opened past, behind a limit entry or a stop entry (#455), so each can be
	/// answered by measurement. Only scenarios 5 to 9 place a stop.
	///
	/// No reconciled trade list holds such an order, because front-month 1-minute bars rarely
	/// open past one, so the probe puts the order there on purpose. Nine scenarios, one per run,
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
	///   3  LimitEntry (#454) At a flat bar's close, a limit entry GapOffsetTicks inside that close
	///                -- a buy limit below it, a sell limit above -- live for the next bar only. A
	///                bar opening beyond the limit has gapped through it, and one that only touches
	///                it says whether an entry limit, like a target, has to trade through.
	///   4  Marketable (#454) The same entry PassedOffsetTicks on the wrong side of the close -- a
	///                buy limit above it, a sell limit below -- so it is marketable when sent:
	///                whether NinjaTrader accepts it, and where it fills.
	///   5  StopInGap (#455) Scenario 3's limit entry with a stop loss StopOffsetTicks beyond the
	///                limit -- a sell stop under a buy limit, a buy stop over a sell limit -- set
	///                before the entry is sent. A bar opening beyond the stop has gapped past both,
	///                and one that also never trades back to the stop fills the entry on the wrong
	///                side of it, so the stop is sent past the market: whether NinjaTrader rejects
	///                it, fills it, or leaves it working, and at what price. A gapped bar that
	///                trades back to the stop, and one that trades to the limit and on through the
	///                stop, are the controls.
	///   6  StopEntry (#455) At a flat bar's close, a stop loss GapOffsetTicks beyond that close --
	///                a sell stop above it, a buy stop below -- then a stop entry StopOffsetTicks
	///                beyond the stop, live for the next bar only. A bar that opens on the far side
	///                of the stop from the entry and then trades through the entry has opened past
	///                its stop: whether the stop fills at that open or at its own price. A bar
	///                opening at the stop or beyond it, toward the entry, is the control.
	///   7  StopEntryTarget (#455) Scenario 6 with a profit target OutOfReachPoints beyond the
	///                entry as well, so the stop goes out beside a target as DeadCatBounce's and
	///                PullBackAndGo's do: whether that changes what the stop does on the entry bar.
	///   8  StopInGapTarget (#455) Scenario 5 with the same out-of-reach target, so the limit
	///                entry's stop goes out beside a target as OpeningRange's does.
	///   9  FourStopEntries (#455) Scenario 6 sent as four entries of one contract each, with a
	///                stop each at the same price, as DeadCatBounce's S1 to S4 and PullBackAndGo's
	///                L1 to L4 are. The first leg is named as every other scenario's entry is, and
	///                the others carry its name and their number.
	///
	/// A position still open HoldBars bars after its entry is exited at market, one exit for each
	/// leg still open, so a target NinjaTrader refuses or leaves resting cannot stall the run.
	/// Scenarios 3 to 6 and 9 set no target, and scenarios 3 to 9 send each entry only once the
	/// last has ended. Scenarios 3 and 4 exit at the entry bar's close; scenarios 5 to 9 at the
	/// close of the bar after, so a stop NinjaTrader leaves working has that bar to itself.
	///
	/// In scenarios 6, 7 and 9 the trial after one the probe exited at market waits a bar, because
	/// NinjaTrader left some stop entries sent on the bar that exit filled on working for good.
	/// Any entry still working past the bar it lives for is recorded as STUCK and cancelled, and
	/// the next trial starts.
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
		private const int ScenarioLimitEntry = 3;
		private const int ScenarioMarketable = 4;
		private const int ScenarioStopInGap = 5;
		private const int ScenarioStopEntry = 6;
		private const int ScenarioStopEntryTarget = 7;
		private const int ScenarioStopInGapTarget = 8;
		private const int ScenarioFourStopEntries = 9;

		/// <summary>Entries a trial sends in scenario 9.</summary>
		private const int FourLegs = 4;

		private const string EventHeader =
			"kind;trial;bar;bar_utc;bar_local;signal_name;from_entry_signal;order_id;order_action;" +
			"order_type;stop_price;limit_price;quantity;filled;average_fill_price;execution_price;order_state;" +
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
		private const string StopSet = "STOP_SET";
		private const string OrderUpdate = "ORDER_UPDATE";
		private const string Execution = "EXECUTION";
		private const string Stuck = "STUCK";

		private const string LongName = "probeLong";
		private const string ShortName = "probeShort";
		private const string ExitName = "probeExit";

		private StringBuilder eventRows;
		private StringBuilder barRows;
		private StringBuilder configRows;

		private int trial;
		private string entryName;

		/// <summary>The trial's entry orders until each ends, so the next trial is not sent over
		/// them. One, except in scenario 9.</summary>
		private readonly List<Order> entryOrders = new List<Order>();

		/// <summary>The trial's entries holding a position, by name, so the probe's exit names each
		/// one that is.</summary>
		private readonly HashSet<string> openLegs = new HashSet<string>();

		/// <summary>Bar the trial's entry was sent on.</summary>
		private int entrySentBar = -1;

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
				Description									= @"Records where a limit order the market has passed fills, and what a stop its entry bar opened past does -- nqbt issues #452, #244, #454 and #455";
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
				StopOffsetTicks = 1;
				// Beyond the whole window's range, so the entry bar never reaches it.
				OutOfReachPoints = 2000;
				HoldBars = 30;
				MaxTrials = 5000;
			}
			else if (State == State.Configure)
			{
				// AllEntries with four allowed, as both strategies run, and set every run, because an
				// instance reused between runs keeps the last one's.
				EntriesPerDirection = EntryLegs();
			}
			else if (State == State.DataLoaded)
			{
				displayZone = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;

				// Reset rather than rely on the field initialisers: the instance is reused
				// between runs at IsInstantiatedOnEachOptimizationIteration = false.
				trial = 0;
				entryName = null;
				entryOrders.Clear();
				openLegs.Clear();
				entrySentBar = -1;
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
			entryOrders.RemoveAll(order => IsTerminalOrderState(order.OrderState));

			// An entry lives for the bar after it is sent, but NinjaTrader has left one working
			// past it, and waiting on that would stall every trial after. The next trial waits a
			// bar for the cancel, because an opposite entry sent while this one works is ignored.
			if (entryOrders.Count > 0 && CurrentBar > entrySentBar + 1)
			{
				ReleaseStuckEntries();
				return;
			}

			if (Position.MarketPosition != MarketPosition.Flat)
			{
				if (positionOpenedBar < 0)
					positionOpenedBar = CurrentBar;

				// The session-close handler flattens at this bar's close, so an exit sent here would
				// be left working into the next session.
				if (CurrentBar - positionOpenedBar >= EffectiveHoldBars() && !Bars.IsLastBarOfSession)
				{
					ExitPosition(utc);
					return;
				}

				if (Scenario == ScenarioResting)
					SetTarget(Close[0] + entrySide * GapOffsetTicks * TickSize, utc);

				return;
			}
			positionOpenedBar = -1;
			// The session-close exit names no entry, so a leg it closed is still listed.
			openLegs.Clear();
			bool exitJustFilled = exitSubmitted;
			exitSubmitted = false;

			// A stop entry sent on the bar the probe's market exit filled on has been left working
			// for good, never filled and never cancelled.
			if (exitJustFilled && IsStopEntryScenario())
				return;

			// An entry order is live through the bar after it is sent, and one sent while it still
			// works would amend it rather than start a trial.
			if (entryOrders.Count > 0)
				return;

			if (trial >= MaxTrials)
				return;

			// An order sent here fills in the next session, so the trial would straddle the
			// boundary the session-close handler acts on.
			if (Bars.IsLastBarOfSession)
				return;

			SubmitTrial(utc);
		}

		private bool IsLimitEntryScenario()
		{
			return Scenario == ScenarioLimitEntry || Scenario == ScenarioMarketable || IsStopInGapScenario();
		}

		private bool IsStopInGapScenario()
		{
			return Scenario == ScenarioStopInGap || Scenario == ScenarioStopInGapTarget;
		}

		private bool IsStopEntryScenario()
		{
			return Scenario == ScenarioStopEntry || Scenario == ScenarioStopEntryTarget
				|| Scenario == ScenarioFourStopEntries;
		}

		private bool IsStopScenario()
		{
			return IsStopInGapScenario() || IsStopEntryScenario();
		}

		/// <summary>Bars a position is held before the probe exits it: HoldBars in scenarios 1 and 2,
		/// none in 3 and 4, and one in 5 to 9.</summary>
		private int EffectiveHoldBars()
		{
			if (IsStopScenario())
				return 1;

			return IsLimitEntryScenario() ? 0 : HoldBars;
		}

		private void SubmitTrial(DateTime utc)
		{
			trial++;
			entrySentBar = CurrentBar;
			bool isLong = trial % 2 == 1;
			entryName = isLong ? LongName : ShortName;
			entrySide = isLong ? 1 : -1;
			OrderAction action = isLong ? OrderAction.Buy : OrderAction.SellShort;

			if (IsStopEntryScenario())
			{
				double stop = Close[0] + entrySide * GapOffsetTicks * TickSize;
				double trigger = stop + entrySide * StopOffsetTicks * TickSize;
				SetStop(stop, utc);
				if (Scenario == ScenarioStopEntryTarget)
					SetTarget(trigger + entrySide * OutOfReachPoints, utc);

				for (int leg = 1; leg <= EntryLegs(); leg++)
				{
					string legName = LegName(leg);
					RecordSubmit(legName, string.Empty, action, OrderType.StopMarket, trigger, double.NaN, utc);
					Order placed = isLong
						? EnterLongStopMarket(1, trigger, legName)
						: EnterShortStopMarket(1, trigger, legName);
					Track(placed);
				}
				return;
			}

			if (IsLimitEntryScenario())
			{
				double limit = Scenario == ScenarioMarketable
					? Close[0] + entrySide * PassedOffsetTicks * TickSize
					: Close[0] - entrySide * GapOffsetTicks * TickSize;

				// Set before the entry and reset on every trial, as the target is below.
				if (IsStopInGapScenario())
					SetStop(limit - entrySide * StopOffsetTicks * TickSize, utc);
				if (Scenario == ScenarioStopInGapTarget)
					SetTarget(limit + entrySide * OutOfReachPoints, utc);

				RecordSubmit(entryName, string.Empty, action, OrderType.Limit, double.NaN, limit, utc);
				Track(isLong ? EnterLongLimit(1, limit, entryName) : EnterShortLimit(1, limit, entryName));
				return;
			}

			// The target has to be set before the entry it belongs to is sent, and reset on every
			// trial, or the previous trial's price is applied to the new position.
			double target = Scenario == ScenarioEntryBar
				? Close[0] - entrySide * PassedOffsetTicks * TickSize
				: Close[0] + entrySide * OutOfReachPoints;
			SetTarget(target, utc);

			RecordSubmit(entryName, string.Empty, action, OrderType.Market, double.NaN, double.NaN, utc);
			if (isLong)
				EnterLong(1, entryName);
			else
				EnterShort(1, entryName);
		}

		/// <summary>Terminal is enumerated rather than inferred from "not Filled" -- see
		/// NqbtOrderLifetimeProbe.</summary>
		private static bool IsTerminalOrderState(OrderState state)
		{
			return state == OrderState.Filled || state == OrderState.Cancelled || state == OrderState.Rejected;
		}

		/// <summary>Record each entry still working as stuck, cancel it unless that is already under
		/// way, and stop waiting on them.</summary>
		private void ReleaseStuckEntries()
		{
			// A cancel can call back into OnOrderUpdate before it returns, so the list is copied.
			foreach (Order order in entryOrders.ToArray())
			{
				RecordEvent(Stuck, order, double.NaN, Time[0], ErrorCode.NoError, string.Empty);
				if (!IsCancelling(order.OrderState))
					CancelOrder(order);
			}
			entryOrders.Clear();
		}

		/// <summary>Wait on an entry order until it ends, once however often it is seen.</summary>
		private void Track(Order order)
		{
			if (order != null && !entryOrders.Contains(order))
				entryOrders.Add(order);
		}

		private bool IsTrialEntryName(string name)
		{
			for (int leg = 1; leg <= EntryLegs(); leg++)
				if (name == LegName(leg))
					return true;
			return false;
		}

		private static bool IsCancelling(OrderState state)
		{
			return state == OrderState.CancelPending || state == OrderState.CancelSubmitted;
		}

		private void SetTarget(double price, DateTime utc)
		{
			SetProfitTarget(entryName, CalculationMode.Price, price);
			RecordTargetSet(price, utc);
		}

		private void SetStop(double price, DateTime utc)
		{
			for (int leg = 1; leg <= EntryLegs(); leg++)
				SetStopLoss(LegName(leg), CalculationMode.Price, price, false);
			RecordStopSet(price, utc);
		}

		private int EntryLegs()
		{
			return Scenario == ScenarioFourStopEntries ? FourLegs : 1;
		}

		/// <summary>The trial's entry name for its first leg, and that name and the leg's number for
		/// the others.</summary>
		private string LegName(int leg)
		{
			return leg == 1 ? entryName : entryName + leg.ToString(CultureInfo.InvariantCulture);
		}

		private void ExitPosition(DateTime utc)
		{
			if (exitSubmitted)
				return;

			bool isLong = Position.MarketPosition == MarketPosition.Long;
			// One exit for each leg still open. ExitLong(string) takes the entry to exit, not the
			// exit's own name, and an exit naming an entry with no position is ignored.
			for (int leg = 1; leg <= EntryLegs(); leg++)
			{
				string legName = LegName(leg);
				if (!openLegs.Contains(legName))
					continue;

				RecordSubmit(ExitName, legName, isLong ? OrderAction.Sell : OrderAction.BuyToCover,
					OrderType.Market, double.NaN, double.NaN, utc);
				if (isLong)
					ExitLong(ExitName, legName);
				else
					ExitShort(ExitName, legName);
			}

			exitSubmitted = true;
		}

		protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
			int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
		{
			if (eventRows == null || CurrentBar < 0)
				return;

			// The Enter methods return the order, but the callback can arrive before that
			// assignment lands, so the reference is claimed by name here as well.
			if (IsTrialEntryName(order.Name) && !IsTerminalOrderState(orderState) && !IsCancelling(orderState))
				Track(order);

			RecordEvent(OrderUpdate, order, double.NaN, time, error, comment);
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
			int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (eventRows == null || CurrentBar < 0 || execution.Order == null)
				return;

			Order order = execution.Order;
			if (IsTrialEntryName(order.Name))
				openLegs.Add(order.Name);
			else if (!string.IsNullOrEmpty(order.FromEntrySignal))
				openLegs.Remove(order.FromEntrySignal);

			RecordEvent(Execution, order, price, time, ErrorCode.NoError, executionId);
		}

		/// <summary>One row per order sent. A price is NaN on an order that has none: both on a market
		/// order, the stop price on a limit and the limit price on a stop. <paramref name="fromEntry"/>
		/// is the entry an exit names, and empty on an entry.</summary>
		private void RecordSubmit(string signalName, string fromEntry, OrderAction action, OrderType orderType,
			double stopPrice, double limitPrice, DateTime utc)
		{
			eventRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1};{2};{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};{5};{6};;{7};{8};{9};{10};1;;;;;{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};;;{11}\n",
				Submit,
				trial,
				CurrentBar,
				utc,
				Time[0],
				signalName,
				fromEntry,
				action,
				orderType,
				PriceField(stopPrice),
				PriceField(limitPrice),
				Bars.IsLastBarOfSession ? 1 : 0));
		}

		private static string PriceField(double price)
		{
			return double.IsNaN(price) ? string.Empty : price.ToString("R", CultureInfo.InvariantCulture);
		}

		private void RecordTargetSet(double price, DateTime utc)
		{
			eventRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1};{2};{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};;{5};;;Limit;;{6};1;;;;;{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};;;{7}\n",
				TargetSet,
				trial,
				CurrentBar,
				utc,
				Time[0],
				entryName,
				price,
				Bars.IsLastBarOfSession ? 1 : 0));
		}

		private void RecordStopSet(double price, DateTime utc)
		{
			eventRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1};{2};{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};;{5};;;StopMarket;{6};;1;;;;;{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};;;{7}\n",
				StopSet,
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
				"{0};{1};{2};{3:yyyyMMdd HHmmss};{4:yyyyMMdd HHmmss};{5};{6};{7};{8};{9};{10};{11};{12};{13};{14};{15};{16};{17:yyyyMMdd HHmmss};{18:yyyyMMdd HHmmss};{19};{20};{21}\n",
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
				order.StopPrice,
				order.LimitPrice,
				order.Quantity,
				order.Filled,
				order.AverageFillPrice,
				PriceField(executionPrice),
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

			int offsetTicks = Scenario == ScenarioEntryBar || Scenario == ScenarioMarketable
				? PassedOffsetTicks
				: GapOffsetTicks;
			string stopPart = IsStopScenario()
				? string.Format(CultureInfo.InvariantCulture, "_stop{0}", StopOffsetTicks)
				: string.Empty;
			string stem = string.Format(CultureInfo.InvariantCulture,
				"{0}_s{1}_off{2}{3}_hold{4}_{5:yyyyMMdd}_{6:yyyyMMdd}",
				Instrument.FullName.Replace(' ', '-'),
				Scenario,
				offsetTicks,
				stopPart,
				EffectiveHoldBars(),
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
		[Range(1, 9)]
		[Display(Name = "Scenario (1 entry bar, 2 resting, 3 limit entry, 4 marketable entry, 5 stop in the gap, 6 stop entry, 7 stop entry with a target, 8 stop in the gap with a target, 9 four stop entries)", Order = 1, GroupName = "Parameters")]
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
		[Range(1, int.MaxValue)]
		[Display(Name = "StopOffsetTicks", Order = 4, GroupName = "Parameters")]
		public int StopOffsetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "OutOfReachPoints", Order = 5, GroupName = "Parameters")]
		public double OutOfReachPoints { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "HoldBars", Order = 6, GroupName = "Parameters")]
		public int HoldBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MaxTrials", Order = 7, GroupName = "Parameters")]
		public int MaxTrials { get; set; }

		#endregion
	}
}
