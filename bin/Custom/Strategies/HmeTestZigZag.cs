//
// Copyright (C) 2024, NinjaTrader LLC <www.ninjatrader.com>.
// NinjaTrader reserves the right to modify or overwrite this NinjaScript component with each release.
//
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
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

//This namespace holds strategies in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Strategies
{
	public class HmeTestZigZag : Strategy
	{
		private ZigZag zigZag;

		// Track all ZigZag highs and lows
		private List<ZigZagLevel> zigZagHighs = new List<ZigZagLevel>();
		private List<ZigZagLevel> zigZagLows = new List<ZigZagLevel>();

		// Track traded ZigZag points separately for highs and lows
		private HashSet<double> tradedHighs = new HashSet<double>();
		private HashSet<double> tradedLows = new HashSet<double>();

		// Reversal distance tracking variables
		private double zoneHighExtremum = 0;
		private double zoneLowExtremum = 0;
		private bool inZoneForHigh = false;
		private bool inZoneForLow = false;

		// ATM Strategy variables
		private string atmStrategyId = string.Empty;
		private string orderId = string.Empty;
		private bool isAtmStrategyCreated = false;

		private class ZigZagLevel
		{
			public double Price { get; set; }
			public int DetectedBar { get; set; }
		}

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "ZigZag Proximity Strategy - Places orders when price approaches ZigZag points and candle reverses";
				Name = "HmeTestZigZag";
				Calculate = Calculate.OnBarClose;
				EntriesPerDirection = 1;
				EntryHandling = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds = 30;
				BarsRequiredToTrade = 20;

				// ZigZag parameters
				DeviationValue = 60.0;  // Default 60 points
				UseHighLow = true;

				// Proximity zone parameters
				ZoneAbovePoints = 2.0;
				ZoneBelowPoints = 2.0;

				// Reversal distance parameter (0 = OnBarClose mode, >0 = tick-based reversal mode)
				ReversalDistancePoints = 0.0;

				// Risk management in points
				StopLossPoints = 10.0;
				ProfitTargetPoints = 15.0;

				// Time filter
				TradingStartTime = TimeSpan.Parse("00:00");
				TradingEndTime = TimeSpan.Parse("23:59");

				// ATM Strategy (leave empty for standard mode)
				AtmTemplateName = string.Empty;

				// This strategy has been designed to take advantage of performance gains in Strategy Analyzer optimizations
				IsInstantiatedOnEachOptimizationIteration = true;
			}
			else if (State == State.DataLoaded)
			{
				// Set Calculate mode based on ReversalDistancePoints
				if (ReversalDistancePoints > 0)
					Calculate = Calculate.OnEachTick;
				else
					Calculate = Calculate.OnBarClose;

				// Create ZigZag indicator with points deviation
				zigZag = ZigZag(DeviationType.Points, DeviationValue, UseHighLow);
				AddChartIndicator(zigZag);
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < BarsRequiredToTrade)
				return;

			// Detect new ZigZag highs and add to list
			double currentZigZagHigh = zigZag.ZigZagHigh[0];
			if (currentZigZagHigh != 0.0 && !zigZagHighs.Any(z => Math.Abs(z.Price - currentZigZagHigh) < 0.01))
			{
				zigZagHighs.Add(new ZigZagLevel
				{
					Price = currentZigZagHigh,
					DetectedBar = CurrentBar
				});
				Print(string.Format("Bar {0}: New ZigZag HIGH detected at {1}", CurrentBar, currentZigZagHigh));
			}

			// Detect new ZigZag lows and add to list
			double currentZigZagLow = zigZag.ZigZagLow[0];
			if (currentZigZagLow != 0.0 && !zigZagLows.Any(z => Math.Abs(z.Price - currentZigZagLow) < 0.01))
			{
				zigZagLows.Add(new ZigZagLevel
				{
					Price = currentZigZagLow,
					DetectedBar = CurrentBar
				});
				Print(string.Format("Bar {0}: New ZigZag LOW detected at {1}", CurrentBar, currentZigZagLow));
			}

			// Invalidate zones that have been passed through without reversal
			InvalidateZones();

			// For OnBarClose mode, check for orders at bar close
			if (ReversalDistancePoints == 0)
			{
				CheckForOrders();
			}
			else
			{
				// For tick-based mode, reset zone tracking at new bar
				ResetZoneTracking();
			}
		}

		protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
		{
			if (ReversalDistancePoints == 0 || CurrentBar < BarsRequiredToTrade)
				return;

			// Only process last price updates
			if (marketDataUpdate.MarketDataType != MarketDataType.Last)
				return;

			// Time filter check
			DateTime currentTime = Time[0];
			if (currentTime.TimeOfDay < TradingStartTime || currentTime.TimeOfDay > TradingEndTime)
				return;

			// Only trade if flat
			if (Position.MarketPosition != MarketPosition.Flat)
				return;

			// Check for reversal distance orders
			CheckForReversalDistanceOrders(marketDataUpdate.Price);
		}

		private void CheckForOrders()
		{
			// Time filter check
			DateTime currentTime = Time[0];
			if (currentTime.TimeOfDay < TradingStartTime || currentTime.TimeOfDay > TradingEndTime)
				return;

			// Only trade if flat
			if (Position.MarketPosition != MarketPosition.Flat)
				return;

			// Check ALL ZigZag high points (potential short entries)
			CheckZigZagHighs();

			// Check ALL ZigZag low points (potential long entries)
			CheckZigZagLows();

			// Handle ATM strategy if configured and running live
			if (!string.IsNullOrEmpty(AtmTemplateName) && State != State.Historical)
				HandleAtmStrategy();
		}

		private void CheckForReversalDistanceOrders(double currentPrice)
		{
			// Check ALL ZigZag highs for potential short entries
			foreach (ZigZagLevel high in zigZagHighs.ToList())
			{
				if (tradedHighs.Contains(high.Price))
					continue;

				// Check if current price is within the proximity zone
				double zoneTop = high.Price + ZoneAbovePoints;
				double zoneBottom = high.Price - ZoneBelowPoints;

				if (currentPrice >= zoneBottom && currentPrice <= zoneTop)
				{
					// Enter zone - start tracking extremum
					if (!inZoneForHigh)
					{
						inZoneForHigh = true;
						zoneHighExtremum = currentPrice;
						Print(string.Format("Tick: Entered HIGH zone at {0} (ZigZag: {1}), starting extremum tracking at {2}",
							currentPrice, high.Price, zoneHighExtremum));
					}
					else
					{
						// Update extremum if price is higher
						if (currentPrice > zoneHighExtremum)
						{
							zoneHighExtremum = currentPrice;
						}
						// Check for reversal: price drops by ReversalDistancePoints from extremum
						else if (zoneHighExtremum - currentPrice >= ReversalDistancePoints)
						{
							Print(string.Format("Tick: SHORT Reversal detected! ZigZag HIGH: {0}, Extremum: {1}, Current: {2}, Drop: {3} >= {4}",
								high.Price, zoneHighExtremum, currentPrice, zoneHighExtremum - currentPrice, ReversalDistancePoints));

							PlaceShortOrder(high.Price);
							tradedHighs.Add(high.Price);
							ResetZoneTracking();
							break; // Only one order at a time
						}
					}
				}
				else if (inZoneForHigh)
				{
					// Left zone - reset tracking
					Print(string.Format("Tick: Left HIGH zone (price: {0}, zone: [{1}-{2}])", currentPrice, zoneBottom, zoneTop));
					inZoneForHigh = false;
					zoneHighExtremum = 0;
				}
			}

			// Check ALL ZigZag lows for potential long entries
			foreach (ZigZagLevel low in zigZagLows.ToList())
			{
				if (tradedLows.Contains(low.Price))
					continue;

				// Check if current price is within the proximity zone
				double zoneTop = low.Price + ZoneBelowPoints;
				double zoneBottom = low.Price - ZoneAbovePoints;

				if (currentPrice >= zoneBottom && currentPrice <= zoneTop)
				{
					// Enter zone - start tracking extremum
					if (!inZoneForLow)
					{
						inZoneForLow = true;
						zoneLowExtremum = currentPrice;
						Print(string.Format("Tick: Entered LOW zone at {0} (ZigZag: {1}), starting extremum tracking at {2}",
							currentPrice, low.Price, zoneLowExtremum));
					}
					else
					{
						// Update extremum if price is lower
						if (currentPrice < zoneLowExtremum)
						{
							zoneLowExtremum = currentPrice;
						}
						// Check for reversal: price rises by ReversalDistancePoints from extremum
						else if (currentPrice - zoneLowExtremum >= ReversalDistancePoints)
						{
							Print(string.Format("Tick: LONG Reversal detected! ZigZag LOW: {0}, Extremum: {1}, Current: {2}, Rise: {3} >= {4}",
								low.Price, zoneLowExtremum, currentPrice, currentPrice - zoneLowExtremum, ReversalDistancePoints));

							PlaceLongOrder(low.Price);
							tradedLows.Add(low.Price);
							ResetZoneTracking();
							break; // Only one order at a time
						}
					}
				}
				else if (inZoneForLow)
				{
					// Left zone - reset tracking
					Print(string.Format("Tick: Left LOW zone (price: {0}, zone: [{1}-{2}])", currentPrice, zoneBottom, zoneTop));
					inZoneForLow = false;
					zoneLowExtremum = 0;
				}
			}
		}

		private void ResetZoneTracking()
		{
			inZoneForHigh = false;
			inZoneForLow = false;
			zoneHighExtremum = 0;
			zoneLowExtremum = 0;
		}

		private void CheckZigZagHighs()
		{
			// Check ALL ZigZag highs for potential short entries
			foreach (ZigZagLevel high in zigZagHighs.ToList())
			{
				if (tradedHighs.Contains(high.Price))
					continue;

				// Check if current bar's high is within the proximity zone
				double zoneTop = high.Price + ZoneAbovePoints;
				double zoneBottom = high.Price - ZoneBelowPoints;

				if (High[0] >= zoneBottom && High[0] <= zoneTop)
				{
					// Check for bearish reversal (candle closes below open)
					if (Close[0] < Open[0])
					{
						Print(string.Format("Bar {0}: ZigZag HIGH at {1} (detected bar {2}), High {3} in zone [{4}-{5}], Bearish reversal detected",
							CurrentBar, high.Price, high.DetectedBar, High[0], zoneBottom, zoneTop));

						PlaceShortOrder(high.Price);
						tradedHighs.Add(high.Price);
						break; // Only one order at a time
					}
				}
			}
		}

		private void CheckZigZagLows()
		{
			// Check ALL ZigZag lows for potential long entries
			foreach (ZigZagLevel low in zigZagLows.ToList())
			{
				if (tradedLows.Contains(low.Price))
					continue;

				// Check if current bar's low is within the proximity zone
				// ZoneAbovePoints: below the low (further down), ZoneBelowPoints: above the low (where price comes from)
				double zoneTop = low.Price + ZoneBelowPoints;
				double zoneBottom = low.Price - ZoneAbovePoints;

				if (Low[0] >= zoneBottom && Low[0] <= zoneTop)
				{
					// Check for bullish reversal (candle closes above open)
					if (Close[0] > Open[0])
					{
						Print(string.Format("Bar {0}: ZigZag LOW at {1} (detected bar {2}), Low {3} in zone [{4}-{5}], Bullish reversal detected",
							CurrentBar, low.Price, low.DetectedBar, Low[0], zoneBottom, zoneTop));

						PlaceLongOrder(low.Price);
						tradedLows.Add(low.Price);
						break; // Only one order at a time
					}
				}
			}
		}

		private void PlaceShortOrder(double zigZagHigh)
		{
			string signalName = "ZigZagShort_" + CurrentBar;

			if (string.IsNullOrEmpty(AtmTemplateName) || State == State.Historical)
			{
				// Standard mode - use SetStopLoss/SetProfitTarget
				double stopPrice = Close[0] + StopLossPoints;  // Fix: Calculate from entry price, not ZigZag point
				double targetPrice = Close[0] - ProfitTargetPoints;

				SetStopLoss(signalName, CalculationMode.Price, stopPrice, false);
				SetProfitTarget(signalName, CalculationMode.Price, targetPrice);

				Print(string.Format("    SHORT Entry: {0}, Stop: {1}, Target: {2}",
					Close[0], stopPrice, targetPrice));

				EnterShort(0, signalName);
			}
			else
			{
				// ATM mode - create ATM strategy
				CreateAtmStrategy(OrderAction.Sell, Close[0]);
			}
		}

		private void PlaceLongOrder(double zigZagLow)
		{
			string signalName = "ZigZagLong_" + CurrentBar;

			if (string.IsNullOrEmpty(AtmTemplateName) || State == State.Historical)
			{
				// Standard mode - use SetStopLoss/SetProfitTarget
				double stopPrice = Close[0] - StopLossPoints;  // Fix: Calculate from entry price, not ZigZag point
				double targetPrice = Close[0] + ProfitTargetPoints;

				SetStopLoss(signalName, CalculationMode.Price, stopPrice, false);
				SetProfitTarget(signalName, CalculationMode.Price, targetPrice);

				Print(string.Format("    LONG Entry: {0}, Stop: {1}, Target: {2}",
					Close[0], stopPrice, targetPrice));

				EnterLong(0, signalName);
			}
			else
			{
				// ATM mode - create ATM strategy
				CreateAtmStrategy(OrderAction.Buy, Close[0]);
			}
		}

		private void CreateAtmStrategy(OrderAction orderAction, double entryPrice)
		{
			if (orderId.Length == 0 && atmStrategyId.Length == 0)
			{
				isAtmStrategyCreated = false;
				atmStrategyId = GetAtmStrategyUniqueId();
				orderId = GetAtmStrategyUniqueId();

				AtmStrategyCreate(orderAction, OrderType.Market, entryPrice, 0, TimeInForce.Day, orderId,
					AtmTemplateName, atmStrategyId, (atmCallbackErrorCode, atmCallBackId) => {
					if (atmCallbackErrorCode == ErrorCode.NoError && atmCallBackId == atmStrategyId)
					{
						isAtmStrategyCreated = true;
						Print("ATM Strategy created successfully: " + atmStrategyId);
					}
				});
			}
		}

		private void InvalidateZones()
		{
			// Remove ZigZag highs that have been passed through without reversal
			zigZagHighs.RemoveAll(high =>
			{
				if (tradedHighs.Contains(high.Price))
					return false; // Already traded, keep in list

				// Calculate zone boundaries
				double zoneTop = high.Price + ZoneAbovePoints;
				double zoneBottom = high.Price - ZoneBelowPoints;

				// Invalidate if price has moved above the zone (passed through without reversal)
				if (High[0] > zoneTop)
				{
					Print(string.Format("Bar {0}: ZigZag HIGH zone at {1} invalidated (price {2} > zone top {3})",
						CurrentBar, high.Price, High[0], zoneTop));
					return true; // Remove from list
				}

				return false; // Keep in list
			});

			// Remove ZigZag lows that have been passed through without reversal
			zigZagLows.RemoveAll(low =>
			{
				if (tradedLows.Contains(low.Price))
					return false; // Already traded, keep in list

				// Calculate zone boundaries
				// ZoneAbovePoints: below the low (further down), ZoneBelowPoints: above the low (where price comes from)
				double zoneTop = low.Price + ZoneBelowPoints;
				double zoneBottom = low.Price - ZoneAbovePoints;

				// Invalidate if price has moved below the zone (passed through without reversal)
				if (Low[0] < zoneBottom)
				{
					Print(string.Format("Bar {0}: ZigZag LOW zone at {1} invalidated (price {2} < zone bottom {3})",
						CurrentBar, low.Price, Low[0], zoneBottom));
					return true; // Remove from list
				}

				return false; // Keep in list
			});
		}

		private void HandleAtmStrategy()
		{
			// Check that atm strategy was created before checking other properties
			if (!isAtmStrategyCreated)
				return;

			// Check for a pending entry order
			if (orderId.Length > 0)
			{
				string[] status = GetAtmStrategyEntryOrderStatus(orderId);

				if (status.GetLength(0) > 0)
				{
					// If the order state is terminal, reset the order id value
					if (status[2] == "Filled" || status[2] == "Cancelled" || status[2] == "Rejected")
						orderId = string.Empty;
				}
			}
			// If the strategy has terminated reset the strategy id
			else if (atmStrategyId.Length > 0 && GetAtmStrategyMarketPosition(atmStrategyId) == Cbi.MarketPosition.Flat)
			{
				atmStrategyId = string.Empty;
				isAtmStrategyCreated = false;
			}
		}

		#region Properties

		[Range(0.1, double.MaxValue), NinjaScriptProperty]
		[Display(Name = "DeviationValue", Order = 1, GroupName = "ZigZag Parameters")]
		public double DeviationValue { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "UseHighLow", Order = 2, GroupName = "ZigZag Parameters")]
		public bool UseHighLow { get; set; }

		[Range(0, double.MaxValue), NinjaScriptProperty]
		[Display(Name = "ZoneAbovePoints", Order = 3, GroupName = "Proximity Zone")]
		public double ZoneAbovePoints { get; set; }

		[Range(0, double.MaxValue), NinjaScriptProperty]
		[Display(Name = "ZoneBelowPoints", Order = 4, GroupName = "Proximity Zone")]
		public double ZoneBelowPoints { get; set; }

		[Range(0.1, double.MaxValue), NinjaScriptProperty]
		[Display(Name = "StopLossPoints", Order = 5, GroupName = "Risk Management")]
		public double StopLossPoints { get; set; }

		[Range(0.1, double.MaxValue), NinjaScriptProperty]
		[Display(Name = "ProfitTargetPoints", Order = 6, GroupName = "Risk Management")]
		public double ProfitTargetPoints { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "TradingStartTime", Order = 7, GroupName = "Time Filter")]
		public TimeSpan TradingStartTime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "TradingEndTime", Order = 8, GroupName = "Time Filter")]
		public TimeSpan TradingEndTime { get; set; }

		[Range(0, double.MaxValue), NinjaScriptProperty]
		[Display(Name = "ReversalDistancePoints", Order = 9, GroupName = "Reversal Mode")]
		public double ReversalDistancePoints { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "AtmTemplateName", Order = 10, GroupName = "ATM Strategy")]
		public string AtmTemplateName { get; set; }

		#endregion
	}
}
