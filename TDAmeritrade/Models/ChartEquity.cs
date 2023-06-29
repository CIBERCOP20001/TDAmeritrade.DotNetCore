using Newtonsoft.Json;

namespace TDAmeritrade.Models
{
    public class Rootobject
    {
        public Datum[] data { get; set; }
    }

    public class Datum
    {
        public string service { get; set; }
        public long timestamp { get; set; }
        public string command { get; set; }
        public Content[] content { get; set; }
    }

    public class Content
    {
        [JsonProperty("seq")]
        public int Seq { get; set; }
        [JsonProperty("key")]
        public string Symbol { get; set; }
        [JsonProperty("1")]
        public float OpenPrice { get; set; }
        [JsonProperty("2")]
        public float HighPrice { get; set; }
        [JsonProperty("3")]
        public float LowPrice { get; set; }
        [JsonProperty("4")]
        public float ClosePrice { get; set; }
        [JsonProperty("5")]
        public float Volume { get; set; }
        [JsonProperty("6")]
        public int Sequence { get; set; }
        [JsonProperty("7")]
        public long ChartTime { get; set; }
        [JsonProperty("8")]
        public int ChartDay { get; set; }

    }


}
