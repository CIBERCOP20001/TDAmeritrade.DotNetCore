using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TDAmeritrade.Models
{

    public class SecuritiesaccountRootobject
    {
        public Securitiesaccount securitiesAccount { get; set; }
    }

    public class Securitiesaccount
    {
        public string type { get; set; }
        public string accountId { get; set; }
        public int roundTrips { get; set; }
        public bool isDayTrader { get; set; }
        public bool isClosingOnlyRestricted { get; set; }
        public Orderstrategy[] orderStrategies { get; set; }
        public Initialbalances initialBalances { get; set; }
        public Currentbalances currentBalances { get; set; }
        public Projectedbalances projectedBalances { get; set; }
    }

    public class Initialbalances
    {
        public float accruedInterest { get; set; }
        public float cashAvailableForTrading { get; set; }
        public float cashAvailableForWithdrawal { get; set; }
        public float cashBalance { get; set; }
        public float bondValue { get; set; }
        public float cashReceipts { get; set; }
        public float liquidationValue { get; set; }
        public float longOptionMarketValue { get; set; }
        public float longStockValue { get; set; }
        public float moneyMarketFund { get; set; }
        public float mutualFundValue { get; set; }
        public float shortOptionMarketValue { get; set; }
        public float shortStockValue { get; set; }
        public bool isInCall { get; set; }
        public float unsettledCash { get; set; }
        public float cashDebitCallValue { get; set; }
        public float pendingDeposits { get; set; }
        public float accountValue { get; set; }
    }

    public class Currentbalances
    {
        public float accruedInterest { get; set; }
        public float cashBalance { get; set; }
        public float cashReceipts { get; set; }
        public float longOptionMarketValue { get; set; }
        public float liquidationValue { get; set; }
        public float longMarketValue { get; set; }
        public float moneyMarketFund { get; set; }
        public float savings { get; set; }
        public float shortMarketValue { get; set; }
        public float pendingDeposits { get; set; }
        public float cashAvailableForTrading { get; set; }
        public float cashAvailableForWithdrawal { get; set; }
        public float cashCall { get; set; }
        public float longNonMarginableMarketValue { get; set; }
        public float totalCash { get; set; }
        public float shortOptionMarketValue { get; set; }
        public float mutualFundValue { get; set; }
        public float bondValue { get; set; }
        public float cashDebitCallValue { get; set; }
        public float unsettledCash { get; set; }
    }

    public class Projectedbalances
    {
        public float cashAvailableForTrading { get; set; }
        public float cashAvailableForWithdrawal { get; set; }
    }

    public class Orderstrategy
    {
        public string session { get; set; }
        public string duration { get; set; }
        public string orderType { get; set; }
        public string complexOrderStrategyType { get; set; }
        public float quantity { get; set; }
        public float filledQuantity { get; set; }
        public float remainingQuantity { get; set; }
        public string requestedDestination { get; set; }
        public string destinationLinkName { get; set; }
        public float price { get; set; }
        public Orderlegcollection[] orderLegCollection { get; set; }
        public string orderStrategyType { get; set; }
        public long orderId { get; set; }
        public bool cancelable { get; set; }
        public bool editable { get; set; }
        public string status { get; set; }
        public DateTime enteredTime { get; set; }
        public DateTime closeTime { get; set; }
        public string tag { get; set; }
        public int accountId { get; set; }
        public Orderactivitycollection[] orderActivityCollection { get; set; }
        public string statusDescription { get; set; }
    }

    public class Orderlegcollection
    {
        public string orderLegType { get; set; }
        public int legId { get; set; }
        public Instrument instrument { get; set; }
        public string instruction { get; set; }
        public string positionEffect { get; set; }
        public float quantity { get; set; }
    }

    public class Instrument
    {
        public string assetType { get; set; }
        public string cusip { get; set; }
        public string symbol { get; set; }
        public string description { get; set; }
        public string type { get; set; }
        public string putCall { get; set; }
        public string underlyingSymbol { get; set; }
    }

    public class Orderactivitycollection
    {
        public string activityType { get; set; }
        public long activityId { get; set; }
        public string executionType { get; set; }
        public float quantity { get; set; }
        public float orderRemainingQuantity { get; set; }
        public Executionleg[] executionLegs { get; set; }
    }

    public class Executionleg
    {
        public int legId { get; set; }
        public float quantity { get; set; }
        public float mismarkedQuantity { get; set; }
        public float price { get; set; }
        public DateTime time { get; set; }
    }


}
