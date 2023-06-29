using Dapper;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Skender.Stock.Indicators;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
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
            TDAuthResult authResult = JsonConvert.DeserializeObject<TDAuthResult>(cache.Load("TDAmeritradeKey"));
            if (authResult == null)
            {
                await SignIn();

            }
            await RecordStream();

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

        public async Task RecordStream()
        {
            await client.SignIn();
            Console.WriteLine($"IsSignedIn : {client.IsSignedIn}");

            var symbols = "SPY";// Console.ReadLine();

            var path = @"C:\Users\anton\Desktop\data"; // Console.ReadLine();

            char format = '1'; //Console.ReadKey().KeyChar;

            var qos = '0'; // Console.ReadKey().KeyChar;

            int qosInt = 0;
            int.TryParse(qos.ToString(), out qosInt);

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
                            await socket.SubscribeChart(symbols, IsFutureSymbol(symbols) ? TDChartSubs.CHART_FUTURES : TDChartSubs.CHART_EQUITY);
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

                socket.OnJsonSignal += (m) =>
                {
                    if (format == '0' || format == '1')
                    {
                        try
                        {
                            if (!m.Contains("CHART_EQUITY")) return;

                            var res = JsonConvert.DeserializeObject<Rootobject>(m);
                            if (res.data != null)
                            {
                                var values = res.data[0].content[0];

                                var datecst = TDHelpers.ToCST(TDHelpers.FromUnixTimeMilliseconds(values.ChartTime)).AddHours(-5).ToString("MM/dd/yyyy HH:mm:ss");
                                var sql = $"INSERT INTO AmeritradeData values ('{datecst}',{values.OpenPrice},{values.HighPrice},{values.LowPrice},{values.ClosePrice}, {values.Volume}, {values.Seq}, '{values.Symbol}' )";
                                using (var connection = new SqlConnection("Data Source=TONYDURAN\\SQLEXPRESS;Initial Catalog=Ameritrade;Integrated Security=True;TrustServerCertificate=true;"))
                                {
                                    var affectedRows = connection.Execute(sql);
                                    Console.WriteLine(sql);
                                    Console.WriteLine($"Affected Rows: {affectedRows}");
                                }
                            }
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