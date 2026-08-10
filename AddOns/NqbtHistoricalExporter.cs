#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

//This namespace holds Add ons in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.AddOns
{
	/// <summary>
	/// Exports 1-minute bars per contract in the same format nqbt ingests:
	/// yyyyMMdd HHmmss;open;high;low;close;volume  -- end-of-bar, UTC, semicolon delimited.
	///
	/// Why this exists: Tools -> Historical Data dumps whatever NinjaTrader happens to hold
	/// locally at that moment, and that is not stable. Re-exporting long-expired contracts
	/// produced materially different files -- three gained exactly their final five trading
	/// days through expiry, while MNQ 03-26 lost most of the 2026-01-19 session. NQ 03-26,
	/// exported hours earlier, still had that session at its full 1,141 bars, so the data
	/// exists; the export simply did not contain it.
	///
	/// BarsRequest asks the provider for an explicit range instead, and measurably does
	/// better: +634k bars overall, the 2026-01-19 session restored, and 2026-04-06 recovered
	/// on both MNQ and NQ 06-26 where the manual export had dropped it entirely. Where the
	/// two overlap they agree, bar volume differing by one or two contracts on 0.4% of bars
	/// -- live tick aggregation against the settled archive.
	///
	/// The one thing it did worse was the tail: a single 200-day request returned ~171 days
	/// ending ~2.5 weeks before expiry, giving up the contract's most liquid sessions. The
	/// window looked anchored to the start of the request rather than the end, so requests
	/// are now issued in overlapping chunks and merged by timestamp.
	///
	/// Verify with tools/compare_exports.py before ingesting. It refuses the folder outright
	/// on a whole-hour timezone shift, which is how the first version's 5-hour error was
	/// caught -- prices stayed plausible and every file parsed cleanly.
	/// </summary>
	public class NqbtHistoricalExporter : NinjaTrader.NinjaScript.AddOnBase
	{
		// ---- configuration: edit these three, everything else is derived -----------------

		/// <summary>Existing exports. Contract names are read from here so the comparison
		/// covers exactly the same set. Nothing in this folder is written to.</summary>
		private const string SourceFolder = @"C:\Users\matty\Documents\Trading Tools\data\minute";

		/// <summary>Where this AddOn writes. Deliberately NOT the folder above: the point is
		/// to diff the two, and overwriting the known-good copy would destroy the comparison
		/// before it could be made.</summary>
		private const string OutputFolder = @"C:\Users\matty\Documents\Trading Tools\data\addon";

		/// <summary>Days before expiry to ask for. Deliberately more than NinjaTrader has
		/// ever returned (~95), so the provider decides the limit rather than this script.</summary>
		private const int LookbackDays = 200;

		// ---------------------------------------------------------------------------------

		/// <summary>Span of one request. A single 200-day request came back as roughly 171
		/// days that stopped ~2.5 weeks short of expiry, losing the contract's most liquid
		/// sessions. The returned window looked anchored to the *start* of the request rather
		/// than the end, so walking a shorter window forward should reach the tail.</summary>
		private const int ChunkDays = 90;

		/// <summary>Overlap between consecutive chunks, so a bar on a boundary cannot fall
		/// between two requests. Duplicates are free -- chunks merge by timestamp.</summary>
		private const int ChunkOverlapDays = 10;

		private const int RequestTimeoutSeconds = 180;

		private NTMenuItem exportMenuItem;
		private NTMenuItem toolsMenuItem;
		private int running;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description	= @"Exports per-contract 1-minute bars in nqbt's ingest format via BarsRequest.";
				Name		= "NqbtHistoricalExporter";
			}
		}

		protected override void OnWindowCreated(Window window)
		{
			ControlCenter controlCenter = window as ControlCenter;
			if (controlCenter == null)
				return;

			toolsMenuItem = controlCenter.FindFirst("ControlCenterMenuItemTools") as NTMenuItem;
			if (toolsMenuItem == null)
				return;

			exportMenuItem = new NTMenuItem
			{
				Header	= "Export historical bars (nqbt)",
				Style	= Application.Current.TryFindResource("MainMenuItem") as Style
			};
			exportMenuItem.Click += OnExportClick;
			toolsMenuItem.Items.Add(exportMenuItem);
		}

		protected override void OnWindowDestroyed(Window window)
		{
			if (exportMenuItem == null || !(window is ControlCenter))
				return;

			exportMenuItem.Click -= OnExportClick;
			if (toolsMenuItem != null && toolsMenuItem.Items.Contains(exportMenuItem))
				toolsMenuItem.Items.Remove(exportMenuItem);

			exportMenuItem = null;
			toolsMenuItem = null;
		}

		private void OnExportClick(object sender, RoutedEventArgs e)
		{
			// Interlocked rather than a bool: the menu is clickable again the moment the
			// handler returns, and a second pass would race the first on the same files.
			if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
			{
				Log("Export already running.", LogLevel.Warning);
				return;
			}

			Task.Run(() =>
			{
				try { ExportAll(); }
				catch (Exception ex) { Log("Export failed: " + ex, LogLevel.Error); }
				finally { Interlocked.Exchange(ref running, 0); }
			});
		}

		private void ExportAll()
		{
			if (!Directory.Exists(SourceFolder))
			{
				Log("Source folder not found: " + SourceFolder, LogLevel.Error);
				return;
			}
			Directory.CreateDirectory(OutputFolder);

			List<string> names = Directory
				.GetFiles(SourceFolder, "*.Last.txt")
				.Select(p => Path.GetFileName(p).Replace(".Last.txt", string.Empty))
				.OrderBy(n => n)
				.ToList();

			if (names.Count == 0)
			{
				Log("No .Last.txt files in " + SourceFolder + "; nothing to mirror.", LogLevel.Warning);
				return;
			}

			Log(string.Format("nqbt export: {0} contracts -> {1}", names.Count, OutputFolder), LogLevel.Information);
			StringBuilder summary = new StringBuilder();
			summary.AppendLine("contract,bars,first_utc,last_utc,seconds,status");

			foreach (string name in names)
			{
				DateTime started = DateTime.UtcNow;
				Outcome outcome = ExportContract(name);
				double elapsed = (DateTime.UtcNow - started).TotalSeconds;

				Log(string.Format("  {0,-12} {1,8:N0} bars  {2,6:N1}s  {3}",
						name, outcome.Bars, elapsed, outcome.Status),
					outcome.Bars > 0 ? LogLevel.Information : LogLevel.Warning);
				summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
					"{0},{1},{2},{3},{4:F1},\"{5}\"",
					name, outcome.Bars, outcome.FirstUtc, outcome.LastUtc, elapsed, Csv(outcome.Status)));
			}

			string summaryPath = Path.Combine(OutputFolder, "_export_summary.csv");
			File.WriteAllText(summaryPath, summary.ToString(), new UTF8Encoding(false));
			Log("nqbt export complete. Summary: " + summaryPath, LogLevel.Information);
		}

		/// <summary>.NET exception messages carry embedded newlines, which broke the summary
		/// into unreadable half-rows the first time one fired.</summary>
		private static string Csv(string value)
		{
			return (value ?? string.Empty)
				.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
		}

		private class Outcome
		{
			public int Bars;
			public string FirstUtc = string.Empty;
			public string LastUtc = string.Empty;
			public string Status = string.Empty;
		}

		private Outcome ExportContract(string name)
		{
			Outcome outcome = new Outcome();

			Instrument instrument = Instrument.GetInstrument(name);
			if (instrument == null)
			{
				outcome.Status = "instrument not found";
				return outcome;
			}

			// Ask past expiry and let the provider clip it. Expiry is the natural end for a
			// dead contract; a live one has no data past today. A contract with no usable
			// expiry falls back to today rather than requesting from year 1.
			DateTime today = DateTime.Now.Date.AddDays(1);
			DateTime to = instrument.Expiry > DateTime.MinValue
				? instrument.Expiry.Date.AddDays(1)
				: today;
			if (to > today)
				to = today;
			DateTime from = to.AddDays(-LookbackDays);

			// Keyed by UTC bar time so overlapping chunks merge rather than duplicate, and so
			// the file comes out sorted regardless of the order chunks return in.
			SortedDictionary<DateTime, string> rows = new SortedDictionary<DateTime, string>();
			List<string> failures = new List<string>();
			int chunks = 0;

			int step = Math.Max(1, ChunkDays - ChunkOverlapDays);
			for (DateTime chunkStart = from; chunkStart < to; chunkStart = chunkStart.AddDays(step))
			{
				DateTime chunkEnd = chunkStart.AddDays(ChunkDays);
				if (chunkEnd > to)
					chunkEnd = to;

				chunks++;
				string failure = RequestChunk(instrument, chunkStart, chunkEnd, rows);
				if (failure != null)
					failures.Add(failure);

				if (chunkEnd >= to)
					break;
			}

			if (rows.Count == 0)
			{
				outcome.Status = failures.Count > 0
					? string.Join(" | ", failures)
					: "no bars returned";
				return outcome;
			}

			Write(name, rows, outcome);
			// Report partial success loudly: a contract short one chunk still writes a file,
			// and silently shipping a file with a hole is how the manual export misled us.
			outcome.Status = failures.Count == 0
				? string.Format(CultureInfo.InvariantCulture, "ok ({0} chunks)", chunks)
				: string.Format(CultureInfo.InvariantCulture, "PARTIAL {0} of {1} chunks failed: {2}",
					failures.Count, chunks, failures[0]);
			return outcome;
		}

		/// <summary>One request for one window, merged into <paramref name="rows"/>.
		/// Returns null on success, or a description of what went wrong.</summary>
		private string RequestChunk(
			Instrument instrument, DateTime from, DateTime to, SortedDictionary<DateTime, string> rows)
		{
			BarsRequest request = new BarsRequest(instrument, from, to)
			{
				BarsPeriod	= new BarsPeriod { BarsPeriodType = BarsPeriodType.Minute, Value = 1 },
				// 24x7 rather than the CME ETH template: session filtering is nqbt's job, and
				// a template here would silently drop the out-of-session stray prints that
				// ingest currently tags in_session=False. Those differing would look like a
				// data discrepancy when it was only a settings difference.
				TradingHours	= TradingHours.Get("Default 24 x 7"),
				MergePolicy	= MergePolicy.DoNotMerge
			};

			string failure = null;
			using (ManualResetEventSlim done = new ManualResetEventSlim(false))
			{
				request.Request((req, errorCode, errorMessage) =>
				{
					try
					{
						if (errorCode != ErrorCode.NoError)
							failure = string.Format(CultureInfo.InvariantCulture,
								"{0:yyyy-MM-dd}: {1} {2}", from, errorCode, errorMessage);
						else if (req.Bars != null && req.Bars.Count > 0)
							Accumulate(req.Bars, rows);
						// An empty window is normal -- a contract simply may not have traded
						// that early -- so it is not recorded as a failure.
					}
					catch (Exception ex)
					{
						failure = string.Format(CultureInfo.InvariantCulture,
							"{0:yyyy-MM-dd}: {1}", from, ex.Message);
					}
					finally
					{
						done.Set();
					}
				});

				// A request that never calls back would otherwise hang the whole run.
				if (!done.Wait(TimeSpan.FromSeconds(RequestTimeoutSeconds)))
					failure = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd}: timed out", from);
			}

			request.Dispose();
			return failure;
		}

		private void Accumulate(Bars bars, SortedDictionary<DateTime, string> rows)
		{
			// Bar times come back in NinjaTrader's *display* timezone (Tools -> Options ->
			// General -> Time zone), NOT the Bars' trading-hours zone. Converting from the
			// trading-hours zone put every bar 5 hours ahead: a UK display zone read as US
			// Eastern. Both regions shift for DST together, so the error was a constant 5
			// rather than varying with the season -- which is precisely why it looked
			// plausible and had to be caught by diffing whole files.
			TimeZoneInfo zone = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;

			// Only valid within a chunk, where bars arrive in order. That is why the
			// conversion lives here rather than at write time, once chunks have interleaved.
			DateTime previousUtc = DateTime.MinValue;

			for (int i = 0; i < bars.Count; i++)
			{
				DateTime local = DateTime.SpecifyKind(bars.GetTime(i), DateTimeKind.Unspecified);

				// The hour skipped by a spring-forward does not exist locally, but bars
				// still land in it, and ConvertTimeToUtc throws rather than coping. That
				// threw away two whole contracts on the first run.
				if (zone.IsInvalidTime(local))
					local = local.AddHours(1);

				DateTime utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);

				// The autumn repeated hour is ambiguous and resolves to standard time, which
				// puts its first pass an hour late. Bar times are strictly increasing, so a
				// step backwards identifies it -- but only trust that inside the ambiguous
				// hour, so an unrelated ordering problem stays visible rather than nudged.
				if (zone.IsAmbiguousTime(local) && previousUtc != DateTime.MinValue && utc <= previousUtc)
					utc = utc.AddHours(1);
				previousUtc = utc;

				rows[utc] = string.Concat(
					bars.GetOpen(i).ToString(CultureInfo.InvariantCulture), ";",
					bars.GetHigh(i).ToString(CultureInfo.InvariantCulture), ";",
					bars.GetLow(i).ToString(CultureInfo.InvariantCulture), ";",
					bars.GetClose(i).ToString(CultureInfo.InvariantCulture), ";",
					bars.GetVolume(i).ToString(CultureInfo.InvariantCulture));
			}
		}

		private void Write(string name, SortedDictionary<DateTime, string> rows, Outcome outcome)
		{
			StringBuilder sb = new StringBuilder(rows.Count * 48);
			foreach (KeyValuePair<DateTime, string> row in rows)
				sb.Append(row.Key.ToString("yyyyMMdd HHmmss", CultureInfo.InvariantCulture))
				  .Append(';').Append(row.Value).Append('\n');

			// Write-then-move: ingest hashes the whole consumed byte range, so a file caught
			// half-written would be detected as a rewrite and reparsed -- correct, but it
			// would parse a truncated file as though it were complete.
			string finalPath = Path.Combine(OutputFolder, name + ".Last.txt");
			string tempPath = finalPath + ".tmp";
			File.WriteAllText(tempPath, sb.ToString(), new UTF8Encoding(false));
			if (File.Exists(finalPath))
				File.Delete(finalPath);
			File.Move(tempPath, finalPath);

			outcome.Bars = rows.Count;
			outcome.FirstUtc = rows.Keys.First().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
			outcome.LastUtc = rows.Keys.Last().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
		}
	}
}
