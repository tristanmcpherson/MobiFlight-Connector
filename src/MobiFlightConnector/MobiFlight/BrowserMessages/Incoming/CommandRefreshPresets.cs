using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MobiFlight.BrowserMessages.Incoming
{

    public enum PresetType
    {
        [EnumMember(Value = "prosim")]
        PROSIM,
        [EnumMember(Value = "vjoy")]
        VJOY
    }
    public class CommandRefreshPresets
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public PresetType type;
    }
}
