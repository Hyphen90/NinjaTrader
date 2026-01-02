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
		// Custom ZigZag implementation (no repainting!)
		private enum TrendDirection { Up, Down, Unknown }
		private TrendDirection currentTrend = TrendDirection.Unknown;
		private double currentExtremum = 0;
		private int extremumBar = -1;

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

		// Breakeven tracking
		private bool breakevenSet = false;

		// Unmanaged order tracking
		private Order entryOrder = null;
		private Order stopOrder = null;
		private Order targetOrder = null;

		private class ZigZagLevel
		{
			public double Price { get; set; }
			public int DetectedBar { get; set; }
			public int ActivationBar { get; set; }  // Bar when zone becomes active
			public bool HasLeftZone { get; set; }    // True if price has left zone at least once
			public int LeftZoneBar { get; set; }     // Bar when HasLeftZone was set (prevents immediate invalidation)
			public bool IsLineActive { get; set; }    // True if support/resistance line is still active
			public int LineStartBar { get; set; }     // Bar where the line was first drawn
			public int LineEndBar { get; set; }       // Bar where the line was broken (or CurrentBar if active)
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
				IsUnmanaged = true;  // Enable unmanaged order management for full control

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
				BreakevenPoints = 20.0;  // Move stop to breakeven when 20 points in profit

				// Time filter
				TradingStartTime = TimeSpan.Parse("00:00");
				TradingEndTime = TimeSpan.Parse("23:59");

				// ATM Strategy (leave empty for standard mode)
				AtmTemplateName = string.Empty;

				// Add strategy-owned plots for ZigZag visualization (works in Strategy Analyzer)
				AddPlot(Brushes.Red, "ZigZagHighs");
				AddPlot(Brushes.Blue, "ZigZagLows");

				// This strategy has been designed to take advantage of performance gains in Strategy Analyzer optimizations
				IsInstantiatedOnEachOptimizationIteration = true;
			}
			else if (State == State.Configure)
			{
				// Add secondary tick series for precise breakeven management
				AddDataSeries(BarsPeriodType.Tick, 1);
			}
			else if (State == State.DataLoaded)
			{
				// Reset variables for new run
				zigZagHighs.Clear();
				zigZagLows.Clear();
				tradedHighs.Clear();
				tradedLows.Clear();
				currentTrend = TrendDirection.Unknown;
				currentExtremum = 0;
				extremumBar = -1;
				zoneHighExtremum = 0;
				zoneLowExtremum = 0;
				inZoneForHigh = false;
				inZoneForLow = false;
				atmStrategyId = string.Empty;
				orderId = string.Empty;
				isAtmStrategyCreated = false;

				// Always use OnBarClose for performance with Range Bars
				Calculate = Calculate.OnBarClose;

				// Add original ZigZag indicator for visual comparison (NOT used in trading logic!)
				ZigZag displayZigZag = ZigZag(DeviationType.Points, DeviationValue, UseHighLow);
				AddChartIndicator(displayZigZag);

				// Print strategy parameters at start
				Print($"[START] Deviation={DeviationValue:F1} ZoneAbove={ZoneAbovePoints:F1} ZoneBelow={ZoneBelowPoints:F1} SL={StopLossPoints:F1} PT={ProfitTargetPoints:F1} RevDist={ReversalDistancePoints:F1}");
			}
			else if (State == State.Terminated)
			{
				// Clean termination - no custom statistics needed
				// NinjaTrader will handle all trade metrics automatically
			}
		}

		protected override void OnBarUpdate()
		{
			// Handle different data series
			if (BarsInProgress == 0) // Primary series (Range bars) - ZigZag logic
			{
				if (CurrentBar < BarsRequiredToTrade)
					return;

				// Custom ZigZag implementation (no repainting!)
				// NOTE: NO TIME FILTER HERE - Lines should be drawn 24/7 regardless of trading hours

				// Initialize trend on first bars
				if (currentTrend == TrendDirection.Unknown && CurrentBar >= 2)
				{
					// Determine initial trend based on first few bars
					if (Close[0] > Open[0])
						currentTrend = TrendDirection.Up;
					else
						currentTrend = TrendDirection.Down;

					currentExtremum = (currentTrend == TrendDirection.Up) ? Low[0] : High[0];
					extremumBar = CurrentBar;
					Print($"[TREND INIT {currentTrend} B{CurrentBar} Extremum{currentExtremum:F2}]");
				}

				// Track trend and detect ZigZag points
				if (currentTrend != TrendDirection.Unknown)
				{
					if (currentTrend == TrendDirection.Up)
					{
						// Looking for higher highs
						if (High[0] > currentExtremum)
						{
							currentExtremum = High[0];
							extremumBar = CurrentBar;
						}
						// Check if price dropped enough to confirm the high
						else if (currentExtremum - Low[0] >= DeviationValue)
						{
							// High confirmed! Add to ZigZag highs
							zigZagHighs.Add(new ZigZagLevel
							{
								Price = currentExtremum,
								DetectedBar = extremumBar,
								ActivationBar = CurrentBar + 1,
								HasLeftZone = false,
								LeftZoneBar = -1,
								IsLineActive = true,
								LineStartBar = CurrentBar,
								LineEndBar = CurrentBar + 100
							});

							// Draw support line from peak to current bar
							int barsAgo = CurrentBar - extremumBar;
							Draw.Line(this, $"SupportLine_{extremumBar}", barsAgo, currentExtremum, 0, currentExtremum, Brushes.Green);
							Print($"[HIGH CONFIRMED B{CurrentBar} P{currentExtremum:F2} PeakB{extremumBar}]");

							// Switch to downtrend
							currentTrend = TrendDirection.Down;
							currentExtremum = Low[0];
							extremumBar = CurrentBar;
						}
					}
					else // TrendDirection.Down
					{
						// Looking for lower lows
						if (Low[0] < currentExtremum)
						{
							currentExtremum = Low[0];
							extremumBar = CurrentBar;
						}
						// Check if price rose enough to confirm the low
						else if (High[0] - currentExtremum >= DeviationValue)
						{
							// Low confirmed! Add to ZigZag lows
							zigZagLows.Add(new ZigZagLevel
							{
								Price = currentExtremum,
								DetectedBar = extremumBar,
								ActivationBar = CurrentBar + 1,
								HasLeftZone = false,
								LeftZoneBar = -1,
								IsLineActive = true,
								LineStartBar = CurrentBar,
								LineEndBar = CurrentBar + 100
							});

							// Draw resistance line from peak to current bar
							int barsAgo = CurrentBar - extremumBar;
							Draw.Line(this, $"ResistanceLine_{extremumBar}", barsAgo, currentExtremum, 0, currentExtremum, Brushes.Red);
							Print($"[LOW CONFIRMED B{CurrentBar} P{currentExtremum:F2} PeakB{extremumBar}]");

							// Switch to uptrend
							currentTrend = TrendDirection.Up;
							currentExtremum = High[0];
							extremumBar = CurrentBar;
						}
					}
				}

				// Check for orders at bar close BEFORE invalidating zones
				// Works for both OnBarClose mode (ReversalDistancePoints = 0) and bar-based distance mode (ReversalDistancePoints > 0)
				CheckForOrders();

				// For tick-based mode, reset zone tracking at new bar
				if (ReversalDistancePoints > 0)
				{
					ResetZoneTracking();
				}

				// Invalidate zones that have been passed through without reversal (AFTER checking for orders)
				InvalidateZones();

				// Check for broken support/resistance lines and update them
				UpdateSupportResistanceLines();
			}
			else if (BarsInProgress == 1) // Secondary series (Tick) - Precise order management
			{
				// Safety: Reset orders when position is flat (covers Exit on Session and other edge cases)
				if (Position.MarketPosition == MarketPosition.Flat && (stopOrder != null || targetOrder != null))
				{
					if (stopOrder != null)
					{
						CancelOrder(stopOrder);
						stopOrder = null;
					}
					if (targetOrder != null)
					{
						CancelOrder(targetOrder);
						targetOrder = null;
					}
					breakevenSet = false;
					Print("[RESET] Orders cleared - Position flat");
				}
				// Place stop/target orders after entry fill (tick-precise)
				else if (Position.MarketPosition == MarketPosition.Long && stopOrder == null)
				{
					double stopPriceLevel = Position.AveragePrice - StopLossPoints;
					double targetPriceLevel = Position.AveragePrice + ProfitTargetPoints;

					stopOrder = SubmitOrderUnmanaged(1, OrderAction.Sell, OrderType.StopMarket, Position.Quantity, 0, stopPriceLevel, "StopLoss", "");
					targetOrder = SubmitOrderUnmanaged(1, OrderAction.Sell, OrderType.Limit, Position.Quantity, targetPriceLevel, 0, "ProfitTarget", "");

					Print($"[ORDERS PLACED LONG TICK] Stop:{stopPriceLevel:F2} Target:{targetPriceLevel:F2}");
				}
				else if (Position.MarketPosition == MarketPosition.Short && stopOrder == null)
				{
					double stopPriceLevel = Position.AveragePrice + StopLossPoints;
					double targetPriceLevel = Position.AveragePrice - ProfitTargetPoints;

					stopOrder = SubmitOrderUnmanaged(1, OrderAction.BuyToCover, OrderType.StopMarket, Position.Quantity, 0, stopPriceLevel, "StopLoss", "");
					targetOrder = SubmitOrderUnmanaged(1, OrderAction.BuyToCover, OrderType.Limit, Position.Quantity, targetPriceLevel, 0, "ProfitTarget", "");

					Print($"[ORDERS PLACED SHORT TICK] Stop:{stopPriceLevel:F2} Target:{targetPriceLevel:F2}");
				}

				// Breakeven management on tick series for precise execution
				if (BreakevenPoints > 0 && stopOrder != null)
				{
					if (Position.MarketPosition == MarketPosition.Long && !breakevenSet)
					{
						// Calculate unrealized profit for long position using tick data
						double unrealizedProfit = (Closes[1][0] - Position.AveragePrice);

						if (unrealizedProfit >= BreakevenPoints)
						{
							// Move stop to breakeven (entry price + 1 tick)
							double breakevenPrice = Position.AveragePrice + TickSize;

							// Modify existing stop order
							ChangeOrder(stopOrder, Position.Quantity, 0, breakevenPrice);

							breakevenSet = true;
							Print($"[BREAKEVEN LONG] Stop moved to {breakevenPrice:F2} (Entry: {Position.AveragePrice:F2})");
						}
					}
					else if (Position.MarketPosition == MarketPosition.Short && !breakevenSet)
					{
						// Calculate unrealized profit for short position using tick data
						double unrealizedProfit = (Position.AveragePrice - Closes[1][0]);

						if (unrealizedProfit >= BreakevenPoints)
						{
							// Move stop to breakeven (entry price - 1 tick)
							double breakevenPrice = Position.AveragePrice - TickSize;

							// Modify existing stop order
							ChangeOrder(stopOrder, Position.Quantity, 0, breakevenPrice);

							breakevenSet = true;
							Print($"[BREAKEVEN SHORT] Stop moved to {breakevenPrice:F2} (Entry: {Position.AveragePrice:F2})");
						}
					}
					else if (Position.MarketPosition == MarketPosition.Flat)
					{
						// Reset breakeven flag when position is closed
						breakevenSet = false;
					}
				}
			}
		}


		protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
		{
			// Track order state changes
			if (order == entryOrder)
			{
				if (orderState == OrderState.Filled)
				{
					entryOrder = null; // Reset entry order reference
					Print($"[ENTRY FILLED] Position:{Position.MarketPosition} Entry:{Position.AveragePrice:F2}");
					// Stop/Target orders will be placed in OnBarUpdate on tick series
				}
				else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
				{
					entryOrder = null; // Reset on failure
				}
			}
			else if (order == stopOrder || order == targetOrder)
			{
				// Stop or target filled - reset all orders
				if (orderState == OrderState.Filled)
				{
					// Cancel remaining orders
					if (stopOrder != null && stopOrder != order) CancelOrder(stopOrder);
					if (targetOrder != null && targetOrder != order) CancelOrder(targetOrder);

					stopOrder = null;
					targetOrder = null;
					breakevenSet = false; // Reset for next trade
					Print($"[EXIT FILLED] Order:{order.Name} AvgFillPrice:{averageFillPrice:F2}");
				}
				// Handle cancelled/rejected orders (e.g., Exit on Session)
				else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
				{
					if (order == stopOrder)
					{
						stopOrder = null;
						Print($"[STOP CANCELLED] Order:{order.Name}");
					}
					if (order == targetOrder)
					{
						targetOrder = null;
						Print($"[TARGET CANCELLED] Order:{order.Name}");
					}
					
					// Reset breakeven flag when both orders are cleared
					if (stopOrder == null && targetOrder == null)
					{
						breakevenSet = false;
						Print($"[ORDERS RESET] All exit orders cleared");
					}
				}
			}
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			// Handle execution updates if needed
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
			if (ReversalDistancePoints > 0) // Only print tick logs when using tick-based mode
				Print($"[TICK CHECK Price{currentPrice:F2} Highs{zigZagHighs.Count} Lows{zigZagLows.Count}]");

			// Check ALL ZigZag highs for potential short entries
			foreach (ZigZagLevel high in zigZagHighs.ToList())
			{
				if (tradedHighs.Contains(high.Price))
					continue;

				// Zone must be active and price must have left zone at least once
				if (CurrentBar < high.ActivationBar || !high.HasLeftZone)
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
							Print($"[TICK SHORT REVERSAL B{CurrentBar} P{high.Price:F2} Extremum{zoneHighExtremum:F2} Current{currentPrice:F2} Drop{zoneHighExtremum - currentPrice:F2}]");

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
					inZoneForHigh = false;
					zoneHighExtremum = 0;
				}
			}

			// Check ALL ZigZag lows for potential long entries
			foreach (ZigZagLevel low in zigZagLows.ToList())
			{
				if (tradedLows.Contains(low.Price))
					continue;

				// Zone must be active and price must have left zone at least once
				if (CurrentBar < low.ActivationBar || !low.HasLeftZone)
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
							Print($"[TICK LONG REVERSAL B{CurrentBar} P{low.Price:F2} Extremum{zoneLowExtremum:F2} Current{currentPrice:F2} Rise{currentPrice - zoneLowExtremum:F2}]");

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

				// Zone must be active and price must have left zone at least once
				if (CurrentBar < high.ActivationBar || !high.HasLeftZone)
					continue;

				// Check if current bar's high is within the proximity zone
				double zoneTop = high.Price + ZoneAbovePoints;
				double zoneBottom = high.Price - ZoneBelowPoints;

				if (High[0] >= zoneBottom && High[0] <= zoneTop)
				{
					// For ReversalDistancePoints > 0, check if bar left zone by required distance
					bool meetsDistanceRequirement = true;
					if (ReversalDistancePoints > 0)
					{
						// Bar must have moved below zone bottom by at least ReversalDistancePoints
						double requiredBreakoutLevel = zoneBottom - ReversalDistancePoints;
						meetsDistanceRequirement = Low[0] <= requiredBreakoutLevel;
					}

					if (meetsDistanceRequirement && Close[0] < Open[0])
					{
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

				// Zone must be active and price must have left zone at least once
				if (CurrentBar < low.ActivationBar || !low.HasLeftZone)
					continue;

				// Check if current bar's low is within the proximity zone
				// ZoneAbovePoints: below the low (further down), ZoneBelowPoints: above the low (where price comes from)
				double zoneTop = low.Price + ZoneBelowPoints;
				double zoneBottom = low.Price - ZoneAbovePoints;

				if (Low[0] >= zoneBottom && Low[0] <= zoneTop)
				{
					// For ReversalDistancePoints > 0, check if bar left zone by required distance
					bool meetsDistanceRequirement = true;
					if (ReversalDistancePoints > 0)
					{
						// Bar must have moved above zone top by at least ReversalDistancePoints
						double requiredBreakoutLevel = zoneTop + ReversalDistancePoints;
						meetsDistanceRequirement = High[0] >= requiredBreakoutLevel;
					}

					if (meetsDistanceRequirement && Close[0] > Open[0])
					{
						PlaceLongOrder(low.Price);
						tradedLows.Add(low.Price);
						break; // Only one order at a time
					}
				}
			}
		}

		private void PlaceShortOrder(double zigZagHigh)
		{
			if (string.IsNullOrEmpty(AtmTemplateName) || State == State.Historical)
			{
				// Unmanaged mode - submit entry order
				entryOrder = SubmitOrderUnmanaged(0, OrderAction.SellShort, OrderType.Market, 1, Close[0], 0, "ZigZagShort", "");
				Print($"[ENTRY SHORT SUBMITTED B{CurrentBar} Price{Close[0]:F2}]");
			}
			else
			{
				// ATM mode - create ATM strategy
				CreateAtmStrategy(OrderAction.Sell, Close[0]);
			}
		}

		private void PlaceLongOrder(double zigZagLow)
		{
			if (string.IsNullOrEmpty(AtmTemplateName) || State == State.Historical)
			{
				// Unmanaged mode - submit entry order
				entryOrder = SubmitOrderUnmanaged(0, OrderAction.Buy, OrderType.Market, 1, Close[0], 0, "ZigZagLong", "");
				Print($"[ENTRY LONG SUBMITTED B{CurrentBar} Price{Close[0]:F2}]");
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
			// Process ZigZag highs
			foreach (ZigZagLevel high in zigZagHighs.ToList())
			{
				if (tradedHighs.Contains(high.Price))
					continue; // Already traded, skip

				// Zone not yet active
				if (CurrentBar < high.ActivationBar)
					continue;

				// Calculate zone boundaries
				double zoneTop = high.Price + ZoneAbovePoints;
				double zoneBottom = high.Price - ZoneBelowPoints;

				// Set HasLeftZone flag if price has moved DOWN from the zone (Low < zoneBottom)
				// This means price has left the zone in the direction that would allow a return
				if (Low[0] < zoneBottom && !high.HasLeftZone)
				{
					high.HasLeftZone = true;
					high.LeftZoneBar = CurrentBar;
					Print($"[HIGH ZONE LEFT B{CurrentBar} P{high.Price:F2} Low{Low[0]:F2}]");
				}

				// Invalidate if price has moved above the zone (passed through without reversal)
				// But NOT in the same bar where HasLeftZone was set
				if (High[0] > zoneTop && high.LeftZoneBar != CurrentBar)
				{
					zigZagHighs.Remove(high);
					Print($"[HIGH ZONE INVALIDATED B{CurrentBar} P{high.Price:F2} High{High[0]:F2}]");
				}
			}

			// Process ZigZag lows
			foreach (ZigZagLevel low in zigZagLows.ToList())
			{
				if (tradedLows.Contains(low.Price))
					continue; // Already traded, skip

				// Zone not yet active
				if (CurrentBar < low.ActivationBar)
					continue;

				// Calculate zone boundaries
				double zoneTop = low.Price + ZoneBelowPoints;
				double zoneBottom = low.Price - ZoneAbovePoints;

				// Set HasLeftZone flag if price has moved UP from the zone (High > zoneTop)
				// This means price has left the zone in the direction that would allow a return
				if (High[0] > zoneTop && !low.HasLeftZone)
				{
					low.HasLeftZone = true;
					low.LeftZoneBar = CurrentBar;
					Print($"[LOW ZONE LEFT B{CurrentBar} P{low.Price:F2} High{High[0]:F2}]");
				}

				// Invalidate if price has moved below the zone (passed through without reversal)
				// But NOT in the same bar where HasLeftZone was set
				if (Low[0] < zoneBottom && low.LeftZoneBar != CurrentBar)
				{
					zigZagLows.Remove(low);
					Print($"[LOW ZONE INVALIDATED B{CurrentBar} P{low.Price:F2} Low{Low[0]:F2}]");
				}
			}
		}

		private void UpdateSupportResistanceLines()
		{
			// Extend active support lines (ZigZag highs) to current bar
			foreach (ZigZagLevel high in zigZagHighs.ToList())
			{
				if (!high.IsLineActive)
					continue; // Already broken

				// Check if price broke above the support level
				if (High[0] > high.Price)
				{
					// Support line is broken - redraw line to end at current bar
					high.IsLineActive = false;
					high.LineEndBar = CurrentBar;

					int startBarsAgo = CurrentBar - high.DetectedBar;
					int endBarsAgo = 0; // Current bar
					Draw.Line(this, $"SupportLine_{high.DetectedBar}", startBarsAgo, high.Price, endBarsAgo, high.Price, Brushes.Green);

					Print($"[SUPPORT BROKEN B{CurrentBar} P{high.Price:F2} High{High[0]:F2}]");
				}
				else
				{
					// Support line still active - extend to current bar
					int startBarsAgo = CurrentBar - high.DetectedBar;
					int endBarsAgo = 0; // Current bar
					Draw.Line(this, $"SupportLine_{high.DetectedBar}", startBarsAgo, high.Price, endBarsAgo, high.Price, Brushes.Green);
				}
			}

			// Extend active resistance lines (ZigZag lows) to current bar
			foreach (ZigZagLevel low in zigZagLows.ToList())
			{
				if (!low.IsLineActive)
					continue; // Already broken

				// Check if price broke below the resistance level
				if (Low[0] < low.Price)
				{
					// Resistance line is broken - redraw line to end at current bar
					low.IsLineActive = false;
					low.LineEndBar = CurrentBar;

					int startBarsAgo = CurrentBar - low.DetectedBar;
					int endBarsAgo = 0; // Current bar
					Draw.Line(this, $"ResistanceLine_{low.DetectedBar}", startBarsAgo, low.Price, endBarsAgo, low.Price, Brushes.Red);

					Print($"[RESISTANCE BROKEN B{CurrentBar} P{low.Price:F2} Low{Low[0]:F2}]");
				}
				else
				{
					// Resistance line still active - extend to current bar
					int startBarsAgo = CurrentBar - low.DetectedBar;
					int endBarsAgo = 0; // Current bar
					Draw.Line(this, $"ResistanceLine_{low.DetectedBar}", startBarsAgo, low.Price, endBarsAgo, low.Price, Brushes.Red);
				}
			}
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

		[Range(0, double.MaxValue), NinjaScriptProperty]
		[Display(Name = "BreakevenPoints", Order = 7, GroupName = "Risk Management")]
		public double BreakevenPoints { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "TradingStartTime", Order = 8, GroupName = "Time Filter")]
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
