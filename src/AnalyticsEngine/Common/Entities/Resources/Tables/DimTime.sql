/****** Object:  Table [dbo].[DimTime]    Script Date: 2020-01-19 18:07:08 ******/

IF OBJECT_ID('dbo.dimTime','u') IS NOT NULL
BEGIN
DROP TABLE [dbo].[dimTime]
END

GO
/****** Object:  Table [dbo].[DimTime]    Script Date: 2020-01-19 18:07:08 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[DimTime](
	[Time] [varchar](50) NOT NULL,
	[Hour] [varchar](50) NOT NULL,
	[Quarter Hour] [varchar](50) NOT NULL,
	[Minute] [int] NOT NULL,
	[Hour Number] [int] NOT NULL,
	[Next Hour] [varchar](50) NOT NULL,
	[Next Quarter Hour] [varchar](50) NOT NULL,
	[Period of Day] [varchar](50) NOT NULL,
	[PeriodSort] [int] NOT NULL,
	[TimeKey] [varchar](50) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[Time] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:00', N'00:00:00', N'00:00:00', 0, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'0')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:01', N'00:00:00', N'00:00:00', 1, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'1')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:02', N'00:00:00', N'00:00:00', 2, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'2')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:03', N'00:00:00', N'00:00:00', 3, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'3')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:04', N'00:00:00', N'00:00:00', 4, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'4')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:05', N'00:00:00', N'00:00:00', 5, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'5')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:06', N'00:00:00', N'00:00:00', 6, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'6')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:07', N'00:00:00', N'00:00:00', 7, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'7')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:08', N'00:00:00', N'00:00:00', 8, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'8')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:09', N'00:00:00', N'00:00:00', 9, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'9')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:10', N'00:00:00', N'00:00:00', 10, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'10')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:11', N'00:00:00', N'00:00:00', 11, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'11')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:12', N'00:00:00', N'00:00:00', 12, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'12')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:13', N'00:00:00', N'00:00:00', 13, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'13')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:14', N'00:00:00', N'00:00:00', 14, 0, N'01:00:00', N'00:15:00', N'After Midnight', 0, N'14')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:15', N'00:00:00', N'00:15:00', 15, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'15')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:16', N'00:00:00', N'00:15:00', 16, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'16')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:17', N'00:00:00', N'00:15:00', 17, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'17')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:18', N'00:00:00', N'00:15:00', 18, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'18')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:19', N'00:00:00', N'00:15:00', 19, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'19')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:20', N'00:00:00', N'00:15:00', 20, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'20')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:21', N'00:00:00', N'00:15:00', 21, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'21')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:22', N'00:00:00', N'00:15:00', 22, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'22')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:23', N'00:00:00', N'00:15:00', 23, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'23')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:24', N'00:00:00', N'00:15:00', 24, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'24')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:25', N'00:00:00', N'00:15:00', 25, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'25')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:26', N'00:00:00', N'00:15:00', 26, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'26')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:27', N'00:00:00', N'00:15:00', 27, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'27')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:28', N'00:00:00', N'00:15:00', 28, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'28')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:29', N'00:00:00', N'00:15:00', 29, 0, N'01:00:00', N'00:30:00', N'After Midnight', 0, N'29')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:30', N'00:00:00', N'00:30:00', 30, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'30')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:31', N'00:00:00', N'00:30:00', 31, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'31')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:32', N'00:00:00', N'00:30:00', 32, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'32')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:33', N'00:00:00', N'00:30:00', 33, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'33')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:34', N'00:00:00', N'00:30:00', 34, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'34')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:35', N'00:00:00', N'00:30:00', 35, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'35')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:36', N'00:00:00', N'00:30:00', 36, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'36')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:37', N'00:00:00', N'00:30:00', 37, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'37')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:38', N'00:00:00', N'00:30:00', 38, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'38')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:39', N'00:00:00', N'00:30:00', 39, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'39')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:40', N'00:00:00', N'00:30:00', 40, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'40')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:41', N'00:00:00', N'00:30:00', 41, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'41')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:42', N'00:00:00', N'00:30:00', 42, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'42')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:43', N'00:00:00', N'00:30:00', 43, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'43')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:44', N'00:00:00', N'00:30:00', 44, 0, N'01:00:00', N'00:45:00', N'After Midnight', 0, N'44')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:45', N'00:00:00', N'00:45:00', 45, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'45')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:46', N'00:00:00', N'00:45:00', 46, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'46')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:47', N'00:00:00', N'00:45:00', 47, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'47')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:48', N'00:00:00', N'00:45:00', 48, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'48')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:49', N'00:00:00', N'00:45:00', 49, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'49')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:50', N'00:00:00', N'00:45:00', 50, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'50')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:51', N'00:00:00', N'00:45:00', 51, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'51')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:52', N'00:00:00', N'00:45:00', 52, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'52')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:53', N'00:00:00', N'00:45:00', 53, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'53')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:54', N'00:00:00', N'00:45:00', 54, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'54')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:55', N'00:00:00', N'00:45:00', 55, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'55')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:56', N'00:00:00', N'00:45:00', 56, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'56')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:57', N'00:00:00', N'00:45:00', 57, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'57')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:58', N'00:00:00', N'00:45:00', 58, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'58')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'00:59', N'00:00:00', N'00:45:00', 59, 0, N'01:00:00', N'01:00:00', N'After Midnight', 0, N'59')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:00', N'01:00:00', N'01:00:00', 0, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'100')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:01', N'01:00:00', N'01:00:00', 1, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'101')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:02', N'01:00:00', N'01:00:00', 2, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'102')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:03', N'01:00:00', N'01:00:00', 3, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'103')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:04', N'01:00:00', N'01:00:00', 4, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'104')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:05', N'01:00:00', N'01:00:00', 5, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'105')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:06', N'01:00:00', N'01:00:00', 6, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'106')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:07', N'01:00:00', N'01:00:00', 7, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'107')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:08', N'01:00:00', N'01:00:00', 8, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'108')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:09', N'01:00:00', N'01:00:00', 9, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'109')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:10', N'01:00:00', N'01:00:00', 10, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'110')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:11', N'01:00:00', N'01:00:00', 11, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'111')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:12', N'01:00:00', N'01:00:00', 12, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'112')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:13', N'01:00:00', N'01:00:00', 13, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'113')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:14', N'01:00:00', N'01:00:00', 14, 1, N'02:00:00', N'01:15:00', N'After Midnight', 0, N'114')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:15', N'01:00:00', N'01:15:00', 15, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'115')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:16', N'01:00:00', N'01:15:00', 16, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'116')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:17', N'01:00:00', N'01:15:00', 17, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'117')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:18', N'01:00:00', N'01:15:00', 18, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'118')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:19', N'01:00:00', N'01:15:00', 19, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'119')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:20', N'01:00:00', N'01:15:00', 20, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'120')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:21', N'01:00:00', N'01:15:00', 21, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'121')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:22', N'01:00:00', N'01:15:00', 22, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'122')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:23', N'01:00:00', N'01:15:00', 23, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'123')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:24', N'01:00:00', N'01:15:00', 24, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'124')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:25', N'01:00:00', N'01:15:00', 25, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'125')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:26', N'01:00:00', N'01:15:00', 26, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'126')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:27', N'01:00:00', N'01:15:00', 27, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'127')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:28', N'01:00:00', N'01:15:00', 28, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'128')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:29', N'01:00:00', N'01:15:00', 29, 1, N'02:00:00', N'01:30:00', N'After Midnight', 0, N'129')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:30', N'01:00:00', N'01:30:00', 30, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'130')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:31', N'01:00:00', N'01:30:00', 31, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'131')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:32', N'01:00:00', N'01:30:00', 32, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'132')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:33', N'01:00:00', N'01:30:00', 33, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'133')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:34', N'01:00:00', N'01:30:00', 34, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'134')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:35', N'01:00:00', N'01:30:00', 35, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'135')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:36', N'01:00:00', N'01:30:00', 36, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'136')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:37', N'01:00:00', N'01:30:00', 37, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'137')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:38', N'01:00:00', N'01:30:00', 38, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'138')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:39', N'01:00:00', N'01:30:00', 39, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'139')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:40', N'01:00:00', N'01:30:00', 40, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'140')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:41', N'01:00:00', N'01:30:00', 41, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'141')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:42', N'01:00:00', N'01:30:00', 42, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'142')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:43', N'01:00:00', N'01:30:00', 43, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'143')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:44', N'01:00:00', N'01:30:00', 44, 1, N'02:00:00', N'01:45:00', N'After Midnight', 0, N'144')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:45', N'01:00:00', N'01:45:00', 45, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'145')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:46', N'01:00:00', N'01:45:00', 46, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'146')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:47', N'01:00:00', N'01:45:00', 47, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'147')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:48', N'01:00:00', N'01:45:00', 48, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'148')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:49', N'01:00:00', N'01:45:00', 49, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'149')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:50', N'01:00:00', N'01:45:00', 50, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'150')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:51', N'01:00:00', N'01:45:00', 51, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'151')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:52', N'01:00:00', N'01:45:00', 52, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'152')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:53', N'01:00:00', N'01:45:00', 53, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'153')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:54', N'01:00:00', N'01:45:00', 54, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'154')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:55', N'01:00:00', N'01:45:00', 55, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'155')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:56', N'01:00:00', N'01:45:00', 56, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'156')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:57', N'01:00:00', N'01:45:00', 57, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'157')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:58', N'01:00:00', N'01:45:00', 58, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'158')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'01:59', N'01:00:00', N'01:45:00', 59, 1, N'02:00:00', N'02:00:00', N'After Midnight', 0, N'159')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:00', N'02:00:00', N'02:00:00', 0, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'200')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:01', N'02:00:00', N'02:00:00', 1, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'201')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:02', N'02:00:00', N'02:00:00', 2, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'202')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:03', N'02:00:00', N'02:00:00', 3, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'203')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:04', N'02:00:00', N'02:00:00', 4, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'204')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:05', N'02:00:00', N'02:00:00', 5, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'205')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:06', N'02:00:00', N'02:00:00', 6, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'206')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:07', N'02:00:00', N'02:00:00', 7, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'207')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:08', N'02:00:00', N'02:00:00', 8, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'208')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:09', N'02:00:00', N'02:00:00', 9, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'209')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:10', N'02:00:00', N'02:00:00', 10, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'210')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:11', N'02:00:00', N'02:00:00', 11, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'211')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:12', N'02:00:00', N'02:00:00', 12, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'212')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:13', N'02:00:00', N'02:00:00', 13, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'213')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:14', N'02:00:00', N'02:00:00', 14, 2, N'03:00:00', N'02:15:00', N'After Midnight', 0, N'214')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:15', N'02:00:00', N'02:15:00', 15, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'215')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:16', N'02:00:00', N'02:15:00', 16, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'216')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:17', N'02:00:00', N'02:15:00', 17, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'217')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:18', N'02:00:00', N'02:15:00', 18, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'218')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:19', N'02:00:00', N'02:15:00', 19, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'219')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:20', N'02:00:00', N'02:15:00', 20, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'220')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:21', N'02:00:00', N'02:15:00', 21, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'221')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:22', N'02:00:00', N'02:15:00', 22, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'222')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:23', N'02:00:00', N'02:15:00', 23, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'223')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:24', N'02:00:00', N'02:15:00', 24, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'224')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:25', N'02:00:00', N'02:15:00', 25, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'225')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:26', N'02:00:00', N'02:15:00', 26, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'226')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:27', N'02:00:00', N'02:15:00', 27, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'227')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:28', N'02:00:00', N'02:15:00', 28, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'228')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:29', N'02:00:00', N'02:15:00', 29, 2, N'03:00:00', N'02:30:00', N'After Midnight', 0, N'229')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:30', N'02:00:00', N'02:30:00', 30, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'230')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:31', N'02:00:00', N'02:30:00', 31, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'231')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:32', N'02:00:00', N'02:30:00', 32, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'232')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:33', N'02:00:00', N'02:30:00', 33, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'233')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:34', N'02:00:00', N'02:30:00', 34, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'234')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:35', N'02:00:00', N'02:30:00', 35, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'235')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:36', N'02:00:00', N'02:30:00', 36, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'236')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:37', N'02:00:00', N'02:30:00', 37, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'237')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:38', N'02:00:00', N'02:30:00', 38, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'238')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:39', N'02:00:00', N'02:30:00', 39, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'239')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:40', N'02:00:00', N'02:30:00', 40, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'240')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:41', N'02:00:00', N'02:30:00', 41, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'241')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:42', N'02:00:00', N'02:30:00', 42, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'242')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:43', N'02:00:00', N'02:30:00', 43, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'243')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:44', N'02:00:00', N'02:30:00', 44, 2, N'03:00:00', N'02:45:00', N'After Midnight', 0, N'244')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:45', N'02:00:00', N'02:45:00', 45, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'245')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:46', N'02:00:00', N'02:45:00', 46, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'246')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:47', N'02:00:00', N'02:45:00', 47, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'247')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:48', N'02:00:00', N'02:45:00', 48, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'248')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:49', N'02:00:00', N'02:45:00', 49, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'249')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:50', N'02:00:00', N'02:45:00', 50, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'250')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:51', N'02:00:00', N'02:45:00', 51, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'251')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:52', N'02:00:00', N'02:45:00', 52, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'252')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:53', N'02:00:00', N'02:45:00', 53, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'253')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:54', N'02:00:00', N'02:45:00', 54, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'254')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:55', N'02:00:00', N'02:45:00', 55, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'255')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:56', N'02:00:00', N'02:45:00', 56, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'256')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:57', N'02:00:00', N'02:45:00', 57, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'257')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:58', N'02:00:00', N'02:45:00', 58, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'258')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'02:59', N'02:00:00', N'02:45:00', 59, 2, N'03:00:00', N'03:00:00', N'After Midnight', 0, N'259')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:00', N'03:00:00', N'03:00:00', 0, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'300')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:01', N'03:00:00', N'03:00:00', 1, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'301')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:02', N'03:00:00', N'03:00:00', 2, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'302')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:03', N'03:00:00', N'03:00:00', 3, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'303')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:04', N'03:00:00', N'03:00:00', 4, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'304')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:05', N'03:00:00', N'03:00:00', 5, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'305')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:06', N'03:00:00', N'03:00:00', 6, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'306')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:07', N'03:00:00', N'03:00:00', 7, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'307')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:08', N'03:00:00', N'03:00:00', 8, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'308')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:09', N'03:00:00', N'03:00:00', 9, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'309')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:10', N'03:00:00', N'03:00:00', 10, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'310')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:11', N'03:00:00', N'03:00:00', 11, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'311')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:12', N'03:00:00', N'03:00:00', 12, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'312')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:13', N'03:00:00', N'03:00:00', 13, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'313')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:14', N'03:00:00', N'03:00:00', 14, 3, N'04:00:00', N'03:15:00', N'After Midnight', 0, N'314')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:15', N'03:00:00', N'03:15:00', 15, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'315')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:16', N'03:00:00', N'03:15:00', 16, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'316')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:17', N'03:00:00', N'03:15:00', 17, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'317')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:18', N'03:00:00', N'03:15:00', 18, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'318')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:19', N'03:00:00', N'03:15:00', 19, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'319')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:20', N'03:00:00', N'03:15:00', 20, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'320')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:21', N'03:00:00', N'03:15:00', 21, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'321')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:22', N'03:00:00', N'03:15:00', 22, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'322')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:23', N'03:00:00', N'03:15:00', 23, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'323')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:24', N'03:00:00', N'03:15:00', 24, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'324')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:25', N'03:00:00', N'03:15:00', 25, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'325')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:26', N'03:00:00', N'03:15:00', 26, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'326')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:27', N'03:00:00', N'03:15:00', 27, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'327')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:28', N'03:00:00', N'03:15:00', 28, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'328')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:29', N'03:00:00', N'03:15:00', 29, 3, N'04:00:00', N'03:30:00', N'After Midnight', 0, N'329')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:30', N'03:00:00', N'03:30:00', 30, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'330')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:31', N'03:00:00', N'03:30:00', 31, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'331')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:32', N'03:00:00', N'03:30:00', 32, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'332')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:33', N'03:00:00', N'03:30:00', 33, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'333')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:34', N'03:00:00', N'03:30:00', 34, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'334')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:35', N'03:00:00', N'03:30:00', 35, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'335')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:36', N'03:00:00', N'03:30:00', 36, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'336')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:37', N'03:00:00', N'03:30:00', 37, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'337')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:38', N'03:00:00', N'03:30:00', 38, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'338')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:39', N'03:00:00', N'03:30:00', 39, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'339')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:40', N'03:00:00', N'03:30:00', 40, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'340')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:41', N'03:00:00', N'03:30:00', 41, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'341')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:42', N'03:00:00', N'03:30:00', 42, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'342')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:43', N'03:00:00', N'03:30:00', 43, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'343')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:44', N'03:00:00', N'03:30:00', 44, 3, N'04:00:00', N'03:45:00', N'After Midnight', 0, N'344')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:45', N'03:00:00', N'03:45:00', 45, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'345')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:46', N'03:00:00', N'03:45:00', 46, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'346')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:47', N'03:00:00', N'03:45:00', 47, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'347')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:48', N'03:00:00', N'03:45:00', 48, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'348')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:49', N'03:00:00', N'03:45:00', 49, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'349')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:50', N'03:00:00', N'03:45:00', 50, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'350')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:51', N'03:00:00', N'03:45:00', 51, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'351')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:52', N'03:00:00', N'03:45:00', 52, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'352')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:53', N'03:00:00', N'03:45:00', 53, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'353')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:54', N'03:00:00', N'03:45:00', 54, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'354')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:55', N'03:00:00', N'03:45:00', 55, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'355')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:56', N'03:00:00', N'03:45:00', 56, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'356')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:57', N'03:00:00', N'03:45:00', 57, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'357')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:58', N'03:00:00', N'03:45:00', 58, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'358')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'03:59', N'03:00:00', N'03:45:00', 59, 3, N'04:00:00', N'04:00:00', N'After Midnight', 0, N'359')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:00', N'04:00:00', N'04:00:00', 0, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'400')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:01', N'04:00:00', N'04:00:00', 1, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'401')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:02', N'04:00:00', N'04:00:00', 2, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'402')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:03', N'04:00:00', N'04:00:00', 3, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'403')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:04', N'04:00:00', N'04:00:00', 4, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'404')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:05', N'04:00:00', N'04:00:00', 5, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'405')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:06', N'04:00:00', N'04:00:00', 6, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'406')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:07', N'04:00:00', N'04:00:00', 7, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'407')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:08', N'04:00:00', N'04:00:00', 8, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'408')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:09', N'04:00:00', N'04:00:00', 9, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'409')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:10', N'04:00:00', N'04:00:00', 10, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'410')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:11', N'04:00:00', N'04:00:00', 11, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'411')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:12', N'04:00:00', N'04:00:00', 12, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'412')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:13', N'04:00:00', N'04:00:00', 13, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'413')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:14', N'04:00:00', N'04:00:00', 14, 4, N'05:00:00', N'04:15:00', N'Early Morning', 1, N'414')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:15', N'04:00:00', N'04:15:00', 15, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'415')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:16', N'04:00:00', N'04:15:00', 16, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'416')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:17', N'04:00:00', N'04:15:00', 17, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'417')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:18', N'04:00:00', N'04:15:00', 18, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'418')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:19', N'04:00:00', N'04:15:00', 19, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'419')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:20', N'04:00:00', N'04:15:00', 20, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'420')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:21', N'04:00:00', N'04:15:00', 21, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'421')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:22', N'04:00:00', N'04:15:00', 22, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'422')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:23', N'04:00:00', N'04:15:00', 23, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'423')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:24', N'04:00:00', N'04:15:00', 24, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'424')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:25', N'04:00:00', N'04:15:00', 25, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'425')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:26', N'04:00:00', N'04:15:00', 26, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'426')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:27', N'04:00:00', N'04:15:00', 27, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'427')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:28', N'04:00:00', N'04:15:00', 28, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'428')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:29', N'04:00:00', N'04:15:00', 29, 4, N'05:00:00', N'04:30:00', N'Early Morning', 1, N'429')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:30', N'04:00:00', N'04:30:00', 30, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'430')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:31', N'04:00:00', N'04:30:00', 31, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'431')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:32', N'04:00:00', N'04:30:00', 32, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'432')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:33', N'04:00:00', N'04:30:00', 33, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'433')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:34', N'04:00:00', N'04:30:00', 34, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'434')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:35', N'04:00:00', N'04:30:00', 35, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'435')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:36', N'04:00:00', N'04:30:00', 36, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'436')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:37', N'04:00:00', N'04:30:00', 37, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'437')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:38', N'04:00:00', N'04:30:00', 38, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'438')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:39', N'04:00:00', N'04:30:00', 39, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'439')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:40', N'04:00:00', N'04:30:00', 40, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'440')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:41', N'04:00:00', N'04:30:00', 41, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'441')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:42', N'04:00:00', N'04:30:00', 42, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'442')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:43', N'04:00:00', N'04:30:00', 43, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'443')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:44', N'04:00:00', N'04:30:00', 44, 4, N'05:00:00', N'04:45:00', N'Early Morning', 1, N'444')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:45', N'04:00:00', N'04:45:00', 45, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'445')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:46', N'04:00:00', N'04:45:00', 46, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'446')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:47', N'04:00:00', N'04:45:00', 47, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'447')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:48', N'04:00:00', N'04:45:00', 48, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'448')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:49', N'04:00:00', N'04:45:00', 49, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'449')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:50', N'04:00:00', N'04:45:00', 50, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'450')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:51', N'04:00:00', N'04:45:00', 51, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'451')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:52', N'04:00:00', N'04:45:00', 52, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'452')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:53', N'04:00:00', N'04:45:00', 53, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'453')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:54', N'04:00:00', N'04:45:00', 54, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'454')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:55', N'04:00:00', N'04:45:00', 55, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'455')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:56', N'04:00:00', N'04:45:00', 56, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'456')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:57', N'04:00:00', N'04:45:00', 57, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'457')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:58', N'04:00:00', N'04:45:00', 58, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'458')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'04:59', N'04:00:00', N'04:45:00', 59, 4, N'05:00:00', N'05:00:00', N'Early Morning', 1, N'459')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:00', N'05:00:00', N'05:00:00', 0, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'500')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:01', N'05:00:00', N'05:00:00', 1, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'501')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:02', N'05:00:00', N'05:00:00', 2, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'502')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:03', N'05:00:00', N'05:00:00', 3, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'503')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:04', N'05:00:00', N'05:00:00', 4, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'504')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:05', N'05:00:00', N'05:00:00', 5, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'505')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:06', N'05:00:00', N'05:00:00', 6, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'506')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:07', N'05:00:00', N'05:00:00', 7, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'507')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:08', N'05:00:00', N'05:00:00', 8, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'508')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:09', N'05:00:00', N'05:00:00', 9, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'509')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:10', N'05:00:00', N'05:00:00', 10, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'510')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:11', N'05:00:00', N'05:00:00', 11, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'511')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:12', N'05:00:00', N'05:00:00', 12, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'512')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:13', N'05:00:00', N'05:00:00', 13, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'513')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:14', N'05:00:00', N'05:00:00', 14, 5, N'06:00:00', N'05:15:00', N'Early Morning', 1, N'514')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:15', N'05:00:00', N'05:15:00', 15, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'515')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:16', N'05:00:00', N'05:15:00', 16, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'516')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:17', N'05:00:00', N'05:15:00', 17, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'517')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:18', N'05:00:00', N'05:15:00', 18, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'518')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:19', N'05:00:00', N'05:15:00', 19, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'519')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:20', N'05:00:00', N'05:15:00', 20, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'520')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:21', N'05:00:00', N'05:15:00', 21, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'521')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:22', N'05:00:00', N'05:15:00', 22, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'522')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:23', N'05:00:00', N'05:15:00', 23, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'523')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:24', N'05:00:00', N'05:15:00', 24, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'524')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:25', N'05:00:00', N'05:15:00', 25, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'525')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:26', N'05:00:00', N'05:15:00', 26, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'526')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:27', N'05:00:00', N'05:15:00', 27, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'527')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:28', N'05:00:00', N'05:15:00', 28, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'528')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:29', N'05:00:00', N'05:15:00', 29, 5, N'06:00:00', N'05:30:00', N'Early Morning', 1, N'529')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:30', N'05:00:00', N'05:30:00', 30, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'530')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:31', N'05:00:00', N'05:30:00', 31, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'531')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:32', N'05:00:00', N'05:30:00', 32, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'532')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:33', N'05:00:00', N'05:30:00', 33, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'533')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:34', N'05:00:00', N'05:30:00', 34, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'534')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:35', N'05:00:00', N'05:30:00', 35, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'535')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:36', N'05:00:00', N'05:30:00', 36, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'536')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:37', N'05:00:00', N'05:30:00', 37, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'537')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:38', N'05:00:00', N'05:30:00', 38, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'538')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:39', N'05:00:00', N'05:30:00', 39, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'539')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:40', N'05:00:00', N'05:30:00', 40, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'540')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:41', N'05:00:00', N'05:30:00', 41, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'541')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:42', N'05:00:00', N'05:30:00', 42, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'542')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:43', N'05:00:00', N'05:30:00', 43, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'543')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:44', N'05:00:00', N'05:30:00', 44, 5, N'06:00:00', N'05:45:00', N'Early Morning', 1, N'544')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:45', N'05:00:00', N'05:45:00', 45, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'545')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:46', N'05:00:00', N'05:45:00', 46, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'546')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:47', N'05:00:00', N'05:45:00', 47, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'547')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:48', N'05:00:00', N'05:45:00', 48, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'548')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:49', N'05:00:00', N'05:45:00', 49, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'549')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:50', N'05:00:00', N'05:45:00', 50, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'550')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:51', N'05:00:00', N'05:45:00', 51, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'551')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:52', N'05:00:00', N'05:45:00', 52, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'552')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:53', N'05:00:00', N'05:45:00', 53, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'553')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:54', N'05:00:00', N'05:45:00', 54, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'554')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:55', N'05:00:00', N'05:45:00', 55, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'555')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:56', N'05:00:00', N'05:45:00', 56, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'556')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:57', N'05:00:00', N'05:45:00', 57, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'557')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:58', N'05:00:00', N'05:45:00', 58, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'558')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'05:59', N'05:00:00', N'05:45:00', 59, 5, N'06:00:00', N'06:00:00', N'Early Morning', 1, N'559')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:00', N'06:00:00', N'06:00:00', 0, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'600')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:01', N'06:00:00', N'06:00:00', 1, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'601')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:02', N'06:00:00', N'06:00:00', 2, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'602')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:03', N'06:00:00', N'06:00:00', 3, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'603')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:04', N'06:00:00', N'06:00:00', 4, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'604')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:05', N'06:00:00', N'06:00:00', 5, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'605')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:06', N'06:00:00', N'06:00:00', 6, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'606')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:07', N'06:00:00', N'06:00:00', 7, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'607')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:08', N'06:00:00', N'06:00:00', 8, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'608')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:09', N'06:00:00', N'06:00:00', 9, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'609')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:10', N'06:00:00', N'06:00:00', 10, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'610')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:11', N'06:00:00', N'06:00:00', 11, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'611')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:12', N'06:00:00', N'06:00:00', 12, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'612')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:13', N'06:00:00', N'06:00:00', 13, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'613')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:14', N'06:00:00', N'06:00:00', 14, 6, N'07:00:00', N'06:15:00', N'Early Morning', 1, N'614')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:15', N'06:00:00', N'06:15:00', 15, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'615')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:16', N'06:00:00', N'06:15:00', 16, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'616')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:17', N'06:00:00', N'06:15:00', 17, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'617')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:18', N'06:00:00', N'06:15:00', 18, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'618')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:19', N'06:00:00', N'06:15:00', 19, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'619')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:20', N'06:00:00', N'06:15:00', 20, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'620')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:21', N'06:00:00', N'06:15:00', 21, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'621')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:22', N'06:00:00', N'06:15:00', 22, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'622')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:23', N'06:00:00', N'06:15:00', 23, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'623')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:24', N'06:00:00', N'06:15:00', 24, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'624')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:25', N'06:00:00', N'06:15:00', 25, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'625')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:26', N'06:00:00', N'06:15:00', 26, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'626')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:27', N'06:00:00', N'06:15:00', 27, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'627')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:28', N'06:00:00', N'06:15:00', 28, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'628')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:29', N'06:00:00', N'06:15:00', 29, 6, N'07:00:00', N'06:30:00', N'Early Morning', 1, N'629')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:30', N'06:00:00', N'06:30:00', 30, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'630')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:31', N'06:00:00', N'06:30:00', 31, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'631')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:32', N'06:00:00', N'06:30:00', 32, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'632')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:33', N'06:00:00', N'06:30:00', 33, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'633')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:34', N'06:00:00', N'06:30:00', 34, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'634')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:35', N'06:00:00', N'06:30:00', 35, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'635')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:36', N'06:00:00', N'06:30:00', 36, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'636')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:37', N'06:00:00', N'06:30:00', 37, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'637')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:38', N'06:00:00', N'06:30:00', 38, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'638')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:39', N'06:00:00', N'06:30:00', 39, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'639')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:40', N'06:00:00', N'06:30:00', 40, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'640')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:41', N'06:00:00', N'06:30:00', 41, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'641')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:42', N'06:00:00', N'06:30:00', 42, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'642')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:43', N'06:00:00', N'06:30:00', 43, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'643')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:44', N'06:00:00', N'06:30:00', 44, 6, N'07:00:00', N'06:45:00', N'Early Morning', 1, N'644')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:45', N'06:00:00', N'06:45:00', 45, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'645')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:46', N'06:00:00', N'06:45:00', 46, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'646')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:47', N'06:00:00', N'06:45:00', 47, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'647')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:48', N'06:00:00', N'06:45:00', 48, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'648')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:49', N'06:00:00', N'06:45:00', 49, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'649')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:50', N'06:00:00', N'06:45:00', 50, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'650')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:51', N'06:00:00', N'06:45:00', 51, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'651')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:52', N'06:00:00', N'06:45:00', 52, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'652')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:53', N'06:00:00', N'06:45:00', 53, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'653')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:54', N'06:00:00', N'06:45:00', 54, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'654')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:55', N'06:00:00', N'06:45:00', 55, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'655')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:56', N'06:00:00', N'06:45:00', 56, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'656')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:57', N'06:00:00', N'06:45:00', 57, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'657')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:58', N'06:00:00', N'06:45:00', 58, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'658')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'06:59', N'06:00:00', N'06:45:00', 59, 6, N'07:00:00', N'07:00:00', N'Early Morning', 1, N'659')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:00', N'07:00:00', N'07:00:00', 0, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'700')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:01', N'07:00:00', N'07:00:00', 1, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'701')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:02', N'07:00:00', N'07:00:00', 2, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'702')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:03', N'07:00:00', N'07:00:00', 3, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'703')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:04', N'07:00:00', N'07:00:00', 4, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'704')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:05', N'07:00:00', N'07:00:00', 5, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'705')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:06', N'07:00:00', N'07:00:00', 6, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'706')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:07', N'07:00:00', N'07:00:00', 7, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'707')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:08', N'07:00:00', N'07:00:00', 8, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'708')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:09', N'07:00:00', N'07:00:00', 9, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'709')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:10', N'07:00:00', N'07:00:00', 10, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'710')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:11', N'07:00:00', N'07:00:00', 11, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'711')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:12', N'07:00:00', N'07:00:00', 12, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'712')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:13', N'07:00:00', N'07:00:00', 13, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'713')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:14', N'07:00:00', N'07:00:00', 14, 7, N'08:00:00', N'07:15:00', N'Early Morning', 1, N'714')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:15', N'07:00:00', N'07:15:00', 15, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'715')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:16', N'07:00:00', N'07:15:00', 16, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'716')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:17', N'07:00:00', N'07:15:00', 17, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'717')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:18', N'07:00:00', N'07:15:00', 18, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'718')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:19', N'07:00:00', N'07:15:00', 19, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'719')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:20', N'07:00:00', N'07:15:00', 20, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'720')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:21', N'07:00:00', N'07:15:00', 21, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'721')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:22', N'07:00:00', N'07:15:00', 22, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'722')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:23', N'07:00:00', N'07:15:00', 23, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'723')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:24', N'07:00:00', N'07:15:00', 24, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'724')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:25', N'07:00:00', N'07:15:00', 25, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'725')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:26', N'07:00:00', N'07:15:00', 26, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'726')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:27', N'07:00:00', N'07:15:00', 27, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'727')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:28', N'07:00:00', N'07:15:00', 28, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'728')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:29', N'07:00:00', N'07:15:00', 29, 7, N'08:00:00', N'07:30:00', N'Early Morning', 1, N'729')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:30', N'07:00:00', N'07:30:00', 30, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'730')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:31', N'07:00:00', N'07:30:00', 31, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'731')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:32', N'07:00:00', N'07:30:00', 32, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'732')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:33', N'07:00:00', N'07:30:00', 33, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'733')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:34', N'07:00:00', N'07:30:00', 34, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'734')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:35', N'07:00:00', N'07:30:00', 35, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'735')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:36', N'07:00:00', N'07:30:00', 36, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'736')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:37', N'07:00:00', N'07:30:00', 37, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'737')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:38', N'07:00:00', N'07:30:00', 38, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'738')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:39', N'07:00:00', N'07:30:00', 39, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'739')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:40', N'07:00:00', N'07:30:00', 40, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'740')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:41', N'07:00:00', N'07:30:00', 41, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'741')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:42', N'07:00:00', N'07:30:00', 42, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'742')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:43', N'07:00:00', N'07:30:00', 43, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'743')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:44', N'07:00:00', N'07:30:00', 44, 7, N'08:00:00', N'07:45:00', N'Early Morning', 1, N'744')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:45', N'07:00:00', N'07:45:00', 45, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'745')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:46', N'07:00:00', N'07:45:00', 46, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'746')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:47', N'07:00:00', N'07:45:00', 47, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'747')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:48', N'07:00:00', N'07:45:00', 48, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'748')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:49', N'07:00:00', N'07:45:00', 49, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'749')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:50', N'07:00:00', N'07:45:00', 50, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'750')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:51', N'07:00:00', N'07:45:00', 51, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'751')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:52', N'07:00:00', N'07:45:00', 52, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'752')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:53', N'07:00:00', N'07:45:00', 53, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'753')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:54', N'07:00:00', N'07:45:00', 54, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'754')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:55', N'07:00:00', N'07:45:00', 55, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'755')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:56', N'07:00:00', N'07:45:00', 56, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'756')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:57', N'07:00:00', N'07:45:00', 57, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'757')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:58', N'07:00:00', N'07:45:00', 58, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'758')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'07:59', N'07:00:00', N'07:45:00', 59, 7, N'08:00:00', N'08:00:00', N'Early Morning', 1, N'759')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:00', N'08:00:00', N'08:00:00', 0, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'800')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:01', N'08:00:00', N'08:00:00', 1, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'801')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:02', N'08:00:00', N'08:00:00', 2, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'802')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:03', N'08:00:00', N'08:00:00', 3, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'803')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:04', N'08:00:00', N'08:00:00', 4, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'804')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:05', N'08:00:00', N'08:00:00', 5, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'805')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:06', N'08:00:00', N'08:00:00', 6, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'806')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:07', N'08:00:00', N'08:00:00', 7, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'807')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:08', N'08:00:00', N'08:00:00', 8, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'808')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:09', N'08:00:00', N'08:00:00', 9, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'809')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:10', N'08:00:00', N'08:00:00', 10, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'810')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:11', N'08:00:00', N'08:00:00', 11, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'811')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:12', N'08:00:00', N'08:00:00', 12, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'812')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:13', N'08:00:00', N'08:00:00', 13, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'813')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:14', N'08:00:00', N'08:00:00', 14, 8, N'09:00:00', N'08:15:00', N'Late Morning', 2, N'814')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:15', N'08:00:00', N'08:15:00', 15, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'815')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:16', N'08:00:00', N'08:15:00', 16, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'816')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:17', N'08:00:00', N'08:15:00', 17, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'817')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:18', N'08:00:00', N'08:15:00', 18, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'818')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:19', N'08:00:00', N'08:15:00', 19, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'819')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:20', N'08:00:00', N'08:15:00', 20, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'820')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:21', N'08:00:00', N'08:15:00', 21, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'821')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:22', N'08:00:00', N'08:15:00', 22, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'822')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:23', N'08:00:00', N'08:15:00', 23, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'823')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:24', N'08:00:00', N'08:15:00', 24, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'824')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:25', N'08:00:00', N'08:15:00', 25, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'825')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:26', N'08:00:00', N'08:15:00', 26, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'826')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:27', N'08:00:00', N'08:15:00', 27, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'827')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:28', N'08:00:00', N'08:15:00', 28, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'828')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:29', N'08:00:00', N'08:15:00', 29, 8, N'09:00:00', N'08:30:00', N'Late Morning', 2, N'829')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:30', N'08:00:00', N'08:30:00', 30, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'830')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:31', N'08:00:00', N'08:30:00', 31, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'831')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:32', N'08:00:00', N'08:30:00', 32, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'832')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:33', N'08:00:00', N'08:30:00', 33, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'833')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:34', N'08:00:00', N'08:30:00', 34, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'834')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:35', N'08:00:00', N'08:30:00', 35, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'835')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:36', N'08:00:00', N'08:30:00', 36, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'836')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:37', N'08:00:00', N'08:30:00', 37, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'837')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:38', N'08:00:00', N'08:30:00', 38, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'838')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:39', N'08:00:00', N'08:30:00', 39, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'839')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:40', N'08:00:00', N'08:30:00', 40, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'840')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:41', N'08:00:00', N'08:30:00', 41, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'841')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:42', N'08:00:00', N'08:30:00', 42, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'842')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:43', N'08:00:00', N'08:30:00', 43, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'843')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:44', N'08:00:00', N'08:30:00', 44, 8, N'09:00:00', N'08:45:00', N'Late Morning', 2, N'844')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:45', N'08:00:00', N'08:45:00', 45, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'845')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:46', N'08:00:00', N'08:45:00', 46, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'846')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:47', N'08:00:00', N'08:45:00', 47, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'847')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:48', N'08:00:00', N'08:45:00', 48, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'848')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:49', N'08:00:00', N'08:45:00', 49, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'849')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:50', N'08:00:00', N'08:45:00', 50, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'850')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:51', N'08:00:00', N'08:45:00', 51, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'851')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:52', N'08:00:00', N'08:45:00', 52, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'852')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:53', N'08:00:00', N'08:45:00', 53, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'853')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:54', N'08:00:00', N'08:45:00', 54, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'854')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:55', N'08:00:00', N'08:45:00', 55, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'855')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:56', N'08:00:00', N'08:45:00', 56, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'856')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:57', N'08:00:00', N'08:45:00', 57, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'857')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:58', N'08:00:00', N'08:45:00', 58, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'858')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'08:59', N'08:00:00', N'08:45:00', 59, 8, N'09:00:00', N'09:00:00', N'Late Morning', 2, N'859')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:00', N'09:00:00', N'09:00:00', 0, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'900')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:01', N'09:00:00', N'09:00:00', 1, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'901')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:02', N'09:00:00', N'09:00:00', 2, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'902')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:03', N'09:00:00', N'09:00:00', 3, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'903')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:04', N'09:00:00', N'09:00:00', 4, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'904')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:05', N'09:00:00', N'09:00:00', 5, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'905')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:06', N'09:00:00', N'09:00:00', 6, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'906')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:07', N'09:00:00', N'09:00:00', 7, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'907')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:08', N'09:00:00', N'09:00:00', 8, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'908')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:09', N'09:00:00', N'09:00:00', 9, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'909')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:10', N'09:00:00', N'09:00:00', 10, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'910')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:11', N'09:00:00', N'09:00:00', 11, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'911')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:12', N'09:00:00', N'09:00:00', 12, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'912')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:13', N'09:00:00', N'09:00:00', 13, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'913')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:14', N'09:00:00', N'09:00:00', 14, 9, N'10:00:00', N'09:15:00', N'Late Morning', 2, N'914')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:15', N'09:00:00', N'09:15:00', 15, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'915')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:16', N'09:00:00', N'09:15:00', 16, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'916')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:17', N'09:00:00', N'09:15:00', 17, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'917')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:18', N'09:00:00', N'09:15:00', 18, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'918')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:19', N'09:00:00', N'09:15:00', 19, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'919')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:20', N'09:00:00', N'09:15:00', 20, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'920')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:21', N'09:00:00', N'09:15:00', 21, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'921')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:22', N'09:00:00', N'09:15:00', 22, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'922')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:23', N'09:00:00', N'09:15:00', 23, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'923')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:24', N'09:00:00', N'09:15:00', 24, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'924')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:25', N'09:00:00', N'09:15:00', 25, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'925')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:26', N'09:00:00', N'09:15:00', 26, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'926')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:27', N'09:00:00', N'09:15:00', 27, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'927')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:28', N'09:00:00', N'09:15:00', 28, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'928')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:29', N'09:00:00', N'09:15:00', 29, 9, N'10:00:00', N'09:30:00', N'Late Morning', 2, N'929')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:30', N'09:00:00', N'09:30:00', 30, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'930')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:31', N'09:00:00', N'09:30:00', 31, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'931')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:32', N'09:00:00', N'09:30:00', 32, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'932')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:33', N'09:00:00', N'09:30:00', 33, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'933')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:34', N'09:00:00', N'09:30:00', 34, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'934')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:35', N'09:00:00', N'09:30:00', 35, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'935')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:36', N'09:00:00', N'09:30:00', 36, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'936')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:37', N'09:00:00', N'09:30:00', 37, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'937')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:38', N'09:00:00', N'09:30:00', 38, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'938')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:39', N'09:00:00', N'09:30:00', 39, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'939')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:40', N'09:00:00', N'09:30:00', 40, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'940')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:41', N'09:00:00', N'09:30:00', 41, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'941')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:42', N'09:00:00', N'09:30:00', 42, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'942')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:43', N'09:00:00', N'09:30:00', 43, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'943')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:44', N'09:00:00', N'09:30:00', 44, 9, N'10:00:00', N'09:45:00', N'Late Morning', 2, N'944')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:45', N'09:00:00', N'09:45:00', 45, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'945')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:46', N'09:00:00', N'09:45:00', 46, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'946')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:47', N'09:00:00', N'09:45:00', 47, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'947')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:48', N'09:00:00', N'09:45:00', 48, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'948')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:49', N'09:00:00', N'09:45:00', 49, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'949')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:50', N'09:00:00', N'09:45:00', 50, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'950')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:51', N'09:00:00', N'09:45:00', 51, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'951')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:52', N'09:00:00', N'09:45:00', 52, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'952')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:53', N'09:00:00', N'09:45:00', 53, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'953')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:54', N'09:00:00', N'09:45:00', 54, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'954')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:55', N'09:00:00', N'09:45:00', 55, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'955')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:56', N'09:00:00', N'09:45:00', 56, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'956')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:57', N'09:00:00', N'09:45:00', 57, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'957')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:58', N'09:00:00', N'09:45:00', 58, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'958')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'09:59', N'09:00:00', N'09:45:00', 59, 9, N'10:00:00', N'10:00:00', N'Late Morning', 2, N'959')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:00', N'10:00:00', N'10:00:00', 0, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1000')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:01', N'10:00:00', N'10:00:00', 1, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1001')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:02', N'10:00:00', N'10:00:00', 2, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1002')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:03', N'10:00:00', N'10:00:00', 3, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1003')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:04', N'10:00:00', N'10:00:00', 4, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1004')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:05', N'10:00:00', N'10:00:00', 5, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1005')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:06', N'10:00:00', N'10:00:00', 6, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1006')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:07', N'10:00:00', N'10:00:00', 7, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1007')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:08', N'10:00:00', N'10:00:00', 8, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1008')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:09', N'10:00:00', N'10:00:00', 9, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1009')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:10', N'10:00:00', N'10:00:00', 10, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1010')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:11', N'10:00:00', N'10:00:00', 11, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1011')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:12', N'10:00:00', N'10:00:00', 12, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1012')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:13', N'10:00:00', N'10:00:00', 13, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1013')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:14', N'10:00:00', N'10:00:00', 14, 10, N'11:00:00', N'10:15:00', N'Late Morning', 2, N'1014')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:15', N'10:00:00', N'10:15:00', 15, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1015')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:16', N'10:00:00', N'10:15:00', 16, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1016')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:17', N'10:00:00', N'10:15:00', 17, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1017')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:18', N'10:00:00', N'10:15:00', 18, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1018')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:19', N'10:00:00', N'10:15:00', 19, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1019')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:20', N'10:00:00', N'10:15:00', 20, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1020')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:21', N'10:00:00', N'10:15:00', 21, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1021')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:22', N'10:00:00', N'10:15:00', 22, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1022')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:23', N'10:00:00', N'10:15:00', 23, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1023')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:24', N'10:00:00', N'10:15:00', 24, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1024')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:25', N'10:00:00', N'10:15:00', 25, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1025')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:26', N'10:00:00', N'10:15:00', 26, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1026')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:27', N'10:00:00', N'10:15:00', 27, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1027')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:28', N'10:00:00', N'10:15:00', 28, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1028')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:29', N'10:00:00', N'10:15:00', 29, 10, N'11:00:00', N'10:30:00', N'Late Morning', 2, N'1029')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:30', N'10:00:00', N'10:30:00', 30, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1030')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:31', N'10:00:00', N'10:30:00', 31, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1031')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:32', N'10:00:00', N'10:30:00', 32, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1032')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:33', N'10:00:00', N'10:30:00', 33, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1033')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:34', N'10:00:00', N'10:30:00', 34, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1034')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:35', N'10:00:00', N'10:30:00', 35, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1035')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:36', N'10:00:00', N'10:30:00', 36, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1036')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:37', N'10:00:00', N'10:30:00', 37, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1037')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:38', N'10:00:00', N'10:30:00', 38, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1038')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:39', N'10:00:00', N'10:30:00', 39, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1039')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:40', N'10:00:00', N'10:30:00', 40, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1040')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:41', N'10:00:00', N'10:30:00', 41, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1041')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:42', N'10:00:00', N'10:30:00', 42, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1042')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:43', N'10:00:00', N'10:30:00', 43, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1043')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:44', N'10:00:00', N'10:30:00', 44, 10, N'11:00:00', N'10:45:00', N'Late Morning', 2, N'1044')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:45', N'10:00:00', N'10:45:00', 45, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1045')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:46', N'10:00:00', N'10:45:00', 46, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1046')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:47', N'10:00:00', N'10:45:00', 47, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1047')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:48', N'10:00:00', N'10:45:00', 48, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1048')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:49', N'10:00:00', N'10:45:00', 49, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1049')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:50', N'10:00:00', N'10:45:00', 50, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1050')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:51', N'10:00:00', N'10:45:00', 51, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1051')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:52', N'10:00:00', N'10:45:00', 52, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1052')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:53', N'10:00:00', N'10:45:00', 53, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1053')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:54', N'10:00:00', N'10:45:00', 54, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1054')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:55', N'10:00:00', N'10:45:00', 55, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1055')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:56', N'10:00:00', N'10:45:00', 56, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1056')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:57', N'10:00:00', N'10:45:00', 57, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1057')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:58', N'10:00:00', N'10:45:00', 58, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1058')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'10:59', N'10:00:00', N'10:45:00', 59, 10, N'11:00:00', N'11:00:00', N'Late Morning', 2, N'1059')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:00', N'11:00:00', N'11:00:00', 0, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1100')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:01', N'11:00:00', N'11:00:00', 1, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1101')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:02', N'11:00:00', N'11:00:00', 2, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1102')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:03', N'11:00:00', N'11:00:00', 3, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1103')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:04', N'11:00:00', N'11:00:00', 4, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1104')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:05', N'11:00:00', N'11:00:00', 5, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1105')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:06', N'11:00:00', N'11:00:00', 6, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1106')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:07', N'11:00:00', N'11:00:00', 7, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1107')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:08', N'11:00:00', N'11:00:00', 8, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1108')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:09', N'11:00:00', N'11:00:00', 9, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1109')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:10', N'11:00:00', N'11:00:00', 10, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1110')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:11', N'11:00:00', N'11:00:00', 11, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1111')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:12', N'11:00:00', N'11:00:00', 12, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1112')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:13', N'11:00:00', N'11:00:00', 13, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1113')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:14', N'11:00:00', N'11:00:00', 14, 11, N'12:00:00', N'11:15:00', N'Late Morning', 2, N'1114')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:15', N'11:00:00', N'11:15:00', 15, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1115')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:16', N'11:00:00', N'11:15:00', 16, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1116')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:17', N'11:00:00', N'11:15:00', 17, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1117')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:18', N'11:00:00', N'11:15:00', 18, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1118')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:19', N'11:00:00', N'11:15:00', 19, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1119')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:20', N'11:00:00', N'11:15:00', 20, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1120')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:21', N'11:00:00', N'11:15:00', 21, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1121')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:22', N'11:00:00', N'11:15:00', 22, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1122')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:23', N'11:00:00', N'11:15:00', 23, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1123')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:24', N'11:00:00', N'11:15:00', 24, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1124')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:25', N'11:00:00', N'11:15:00', 25, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1125')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:26', N'11:00:00', N'11:15:00', 26, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1126')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:27', N'11:00:00', N'11:15:00', 27, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1127')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:28', N'11:00:00', N'11:15:00', 28, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1128')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:29', N'11:00:00', N'11:15:00', 29, 11, N'12:00:00', N'11:30:00', N'Late Morning', 2, N'1129')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:30', N'11:00:00', N'11:30:00', 30, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1130')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:31', N'11:00:00', N'11:30:00', 31, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1131')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:32', N'11:00:00', N'11:30:00', 32, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1132')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:33', N'11:00:00', N'11:30:00', 33, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1133')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:34', N'11:00:00', N'11:30:00', 34, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1134')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:35', N'11:00:00', N'11:30:00', 35, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1135')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:36', N'11:00:00', N'11:30:00', 36, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1136')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:37', N'11:00:00', N'11:30:00', 37, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1137')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:38', N'11:00:00', N'11:30:00', 38, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1138')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:39', N'11:00:00', N'11:30:00', 39, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1139')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:40', N'11:00:00', N'11:30:00', 40, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1140')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:41', N'11:00:00', N'11:30:00', 41, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1141')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:42', N'11:00:00', N'11:30:00', 42, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1142')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:43', N'11:00:00', N'11:30:00', 43, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1143')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:44', N'11:00:00', N'11:30:00', 44, 11, N'12:00:00', N'11:45:00', N'Late Morning', 2, N'1144')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:45', N'11:00:00', N'11:45:00', 45, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1145')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:46', N'11:00:00', N'11:45:00', 46, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1146')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:47', N'11:00:00', N'11:45:00', 47, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1147')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:48', N'11:00:00', N'11:45:00', 48, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1148')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:49', N'11:00:00', N'11:45:00', 49, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1149')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:50', N'11:00:00', N'11:45:00', 50, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1150')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:51', N'11:00:00', N'11:45:00', 51, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1151')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:52', N'11:00:00', N'11:45:00', 52, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1152')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:53', N'11:00:00', N'11:45:00', 53, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1153')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:54', N'11:00:00', N'11:45:00', 54, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1154')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:55', N'11:00:00', N'11:45:00', 55, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1155')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:56', N'11:00:00', N'11:45:00', 56, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1156')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:57', N'11:00:00', N'11:45:00', 57, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1157')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:58', N'11:00:00', N'11:45:00', 58, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1158')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'11:59', N'11:00:00', N'11:45:00', 59, 11, N'12:00:00', N'12:00:00', N'Late Morning', 2, N'1159')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:00', N'12:00:00', N'12:00:00', 0, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1200')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:01', N'12:00:00', N'12:00:00', 1, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1201')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:02', N'12:00:00', N'12:00:00', 2, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1202')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:03', N'12:00:00', N'12:00:00', 3, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1203')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:04', N'12:00:00', N'12:00:00', 4, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1204')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:05', N'12:00:00', N'12:00:00', 5, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1205')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:06', N'12:00:00', N'12:00:00', 6, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1206')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:07', N'12:00:00', N'12:00:00', 7, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1207')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:08', N'12:00:00', N'12:00:00', 8, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1208')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:09', N'12:00:00', N'12:00:00', 9, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1209')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:10', N'12:00:00', N'12:00:00', 10, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1210')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:11', N'12:00:00', N'12:00:00', 11, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1211')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:12', N'12:00:00', N'12:00:00', 12, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1212')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:13', N'12:00:00', N'12:00:00', 13, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1213')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:14', N'12:00:00', N'12:00:00', 14, 12, N'13:00:00', N'12:15:00', N'Afternoon', 3, N'1214')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:15', N'12:00:00', N'12:15:00', 15, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1215')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:16', N'12:00:00', N'12:15:00', 16, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1216')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:17', N'12:00:00', N'12:15:00', 17, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1217')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:18', N'12:00:00', N'12:15:00', 18, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1218')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:19', N'12:00:00', N'12:15:00', 19, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1219')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:20', N'12:00:00', N'12:15:00', 20, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1220')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:21', N'12:00:00', N'12:15:00', 21, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1221')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:22', N'12:00:00', N'12:15:00', 22, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1222')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:23', N'12:00:00', N'12:15:00', 23, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1223')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:24', N'12:00:00', N'12:15:00', 24, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1224')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:25', N'12:00:00', N'12:15:00', 25, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1225')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:26', N'12:00:00', N'12:15:00', 26, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1226')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:27', N'12:00:00', N'12:15:00', 27, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1227')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:28', N'12:00:00', N'12:15:00', 28, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1228')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:29', N'12:00:00', N'12:15:00', 29, 12, N'13:00:00', N'12:30:00', N'Afternoon', 3, N'1229')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:30', N'12:00:00', N'12:30:00', 30, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1230')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:31', N'12:00:00', N'12:30:00', 31, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1231')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:32', N'12:00:00', N'12:30:00', 32, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1232')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:33', N'12:00:00', N'12:30:00', 33, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1233')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:34', N'12:00:00', N'12:30:00', 34, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1234')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:35', N'12:00:00', N'12:30:00', 35, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1235')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:36', N'12:00:00', N'12:30:00', 36, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1236')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:37', N'12:00:00', N'12:30:00', 37, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1237')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:38', N'12:00:00', N'12:30:00', 38, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1238')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:39', N'12:00:00', N'12:30:00', 39, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1239')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:40', N'12:00:00', N'12:30:00', 40, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1240')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:41', N'12:00:00', N'12:30:00', 41, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1241')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:42', N'12:00:00', N'12:30:00', 42, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1242')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:43', N'12:00:00', N'12:30:00', 43, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1243')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:44', N'12:00:00', N'12:30:00', 44, 12, N'13:00:00', N'12:45:00', N'Afternoon', 3, N'1244')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:45', N'12:00:00', N'12:45:00', 45, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1245')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:46', N'12:00:00', N'12:45:00', 46, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1246')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:47', N'12:00:00', N'12:45:00', 47, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1247')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:48', N'12:00:00', N'12:45:00', 48, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1248')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:49', N'12:00:00', N'12:45:00', 49, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1249')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:50', N'12:00:00', N'12:45:00', 50, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1250')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:51', N'12:00:00', N'12:45:00', 51, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1251')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:52', N'12:00:00', N'12:45:00', 52, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1252')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:53', N'12:00:00', N'12:45:00', 53, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1253')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:54', N'12:00:00', N'12:45:00', 54, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1254')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:55', N'12:00:00', N'12:45:00', 55, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1255')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:56', N'12:00:00', N'12:45:00', 56, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1256')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:57', N'12:00:00', N'12:45:00', 57, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1257')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:58', N'12:00:00', N'12:45:00', 58, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1258')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'12:59', N'12:00:00', N'12:45:00', 59, 12, N'13:00:00', N'13:00:00', N'Afternoon', 3, N'1259')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:00', N'13:00:00', N'13:00:00', 0, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1300')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:01', N'13:00:00', N'13:00:00', 1, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1301')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:02', N'13:00:00', N'13:00:00', 2, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1302')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:03', N'13:00:00', N'13:00:00', 3, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1303')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:04', N'13:00:00', N'13:00:00', 4, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1304')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:05', N'13:00:00', N'13:00:00', 5, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1305')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:06', N'13:00:00', N'13:00:00', 6, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1306')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:07', N'13:00:00', N'13:00:00', 7, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1307')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:08', N'13:00:00', N'13:00:00', 8, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1308')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:09', N'13:00:00', N'13:00:00', 9, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1309')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:10', N'13:00:00', N'13:00:00', 10, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1310')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:11', N'13:00:00', N'13:00:00', 11, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1311')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:12', N'13:00:00', N'13:00:00', 12, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1312')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:13', N'13:00:00', N'13:00:00', 13, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1313')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:14', N'13:00:00', N'13:00:00', 14, 13, N'14:00:00', N'13:15:00', N'Afternoon', 3, N'1314')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:15', N'13:00:00', N'13:15:00', 15, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1315')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:16', N'13:00:00', N'13:15:00', 16, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1316')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:17', N'13:00:00', N'13:15:00', 17, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1317')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:18', N'13:00:00', N'13:15:00', 18, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1318')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:19', N'13:00:00', N'13:15:00', 19, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1319')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:20', N'13:00:00', N'13:15:00', 20, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1320')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:21', N'13:00:00', N'13:15:00', 21, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1321')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:22', N'13:00:00', N'13:15:00', 22, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1322')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:23', N'13:00:00', N'13:15:00', 23, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1323')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:24', N'13:00:00', N'13:15:00', 24, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1324')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:25', N'13:00:00', N'13:15:00', 25, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1325')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:26', N'13:00:00', N'13:15:00', 26, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1326')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:27', N'13:00:00', N'13:15:00', 27, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1327')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:28', N'13:00:00', N'13:15:00', 28, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1328')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:29', N'13:00:00', N'13:15:00', 29, 13, N'14:00:00', N'13:30:00', N'Afternoon', 3, N'1329')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:30', N'13:00:00', N'13:30:00', 30, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1330')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:31', N'13:00:00', N'13:30:00', 31, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1331')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:32', N'13:00:00', N'13:30:00', 32, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1332')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:33', N'13:00:00', N'13:30:00', 33, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1333')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:34', N'13:00:00', N'13:30:00', 34, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1334')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:35', N'13:00:00', N'13:30:00', 35, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1335')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:36', N'13:00:00', N'13:30:00', 36, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1336')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:37', N'13:00:00', N'13:30:00', 37, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1337')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:38', N'13:00:00', N'13:30:00', 38, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1338')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:39', N'13:00:00', N'13:30:00', 39, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1339')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:40', N'13:00:00', N'13:30:00', 40, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1340')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:41', N'13:00:00', N'13:30:00', 41, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1341')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:42', N'13:00:00', N'13:30:00', 42, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1342')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:43', N'13:00:00', N'13:30:00', 43, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1343')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:44', N'13:00:00', N'13:30:00', 44, 13, N'14:00:00', N'13:45:00', N'Afternoon', 3, N'1344')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:45', N'13:00:00', N'13:45:00', 45, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1345')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:46', N'13:00:00', N'13:45:00', 46, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1346')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:47', N'13:00:00', N'13:45:00', 47, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1347')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:48', N'13:00:00', N'13:45:00', 48, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1348')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:49', N'13:00:00', N'13:45:00', 49, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1349')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:50', N'13:00:00', N'13:45:00', 50, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1350')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:51', N'13:00:00', N'13:45:00', 51, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1351')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:52', N'13:00:00', N'13:45:00', 52, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1352')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:53', N'13:00:00', N'13:45:00', 53, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1353')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:54', N'13:00:00', N'13:45:00', 54, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1354')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:55', N'13:00:00', N'13:45:00', 55, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1355')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:56', N'13:00:00', N'13:45:00', 56, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1356')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:57', N'13:00:00', N'13:45:00', 57, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1357')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:58', N'13:00:00', N'13:45:00', 58, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1358')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'13:59', N'13:00:00', N'13:45:00', 59, 13, N'14:00:00', N'14:00:00', N'Afternoon', 3, N'1359')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:00', N'14:00:00', N'14:00:00', 0, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1400')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:01', N'14:00:00', N'14:00:00', 1, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1401')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:02', N'14:00:00', N'14:00:00', 2, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1402')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:03', N'14:00:00', N'14:00:00', 3, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1403')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:04', N'14:00:00', N'14:00:00', 4, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1404')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:05', N'14:00:00', N'14:00:00', 5, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1405')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:06', N'14:00:00', N'14:00:00', 6, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1406')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:07', N'14:00:00', N'14:00:00', 7, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1407')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:08', N'14:00:00', N'14:00:00', 8, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1408')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:09', N'14:00:00', N'14:00:00', 9, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1409')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:10', N'14:00:00', N'14:00:00', 10, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1410')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:11', N'14:00:00', N'14:00:00', 11, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1411')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:12', N'14:00:00', N'14:00:00', 12, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1412')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:13', N'14:00:00', N'14:00:00', 13, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1413')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:14', N'14:00:00', N'14:00:00', 14, 14, N'15:00:00', N'14:15:00', N'Afternoon', 3, N'1414')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:15', N'14:00:00', N'14:15:00', 15, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1415')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:16', N'14:00:00', N'14:15:00', 16, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1416')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:17', N'14:00:00', N'14:15:00', 17, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1417')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:18', N'14:00:00', N'14:15:00', 18, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1418')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:19', N'14:00:00', N'14:15:00', 19, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1419')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:20', N'14:00:00', N'14:15:00', 20, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1420')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:21', N'14:00:00', N'14:15:00', 21, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1421')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:22', N'14:00:00', N'14:15:00', 22, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1422')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:23', N'14:00:00', N'14:15:00', 23, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1423')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:24', N'14:00:00', N'14:15:00', 24, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1424')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:25', N'14:00:00', N'14:15:00', 25, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1425')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:26', N'14:00:00', N'14:15:00', 26, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1426')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:27', N'14:00:00', N'14:15:00', 27, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1427')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:28', N'14:00:00', N'14:15:00', 28, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1428')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:29', N'14:00:00', N'14:15:00', 29, 14, N'15:00:00', N'14:30:00', N'Afternoon', 3, N'1429')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:30', N'14:00:00', N'14:30:00', 30, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1430')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:31', N'14:00:00', N'14:30:00', 31, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1431')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:32', N'14:00:00', N'14:30:00', 32, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1432')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:33', N'14:00:00', N'14:30:00', 33, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1433')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:34', N'14:00:00', N'14:30:00', 34, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1434')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:35', N'14:00:00', N'14:30:00', 35, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1435')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:36', N'14:00:00', N'14:30:00', 36, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1436')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:37', N'14:00:00', N'14:30:00', 37, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1437')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:38', N'14:00:00', N'14:30:00', 38, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1438')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:39', N'14:00:00', N'14:30:00', 39, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1439')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:40', N'14:00:00', N'14:30:00', 40, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1440')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:41', N'14:00:00', N'14:30:00', 41, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1441')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:42', N'14:00:00', N'14:30:00', 42, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1442')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:43', N'14:00:00', N'14:30:00', 43, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1443')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:44', N'14:00:00', N'14:30:00', 44, 14, N'15:00:00', N'14:45:00', N'Afternoon', 3, N'1444')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:45', N'14:00:00', N'14:45:00', 45, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1445')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:46', N'14:00:00', N'14:45:00', 46, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1446')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:47', N'14:00:00', N'14:45:00', 47, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1447')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:48', N'14:00:00', N'14:45:00', 48, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1448')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:49', N'14:00:00', N'14:45:00', 49, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1449')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:50', N'14:00:00', N'14:45:00', 50, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1450')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:51', N'14:00:00', N'14:45:00', 51, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1451')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:52', N'14:00:00', N'14:45:00', 52, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1452')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:53', N'14:00:00', N'14:45:00', 53, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1453')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:54', N'14:00:00', N'14:45:00', 54, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1454')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:55', N'14:00:00', N'14:45:00', 55, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1455')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:56', N'14:00:00', N'14:45:00', 56, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1456')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:57', N'14:00:00', N'14:45:00', 57, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1457')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:58', N'14:00:00', N'14:45:00', 58, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1458')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'14:59', N'14:00:00', N'14:45:00', 59, 14, N'15:00:00', N'15:00:00', N'Afternoon', 3, N'1459')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:00', N'15:00:00', N'15:00:00', 0, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1500')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:01', N'15:00:00', N'15:00:00', 1, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1501')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:02', N'15:00:00', N'15:00:00', 2, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1502')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:03', N'15:00:00', N'15:00:00', 3, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1503')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:04', N'15:00:00', N'15:00:00', 4, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1504')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:05', N'15:00:00', N'15:00:00', 5, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1505')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:06', N'15:00:00', N'15:00:00', 6, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1506')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:07', N'15:00:00', N'15:00:00', 7, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1507')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:08', N'15:00:00', N'15:00:00', 8, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1508')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:09', N'15:00:00', N'15:00:00', 9, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1509')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:10', N'15:00:00', N'15:00:00', 10, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1510')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:11', N'15:00:00', N'15:00:00', 11, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1511')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:12', N'15:00:00', N'15:00:00', 12, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1512')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:13', N'15:00:00', N'15:00:00', 13, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1513')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:14', N'15:00:00', N'15:00:00', 14, 15, N'16:00:00', N'15:15:00', N'Afternoon', 3, N'1514')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:15', N'15:00:00', N'15:15:00', 15, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1515')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:16', N'15:00:00', N'15:15:00', 16, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1516')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:17', N'15:00:00', N'15:15:00', 17, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1517')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:18', N'15:00:00', N'15:15:00', 18, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1518')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:19', N'15:00:00', N'15:15:00', 19, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1519')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:20', N'15:00:00', N'15:15:00', 20, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1520')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:21', N'15:00:00', N'15:15:00', 21, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1521')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:22', N'15:00:00', N'15:15:00', 22, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1522')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:23', N'15:00:00', N'15:15:00', 23, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1523')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:24', N'15:00:00', N'15:15:00', 24, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1524')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:25', N'15:00:00', N'15:15:00', 25, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1525')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:26', N'15:00:00', N'15:15:00', 26, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1526')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:27', N'15:00:00', N'15:15:00', 27, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1527')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:28', N'15:00:00', N'15:15:00', 28, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1528')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:29', N'15:00:00', N'15:15:00', 29, 15, N'16:00:00', N'15:30:00', N'Afternoon', 3, N'1529')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:30', N'15:00:00', N'15:30:00', 30, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1530')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:31', N'15:00:00', N'15:30:00', 31, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1531')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:32', N'15:00:00', N'15:30:00', 32, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1532')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:33', N'15:00:00', N'15:30:00', 33, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1533')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:34', N'15:00:00', N'15:30:00', 34, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1534')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:35', N'15:00:00', N'15:30:00', 35, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1535')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:36', N'15:00:00', N'15:30:00', 36, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1536')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:37', N'15:00:00', N'15:30:00', 37, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1537')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:38', N'15:00:00', N'15:30:00', 38, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1538')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:39', N'15:00:00', N'15:30:00', 39, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1539')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:40', N'15:00:00', N'15:30:00', 40, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1540')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:41', N'15:00:00', N'15:30:00', 41, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1541')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:42', N'15:00:00', N'15:30:00', 42, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1542')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:43', N'15:00:00', N'15:30:00', 43, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1543')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:44', N'15:00:00', N'15:30:00', 44, 15, N'16:00:00', N'15:45:00', N'Afternoon', 3, N'1544')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:45', N'15:00:00', N'15:45:00', 45, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1545')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:46', N'15:00:00', N'15:45:00', 46, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1546')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:47', N'15:00:00', N'15:45:00', 47, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1547')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:48', N'15:00:00', N'15:45:00', 48, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1548')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:49', N'15:00:00', N'15:45:00', 49, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1549')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:50', N'15:00:00', N'15:45:00', 50, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1550')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:51', N'15:00:00', N'15:45:00', 51, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1551')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:52', N'15:00:00', N'15:45:00', 52, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1552')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:53', N'15:00:00', N'15:45:00', 53, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1553')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:54', N'15:00:00', N'15:45:00', 54, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1554')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:55', N'15:00:00', N'15:45:00', 55, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1555')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:56', N'15:00:00', N'15:45:00', 56, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1556')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:57', N'15:00:00', N'15:45:00', 57, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1557')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:58', N'15:00:00', N'15:45:00', 58, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1558')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'15:59', N'15:00:00', N'15:45:00', 59, 15, N'16:00:00', N'16:00:00', N'Afternoon', 3, N'1559')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:00', N'16:00:00', N'16:00:00', 0, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1600')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:01', N'16:00:00', N'16:00:00', 1, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1601')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:02', N'16:00:00', N'16:00:00', 2, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1602')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:03', N'16:00:00', N'16:00:00', 3, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1603')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:04', N'16:00:00', N'16:00:00', 4, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1604')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:05', N'16:00:00', N'16:00:00', 5, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1605')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:06', N'16:00:00', N'16:00:00', 6, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1606')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:07', N'16:00:00', N'16:00:00', 7, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1607')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:08', N'16:00:00', N'16:00:00', 8, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1608')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:09', N'16:00:00', N'16:00:00', 9, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1609')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:10', N'16:00:00', N'16:00:00', 10, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1610')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:11', N'16:00:00', N'16:00:00', 11, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1611')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:12', N'16:00:00', N'16:00:00', 12, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1612')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:13', N'16:00:00', N'16:00:00', 13, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1613')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:14', N'16:00:00', N'16:00:00', 14, 16, N'17:00:00', N'16:15:00', N'Evening', 4, N'1614')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:15', N'16:00:00', N'16:15:00', 15, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1615')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:16', N'16:00:00', N'16:15:00', 16, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1616')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:17', N'16:00:00', N'16:15:00', 17, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1617')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:18', N'16:00:00', N'16:15:00', 18, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1618')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:19', N'16:00:00', N'16:15:00', 19, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1619')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:20', N'16:00:00', N'16:15:00', 20, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1620')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:21', N'16:00:00', N'16:15:00', 21, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1621')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:22', N'16:00:00', N'16:15:00', 22, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1622')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:23', N'16:00:00', N'16:15:00', 23, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1623')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:24', N'16:00:00', N'16:15:00', 24, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1624')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:25', N'16:00:00', N'16:15:00', 25, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1625')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:26', N'16:00:00', N'16:15:00', 26, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1626')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:27', N'16:00:00', N'16:15:00', 27, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1627')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:28', N'16:00:00', N'16:15:00', 28, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1628')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:29', N'16:00:00', N'16:15:00', 29, 16, N'17:00:00', N'16:30:00', N'Evening', 4, N'1629')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:30', N'16:00:00', N'16:30:00', 30, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1630')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:31', N'16:00:00', N'16:30:00', 31, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1631')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:32', N'16:00:00', N'16:30:00', 32, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1632')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:33', N'16:00:00', N'16:30:00', 33, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1633')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:34', N'16:00:00', N'16:30:00', 34, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1634')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:35', N'16:00:00', N'16:30:00', 35, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1635')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:36', N'16:00:00', N'16:30:00', 36, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1636')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:37', N'16:00:00', N'16:30:00', 37, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1637')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:38', N'16:00:00', N'16:30:00', 38, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1638')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:39', N'16:00:00', N'16:30:00', 39, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1639')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:40', N'16:00:00', N'16:30:00', 40, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1640')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:41', N'16:00:00', N'16:30:00', 41, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1641')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:42', N'16:00:00', N'16:30:00', 42, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1642')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:43', N'16:00:00', N'16:30:00', 43, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1643')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:44', N'16:00:00', N'16:30:00', 44, 16, N'17:00:00', N'16:45:00', N'Evening', 4, N'1644')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:45', N'16:00:00', N'16:45:00', 45, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1645')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:46', N'16:00:00', N'16:45:00', 46, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1646')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:47', N'16:00:00', N'16:45:00', 47, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1647')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:48', N'16:00:00', N'16:45:00', 48, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1648')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:49', N'16:00:00', N'16:45:00', 49, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1649')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:50', N'16:00:00', N'16:45:00', 50, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1650')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:51', N'16:00:00', N'16:45:00', 51, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1651')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:52', N'16:00:00', N'16:45:00', 52, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1652')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:53', N'16:00:00', N'16:45:00', 53, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1653')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:54', N'16:00:00', N'16:45:00', 54, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1654')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:55', N'16:00:00', N'16:45:00', 55, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1655')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:56', N'16:00:00', N'16:45:00', 56, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1656')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:57', N'16:00:00', N'16:45:00', 57, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1657')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:58', N'16:00:00', N'16:45:00', 58, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1658')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'16:59', N'16:00:00', N'16:45:00', 59, 16, N'17:00:00', N'17:00:00', N'Evening', 4, N'1659')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:00', N'17:00:00', N'17:00:00', 0, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1700')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:01', N'17:00:00', N'17:00:00', 1, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1701')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:02', N'17:00:00', N'17:00:00', 2, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1702')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:03', N'17:00:00', N'17:00:00', 3, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1703')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:04', N'17:00:00', N'17:00:00', 4, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1704')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:05', N'17:00:00', N'17:00:00', 5, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1705')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:06', N'17:00:00', N'17:00:00', 6, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1706')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:07', N'17:00:00', N'17:00:00', 7, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1707')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:08', N'17:00:00', N'17:00:00', 8, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1708')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:09', N'17:00:00', N'17:00:00', 9, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1709')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:10', N'17:00:00', N'17:00:00', 10, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1710')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:11', N'17:00:00', N'17:00:00', 11, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1711')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:12', N'17:00:00', N'17:00:00', 12, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1712')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:13', N'17:00:00', N'17:00:00', 13, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1713')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:14', N'17:00:00', N'17:00:00', 14, 17, N'18:00:00', N'17:15:00', N'Evening', 4, N'1714')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:15', N'17:00:00', N'17:15:00', 15, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1715')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:16', N'17:00:00', N'17:15:00', 16, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1716')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:17', N'17:00:00', N'17:15:00', 17, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1717')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:18', N'17:00:00', N'17:15:00', 18, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1718')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:19', N'17:00:00', N'17:15:00', 19, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1719')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:20', N'17:00:00', N'17:15:00', 20, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1720')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:21', N'17:00:00', N'17:15:00', 21, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1721')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:22', N'17:00:00', N'17:15:00', 22, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1722')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:23', N'17:00:00', N'17:15:00', 23, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1723')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:24', N'17:00:00', N'17:15:00', 24, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1724')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:25', N'17:00:00', N'17:15:00', 25, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1725')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:26', N'17:00:00', N'17:15:00', 26, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1726')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:27', N'17:00:00', N'17:15:00', 27, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1727')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:28', N'17:00:00', N'17:15:00', 28, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1728')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:29', N'17:00:00', N'17:15:00', 29, 17, N'18:00:00', N'17:30:00', N'Evening', 4, N'1729')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:30', N'17:00:00', N'17:30:00', 30, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1730')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:31', N'17:00:00', N'17:30:00', 31, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1731')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:32', N'17:00:00', N'17:30:00', 32, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1732')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:33', N'17:00:00', N'17:30:00', 33, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1733')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:34', N'17:00:00', N'17:30:00', 34, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1734')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:35', N'17:00:00', N'17:30:00', 35, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1735')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:36', N'17:00:00', N'17:30:00', 36, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1736')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:37', N'17:00:00', N'17:30:00', 37, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1737')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:38', N'17:00:00', N'17:30:00', 38, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1738')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:39', N'17:00:00', N'17:30:00', 39, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1739')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:40', N'17:00:00', N'17:30:00', 40, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1740')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:41', N'17:00:00', N'17:30:00', 41, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1741')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:42', N'17:00:00', N'17:30:00', 42, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1742')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:43', N'17:00:00', N'17:30:00', 43, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1743')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:44', N'17:00:00', N'17:30:00', 44, 17, N'18:00:00', N'17:45:00', N'Evening', 4, N'1744')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:45', N'17:00:00', N'17:45:00', 45, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1745')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:46', N'17:00:00', N'17:45:00', 46, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1746')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:47', N'17:00:00', N'17:45:00', 47, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1747')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:48', N'17:00:00', N'17:45:00', 48, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1748')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:49', N'17:00:00', N'17:45:00', 49, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1749')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:50', N'17:00:00', N'17:45:00', 50, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1750')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:51', N'17:00:00', N'17:45:00', 51, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1751')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:52', N'17:00:00', N'17:45:00', 52, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1752')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:53', N'17:00:00', N'17:45:00', 53, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1753')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:54', N'17:00:00', N'17:45:00', 54, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1754')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:55', N'17:00:00', N'17:45:00', 55, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1755')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:56', N'17:00:00', N'17:45:00', 56, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1756')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:57', N'17:00:00', N'17:45:00', 57, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1757')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:58', N'17:00:00', N'17:45:00', 58, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1758')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'17:59', N'17:00:00', N'17:45:00', 59, 17, N'18:00:00', N'18:00:00', N'Evening', 4, N'1759')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:00', N'18:00:00', N'18:00:00', 0, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1800')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:01', N'18:00:00', N'18:00:00', 1, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1801')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:02', N'18:00:00', N'18:00:00', 2, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1802')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:03', N'18:00:00', N'18:00:00', 3, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1803')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:04', N'18:00:00', N'18:00:00', 4, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1804')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:05', N'18:00:00', N'18:00:00', 5, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1805')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:06', N'18:00:00', N'18:00:00', 6, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1806')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:07', N'18:00:00', N'18:00:00', 7, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1807')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:08', N'18:00:00', N'18:00:00', 8, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1808')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:09', N'18:00:00', N'18:00:00', 9, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1809')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:10', N'18:00:00', N'18:00:00', 10, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1810')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:11', N'18:00:00', N'18:00:00', 11, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1811')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:12', N'18:00:00', N'18:00:00', 12, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1812')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:13', N'18:00:00', N'18:00:00', 13, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1813')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:14', N'18:00:00', N'18:00:00', 14, 18, N'19:00:00', N'18:15:00', N'Evening', 4, N'1814')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:15', N'18:00:00', N'18:15:00', 15, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1815')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:16', N'18:00:00', N'18:15:00', 16, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1816')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:17', N'18:00:00', N'18:15:00', 17, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1817')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:18', N'18:00:00', N'18:15:00', 18, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1818')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:19', N'18:00:00', N'18:15:00', 19, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1819')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:20', N'18:00:00', N'18:15:00', 20, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1820')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:21', N'18:00:00', N'18:15:00', 21, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1821')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:22', N'18:00:00', N'18:15:00', 22, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1822')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:23', N'18:00:00', N'18:15:00', 23, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1823')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:24', N'18:00:00', N'18:15:00', 24, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1824')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:25', N'18:00:00', N'18:15:00', 25, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1825')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:26', N'18:00:00', N'18:15:00', 26, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1826')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:27', N'18:00:00', N'18:15:00', 27, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1827')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:28', N'18:00:00', N'18:15:00', 28, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1828')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:29', N'18:00:00', N'18:15:00', 29, 18, N'19:00:00', N'18:30:00', N'Evening', 4, N'1829')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:30', N'18:00:00', N'18:30:00', 30, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1830')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:31', N'18:00:00', N'18:30:00', 31, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1831')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:32', N'18:00:00', N'18:30:00', 32, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1832')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:33', N'18:00:00', N'18:30:00', 33, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1833')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:34', N'18:00:00', N'18:30:00', 34, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1834')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:35', N'18:00:00', N'18:30:00', 35, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1835')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:36', N'18:00:00', N'18:30:00', 36, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1836')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:37', N'18:00:00', N'18:30:00', 37, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1837')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:38', N'18:00:00', N'18:30:00', 38, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1838')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:39', N'18:00:00', N'18:30:00', 39, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1839')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:40', N'18:00:00', N'18:30:00', 40, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1840')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:41', N'18:00:00', N'18:30:00', 41, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1841')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:42', N'18:00:00', N'18:30:00', 42, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1842')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:43', N'18:00:00', N'18:30:00', 43, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1843')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:44', N'18:00:00', N'18:30:00', 44, 18, N'19:00:00', N'18:45:00', N'Evening', 4, N'1844')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:45', N'18:00:00', N'18:45:00', 45, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1845')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:46', N'18:00:00', N'18:45:00', 46, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1846')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:47', N'18:00:00', N'18:45:00', 47, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1847')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:48', N'18:00:00', N'18:45:00', 48, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1848')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:49', N'18:00:00', N'18:45:00', 49, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1849')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:50', N'18:00:00', N'18:45:00', 50, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1850')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:51', N'18:00:00', N'18:45:00', 51, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1851')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:52', N'18:00:00', N'18:45:00', 52, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1852')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:53', N'18:00:00', N'18:45:00', 53, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1853')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:54', N'18:00:00', N'18:45:00', 54, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1854')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:55', N'18:00:00', N'18:45:00', 55, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1855')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:56', N'18:00:00', N'18:45:00', 56, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1856')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:57', N'18:00:00', N'18:45:00', 57, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1857')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:58', N'18:00:00', N'18:45:00', 58, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1858')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'18:59', N'18:00:00', N'18:45:00', 59, 18, N'19:00:00', N'19:00:00', N'Evening', 4, N'1859')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:00', N'19:00:00', N'19:00:00', 0, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1900')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:01', N'19:00:00', N'19:00:00', 1, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1901')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:02', N'19:00:00', N'19:00:00', 2, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1902')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:03', N'19:00:00', N'19:00:00', 3, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1903')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:04', N'19:00:00', N'19:00:00', 4, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1904')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:05', N'19:00:00', N'19:00:00', 5, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1905')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:06', N'19:00:00', N'19:00:00', 6, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1906')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:07', N'19:00:00', N'19:00:00', 7, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1907')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:08', N'19:00:00', N'19:00:00', 8, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1908')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:09', N'19:00:00', N'19:00:00', 9, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1909')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:10', N'19:00:00', N'19:00:00', 10, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1910')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:11', N'19:00:00', N'19:00:00', 11, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1911')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:12', N'19:00:00', N'19:00:00', 12, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1912')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:13', N'19:00:00', N'19:00:00', 13, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1913')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:14', N'19:00:00', N'19:00:00', 14, 19, N'20:00:00', N'19:15:00', N'Evening', 4, N'1914')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:15', N'19:00:00', N'19:15:00', 15, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1915')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:16', N'19:00:00', N'19:15:00', 16, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1916')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:17', N'19:00:00', N'19:15:00', 17, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1917')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:18', N'19:00:00', N'19:15:00', 18, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1918')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:19', N'19:00:00', N'19:15:00', 19, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1919')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:20', N'19:00:00', N'19:15:00', 20, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1920')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:21', N'19:00:00', N'19:15:00', 21, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1921')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:22', N'19:00:00', N'19:15:00', 22, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1922')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:23', N'19:00:00', N'19:15:00', 23, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1923')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:24', N'19:00:00', N'19:15:00', 24, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1924')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:25', N'19:00:00', N'19:15:00', 25, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1925')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:26', N'19:00:00', N'19:15:00', 26, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1926')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:27', N'19:00:00', N'19:15:00', 27, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1927')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:28', N'19:00:00', N'19:15:00', 28, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1928')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:29', N'19:00:00', N'19:15:00', 29, 19, N'20:00:00', N'19:30:00', N'Evening', 4, N'1929')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:30', N'19:00:00', N'19:30:00', 30, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1930')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:31', N'19:00:00', N'19:30:00', 31, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1931')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:32', N'19:00:00', N'19:30:00', 32, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1932')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:33', N'19:00:00', N'19:30:00', 33, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1933')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:34', N'19:00:00', N'19:30:00', 34, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1934')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:35', N'19:00:00', N'19:30:00', 35, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1935')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:36', N'19:00:00', N'19:30:00', 36, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1936')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:37', N'19:00:00', N'19:30:00', 37, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1937')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:38', N'19:00:00', N'19:30:00', 38, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1938')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:39', N'19:00:00', N'19:30:00', 39, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1939')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:40', N'19:00:00', N'19:30:00', 40, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1940')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:41', N'19:00:00', N'19:30:00', 41, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1941')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:42', N'19:00:00', N'19:30:00', 42, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1942')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:43', N'19:00:00', N'19:30:00', 43, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1943')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:44', N'19:00:00', N'19:30:00', 44, 19, N'20:00:00', N'19:45:00', N'Evening', 4, N'1944')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:45', N'19:00:00', N'19:45:00', 45, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1945')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:46', N'19:00:00', N'19:45:00', 46, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1946')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:47', N'19:00:00', N'19:45:00', 47, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1947')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:48', N'19:00:00', N'19:45:00', 48, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1948')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:49', N'19:00:00', N'19:45:00', 49, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1949')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:50', N'19:00:00', N'19:45:00', 50, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1950')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:51', N'19:00:00', N'19:45:00', 51, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1951')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:52', N'19:00:00', N'19:45:00', 52, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1952')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:53', N'19:00:00', N'19:45:00', 53, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1953')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:54', N'19:00:00', N'19:45:00', 54, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1954')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:55', N'19:00:00', N'19:45:00', 55, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1955')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:56', N'19:00:00', N'19:45:00', 56, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1956')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:57', N'19:00:00', N'19:45:00', 57, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1957')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:58', N'19:00:00', N'19:45:00', 58, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1958')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'19:59', N'19:00:00', N'19:45:00', 59, 19, N'20:00:00', N'20:00:00', N'Evening', 4, N'1959')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:00', N'20:00:00', N'20:00:00', 0, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2000')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:01', N'20:00:00', N'20:00:00', 1, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2001')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:02', N'20:00:00', N'20:00:00', 2, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2002')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:03', N'20:00:00', N'20:00:00', 3, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2003')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:04', N'20:00:00', N'20:00:00', 4, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2004')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:05', N'20:00:00', N'20:00:00', 5, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2005')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:06', N'20:00:00', N'20:00:00', 6, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2006')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:07', N'20:00:00', N'20:00:00', 7, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2007')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:08', N'20:00:00', N'20:00:00', 8, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2008')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:09', N'20:00:00', N'20:00:00', 9, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2009')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:10', N'20:00:00', N'20:00:00', 10, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2010')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:11', N'20:00:00', N'20:00:00', 11, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2011')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:12', N'20:00:00', N'20:00:00', 12, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2012')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:13', N'20:00:00', N'20:00:00', 13, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2013')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:14', N'20:00:00', N'20:00:00', 14, 20, N'21:00:00', N'20:15:00', N'Late Night', 5, N'2014')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:15', N'20:00:00', N'20:15:00', 15, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2015')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:16', N'20:00:00', N'20:15:00', 16, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2016')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:17', N'20:00:00', N'20:15:00', 17, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2017')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:18', N'20:00:00', N'20:15:00', 18, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2018')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:19', N'20:00:00', N'20:15:00', 19, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2019')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:20', N'20:00:00', N'20:15:00', 20, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2020')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:21', N'20:00:00', N'20:15:00', 21, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2021')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:22', N'20:00:00', N'20:15:00', 22, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2022')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:23', N'20:00:00', N'20:15:00', 23, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2023')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:24', N'20:00:00', N'20:15:00', 24, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2024')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:25', N'20:00:00', N'20:15:00', 25, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2025')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:26', N'20:00:00', N'20:15:00', 26, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2026')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:27', N'20:00:00', N'20:15:00', 27, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2027')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:28', N'20:00:00', N'20:15:00', 28, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2028')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:29', N'20:00:00', N'20:15:00', 29, 20, N'21:00:00', N'20:30:00', N'Late Night', 5, N'2029')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:30', N'20:00:00', N'20:30:00', 30, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2030')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:31', N'20:00:00', N'20:30:00', 31, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2031')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:32', N'20:00:00', N'20:30:00', 32, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2032')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:33', N'20:00:00', N'20:30:00', 33, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2033')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:34', N'20:00:00', N'20:30:00', 34, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2034')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:35', N'20:00:00', N'20:30:00', 35, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2035')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:36', N'20:00:00', N'20:30:00', 36, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2036')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:37', N'20:00:00', N'20:30:00', 37, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2037')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:38', N'20:00:00', N'20:30:00', 38, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2038')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:39', N'20:00:00', N'20:30:00', 39, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2039')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:40', N'20:00:00', N'20:30:00', 40, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2040')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:41', N'20:00:00', N'20:30:00', 41, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2041')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:42', N'20:00:00', N'20:30:00', 42, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2042')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:43', N'20:00:00', N'20:30:00', 43, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2043')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:44', N'20:00:00', N'20:30:00', 44, 20, N'21:00:00', N'20:45:00', N'Late Night', 5, N'2044')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:45', N'20:00:00', N'20:45:00', 45, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2045')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:46', N'20:00:00', N'20:45:00', 46, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2046')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:47', N'20:00:00', N'20:45:00', 47, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2047')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:48', N'20:00:00', N'20:45:00', 48, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2048')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:49', N'20:00:00', N'20:45:00', 49, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2049')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:50', N'20:00:00', N'20:45:00', 50, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2050')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:51', N'20:00:00', N'20:45:00', 51, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2051')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:52', N'20:00:00', N'20:45:00', 52, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2052')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:53', N'20:00:00', N'20:45:00', 53, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2053')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:54', N'20:00:00', N'20:45:00', 54, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2054')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:55', N'20:00:00', N'20:45:00', 55, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2055')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:56', N'20:00:00', N'20:45:00', 56, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2056')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:57', N'20:00:00', N'20:45:00', 57, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2057')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:58', N'20:00:00', N'20:45:00', 58, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2058')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'20:59', N'20:00:00', N'20:45:00', 59, 20, N'21:00:00', N'21:00:00', N'Late Night', 5, N'2059')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:00', N'21:00:00', N'21:00:00', 0, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2100')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:01', N'21:00:00', N'21:00:00', 1, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2101')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:02', N'21:00:00', N'21:00:00', 2, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2102')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:03', N'21:00:00', N'21:00:00', 3, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2103')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:04', N'21:00:00', N'21:00:00', 4, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2104')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:05', N'21:00:00', N'21:00:00', 5, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2105')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:06', N'21:00:00', N'21:00:00', 6, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2106')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:07', N'21:00:00', N'21:00:00', 7, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2107')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:08', N'21:00:00', N'21:00:00', 8, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2108')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:09', N'21:00:00', N'21:00:00', 9, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2109')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:10', N'21:00:00', N'21:00:00', 10, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2110')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:11', N'21:00:00', N'21:00:00', 11, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2111')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:12', N'21:00:00', N'21:00:00', 12, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2112')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:13', N'21:00:00', N'21:00:00', 13, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2113')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:14', N'21:00:00', N'21:00:00', 14, 21, N'22:00:00', N'21:15:00', N'Late Night', 5, N'2114')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:15', N'21:00:00', N'21:15:00', 15, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2115')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:16', N'21:00:00', N'21:15:00', 16, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2116')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:17', N'21:00:00', N'21:15:00', 17, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2117')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:18', N'21:00:00', N'21:15:00', 18, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2118')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:19', N'21:00:00', N'21:15:00', 19, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2119')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:20', N'21:00:00', N'21:15:00', 20, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2120')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:21', N'21:00:00', N'21:15:00', 21, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2121')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:22', N'21:00:00', N'21:15:00', 22, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2122')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:23', N'21:00:00', N'21:15:00', 23, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2123')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:24', N'21:00:00', N'21:15:00', 24, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2124')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:25', N'21:00:00', N'21:15:00', 25, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2125')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:26', N'21:00:00', N'21:15:00', 26, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2126')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:27', N'21:00:00', N'21:15:00', 27, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2127')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:28', N'21:00:00', N'21:15:00', 28, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2128')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:29', N'21:00:00', N'21:15:00', 29, 21, N'22:00:00', N'21:30:00', N'Late Night', 5, N'2129')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:30', N'21:00:00', N'21:30:00', 30, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2130')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:31', N'21:00:00', N'21:30:00', 31, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2131')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:32', N'21:00:00', N'21:30:00', 32, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2132')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:33', N'21:00:00', N'21:30:00', 33, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2133')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:34', N'21:00:00', N'21:30:00', 34, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2134')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:35', N'21:00:00', N'21:30:00', 35, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2135')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:36', N'21:00:00', N'21:30:00', 36, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2136')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:37', N'21:00:00', N'21:30:00', 37, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2137')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:38', N'21:00:00', N'21:30:00', 38, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2138')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:39', N'21:00:00', N'21:30:00', 39, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2139')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:40', N'21:00:00', N'21:30:00', 40, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2140')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:41', N'21:00:00', N'21:30:00', 41, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2141')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:42', N'21:00:00', N'21:30:00', 42, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2142')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:43', N'21:00:00', N'21:30:00', 43, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2143')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:44', N'21:00:00', N'21:30:00', 44, 21, N'22:00:00', N'21:45:00', N'Late Night', 5, N'2144')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:45', N'21:00:00', N'21:45:00', 45, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2145')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:46', N'21:00:00', N'21:45:00', 46, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2146')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:47', N'21:00:00', N'21:45:00', 47, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2147')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:48', N'21:00:00', N'21:45:00', 48, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2148')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:49', N'21:00:00', N'21:45:00', 49, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2149')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:50', N'21:00:00', N'21:45:00', 50, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2150')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:51', N'21:00:00', N'21:45:00', 51, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2151')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:52', N'21:00:00', N'21:45:00', 52, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2152')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:53', N'21:00:00', N'21:45:00', 53, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2153')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:54', N'21:00:00', N'21:45:00', 54, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2154')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:55', N'21:00:00', N'21:45:00', 55, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2155')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:56', N'21:00:00', N'21:45:00', 56, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2156')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:57', N'21:00:00', N'21:45:00', 57, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2157')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:58', N'21:00:00', N'21:45:00', 58, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2158')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'21:59', N'21:00:00', N'21:45:00', 59, 21, N'22:00:00', N'22:00:00', N'Late Night', 5, N'2159')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:00', N'22:00:00', N'22:00:00', 0, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2200')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:01', N'22:00:00', N'22:00:00', 1, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2201')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:02', N'22:00:00', N'22:00:00', 2, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2202')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:03', N'22:00:00', N'22:00:00', 3, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2203')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:04', N'22:00:00', N'22:00:00', 4, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2204')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:05', N'22:00:00', N'22:00:00', 5, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2205')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:06', N'22:00:00', N'22:00:00', 6, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2206')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:07', N'22:00:00', N'22:00:00', 7, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2207')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:08', N'22:00:00', N'22:00:00', 8, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2208')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:09', N'22:00:00', N'22:00:00', 9, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2209')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:10', N'22:00:00', N'22:00:00', 10, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2210')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:11', N'22:00:00', N'22:00:00', 11, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2211')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:12', N'22:00:00', N'22:00:00', 12, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2212')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:13', N'22:00:00', N'22:00:00', 13, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2213')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:14', N'22:00:00', N'22:00:00', 14, 22, N'23:00:00', N'22:15:00', N'Late Night', 5, N'2214')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:15', N'22:00:00', N'22:15:00', 15, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2215')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:16', N'22:00:00', N'22:15:00', 16, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2216')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:17', N'22:00:00', N'22:15:00', 17, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2217')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:18', N'22:00:00', N'22:15:00', 18, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2218')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:19', N'22:00:00', N'22:15:00', 19, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2219')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:20', N'22:00:00', N'22:15:00', 20, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2220')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:21', N'22:00:00', N'22:15:00', 21, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2221')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:22', N'22:00:00', N'22:15:00', 22, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2222')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:23', N'22:00:00', N'22:15:00', 23, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2223')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:24', N'22:00:00', N'22:15:00', 24, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2224')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:25', N'22:00:00', N'22:15:00', 25, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2225')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:26', N'22:00:00', N'22:15:00', 26, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2226')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:27', N'22:00:00', N'22:15:00', 27, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2227')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:28', N'22:00:00', N'22:15:00', 28, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2228')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:29', N'22:00:00', N'22:15:00', 29, 22, N'23:00:00', N'22:30:00', N'Late Night', 5, N'2229')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:30', N'22:00:00', N'22:30:00', 30, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2230')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:31', N'22:00:00', N'22:30:00', 31, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2231')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:32', N'22:00:00', N'22:30:00', 32, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2232')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:33', N'22:00:00', N'22:30:00', 33, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2233')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:34', N'22:00:00', N'22:30:00', 34, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2234')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:35', N'22:00:00', N'22:30:00', 35, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2235')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:36', N'22:00:00', N'22:30:00', 36, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2236')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:37', N'22:00:00', N'22:30:00', 37, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2237')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:38', N'22:00:00', N'22:30:00', 38, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2238')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:39', N'22:00:00', N'22:30:00', 39, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2239')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:40', N'22:00:00', N'22:30:00', 40, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2240')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:41', N'22:00:00', N'22:30:00', 41, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2241')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:42', N'22:00:00', N'22:30:00', 42, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2242')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:43', N'22:00:00', N'22:30:00', 43, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2243')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:44', N'22:00:00', N'22:30:00', 44, 22, N'23:00:00', N'22:45:00', N'Late Night', 5, N'2244')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:45', N'22:00:00', N'22:45:00', 45, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2245')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:46', N'22:00:00', N'22:45:00', 46, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2246')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:47', N'22:00:00', N'22:45:00', 47, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2247')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:48', N'22:00:00', N'22:45:00', 48, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2248')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:49', N'22:00:00', N'22:45:00', 49, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2249')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:50', N'22:00:00', N'22:45:00', 50, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2250')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:51', N'22:00:00', N'22:45:00', 51, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2251')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:52', N'22:00:00', N'22:45:00', 52, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2252')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:53', N'22:00:00', N'22:45:00', 53, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2253')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:54', N'22:00:00', N'22:45:00', 54, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2254')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:55', N'22:00:00', N'22:45:00', 55, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2255')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:56', N'22:00:00', N'22:45:00', 56, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2256')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:57', N'22:00:00', N'22:45:00', 57, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2257')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:58', N'22:00:00', N'22:45:00', 58, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2258')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'22:59', N'22:00:00', N'22:45:00', 59, 22, N'23:00:00', N'23:00:00', N'Late Night', 5, N'2259')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:00', N'23:00:00', N'23:00:00', 0, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2300')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:01', N'23:00:00', N'23:00:00', 1, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2301')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:02', N'23:00:00', N'23:00:00', 2, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2302')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:03', N'23:00:00', N'23:00:00', 3, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2303')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:04', N'23:00:00', N'23:00:00', 4, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2304')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:05', N'23:00:00', N'23:00:00', 5, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2305')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:06', N'23:00:00', N'23:00:00', 6, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2306')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:07', N'23:00:00', N'23:00:00', 7, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2307')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:08', N'23:00:00', N'23:00:00', 8, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2308')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:09', N'23:00:00', N'23:00:00', 9, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2309')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:10', N'23:00:00', N'23:00:00', 10, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2310')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:11', N'23:00:00', N'23:00:00', 11, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2311')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:12', N'23:00:00', N'23:00:00', 12, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2312')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:13', N'23:00:00', N'23:00:00', 13, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2313')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:14', N'23:00:00', N'23:00:00', 14, 23, N'00:00:00', N'23:15:00', N'Late Night', 5, N'2314')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:15', N'23:00:00', N'23:15:00', 15, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2315')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:16', N'23:00:00', N'23:15:00', 16, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2316')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:17', N'23:00:00', N'23:15:00', 17, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2317')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:18', N'23:00:00', N'23:15:00', 18, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2318')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:19', N'23:00:00', N'23:15:00', 19, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2319')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:20', N'23:00:00', N'23:15:00', 20, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2320')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:21', N'23:00:00', N'23:15:00', 21, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2321')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:22', N'23:00:00', N'23:15:00', 22, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2322')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:23', N'23:00:00', N'23:15:00', 23, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2323')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:24', N'23:00:00', N'23:15:00', 24, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2324')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:25', N'23:00:00', N'23:15:00', 25, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2325')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:26', N'23:00:00', N'23:15:00', 26, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2326')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:27', N'23:00:00', N'23:15:00', 27, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2327')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:28', N'23:00:00', N'23:15:00', 28, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2328')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:29', N'23:00:00', N'23:15:00', 29, 23, N'00:00:00', N'23:30:00', N'Late Night', 5, N'2329')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:30', N'23:00:00', N'23:30:00', 30, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2330')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:31', N'23:00:00', N'23:30:00', 31, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2331')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:32', N'23:00:00', N'23:30:00', 32, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2332')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:33', N'23:00:00', N'23:30:00', 33, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2333')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:34', N'23:00:00', N'23:30:00', 34, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2334')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:35', N'23:00:00', N'23:30:00', 35, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2335')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:36', N'23:00:00', N'23:30:00', 36, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2336')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:37', N'23:00:00', N'23:30:00', 37, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2337')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:38', N'23:00:00', N'23:30:00', 38, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2338')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:39', N'23:00:00', N'23:30:00', 39, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2339')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:40', N'23:00:00', N'23:30:00', 40, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2340')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:41', N'23:00:00', N'23:30:00', 41, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2341')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:42', N'23:00:00', N'23:30:00', 42, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2342')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:43', N'23:00:00', N'23:30:00', 43, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2343')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:44', N'23:00:00', N'23:30:00', 44, 23, N'00:00:00', N'23:45:00', N'Late Night', 5, N'2344')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:45', N'23:00:00', N'23:45:00', 45, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2345')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:46', N'23:00:00', N'23:45:00', 46, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2346')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:47', N'23:00:00', N'23:45:00', 47, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2347')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:48', N'23:00:00', N'23:45:00', 48, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2348')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:49', N'23:00:00', N'23:45:00', 49, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2349')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:50', N'23:00:00', N'23:45:00', 50, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2350')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:51', N'23:00:00', N'23:45:00', 51, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2351')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:52', N'23:00:00', N'23:45:00', 52, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2352')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:53', N'23:00:00', N'23:45:00', 53, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2353')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:54', N'23:00:00', N'23:45:00', 54, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2354')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:55', N'23:00:00', N'23:45:00', 55, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2355')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:56', N'23:00:00', N'23:45:00', 56, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2356')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:57', N'23:00:00', N'23:45:00', 57, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2357')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:58', N'23:00:00', N'23:45:00', 58, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2358')
GO
INSERT [dbo].[DimTime] ([Time], [Hour], [Quarter Hour], [Minute], [Hour Number], [Next Hour], [Next Quarter Hour], [Period of Day], [PeriodSort], [TimeKey]) VALUES (N'23:59', N'23:00:00', N'23:45:00', 59, 23, N'00:00:00', N'00:00:00', N'Late Night', 5, N'2359')
GO
