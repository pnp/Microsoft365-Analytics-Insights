using Common.DataUtils;
using Newtonsoft.Json;
using System;

namespace App.ControlPanel.Engine.Models
{
    public abstract class Base64Serialisable<T>
    {

        public string ToBase64()
        {
            string json = JsonConvert.SerializeObject(this);
            return json.Base64Encode();
        }

        public static T GetFromBase64String(string hash)
        {
            if (string.IsNullOrEmpty(hash))
            {
                throw new ArgumentNullException(nameof(hash));
            }

            string json = hash.Base64Decode();
            T info = JsonConvert.DeserializeObject<T>(json);

            return info;
        }
    }
}
