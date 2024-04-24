
-- --------------------------------------------------
-- Entity Designer DDL Script for SQL Server 2005, 2008, 2012 and Azure
-- --------------------------------------------------
-- Date Created: 12/09/2017 19:20:09
-- Generated from EDMX file: C:\Users\sambetts\Source\Repos\SPOInsights VS Projects\Common.Entities\SPOInsightsActivityModel.edmx
-- --------------------------------------------------

SET QUOTED_IDENTIFIER OFF;
GO

IF SCHEMA_ID(N'dbo') IS NULL EXECUTE(N'CREATE SCHEMA [dbo]');
GO

-- --------------------------------------------------
-- Dropping existing FOREIGN KEY constraints
-- --------------------------------------------------

IF OBJECT_ID(N'[dbo].[audit_events]', 'U') IS NOT NULL begin

	RAISERROR ('Database is already populated with tables; creating views only. Manual setup may be required', 1, 1);
	Goto Skipper 
end

-- --------------------------------------------------
-- Creating all tables
-- --------------------------------------------------

-- Creating table 'audit_events'
CREATE TABLE [dbo].[audit_events] (
    [id] uniqueidentifier  NOT NULL,
    [user_id] int  NOT NULL,
    [url_id] int  NULL,
    [operation_id] int  NOT NULL,
    [item_type_id] int  NULL,
    [file_extension_id] int  NULL,
    [file_name_id] int  NULL,
    [time_stamp] datetime  NOT NULL,
    [workload_id] int  NOT NULL
);


-- Creating table 'browsers'
CREATE TABLE [dbo].[browsers] (
    [id] int IDENTITY(1,1) NOT NULL,
    [browser_name] varchar(250)  NULL
);


-- Creating table 'cities'
CREATE TABLE [dbo].[cities] (
    [id] int IDENTITY(1,1) NOT NULL,
    [city_name] varchar(250)  NOT NULL
);


-- Creating table 'countries'
CREATE TABLE [dbo].[countries] (
    [id] int IDENTITY(1,1) NOT NULL,
    [country_name] varchar(250)  NOT NULL
);


-- Creating table 'devices'
CREATE TABLE [dbo].[devices] (
    [id] int IDENTITY(1,1) NOT NULL,
    [device_name] varchar(200)  NOT NULL
);


-- Creating table 'event_file_ext'
CREATE TABLE [dbo].[event_file_ext] (
    [id] int IDENTITY(1,1) NOT NULL,
    [extension_name] varchar(250)  NOT NULL
);


-- Creating table 'event_file_names'
CREATE TABLE [dbo].[event_file_names] (
    [id] int IDENTITY(1,1) NOT NULL,
    [file_name] varchar(250)  NOT NULL
);


-- Creating table 'event_operations'
CREATE TABLE [dbo].[event_operations] (
    [id] int IDENTITY(1,1) NOT NULL,
    [operation_name] varchar(250)  NOT NULL
);


-- Creating table 'event_types'
CREATE TABLE [dbo].[event_types] (
    [id] int IDENTITY(1,1) NOT NULL,
    [type_name] varchar(250)  NOT NULL
);


-- Creating table 'event_workloads'
CREATE TABLE [dbo].[event_workloads] (
    [id] int IDENTITY(1,1) NOT NULL,
    [workload_name] varchar(100)  NOT NULL
);


-- Creating table 'hits'
CREATE TABLE [dbo].[hits] (
    [id] int IDENTITY(1,1) NOT NULL,
    [url_id] int  NOT NULL,
    [hit_timestamp] datetime  NOT NULL,
    [location_province_id] int  NULL,
    [agent_id] int  NULL,
    [session_id] int  NULL,
    [page_title_id] int  NULL,
    [seconds_on_page] float  NULL,
    [device_id] int  NULL,
    [os_id] int  NULL,
    [city_id] int  NULL,
    [country_id] int  NULL,
    [page_request_id] uniqueidentifier  NOT NULL
);


-- Creating table 'import_log'
CREATE TABLE [dbo].[import_log] (
    [id] int IDENTITY(1,1) NOT NULL,
    [import_message] varchar(250)  NOT NULL,
    [contents] varchar(max)  NOT NULL,
    [machine_name] varchar(50)  NOT NULL,
    [time_stamp] datetime  NOT NULL,
    [hit_id] int  NULL,
    [event_id] uniqueidentifier  NULL,
    [search_id] int  NULL
);


-- Creating table 'operating_systems'
CREATE TABLE [dbo].[operating_systems] (
    [id] int IDENTITY(1,1) NOT NULL,
    [os_name] varchar(200)  NOT NULL
);


-- Creating table 'org_urls'
CREATE TABLE [dbo].[org_urls] (
    [id] int IDENTITY(1,1) NOT NULL,
    [url_base] nvarchar(500)  NULL,
    [org_id] int  NULL
);


-- Creating table 'orgs'
CREATE TABLE [dbo].[orgs] (
    [id] int IDENTITY(1,1) NOT NULL,
    [org_name] nvarchar(250)  NULL
);


-- Creating table 'page_titles'
CREATE TABLE [dbo].[page_titles] (
    [id] int IDENTITY(1,1) NOT NULL,
    [title] nvarchar(250)  NOT NULL
);


-- Creating table 'processed_audit_events'
CREATE TABLE [dbo].[processed_audit_events] (
    [event_id] uniqueidentifier  NOT NULL,
    [processed_timestamp] datetime  NOT NULL
);


-- Creating table 'provinces'
CREATE TABLE [dbo].[provinces] (
    [id] int IDENTITY(1,1) NOT NULL,
    [province_name] varchar(250)  NOT NULL
);


-- Creating table 'search_terms'
CREATE TABLE [dbo].[search_terms] (
    [id] int IDENTITY(1,1) NOT NULL,
    [search_term] nvarchar(250)  NOT NULL
);


-- Creating table 'searches'
CREATE TABLE [dbo].[searches] (
    [id] int IDENTITY(1,1) NOT NULL,
    [session_id] int  NOT NULL,
    [search_term_id] int  NOT NULL
);


-- Creating table 'sessions'
CREATE TABLE [dbo].[sessions] (
    [id] int IDENTITY(1,1) NOT NULL,
    [ai_session_id] varchar(50)  NOT NULL,
    [user_id] int  NOT NULL
);


-- Creating table 'urls'
CREATE TABLE [dbo].[urls] (
    [id] int IDENTITY(1,1) NOT NULL,
    [full_url] varchar(max)  NOT NULL
);


-- Creating table 'users'
CREATE TABLE [dbo].[users] (
    [id] int IDENTITY(1,1) NOT NULL,
    [user_name] varchar(250)  NOT NULL,
	org_id int NULL
);


-- --------------------------------------------------
-- Creating all PRIMARY KEY constraints
-- --------------------------------------------------

-- Creating primary key on [id] in table 'audit_events'
ALTER TABLE [dbo].[audit_events]
ADD CONSTRAINT [PK_audit_events]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'browsers'
ALTER TABLE [dbo].[browsers]
ADD CONSTRAINT [PK_browsers]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'cities'
ALTER TABLE [dbo].[cities]
ADD CONSTRAINT [PK_cities]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'countries'
ALTER TABLE [dbo].[countries]
ADD CONSTRAINT [PK_countries]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'devices'
ALTER TABLE [dbo].[devices]
ADD CONSTRAINT [PK_devices]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'event_file_ext'
ALTER TABLE [dbo].[event_file_ext]
ADD CONSTRAINT [PK_event_file_ext]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'event_file_names'
ALTER TABLE [dbo].[event_file_names]
ADD CONSTRAINT [PK_event_file_names]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'event_operations'
ALTER TABLE [dbo].[event_operations]
ADD CONSTRAINT [PK_event_operations]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'event_types'
ALTER TABLE [dbo].[event_types]
ADD CONSTRAINT [PK_event_types]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'event_workloads'
ALTER TABLE [dbo].[event_workloads]
ADD CONSTRAINT [PK_event_workloads]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'hits'
ALTER TABLE [dbo].[hits]
ADD CONSTRAINT [PK_hits]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'import_log'
ALTER TABLE [dbo].[import_log]
ADD CONSTRAINT [PK_import_log]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'operating_systems'
ALTER TABLE [dbo].[operating_systems]
ADD CONSTRAINT [PK_operating_systems]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'org_urls'
ALTER TABLE [dbo].[org_urls]
ADD CONSTRAINT [PK_org_urls]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'orgs'
ALTER TABLE [dbo].[orgs]
ADD CONSTRAINT [PK_orgs]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'page_titles'
ALTER TABLE [dbo].[page_titles]
ADD CONSTRAINT [PK_page_titles]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [event_id] in table 'processed_audit_events'
ALTER TABLE [dbo].[processed_audit_events]
ADD CONSTRAINT [PK_processed_audit_events]
    PRIMARY KEY CLUSTERED ([event_id] ASC);


-- Creating primary key on [id] in table 'provinces'
ALTER TABLE [dbo].[provinces]
ADD CONSTRAINT [PK_provinces]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'search_terms'
ALTER TABLE [dbo].[search_terms]
ADD CONSTRAINT [PK_search_terms]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'searches'
ALTER TABLE [dbo].[searches]
ADD CONSTRAINT [PK_searches]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'sessions'
ALTER TABLE [dbo].[sessions]
ADD CONSTRAINT [PK_sessions]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'urls'
ALTER TABLE [dbo].[urls]
ADD CONSTRAINT [PK_urls]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- Creating primary key on [id] in table 'users'
ALTER TABLE [dbo].[users]
ADD CONSTRAINT [PK_users]
    PRIMARY KEY CLUSTERED ([id] ASC);


-- --------------------------------------------------
-- Creating all FOREIGN KEY constraints
-- --------------------------------------------------

--Hacked in 

-- Creating foreign key on [workload_id] in table 'audit_events'
ALTER TABLE [dbo].[audit_events]
ADD CONSTRAINT [FK_audit_events_event_workloads]
    FOREIGN KEY ([workload_id])
    REFERENCES [dbo].[event_workloads]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_audit_events_event_workloads'
CREATE INDEX [IX_FK_audit_events_event_workloads]
ON [dbo].[audit_events]
    ([workload_id]);


-- Creating foreign key on [file_extension_id] in table 'audit_events'
ALTER TABLE [dbo].[audit_events]
ADD CONSTRAINT [FK_events_event_file_ext]
    FOREIGN KEY ([file_extension_id])
    REFERENCES [dbo].[event_file_ext]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_events_event_file_ext'
CREATE INDEX [IX_FK_events_event_file_ext]
ON [dbo].[audit_events]
    ([file_extension_id]);


-- Creating foreign key on [file_name_id] in table 'audit_events'
ALTER TABLE [dbo].[audit_events]
ADD CONSTRAINT [FK_events_event_filenames]
    FOREIGN KEY ([file_name_id])
    REFERENCES [dbo].[event_file_names]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_events_event_filenames'
CREATE INDEX [IX_FK_events_event_filenames]
ON [dbo].[audit_events]
    ([file_name_id]);


-- Creating foreign key on [operation_id] in table 'audit_events'
ALTER TABLE [dbo].[audit_events]
ADD CONSTRAINT [FK_events_event_operations]
    FOREIGN KEY ([operation_id])
    REFERENCES [dbo].[event_operations]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_events_event_operations'
CREATE INDEX [IX_FK_events_event_operations]
ON [dbo].[audit_events]
    ([operation_id]);


-- Creating foreign key on [item_type_id] in table 'audit_events'
ALTER TABLE [dbo].[audit_events]
ADD CONSTRAINT [FK_events_event_types]
    FOREIGN KEY ([item_type_id])
    REFERENCES [dbo].[event_types]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_events_event_types'
CREATE INDEX [IX_FK_events_event_types]
ON [dbo].[audit_events]
    ([item_type_id]);


-- Creating foreign key on [url_id] in table 'audit_events'
ALTER TABLE [dbo].[audit_events]
ADD CONSTRAINT [FK_events_urls]
    FOREIGN KEY ([url_id])
    REFERENCES [dbo].[urls]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_events_urls'
CREATE INDEX [IX_FK_events_urls]
ON [dbo].[audit_events]
    ([url_id]);


-- Creating foreign key on [user_id] in table 'audit_events'
ALTER TABLE [dbo].[audit_events]
ADD CONSTRAINT [FK_events_users]
    FOREIGN KEY ([user_id])
    REFERENCES [dbo].[users]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_events_users'
CREATE INDEX [IX_FK_events_users]
ON [dbo].[audit_events]
    ([user_id]);


-- Creating foreign key on [agent_id] in table 'hits'
ALTER TABLE [dbo].[hits]
ADD CONSTRAINT [FK_hits_user_agents]
    FOREIGN KEY ([agent_id])
    REFERENCES [dbo].[browsers]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_hits_user_agents'
CREATE INDEX [IX_FK_hits_user_agents]
ON [dbo].[hits]
    ([agent_id]);


-- Creating foreign key on [city_id] in table 'hits'
ALTER TABLE [dbo].[hits]
ADD CONSTRAINT [FK_hits_cities]
    FOREIGN KEY ([city_id])
    REFERENCES [dbo].[cities]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_hits_cities'
CREATE INDEX [IX_FK_hits_cities]
ON [dbo].[hits]
    ([city_id]);


-- Creating foreign key on [country_id] in table 'hits'
ALTER TABLE [dbo].[hits]
ADD CONSTRAINT [FK_hits_countries]
    FOREIGN KEY ([country_id])
    REFERENCES [dbo].[countries]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_hits_countries'
CREATE INDEX [IX_FK_hits_countries]
ON [dbo].[hits]
    ([country_id]);


-- Creating foreign key on [device_id] in table 'hits'
ALTER TABLE [dbo].[hits]
ADD CONSTRAINT [FK_hits_devices]
    FOREIGN KEY ([device_id])
    REFERENCES [dbo].[devices]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_hits_devices'
CREATE INDEX [IX_FK_hits_devices]
ON [dbo].[hits]
    ([device_id]);


-- Creating foreign key on [page_title_id] in table 'hits'
ALTER TABLE [dbo].[hits]
ADD CONSTRAINT [FK_hits_hit_page_title]
    FOREIGN KEY ([page_title_id])
    REFERENCES [dbo].[page_titles]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_hits_hit_page_title'
CREATE INDEX [IX_FK_hits_hit_page_title]
ON [dbo].[hits]
    ([page_title_id]);


-- Creating foreign key on [os_id] in table 'hits'
ALTER TABLE [dbo].[hits]
ADD CONSTRAINT [FK_hits_operating_systems]
    FOREIGN KEY ([os_id])
    REFERENCES [dbo].[operating_systems]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_hits_operating_systems'
CREATE INDEX [IX_FK_hits_operating_systems]
ON [dbo].[hits]
    ([os_id]);


-- Creating foreign key on [session_id] in table 'hits'
ALTER TABLE [dbo].[hits]
ADD CONSTRAINT [FK_hits_sessions]
    FOREIGN KEY ([session_id])
    REFERENCES [dbo].[sessions]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_hits_sessions'
CREATE INDEX [IX_FK_hits_sessions]
ON [dbo].[hits]
    ([session_id]);


-- Creating foreign key on [url_id] in table 'hits'
ALTER TABLE [dbo].[hits]
ADD CONSTRAINT [FK_hits_urls]
    FOREIGN KEY ([url_id])
    REFERENCES [dbo].[urls]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_hits_urls'
CREATE INDEX [IX_FK_hits_urls]
ON [dbo].[hits]
    ([url_id]);


-- Creating foreign key on [location_province_id] in table 'hits'
ALTER TABLE [dbo].[hits]
ADD CONSTRAINT [FK_hits_user_provinces]
    FOREIGN KEY ([location_province_id])
    REFERENCES [dbo].[provinces]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_hits_user_provinces'
CREATE INDEX [IX_FK_hits_user_provinces]
ON [dbo].[hits]
    ([location_province_id]);


-- Creating foreign key on [org_id] in table 'org_urls'
ALTER TABLE [dbo].[org_urls]
ADD CONSTRAINT [FK_org_urls_orgs]
    FOREIGN KEY ([org_id])
    REFERENCES [dbo].[orgs]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_org_urls_orgs'
CREATE INDEX [IX_FK_org_urls_orgs]
ON [dbo].[org_urls]
    ([org_id]);


-- Creating foreign key on [search_term_id] in table 'searches'
ALTER TABLE [dbo].[searches]
ADD CONSTRAINT [FK_searches_search_terms]
    FOREIGN KEY ([search_term_id])
    REFERENCES [dbo].[search_terms]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_searches_search_terms'
CREATE INDEX [IX_FK_searches_search_terms]
ON [dbo].[searches]
    ([search_term_id]);


-- Creating foreign key on [session_id] in table 'searches'
ALTER TABLE [dbo].[searches]
ADD CONSTRAINT [FK_searches_sessions]
    FOREIGN KEY ([session_id])
    REFERENCES [dbo].[sessions]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_searches_sessions'
CREATE INDEX [IX_FK_searches_sessions]
ON [dbo].[searches]
    ([session_id]);


-- Creating foreign key on [user_id] in table 'sessions'
ALTER TABLE [dbo].[sessions]
ADD CONSTRAINT [FK_sessions_users]
    FOREIGN KEY ([user_id])
    REFERENCES [dbo].[users]
        ([id])
    ON DELETE NO ACTION ON UPDATE NO ACTION;


-- Creating non-clustered index for FOREIGN KEY 'FK_sessions_users'
CREATE INDEX [IX_FK_sessions_users]
ON [dbo].[sessions]
    ([user_id]);







-- --------------------------------------------------
-- Create indexes
-- --------------------------------------------------


-- Lookups
CREATE UNIQUE NONCLUSTERED INDEX IX_search ON dbo.search_terms
	(
	[search_term]
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY];


CREATE UNIQUE NONCLUSTERED INDEX IX_cities ON dbo.cities
	(
	city_name
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]


CREATE UNIQUE NONCLUSTERED INDEX IX_countries ON dbo.countries
	(
	country_name
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]


CREATE UNIQUE NONCLUSTERED INDEX IX_devices ON dbo.devices
	(
	device_name
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]


CREATE UNIQUE NONCLUSTERED INDEX IX_users ON dbo.users
	(
	[user_name]
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]


CREATE UNIQUE NONCLUSTERED INDEX IX_browsers ON dbo.browsers
	(
	browser_name
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]


CREATE UNIQUE NONCLUSTERED INDEX IX_page_titles ON dbo.page_titles
	(
	title
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]


CREATE UNIQUE NONCLUSTERED INDEX IX_provinces ON dbo.provinces
	(
	province_name
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]


-- Orgs
CREATE UNIQUE NONCLUSTERED INDEX IX_orgs ON dbo.orgs
	(
	org_name
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]


CREATE UNIQUE NONCLUSTERED INDEX IX_org_urls ON dbo.org_urls
	(
	url_base
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]


CREATE UNIQUE NONCLUSTERED INDEX IX_operating_systems ON dbo.operating_systems
	(
	os_name
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]



-- Events
CREATE UNIQUE NONCLUSTERED INDEX IX_event_types ON dbo.event_types
	(
	[type_name]
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]


CREATE UNIQUE NONCLUSTERED INDEX IX_event_operations ON dbo.event_operations
	(
	operation_name
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]


CREATE UNIQUE NONCLUSTERED INDEX IX_event_filenames ON dbo.event_file_names
	(
	[file_name]
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]


CREATE UNIQUE NONCLUSTERED INDEX IX_event_file_ext ON dbo.event_file_ext
	(
	extension_name
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]



--------------------------------------------------------------
--- Insert default org
--------------------------------------------------------------

insert into dbo.orgs (org_name) values ('Default org')
	

Skipper:

--------------------------------------------------------------
--- Create VIEWS
--------------------------------------------------------------


IF object_id(N'dbo.events_view', 'V') IS NOT NULL
	DROP VIEW dbo.events_view
GO

CREATE VIEW dbo.events_view AS


SELECT audit_events.id
      ,[user_id]
	  ,[users].[user_name]
	  ,audit_events.workload_id
	  ,[event_workloads].[workload_name]
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
  left join urls on 
	audit_events.url_id = urls.id
  left join event_file_ext on -- Not always a file 
	audit_events.file_extension_id = event_file_ext.id
  left join event_file_names on -- Not always a file
	audit_events.file_name_id = event_file_names.id
  left join event_types on 
	audit_events.item_type_id = event_types.id
	inner join event_operations on
		audit_events.operation_id = event_operations.id
	inner join event_workloads on
		audit_events.workload_id = event_workloads.id


go


IF object_id(N'dbo.hits_view', 'V') IS NOT NULL
	DROP VIEW [dbo].[hits_view]
GO

CREATE VIEW [dbo].[hits_view] AS


	select hits.id, full_url, [users].user_name, hits.session_id, ai_session_id, seconds_on_page, hit_timestamp
		from hits 
	inner join urls on hits.url_id = urls.id
	inner join [sessions] on hits.session_id = [sessions].id
	inner join [users] on [sessions].user_id = users.id

GO



IF object_id(N'session_info', 'V') IS NOT NULL
	DROP VIEW session_info
GO

CREATE VIEW session_info AS

select 
	s.id
	,ai_session_id
	-- ENTRANCE
	,(
		select top 1 h.id		-- ID
		from hits h
		where h.session_id = s.id
		order by [hit_timestamp] asc
	) as entrance_hit_id
	,(
		select top 1 page_titles.title	-- Page Title
		from hits h
			inner join page_titles on h.page_title_id = page_titles.id
		where h.session_id = s.id
		order by [hit_timestamp] asc
	) as entrance_hit_title
	,(
		select top 1 h.hit_timestamp	-- Hit timestamp
		from hits h
			inner join urls on h.url_id = urls.id
		where h.session_id = s.id
		order by [hit_timestamp] asc
	) as entrance_hit_time_stamp
	-- EXIT
	,(
		select top 1 h.id		-- ID
		from hits h
		where h.session_id = s.id
		order by [hit_timestamp] desc
	) as exit_hit_id
	,(
		select top 1 page_titles.title		--Title
		from hits h
			inner join page_titles on h.page_title_id = page_titles.id
		where h.session_id = s.id
		order by [hit_timestamp] desc
	) as exit_hit_title
	,(
		select top 1 h.hit_timestamp	--Timestamp
		from hits h
			inner join urls on h.url_id = urls.id
		where h.session_id = s.id
		order by [hit_timestamp] desc
	) as exit_hit_time_stamp
	
from [sessions] s

Go

IF object_id(N'url_stats', 'V') IS NOT NULL
	DROP VIEW url_stats
GO

CREATE VIEW url_stats AS

	select 
		urls.id as url_id
		,full_url
		,(
			select count(DISTINCT user_id) from hits 
			inner join [sessions] on hits.session_id = [sessions].id
			where url_id = urls.id
		) url_unqiue_hit_count
		,(
			select count(user_id) from hits 
			inner join [sessions] on hits.session_id = [sessions].id
			where url_id = urls.id
		) url_all_hit_count
	from urls

GO

