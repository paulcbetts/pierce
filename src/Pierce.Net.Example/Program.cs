using System;
using Pierce.Net;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;

namespace Pierce.Net.Example
{
    class MainClass
    {
        public static void Main(string[] args)
        {
            var queue = new RequestQueue();

            var google = new Uri("http://www.google.com");
            var yahoo = new Uri("http://www.yahoo.com");

            for (var x = 1; x <= 16; x++)
            {
                Thread.Sleep(200);

                var request_number = x;
                var uri = x % 2 == 0 ?
                    google :
                    yahoo;

                queue.Add(new Request
                {
                    Uri = uri,
                    OnResponse = response =>
                    {
                        string result = Encoding.UTF8.GetString(response.Data);
                        Console.WriteLine("received response {0}, {1}", request_number, result.Substring(0, 48));
                    },
                });
            };

            Console.ReadLine();
        }
    }
}
