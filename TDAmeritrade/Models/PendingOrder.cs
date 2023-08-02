namespace TDAmeritrade.Models
{
    public class PendingOrder
    {
        public string Symbol { get; set; }
        public float Quantity { get; set; }
        public decimal Price { get; set; } = 0;
    }
}
