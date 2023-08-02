using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TDAmeritrade.Models
{
    public class TradingParams
    {
        public bool IsBuyAllowed { get; set; }
        public bool IsSellAllowed { get; set; }
        public bool UseBBandToBuy { get; set; }
        public decimal BBandRedValue { get; set; }

        public TradingParams()
        {

        }
    }
}
