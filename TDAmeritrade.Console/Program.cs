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

            //var bBandPreviousWidth = decimal.Round((decimal)quotes.GetBollingerBands().LastOrDefault().Width, 9);

            //var res1 = quotes.GetBollingerBands();
            //IEnumerable<PivotsResult> results = quotes.GetPivots(2, 2, 20, EndType.HighLow);

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
                        //    if (m.Contains("BOOK"))
                        //    {
                        //        var optionData = JsonConvert.DeserializeObject<>(m);
                        //        return;
                        //    }

                        var res = JsonConvert.DeserializeObject<Rootobject>(m);

                        if (res.data != null && m.Contains("CHART_EQUITY"))
                        {
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

                            timeNow = DateTime.Now.TimeOfDay;

                            //If time is not between 8:30 am and 15:00  then return
                            if (timeNow <= startTradeTime || timeNow >= endTradeTime) return;

                            //If EMAS are not ready then return
                            if (ema8.Last().Ema == null || ema13.Last().Ema == null) return;

                            //IEnumerable<BollingerBandsResult> bBands = quotes.GetBollingerBands();
                            //IEnumerable<PivotsResult> pivots = quotes.GetPivots(2, 2, 20, EndType.HighLow);

                            var ema8Val = decimal.Round((decimal)ema8.LastOrDefault().Ema, 2);
                            var ema13Val = decimal.Round((decimal)ema13.LastOrDefault().Ema, 2);

                            var ema8dif = decimal.Subtract(ema8Val, ema13Val);
                            var ema13dif = decimal.Subtract(ema13Val, ema8Val);

                            //var bBandWidth = decimal.Round((decimal)bBands.LastOrDefault().Width, 9);

                            Console.WriteLine("---------------------------------");
                            Console.WriteLine(DateTime.Now.ToString());
                            //if (bBandWidth < tradingParams.BBandRedValue)
                            //{ BBandRedCount++; }
                            //else { BBandRedCount = 0; }

                            if (ema8dif >= 0)
                            { Console.WriteLine($"EMA8 ${ema8Val} >= EMA13 {ema13Val} Bullish"); }
                            else if (ema13dif >= 0)
                            { Console.WriteLine($"EMA13 ${ema13Val} >= SMA8 {ema8Val} Bearish"); }

                            //Console.WriteLine($"BollingerBand Width {bBandWidth} in squeeze =  ({bBandWidth < tradingParams.BBandRedValue}) RED Cont: {BBandRedCount}");

                            //Console.WriteLine($"HighLine : {pivots.ToList()[pivots.ToList().Count - 4].HighLine?.ToString()} | HighPoint : {pivots.ToList()[pivots.ToList().Count - 4].HighPoint?.ToString()}");
                            //Console.WriteLine($"LowLine : {pivots.ToList()[pivots.ToList().Count - 4].LowLine?.ToString()} | LowPoint : {pivots.ToList()[pivots.ToList().Count - 4].LowPoint?.ToString()}");

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

                            ////IF BETWEEN 8:30 AND 8:40 AM CST AND DIFFERENCE BETWEEND EMAS IS LESS THAN .05 NOT BUY
                            //if (timeNow <= TimeSpan.Parse("08:40:00"))
                            ////No trade set yet // at the begin of the trade day
                            //{
                            //    if (string.IsNullOrEmpty(currentOrderTraded))
                            //    {
                            //        if (ema8Val > ema13Val && ema8dif >= .05m)
                            //        {
                            //            //Get option Chain
                            //            var symbol = GetCallOptionChainAsync().GetAwaiter().GetResult();
                            //            Console.WriteLine("buy call " + symbol);

                            //            //Buy a call
                            //            var placed = await PlaceOrderAsync(symbol, "CALL", "BUY_TO_OPEN", bBandWidth);
                            //        }
                            //        else if (ema13Val > ema8Val && ema13dif >= .05m)
                            //        {
                            //            //Get option Chain
                            //            var symbol = GetPutOptionChainAsync().GetAwaiter().GetResult();
                            //            Console.WriteLine("buy put " + symbol);
                            //            //Buy a Put
                            //            var placed = await PlaceOrderAsync(symbol, "PUT", "BUY_TO_OPEN", bBandWidth);
                            //        }
                            //        return;
                            //    }
                            //}

                            if (string.IsNullOrEmpty(currentOrderTraded))
                            {
                                putCall = string.Empty;
                                currentSymbol = string.Empty;
                            }
                            else
                            {
                                currentSymbol = currentOrderTraded;
                                putCall = currentSymbol.Contains("C") ? "Call" : "Put";
                            };

                            if (timeNow >= lasMinuteTrade) // Last minute to sell everything
                            {
                                if (!string.IsNullOrEmpty(currentSymbol))
                                {
                                    if (putCall == "Call")
                                    { _ = await PlaceOrderAsync(currentSymbol, "CALL", "SELL_TO_CLOSE"); }
                                    else
                                    { _ = await PlaceOrderAsync(currentSymbol, "PUT", "SELL_TO_CLOSE"); }
                                }
                                return;
                            }

                            if (string.IsNullOrEmpty(putCall))
                            {
                                var buySignal = string.Empty;
                                if (tradingParams.UseBBandToBuy)
                                {
                                    //if (bBandWidth < tradingParams.BBandRedValue) return;

                                    if (ema8dif > 0)
                                    { buySignal = "Call"; }
                                    else if (ema13dif > 0)
                                    { buySignal = "Put"; }
                                }
                                else
                                {
                                    var ema8ValPrev = decimal.Round((decimal)ema8.ToList()[ema8.ToList().Count - 2].Ema, 2);
                                    var ema13ValPrev = decimal.Round((decimal)ema13.ToList()[ema13.ToList().Count - 2].Ema, 2);

                                    Console.WriteLine($"Prev EMAs were EMA8 ${ema8ValPrev} EMA13 ${ema13ValPrev}");
                                    if (ema8ValPrev >= ema13ValPrev && ema13Val > ema8Val)
                                    {
                                        buySignal = "Put";
                                    }
                                    else if (ema13ValPrev >= ema8ValPrev && ema8Val > ema13Val)
                                    {
                                        buySignal = "Call";
                                    }
                                }

                                if (buySignal == "Put")
                                {
                                    var symbol = GetPutOptionChainAsync().GetAwaiter().GetResult();
                                    Console.WriteLine("<<<<<< BUY PUT " + symbol);

                                    //_ = await PlaceOrderAsync(symbol, "PUT", "BUY_TO_OPEN", bBandWidth);
                                }
                                else if (buySignal == "Call")
                                {
                                    //Get option Chain
                                    var symbol = GetCallOptionChainAsync().GetAwaiter().GetResult();
                                    Console.WriteLine("<<<<<<< BUY CALL" + symbol);

                                    //Buy a call
                                    //_ = await PlaceOrderAsync(symbol, "CALL", "BUY_TO_OPEN", bBandWidth);
                                }
                            }
                            else if (putCall == "Call") //If the current trade is a CALL
                            {
                                if (ema13Val > ema8Val)
                                {
                                    //Sell the call
                                    if (!string.IsNullOrEmpty(currentSymbol))
                                    {
                                        _ = await PlaceOrderAsync(currentSymbol, "CALL", "SELL_TO_CLOSE");
                                        Console.WriteLine("<<<<<< SELL CALL " + currentSymbol);
                                    }


                                    //Get option Chain
                                    var symbol = GetPutOptionChainAsync().GetAwaiter().GetResult();
                                    Console.WriteLine(">>>>>> BUY PUT " + symbol);

                                    //_ = await PlaceOrderAsync(symbol, "PUT", "BUY_TO_OPEN", bBandWidth);
                                }
                            }
                            else if (putCall == "Put")//If the current trade is a PUT
                            {
                                if (ema8Val > ema13Val)
                                {
                                    //Sell the put
                                    if (!string.IsNullOrEmpty(currentSymbol))
                                    {
                                        _ = await PlaceOrderAsync(currentSymbol, "PUT", "SELL_TO_CLOSE");
                                        Console.WriteLine("<<<<<<< SELL PUT " + currentSymbol);
                                    }


                                    //Get option Chain
                                    var symbol = GetCallOptionChainAsync().GetAwaiter().GetResult();
                                    Console.WriteLine(">>>>>> BUY CALL " + symbol);

                                    //Buy a call
                                    //_ = await PlaceOrderAsync(symbol, "CALL", "BUY_TO_OPEN", bBandWidth);
                                }
                            }
                        }
                        //Console.Write("null");
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

                var expMonth = DateTime.Now.ToString("MMM").ToUpper().Substring(0, 3);

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

        private async Task<bool> PlaceOrderAsync(string symbol, string putCall, string instruction, decimal bBandValue = 1)
        {
            if (instruction == "BUY_TO_OPEN" && !tradingParams.IsBuyAllowed)
            {
                Console.WriteLine("NOT BUYING BECAUSE BUYING IS NOT ALLOWED BY CONFIG");
                return false;
            }
            else if (instruction == "BUY_TO_OPEN" && tradingParams.UseBBandToBuy && ((bBandValue == 0) ? 1 : bBandValue) < tradingParams.BBandRedValue)
            {
                Console.WriteLine($"NOT BUYING BECAUSE OF BBAND VALUE OF {bBandValue}");
                return false;
            }
            else if (instruction == "BUY_TO_OPEN" && DateTime.Now.TimeOfDay >= TimeSpan.Parse("14:50:00"))
            {
                Console.WriteLine($"NOT BUYING BECAUSE OF TIME >= 14:50 PM");
                return false;
            }
            else if (instruction == "SELL_TO_CLOSE" && !tradingParams.IsSellAllowed)
            {
                Console.WriteLine("NOT BUYING BECAUSE SELLING IS NOT ALLOWED BY CONFIG");
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
                    putCall = putCall
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
                        throw (new Exception($"{res.StatusCode} {res.ReasonPhrase}"));
                }
            }

            return false;
        }

        private async Task<string> GetLastOptionPriceAsync(string symbol)
        {
            //"SPY_071323P448"
            try
            {
                var strike = symbol.Substring(symbol.Length - 3, 3) + ".0";
                var OptionChainDate = DateTime.Now.ToString("yyyy-MM-dd");
                var expMonth = DateTime.Now.ToString("MMM").ToUpper().Substring(0, 3);

                TDAuthResult authResult = JsonConvert.DeserializeObject<TDAuthResult>(cache.Load("TDAmeritradeKey"));

                var decoded = HttpUtility.UrlDecode(authResult.security_code);

                var path = $"https://api.tdameritrade.com/v1/marketdata/chains?apikey=BGBSDP5BNDZ91JQHAOIGCXHAAIZVYU3D&symbol=SPY&contractType=PUT&strikeCount=10&includeQuotes=FALSE&strategy=SINGLE&range=ALL&fromDate={OptionChainDate}&toDate={OptionChainDate}&expMonth={expMonth}&optionType=ALL";

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
                            if (jsonDom["status"].ToString() == "FAILED") return string.Empty;
                            var s = jsonDom["putExpDateMap"]![OptionChainDateField]![strike];
                            return s.First()!["mark"].ToString();
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
            return string.Empty;
        }
        private async Task<string> GetPutOptionChainAsync()
        {
            var OptionChainDate = DateTime.Now.ToString("yyyy-MM-dd");
            var expMonth = DateTime.Now.ToString("MMM").ToUpper().Substring(0, 3);

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
                        throw (new Exception($"{res.StatusCode} {res.ReasonPhrase}"));
                }
            }

            return string.Empty;
        }

        private async Task<string> GetCallOptionChainAsync()
        {
            var OptionChainDate = DateTime.Now.ToString("yyyy-MM-dd");
            var expMonth = DateTime.Now.ToString("MMM").ToUpper().Substring(0, 3);

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
                        throw (new Exception($"{res.StatusCode} {res.ReasonPhrase}"));
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