

-- Create tables
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

-- Check to see if we see the expected number of columns
if (SELECT count (*)  FROM INFORMATION_SCHEMA.COLUMNS 
                 WHERE TABLE_SCHEMA = 'dbo' 
                 AND  TABLE_NAME = 'audit_events') = 5
begin

	print 'Upgraded already? Skipping schema modification & migration'
end

else
begin

	print 'Starting upgrade...'

	BEGIN TRY  
	
		--- Make sure we transaction everything
		begin tran AuditUpgrade;

		IF (NOT EXISTS (SELECT * 
					 FROM INFORMATION_SCHEMA.TABLES 
					 WHERE TABLE_SCHEMA = 'dbo' 
					 AND  TABLE_NAME = 'event_meta_azure_ad'))
		BEGIN
   
			CREATE TABLE [dbo].[event_meta_azure_ad](
				[event_id] [uniqueidentifier] NOT NULL,
			 CONSTRAINT [PK_dbo.event_meta_azure_ad] PRIMARY KEY CLUSTERED 
			(
				[event_id] ASC
			)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
			) ON [PRIMARY]

		END

		IF (NOT EXISTS (SELECT * 
					 FROM INFORMATION_SCHEMA.TABLES 
					 WHERE TABLE_SCHEMA = 'dbo' 
					 AND  TABLE_NAME = 'event_meta_exchange'))
		BEGIN
			CREATE TABLE [dbo].[event_meta_exchange](
				[event_id] [uniqueidentifier] NOT NULL,
				[object_id] [nvarchar](max) NULL,
			 CONSTRAINT [PK_dbo.event_meta_exchange] PRIMARY KEY CLUSTERED 
			(
				[event_id] ASC
			)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
			) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
		end

		IF (NOT EXISTS (SELECT * 
					 FROM INFORMATION_SCHEMA.TABLES 
					 WHERE TABLE_SCHEMA = 'dbo' 
					 AND  TABLE_NAME = 'event_meta_general'))
		BEGIN
			CREATE TABLE [dbo].[event_meta_general](
				[event_id] [uniqueidentifier] NOT NULL,
				[json] [nvarchar](max) NULL,
				[workload] [nvarchar](100)
			 CONSTRAINT [PK_dbo.event_meta_general] PRIMARY KEY CLUSTERED 
			(
				[event_id] ASC
			)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
			) ON [PRIMARY]
		end

		IF (NOT EXISTS (SELECT * 
					 FROM INFORMATION_SCHEMA.TABLES 
					 WHERE TABLE_SCHEMA = 'dbo' 
					 AND  TABLE_NAME = 'event_meta_sharepoint'))
		BEGIN
			CREATE TABLE [dbo].[event_meta_sharepoint](
				[event_id] [uniqueidentifier] NOT NULL,
				[file_extension_id] [int] NULL,
				[file_name_id] [int] NULL,
				[related_web_id] [int] NULL,
				[item_type_id] [int] NULL,
				[url_id] [int] NULL,
			 CONSTRAINT [PK_dbo.event_meta_sharepoint] PRIMARY KEY CLUSTERED 
			(
				[event_id] ASC
			)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
			) ON [PRIMARY]
		END

		----Rename old PK constraint?
		--IF (EXISTS (SELECT * 
		--	FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS 
		--	WHERE CONSTRAINT_TYPE = 'PRIMARY KEY' 
		--	AND TABLE_NAME = 'audit_events' 
		--	AND TABLE_SCHEMA ='dbo'
		--	AND CONSTRAINT_NAME = 'PK_dbo.audit_events_new'))
		--BEGIN
		--	EXEC sp_rename N'PK_dbo.audit_events_new', N'PK_dbo.audit_events', N'OBJECT'
		--end

		IF (NOT EXISTS (SELECT * 
					 FROM INFORMATION_SCHEMA.TABLES 
					 WHERE TABLE_SCHEMA = 'dbo' 
					 AND  TABLE_NAME = 'audit_events_new'))
		BEGIN
			print 'Creating new audit table'
			CREATE TABLE [dbo].[audit_events_new](
				[id] [uniqueidentifier] NOT NULL,
				[time_stamp] [datetime] NOT NULL,
				[event_data] [nvarchar](max) NULL,
				[operation_id] [int] NULL,
				[user_id] [int] NULL,
			 CONSTRAINT [PK_dbo.audit_events_new] PRIMARY KEY CLUSTERED 
			(
				[id] ASC
			)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
			) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
		END

		-- Props tables
		IF (NOT EXISTS (SELECT * 
					 FROM INFORMATION_SCHEMA.TABLES 
					 WHERE TABLE_SCHEMA = 'dbo' 
					 AND  TABLE_NAME = 'audit_event_prop_names'))
		BEGIN
			CREATE TABLE [dbo].[audit_event_prop_names](
				[id] [int] IDENTITY(1,1) NOT NULL,
				[name] [nvarchar](max) NULL,
			 CONSTRAINT [PK_dbo.audit_event_prop_names] PRIMARY KEY CLUSTERED 
			(
				[id] ASC
			)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
			) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
		END

		IF (NOT EXISTS (SELECT * 
					 FROM INFORMATION_SCHEMA.TABLES 
					 WHERE TABLE_SCHEMA = 'dbo' 
					 AND  TABLE_NAME = 'audit_event_prop_vals'))
		BEGIN
			CREATE TABLE [dbo].[audit_event_prop_vals](
				[id] [int] IDENTITY(1,1) NOT NULL,
				[value] [nvarchar](max) NULL,
			 CONSTRAINT [PK_dbo.audit_event_prop_vals] PRIMARY KEY CLUSTERED 
			(
				[id] ASC
			)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
			) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
		END

		IF (NOT EXISTS (SELECT * 
					 FROM INFORMATION_SCHEMA.TABLES 
					 WHERE TABLE_SCHEMA = 'dbo' 
					 AND  TABLE_NAME = 'audit_event_exchange_props'))
		BEGIN
			CREATE TABLE [dbo].[audit_event_exchange_props](
				[id] [int] IDENTITY(1,1) NOT NULL,
				[prop_name_id] [int] NOT NULL,
				[prop_val_id] [int] NOT NULL,
				[event_id] [uniqueidentifier] NULL,
			 CONSTRAINT [PK_dbo.audit_event_exchange_props] PRIMARY KEY CLUSTERED 
			(
				[id] ASC
			)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
			) ON [PRIMARY]
		END

		IF (NOT EXISTS (SELECT * 
					 FROM INFORMATION_SCHEMA.TABLES 
					 WHERE TABLE_SCHEMA = 'dbo' 
					 AND  TABLE_NAME = 'audit_event_azure_ad_props'))
		BEGIN
			CREATE TABLE [dbo].[audit_event_azure_ad_props](
				[id] [int] IDENTITY(1,1) NOT NULL,
				[prop_name_id] [int] NOT NULL,
				[prop_val_id] [int] NOT NULL,
				[event_id] [uniqueidentifier] NULL,
			 CONSTRAINT [PK_dbo.audit_event_azure_ad_props] PRIMARY KEY CLUSTERED 
			(
				[id] ASC
			)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
			) ON [PRIMARY]
		END

		-- Foreign keys

		IF (NOT EXISTS (SELECT * 
			FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS 
			WHERE CONSTRAINT_NAME ='FK_audit_event_exchange_props_event_meta_exchange'))
		BEGIN

			ALTER TABLE dbo.audit_event_exchange_props ADD CONSTRAINT
				FK_audit_event_exchange_props_event_meta_exchange FOREIGN KEY
				(
				event_id
				) REFERENCES dbo.event_meta_exchange
				(
				event_id
				) ON UPDATE  NO ACTION 
				 ON DELETE  NO ACTION 
		END

		IF (NOT EXISTS (SELECT * 
			FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS 
			WHERE CONSTRAINT_NAME ='FK_audit_event_exchange_props_audit_event_prop_names'))
		BEGIN
			ALTER TABLE dbo.audit_event_exchange_props ADD CONSTRAINT
				FK_audit_event_exchange_props_audit_event_prop_names FOREIGN KEY
				(
				prop_name_id
				) REFERENCES dbo.audit_event_prop_names
				(
				id
				) ON UPDATE  NO ACTION 
				 ON DELETE  NO ACTION 
		END

		IF (NOT EXISTS (SELECT * 
			FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS 
			WHERE CONSTRAINT_NAME ='FK_audit_event_exchange_props_audit_event_prop_vals'))
		BEGIN
			ALTER TABLE dbo.audit_event_exchange_props ADD CONSTRAINT
				FK_audit_event_exchange_props_audit_event_prop_vals FOREIGN KEY
				(
				prop_val_id
				) REFERENCES dbo.audit_event_prop_vals
				(
				id
				) ON UPDATE  NO ACTION 
				 ON DELETE  NO ACTION
		END

		IF (NOT EXISTS (SELECT * 
			FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS 
			WHERE CONSTRAINT_NAME ='FK_audit_event_azure_ad_props_event_meta_azure_ad'))
		BEGIN
			ALTER TABLE dbo.audit_event_azure_ad_props ADD CONSTRAINT
				FK_audit_event_azure_ad_props_event_meta_azure_ad FOREIGN KEY
				(
				event_id
				) REFERENCES dbo.event_meta_azure_ad
				(
				event_id
				) ON UPDATE  NO ACTION 
				 ON DELETE  NO ACTION 
		END

		IF (NOT EXISTS (SELECT * 
			FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS 
			WHERE CONSTRAINT_NAME ='FK_audit_event_azure_ad_props_audit_event_prop_names'))
		BEGIN
			ALTER TABLE dbo.audit_event_azure_ad_props ADD CONSTRAINT
				FK_audit_event_azure_ad_props_audit_event_prop_names FOREIGN KEY
				(
				prop_name_id
				) REFERENCES dbo.audit_event_prop_names
				(
				id
				) ON UPDATE  NO ACTION 
				 ON DELETE  NO ACTION 
		END

		IF (NOT EXISTS (SELECT * 
			FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS 
			WHERE CONSTRAINT_NAME ='FK_audit_event_azure_ad_props_audit_event_prop_vals'))
		BEGIN
			ALTER TABLE dbo.audit_event_azure_ad_props ADD CONSTRAINT
				FK_audit_event_azure_ad_props_audit_event_prop_vals FOREIGN KEY
				(
				prop_val_id
				) REFERENCES dbo.audit_event_prop_vals
				(
				id
				) ON UPDATE  NO ACTION 
				 ON DELETE  NO ACTION 
		END

		-- Migrate data from old table structure to new



			print 'Migrating audit_events to audit_events_new...'

			EXEC('
			SET QUOTED_IDENTIFIER ON

			DECLARE @AuditEventCursor as CURSOR;

			DECLARE @id as uniqueidentifier;
			DECLARE @time_stamp as datetime;
			DECLARE @event_data as nvarchar(max);
			DECLARE @item_type_id as int;
			DECLARE @operation_id as int;
			DECLARE @user_id as int;

			DECLARE @url_id as int;
			DECLARE @workload_name as varchar(200);

			DECLARE @hitTimeStamp as datetime;

			DECLARE @related_web_id as int;
			DECLARE @file_extension_id as int;
			DECLARE @file_name_id as int;
			

			SET @AuditEventCursor = CURSOR FOR
					select 
						  audit_events.id
						, audit_events.time_stamp
						, audit_events.event_data
						, audit_events.item_type_id
						, audit_events.operation_id
						, audit_events.user_id 
						, audit_events.file_extension_id
						, audit_events.file_name_id
						, audit_events.url_id
						, event_workloads.workload_name
						, audit_events.event_data
						, audit_events.related_web_id
					from audit_events
					inner join event_workloads 
						on audit_events.workload_id = event_workloads.id

				open @AuditEventCursor

				FETCH NEXT FROM @AuditEventCursor INTO 
				@id, 
				@time_stamp, 
				@event_data, 
				@item_type_id, 
				@operation_id, 
				@user_id, 
				@file_extension_id,
				@file_name_id,
				@url_id,
				@workload_name,
				@event_data,
				@related_web_id;
	
				WHILE @@FETCH_STATUS = 0
			BEGIN
					-- Do something

					if exists(select 1 from audit_events_new where id = @id) begin

						print ''Already processed audit event''

					end
					else begin

						-- Insert common data
						insert into audit_events_new (id, time_stamp, event_data, operation_id, [user_id])
							values(@id, @time_stamp, @event_data, @operation_id, @user_id)

						-- Specialised workloads
						-- Ref: https://docs.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#sharepoint-base-schema

						if @workload_name = ''SharePoint'' or @workload_name = ''OneDrive'' begin

							if exists(select 1 from event_meta_sharepoint where event_id = @id) begin
								print ''Already processed SharePoint event '' + CAST(@id AS varchar(50)) 
							end
							else

								insert into event_meta_sharepoint 
									(event_id, file_extension_id, file_name_id, related_web_id, url_id, item_type_id)
								values
									(@id, @file_extension_id, @file_name_id, @related_web_id, @url_id, @item_type_id)

						end
						else if @workload_name = ''Exchange'' begin

							if exists(select 1 from event_meta_exchange where event_id = @id) begin
								print ''Already processed Exchange event '' + CAST(@id AS varchar(50))
							end
							else
								insert into event_meta_exchange
									(event_id)
								values
									(@id)
						end
						else if @workload_name = ''AzureActiveDirectory'' begin

							if exists(select 1 from event_meta_azure_ad where event_id = @id) begin
								print ''Already processed AzureAD event '' + CAST(@id AS varchar(50)) 
							end
							else
								insert into event_meta_azure_ad
									(event_id)
								values
									(@id)

						end
						else begin
							print ''Unknown audit-event workload '' + @workload_name + '' for id '' + CAST(@id AS varchar(50)) + ''. Leaving event with base data only.''
						
							if exists(select 1 from event_meta_general where event_id = @id) begin
								print ''Already processed generic event '' + CAST(@id AS varchar(50)) 
							end
							else
								insert into event_meta_general
									(event_id, workload)
								values
									(@id, @workload_name)
						end
					end --Does this ID exist already?

					-- Read next event
				FETCH NEXT FROM @AuditEventCursor INTO 
				@id, 
				@time_stamp, 
				@event_data, 
				@item_type_id, 
				@operation_id, 
				@user_id, 
				@file_extension_id,
				@file_name_id,
				@url_id,
				@workload_name,
				@event_data,
				@related_web_id;

			END

			DEALLOCATE @AuditEventCursor

			-- Delete old view
			drop view events_view')

			-- Delete old table
			drop table audit_events
			exec sp_rename 'dbo.audit_events_new', 'audit_events'
		

			-- Create new constraints now the audit table has been renamed.
			IF (NOT EXISTS (SELECT * 
				FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS 
				WHERE CONSTRAINT_NAME ='FK_event_meta_azure_ad_audit_events'))
			BEGIN
				ALTER TABLE dbo.event_meta_azure_ad ADD CONSTRAINT
				FK_event_meta_azure_ad_audit_events FOREIGN KEY
				(
				event_id
				) REFERENCES dbo.audit_events
				(
				id
				) ON UPDATE  NO ACTION 
				 ON DELETE  NO ACTION 
			END

			IF (NOT EXISTS (SELECT * 
				FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS 
				WHERE CONSTRAINT_NAME ='event_meta_exchange'))
			BEGIN
				ALTER TABLE dbo.event_meta_exchange ADD CONSTRAINT
				FK_event_meta_exchange_audit_events FOREIGN KEY
				(
				event_id
				) REFERENCES dbo.audit_events
				(
				id
				) ON UPDATE  NO ACTION 
				 ON DELETE  NO ACTION
			END
	
			IF (NOT EXISTS (SELECT * 
				FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS 
				WHERE CONSTRAINT_NAME ='event_meta_general'))
			BEGIN
				ALTER TABLE dbo.event_meta_general ADD CONSTRAINT
				FK_event_meta_general_audit_events FOREIGN KEY
				(
				event_id
				) REFERENCES dbo.audit_events
				(
				id
				) ON UPDATE  NO ACTION 
				 ON DELETE  NO ACTION 

				 ALTER TABLE dbo.event_meta_sharepoint ADD CONSTRAINT
				FK_event_meta_sharepoint_audit_events FOREIGN KEY
				(
				event_id
				) REFERENCES dbo.audit_events
				(
				id
				) ON UPDATE  NO ACTION 
				 ON DELETE  NO ACTION 
			END

			-- Delete old workloads table. General events has a workload column & each main workload its' own table
			DROP TABLE [dbo].[event_workloads]

		commit tran AuditUpgrade;
		print 'Schema upgraded.'

	END TRY
	BEGIN CATCH
		print 'Got an error upgrading schema - check results for more info. Transaction rolled-back.'
		 SELECT  
			ERROR_NUMBER() AS ErrorNumber  
			,ERROR_SEVERITY() AS ErrorSeverity  
			,ERROR_STATE() AS ErrorState  
			,ERROR_PROCEDURE() AS ErrorProcedure  
			,ERROR_LINE() AS ErrorLine  
			,ERROR_MESSAGE() AS ErrorMessage;  

		rollback tran AuditUpgrade;
	END CATCH
end
Go


	
-- Create workload specific views

IF EXISTS(select * FROM sys.views where name = 'events_view_sharepoint')
begin
	DROP VIEW [dbo].events_view_sharepoint
end
go
CREATE VIEW dbo.events_view_sharepoint AS

SELECT audit_events.id
		,[user_id]
		,[users].[user_name]
		,url_id
		,[urls].full_url
		,operation_id
		,event_operations.operation_name as operation
		,item_type_id
		,event_types.[type_name] 
		,file_extension_id
		,event_file_ext.extension_name as file_extention
		,file_name_id
		,event_file_names.[file_name]
		,time_stamp
	FROM [dbo].audit_events
	inner join users on 
	audit_events.[user_id] = users.id
	inner join event_meta_sharepoint on
	event_meta_sharepoint.event_id = audit_events.id
	left join urls on 
	event_meta_sharepoint.url_id = urls.id
	left join event_file_ext on -- Not always a file 
	event_meta_sharepoint.file_extension_id = event_file_ext.id
	left join event_file_names on -- Not always a file
	event_meta_sharepoint.file_name_id = event_file_names.id
	left join event_types on 
	event_meta_sharepoint.item_type_id = event_types.id
	inner join event_operations on
		audit_events.operation_id = event_operations.id
Go

IF EXISTS(select * FROM sys.views where name = 'events_view_azure_ad')
begin
	DROP VIEW [dbo].events_view_azure_ad
end
go
CREATE VIEW [dbo].events_view_azure_ad AS

SELECT audit_events.id
		,[user_id]
		,[users].[user_name]
		,operation_id
		,event_operations.operation_name as operation
		,time_stamp
		,(select count(event_id) from audit_event_azure_ad_props where audit_event_azure_ad_props.event_id = audit_events.id) as extended_properties_count
	FROM [dbo].audit_events
	inner join users on 
	audit_events.[user_id] = users.id
	inner join event_meta_azure_ad on
	event_meta_azure_ad.event_id = audit_events.id
	inner join event_operations on
		audit_events.operation_id = event_operations.id
Go

IF EXISTS(select * FROM sys.views where name = 'events_view_exchange')
begin
	DROP VIEW [dbo].events_view_exchange
end
Go
CREATE VIEW [dbo].events_view_exchange AS

SELECT audit_events.id
		,[user_id]
		,[users].[user_name]
		,operation_id
		,event_operations.operation_name as operation
		,time_stamp
		,(select count(event_id) from audit_event_exchange_props where audit_event_exchange_props.event_id = audit_events.id) as extended_properties_count
	FROM [dbo].audit_events
	inner join users on 
	audit_events.[user_id] = users.id
	inner join event_meta_exchange on
	event_meta_exchange.event_id = audit_events.id
	inner join event_operations on
		audit_events.operation_id = event_operations.id

Go

IF EXISTS(select * FROM sys.views where name = 'events_view_general')
begin
	DROP VIEW [dbo].events_view_general
end
Go
CREATE VIEW [dbo].events_view_general AS

SELECT audit_events.id
		,[user_id]
		,[users].[user_name]
		,operation_id
		,event_operations.operation_name as operation
		,time_stamp
	FROM [dbo].audit_events
	inner join users on 
	audit_events.[user_id] = users.id
	inner join event_meta_general on
	event_meta_general.event_id = audit_events.id
	inner join event_operations on
		audit_events.operation_id = event_operations.id
Go
	
print 'Migration to refactored audit-log tables successful.'

