#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

//This namespace holds Strategies in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Strategies
{
	/// <summary>
	/// Records, per 1-minute bar, exactly which higher-timeframe bar NinjaTrader considers
	/// current and what its moving averages read. Places no orders.
	///
	/// This exists because nqbt's `higher_timeframe.py` stamps a coarse average onto the
	/// 1-minute index and has to pick a rule at the boundary: when a 1-minute bar and a
	/// 60-minute bar close at the same instant, may the 1-minute bar read that 60-minute
	/// bar, or only the one before it? nqbt implements the first. A trade list cannot
	/// settle that, because for an EMA the two readings are algebraically indistinguishable
	/// -- the update moves the average toward the close and never past it, so
	/// `close - EMA_new = (1 - alpha)(close - EMA_prev)` keeps the sign and the gate never
	/// flips. Measured over 914,700 MNQ bars the label differed on one bar, and that one was
	/// a warm-up artefact.
	///
	/// So this probe observes the mechanism directly rather than through trades. The
	/// discriminator is `coarse_utc` against `utc`: on a bar that closes alongside a coarse
	/// bar, a `coarse_utc` equal to `utc` confirms nqbt's rule and one an interval earlier
	/// refutes it. That is decided on every coarse close in the run -- ~15,000 of them over
	/// one contract -- rather than on the zero trades a reconciliation would find.
	///
	/// It answers three further questions the projection rule does not touch, and each is
	/// currently an assumption in nqbt: whether NinjaTrader builds the coarse series where
	/// `resample.py` cuts it (session-anchored, no bucket spanning the maintenance break),
	/// whether `EMA(Closes[1], n)` seeds the way `indicators.nt8_ema` does on a *secondary*
	/// series, and how many bars pass before the secondary series is readable at all.
	///
	/// SMA columns are recorded even though nqbt fixes the kind at EMA. An SMA drops the
	/// oldest value out of its window and *can* move past the close, so the boundary rule is
	/// observable there -- 842 differing bars at 15-minute SMA(20) over the same data. That
	/// is the case #72 would make live, and NinjaTrader time is too scarce to come back for
	/// a column that costs nothing now.
	///
	/// Run it in Strategy Analyzer over one contract, 1-minute, then compare with
	/// `tools/reconcile_higher_timeframe.py`.
	/// </summary>
	public class NqbtHigherTimeframeProbe : Strategy
	{
		/// <summary>Where the two CSVs are written. The one thing to edit.</summary>
		private const string OutputFolder = @"C:\Users\matty\Documents\Trading Tools\verification\nt8_higher_timeframe";

		private const string PrimaryHeader =
			"bar;utc;local;is_first_bar_of_session;open;high;low;close;volume;" +
			"coarse_bar;coarse_utc;coarse_open;coarse_high;coarse_low;coarse_close;coarse_volume;" +
			"coarse_ema_short;coarse_ema_long;coarse_sma_short;coarse_sma_long";

		private const string CoarseHeader = "bar;utc;local;is_first_bar_of_session;open;high;low;close;volume";

		private EMA coarseEmaShort;
		private EMA coarseEmaLong;
		private SMA coarseSmaShort;
		private SMA coarseSmaLong;

		private StringBuilder primaryRows;
		private StringBuilder coarseRows;

		/// <summary>NinjaTrader's *display* zone, which is what bar times are in. Reading the
		/// trading-hours zone instead put every bar of an earlier export 5 hours out --
		/// see NqbtHistoricalExporter, which this conversion is lifted from.</summary>
		private TimeZoneInfo displayZone;

		private DateTime previousPrimaryUtc = DateTime.MinValue;
		private DateTime previousCoarseUtc = DateTime.MinValue;
		private DateTime firstUtc = DateTime.MinValue;
		private DateTime lastUtc = DateTime.MinValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Records which higher-timeframe bar NT8 considers current, per 1-minute bar";
				Name										= "NqbtHigherTimeframeProbe";
				Calculate									= Calculate.OnBarClose;
				EntriesPerDirection							= 1;
				EntryHandling								= EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy				= false;
				MaximumBarsLookBack							= MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution							= OrderFillResolution.Standard;
				TraceOrders									= false;
				// Zero so that OnBarUpdate is never withheld: the warm-up is part of what is
				// being measured, not something to skip past.
				BarsRequiredToTrade							= 0;
				IsInstantiatedOnEachOptimizationIteration	= false;

				CoarseMinutes = 60;
				ShortPeriod = 3;
				LongPeriod = 50;
			}
			else if (State == State.Configure)
			{
				AddDataSeries(BarsPeriodType.Minute, CoarseMinutes);
			}
			else if (State == State.DataLoaded)
			{
				// Instantiated here rather than in Configure: Closes[1] does not exist until
				// the added series has loaded.
				coarseEmaShort = EMA(Closes[1], ShortPeriod);
				coarseEmaLong = EMA(Closes[1], LongPeriod);
				coarseSmaShort = SMA(Closes[1], ShortPeriod);
				coarseSmaLong = SMA(Closes[1], LongPeriod);

				displayZone = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;

				// Reset rather than rely on the field initialisers: the instance is reused
				// between runs at IsInstantiatedOnEachOptimizationIteration = false, and a
				// carried-over previousUtc would misjudge the next run's ambiguous hour.
				previousPrimaryUtc = DateTime.MinValue;
				previousCoarseUtc = DateTime.MinValue;
				firstUtc = DateTime.MinValue;
				lastUtc = DateTime.MinValue;

				primaryRows = new StringBuilder(1 << 22);
				primaryRows.Append(PrimaryHeader).Append('\n');
				coarseRows = new StringBuilder(1 << 16);
				coarseRows.Append(CoarseHeader).Append('\n');
			}
			else if (State == State.Terminated)
			{
				Write();
			}
		}

		protected override void OnBarUpdate()
		{
			if (primaryRows == null)
				return;

			if (BarsInProgress == 1)
			{
				RecordCoarseBar();
				return;
			}

			if (BarsInProgress != 0 || CurrentBar < 0)
				return;

			RecordPrimaryBar();
		}

		/// <summary>The higher-timeframe series as NinjaTrader itself built it, so the
		/// bucketing can be diffed against resample.py rather than inferred from it.</summary>
		private void RecordCoarseBar()
		{
			DateTime utc = ToUtc(Times[1][0], ref previousCoarseUtc);
			coarseRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1:yyyyMMdd HHmmss};{2:yyyyMMdd HHmmss};{3};{4};{5};{6};{7};{8}\n",
				CurrentBars[1],
				utc,
				Times[1][0],
				BarsArray[1].IsFirstBarOfSession ? 1 : 0,
				Opens[1][0],
				Highs[1][0],
				Lows[1][0],
				Closes[1][0],
				Volumes[1][0]));
		}

		private void RecordPrimaryBar()
		{
			DateTime utc = ToUtc(Time[0], ref previousPrimaryUtc);
			if (firstUtc == DateTime.MinValue)
				firstUtc = utc;
			lastUtc = utc;

			primaryRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1:yyyyMMdd HHmmss};{2:yyyyMMdd HHmmss};{3};{4};{5};{6};{7};{8};",
				CurrentBar,
				utc,
				Time[0],
				Bars.IsFirstBarOfSession ? 1 : 0,
				Open[0],
				High[0],
				Low[0],
				Close[0],
				Volume[0]));

			// Before the added series has produced a bar there is nothing to read and every
			// accessor throws. The row is still written, with the coarse half empty: how long
			// that lasts is one of the things being measured.
			if (CurrentBars[1] < 0)
			{
				primaryRows.Append("-1;;;;;;;;;;\n");
				return;
			}

			primaryRows.Append(string.Format(CultureInfo.InvariantCulture,
				"{0};{1:yyyyMMdd HHmmss};{2};{3};{4};{5};{6};{7};{8};{9};{10}\n",
				CurrentBars[1],
				ToUtcUnordered(Times[1][0]),
				Opens[1][0],
				Highs[1][0],
				Lows[1][0],
				Closes[1][0],
				Volumes[1][0],
				coarseEmaShort[0],
				coarseEmaLong[0],
				coarseSmaShort[0],
				coarseSmaLong[0]));
		}

		/// <summary>Display-zone bar time to UTC, resolving both DST edges.
		///
		/// Lifted from NqbtHistoricalExporter, where each half was paid for: the hour a
		/// spring-forward skips does not exist locally and ConvertTimeToUtc throws on it
		/// rather than coping, and the autumn repeated hour resolves to standard time, which
		/// puts its first pass an hour late. Bar times increase strictly, so a step backwards
		/// identifies that second case -- but only trust it inside the ambiguous hour, so an
		/// unrelated ordering fault stays visible rather than being nudged away.</summary>
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
		/// The coarse stamp read from a primary bar repeats for every bar of its bucket, so
		/// it is not monotonic in this position and the ambiguous-hour rule above cannot
		/// apply. One coarse bar per autumn changeover is therefore an hour late here; join
		/// on the coarse series' own file, which is ordered, rather than trusting this
		/// column across that boundary.</summary>
		private DateTime ToUtcUnordered(DateTime barTime)
		{
			DateTime local = DateTime.SpecifyKind(barTime, DateTimeKind.Unspecified);
			if (displayZone.IsInvalidTime(local))
				local = local.AddHours(1);
			return TimeZoneInfo.ConvertTimeToUtc(local, displayZone);
		}

		private void Write()
		{
			if (primaryRows == null || firstUtc == DateTime.MinValue)
				return;

			Directory.CreateDirectory(OutputFolder);
			string stem = string.Format(CultureInfo.InvariantCulture,
				"{0}_{1}min_{2:yyyyMMdd}_{3:yyyyMMdd}",
				Instrument.FullName.Replace(' ', '-'),
				CoarseMinutes,
				firstUtc,
				lastUtc);

			File.WriteAllText(Path.Combine(OutputFolder, stem + "_primary.csv"),
				primaryRows.ToString(), new UTF8Encoding(false));
			File.WriteAllText(Path.Combine(OutputFolder, stem + "_coarse.csv"),
				coarseRows.ToString(), new UTF8Encoding(false));

			Print(string.Format(CultureInfo.InvariantCulture,
				"NqbtHigherTimeframeProbe: wrote {0}_primary.csv and {0}_coarse.csv to {1}", stem, OutputFolder));

			primaryRows = null;
			coarseRows = null;
		}

		#region Properties

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "CoarseMinutes", Order = 1, GroupName = "Parameters")]
		public int CoarseMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "ShortPeriod", Order = 2, GroupName = "Parameters")]
		public int ShortPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "LongPeriod", Order = 3, GroupName = "Parameters")]
		public int LongPeriod { get; set; }

		#endregion
	}
}
