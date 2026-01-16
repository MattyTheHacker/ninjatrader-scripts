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

#endregion

//This namespace holds Drawing tools in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.DrawingTools
{
	public class RiskRewardExtras : DrawingTool
	{

		private const int cursorSensitivity = 15;
		private ChartAnchor editingAnchor;
		private double entryPrice;
		private bool needsRatioUpdate = true;
		private double ratio = 2;
		private double risk;
		private double cachedOrderQuantity;
		private double reward;
		private double stopPrice;
		private double targetPrice;
		private double textLeftPoint;
		private double textRightPoint;

		[Display(Order = 1)]
		public ChartAnchor EntryAnchor { get; set; }

		[Display(Order = 2)]
		public ChartAnchor RiskAnchor { get; set; }

		[Display(Order = 3)]
		public ChartAnchor RewardAnchor { get; set; }

		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptDrawingToolRiskRewardExtrasDrawTarget", GroupName = "NinjaScriptGeneral", Order = 4)]
		public bool ShowRiskText { get; set; } = true;

		[Browsable(false)]
		private bool DrawTarget => RiskAnchor is { IsEditing: false} || RewardAnchor is { IsEditing: false};

		public override object Icon => Icons.DrawRiskReward;

		[Range(0, double.MaxValue)]
		[NinjaScriptProperty]
		[Display(ResourceType = typeof(Custom.Resource), Name="RiskRewardExtras", GroupName = "NinjaScriptGeneral", Order = 1)]
		public double Ratio
		{
			get => ratio;
			set
			{
				if (ratio.ApproxCompare(value) == 0)
					return;
				ratio = value;
				needsRatioUpdate = true;
			}
		}

		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptDrawingToolAnchor", GroupName = "NinjaScriptLines", Order = 3)]
		public Stroke AnchorLineStroke { get; set; }

		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptDrawingToolRiskRewardExtrasLineStrokeRisk", GroupName = "NinjaScriptLines", Order = 4)]
		public Stroke StopLineStroke { get; set; }

		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptDrawingToolRiskRewardExtrasLineStrokeRisk", GroupName = "NinjaScriptLines", Order = 5)]
		public Stroke TargetLineStroke { get; set; }

		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptDrawingToolRiskRewardExtrasLineStrokeEntry", GroupName = "NinjaScriptLines", Order = 6)]
		public Stroke EntryLineStroke { get; set; }

		public override IEnumerable<ChartAnchor> Anchors { get { return [EntryAnchor, RiskAnchor, RewardAnchor]; } }

		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptDrawingToolRiskRewardExtrasExtendLinesLeft", GroupName = "NinjaScriptLines", Order = 1)]
		public bool IsExtendedLinesLeft { get; set; }

		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptDrawingToolRiskRewardExtrasExtendLinesRight", GroupName = "NinjaScriptLines", Order = 2)]
		public bool IsExtendedLinesRight { get; set; }

		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptDrawingToolTextAlignment", GroupName="NinjaScriptGeneral", Order = 2)]
		public TextLocation TextAlignment { get; set; }

		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptDrawingToolRulerYValueDisplayUnit", GroupName = "NinjaScriptGeneral", Order = 3)]
		public ValueUnit DisplayUnit { get; set; }

        public override bool SupportsAlerts => true;

		private void DrawRiskText(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale)
		{
			if (!ShowRiskText) return;

			ChartBars chartBars = GetAttachedToChartBars();

			if (chartBars == null) return;

			Point entryPoint = EntryAnchor.GetPoint(chartControl, chartPanel, chartScale);
			Point stopPoint = RiskAnchor.GetPoint(chartControl, chartPanel, chartScale);

			Point midPoint = new((entryPoint.X + stopPoint.X) / 2, (entryPoint.Y + stopPoint.Y) / 2);

			double tickSize = AttachedTo.Instrument.MasterInstrument.TickSize;
			double pointValue = AttachedTo.Instrument.MasterInstrument.PointValue;
			double absRisk = Math.Abs(risk);

			chartControl.Dispatcher.BeginInvoke(new Action(() =>
			{
                if (Window.GetWindow(chartControl)?
                    .FindFirst("ChartTraderControlQuantitySelector") is QuantityUpDown q)
                    cachedOrderQuantity = q.Value;
            }));

			string riskText = $"Risk: {Core.Globals.FormatCurrency(absRisk / tickSize * (tickSize * pointValue) * cachedOrderQuantity)}";

			SimpleFont						wpfFont		= chartControl.Properties.LabelFont ?? new SimpleFont();
			SharpDX.DirectWrite.TextFormat	textFormat	= wpfFont.ToDirectWriteTextFormat();
			textFormat.TextAlignment					= SharpDX.DirectWrite.TextAlignment.Leading;
			textFormat.WordWrapping						= SharpDX.DirectWrite.WordWrapping.NoWrap;
			SharpDX.DirectWrite.TextLayout textLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, riskText, textFormat, chartPanel.W, textFormat.FontSize);

			RenderTarget.DrawTextLayout(new SharpDX.Vector2((float)midPoint.X, (float)midPoint.Y), textLayout, StopLineStroke.BrushDX, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
		}

		private string GetPriceString(double price, ChartBars chartBars)
		{
			string priceString;
			double yValueEntry	= AttachedTo.Instrument.MasterInstrument.RoundToTickSize(EntryAnchor.Price);
			double tickSize		= AttachedTo.Instrument.MasterInstrument.TickSize;
			double pointValue	= AttachedTo.Instrument.MasterInstrument.PointValue;

			switch (DisplayUnit)
			{
				case ValueUnit.Currency:
					if (AttachedTo.Instrument.MasterInstrument.InstrumentType == InstrumentType.Forex)
					{
						priceString = price > yValueEntry ?
							Core.Globals.FormatCurrency(AttachedTo.Instrument.MasterInstrument.RoundToTickSize(price - yValueEntry) / tickSize * (tickSize * pointValue * Account.All[0].ForexLotSize)) :
							Core.Globals.FormatCurrency(AttachedTo.Instrument.MasterInstrument.RoundToTickSize(yValueEntry - price) / tickSize * (tickSize * pointValue * Account.All[0].ForexLotSize));
					} else {
						priceString = price > yValueEntry ?
							Core.Globals.FormatCurrency(AttachedTo.Instrument.MasterInstrument.RoundToTickSize(price - yValueEntry) / tickSize * (tickSize * pointValue)) :
							Core.Globals.FormatCurrency(AttachedTo.Instrument.MasterInstrument.RoundToTickSize(yValueEntry - price) / tickSize * (tickSize * pointValue));
					}
					break;
				case ValueUnit.Percent:
					priceString = price > yValueEntry ?
						(AttachedTo.Instrument.MasterInstrument.RoundToTickSize(price - yValueEntry) / yValueEntry).ToString("P", Core.Globals.GeneralOptions.CurrentCulture) :
						(AttachedTo.Instrument.MasterInstrument.RoundToTickSize(yValueEntry - price) / yValueEntry).ToString("P", Core.Globals.GeneralOptions.CurrentCulture);
					break;
				case ValueUnit.Ticks:
					priceString = price > yValueEntry ?
						(AttachedTo.Instrument.MasterInstrument.RoundToTickSize(price - yValueEntry) / tickSize).ToString("F0") :
						(AttachedTo.Instrument.MasterInstrument.RoundToTickSize(yValueEntry - price) / tickSize).ToString("F0");
					break;
				case ValueUnit.Pips:
					priceString = price > yValueEntry ?
						(AttachedTo.Instrument.MasterInstrument.RoundToTickSize(price - yValueEntry) / tickSize / 10).ToString("F0") :
						(AttachedTo.Instrument.MasterInstrument.RoundToTickSize(yValueEntry - price) / tickSize / 10).ToString("F0");
					break;
				default:
					priceString = chartBars.Bars.Instrument.MasterInstrument.FormatPrice(price);
					break;
			}
			return priceString;
		}

		private void DrawPriceText(ChartAnchor anchor, Point point, double price, ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale)
		{
			if (TextAlignment == TextLocation.Off) return;

			ChartBars chartBars = GetAttachedToChartBars();

			if (chartBars == null) return;

			if (!IsUserDrawn)
				price = AttachedTo.Instrument.MasterInstrument.RoundToTickSize(anchor.Price);

			string priceString = GetPriceString(price, chartBars);

			Stroke colour;
			textLeftPoint = RiskAnchor.GetPoint(chartControl, chartPanel, chartScale).X;
			textRightPoint = EntryAnchor.GetPoint(chartControl, chartPanel, chartScale).X;

			if (anchor == RewardAnchor) colour = TargetLineStroke;
			else if (anchor == RiskAnchor) colour = StopLineStroke;
			else if (anchor == EntryAnchor) colour = EntryLineStroke;
			else colour = AnchorLineStroke;

			SimpleFont						wpfFont		= chartControl.Properties.LabelFont ?? new SimpleFont();
			SharpDX.DirectWrite.TextFormat	textFormat	= wpfFont.ToDirectWriteTextFormat();
			textFormat.TextAlignment					= SharpDX.DirectWrite.TextAlignment.Leading;
			textFormat.WordWrapping						= SharpDX.DirectWrite.WordWrapping.NoWrap;
			SharpDX.DirectWrite.TextLayout textLayout   = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, priceString, textFormat, chartPanel.H, textFormat.FontSize);

			if (RiskAnchor.Time <= EntryAnchor.Time)
			{
				if(!IsExtendedLinesLeft && !IsExtendedLinesRight)
					point.X = TextAlignment switch
					{
						TextLocation.InsideLeft		=> textLeftPoint,
						TextLocation.InsideRight	=> textRightPoint - textLayout.Metrics.Width,
						TextLocation.ExtremeLeft	=> textLeftPoint,
						TextLocation.ExtremeRight	=> textRightPoint - textLayout.Metrics.Width,
						_							=> point.X
					};
				else if (IsExtendedLinesLeft && !IsExtendedLinesRight)
					point.X = TextAlignment switch
					{
						TextLocation.InsideLeft		=> textLeftPoint,
						TextLocation.InsideRight	=> textRightPoint - textLayout.Metrics.Width,
						TextLocation.ExtremeLeft	=> chartPanel.X,
						TextLocation.ExtremeRight	=> textRightPoint - textLayout.Metrics.Width,
						_							=> point.X
					};
				else if (!IsExtendedLinesLeft && IsExtendedLinesRight)
					point.X = TextAlignment switch
					{
						TextLocation.InsideLeft		=> textLeftPoint,
						TextLocation.InsideRight	=> textRightPoint - textLayout.Metrics.Width,
						TextLocation.ExtremeLeft	=> textLeftPoint,
						TextLocation.ExtremeRight	=> chartPanel.W - textLayout.Metrics.Width,
						_							=> point.X
					};
				else if (IsExtendedLinesLeft && IsExtendedLinesRight)
					point.X = TextAlignment switch
					{
						TextLocation.InsideLeft		=> textLeftPoint,
						TextLocation.InsideRight	=> textRightPoint - textLayout.Metrics.Width,
						TextLocation.ExtremeRight	=> chartPanel.W - textLayout.Metrics.Width,
						TextLocation.ExtremeLeft	=> chartPanel.X,
						_							=> point.X
					};
			}
			else if (RiskAnchor.Time >= EntryAnchor.Time)
				if (!IsExtendedLinesLeft && !IsExtendedLinesRight)
				{
					point.X = TextAlignment switch
					{
						TextLocation.InsideLeft		=> textRightPoint,
						TextLocation.InsideRight	=> textLeftPoint - textLayout.Metrics.Width,
						TextLocation.ExtremeLeft	=> textRightPoint,
						TextLocation.ExtremeRight	=> textLeftPoint - textLayout.Metrics.Width,
						_							=> point.X
					};
				}
				else if (IsExtendedLinesLeft && !IsExtendedLinesRight)
					point.X = TextAlignment switch
					{
						TextLocation.InsideLeft		=> textRightPoint,
						TextLocation.InsideRight	=> textLeftPoint - textLayout.Metrics.Width,
						TextLocation.ExtremeLeft	=> chartPanel.X,
						TextLocation.ExtremeRight	=> textLeftPoint - textLayout.Metrics.Width,
						_							=> point.X
					};
				else if (!IsExtendedLinesLeft && IsExtendedLinesRight)
					point.X = TextAlignment switch
					{
						TextLocation.InsideLeft		=> textRightPoint,
						TextLocation.InsideRight	=> textLeftPoint - textLayout.Metrics.Width,
						TextLocation.ExtremeLeft	=> textRightPoint,
						TextLocation.ExtremeRight	=> chartPanel.W - textLayout.Metrics.Width,
						_							=> point.X
					};
				else if (IsExtendedLinesLeft && IsExtendedLinesRight)
					point.X = TextAlignment switch
					{
						TextLocation.InsideLeft		=> textRightPoint,
						TextLocation.InsideRight	=> textLeftPoint - textLayout.Metrics.Width,
						TextLocation.ExtremeRight	=> chartPanel.W - textLayout.Metrics.Width,
						TextLocation.ExtremeLeft	=> chartPanel.X,
						_							=> point.X
					};

			RenderTarget.DrawTextLayout(
				new SharpDX.Vector2(
					(float)point.X,
					(float)point.Y),
					textLayout,
					colour.BrushDX,
					SharpDX.Direct2D1.DrawTextOptions.NoSnap
			);
		}

		public override IEnumerable<AlertConditionItem> GetAlertConditionItems() =>
			Anchors.Select(anchor => new AlertConditionItem { Name = anchor.DisplayName, ShouldOnlyDisplayName = true, Tag = anchor });

        public override Cursor GetCursor(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, Point point)
        {
            switch (DrawingState)
			{
				case DrawingState.Building: return Cursors.Pen;
				case DrawingState.Moving: return IsLocked ? Cursors.No : Cursors.SizeAll;
				case DrawingState.Editing: return IsLocked ? Cursors.No : editingAnchor == EntryAnchor ? Cursors.SizeNESW : Cursors.SizeNWSE;
				default:
					if (!DrawTarget) return null;

					Point entryAnchorPixelPoint = EntryAnchor.GetPoint(chartControl, chartPanel, chartScale);
					ChartAnchor closestAnchor = GetClosestAnchor(chartControl, chartPanel, chartScale, cursorSensitivity, point);

					if (closestAnchor != null)
						return IsLocked ? Cursors.Arrow : closestAnchor == EntryAnchor ? Cursors.SizeNESW : Cursors.SizeNWSE;

					Point stopAnchorPixelPoint = RiskAnchor.GetPoint(chartControl, chartPanel, chartScale);
					Vector anchorsVector = stopAnchorPixelPoint - entryAnchorPixelPoint;

					if (MathHelper.IsPointAlongVector(point, entryAnchorPixelPoint, anchorsVector, cursorSensitivity))
						return IsLocked ? Cursors.Arrow : Cursors.SizeAll;

					Point targetPoint = RewardAnchor.GetPoint(chartControl, chartPanel, chartScale);
					Vector targetToEntryVector = targetPoint - entryAnchorPixelPoint;

					return MathHelper.IsPointAlongVector(
						point,
						entryAnchorPixelPoint,
						targetToEntryVector,
						cursorSensitivity
					) ? IsLocked ? Cursors.Arrow : Cursors.SizeAll : null;
			}
        }

		public override Point[] GetSelectionPoints(ChartControl chartControl, ChartScale chartScale)
		{
			ChartPanel chartPanel = chartControl.ChartPanels[chartScale.PanelIndex];
			Point entryPoint = EntryAnchor.GetPoint(chartControl, chartPanel, chartScale);
			Point stopPoint = RiskAnchor.GetPoint(chartControl, chartPanel, chartScale);

			if (!DrawTarget) return [entryPoint, stopPoint];

			Point targetPoint = RewardAnchor.GetPoint(chartControl, chartPanel, chartScale);
			return [entryPoint, stopPoint, targetPoint];
		}

        public override bool IsAlertConditionTrue(AlertConditionItem conditionItem, Condition condition, ChartAlertValue[] values, ChartControl chartControl, ChartScale chartScale)
        {
            if (conditionItem.Tag is not ChartAnchor chartAnchor) return false;

			ChartPanel chartPanel = chartControl.ChartPanels[PanelIndex];
			double alertY = chartScale.GetYByValue(chartAnchor.Price);
			Point entryPoint = EntryAnchor.GetPoint(chartControl, chartPanel, chartScale);
			Point stopPoint = RiskAnchor.GetPoint(chartControl, chartPanel, chartScale);
			Point targetPoint = RewardAnchor.GetPoint(chartControl, chartPanel, chartScale);
			double anchorMinX = DrawTarget ? new[] { entryPoint.X, stopPoint.X, targetPoint.X }.Min() : new[] {entryPoint.X, stopPoint.X}.Min();
			double anchorMaxX = DrawTarget ? new[] { entryPoint.X, stopPoint.X, targetPoint.X }.Max() : new[] {entryPoint.X, stopPoint.X}.Max();
			double lineStartX = IsExtendedLinesLeft ? chartPanel.X : anchorMinX;
			double lineEndX = IsExtendedLinesRight ? chartPanel.X + chartPanel.W : anchorMaxX;

			double firstBarX = chartControl.GetXByTime(values[0].Time);
			double firstBarY = chartScale.GetYByValue(values[0].Value);
			
			if (lineEndX < firstBarX) return false;

			Point lineStartPoint = new Point(lineStartX, alertY);
			Point lineEndPoint = new Point(lineEndX, alertY);
			Point barPoint = new Point(firstBarX, firstBarY);

			MathHelper.PointLineLocation pointLocation = MathHelper.GetPointLineLocation(lineStartPoint, lineEndPoint, barPoint);

			switch (condition)
			{
				case Condition.Greater: return pointLocation == MathHelper.PointLineLocation.LeftOrAbove;
				case Condition.GreaterEqual: return pointLocation == MathHelper.PointLineLocation.LeftOrAbove || pointLocation == MathHelper.PointLineLocation.DirectlyOnLine;
				case Condition.Less: return pointLocation == MathHelper.PointLineLocation.RightOrBelow;
				case Condition.LessEqual: return pointLocation == MathHelper.PointLineLocation.RightOrBelow || pointLocation == MathHelper.PointLineLocation.DirectlyOnLine;
				case Condition.Equals: return pointLocation == MathHelper.PointLineLocation.DirectlyOnLine;
				case Condition.NotEqual: return pointLocation != MathHelper.PointLineLocation.DirectlyOnLine;
				case Condition.CrossAbove:
				case Condition.CrossBelow:

					bool Predicate(ChartAlertValue alertValue)
					{
						double barX = chartControl.GetXByTime(alertValue.Time);
						double barY = chartScale.GetYByValue(alertValue.Value);

						Point stepBarPoint = new Point(barX, barY);

						MathHelper.PointLineLocation ptLocation = MathHelper.GetPointLineLocation(lineStartPoint, lineEndPoint, stepBarPoint);

						if (condition == Condition.CrossAbove) 
							return ptLocation == MathHelper.PointLineLocation.LeftOrAbove;
						
						return ptLocation == MathHelper.PointLineLocation.RightOrBelow;
					}

					return MathHelper.DidPredicateCross(values, Predicate);
			}
			return false;
        }

        public override bool IsVisibleOnChart(ChartControl chartControl, ChartScale chartScale, DateTime firstTimeOnChart, DateTime lastTimeOnChart)
			=> DrawingState == DrawingState.Building || Anchors.Any(a => a.Time >= firstTimeOnChart && a.Time <= lastTimeOnChart);

		public override void OnCalculateMinMax()
		{
			MinValue = double.MaxValue;
			MaxValue = double.MinValue;

			if (!IsVisible) return;

			if (Anchors.Any(a => !a.IsEditing))
				foreach (ChartAnchor anchor in Anchors)
				{
					if (anchor.DisplayName == RewardAnchor.DisplayName && !DrawTarget) continue;
					
					MinValue = Math.Min(MinValue, anchor.Price);
					MaxValue = Math.Max(MaxValue, anchor.Price);
				}
		}

		[EditorBrowsable(EditorBrowsableState.Never)]
		public void SetReward()
		{
			if (Anchors == null || AttachedTo == null) return;

			entryPrice = AttachedTo.Instrument.MasterInstrument.RoundToTickSize(EntryAnchor.Price);
			stopPrice = AttachedTo.Instrument.MasterInstrument.RoundToTickSize(RiskAnchor.Price);

			risk = entryPrice - stopPrice;
			reward = risk * Ratio;
			targetPrice = AttachedTo.Instrument.MasterInstrument.RoundToTickSize(entryPrice + reward);

			RewardAnchor.Price = targetPrice;
			RewardAnchor.IsEditing = false;

			needsRatioUpdate = false;
		}

		[EditorBrowsable(EditorBrowsableState.Never)]
		public void SetRisk()
		{
			if (Anchors == null || AttachedTo == null) return;

			entryPrice = AttachedTo.Instrument.MasterInstrument.RoundToTickSize(EntryAnchor.Price);
			targetPrice = AttachedTo.Instrument.MasterInstrument.RoundToTickSize(RewardAnchor.Price);

			reward = targetPrice - entryPrice;
			risk = reward / Ratio;
			stopPrice = AttachedTo.Instrument.MasterInstrument.RoundToTickSize(entryPrice - risk);

			RiskAnchor.Price = stopPrice;
			RiskAnchor.IsEditing = false;

			needsRatioUpdate = false;
		}

		public override void OnMouseDown(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
		{
			switch(DrawingState)
			{
				case DrawingState.Building:
					if (EntryAnchor.IsEditing)
					{
						dataPoint.CopyDataValues(EntryAnchor);
						dataPoint.CopyDataValues(RiskAnchor);
						EntryAnchor.IsEditing	= false;
						entryPrice				= AttachedTo.Instrument.MasterInstrument.RoundToTickSize(EntryAnchor.Price);
					}
					else if (RiskAnchor.IsEditing)
					{
						dataPoint.CopyDataValues(RiskAnchor);
						RiskAnchor.IsEditing	= false;
						stopPrice				= AttachedTo.Instrument.MasterInstrument.RoundToTickSize(RiskAnchor.Price);

						SetReward();

						RewardAnchor.Time		= EntryAnchor.Time;
						RewardAnchor.SlotIndex	= EntryAnchor.SlotIndex;
						RewardAnchor.IsEditing	= false;
					}
					if (!EntryAnchor.IsEditing && !RiskAnchor.IsEditing && !RewardAnchor.IsEditing)
					{
						DrawingState = DrawingState.Normal;
						IsSelected = false;
					}
					break;

				case DrawingState.Normal:
					Point point = dataPoint.GetPoint(chartControl, chartPanel, chartScale);
					editingAnchor = GetClosestAnchor(chartControl, chartPanel, chartScale, cursorSensitivity, point);

					if (editingAnchor != null)
					{
						editingAnchor.IsEditing = true;
						DrawingState = DrawingState.Editing;
					}
					else if (GetCursor(chartControl, chartPanel, chartScale, point) == null)
						IsSelected = false;
					else
						DrawingState = DrawingState.Moving;
					break;
			}
		}

        public override void OnMouseMove(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
        {
            if (IsLocked && DrawingState != DrawingState.Building || !IsVisible) return;

			if (DrawingState == DrawingState.Building)
			{
				if (EntryAnchor.IsEditing)
					dataPoint.CopyDataValues(EntryAnchor);
				else if (RiskAnchor.IsEditing)
					dataPoint.CopyDataValues(RiskAnchor);
				else if (RewardAnchor.IsEditing)
					dataPoint.CopyDataValues(RewardAnchor);
			} 
			else if (DrawingState == DrawingState.Editing && editingAnchor != null)
			{
				dataPoint.CopyDataValues(editingAnchor);
				if (editingAnchor != EntryAnchor)
				{
					if (editingAnchor != RewardAnchor && Ratio.ApproxCompare(0) != 0)
						SetReward();
					else if (Ratio.ApproxCompare(0) != 0)
						SetRisk();
				}
			}
			else if (DrawingState == DrawingState.Moving)
				foreach (ChartAnchor anchor in Anchors)
					anchor.MoveAnchor(InitialMouseDownAnchor, dataPoint, chartControl, chartPanel, chartScale, this);
			
			entryPrice = AttachedTo.Instrument.MasterInstrument.RoundToTickSize(EntryAnchor.Price);
			stopPrice = AttachedTo.Instrument.MasterInstrument.RoundToTickSize(RiskAnchor.Price);
			targetPrice = AttachedTo.Instrument.MasterInstrument.RoundToTickSize(RewardAnchor.Price);
        }

        public override void OnMouseUp(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
		{
			if (DrawingState == DrawingState.Building) return;

			if (DrawingState == DrawingState.Editing || DrawingState == DrawingState.Moving) 
				DrawingState = DrawingState.Normal;

			if (editingAnchor != null)
			{
				if (editingAnchor == EntryAnchor)
				{
					SetReward();

					if (Ratio.ApproxCompare(0) != 0) SetRisk();
				}
				editingAnchor.IsEditing = false;
			}
			editingAnchor = null;
		}

        public override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (!IsVisible) return;
			if (Anchors.All(a => a.IsEditing)) return;
			if (needsRatioUpdate && DrawTarget) SetReward();

			ChartPanel chartPanel = chartControl.ChartPanels[PanelIndex];
			Point entryPoint = EntryAnchor.GetPoint(chartControl, chartPanel, chartScale);
			Point stopPoint = RiskAnchor.GetPoint(chartControl, chartPanel, chartScale);
			Point targetPoint = RewardAnchor.GetPoint(chartControl, chartPanel, chartScale);

			AnchorLineStroke.RenderTarget = RenderTarget;
			StopLineStroke.RenderTarget = RenderTarget;
			EntryLineStroke.RenderTarget = RenderTarget;

			RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
			RenderTarget.DrawLine(entryPoint.ToVector2(), stopPoint.ToVector2(), AnchorLineStroke.BrushDX, AnchorLineStroke.Width, AnchorLineStroke.StrokeStyle);

			double anchorMinX = DrawTarget ? new[] { entryPoint.X, stopPoint.X, targetPoint.X }.Min() : new[] {entryPoint.X, stopPoint.X}.Min();
			double anchorMaxX = DrawTarget ? new[] { entryPoint.X, stopPoint.X, targetPoint.X }.Max() : new[] {entryPoint.X, stopPoint.X}.Max();
			double lineStartX = IsExtendedLinesLeft ? chartPanel.X : anchorMinX;
			double lineEndX = IsExtendedLinesRight ? chartPanel.X + chartPanel.W : anchorMaxX;

			SharpDX.Vector2 entryStartVector = new((float)lineStartX, (float)entryPoint.Y);
			SharpDX.Vector2 entryEndVector = new((float)lineEndX, (float)entryPoint.Y);
			SharpDX.Vector2 stopStartVector = new((float)lineStartX, (float)stopPoint.Y);
			SharpDX.Vector2 stopEndVector = new((float)lineEndX, (float)stopPoint.Y);

			SharpDX.Direct2D1.Brush tmpBrush = IsInHitTest ? chartControl.SelectionBrush : AnchorLineStroke.BrushDX;

			if (DrawTarget)
			{
				AnchorLineStroke.RenderTarget = RenderTarget;
				RenderTarget.DrawLine(entryPoint.ToVector2(), targetPoint.ToVector2(), tmpBrush, AnchorLineStroke.Width, AnchorLineStroke.StrokeStyle);

				TargetLineStroke.RenderTarget = RenderTarget;
				SharpDX.Vector2 targetStartVector = new((float)lineStartX, (float)targetPoint.Y);
				SharpDX.Vector2 targetEndVector = new((float)lineEndX, (float)targetPoint.Y);

				tmpBrush = IsInHitTest ? chartControl.SelectionBrush : TargetLineStroke.BrushDX;
				RenderTarget.DrawLine(targetStartVector, targetEndVector, tmpBrush, TargetLineStroke.Width, TargetLineStroke.StrokeStyle);
				DrawPriceText(RewardAnchor, targetPoint, targetPrice, chartControl, chartPanel, chartScale);
			}

			tmpBrush = IsInHitTest ? chartControl.SelectionBrush : EntryLineStroke.BrushDX;
			RenderTarget.DrawLine(entryStartVector, entryEndVector, tmpBrush, EntryLineStroke.Width, EntryLineStroke.StrokeStyle);
			DrawPriceText(EntryAnchor, entryPoint, entryPrice, chartControl, chartPanel, chartScale);

			tmpBrush = IsInHitTest ? chartControl.SelectionBrush : StopLineStroke.BrushDX;
			RenderTarget.DrawLine(stopStartVector, stopEndVector, tmpBrush, StopLineStroke.Width, StopLineStroke.StrokeStyle);
			DrawPriceText(RiskAnchor, stopPoint, stopPrice, chartControl, chartPanel, chartScale);

			DrawRiskText(chartControl, chartPanel, chartScale);
		}


		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= @"Custom Risk vs Reward tool which will also auto calculate and display the risk, and recommended max contracts based on acceptable risk. ";
				Name						= "RiskRewardExtras";
				Ratio						= 2;
				ShowRiskText				= true;
				AnchorLineStroke 			= new Stroke(Brushes.DarkGray,	DashStyleHelper.Solid, 1f, 50);
				EntryLineStroke 			= new Stroke(Brushes.Goldenrod,	DashStyleHelper.Solid, 2f);
				StopLineStroke 				= new Stroke(Brushes.Crimson,	DashStyleHelper.Solid, 2f);
				TargetLineStroke 			= new Stroke(Brushes.SeaGreen,	DashStyleHelper.Solid, 2f);
				EntryAnchor					= new ChartAnchor { IsEditing = true, DrawingTool = this };
				RiskAnchor					= new ChartAnchor { IsEditing = true, DrawingTool = this };
				RewardAnchor				= new ChartAnchor { IsEditing = true, DrawingTool = this };
				EntryAnchor.DisplayName		= "Entry Point";
				RiskAnchor.DisplayName		= "Stop Loss";
				RewardAnchor.DisplayName	= "Take Profit";
			}
			else if (State == State.Terminated)
			{
				Dispose();
			}
		}

	}
}
