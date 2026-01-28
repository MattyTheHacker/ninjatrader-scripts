#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Strategies;
#endregion

//This namespace holds Add ons in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.AddOns
{
	public class RvRExecutionButtonAddOn : NinjaTrader.NinjaScript.AddOnBase
	{
		private Button executeButton;
		private RowDefinition rowDefinition;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"AddOn to create an execution button to trigger the RvR drawing tool strategy orders.";
				Name										= "RvRExecutionButtonAddOn";
			}
			else if (State == State.Configure)
			{
			}
		}

		private T FindFirstVisualChild<T>(DependencyObject parent) where T : DependencyObject
		{
			if (parent == null) return null;
			for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
			{
				var child = VisualTreeHelper.GetChild(parent, i);
				if (child is T typedChild) return typedChild;

				var result = FindFirstVisualChild<T>(child);
				if (result != null) return result;
			}
			return null;
		}

        protected override void OnWindowCreated(Window window)
        {
            if (window is Chart chart)
            {
                if (chart.FindFirst("ChartWindowChartTraderControl") is not ChartTrader chartTrader) return;

                if (chartTrader.FindName("grdMain") is not Grid mainGrid) return;

                int instrumentRowIndex = mainGrid.RowDefinitions.Count;
                mainGrid.RowDefinitions.Insert(instrumentRowIndex, new RowDefinition { Height = GridLength.Auto });

				executeButton ??= new Button
					{
						Content = "Place RvR Orders",
						Background = Brushes.DarkSlateGray,
						Foreground = Brushes.White,
						Margin = new Thickness(5),
						Padding = new Thickness(5),
						HorizontalAlignment = HorizontalAlignment.Stretch
					};

                executeButton.Click += (_, _) => OnExecuteButtonClicked(chart);

                Grid.SetRow(executeButton, instrumentRowIndex);
                mainGrid.Children.Add(executeButton);
            }
        }

		protected override void OnWindowDestroyed(Window window)
		{
			if (executeButton != null)
			{
				executeButton.Click -= (s, e) => { };
				executeButton = null;
			}
		}

		private void OnExecuteButtonClicked(Chart chart)
		{
			RiskRewardOrderPlacing strategy = StrategyBase.All
				.OfType<RiskRewardOrderPlacing>()
				.FirstOrDefault();

			strategy ??= new RiskRewardOrderPlacing { IsEnabled = true };

			strategy.PlaceOrders();
		}
	}
}
