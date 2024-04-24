using App.ControlPanel.Engine.SharePointModelBuilder;
using CloudInstallEngine;
using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using OfficeDevPnP.Core.Framework.Provisioning.Model;
using OfficeDevPnP.Core.Framework.Provisioning.ObjectHandlers;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks.Adoptify
{
    /// <summary>
    /// Deploys SharePoint site & returns list info
    /// </summary>
    public class AdoptifySiteProvisionTask : BaseInstallTask
    {
        private readonly ClientContext _clientContext;

        public AdoptifySiteProvisionTask(TaskConfig config, ILogger logger, ClientContext clientContext) : base(config, logger)
        {
            _clientContext = clientContext;
        }

        private void ApplyProvisioningTemplate(ClientContext ctx, ProvisioningTemplate template)
        {
            ctx.RequestTimeout = Timeout.Infinite;

            var web = ctx.Web;

            var ptai = new ProvisioningTemplateApplyingInformation();
            ptai.ProgressDelegate = delegate (String message, Int32 progress, Int32 total)
            {
                _logger.LogInformation("SPSite deploy: {0:00}/{1:00} - {2}", progress, total, message);
            };

            web.ApplyProvisioningTemplate(template, ptai);

        }

        public override async Task<object> ExecuteTask(object contextArg)
        {
            if (_clientContext == null)
            {
                throw new AdoptifyInstallException("No client context");
            }

            _clientContext.Load(_clientContext.Site, s => s.Url);
            await _clientContext.ExecuteQueryAsync();

            // Build lists
            _clientContext.Load(_clientContext.Web, w => w.Url);
            await _clientContext.ExecuteQueryAsync();
            _logger.LogInformation($"Deploying SharePoint site schema to {_clientContext.Web.Url}");
            ApplyProvisioningTemplate(_clientContext, GetAdoptifyTemplate());

            _clientContext.Web.Lists.EnsureSiteAssetsLibrary();
            await _clientContext.ExecuteQueryAsync();

            _logger.LogInformation($"SharePoint site provisioning complete");

            return null;
        }

        /// <summary>
        /// Build list definitions. Use hard-coded IDs for fields (generated randomly online).
        /// </summary>
        private ProvisioningTemplate GetAdoptifyTemplate()
        {
            var template = new ProvisioningTemplate();

            // Adoptify Information
            template.Lists.Add(SiteBuilder.BuildGenericList("Adoptify Information", "lists/AdoptifyInformation", false, new SPField[]
            {
                new TextField { ID = Guid.Parse("4591e1d8-b849-4d34-b2c3-0dbdcb1450db"), Name = "Value", Title = "Value", IncludeInDefaultView = true },
            }));

            // Badges
            template.Lists.Add(SiteBuilder.BuildGenericList("Badges", "Lists/Badges", true, new SPField[]
            {
                new TextField { ID = Guid.Parse("8eb856c1-5e0d-4f25-9837-3625f0662bee"),        Name = "BadgeName", Title = "Badge Name", IncludeInDefaultView = true },
                new TextField { ID = Guid.Parse("162207b5-016b-469b-8925-27907303f85d"),        Name = "BadgeDescription", Title = "Badge Description", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("09ab5fb7-a608-4d30-aa9f-869d696e99b9"),         Name = "Points", Title = "Points", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("6def1ca4-1708-46e4-bd16-55d5c75e9bea"),        Name = "BadgeOrder", Title = "Badge Order", IncludeInDefaultView = true },
                new ThumbnailField { ID = Guid.Parse("693a4fd3-446c-4923-b9ae-9cd6d772331c"),   Name = "BadgeImage", Title = "Badge Image", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("09ab5fb7-a608-4d30-aa9f-869d696e99c9"),         Name = "Awarded", Title = "Awarded", IncludeInDefaultView = false },
                new NoteField { ID = Guid.Parse("3019ef3d-ad02-4bc9-96d3-c9ba6473930d"),        Name = "BadgeAdaptiveImage", Title = "Badge Adaptive Image", IncludeInDefaultView = true },
                new ChoiceField{ ID = Guid.Parse("3019ef3d-ad02-4bc9-96d3-c9ba6473930e"),       Name = "BadgeStatus", Title = "Badge Status", Choices = new string[]{ "Active", "Draft", "Retired" }, Default = "Active", IncludeInDefaultView = true },
                new IntField {ID = Guid.Parse("c7a11afa-6208-4908-be10-e863c390f24f"),          Name = "QuestID", Title = "Quest ID", IncludeInDefaultView = true },
                new ChoiceField{ ID = Guid.Parse("36f048aa-ec1a-477c-8778-5e77ec3f93a5"),       Name = "BadgeType", Title = "Badge Type", Choices = new string[]{ "XP", "Quest" }, Default = "XP", IncludeInDefaultView = true }
            }));

            // Levels
            template.Lists.Add(SiteBuilder.BuildGenericList("Levels", "Lists/Levels", true, new SPField[]
            {
                new IntField {ID = Guid.Parse("77e52e22-1e13-411c-8265-811a9d0468e7"),          Name = "XP", Title = "XP", IncludeInDefaultView = true },
                new ThumbnailField {ID = Guid.Parse("4ccbfe99-fa4c-4668-88ae-eba5ab1ffe3f"),    Name = "LevelImage", Title = "Level Image", IncludeInDefaultView = true  },
                new NoteField {ID = Guid.Parse("2bc6891f-173b-4302-8bd1-26fd743cae5b"),         Name = "LevelAdaptiveImage", Title = "Level Adaptive Image", IncludeInDefaultView = true  },
                new ChoiceField {ID = Guid.Parse("9cdd80e6-29a0-48c9-9e06-f895de423591"),       Name = "LevelStatus", Title = "Level Status", Choices = new string[]{ "Active", "Draft" }, Default = "Active", IncludeInDefaultView = true  },
                new IntField {ID = Guid.Parse("23c8f1e9-02f3-4d6a-8f4f-7ee6aabb2f21"),          Name = "Achieved", Title = "Achieved", IncludeInDefaultView = true  },
            }));

            // Quests (definitions)
            template.Lists.Add(SiteBuilder.BuildGenericList("Quests", "Lists/Quests", true, new SPField[]
            {
                new NoteField {ID = Guid.Parse("297a7b82-ee31-4022-9f7b-5d094f95cd44"),             Name = "ShortDescription", Title = "Short Description", IncludeInDefaultView = true  },
                new NoteField {ID = Guid.Parse("a20e3071-419f-48ce-b046-af8e299c072e"),             Name = "LongDescription", Title = "Long Description", IncludeInDefaultView = true  },
                new IntField {ID = Guid.Parse("0af6cefd-e58b-4cf1-9cca-02a04fdf7069"),              Name = "XP", Title = "XP", IncludeInDefaultView = true  },
                new ChoiceField {ID = Guid.Parse("fb047e16-b67c-4f9b-8d03-cb445e735532"),           Name = "QuestType", Title = "Quest Type", Choices = new string[]{ "Meeting", "Chat", "Channel Chat", "Calling", "Device", "App", "Reaction", "Custom" }, IncludeInDefaultView = true },
                new IntField {ID = Guid.Parse("0af6cefd-e58b-4cf1-9cca-02a04fdf7008"),              Name = "ItemCount", Title = "Item Count", IncludeInDefaultView = true  },
                new IntField {ID = Guid.Parse("c15e1a1a-a7b5-4be7-9c75-c37d84253e89"),              Name = "BadgeID", Title = "Badge ID", IncludeInDefaultView = true  },
                new IntField {ID = Guid.Parse("4faaa8a9-c42b-4826-be32-d72ef1333fb5"),              Name = "Completions", Title = "Completions", IncludeInDefaultView = false  },
                new ThumbnailField {ID = Guid.Parse("231a5594-091a-4f56-985c-9ccd0b06f59e"),        Name = "QuestImage", Title = "Quest Image", IncludeInDefaultView = true  },
                new UrlField { ID = Guid.Parse("c49d71df-af21-46c7-a78a-9fe12939b442"),             Name = "TrainingLink", Title = "Training Link", IncludeInDefaultView = true  },
                new ChoiceField { ID = Guid.Parse("a9188ae8-9ee0-4382-8566-7e2272251c79"),          Name = "ModalityType", Title = "Modality Type", Choices = new string[]{ "All", "Audio", "Video", "Screen Sharing" }, IncludeInDefaultView = true },
                new ChoiceField { ID = Guid.Parse("5b2c3ae3-c23d-4360-8299-2eeb68d0a515"),          Name = "MeetingRole", Title = "Meeting Role", Choices = new string[]{ "Attendee", "Organizer", "Both" }, Default = "Both", IncludeInDefaultView = true },
                new ChoiceField { ID = Guid.Parse("68c9e77f-849a-4b8e-9620-e34439bb7d55"),          Name = "DeviceType", Title = "Device Type", Choices = new string[]{ "Desktop", "Web", "Mobile" }, IncludeInDefaultView = true },
                new ChoiceField { ID = Guid.Parse("b50c2d98-b20d-4d51-838b-0cfb7c3488df"),          Name = "ReactionType", Title = "Reaction Type", Choices = new string[]{ "All", "Like", "Heart", "Surprised", "Laugh", "Angry", "Sad" }, IncludeInDefaultView = true},
                new NoteField { ID = Guid.Parse("8cdb7525-7b24-4b8b-986f-56edd825cf3f"),            Name = "QuestAdaptiveImage", Title = "Quest Adaptive Image", IncludeInDefaultView = true },
                new ChoiceField { ID = Guid.Parse("e807f3ec-3683-4156-94db-bc79cdf3f349"),          Name = "QuestStatus", Title = "Quest Status", Choices = new string[]{ "Active", "Draft", "Retired" }, Default = "Active", IncludeInDefaultView = true },
                new BooleanField { ID = Guid.Parse("3839527c-9620-4ee7-8a9b-d6b099e0c3fb"),         Name = "Notified", Default = false, Title = "Notified", IncludeInDefaultView = false },
                new DateField { ID = Guid.Parse("6f7dc838-ab91-4079-867e-6c682d13f5ca"),            Name = "TrackFromDate", Title = "Track From Date", IncludeInDefaultView = true },
                new DateField { ID = Guid.Parse("39843ba2-49d8-41df-8827-8665daae46a2"),            Name = "TrackToDate", Title = "Track To Date", IncludeInDefaultView = true },
                new TextField { ID = Guid.Parse("e0fd4a61-1315-4f77-b702-2413bb20cae8"),            Name = "AppId", Title = "App Id", IncludeInDefaultView = true },
                new UserField { ID = Guid.Parse("f9981f2b-36d5-4ec3-9b36-82e84919e1dd"),            Name = "VisibleTo", Title = "Visible To", SelectionMode = UserField.UserSelectionMode.PeopleAndGroups, IncludeInDefaultView = true },
                new BooleanField {ID = Guid.Parse("bd9bddfc-b9b4-40cc-b476-c7bc367d0437"),          Name = "ApprovalRequired", Title = "Approval Required", IncludeInDefaultView = true }
            }));

            // Rewards
            template.Lists.Add(SiteBuilder.BuildGenericList("Rewards", "lists/Rewards", true, new SPField[]
            {
                new NoteField { ID = Guid.Parse("a182d35d-79ab-4f8c-ace6-d158bf0c5a16"),            Name = "Description", Title = "Description", IncludeInDefaultView = true },
                new ThumbnailField { ID = Guid.Parse("419e2784-502f-4a2b-be1e-683a09d40f66"),       Name = "RewardImage", Title = "Reward Image", IncludeInDefaultView = true },
                new TextField { ID = Guid.Parse("c6979a79-ab7f-4fec-9c92-e85da801f172"),            Name = "RedeemInstructions", Title = "Redeem Instructions", IncludeInDefaultView = true },
                new UrlField { ID = Guid.Parse("75e8a40a-bedd-4249-962c-ff740038528a"),             Name = "RedeemURL", Title = "Redeem URL", IncludeInDefaultView = true },
                new TextField { ID = Guid.Parse("f2b5947a-9f63-4ad3-8bcf-28998c468b6b"),            Name = "VoucherCode", Title = "Voucher Code", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("dff649a9-3f25-43ad-b26c-a467e5e30398"),             Name = "XP", Title = "XP", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("1c0f9a6e-38aa-4718-8ba5-e82c3cd2896c"),             Name = "Redemptions", Title = "Redemptions", IncludeInDefaultView = true },
                new ChoiceField { ID = Guid.Parse("604ead89-f4f0-404a-89df-cdfa4696d5a3"),          Name = "RewardStatus", Title = "Reward Status", Choices = new string[]{ "Active", "Draft", "Retired" }, IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("d1c9c7e2-2ac4-4d32-80c5-e6949e5472ba"),             Name = "RedemptionLimit", Title = "Redemption Limit", IncludeInDefaultView = true },
                new BooleanField { ID = Guid.Parse("ab8960a1-ec85-4142-976a-aa0b372fab80"),         Name = "UniqueCodeRequired", Title = "Unique Code Required", Default = false, IncludeInDefaultView = true },
                new BooleanField { ID = Guid.Parse("abeae93d-45b6-4416-a802-49b1e6f89e14"),         Name = "ApprovalRequired", Title = "Approval Required", Default = false, IncludeInDefaultView = true },
            }));

            // Settings
            template.Lists.Add(SiteBuilder.BuildGenericList("Settings", "lists/Settings", false, new SPField[]
            {
                new NoteField { ID = Guid.Parse("41b9888a-4603-4926-af7a-9cb560ff1dfe"), Name = "Description", Title = "Description", IncludeInDefaultView = true },
                new TextField { ID = Guid.Parse("32005527-2e25-4316-bc83-97fdd86116bc"), Name = "Value", Title = "Value", IncludeInDefaultView = true }
            }));

            // Stats
            template.Lists.Add(SiteBuilder.BuildGenericList("Stats", "lists/Stats", false, new SPField[]
            {
                new TextField { ID = Guid.Parse("e72a5b1d-1aaf-4b00-88c6-e45b5d68e3c3"), Name = "StatName", Title = "Stat Name", IncludeInDefaultView = true },
                new TextField { ID = Guid.Parse("383f29e8-d9d1-4445-a92b-32861c2d6b68"), Name = "StatValue", Title = "Stat Value", IncludeInDefaultView = true }
            }));

            // Teams apps
            template.Lists.Add(SiteBuilder.BuildGenericList("Teams Apps", "lists/TeamsApps", true, new SPField[]
            {
                new TextField { ID = Guid.Parse("f0e223a4-1bcf-4edf-a972-c4e29a8ddd83"),    Name = "AppName", Title = "App Name", IncludeInDefaultView = true },
                new NoteField { ID = Guid.Parse("28bb71c4-2936-4b78-bf8b-44baa718e334"),    Name = "AppDescription", Title = "App Description", IncludeInDefaultView = true },
                new TextField { ID = Guid.Parse("bad3cd5f-0552-4132-8a0d-7e40fa74c5f6"),    Name = "AppId", Title = "App Id", IncludeInDefaultView = true },
                new TextField { ID = Guid.Parse("1b824ed4-ad27-41a2-a139-c0298528f545"),    Name = "AppDefinitionId", Title = "App Definition ID", IncludeInDefaultView = true },
                new UrlField { ID = Guid.Parse("4d27d0fe-2691-4c09-adbd-c0ccbb7fcd0a"),     Name = "AppIconURL", Title = "App Icon URL", IncludeInDefaultView = true }
            }));

            // User Badges
            template.Lists.Add(SiteBuilder.BuildGenericList("User Badges", "Lists/User Badges", false, new SPField[]
            {
                new UserField {ID = Guid.Parse("f0b84fcd-a0b2-4591-b23c-f9004ec0dc03"),     Name = "User", Title = "User", IncludeInDefaultView = false },
                new IntField {ID = Guid.Parse("033676ef-a732-4c67-beb6-dd62b9e0a8f8"),      Name = "UserID", Title = "User ID", IncludeInDefaultView = true },
                new IntField {ID = Guid.Parse("4f19f726-94e5-4171-bca1-35cade0b6644"),      Name = "BadgeID", Title = "Badge ID", IncludeInDefaultView = true },
                new BooleanField {ID = Guid.Parse("ba708bc1-b9dc-48ee-9d58-6be162c12c22"),  Name = "Notify", Title = "Notify", IncludeInDefaultView = true },
            }));

            // User Quests (quests user has accepted)
            template.Lists.Add(SiteBuilder.BuildGenericList("User Quests", "Lists/UserQuests", true, new SPField[]
            {
                new IntField { ID = Guid.Parse("8e3c96ed-0730-4be1-bc5a-767294d0943f"),         Name = "UserID", Title = "User ID", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("3487655c-9c38-4681-af73-309608fafc50"),         Name = "QuestID", Title = "Quest ID", IncludeInDefaultView = true },
                new TextField { ID = Guid.Parse("e0f1d37e-6798-40d8-8bc6-6adf000fcc79"),        Name = "QuestType", Title = "Quest Type", IncludeInDefaultView = true },
                new BooleanField { ID = Guid.Parse("28b47dae-f1b0-443c-9971-d6fd713d1fdf"),     Name = "IsComplete", Title = "Is Complete", IncludeInDefaultView = true },
                new BooleanField { ID = Guid.Parse("6bfee35c-c2e5-411b-ba01-f7da9ecc9118"),     Name = "Notify", Title = "Notify", IncludeInDefaultView = true },
                new NoteField { ID = Guid.Parse("33974545-30b1-4de3-b672-52f5fcf0ee52"),        Name = "ApprovalComments", Title = "Approval Comments", IncludeInDefaultView = true },
                new NoteField { ID = Guid.Parse("6dd200ed-f2ea-40aa-8850-14b5d5812f7a"),        Name = "CompletionComments", Title = "Completion Comments", IncludeInDefaultView = true },
                new ChoiceField { ID = Guid.Parse("30f57507-094a-4b49-94ce-6d9339f7d4e4"),      Name = "ApprovalStatus", Title = "Approval Status", Choices = new string[] { "Pending", "Approved", "Rejected", "N/A" }, Default = "Pending", IncludeInDefaultView = true },
                new ThumbnailField {ID = Guid.Parse("2a893a44-944d-43eb-880c-1df9dda7c2bc"),    Name = "CompletionImage", Title = "Completion Image", IncludeInDefaultView = true  }
            }));


            // User Quest Processing
            template.Lists.Add(SiteBuilder.BuildGenericList("User Quest Processing", "Lists/UserQuestProcessing", false, new SPField[]
            {
                new IntField { ID = Guid.Parse("354d9067-8687-452f-914f-29ba70329f0d"),         Name = "UserID", Title = "User ID", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("de6f50f8-8043-4093-aa49-3bd4627bcadc"),         Name = "QuestID", Title = "Quest ID", IncludeInDefaultView = true },
                new TextField { ID = Guid.Parse("e231e20c-e5a3-42b9-9733-948acb3a6ab8"),        Name = "QuestType", Title = "Quest Type", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("cd780b9e-27c8-4437-8854-626164a26271"),         Name = "UserQuestID", Title = "User Quest ID", IncludeInDefaultView = true },
                new BooleanField { ID = Guid.Parse("ca0371d0-ffa2-4296-b7ac-5e2907908744"),     Name = "Notify", Title = "Notify", IncludeInDefaultView = true },
                new ChoiceField { ID = Guid.Parse("30f57507-094a-4b49-94ce-6d9339f7d4e4"),      Name = "ProcessingStatus", Title = "Processing Status", Choices = new string[] { "Not Started", "Processing", "Completed", "Failed" }, Default = "Not Started", IncludeInDefaultView = true },
                new ChoiceField { ID = Guid.Parse("fe25a8ec-0e19-4e3e-9d44-dc3e66755b65"),      Name = "ApprovalStatus", Title = "Approval Status", Choices = new string[] { "Pending", "Approved", "Rejected", "N/A" }, Default = "Pending", IncludeInDefaultView = true },
                new BooleanField { ID = Guid.Parse("5fa34271-c505-4931-9bbd-0b2835285c03"),     Name = "ApprovalRequired", Title = "Approval Required", IncludeInDefaultView = true }
            }));

            // User Rewards
            template.Lists.Add(SiteBuilder.BuildGenericList("User Rewards", "Lists/UserRewards", true, new SPField[]
            {
                new IntField { ID = Guid.Parse("a0c6af86-85d0-49ef-bde1-572c0ff6f079"),         Name = "UserID", Title = "User ID", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("16e415ac-63c1-4990-896e-50e3dd730920"),         Name = "RewardID", Title = "Reward ID", IncludeInDefaultView = true },
                new BooleanField { ID = Guid.Parse("ca0371d0-ffa2-4296-b7ac-5e2907908743"),     Name = "Notify", Title = "Notify", IncludeInDefaultView = true },
                new BooleanField { ID = Guid.Parse("21364669-b33b-401e-b277-82987cac8011"),     Name = "Redeemed", Title = "Redeemed", IncludeInDefaultView = true },
                new TextField { ID = Guid.Parse("4f9101b5-7b28-427e-9180-bdb67e737464"),        Name = "RedemptionComments", Title = "Redemption Comments", IncludeInDefaultView = true },
                new TextField { ID = Guid.Parse("c1eac3cb-cdc5-45a2-91a0-32293f3c6847"),        Name = "VoucherCode", Title = "Voucher Code", IncludeInDefaultView = true },
                new ChoiceField { ID = Guid.Parse("da2788a6-b1b5-4681-832c-a09b20c32c26"),      Name = "CodeStatus", Title = "Code Status", Choices = new string[]{ "Requested", "Provided", "Denied", "Approved", "Rejected" }, IncludeInDefaultView = true },
            }));

            // User Reward Processing
            template.Lists.Add(SiteBuilder.BuildGenericList("User Reward Processing", "Lists/UserRewardProcessing", false, new SPField[]
            {
                new IntField { ID = Guid.Parse("727af257-dc4c-4b7f-8074-8c47b97c94da"), Name = "UserID", Title = "User ID", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("2fac95c1-9b8c-4e9f-ba50-c590c41e6903"), Name = "RewardID", Title = "Reward ID", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("ad682859-d629-4bec-b458-f7ce3fe85265"), Name = "UserRewardID", Title = "User Reward ID", IncludeInDefaultView = true }
            }));

            // Users
            template.Lists.Add(SiteBuilder.BuildGenericList("Users", "lists/Users", true, new SPField[]
            {
                new UserField { ID = Guid.Parse("7bbb2019-6085-4a80-a0b4-27930291c365"),    Name = "User", Title = "User", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("585c0c96-8168-4744-be16-647233620264"),     Name = "Points", Title = "Points", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("2867549c-8dd1-4e72-99ba-b21470a2ee44"),     Name = "EligablePoints", Title = "Eligable Points", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("fcb4e1c8-0808-4488-ac64-603823701544"),     Name = "LevelID", Title = "Level ID", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("1106c195-6835-492a-b346-92e5b98d692e"),     Name = "EligableLevel", Title = "Eligable Level", IncludeInDefaultView = true },
                new IntField { ID = Guid.Parse("db2c2b32-80ac-4d20-b9cf-567aa2228997"),     Name = "BadgeCount", Title = "Badge Count", IncludeInDefaultView = true },
                new DateField { ID = Guid.Parse("2042f1da-7361-4c9f-a029-e15ed1464b2b"),    Name = "AwardDate", Title = "Award Date", IncludeInDefaultView = false },
                new IntField { ID = Guid.Parse("d44a6386-abd8-406f-b65b-81d4533e876c"),     Name = "QuestCount", Title = "Quest Count", IncludeInDefaultView = true },
                new BooleanField { ID = Guid.Parse("8d9a8f43-e06c-4620-8c5a-95ae000e8a0f"), Name = "TutorialComplete", Title = "Tutorial Complete", Default = false, IncludeInDefaultView = true },
                new DateField { ID = Guid.Parse("a196819d-9296-485b-91c9-ebcdfbc7fcb8"),    Name = "LastActive", Title = "Last Active", IncludeInDefaultView = true },
                new DateField { ID = Guid.Parse("117f304f-4547-441c-8eb1-ff7bee27f304"),    Name = "LastNotifiedActivity", Title = "Last Notified Activity", IncludeInDefaultView = true },
                new DateField { ID = Guid.Parse("4dbb5b81-6d51-44ac-92b4-b769a648d46a"),    Name = "LastNotifiedReward", Title = "Last Notified Reward", IncludeInDefaultView = true }
            }));

            // Cards
            template.Lists.Add(SiteBuilder.BuildGenericList("Cards", "lists/Cards", true, new SPField[]
            {
                new NoteField { ID = Guid.Parse("93abd61d-59be-4b8f-b292-612072f060c9"), Name = "CardJSON", Title = "Card JSON", IncludeInDefaultView = true }
            }));

            return template;
        }
    }
}
