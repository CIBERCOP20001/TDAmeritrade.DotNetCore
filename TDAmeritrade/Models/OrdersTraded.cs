using System;

namespace TDAmeritrade.Models
{
    public class OrdersTraded
    {
        public OrdersData[] Orders { get; set; }
    }

    public class OrdersData
    {
        public string session { get; set; }
        public string duration { get; set; }
        public string orderType { get; set; }
        public string complexOrderStrategyType { get; set; }
        public decimal quantity { get; set; }
        public decimal filledQuantity { get; set; }
        public decimal remainingQuantity { get; set; }
        public string requestedDestination { get; set; }
        public string destinationLinkName { get; set; }
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
        public float price { get; set; }
    }
}
