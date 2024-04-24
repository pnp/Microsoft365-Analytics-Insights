using Microsoft.Graph;
using System;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public static class ItemBodyExtensions
    {
        public static string ToPlainText(this ItemBody itemBody)
        {
            if (itemBody.ContentType == BodyType.Text)
            {
                return itemBody.Content;
            }
            else if (itemBody.ContentType == BodyType.Html)
            {
                var plainText = System.Text.RegularExpressions.Regex.Replace(itemBody.Content, @"<(.|\n)*?>", "");
                return plainText;
            }
            else
            {
                throw new NotSupportedException();
            }
        }
    }
}
