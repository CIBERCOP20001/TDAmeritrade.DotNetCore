using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TDAmeritrade.Models
{

    public class OptionPutCallData
    {
        public string complexOrderStrategyType { get; set; }
        public string orderStrategyType { get; set; }
        public string orderType { get; set; }
        public string session { get; set; }
        public string duration { get; set; }
        public Orderlegcollection[] orderLegCollection { get; set; }


    }

    //public class Orderlegcollection
    //{
    //    public string orderLegType { get; set; }
    //    public int legId { get; set; }
    //    public string instruction { get; set; }
    //    public int quantity { get; set; }
    //    public Instrument instrument { get; set; }
    //}

    //public class Instrument
    //{
    //    public string symbol { get; set; }
    //    public string assetType { get; set; }
    //    public string putCall { get; set; }
    //}

}
