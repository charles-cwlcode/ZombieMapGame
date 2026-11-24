using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace ZombieMap.Core.Protocol
{
    /// <summary>One WebSocket frame.</summary>
    public sealed class Envelope
    {
        public string Type;
        public int Id;
        public JToken Data;

        public T DataAs<T>() => Data == null || Data.Type == JTokenType.Null
            ? default
            : Data.ToObject<T>(ProtocolJson.Serializer);
    }

    /// <summary>
    /// The one JSON setup used by both client and server: camelCase, enums as strings,
    /// nulls left out. Same code on both sides, so they can't drift apart.
    /// </summary>
    public static class ProtocolJson
    {
        public static readonly JsonSerializerSettings Settings = CreateSettings();
        public static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

        /// <summary>
        /// For saved state on the server (not the wire): plain property names, so dictionary keys stay
        /// as they are; enums by name, so reordering an enum never changes saved data; and new objects
        /// for every collection on read, so defaults are never doubled up.
        /// </summary>
        public static readonly JsonSerializerSettings StorageSettings = new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter() },
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            MissingMemberHandling = MissingMemberHandling.Ignore,
        };

        private static JsonSerializerSettings CreateSettings()
        {
            var s = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.None,
            };
            s.Converters.Add(new StringEnumConverter());
            return s;
        }

        public static string Write(string type, object data, int id = 0)
        {
            var envelope = new JObject
            {
                ["type"] = type,
                ["id"] = id,
                ["data"] = data == null ? JValue.CreateNull() : JToken.FromObject(data, Serializer),
            };
            return envelope.ToString(Formatting.None);
        }

        /// <summary>Parses a frame. Throws JsonException on bad input.</summary>
        public static Envelope Read(string json)
        {
            var obj = JObject.Parse(json);
            return new Envelope
            {
                Type = (string)obj["type"],
                Id = obj["id"]?.Type == JTokenType.Integer ? (int)obj["id"] : 0,
                Data = obj["data"],
            };
        }

        public static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);
        public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);
    }
}
