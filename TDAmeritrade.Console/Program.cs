using Dapper;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Skender.Stock.Indicators;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using TDAmeritrade;
using TDAmeritrade.Models;

namespace TDConsole
{
    class Program : IDisposable
    {
        HubConnection hubConnection;
        static async Task Main(string[] args)
        {
            using (var instance = new Program())
            {
                await instance.Run();
            }
        }

        TDStreamJsonProcessor _parser;
        TDUnprotectedCache cache;
        TDAmeritradeClient client;
        FileStream stream;
        bool terminated;
        IList<Quote> quotes;
        TradingParams tradingParams = new TradingParams();
        private decimal decimalprice;
        bool callComprada = false;
        bool putComprada = false;
        bool justLeavingSqueeze = false;
        int lastCrossOver = 0;
        double prevLowerBandBB = 0d;

        public Program()
        {

            cache = new TDUnprotectedCache();
            client = new TDAmeritradeClient(cache);
            _parser = new TDStreamJsonProcessor();
            quotes = new List<Quote>();
            //hubConnection = new HubConnectionBuilder()
            //   .WithUrl("http://localhost:5544/realTradeDataHub")
            //   .Build();
            //hubConnection.StartAsync().Wait();
        }

        public void Dispose()
        {
            terminated = true;
            if (stream != null)
            {
                stream.Dispose();
            }
        }

        public async Task Run()
        {
            SignInRefresh().GetAwaiter().GetResult();
            //await GetRealTimeTradeData();
            await TradeAsync();
            CancellationTokenSource cancellation = new CancellationTokenSource();
            AuthRequestTask(TimeSpan.FromMinutes(25), cancellation.Token).Wait();
            Console.WriteLine("Type any key to exit");
            Console.ReadLine();
        }

        private async Task GetRealTimeTradeData()
        {
            var symbols = "SPY";
            int qosInt = 0;

            using (var socket = new TDAmeritradeStreamClient(client))
            {
                async void Retry()
                {
                    if (!terminated)
                    {
                        Console.WriteLine("Retrying...");
                        await Task.Delay(5000);
                        Connect();
                    }
                }

                async void Connect()
                {
                    Console.WriteLine("Connecting...");
                    try
                    {
                        await socket.Connect();

                        if (socket.IsConnected)
                        {
                            await socket.RequestQOS((TDQOSLevels)qosInt);
                            await Task.Delay(1000);
                            await socket.SubscribeQuote(symbols);
                            await socket.SubscribeChart(symbols, IsFutureSymbol(symbols) ? TDChartSubs.CHART_FUTURES : TDChartSubs.CHART_EQUITY);
                            //await socket.SubscribeTimeSale(symbols, IsFutureSymbol(symbols) ? TDTimeSaleServices.TIMESALE_FUTURES : TDTimeSaleServices.TIMESALE_EQUITY);
                            //await socket.SubscribeBook(symbols, TDBookOptions.LISTED_BOOK);
                            //await socket.SubscribeBook(symbols, TDBookOptions.NASDAQ_BOOK);
                            //await socket.SubscribeBook(symbols, TDBookOptions.FUTURES_BOOK);
                        }
                        else if (!terminated)
                        {
                            Retry();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error " + ex);
                        Retry();
                    }
                }

                socket.OnJsonSignal += async (m) =>
                {
                    try
                    {
                        var res = JsonConvert.DeserializeObject<Rootobject>(m);

                        if (res.data != null && m.Contains("CHART_EQUITY"))
                        {
                            var values = res.data[0].content[0];

                            var datecst = TDHelpers.ToCST(TDHelpers.FromUnixTimeMilliseconds(values.ChartTime)).AddHours(-5).ToString("MM/dd/yyyy HH:mm:ss");
                            var sql = $"INSERT INTO AmeritradeData values ('{datecst}',{values.OpenPrice},{values.HighPrice},{values.LowPrice},{values.ClosePrice}, {values.Volume}, {values.Seq}, '{values.Symbol}' )";
                            using (var connection = new SqlConnection("Data Source=TONYDURAN\\SQLEXPRESS;Initial Catalog=Ameritrade;Integrated Security=True;TrustServerCertificate=true;"))
                            {
                                var affectedRows = await connection.ExecuteAsync(sql);

                                //Console.WriteLine(sql);
                                Console.WriteLine($"Affected Rows: {affectedRows}");
                            }
                            DateTime.TryParseExact(datecst, "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate);
                            var newData = new Quote()
                            {
                                Close = (decimal)values.ClosePrice,
                                Date = parsedDate,
                                High = (decimal)values.HighPrice,
                                Low = (decimal)values.LowPrice,
                                Open = (decimal)values.OpenPrice,
                                Volume = (decimal)values.Volume
                            };

                            await hubConnection.InvokeCoreAsync("SendMessage", args: new[] { newData });

                        }
                    }
                    catch (Exception ex) { }
                };

                socket.OnConnect += (s) =>
                {
                    if (!s)
                    {
                        Connect();
                    }
                };

                Connect();
                Console.WriteLine("Type any key to quit");
                Console.ReadLine();
                terminated = true;
                await socket.Disconnect();

            }
        }

        public async Task AuthRequestTask(TimeSpan interval, CancellationToken cancellationToken)
        {
            while (true)
            {

                Task task = Task.Delay(interval, cancellationToken);

                try
                {
                    await task;
                    await SignInRefresh();
                }

                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }

        public async Task SignIn()
        {
            Console.WriteLine("Paste consumer key : (https://developer.tdameritrade.com/user/me/apps)");
            var consumerKey = Console.ReadLine();
            Console.WriteLine("Opening Browser. Please sign in.");
            var uri = client.GetSignInUrl(consumerKey);
            OpenBrowser(uri);
            Console.WriteLine("When complete,please input the code (code={code}) query paramater. Located inside your browser url bar.");
            var code = Console.ReadLine();
            await client.SignIn(consumerKey, code);
            Console.WriteLine($"IsSignedIn : {client.IsSignedIn}");
        }

        public async Task SignInRefresh()
        {
            await client.SignIn();
            Console.WriteLine($"IsSignedIn : {client.IsSignedIn} {DateTime.Now.ToString()}");
        }

        public async Task TradeAsync()
        {
            var putCall = string.Empty;
            var currentSymbol = string.Empty;
            var BBandRedCount = 0;

            TimeSpan startTradeTime = TimeSpan.Parse("08:30:00");
            TimeSpan endTradeTime = TimeSpan.Parse("15:01:00");
            TimeSpan lasMinuteTrade = TimeSpan.Parse("15:00:00");
            TimeSpan timeNow = DateTime.Now.TimeOfDay;

            //await client.SignIn();
            Console.WriteLine($"IsSignedIn : {client.IsSignedIn}");

            var symbols = "SPY";

            char format = '1';

            var qos = '0';
            int qosInt = 0;

            int.TryParse(qos.ToString(), out qosInt);

            var dateTimeIni = DateTime.Now.ToString("yyyy/MM/dd 08:00:00");
            //var dateTimeIni = DateTime.Now.AddDays(-10).ToString("yyyy/MM/dd 08:00:00");
            var dateTimeFin = DateTime.Now.ToString("yyyy/MM/dd 18:00:00");
            var sql = $"SELECT [DateTimeCST] [Date],[OpenPrice] [Open],[HighPrice] [High],[LowPrice] [Low],[ClosePrice][Close],[Volume][Volume] FROM [Ameritrade].[dbo].[AmeritradeData] WHERE DateTimeCST BETWEEN '{dateTimeIni}' AND '{dateTimeFin}' ORDER BY DateTimeCST ASC";
            using (var connection = new SqlConnection("Data Source=TONYDURAN\\SQLEXPRESS;Initial Catalog=Ameritrade;Integrated Security=True;TrustServerCertificate=true;"))
            {
                var res = await connection.QueryAsync<Quote>(sql);
                quotes = res.ToList();

            };



            using (var socket = new TDAmeritradeStreamClient(client))
            {
                async void Retry()
                {
                    if (!terminated)
                    {
                        Console.WriteLine("Retrying...");
                        await Task.Delay(5000);
                        Connect();
                    }
                }

                async void Connect()
                {
                    Console.WriteLine("Connecting...");
                    try
                    {
                        await socket.Connect();

                        if (socket.IsConnected)
                        {
                            await socket.RequestQOS((TDQOSLevels)qosInt);
                            await Task.Delay(1000);
                            await socket.SubscribeQuote(symbols);
                            await socket.SubscribeChart(symbols, IsFutureSymbol(symbols) ? TDChartSubs.CHART_FUTURES : TDChartSubs.CHART_EQUITY);
                            //await socket.SubscribeTimeSale(symbols, IsFutureSymbol(symbols) ? TDTimeSaleServices.TIMESALE_FUTURES : TDTimeSaleServices.TIMESALE_EQUITY);
                            //await socket.SubscribeBook(symbols, TDBookOptions.LISTED_BOOK);
                            //await socket.SubscribeBook(symbols, TDBookOptions.NASDAQ_BOOK);
                            //await socket.SubscribeBook(symbols, TDBookOptions.FUTURES_BOOK);
                        }
                        else if (!terminated)
                        {
                            Retry();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error " + ex);
                        Retry();
                    }
                }

                socket.OnJsonSignal += async (m) =>
                {
                    //Console.WriteLine(m);
                    //return;

                    var currentOptionTraded = GetCurrentOrdersTradedAsync().GetAwaiter().GetResult();

                    if (currentOptionTraded?.Count > 0)
                    {
                        var price = GetLastOptionPriceAsync(currentOptionTraded.First().Symbol).GetAwaiter().GetResult();
                        Console.WriteLine($"option current price : {price} vs bought price : {currentOptionTraded.First().Price}");
                        decimal.TryParse(price, out var decimalPrice);

                        if (decimalPrice - currentOptionTraded.First().Price != 0)
                        {
                            var gainLost = 100 - ((decimalPrice * 100) * 100 / (currentOptionTraded.First().Price * 100));
                            if (gainLost > 0)
                            {
                                Console.WriteLine($"amount: {(decimalPrice - currentOptionTraded.First().Price) * 100}   |   Percentage : -{gainLost}%");
                            }
                            else
                            {
                                Console.WriteLine($"amount: {(decimalPrice - currentOptionTraded.First().Price) * 100}   |   Percentage : +{gainLost * -1}%");

                            }
                        }
                    }

                    var sql = $"SELECT TOP (1) [TradeOn] ,[IsBuyAllowed] ,[IsSellAllowed] ,[UseBBandToBuy], [BBandRedValue] FROM [Ameritrade].[dbo].[TradingConfig]";

                    using (var connection = new SqlConnection("Data Source=TONYDURAN\\SQLEXPRESS;Initial Catalog=Ameritrade;Integrated Security=True;TrustServerCertificate=true;"))
                    {
                        var res = await connection.QueryAsync<TradingParams>(sql);
                        tradingParams.IsSellAllowed = res.First().IsSellAllowed;
                        tradingParams.IsBuyAllowed = res.First().IsBuyAllowed;
                        tradingParams.UseBBandToBuy = res.First().UseBBandToBuy;
                        tradingParams.BBandRedValue = res.First().BBandRedValue;
                    };

                    try
                    {
                        var res = JsonConvert.DeserializeObject<Rootobject>(m);

                        if (res.data != null && m.Contains("CHART_EQUITY"))
                        {
                            //var r = GetCallOptionChainAsync().GetAwaiter().GetResult();
                            //Console.WriteLine(m);
                            var values = res.data[0].content[0];

                            var datecst = TDHelpers.ToCST(TDHelpers.FromUnixTimeMilliseconds(values.ChartTime)).AddHours(-5).ToString("MM/dd/yyyy HH:mm:ss");
                            sql = $"INSERT INTO AmeritradeData values ('{datecst}',{values.OpenPrice},{values.HighPrice},{values.LowPrice},{values.ClosePrice}, {values.Volume}, {values.Seq}, '{values.Symbol}' )";
                            using (var connection = new SqlConnection("Data Source=TONYDURAN\\SQLEXPRESS;Initial Catalog=Ameritrade;Integrated Security=True;TrustServerCertificate=true;"))
                            {
                                var affectedRows = connection.Execute(sql);
                                //Console.WriteLine(sql);
                                //Console.WriteLine($"Affected Rows: {affectedRows}");
                            }

                            quotes.Add(new Quote()
                            {
                                Close = (decimal)values.ClosePrice,
                                Date = TDHelpers.ToCST(TDHelpers.FromUnixTimeMilliseconds(values.ChartTime)).AddHours(-5),
                                High = (decimal)values.HighPrice,
                                Low = (decimal)values.LowPrice,
                                Open = (decimal)values.OpenPrice,
                                Volume = (decimal)values.Volume

                            });

                            IEnumerable<EmaResult> ema8 = quotes.GetEma(8);
                            IEnumerable<EmaResult> ema13 = quotes.GetEma(13);
                            IEnumerable<RsiResult> rSi = quotes.GetRsi(14);

                            timeNow = DateTime.Now.TimeOfDay;

                            //If time is not between 8:30 am and 15:00  then return
                            if (timeNow <= startTradeTime || timeNow >= endTradeTime) return;

                            //If EMAS are not ready then return
                            Console.WriteLine("Validating EMAS");
                            if (ema8.Last().Ema == null || ema13.Last().Ema == null) return;

                            var ema8Val = decimal.Round((decimal)ema8.LastOrDefault().Ema, 2);
                            var ema13Val = decimal.Round((decimal)ema13.LastOrDefault().Ema, 2);

                            var ema8ValPrev = decimal.Round((decimal)ema8.ToList()[ema8.ToList().Count - 2].Ema, 2);
                            var ema13ValPrev = decimal.Round((decimal)ema13.ToList()[ema13.ToList().Count - 2].Ema, 2);

                            var squeezeInfo = IsInSqueeze(quotes);

                            var chopiness = Choppiness(quotes);

                            var ema8dif = decimal.Subtract(ema8Val, ema13Val);
                            var ema13dif = decimal.Subtract(ema13Val, ema8Val);


                            Console.WriteLine("---------------------------------");
                            Console.WriteLine(quotes.Last().Date.ToString());


                            if (ema8dif >= 0)
                            { Console.WriteLine($"EMA8 ${ema8Val} >= EMA13 {ema13Val} Bullish"); }
                            else if (ema13dif >= 0)
                            { Console.WriteLine($"EMA13 ${ema13Val} >= SMA8 {ema8Val} Bearish"); }

                            var currentOrderTraded = string.Empty;
                            try
                            {
                                currentOrderTraded = (GetCurrentOrdersTradedAsync().GetAwaiter().GetResult().FirstOrDefault() != null)
                               ? GetCurrentOrdersTradedAsync().GetAwaiter().GetResult().FirstOrDefault().Symbol
                               : string.Empty;
                            }
                            catch (Exception e)
                            {
                                if (e.Message.Contains("Unauthorized"))
                                {
                                    currentOrderTraded = (GetCurrentOrdersTradedAsync().GetAwaiter().GetResult().FirstOrDefault() != null)
                                            ? GetCurrentOrdersTradedAsync().GetAwaiter().GetResult().FirstOrDefault().Symbol
                                            : string.Empty;
                                }
                            }

                            if (string.IsNullOrEmpty(currentOrderTraded))
                            {
                                callComprada = false;
                                putComprada = false;
                            }
                            else
                            {
                                currentSymbol = currentOrderTraded;
                                callComprada = currentSymbol.Contains("C") ? true : false;
                                putComprada = currentSymbol.Contains("P") ? true : false;
                            };

                            if (timeNow >= lasMinuteTrade) // Last minute to sell everything
                            {
                                if (!string.IsNullOrEmpty(currentSymbol))
                                {
                                    if (putCall == "Call")
                                    {
                                        var orderPlaced = await PlaceOrderAsync(currentSymbol, true, "SELL_TO_CLOSE");
                                        if (orderPlaced)
                                        {
                                            callComprada = false;
                                        }
                                    }
                                    else
                                    {
                                        var orderPlaced = await PlaceOrderAsync(currentSymbol, false, "SELL_TO_CLOSE");
                                        if (orderPlaced)
                                        {
                                            putComprada = false;
                                        }
                                    }
                                }
                                return;
                            }

                            if (callComprada)
                            {
                                if ((ema8Val < ema13Val)
                                        && (ema8ValPrev >= ema13ValPrev))
                                {
                                    lastCrossOver = 0;
                                    //Sell the call
                                    if (!string.IsNullOrEmpty(currentSymbol))
                                    {
                                        var orderPlaced = await PlaceOrderAsync(currentSymbol, true, "SELL_TO_CLOSE");
                                        Console.WriteLine("<<<<<< SELL CALL " + currentSymbol);
                                        if (orderPlaced)
                                        {
                                            callComprada = false;
                                        }
                                    }

                                }
                            }

                            if (putComprada)
                            {
                                if ((ema8Val > ema13Val)
                                        && (ema8ValPrev <= ema13ValPrev))
                                {
                                    lastCrossOver = 0;
                                    //Sell the put
                                    if (!string.IsNullOrEmpty(currentSymbol))
                                    {
                                        var orderPlaced = await PlaceOrderAsync(currentSymbol, false, "SELL_TO_CLOSE");
                                        Console.WriteLine("<<<<<<< SELL PUT " + currentSymbol);
                                        if (orderPlaced)
                                        {
                                            putComprada = false;
                                        }

                                    }
                                }
                            }

                            if ((!squeezeInfo.InSqueeze && !squeezeInfo.inPresqueeze) && chopiness < 60)
                            {
                                if (!callComprada)
                                {
                                    if (justLeavingSqueeze && lastCrossOver <= 8)
                                    {
                                        if ((ema8Val > ema13Val))
                                        {
                                            if (rSi.Last().Rsi < 70)
                                            {
                                                //Get option Chain
                                                var symbol = GetCallOptionChainAsync().GetAwaiter().GetResult();
                                                Console.WriteLine("<<<<<<< BUY CALL" + symbol);

                                                if (!string.IsNullOrWhiteSpace(symbol))
                                                {
                                                    var orderPlaced = await PlaceOrderAsync(symbol, true, "BUY_TO_OPEN");
                                                    if (orderPlaced)
                                                    {
                                                        callComprada = true;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        if ((ema8Val > ema13Val)
                                            && (ema8ValPrev <= ema13ValPrev))
                                        {
                                            lastCrossOver = 0;
                                            if (rSi.Last().Rsi < 70)
                                            {
                                                //Get option Chain
                                                var symbol = GetCallOptionChainAsync().GetAwaiter().GetResult();
                                                Console.WriteLine("<<<<<<< BUY CALL" + symbol);

                                                if (!string.IsNullOrWhiteSpace(symbol))
                                                {
                                                    var orderPlaced = await PlaceOrderAsync(symbol, true, "BUY_TO_OPEN");
                                                    if (orderPlaced)
                                                    {
                                                        callComprada = true;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                if (!putComprada)
                                {
                                    if (justLeavingSqueeze && lastCrossOver <= 8)
                                    {
                                        if ((ema8Val < ema13Val))
                                        {
                                            if (rSi.Last().Rsi > 45)
                                            {
                                                var symbol = GetPutOptionChainAsync().GetAwaiter().GetResult();
                                                Console.WriteLine("<<<<<< BUY PUT " + symbol);
                                                if (!string.IsNullOrWhiteSpace(symbol))
                                                {
                                                    var orderPlaced = await PlaceOrderAsync(symbol, false, "BUY_TO_OPEN");
                                                    if (orderPlaced)
                                                    {
                                                        putComprada = true;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        if ((ema8Val < ema13Val)
                                        && (ema8ValPrev >= ema13ValPrev))
                                        {
                                            lastCrossOver = 0;
                                            if (rSi.Last().Rsi > 45)
                                            {
                                                var symbol = GetPutOptionChainAsync().GetAwaiter().GetResult();
                                                Console.WriteLine("<<<<<< BUY PUT " + symbol);
                                                if (!string.IsNullOrWhiteSpace(symbol))
                                                {
                                                    var orderPlaced = await PlaceOrderAsync(symbol, false, "BUY_TO_OPEN");
                                                    if (orderPlaced)
                                                    {
                                                        putComprada = true;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                justLeavingSqueeze = squeezeInfo.InSqueeze;

                                if (
                                        ((ema8Val < ema13Val) && (ema8ValPrev >= ema13ValPrev))
                                        ||
                                        ((ema8Val > ema13Val) && (ema8ValPrev <= ema13ValPrev))
                                    )
                                {
                                    lastCrossOver = 0;
                                }
                                else
                                {
                                    lastCrossOver++;
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Console.Write(e.Message);
                    }
                };

                socket.OnConnect += (s) =>
                {
                    if (!s)
                    {
                        Connect();
                    }
                };

                Connect();
                Console.WriteLine("Type any key to quit");
                Console.ReadLine();
                terminated = true;
                await socket.Disconnect();
            }
        }

        private async Task<List<PendingOrder>>? GetCurrentOrdersTradedAsync()
        {
            //await client.SignIn();
            try
            {
                var OptionChainDate = DateTime.Now.ToString("yyyy-MM-dd");

                var expMonth = DateTime.Now.ToString("MMM", new CultureInfo("en-GB")).ToUpper().Substring(0, 3);

                TDAuthResult authResult = JsonConvert.DeserializeObject<TDAuthResult>(cache.Load("TDAmeritradeKey"));

                var decoded = HttpUtility.UrlDecode(authResult.security_code);

                var path = $"https://api.tdameritrade.com/v1/accounts/277090213/orders?fromEnteredTime={OptionChainDate}&toEnteredTime={OptionChainDate}&status=FILLED";

                var req = new HttpRequestMessage(HttpMethod.Get, path);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authResult.access_token);
                using (var client = new HttpClient())
                {
                    var res = await client.SendAsync(req);

                    switch (res.StatusCode)
                    {
                        case HttpStatusCode.OK:
                            var json = await res.Content.ReadAsStringAsync();
                            var jsonDom = JsonConvert.DeserializeObject<List<OrdersData>>(json);
                            if (jsonDom.Count == 0) return new List<PendingOrder>();
                            var x = jsonDom.GroupBy(s => (s.orderLegCollection[0].instrument.symbol, s.orderLegCollection[0].instruction))
                                .Select(grp => new
                                {
                                    symbol = grp.FirstOrDefault().orderLegCollection.First().instrument.symbol,
                                    price = grp.FirstOrDefault().orderActivityCollection.First().executionLegs.FirstOrDefault().price,
                                    instruction = grp.FirstOrDefault().orderLegCollection.First().instruction,
                                    quantity = (grp.FirstOrDefault().orderLegCollection.First().instruction)
                                        .Contains("SELL_TO_CLOSE") ? grp.Sum(q => q.orderLegCollection.First().quantity) * -1
                                        : grp.Sum(q => q.orderLegCollection.First().quantity)
                                })
                                .OrderBy(s => s.symbol);

                            //var x = new[] {
                            //      new { symbol = "SPY_062923C437", price = 0.71, instruction = "BUY_TO_OPEN", quantity = 2 },
                            //      new { symbol = "SPY_062923C437", price = 0.81, instruction = "SELL_TO_CLOSE", quantity = -2 },
                            //      new { symbol = "SPY_062923P435", price = 0.51, instruction = "SELL_TO_CLOSE", quantity = -1 },
                            //      new { symbol = "SPY_062923P435", price = 0.71, instruction = "BUY_TO_OPEN", quantity = 1 },
                            //      new { symbol = "SPY_062923P436", price = 0.71, instruction = "SELL_TO_CLOSE", quantity = -2 },
                            //      new { symbol = "SPY_062923P436", price = 0.11, instruction = "BUY_TO_OPEN", quantity = 3 }
                            //}.ToList();
                            //if (PendingOrder is null) return null;

                            var pendingOrders = x.GroupBy(s => (s.symbol))
                                .Select(grp => new PendingOrder
                                {
                                    Symbol = grp.FirstOrDefault().symbol,
                                    Quantity = grp.Sum(a => a.quantity),
                                    Price = (decimal)grp.FirstOrDefault(opt => opt.instruction == "BUY_TO_OPEN").price
                                })
                                .Where(c => c.Quantity > 0);
                            return pendingOrders?.ToList();
                            break;
                        case HttpStatusCode.Unauthorized:
                            Console.WriteLine("Unauthorized");
                            await SignInRefresh();
                            break;

                    }
                }
            }
            catch (Exception e)
            {

                Console.WriteLine(e.ToString());
                Console.WriteLine("No able to get options chain");
                if (e.Message.Contains("Unauthorized"))
                {
                    await SignInRefresh();
                }
                throw;
            }
            return null;
        }

        (bool inPresqueeze, bool OutPreSqueeze, bool InSqueeze) IsInSqueeze(IList<Quote> quotes)
        {
            try
            {
                var price = quotes.Last().Close;
                var length = 20;
                var Num_Dev_Dn = -2.0;
                var Num_Dev_up = 2.0;
                var averageType = "SIMPLE";
                var displace = 0;
                var sDev = quotes.GetStdDev(20);
                var MidLineBB = quotes.GetSma(20);
                var LowerBandBB = MidLineBB.Last().Sma + (Num_Dev_Dn * sDev.Last().StdDev);
                var UpperBandBB = MidLineBB.Last().Sma + (Num_Dev_up * sDev.Last().StdDev);
                var factormid = 1.5;
                var factorlow = 2.0;

                var trueRangeAverageType = "SIMPLE";
                var shiftMid = factormid * quotes.GetTr().GetSma(length).Last().Sma;
                var shiftlow = factorlow * quotes.GetTr().GetSma(length).Last().Sma;

                //def shifthigh = factorhigh * MovingAverage(trueRangeAverageType, TrueRange(high, close, low), length);
                //def shiftMid = factormid * MovingAverage(trueRangeAverageType, TrueRange(high, close, low), length);
                //def shiftlow = factorlow * MovingAverage(trueRangeAverageType, TrueRange(high, close, low), length);

                var average = quotes.GetSma(20);

                var LowerBandKCLow = average.Last().Sma - shiftlow;
                var UpperBandKCLow = average.Last().Sma + shiftlow;

                var UpperBandKCMid = average.Last().Sma + shiftMid;
                var LowerBandKCMid = average.Last().Sma - shiftMid;

                var originalSqueezein = LowerBandBB > LowerBandKCMid && UpperBandBB < UpperBandKCMid && LowerBandBB > prevLowerBandBB;
                var originalSqueezeout = LowerBandBB > LowerBandKCMid && UpperBandBB < UpperBandKCMid && LowerBandBB < prevLowerBandBB;

                var presqueezein = LowerBandBB > LowerBandKCLow && UpperBandBB < UpperBandKCLow && LowerBandBB > prevLowerBandBB;
                var presqueezeout = LowerBandBB > LowerBandKCLow && UpperBandBB < UpperBandKCLow && LowerBandBB < prevLowerBandBB;

                prevLowerBandBB = (double)LowerBandBB;

                return (presqueezein, presqueezeout, (originalSqueezein || originalSqueezeout));

                //(if ExtrSqueezein then Color.dark_red
                //else if extrsqueezeout then color.dark_red
                //else if originalSqueezein then Color.red
                //else if originalSqueezeout then color.red
                //else if presqueezein then Color.pink
                //else if presqueezeout then color.yellow else Color.green);

            }
            catch (Exception)
            {
                return (true, true, true);
            }
        }

        static double Choppiness(IList<Quote> quotes)
        {
            var length = 14;

            var Hmax = quotes.TakeLast(length).Select(r => r.High).Max();

            var Lmin = quotes.TakeLast(length).Select(r => r.Low).Min();

            var hlvalue = Hmax - Lmin;

            var truerange = TrueRangeCalculator(quotes);

            var log = Math.Log(truerange / (double)hlvalue);

            return (100 * log) / Math.Log(length);
        }

        static double TrueRangeCalculator(IList<Quote> quotes)
        {
            double tRange = 0;

            foreach (var quote in quotes.TakeLast(14))
            {
                double tr = (double)Math.Max(quote.High - quote.Low, Math.Max(Math.Abs(quote.High - quote.Close), Math.Abs(quote.Low - quote.Close)));
                tRange += tr;
            }
            return tRange;
        }
        private async Task<bool> PlaceOrderAsync(string symbol, bool buyCall, string instruction)
        {
            Console.WriteLine("trying to place order");
            if (instruction == "BUY_TO_OPEN" && !tradingParams.IsBuyAllowed)
            {
                Console.WriteLine("NOT BUYING BECAUSE BUYING IS NOT ALLOWED BY CONFIG");

                return false;
            }
            else if (instruction == "BUY_TO_OPEN" && DateTime.Now.TimeOfDay >= TimeSpan.Parse("14:50:00"))
            {
                Console.WriteLine($"NOT BUYING BECAUSE OF TIME >= 14:50 PM");
                return false;
            }
            else if (instruction == "SELL_TO_CLOSE" && !tradingParams.IsSellAllowed)
            {
                Console.WriteLine("NOT SELLING BECAUSE SELLING IS NOT ALLOWED BY CONFIG");
                return false;
            }

            TDAuthResult authResult = JsonConvert.DeserializeObject<TDAuthResult>(cache.Load("TDAmeritradeKey"));

            var decoded = HttpUtility.UrlDecode(authResult.security_code);

            var path = $"https://api.tdameritrade.com/v1/accounts/277090213/orders";

            var orderLegCol = new List<Orderlegcollection>();
            orderLegCol.Add(new Orderlegcollection()
            {
                orderLegType = "OPTION",
                legId = 0,
                instruction = instruction,
                quantity = 1,
                instrument = new Instrument()
                {
                    symbol = symbol,
                    assetType = "OPTION",
                    putCall = (buyCall) ? "CALL" : "PUT"
                }
            });
            var callOption = new OptionPutCallData()
            {
                complexOrderStrategyType = "NONE",
                orderStrategyType = "SINGLE",
                orderType = "MARKET",
                session = "NORMAL",
                duration = "DAY",
                orderLegCollection = orderLegCol.ToArray(),
            };

            var jsonContent = JsonConvert.SerializeObject(callOption);

            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authResult.access_token);

                var res = await client.PostAsync(path, content);
                switch (res.StatusCode)
                {
                    case HttpStatusCode.Created:
                        return true;
                    default:
                        Console.WriteLine($"{res.StatusCode} {res.ReasonPhrase}");
                        return false;
                }
            }
        }

        private async Task<string> GetLastOptionPriceAsync(string symbol)
        {
            //"SPY_071323P448"
            try
            {
                var strike = symbol.Substring(symbol.Length - 3, 3) + ".0";
                var OptionChainDate = DateTime.Now.ToString("yyyy-MM-dd");
                var expMonth = DateTime.Now.ToString("MMM", new CultureInfo("en-GB")).ToUpper().Substring(0, 3);

                TDAuthResult authResult = JsonConvert.DeserializeObject<TDAuthResult>(cache.Load("TDAmeritradeKey"));

                var decoded = HttpUtility.UrlDecode(authResult.security_code);

                var path = string.Empty;

                if (symbol.Contains("C"))
                {
                    path = $"https://api.tdameritrade.com/v1/marketdata/chains?apikey=BGBSDP5BNDZ91JQHAOIGCXHAAIZVYU3D&symbol=SPY&contractType=CALL&strikeCount=10&includeQuotes=FALSE&strategy=SINGLE&range=ALL&fromDate={OptionChainDate}&toDate={OptionChainDate}&expMonth={expMonth}&optionType=ALL";
                }
                else
                {
                    path = $"https://api.tdameritrade.com/v1/marketdata/chains?apikey=BGBSDP5BNDZ91JQHAOIGCXHAAIZVYU3D&symbol=SPY&contractType=PUT&strikeCount=10&includeQuotes=FALSE&strategy=SINGLE&range=ALL&fromDate={OptionChainDate}&toDate={OptionChainDate}&expMonth={expMonth}&optionType=ALL";
                }
                var req = new HttpRequestMessage(HttpMethod.Get, path);

                var expDateMap = string.Empty;
                if (symbol.Contains("C"))
                {
                    expDateMap = "callExpDateMap";
                }
                else
                {
                    expDateMap = "putExpDateMap";

                }
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authResult.access_token);
                using (var client = new HttpClient())
                {
                    try
                    {
                        var res = await client.SendAsync(req);
                        var OptionChainDateField = DateTime.Now.ToString("yyyy-MM-dd") + ":0";
                        switch (res.StatusCode)
                        {
                            case HttpStatusCode.OK:
                                var json = await res.Content.ReadAsStringAsync();
                                var converter = new ExpandoObjectConverter();
                                var jsonDom = JsonConvert.DeserializeObject<JObject>(json);
                                if (jsonDom["status"].ToString() == "FAILED") return string.Empty;
                                var s = jsonDom[expDateMap]![OptionChainDateField]![strike];
                                return s.First()!["mark"].ToString();
                        }
                    }
                    catch (Exception)
                    {


                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
                Console.WriteLine("No able to get options chain");
                if (e.Message.Contains("Unauthorized"))
                {
                    await SignInRefresh();
                }
            }
            return string.Empty;
        }
        private async Task<string> GetPutOptionChainAsync()
        {
            Console.WriteLine("Getting put opt chain");

            var OptionChainDate = DateTime.Now.ToString("yyyy-MM-dd");
            var expMonth = DateTime.Now.ToString("MMM", new CultureInfo("en-GB")).ToUpper().Substring(0, 3);

            TDAuthResult authResult = JsonConvert.DeserializeObject<TDAuthResult>(cache.Load("TDAmeritradeKey"));

            var decoded = HttpUtility.UrlDecode(authResult.security_code);

            var path = $"https://api.tdameritrade.com/v1/marketdata/chains?apikey=BGBSDP5BNDZ91JQHAOIGCXHAAIZVYU3D&symbol=SPY&contractType=PUT&strikeCount=2&includeQuotes=FALSE&strategy=SINGLE&range=ALL&fromDate={OptionChainDate}&toDate={OptionChainDate}&expMonth={expMonth}&optionType=ALL";

            var req = new HttpRequestMessage(HttpMethod.Get, path);

            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authResult.access_token);
            using (var client = new HttpClient())
            {
                var res = await client.SendAsync(req);
                var OptionChainDateField = DateTime.Now.ToString("yyyy-MM-dd") + ":0";
                switch (res.StatusCode)
                {
                    case HttpStatusCode.OK:
                        var json = await res.Content.ReadAsStringAsync();
                        var converter = new ExpandoObjectConverter();
                        var jsonDom = JsonConvert.DeserializeObject<JObject>(json);
                        var s = jsonDom["putExpDateMap"]![OptionChainDateField].First();
                        return s.First()[0]!["symbol"].ToString();
                        break;
                    default:
                        Console.WriteLine(path);
                        Console.WriteLine($"{res.StatusCode} {res.ReasonPhrase}");
                        break;
                }
            }

            return string.Empty;
        }

        private async Task<string> GetCallOptionChainAsync()
        {
            Console.WriteLine("Getting call opt chain");
            var OptionChainDate = DateTime.Now.ToString("yyyy-MM-dd");
            var expMonth = DateTime.Now.ToString("MMM", new CultureInfo("en-GB")).ToUpper().Substring(0, 3);

            TDAuthResult authResult = JsonConvert.DeserializeObject<TDAuthResult>(cache.Load("TDAmeritradeKey"));

            var decoded = HttpUtility.UrlDecode(authResult.security_code);

            var path = $"https://api.tdameritrade.com/v1/marketdata/chains?apikey=BGBSDP5BNDZ91JQHAOIGCXHAAIZVYU3D&symbol=SPY&contractType=CALL&strikeCount=2&includeQuotes=FALSE&strategy=SINGLE&range=ALL&fromDate={OptionChainDate}&toDate={OptionChainDate}&expMonth={expMonth}&optionType=ALL";

            var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authResult.access_token);
            using (var client = new HttpClient())
            {
                var res = await client.SendAsync(req);
                var OptionChainDateField = DateTime.Now.ToString("yyyy-MM-dd") + ":0";
                switch (res.StatusCode)
                {
                    case HttpStatusCode.OK:
                        var json = await res.Content.ReadAsStringAsync();
                        var converter = new ExpandoObjectConverter();
                        var jsonDom = JsonConvert.DeserializeObject<JObject>(json);
                        var s = jsonDom["callExpDateMap"]![OptionChainDateField].Last();
                        return s.First()[0]!["symbol"].ToString();
                        break;
                    default:
                        Console.WriteLine(path);
                        Console.WriteLine($"{res.StatusCode} {res.ReasonPhrase}");
                        break;
                }
            }
            return string.Empty;
        }

        private async Task<SecuritiesaccountRootobject> GetAccountDataAsync()
        {
            TDAuthResult authResult = JsonConvert.DeserializeObject<TDAuthResult>(cache.Load("TDAmeritradeKey"));

            var decoded = HttpUtility.UrlDecode(authResult.security_code);

            var path = "https://api.tdameritrade.com/v1/accounts/277090213?fields=orders";

            var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authResult.access_token);
            using (var client = new HttpClient())
            {
                var res = await client.SendAsync(req);

                switch (res.StatusCode)
                {
                    case HttpStatusCode.OK:
                        var json = await res.Content.ReadAsStringAsync();
                        return JsonConvert.DeserializeObject<SecuritiesaccountRootobject>(json);
                    default:
                        throw (new Exception($"{res.StatusCode} {res.ReasonPhrase}"));
                }
            }

            return new SecuritiesaccountRootobject() { };
        }
        bool IsFutureSymbol(string s)
        {
            return s.StartsWith("/");
        }

        void OpenBrowser(string url)
        {
            try
            {
                Process.Start(url);
            }
            catch
            {
                // hack because of this: https://github.com/dotnet/corefx/issues/10361
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    url = url.Replace("&", "^&");
                    Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Process.Start("xdg-open", url);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", url);
                }
                else
                {
                    throw;
                }
            }
        }
    }
}


//if (string.IsNullOrEmpty(putCall))
//{
//    var buySignal = string.Empty;

//        var ema8ValPrev = decimal.Round((decimal)ema8.ToList()[ema8.ToList().Count - 2].Ema, 2);
//        var ema13ValPrev = decimal.Round((decimal)ema13.ToList()[ema13.ToList().Count - 2].Ema, 2);

//        Console.WriteLine($"Prev EMAs were EMA8 ${ema8ValPrev} EMA13 ${ema13ValPrev}");
//        if (ema8ValPrev >= ema13ValPrev && ema13Val > ema8Val)
//        {
//            buySignal = "Put";
//        }
//        else if (ema13ValPrev >= ema8ValPrev && ema8Val > ema13Val)
//        {
//            buySignal = "Call";
//        }


//    if (buySignal == "Put")
//    {
//        var symbol = GetPutOptionChainAsync().GetAwaiter().GetResult();
//        Console.WriteLine("<<<<<< BUY PUT " + symbol);

//        //_ = await PlaceOrderAsync(symbol, "PUT", "BUY_TO_OPEN", bBandWidth);
//    }
//    else if (buySignal == "Call")
//    {
//        //Get option Chain
//        var symbol = GetCallOptionChainAsync().GetAwaiter().GetResult();
//        Console.WriteLine("<<<<<<< BUY CALL" + symbol);

//        //Buy a call
//        //_ = await PlaceOrderAsync(symbol, "CALL", "BUY_TO_OPEN", bBandWidth);
//    }
//}
//else if (putCall == "Call") //If the current trade is a CALL
//{
//    if (ema13Val > ema8Val)
//    {
//        //Sell the call
//        if (!string.IsNullOrEmpty(currentSymbol))
//        {
//            _ = await PlaceOrderAsync(currentSymbol, "CALL", "SELL_TO_CLOSE");
//            Console.WriteLine("<<<<<< SELL CALL " + currentSymbol);
//        }


//        //Get option Chain
//        var symbol = GetPutOptionChainAsync().GetAwaiter().GetResult();
//        Console.WriteLine(">>>>>> BUY PUT " + symbol);

//        //_ = await PlaceOrderAsync(symbol, "PUT", "BUY_TO_OPEN", bBandWidth);
//    }
//}
//else if (putCall == "Put")//If the current trade is a PUT
//{
//    if (ema8Val > ema13Val)
//    {
//        //Sell the put
//        if (!string.IsNullOrEmpty(currentSymbol))
//        {
//            _ = await PlaceOrderAsync(currentSymbol, "PUT", "SELL_TO_CLOSE");
//            Console.WriteLine("<<<<<<< SELL PUT " + currentSymbol);
//        }


//        //Get option Chain
//        var symbol = GetCallOptionChainAsync().GetAwaiter().GetResult();
//        Console.WriteLine(">>>>>> BUY CALL " + symbol);

//        //Buy a call
//        //_ = await PlaceOrderAsync(symbol, "CALL", "BUY_TO_OPEN", bBandWidth);
//    }
//}
//}
//Console.Write("null");