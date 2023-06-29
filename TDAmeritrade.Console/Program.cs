using Dapper;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Skender.Stock.Indicators;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using TDAmeritrade;
using TDAmeritrade.Models;

namespace TDConsole
{
    class Program : IDisposable
    {
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
        public Program()
        {
            cache = new TDUnprotectedCache();
            client = new TDAmeritradeClient(cache);
            _parser = new TDStreamJsonProcessor();
            quotes = new List<Quote>();
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
            await TradeAsync();

            Console.WriteLine("Type any key to exit");
            Console.ReadLine();
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
            Console.WriteLine($"IsSignedIn : {client.IsSignedIn}");
        }

        public async Task TradeAsync()
        {
            var firstTrade = false;
            var trading = false;
            var putCall = string.Empty;
            var currentSymbol = string.Empty;
            //var currentSymbol = "SPY_062623C432";

            TimeSpan startTradeTime = TimeSpan.Parse("08:30:00");
            TimeSpan endTradeTime = TimeSpan.Parse("15:01:00");
            TimeSpan lasMinuteTrade = TimeSpan.Parse("15:00:00");
            TimeSpan timeNow = DateTime.Now.TimeOfDay;

            await client.SignIn();
            Console.WriteLine($"IsSignedIn : {client.IsSignedIn}");

            var symbols = "SPY";

            char format = '1';

            var qos = '0';
            int qosInt = 0;

            int.TryParse(qos.ToString(), out qosInt);

            var dateTimeIni = DateTime.Now.ToString("yyyy/MM/dd 08:00:00");
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
                            //await socket.SubscribeQuote(symbols);
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
                    if (format == '0' || format == '1')
                    {
                        try
                        {
                            var res = JsonConvert.DeserializeObject<Rootobject>(m);
                            if (res.data != null)
                            {
                                var values = res.data[0].content[0];

                                var datecst = TDHelpers.ToCST(TDHelpers.FromUnixTimeMilliseconds(values.ChartTime)).AddHours(-5).ToString("MM/dd/yyyy HH:mm:ss");
                                var sql = $"INSERT INTO AmeritradeData values ('{datecst}',{values.OpenPrice},{values.HighPrice},{values.LowPrice},{values.ClosePrice}, {values.Volume}, {values.Seq}, '{values.Symbol}' )";
                                using (var connection = new SqlConnection("Data Source=TONYDURAN\\SQLEXPRESS;Initial Catalog=Ameritrade;Integrated Security=True;TrustServerCertificate=true;"))
                                {
                                    var affectedRows = connection.Execute(sql);
                                    //Console.WriteLine(sql);
                                    Console.WriteLine($"Affected Rows: {affectedRows}");
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

                                //IEnumerable<PivotsResult> results = quotes.GetPivots(2, 2, 10, EndType.HighLow);

                                //Console.WriteLine($"Date: {results.LastOrDefault().Date}");
                                //Console.WriteLine($"HighTrend: {results.LastOrDefault().HighTrend}");
                                //Console.WriteLine($"HighLine: {results.LastOrDefault().HighLine}");
                                //Console.WriteLine($"HighPoint: {results.LastOrDefault().HighPoint}");
                                //Console.WriteLine($"LowTrend: {results.LastOrDefault().LowTrend}");
                                //Console.WriteLine($"LowLine: {results.LastOrDefault().LowLine}");
                                //Console.WriteLine($"LowPoint: {results.LastOrDefault().LowPoint}");

                                timeNow = DateTime.Now.TimeOfDay;

                                if (timeNow <= startTradeTime || timeNow >= endTradeTime) return;

                                if (ema8.Last().Ema != null && ema13.Last().Ema != null)
                                {
                                    var ema8Val = decimal.Round((decimal)ema8.Last().Ema, 2);
                                    var ema13Val = decimal.Round((decimal)ema13.Last().Ema, 2);
                                    var ema8dif = decimal.Subtract(ema8Val, ema13Val);
                                    var ema13dif = decimal.Subtract(ema13Val, ema8Val);

                                    Console.WriteLine($"SMA8 on {ema8.Last().Date} was ${ema8Val}");
                                    Console.WriteLine($"SMA13 on {ema13.Last().Date} was ${ema13Val}");

                                    var currentOrderTraded = (GetCurrentOrdersTradedAsync().GetAwaiter().GetResult().FirstOrDefault() != null)
                                        ? GetCurrentOrdersTradedAsync().GetAwaiter().GetResult().FirstOrDefault().Symbol
                                        : string.Empty;

                                    //IF BETWEEN 8:30 AND 8:40 AM CST AND DIFFERENCE BETWEEND EMAS IS LESS THAN .05 NOT BUY
                                    if (timeNow <= TimeSpan.Parse("08:40:00"))
                                    //No trade set yet // at the begin of the trade day
                                    {
                                        if (!string.IsNullOrEmpty(currentOrderTraded)) return;

                                        if (ema8Val > ema13Val && ema8dif >= .05m)
                                        {
                                            //Get option Chain
                                            var symbol = GetCallOptionChainAsync().GetAwaiter().GetResult();
                                            Console.WriteLine("buy call " + symbol);

                                            //Buy a call
                                            var placed = await PlaceOrderAsync(symbol, "CALL", "BUY_TO_OPEN");
                                            trading = true;
                                        }
                                        else if (ema13Val > ema8Val && ema13dif >= .05m)
                                        {
                                            //Get option Chain
                                            var symbol = GetPutOptionChainAsync().GetAwaiter().GetResult();
                                            Console.WriteLine("buy put " + symbol);
                                            //Buy a Put
                                            var placed = await PlaceOrderAsync(symbol, "PUT", "BUY_TO_OPEN");
                                            trading = true;
                                        }
                                        return;
                                    }


                                    if (string.IsNullOrEmpty(currentOrderTraded))
                                    {
                                        trading = false;
                                        putCall = string.Empty;
                                        currentSymbol = string.Empty;
                                    }
                                    else
                                    {
                                        firstTrade = true;
                                        trading = true;
                                        currentSymbol = currentOrderTraded;
                                        putCall = currentSymbol.Contains("C") ? "Call" : "Put";
                                    };

                                    if (timeNow >= lasMinuteTrade)
                                    {
                                        if (putCall == "Call")
                                        {
                                            var placed = await PlaceOrderAsync(currentSymbol, "CALL", "SELL_TO_CLOSE");
                                        }
                                        else
                                        {
                                            var placed = await PlaceOrderAsync(currentSymbol, "PUT", "SELL_TO_CLOSE");
                                        }
                                    }
                                    else // there is at least one trade in execution
                                    {
                                        var placed = false;

                                        if (string.IsNullOrEmpty(putCall))
                                        {
                                            var ema8ValPrev = decimal.Round((decimal)ema8.ToList()[ema8.ToList().Count - 2].Ema, 2);
                                            var ema13ValPrev = decimal.Round((decimal)ema13.ToList()[ema13.ToList().Count - 2].Ema, 2);
                                            Console.WriteLine($"Prev SMA8 on {ema8.Last().Date} was ${ema8ValPrev}");
                                            Console.WriteLine($"Prev SMA13 on {ema13.Last().Date} was ${ema13ValPrev}");

                                            if (ema8ValPrev >= ema13ValPrev && ema13Val > ema8Val)
                                            {
                                                //Get option Chain
                                                var symbol = GetPutOptionChainAsync().GetAwaiter().GetResult();
                                                Console.WriteLine("buy put " + symbol);

                                                placed = await PlaceOrderAsync(symbol, "PUT", "BUY_TO_OPEN");
                                            }
                                            else if (ema13ValPrev >= ema8ValPrev && ema8Val > ema13Val)
                                            {
                                                //Get option Chain
                                                var symbol = GetCallOptionChainAsync().GetAwaiter().GetResult();
                                                Console.WriteLine("buy call " + symbol);

                                                //Buy a call
                                                placed = await PlaceOrderAsync(symbol, "CALL", "BUY_TO_OPEN");
                                            }
                                        }
                                        else if (putCall == "Call") //If the current trade is a CALL
                                        {
                                            if (ema13Val > ema8Val)
                                            {
                                                //Sell the call
                                                if (!string.IsNullOrEmpty(currentSymbol))
                                                {
                                                    placed = await PlaceOrderAsync(currentSymbol, "CALL", "SELL_TO_CLOSE");
                                                    Console.WriteLine("sell call " + currentSymbol);
                                                }

                                                if (timeNow <= TimeSpan.Parse("14:50:00")) return;
                                                //Get option Chain
                                                var symbol = GetPutOptionChainAsync().GetAwaiter().GetResult();
                                                Console.WriteLine("buy put " + symbol);

                                                placed = await PlaceOrderAsync(symbol, "PUT", "BUY_TO_OPEN");
                                            }
                                        }
                                        else if (putCall == "Put")//If the current trade is a PUT
                                        {
                                            if (ema8Val > ema13Val)
                                            {
                                                //Sell the put
                                                if (!string.IsNullOrEmpty(currentSymbol))
                                                {
                                                    placed = await PlaceOrderAsync(currentSymbol, "PUT", "SELL_TO_CLOSE");
                                                    Console.WriteLine("sell put " + currentSymbol);
                                                }

                                                if (timeNow <= TimeSpan.Parse("14:50:00")) return;
                                                //Get option Chain
                                                var symbol = GetCallOptionChainAsync().GetAwaiter().GetResult();
                                                Console.WriteLine("buy call " + symbol);

                                                //Buy a call
                                                placed = await PlaceOrderAsync(symbol, "CALL", "BUY_TO_OPEN");
                                            }
                                        }
                                    }
                                }



                                //if (accountData.securitiesAccount.currentBalances.cashAvailableForTrading > 0)
                                //{

                                //}
                            }
                            Console.Write("null");
                        }
                        catch
                        {
                            Console.Write("null");
                        }
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

        //private async Task<bool> GetOrdersTradedAsync()
        //{
        //    await client.SignIn();

        //    var OptionChainDate = DateTime.Now.ToString("yyyy-MM-dd");
        //    var expMonth = DateTime.Now.ToString("MMM").ToUpper().Substring(0, 3);

        //    TDAuthResult authResult = JsonConvert.DeserializeObject<TDAuthResult>(cache.Load("TDAmeritradeKey"));

        //    var decoded = HttpUtility.UrlDecode(authResult.security_code);

        //    var path = $"https://api.tdameritrade.com/v1/accounts/277090213/orders?fromEnteredTime={OptionChainDate}&toEnteredTime={OptionChainDate}&status=FILLED";

        //    var req = new HttpRequestMessage(HttpMethod.Get, path);
        //    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authResult.access_token);
        //    using (var client = new HttpClient())
        //    {
        //        var res = await client.SendAsync(req);

        //        switch (res.StatusCode)
        //        {
        //            case HttpStatusCode.OK:
        //                var json = await res.Content.ReadAsStringAsync();
        //                var jsonDom = JsonConvert.DeserializeObject<List<OrdersData>>(json);
        //                return (jsonDom.Count == 0) ? true : false;
        //                break;
        //            default:
        //                throw (new Exception($"{res.StatusCode} {res.ReasonPhrase}"));
        //        }
        //    }

        //    return false;
        //}

        private async Task<List<PendingOrder>> GetCurrentOrdersTradedAsync()
        {
            await client.SignIn();

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
                                instruction = grp.FirstOrDefault().orderLegCollection.First().instruction,
                                quantity = (grp.FirstOrDefault().orderLegCollection.First().instruction)
                                    .Contains("SELL_TO_CLOSE") ? grp.FirstOrDefault().orderLegCollection.First().quantity * -1
                                    : grp.FirstOrDefault().orderLegCollection.First().quantity
                            })
                            .OrderBy(s => s.symbol);

                        var pendingOrders = x.GroupBy(s => (s.symbol))
                            .Select(grp => new PendingOrder
                            {
                                Symbol = grp.FirstOrDefault().symbol,
                                Quantity = grp.Sum(a => a.quantity)
                            })
                            .Where(c => c.Quantity > 0);
                        return pendingOrders.ToList();
                        break;
                    default:
                        throw (new Exception($"{res.StatusCode} {res.ReasonPhrase}"));
                }
            }

            return null;

        }

        private async Task<bool> PlaceOrderAsync(string symbol, string putCall, string instruction)
        {
            await client.SignIn();

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
            //var req = new HttpRequestMessage(HttpMethod.Get, path)
            //{ Content = new StringContent(jsonContent, Encoding.UTF8, "application/json") };

            //req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authResult.access_token);
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

        private async Task<string> GetPutOptionChainAsync()
        {
            await client.SignIn();

            var OptionChainDate = DateTime.Now.ToString("yyyy-MM-dd");
            var expMonth = DateTime.Now.ToString("MMM").ToUpper().Substring(0, 3);

            TDAuthResult authResult = JsonConvert.DeserializeObject<TDAuthResult>(cache.Load("TDAmeritradeKey"));

            var decoded = HttpUtility.UrlDecode(authResult.security_code);

            var path = $"https://api.tdameritrade.com/v1/marketdata/chains?apikey=BGBSDP5BNDZ91JQHAOIGCXHAAIZVYU3D&symbol=SPY&contractType=PUT&strikeCount=2&includeQuotes=FALSE&strategy=SINGLE&range=ALL&fromDate={OptionChainDate}&toDate={OptionChainDate}&expMonth={expMonth}&optionType=ALL";

            //var dict = new Dictionary<string, string>
            //    {
            //        { "grant_type", "refresh_token" },
            //        { "access_type", "" },
            //        { "client_id", $"{AuthResult.consumer_key}@AMER.OAUTHAP" },
            //        { "redirect_uri", AuthResult.refresh_token },
            //        { "refresh_token", AuthResult.refresh_token },
            //        { "code", decoded }
            //    };

            var req = new HttpRequestMessage(HttpMethod.Get, path); // { Content = new FormUrlEncodedContent(dict) };
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
            await client.SignIn();

            var OptionChainDate = DateTime.Now.ToString("yyyy-MM-dd");
            var expMonth = DateTime.Now.ToString("MMM").ToUpper().Substring(0, 3);

            TDAuthResult authResult = JsonConvert.DeserializeObject<TDAuthResult>(cache.Load("TDAmeritradeKey"));

            var decoded = HttpUtility.UrlDecode(authResult.security_code);

            var path = $"https://api.tdameritrade.com/v1/marketdata/chains?apikey=BGBSDP5BNDZ91JQHAOIGCXHAAIZVYU3D&symbol=SPY&contractType=CALL&strikeCount=2&includeQuotes=FALSE&strategy=SINGLE&range=ALL&fromDate={OptionChainDate}&toDate={OptionChainDate}&expMonth={expMonth}&optionType=ALL";

            //var dict = new Dictionary<string, string>
            //    {
            //        { "grant_type", "refresh_token" },
            //        { "access_type", "" },
            //        { "client_id", $"{AuthResult.consumer_key}@AMER.OAUTHAP" },
            //        { "redirect_uri", AuthResult.refresh_token },
            //        { "refresh_token", AuthResult.refresh_token },
            //        { "code", decoded }
            //    };

            var req = new HttpRequestMessage(HttpMethod.Get, path); // { Content = new FormUrlEncodedContent(dict) };
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
            await client.SignIn();

            TDAuthResult authResult = JsonConvert.DeserializeObject<TDAuthResult>(cache.Load("TDAmeritradeKey"));

            var decoded = HttpUtility.UrlDecode(authResult.security_code);

            var path = "https://api.tdameritrade.com/v1/accounts/277090213?fields=orders";

            //var dict = new Dictionary<string, string>
            //    {
            //        { "grant_type", "refresh_token" },
            //        { "access_type", "" },
            //        { "client_id", $"{AuthResult.consumer_key}@AMER.OAUTHAP" },
            //        { "redirect_uri", AuthResult.refresh_token },
            //        { "refresh_token", AuthResult.refresh_token },
            //        { "code", decoded }
            //    };

            var req = new HttpRequestMessage(HttpMethod.Get, path); // { Content = new FormUrlEncodedContent(dict) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authResult.access_token);
            using (var client = new HttpClient())
            {
                var res = await client.SendAsync(req);

                switch (res.StatusCode)
                {
                    case HttpStatusCode.OK:
                        var json = await res.Content.ReadAsStringAsync();
                        return JsonConvert.DeserializeObject<SecuritiesaccountRootobject>(json);
                        //authResult.access_token = result.access_token;
                        //cache.Save("TDAmeritradeKey", JsonConvert.SerializeObject(authResult));
                        //IsSignedIn = true;
                        //HasConsumerKey = true;
                        //OnSignedIn(true);
                        break;
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