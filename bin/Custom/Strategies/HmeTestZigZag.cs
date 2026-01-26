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
		// Entry mode selection
		public enum EntryMode { BarReversal, LimitEntry }
		
		// Secondary data series type selection
		public enum SecondarySeriesType { Tick, Second }
		
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

		// ATM Strategy variables
		private string atmStrategyId = string.Empty;
		private string orderId = string.Empty;
		private bool isAtmStrategyCreated = false;

		// Breakeven tracking
		private bool breakevenSet = false;

		// Trailing stop tracking
		private bool trailingActive = false;
		private double nextTrailingTrigger = 0;

		// Unmanaged order tracking
		private Order entryOrder = null;
		private Order stopOrder = null;
		private Order targetOrder = null;
		
		// Limit order tracking (for LimitEntry mode)
		private Order pendingLimitOrderLong = null;
		private Order pendingLimitOrderShort = null;
		private double pendingLimitPriceLong = 0;
		private double pendingLimitPriceShort = 0;

		// Daily max loss tracking
		private double dailyPnLPoints = 0;
		private DateTime currentTradingDay = DateTime.MinValue;
		private bool tradingHaltedToday = false;
		private double lastEntryPrice = 0;

		// Weekly max loss tracking
		private double weeklyPnLPoints = 0;
		private DateTime currentTradingWeek = DateTime.MinValue;
		private bool tradingHaltedThisWeek = false;

		// Performance measurement
		private System.Diagnostics.Stopwatch performanceTimer;

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

				// Entry mode
				EntryModeSelected = EntryMode.BarReversal;  // Default: Bar Reversal mode
				SecondaryDataSeriesType = SecondarySeriesType.Second;  // Default: 1 Second bars

				// ZigZag parameters
				DeviationValue = 60.0;  // Default 60 points
				UseHighLow = true;

				// Proximity zone parameters
				ZoneAbovePoints = 2.0;
				ZoneBelowPoints = 2.0;

				// Risk management in points
				StopLossPoints = 10.0;
				ProfitTargetPoints = 15.0;
				BreakevenPoints = 20.0;  // Move stop to breakeven when 20 points in profit
				TrailingStopPoints = 0.0;  // 0 = disabled, >0 = trail stop every X points
				DailyMaxLossPoints = 0.0;  // 0 = disabled, >0 = max cumulative loss in points per day
				WeeklyMaxLossPoints = 0.0;  // 0 = disabled, >0 = max cumulative loss in points per week

				// Time filter
				TradingStartTime = TimeSpan.Parse("00:00");
				TradingEndTime = TimeSpan.Parse("23:59");

				// ATM Strategy (leave empty for standard mode)
				AtmTemplateName = string.Empty;

				// Debug settings
				EnableLogging = false;  // Default: OFF for better performance

				// Add strategy-owned plots for ZigZag visualization (works in Strategy Analyzer)
				AddPlot(Brushes.Red, "ZigZagHighs");
				AddPlot(Brushes.Blue, "ZigZagLows");

				// This strategy has been designed to take advantage of performance gains in Strategy Analyzer optimizations
				IsInstantiatedOnEachOptimizationIteration = true;
			}
			else if (State == State.Configure)
			{
				// Add secondary data series for precise breakeven management
				// Type is configurable: Tick or Second
				if (SecondaryDataSeriesType == SecondarySeriesType.Tick)
					AddDataSeries(BarsPeriodType.Tick, 1);
				else
					AddDataSeries(BarsPeriodType.Second, 1);
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
				atmStrategyId = string.Empty;
				orderId = string.Empty;
				isAtmStrategyCreated = false;

				// Always use OnBarClose for performance with Range Bars
				Calculate = Calculate.OnBarClose;

				// Add original ZigZag indicator for visual comparison (NOT used in trading logic!)
				ZigZag displayZigZag = ZigZag(DeviationType.Points, DeviationValue, UseHighLow);
				AddChartIndicator(displayZigZag);

				// Print strategy parameters at start
				MyPrint($"[START] Deviation={DeviationValue:F1} ZoneAbove={ZoneAbovePoints:F1} ZoneBelow={ZoneBelowPoints:F1} SL={StopLossPoints:F1} PT={ProfitTargetPoints:F1}");
				
				// Start performance timer
				performanceTimer = System.Diagnostics.Stopwatch.StartNew();
				//Print($"[PERFORMANCE] Strategy started at {DateTime.Now:HH:mm:ss.fff}");
			}
			else if (State == State.Terminated)
			{
				// Stop performance timer and output execution time
				if (performanceTimer != null)
				{
					performanceTimer.Stop();
					double elapsedSeconds = performanceTimer.Elapsed.TotalSeconds;
					Print($"[PERFORMANCE] Strategy execution time: {elapsedSeconds:F3} sec");
				}
			}
		}

		protected override void OnBarUpdate()
		{
			// Handle different data series
			if (BarsInProgress == 0) // Primary series (Range bars) - ZigZag logic
			{
				if (CurrentBar < BarsRequiredToTrade)
					return;

				// Daily max loss reset - check for new trading day
				DateTime barTime = Time[0];
				bool isNewTradingDay = false;
				
				if (currentTradingDay == DateTime.MinValue || barTime.Date > currentTradingDay.Date)
				{
					isNewTradingDay = true;
					
					// New day detected - add previous day's P&L to weekly total BEFORE resetting
					if (currentTradingDay != DateTime.MinValue && WeeklyMaxLossPoints > 0)
					{
						weeklyPnLPoints += dailyPnLPoints;
						MyPrint($"[DAILY RESET] New day: {barTime.Date:yyyy-MM-dd} | Previous Day P&L: {dailyPnLPoints:F2} pts | Weekly Total: {weeklyPnLPoints:F2} pts");
						
						// Check if weekly max loss reached after adding daily P&L
						if (weeklyPnLPoints <= -WeeklyMaxLossPoints)
						{
							tradingHaltedThisWeek = true;
							MyPrint($"[TRADING HALTED] Weekly max loss reached! Weekly P&L:{weeklyPnLPoints:F2} pts");
						}
					}
					else if (currentTradingDay != DateTime.MinValue)
					{
						MyPrint($"[DAILY RESET] New day: {barTime.Date:yyyy-MM-dd} | Previous Day P&L: {dailyPnLPoints:F2} pts");
					}
					
					currentTradingDay = barTime.Date;
					dailyPnLPoints = 0;
					tradingHaltedToday = false;
					
					// Cancel limit orders from previous day (they expire with TimeInForce.Day anyway)
					// This ensures clean state for new trading day
					if (EntryModeSelected == EntryMode.LimitEntry && Position.MarketPosition == MarketPosition.Flat)
					{
						CancelAllPendingLimitOrders();
						MyPrint($"[NEW DAY] Limit orders cancelled for new trading day");
					}
				}

				// Weekly max loss reset - check for new trading week (Monday)
				if (WeeklyMaxLossPoints > 0)
				{
					// barTime already declared above, reuse it
					
					// Get start of week (Monday) for current bar
					int daysToMonday = ((int)barTime.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
					DateTime weekStart = barTime.Date.AddDays(-daysToMonday);
					
					// Check if this is a new trading week
					if (currentTradingWeek == DateTime.MinValue || weekStart > currentTradingWeek)
					{
						// New week detected - reset weekly tracking
						if (currentTradingWeek != DateTime.MinValue)
						{
							MyPrint($"[WEEKLY RESET] New week: {weekStart:yyyy-MM-dd} | Previous P&L: {weeklyPnLPoints:F2} pts");
						}
						
						currentTradingWeek = weekStart;
						weeklyPnLPoints = 0;
						tradingHaltedThisWeek = false;
					}
				}

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
					MyPrint($"[TREND INIT {currentTrend} B{CurrentBar} Extremum{currentExtremum:F2}]");
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
							MyPrint($"[HIGH CONFIRMED B{CurrentBar} P{currentExtremum:F2} PeakB{extremumBar}]");

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
							MyPrint($"[LOW CONFIRMED B{CurrentBar} P{currentExtremum:F2} PeakB{extremumBar}]");

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

				// Invalidate zones that have been passed through without reversal (AFTER checking for orders)
				InvalidateZones();

				// Check for broken support/resistance lines and update them
				UpdateSupportResistanceLines();
			}
			else if (BarsInProgress == 1) // Secondary series (Tick) - Precise order management
			{
				// Ensure primary series has enough bars before accessing its data
				if (CurrentBars[0] < BarsRequiredToTrade)
					return;

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
					MyPrint("[RESET] Orders cleared - Position flat");
				}
				// Place stop/target orders after entry fill (tick-precise)
				else if (Position.MarketPosition == MarketPosition.Long && stopOrder == null)
				{
					double stopPriceLevel = Position.AveragePrice - StopLossPoints;
					double targetPriceLevel = Position.AveragePrice + ProfitTargetPoints;

					stopOrder = SubmitOrderUnmanaged(1, OrderAction.Sell, OrderType.StopMarket, Position.Quantity, 0, stopPriceLevel, "StopLoss", "");
					targetOrder = SubmitOrderUnmanaged(1, OrderAction.Sell, OrderType.Limit, Position.Quantity, targetPriceLevel, 0, "ProfitTarget", "");

					MyPrint($"[ORDERS PLACED LONG TICK] Stop:{stopPriceLevel:F2} Target:{targetPriceLevel:F2}");
				}
				else if (Position.MarketPosition == MarketPosition.Short && stopOrder == null)
				{
					double stopPriceLevel = Position.AveragePrice + StopLossPoints;
					double targetPriceLevel = Position.AveragePrice - ProfitTargetPoints;

					stopOrder = SubmitOrderUnmanaged(1, OrderAction.BuyToCover, OrderType.StopMarket, Position.Quantity, 0, stopPriceLevel, "StopLoss", "");
					targetOrder = SubmitOrderUnmanaged(1, OrderAction.BuyToCover, OrderType.Limit, Position.Quantity, targetPriceLevel, 0, "ProfitTarget", "");

					MyPrint($"[ORDERS PLACED SHORT TICK] Stop:{stopPriceLevel:F2} Target:{targetPriceLevel:F2}");
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
							MyPrint($"[BREAKEVEN LONG] Stop moved to {breakevenPrice:F2} (Entry: {Position.AveragePrice:F2})");
							
							// Initialize trailing stop after breakeven is triggered
							if (TrailingStopPoints > 0 && !trailingActive)
							{
								trailingActive = true;
								nextTrailingTrigger = Closes[1][0] + TrailingStopPoints;
								MyPrint($"[TRAILING INITIALIZED LONG] Next trigger: {nextTrailingTrigger:F2}");
							}
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
							MyPrint($"[BREAKEVEN SHORT] Stop moved to {breakevenPrice:F2} (Entry: {Position.AveragePrice:F2})");
							
							// Initialize trailing stop after breakeven is triggered
							if (TrailingStopPoints > 0 && !trailingActive)
							{
								trailingActive = true;
								nextTrailingTrigger = Closes[1][0] - TrailingStopPoints;
								MyPrint($"[TRAILING INITIALIZED SHORT] Next trigger: {nextTrailingTrigger:F2}");
							}
						}
					}
					else if (Position.MarketPosition == MarketPosition.Flat)
					{
						// Reset breakeven flag when position is closed
						breakevenSet = false;
					}
				}

				// Trailing stop management on tick series (when TrailingStopPoints > 0)
				if (TrailingStopPoints > 0 && stopOrder != null)
				{
					// For Long positions
					if (Position.MarketPosition == MarketPosition.Long)
					{
						// If breakeven is disabled (0), initialize trailing immediately
						if (BreakevenPoints == 0 && !trailingActive)
						{
							trailingActive = true;
							nextTrailingTrigger = Position.AveragePrice + TrailingStopPoints;
							MyPrint($"[TRAILING INITIALIZED LONG] Next trigger: {nextTrailingTrigger:F2} (No Breakeven)");
						}
						
						// Check if price reached next trailing trigger
						if (trailingActive && Closes[1][0] >= nextTrailingTrigger)
						{
							// Get current stop price from the order
							double currentStopPrice = stopOrder.StopPrice;
							
							// Calculate new stop price (add TrailingStopPoints)
							double newStopPrice = currentStopPrice + TrailingStopPoints;
							
							// Modify stop order
							ChangeOrder(stopOrder, Position.Quantity, 0, newStopPrice);
							
							// Update next trigger level
							nextTrailingTrigger = Closes[1][0] + TrailingStopPoints;
							
							MyPrint($"[TRAILING LONG] Stop: {currentStopPrice:F2} -> {newStopPrice:F2} | Next trigger: {nextTrailingTrigger:F2}");
						}
					}
					// For Short positions
					else if (Position.MarketPosition == MarketPosition.Short)
					{
						// If breakeven is disabled (0), initialize trailing immediately
						if (BreakevenPoints == 0 && !trailingActive)
						{
							trailingActive = true;
							nextTrailingTrigger = Position.AveragePrice - TrailingStopPoints;
							MyPrint($"[TRAILING INITIALIZED SHORT] Next trigger: {nextTrailingTrigger:F2} (No Breakeven)");
						}
						
						// Check if price reached next trailing trigger
						if (trailingActive && Closes[1][0] <= nextTrailingTrigger)
						{
							// Get current stop price from the order
							double currentStopPrice = stopOrder.StopPrice;
							
							// Calculate new stop price (subtract TrailingStopPoints)
							double newStopPrice = currentStopPrice - TrailingStopPoints;
							
							// Modify stop order
							ChangeOrder(stopOrder, Position.Quantity, 0, newStopPrice);
							
							// Update next trigger level
							nextTrailingTrigger = Closes[1][0] - TrailingStopPoints;
							
							MyPrint($"[TRAILING SHORT] Stop: {currentStopPrice:F2} -> {newStopPrice:F2} | Next trigger: {nextTrailingTrigger:F2}");
						}
					}
					else if (Position.MarketPosition == MarketPosition.Flat)
					{
						// Reset trailing flag when position is closed
						trailingActive = false;
						nextTrailingTrigger = 0;
					}
				}
			}
		}


		protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
		{
			// Track order state changes for limit entry orders (LimitEntry mode)
			if (order == pendingLimitOrderLong || order == pendingLimitOrderShort)
			{
				if (orderState == OrderState.Filled)
				{
					// IMPORTANT: Save ZigZag price BEFORE cancelling orders (which resets prices to 0)
					double filledZigZagPrice = 0;
					bool isLongFill = (order == pendingLimitOrderLong);
					
					if (isLongFill)
					{
						filledZigZagPrice = pendingLimitPriceLong;
						tradedLows.Add(filledZigZagPrice);
						MyPrint($"[LIMIT LONG FILLED] ZigZag:{filledZigZagPrice:F2} Entry:{averageFillPrice:F2}");
					}
					else // pendingLimitOrderShort
					{
						filledZigZagPrice = pendingLimitPriceShort;
						tradedHighs.Add(filledZigZagPrice);
						MyPrint($"[LIMIT SHORT FILLED] ZigZag:{filledZigZagPrice:F2} Entry:{averageFillPrice:F2}");
					}
					
					// NOW cancel other pending limit orders (this will reset prices to 0)
					CancelAllPendingLimitOrders();
					
					// Reset the filled order references
					if (isLongFill)
					{
						pendingLimitOrderLong = null;
						pendingLimitPriceLong = 0;
					}
					else
					{
						pendingLimitOrderShort = null;
						pendingLimitPriceShort = 0;
					}
					
					// Store entry price for P&L calculation
					lastEntryPrice = Position.AveragePrice;
					// Stop/Target orders will be placed in OnBarUpdate on tick series
				}
				else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
				{
					// Reset cancelled/rejected limit order
					if (order == pendingLimitOrderLong)
					{
						MyPrint($"[LIMIT LONG CANCELLED/REJECTED] ZigZag:{pendingLimitPriceLong:F2}");
						pendingLimitOrderLong = null;
						pendingLimitPriceLong = 0;
					}
					else // pendingLimitOrderShort
					{
						MyPrint($"[LIMIT SHORT CANCELLED/REJECTED] ZigZag:{pendingLimitPriceShort:F2}");
						pendingLimitOrderShort = null;
						pendingLimitPriceShort = 0;
					}
				}
			}
			// Track order state changes for market entry orders (BarReversal mode)
			else if (order == entryOrder)
			{
				if (orderState == OrderState.Filled)
				{
					// Store entry price for P&L calculation
					lastEntryPrice = Position.AveragePrice;
					entryOrder = null; // Reset entry order reference
					MyPrint($"[ENTRY FILLED] Position:{Position.MarketPosition} Entry:{Position.AveragePrice:F2}");
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
					// Calculate P&L in points for daily tracking
					if ((DailyMaxLossPoints > 0 || WeeklyMaxLossPoints > 0) && lastEntryPrice > 0)
					{
						double pnlPoints = 0;
						
						// Calculate P&L based on position direction
						if (order.OrderAction == OrderAction.Sell || order.OrderAction == OrderAction.SellShort)
						{
							// Exiting long position
							pnlPoints = averageFillPrice - lastEntryPrice;
						}
						else // Buy or BuyToCover
						{
							// Exiting short position
							pnlPoints = lastEntryPrice - averageFillPrice;
						}
						
						// Update cumulative daily P&L (weekly is calculated from daily totals)
						dailyPnLPoints += pnlPoints;
						MyPrint($"[DAILY P&L] Trade:{pnlPoints:F2} pts | Daily Total:{dailyPnLPoints:F2} pts | Weekly Total:{weeklyPnLPoints:F2} pts");
						
						// Check if daily max loss reached
						if (DailyMaxLossPoints > 0 && dailyPnLPoints <= -DailyMaxLossPoints)
						{
							tradingHaltedToday = true;
							MyPrint($"[TRADING HALTED] Daily max loss reached! Daily P&L:{dailyPnLPoints:F2} pts");
						}
					}
					
					// Cancel remaining orders
					if (stopOrder != null && stopOrder != order) CancelOrder(stopOrder);
					if (targetOrder != null && targetOrder != order) CancelOrder(targetOrder);

					stopOrder = null;
					targetOrder = null;
					breakevenSet = false; // Reset for next trade
					trailingActive = false; // Reset trailing for next trade
					nextTrailingTrigger = 0;
					lastEntryPrice = 0; // Reset entry price
					MyPrint($"[EXIT FILLED] Order:{order.Name} AvgFillPrice:{averageFillPrice:F2}");
				}
				// Handle cancelled/rejected orders (e.g., Exit on Session)
				else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
				{
					if (order == stopOrder)
					{
						stopOrder = null;
						MyPrint($"[STOP CANCELLED] Order:{order.Name}");
					}
					if (order == targetOrder)
					{
						targetOrder = null;
						MyPrint($"[TARGET CANCELLED] Order:{order.Name}");
					}
					
					// Reset breakeven flag when both orders are cleared
					if (stopOrder == null && targetOrder == null)
					{
						breakevenSet = false;
						MyPrint($"[ORDERS RESET] All exit orders cleared");
					}
				}
			}
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

			// Daily/Weekly max loss check - halt trading if limit reached
			if ((DailyMaxLossPoints > 0 && tradingHaltedToday) || 
			    (WeeklyMaxLossPoints > 0 && tradingHaltedThisWeek))
			{
				return; // Trading halted
			}

			// Check entry mode
			if (EntryModeSelected == EntryMode.LimitEntry)
			{
				// Limit Entry Mode: Place/update limit orders at next ZigZag points
				// Place Long and Short orders independently
				PlaceOrUpdateLimitLong();
				PlaceOrUpdateLimitShort();
			}
			else // BarReversal mode
			{
				// Bar Reversal Mode: Original behavior
				// Check ALL ZigZag high points (potential short entries)
				CheckZigZagHighs();

				// Check ALL ZigZag low points (potential long entries)
				CheckZigZagLows();

				// Handle ATM strategy if configured and running live
				if (!string.IsNullOrEmpty(AtmTemplateName) && State != State.Historical)
					HandleAtmStrategy();
			}
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

				if (High[0] >= zoneBottom && High[0] <= zoneTop && Close[0] < Open[0])
				{
					PlaceShortOrder(high.Price);
					tradedHighs.Add(high.Price);
					break; // Only one order at a time
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

				if (Low[0] >= zoneBottom && Low[0] <= zoneTop && Close[0] > Open[0])
				{
					PlaceLongOrder(low.Price);
					tradedLows.Add(low.Price);
					break; // Only one order at a time
				}
			}
		}

		private void PlaceShortOrder(double zigZagHigh)
		{
			if (string.IsNullOrEmpty(AtmTemplateName) || State == State.Historical)
			{
				// Unmanaged mode - submit entry order on primary series (index 0)
				entryOrder = SubmitOrderUnmanaged(0, OrderAction.SellShort, OrderType.Market, 1, Close[0], 0, "ZigZagShort", "");
				MyPrint($"[ENTRY SHORT SUBMITTED B{CurrentBar} Price{Close[0]:F2}]");
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
				// Unmanaged mode - submit entry order on primary series (index 0)
				entryOrder = SubmitOrderUnmanaged(0, OrderAction.Buy, OrderType.Market, 1, Close[0], 0, "ZigZagLong", "");
				MyPrint($"[ENTRY LONG SUBMITTED B{CurrentBar} Price{Close[0]:F2}]");
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
						MyPrint("ATM Strategy created successfully: " + atmStrategyId);
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
					MyPrint($"[HIGH ZONE LEFT B{CurrentBar} P{high.Price:F2} Low{Low[0]:F2}]");
				}

				// Invalidate if price has moved above the zone (passed through without reversal)
				// But NOT in the same bar where HasLeftZone was set
				if (High[0] > zoneTop && high.LeftZoneBar != CurrentBar)
				{
					// Cancel pending limit order for this zone (LimitEntry mode)
					if (EntryModeSelected == EntryMode.LimitEntry && pendingLimitOrderShort != null && pendingLimitPriceShort == high.Price)
					{
						CancelOrder(pendingLimitOrderShort);
						MyPrint($"[LIMIT SHORT CANCELLED - ZONE INVALIDATED] ZigZag:{pendingLimitPriceShort:F2}");
						pendingLimitOrderShort = null;
						pendingLimitPriceShort = 0;
					}
					
					zigZagHighs.Remove(high);
					MyPrint($"[HIGH ZONE INVALIDATED B{CurrentBar} P{high.Price:F2} High{High[0]:F2}]");
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
					MyPrint($"[LOW ZONE LEFT B{CurrentBar} P{low.Price:F2} High{High[0]:F2}]");
				}

				// Invalidate if price has moved below the zone (passed through without reversal)
				// But NOT in the same bar where HasLeftZone was set
				if (Low[0] < zoneBottom && low.LeftZoneBar != CurrentBar)
				{
					// Cancel pending limit order for this zone (LimitEntry mode)
					if (EntryModeSelected == EntryMode.LimitEntry && pendingLimitOrderLong != null && pendingLimitPriceLong == low.Price)
					{
						CancelOrder(pendingLimitOrderLong);
						MyPrint($"[LIMIT LONG CANCELLED - ZONE INVALIDATED] ZigZag:{pendingLimitPriceLong:F2}");
						pendingLimitOrderLong = null;
						pendingLimitPriceLong = 0;
					}
					
					zigZagLows.Remove(low);
					MyPrint($"[LOW ZONE INVALIDATED B{CurrentBar} P{low.Price:F2} Low{Low[0]:F2}]");
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

					MyPrint($"[SUPPORT BROKEN B{CurrentBar} P{high.Price:F2} High{High[0]:F2}]");
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

					MyPrint($"[RESISTANCE BROKEN B{CurrentBar} P{low.Price:F2} Low{Low[0]:F2}]");
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

		private void PlaceLimitOrdersPair()
		{
			double currentPrice = Close[0];
			double minDistance = double.MaxValue;
			
			// Find CLOSEST untraded ZigZag high (for short entry)
			ZigZagLevel nextHigh = null;
			foreach (ZigZagLevel high in zigZagHighs)
			{
				bool isTraded = IsAlreadyTraded(high.Price, tradedHighs);
				bool hasPending = IsSamePrice(pendingLimitPriceShort, high.Price);
				double distance = Math.Abs(high.Price - currentPrice);
				
				// Check: not traded and not already having a pending order at this price
				// NOTE: No ActivationBar check for LimitEntry - purely price-based, bar-independent
				if (!isTraded && !hasPending)
				{
					if (distance < minDistance)
					{
						minDistance = distance;
						nextHigh = high;
					}
				}
			}

			// Find CLOSEST untraded ZigZag low (for long entry)
			minDistance = double.MaxValue;
			ZigZagLevel nextLow = null;
			foreach (ZigZagLevel low in zigZagLows)
			{
				bool isTraded = IsAlreadyTraded(low.Price, tradedLows);
				bool hasPending = IsSamePrice(pendingLimitPriceLong, low.Price);
				double distance = Math.Abs(low.Price - currentPrice);
				
				// Check: not traded and not already having a pending order at this price
				// NOTE: No ActivationBar check for LimitEntry - purely price-based, bar-independent
				if (!isTraded && !hasPending)
				{
					if (distance < minDistance)
					{
						minDistance = distance;
						nextLow = low;
					}
				}
			}

			// Place limit order for short entry at next high
			if (nextHigh != null)
			{
				// Limit price = ZigZag High - ZoneBelowPoints
				double limitPrice = nextHigh.Price - ZoneBelowPoints;
				
				// IMPORTANT: Only place order if price is BELOW limit (hasn't reached it yet)
				// Sell Limit must be ABOVE current price, otherwise it would fill immediately
				if (limitPrice > currentPrice)
				{
					pendingLimitOrderShort = SubmitOrderUnmanaged(1, OrderAction.SellShort, OrderType.Limit, 1, limitPrice, 0, "ZigZagLimitShort", "");
					pendingLimitPriceShort = nextHigh.Price;
					MyPrint($"[LIMIT SHORT PLACED] ZigZag:{nextHigh.Price:F2} LimitPrice:{limitPrice:F2} Distance:{Math.Abs(nextHigh.Price - currentPrice):F2}");
				}
			}

			// Place limit order for long entry at next low
			if (nextLow != null)
			{
				// Limit price = ZigZag Low + ZoneBelowPoints
				double limitPrice = nextLow.Price + ZoneBelowPoints;
				
				// IMPORTANT: Only place order if price is ABOVE limit (hasn't reached it yet)
				// Buy Limit must be BELOW current price, otherwise it would fill immediately
				if (limitPrice < currentPrice)
				{
					pendingLimitOrderLong = SubmitOrderUnmanaged(1, OrderAction.Buy, OrderType.Limit, 1, limitPrice, 0, "ZigZagLimitLong", "");
					pendingLimitPriceLong = nextLow.Price;
					MyPrint($"[LIMIT LONG PLACED] ZigZag:{nextLow.Price:F2} LimitPrice:{limitPrice:F2} Distance:{Math.Abs(nextLow.Price - currentPrice):F2}");
				}
			}
		}

		private void PlaceOrUpdateLimitLong()
		{
			double currentPrice = Close[0];
			double minDistance = double.MaxValue;
			
			// Find CLOSEST untraded ZigZag low (for long entry)
			ZigZagLevel nextLow = null;
			foreach (ZigZagLevel low in zigZagLows)
			{
				bool isTraded = IsAlreadyTraded(low.Price, tradedLows);
				double distance = Math.Abs(low.Price - currentPrice);
				
				if (!isTraded && distance < minDistance)
				{
					minDistance = distance;
					nextLow = low;
				}
			}

			// Check if we need to place or update the order
			if (nextLow != null)
			{
				double limitPrice = nextLow.Price + ZoneBelowPoints;
				
				// Only place order if price is ABOVE limit (hasn't reached it yet)
				if (limitPrice < currentPrice)
				{
					// Check if we already have an order at a different ZigZag
					if (pendingLimitOrderLong != null && !IsSamePrice(pendingLimitPriceLong, nextLow.Price))
					{
						// New ZigZag is closer - cancel old order
						CancelOrder(pendingLimitOrderLong);
						MyPrint($"[LIMIT LONG CANCELLED - CLOSER ZIGZAG] Old:{pendingLimitPriceLong:F2} New:{nextLow.Price:F2}");
						pendingLimitOrderLong = null;
						pendingLimitPriceLong = 0;
					}
					
					// Place new order if we don't have one
					if (pendingLimitOrderLong == null)
					{
						pendingLimitOrderLong = SubmitOrderUnmanaged(1, OrderAction.Buy, OrderType.Limit, 1, limitPrice, 0, "ZigZagLimitLong", "");
						pendingLimitPriceLong = nextLow.Price;
						MyPrint($"[LIMIT LONG PLACED] ZigZag:{nextLow.Price:F2} LimitPrice:{limitPrice:F2} Distance:{minDistance:F2}");
					}
				}
			}
		}

		private void PlaceOrUpdateLimitShort()
		{
			double currentPrice = Close[0];
			double minDistance = double.MaxValue;
			
			// Find CLOSEST untraded ZigZag high (for short entry)
			ZigZagLevel nextHigh = null;
			foreach (ZigZagLevel high in zigZagHighs)
			{
				bool isTraded = IsAlreadyTraded(high.Price, tradedHighs);
				double distance = Math.Abs(high.Price - currentPrice);
				
				if (!isTraded && distance < minDistance)
				{
					minDistance = distance;
					nextHigh = high;
				}
			}

			// Check if we need to place or update the order
			if (nextHigh != null)
			{
				double limitPrice = nextHigh.Price - ZoneBelowPoints;
				
				// Only place order if price is BELOW limit (hasn't reached it yet)
				if (limitPrice > currentPrice)
				{
					// Check if we already have an order at a different ZigZag
					if (pendingLimitOrderShort != null && !IsSamePrice(pendingLimitPriceShort, nextHigh.Price))
					{
						// New ZigZag is closer - cancel old order
						CancelOrder(pendingLimitOrderShort);
						MyPrint($"[LIMIT SHORT CANCELLED - CLOSER ZIGZAG] Old:{pendingLimitPriceShort:F2} New:{nextHigh.Price:F2}");
						pendingLimitOrderShort = null;
						pendingLimitPriceShort = 0;
					}
					
					// Place new order if we don't have one
					if (pendingLimitOrderShort == null)
					{
						pendingLimitOrderShort = SubmitOrderUnmanaged(1, OrderAction.SellShort, OrderType.Limit, 1, limitPrice, 0, "ZigZagLimitShort", "");
						pendingLimitPriceShort = nextHigh.Price;
						MyPrint($"[LIMIT SHORT PLACED] ZigZag:{nextHigh.Price:F2} LimitPrice:{limitPrice:F2} Distance:{minDistance:F2}");
					}
				}
			}
		}

		private void CancelAllPendingLimitOrders()
		{
			if (pendingLimitOrderLong != null)
			{
				CancelOrder(pendingLimitOrderLong);
				MyPrint($"[LIMIT LONG CANCELLED] ZigZag:{pendingLimitPriceLong:F2}");
				pendingLimitOrderLong = null;
				pendingLimitPriceLong = 0;
			}

			if (pendingLimitOrderShort != null)
			{
				CancelOrder(pendingLimitOrderShort);
				MyPrint($"[LIMIT SHORT CANCELLED] ZigZag:{pendingLimitPriceShort:F2}");
				pendingLimitOrderShort = null;
				pendingLimitPriceShort = 0;
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

		// Helper method: Check if price is already traded (with tolerance for floating point comparison)
		private bool IsAlreadyTraded(double price, HashSet<double> tradedSet)
		{
			const double tolerance = 0.01; // 1 cent tolerance
			foreach (double tradedPrice in tradedSet)
			{
				if (Math.Abs(price - tradedPrice) < tolerance)
					return true;
			}
			return false;
		}

		// Helper method: Check if two prices are the same (with tolerance for floating point comparison)
		private bool IsSamePrice(double price1, double price2)
		{
			const double tolerance = 0.01; // 1 cent tolerance
			return Math.Abs(price1 - price2) < tolerance;
		}

		// Custom Print method with logging control
		private void MyPrint(string message)
		{
			if (EnableLogging)
				Print(message);
		}


		#region Properties

		[XmlIgnore]
		[NinjaScriptProperty]
		[Display(Name = "Entry Mode", Description = "BarReversal: Market order on bar reversal | LimitEntry: Limit orders at ZigZag points", Order = 0, GroupName = "Entry Settings")]
		public EntryMode EntryModeSelected { get; set; }

		[Browsable(false)]
		public string EntryModeSelectedSerialize
		{
			get { return EntryModeSelected.ToString(); }
			set { EntryModeSelected = (EntryMode)Enum.Parse(typeof(EntryMode), value); }
		}

		[XmlIgnore]
		[NinjaScriptProperty]
		[Display(Name = "Secondary Series Type", Description = "Tick: 1 Tick bars | Second: 1 Second bars", Order = 1, GroupName = "Entry Settings")]
		public SecondarySeriesType SecondaryDataSeriesType { get; set; }

		[Browsable(false)]
		public string SecondaryDataSeriesTypeSerialize
		{
			get { return SecondaryDataSeriesType.ToString(); }
			set { SecondaryDataSeriesType = (SecondarySeriesType)Enum.Parse(typeof(SecondarySeriesType), value); }
		}

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

		[Range(0, double.MaxValue), NinjaScriptProperty]
		[Display(Name = "TrailingStopPoints", Order = 8, GroupName = "Risk Management")]
		public double TrailingStopPoints { get; set; }

		[Range(0, double.MaxValue), NinjaScriptProperty]
		[Display(Name = "DailyMaxLossPoints", Order = 9, GroupName = "Risk Management")]
		public double DailyMaxLossPoints { get; set; }

		[Range(0, double.MaxValue), NinjaScriptProperty]
		[Display(Name = "WeeklyMaxLossPoints", Order = 10, GroupName = "Risk Management")]
		public double WeeklyMaxLossPoints { get; set; }

		[XmlIgnore]
		[NinjaScriptProperty]
		[Display(Name = "TradingStartTime", Order = 11, GroupName = "Time Filter")]
		public TimeSpan TradingStartTime { get; set; }

		[Browsable(false)]
		public string TradingStartTimeSerialize
		{
			get { return TradingStartTime.ToString(); }
			set { TradingStartTime = TimeSpan.Parse(value); }
		}

		[XmlIgnore]
		[NinjaScriptProperty]
		[Display(Name = "TradingEndTime", Order = 12, GroupName = "Time Filter")]
		public TimeSpan TradingEndTime { get; set; }

		[Browsable(false)]
		public string TradingEndTimeSerialize
		{
			get { return TradingEndTime.ToString(); }
			set { TradingEndTime = TimeSpan.Parse(value); }
		}

		[NinjaScriptProperty]
		[Display(Name = "AtmTemplateName", Order = 13, GroupName = "ATM Strategy")]
		public string AtmTemplateName { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable Logging", Order = 14, GroupName = "Debug")]
		public bool EnableLogging { get; set; }

		#endregion
	}
}
