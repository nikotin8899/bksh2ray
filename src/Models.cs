using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace bksh2ray
{
    public class VlessServer
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("server")]
        public string Server { get; set; } = "";

        [JsonPropertyName("server_port")]
        public int ServerPort { get; set; } = 443;

        [JsonPropertyName("uuid")]
        public string Uuid { get; set; } = "";

        [JsonPropertyName("flow")]
        public string Flow { get; set; } = "";

        [JsonPropertyName("security")]
        public string Security { get; set; } = "none";

        [JsonPropertyName("sni")]
        public string Sni { get; set; } = "";

        [JsonPropertyName("pbk")]
        public string Pbk { get; set; } = "";

        [JsonPropertyName("sid")]
        public string Sid { get; set; } = "";

        [JsonPropertyName("spx")]
        public string Spx { get; set; } = "";

        [JsonPropertyName("fp")]
        public string Fp { get; set; } = "chrome";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "tcp";
    }

    public class AppConfig
    {
        [JsonPropertyName("servers")]
        public List<VlessServer> Servers { get; set; } = new();

        [JsonPropertyName("active_server_index")]
        public int ActiveServerIndex { get; set; } = -1;

        [JsonPropertyName("preset_ru")]
        public bool PresetRu { get; set; } = true;

        [JsonPropertyName("system_proxy")]
        public bool SystemProxy { get; set; } = true;

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "proxy";

        [JsonPropertyName("custom_direct_domains")]
        public List<string> CustomDirectDomains { get; set; } = new();
    }
}
