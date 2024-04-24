namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class EnhancedWebTracking : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.hits_clicked_element_class_names",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    class_names = c.String(maxLength: 2000),
                })
                .PrimaryKey(t => t.id)
                .Index(t => t.class_names, unique: true);

            CreateTable(
                "dbo.hits_clicked_element_titles",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);

            CreateTable(
                "dbo.hits_clicked_elements",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    url_id = c.Int(),
                    element_title_id = c.Int(),
                    class_names_id = c.Int(),
                    hit_id = c.Int(nullable: false),
                    timestamp = c.DateTime(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.hits_clicked_element_class_names", t => t.class_names_id)
                .ForeignKey("dbo.hits", t => t.hit_id, cascadeDelete: true)
                .ForeignKey("dbo.hits_clicked_element_titles", t => t.element_title_id)
                .ForeignKey("dbo.urls", t => t.url_id)
                .Index(t => new { t.hit_id, t.url_id, t.timestamp }, unique: true)
                .Index(t => t.element_title_id)
                .Index(t => t.class_names_id);

            CreateTable(
                "dbo.file_metadata_property_values",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    url_id = c.Int(nullable: false),
                    field_id = c.Int(nullable: false),
                    field_value = c.String(),
                    tag_guid = c.Guid(),
                    updated = c.DateTime(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.file_field_definitions", t => t.field_id, cascadeDelete: true)
                .ForeignKey("dbo.urls", t => t.url_id, cascadeDelete: true)
                .Index(t => new { t.url_id, t.field_id }, unique: true);

            CreateTable(
                "dbo.file_field_definitions",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);

            AddColumn("dbo.urls", "file_last_refreshed", c => c.DateTime());

            Console.WriteLine("DB SCHEMA: Applied 'Enhanced Web Tracking' succesfully.");
        }

        public override void Down()
        {
            DropForeignKey("dbo.hits_clicked_elements", "url_id", "dbo.urls");
            DropForeignKey("dbo.hits_clicked_elements", "element_title_id", "dbo.hits_clicked_element_titles");
            DropForeignKey("dbo.hits_clicked_elements", "hit_id", "dbo.hits");
            DropForeignKey("dbo.file_metadata_property_values", "url_id", "dbo.urls");
            DropForeignKey("dbo.file_metadata_property_values", "field_id", "dbo.file_field_definitions");
            DropForeignKey("dbo.hits_clicked_elements", "class_names_id", "dbo.hits_clicked_element_class_names");
            DropIndex("dbo.file_field_definitions", new[] { "name" });
            DropIndex("dbo.file_metadata_property_values", new[] { "url_id", "field_id" });
            DropIndex("dbo.hits_clicked_elements", new[] { "class_names_id" });
            DropIndex("dbo.hits_clicked_elements", new[] { "element_title_id" });
            DropIndex("dbo.hits_clicked_elements", new[] { "hit_id", "url_id", "timestamp" });
            DropIndex("dbo.hits_clicked_element_titles", new[] { "name" });
            DropIndex("dbo.hits_clicked_element_class_names", new[] { "class_names" });
            DropColumn("dbo.urls", "file_last_refreshed");
            DropTable("dbo.file_field_definitions");
            DropTable("dbo.file_metadata_property_values");
            DropTable("dbo.hits_clicked_elements");
            DropTable("dbo.hits_clicked_element_titles");
            DropTable("dbo.hits_clicked_element_class_names");
        }
    }
}
