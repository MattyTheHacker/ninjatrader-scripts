#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
	/// Dumps NinjaTrader's own indicator values to CSV so nqbt can pin its hand-rolled
	/// versions against them. Places no orders and is not a strategy in any real sense --
	/// it is a Strategy only because Strategy Analyzer gives an exact, reproducible date
	/// range where a chart does not.
	///
	/// Answers GitHub issues #20 (ATR), #21 (StdDev and Bollinger), #22 (Keltner) and the
	/// measurement half of #23 (True Range across a session boundary) from ONE run.
	///
	/// The reason this exists at all: TA-Lib's EMA already disagreed with NT8's through
	/// seeding alone, and ATR, StdDev, Bollinger and Keltner are all still unpinned. The
	/// milestone's own rule is do not answer from memory, so the values have to be read
	/// out of the platform before the recursions are written.
	///
	/// HOW TO RUN
	///   1. Copy to Documents\NinjaTrader 8\bin\Custom\Strategies\ (or via the NinjaScript
	///      Editor) and compile with F5.
	///   2. Strategy Analyzer -> select instrument -> 1 Minute -> set the date range.
	///   3. Select NqbtIndicatorProbe, leave every parameter at its default, Run.
	///   4. One CSV appears in OutputFolder. Hand it to nqbt.
	///
	/// WHY THE VALUES ARE WRITTEN AT G17
	/// Pinning means exact equality, and .NET's default double formatting is not
	/// round-trip exact. G17 round-trips a double without loss. "R" is the documented
	/// round-trip format but is known-broken for some values on .NET Framework, which is
	/// what NinjaTrader 8 runs on, so G17 it is.
	///
	/// WHY ATR(1) IS EXPORTED
	/// NinjaTrader exposes no True Range indicator, and #23 needs TR itself at a session
	/// boundary. Under the Wilder recursion ATR with period 1 reduces to TR exactly, so
	/// ATR(1) reads NT8's True Range straight out without assuming anything about how the
	/// average is seeded.
	///
	/// WHY THE FIRST BARS MATTER MOST
	/// Every open question here is about seeding, not about the steady state -- what
	/// Value[0] is, whether anything is emitted before the period fills, and what a partial
	/// window averages. So nothing is skipped and no BarsRequiredToTrade gate is applied:
	/// bar 0 is the most valuable row in the file.
	/// </summary>
	public class NqbtIndicatorProbe : Strategy
	{
		private ATR atr1;
		private ATR atr14;
		private ATR atr20;
		private SMA sma20;
		private EMA ema20;
		private StdDev stdDev20;
		private Bollinger bollinger;
		private KeltnerChannel keltner;
		private SMA typicalSma20;
		private EMA typicalEma20;

		private StringBuilder rows;
		private TimeZoneInfo zone;
		private DateTime previousUtc;

		// Captured while the data is alive. State.Terminated can arrive after Bars and
		// Instrument have been released, and reading them there would throw inside the one
		// callback that writes the file -- losing the whole run silently.
		private string instrumentName;
		private int periodMinutes;
		private DateTime firstBarTime;
		private DateTime lastBarTime;
		private int barsSeen;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = @"Exports NT8 indicator values for nqbt parity pinning. Places no orders.";
				Name = "NqbtIndicatorProbe";
				Calculate = Calculate.OnBarClose;
				IsExitOnSessionCloseStrategy = false;
				EntriesPerDirection = 1;

				// Deliberately 0, not 200. The seeding questions live in the first bars, and
				// a warm-up gate would discard exactly the rows worth having.
				BarsRequiredToTrade = 0;

				// The steady-state recursion is identical for every period, so two ordinary
				// ones are enough to confirm it generalises rather than fitting one case.
				AtrPeriodA = 14;
				AtrPeriodB = 20;
				MaPeriod = 20;
				BollingerStdDevs = 2.0;
				KeltnerOffset = 1.5;
				OutputFolder = @"C:\Users\matty\Documents\Trading Tools\verification\nt8_indicators";
			}
			else if (State == State.DataLoaded)
			{
				atr1 = ATR(1);
				atr14 = ATR(AtrPeriodA);
				atr20 = ATR(AtrPeriodB);
				sma20 = SMA(MaPeriod);
				ema20 = EMA(MaPeriod);
				stdDev20 = StdDev(MaPeriod);
				bollinger = Bollinger(BollingerStdDevs, MaPeriod);
				keltner = KeltnerChannel(KeltnerOffset, MaPeriod);

				// Keltner's midline is the question #22 exists to settle: platforms disagree
				// on SMA vs EMA and on close vs typical price. All four candidates are
				// exported so the answer is read off rather than argued about.
				typicalSma20 = SMA(Typical, MaPeriod);
				typicalEma20 = EMA(Typical, MaPeriod);

				rows = new StringBuilder(1 << 20);
				rows.Append("bar;utc;local;is_first_bar_of_session;open;high;low;close;volume;")
					.Append("atr1;atr_a;atr_b;sma;ema;stddev;")
					.Append("bb_upper;bb_middle;bb_lower;")
					.Append("kc_upper;kc_midline;kc_lower;")
					.Append("typical_sma;typical_ema\n");

				// Bar times come back in NinjaTrader's DISPLAY timezone, not the Bars'
				// trading-hours zone. Reading the trading-hours zone instead put every bar
				// 5 hours out in NqbtHistoricalExporter, plausibly enough that it survived
				// until whole files were diffed. Same zone, same two DST guards below.
				zone = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
				previousUtc = DateTime.MinValue;

				instrumentName = Instrument.FullName.Replace(' ', '-');
				periodMinutes = BarsPeriod.Value;
				barsSeen = 0;
			}
			else if (State == State.Terminated)
			{
				Write();
			}
		}

		protected override void OnBarUpdate()
		{
			if (barsSeen == 0)
				firstBarTime = Time[0];
			lastBarTime = Time[0];
			barsSeen++;

			rows.Append(CurrentBar.ToString(CultureInfo.InvariantCulture)).Append(';')
				.Append(Utc(Time[0]).ToString("yyyyMMdd HHmmss", CultureInfo.InvariantCulture)).Append(';')
				.Append(Time[0].ToString("yyyyMMdd HHmmss", CultureInfo.InvariantCulture)).Append(';')
				.Append(Bars.IsFirstBarOfSession ? '1' : '0').Append(';');

			Number(Open[0]); Number(High[0]); Number(Low[0]); Number(Close[0]); Number(Volume[0]);
			Number(atr1[0]); Number(atr14[0]); Number(atr20[0]);
			Number(sma20[0]); Number(ema20[0]); Number(stdDev20[0]);
			Number(bollinger.Upper[0]); Number(bollinger.Middle[0]); Number(bollinger.Lower[0]);
			Number(keltner.Upper[0]); Number(keltner.Midline[0]); Number(keltner.Lower[0]);
			Number(typicalSma20[0]); Number(typicalEma20[0]);

			rows.Length -= 1;
			rows.Append('\n');
		}

		/// <summary>G17 so a pinned value round-trips exactly. See the class remarks.</summary>
		private void Number(double value)
		{
			rows.Append(value.ToString("G17", CultureInfo.InvariantCulture)).Append(';');
		}

		/// <summary>
		/// Display-zone bar time to UTC, with the two DST traps NqbtHistoricalExporter
		/// already paid for. Duplicated rather than shared because NinjaScript files are
		/// compiled independently by NinjaTrader and cannot import from one another; if a
		/// third consumer appears, promote it to an AddOn helper.
		/// </summary>
		private DateTime Utc(DateTime barTime)
		{
			DateTime local = DateTime.SpecifyKind(barTime, DateTimeKind.Unspecified);

			// The hour skipped by a spring-forward does not exist locally, but bars still
			// land in it, and ConvertTimeToUtc throws rather than coping.
			if (zone.IsInvalidTime(local))
				local = local.AddHours(1);

			DateTime utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);

			// The autumn repeated hour resolves to standard time, putting its first pass an
			// hour late. Bar times strictly increase, so a step backwards identifies it --
			// but only trust that inside the ambiguous hour.
			if (zone.IsAmbiguousTime(local) && previousUtc != DateTime.MinValue && utc <= previousUtc)
				utc = utc.AddHours(1);
			previousUtc = utc;
			return utc;
		}

		private void Write()
		{
			// Terminated also fires when NinjaTrader merely enumerates the strategy without
			// running it, so no bars is the normal no-op rather than a failure.
			if (rows == null || barsSeen == 0)
				return;

			Directory.CreateDirectory(OutputFolder);

			string name = string.Format(CultureInfo.InvariantCulture, "{0}_{1}min_{2:yyyyMMdd}_{3:yyyyMMdd}.csv",
				instrumentName, periodMinutes, firstBarTime, lastBarTime);

			// Write-then-move, so a file caught half-written is never read as complete.
			string finalPath = Path.Combine(OutputFolder, name);
			string tempPath = finalPath + ".tmp";
			File.WriteAllText(tempPath, rows.ToString(), new UTF8Encoding(false));
			if (File.Exists(finalPath))
				File.Delete(finalPath);
			File.Move(tempPath, finalPath);

			Print(string.Format(CultureInfo.InvariantCulture,
				"NqbtIndicatorProbe: wrote {0} bars to {1}", barsSeen, finalPath));
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "ATR period A", Order = 1, GroupName = "Parameters")]
		public int AtrPeriodA { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "ATR period B", Order = 2, GroupName = "Parameters")]
		public int AtrPeriodB { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MA / StdDev / band period", Order = 3, GroupName = "Parameters")]
		public int MaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Bollinger std devs", Order = 4, GroupName = "Parameters")]
		public double BollingerStdDevs { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Keltner offset", Order = 5, GroupName = "Parameters")]
		public double KeltnerOffset { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Output folder", Order = 6, GroupName = "Parameters")]
		public string OutputFolder { get; set; }
		#endregion
	}
}
