using System;
using System.Collections.Generic;

namespace App.ControlPanel.Engine.SharePointModelBuilder
{

    public abstract class SPField
    {
        public Guid ID { get; set; } = Guid.Empty;
        public string Name { get; set; }
        public string Title { get; set; }
        public abstract string Type { get; }

        public bool IncludeInDefaultView { get; set; } = true;

        public abstract string GetTypeSpecificXmlAttribs();

        public virtual string ToXmlString()
        {
            var idStr = "{" + ID.ToString() + "}";
            return $"<Field DisplayName=\"{Title}\" Name=\"{Name}\" Title=\"{Title}\" Type=\"{Type}\" ID=\"{idStr}\" {GetTypeSpecificXmlAttribs()} IsModern=\"TRUE\" />";
        }
    }
    public class TextField : SPField
    {
        public override string Type => "Text";

        public override string GetTypeSpecificXmlAttribs()
        {
            return $"MaxLength=\"255\"";
        }
    }
    public class NoteField : SPField
    {
        public override string Type => "Note";

        public override string GetTypeSpecificXmlAttribs()
        {
            return $"RichText=\"FALSE\"";
        }
    }
    public class UrlField : SPField
    {
        public override string Type => "URL";

        public override string GetTypeSpecificXmlAttribs()
        {
            return $"";
        }
    }
    public class IntField : SPField
    {
        public override string Type => "Number";

        public override string GetTypeSpecificXmlAttribs()
        {
            return $"Percentage=\"FALSE\"";
        }
    }

    public class UserField : SPField
    {
        public override string Type => "User";

        public override string GetTypeSpecificXmlAttribs()
        {
            var selectionMode = "UserSelectionMode=\"0\"";
            if (SelectionMode == UserSelectionMode.PeopleAndGroups)
            {
                selectionMode = "UserSelectionMode=\"PeopleAndGroups\"";
            }
            return $"List=\"UserInfo\" {selectionMode} UserSelectionScope=\"0\"";
        }

        public UserSelectionMode SelectionMode { get; set; }

        public enum UserSelectionMode
        {
            Default,
            PeopleAndGroups
        }
    }

    public class BooleanField : SPField
    {
        public override string Type => "Boolean";

        public override string GetTypeSpecificXmlAttribs()
        {
            return $"Percentage=\"FALSE\"";
        }

        public bool Default { get; set; } = false;

        public override string ToXmlString()
        {
            var idStr = "{" + ID.ToString() + "}";
            var openTag = $"<Field DisplayName=\"{Title}\" Name=\"{Name}\" Title=\"{Title}\" Type=\"{Type}\" ID=\"{idStr}\" {GetTypeSpecificXmlAttribs()}>";
            var defaultVal = Default ? "1" : "0";
            return $"{openTag}<Default>{defaultVal}</Default></Field>";
        }
    }
    public class ChoiceField : SPField
    {
        public override string Type => "Choice";

        public string Default { get; set; }
        public IEnumerable<string> Choices { get; set; } = new List<string>();

        public override string GetTypeSpecificXmlAttribs()
        {
            return $"Percentage=\"FALSE\"";
        }

        public override string ToXmlString()
        {
            var idStr = "{" + ID.ToString() + "}";
            var openTag = $"<Field DisplayName=\"{Title}\" Name=\"{Name}\" Title=\"{Title}\" Type=\"{Type}\" ID=\"{idStr}\" {GetTypeSpecificXmlAttribs()} Required=\"FALSE\" EnforceUniqueValues=\"FALSE\">";
            var choicesTags = string.Empty;
            foreach (var tag in Choices)
                choicesTags += $"<CHOICE>{tag}</CHOICE>";
            var defaultTag = string.Empty;
            if (!string.IsNullOrEmpty(Default))
            {
                defaultTag = $"<Default>{Default}</Default>";
            }
            return $"{openTag}<CHOICES>{choicesTags}</CHOICES>{defaultTag}</Field>";
        }
    }

    public class DateField : SPField
    {
        public override string Type => "DateTime";

        public override string GetTypeSpecificXmlAttribs()
        {
            return $"FriendlyDisplayFormat=\"Disabled\" Format=\"DateOnly\"";
        }
    }

    public class ThumbnailField : SPField
    {
        public override string Type => "Thumbnail";

        public override string GetTypeSpecificXmlAttribs()
        {
            return String.Empty;
        }
    }
}
