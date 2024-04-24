USE [SPOInsightsDemo]
GO
CREATE NONCLUSTERED INDEX [NIX_DimDate_Date]
ON [dbo].[DimDate] ([Date])
INCLUDE ([WeekDayName],[WeekOfYear],[Month],[MonthName],[Quarter],[Year],[MonthYear])
GO

