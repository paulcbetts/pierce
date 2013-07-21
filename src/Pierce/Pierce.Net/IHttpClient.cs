using System;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Text;

namespace Pierce.Net
{
    public enum Priority
    {
        Low,
        Normal,
        High,
    }

    public abstract class Request
    {
        public Request()
        {
            Priority = Priority.Normal;
            ShouldCache = true;
        }

        public Uri Uri { get; set; }
        public Priority Priority { get; set; }
        public int Sequence { get; set; }
        public bool ShouldCache { get; set; }
        public bool IsCanceled { get; private set; }

        public abstract Response Parse(NetworkResponse response);
        public abstract void SetResponse(Response response);

        public void Cancel()
        {
            IsCanceled = true;
        }

        public override string ToString()
        {
            return String.Format("{0}:{1}", Sequence, Uri);
        }
    }

    public abstract class Request<T> : Request
    {
        public Action<T> OnResponse { get; set; }

        public override void SetResponse(Response response)
        {
            var typed_response = response as Response<T>;
            var result = typed_response.Result;

            OnResponse(result);
        }

        protected static CacheEntry GetCacheEntry(NetworkResponse response)
        {
            return new CacheEntry
            {
                Data = response.Data,
                Headers = response.Headers,
            };
        }
    }

    public class StringRequest : Request<string>
    {
        public override Response Parse(NetworkResponse response)
        {
            return new Response<string>
            {
                CacheEntry = GetCacheEntry(response),
                Result = Encoding.UTF8.GetString(response.Data),
            };
        }
    }

    public class Response
    {
        public CacheEntry CacheEntry { get; set; }
    }

    public class Response<T> : Response
    {
        public T Result { get; set; }
    }

    public class NetworkResponse
    {
        public int StatusCode { get; set; }
        public byte[] Data { get; set; }
        public WebHeaderCollection Headers { get; set; }
        public bool NotModified { get; set; }
    }

    public class Network
    {
        public NetworkResponse Execute(Request request)
        {
            var response = new NetworkResponse();

            try
            {
                var client = new WebClient();
                response.Data = client.DownloadData(request.Uri);
                response.StatusCode = 200;
            }
            catch (Exception)
            {
                response.StatusCode = 500;
            }

            return response;
        }
    }

    public class CacheEntry
    {
        public byte[] Data { get; set; }
        public WebHeaderCollection Headers { get; set; }
    }

    public class Cache
    {
        private readonly IDictionary<object, CacheEntry> _dictionary = new Dictionary<object, CacheEntry>();

        public CacheEntry this [object key]
        {
            get
            {
                lock (_dictionary)
                {
                    return _dictionary.ContainsKey(key) ? _dictionary[key] : null;
                }
            }

            set
            {
                lock (_dictionary)
                {
                    _dictionary[key] = value;
                }
            }
        }
    }

    public class RequestQueue
    {
        private readonly BlockingCollection<Request> _cache_queue = new BlockingCollection<Request>();
        private readonly BlockingCollection<Request> _network_queue = new BlockingCollection<Request>();
        private readonly IDictionary<Uri, List<Request>> _requests = new Dictionary<Uri, List<Request>>();

        private readonly Cache _cache;
        private readonly Network _network;

        private int sequence;

        public RequestQueue(Cache cache = null, Network network = null)
        {
            _cache = cache ?? new Cache();
            _network = network ?? new Network();

            Task.Factory.StartNew(CacheConsumer);

            Task.Factory.StartNew(NetworkConsumer);
            Task.Factory.StartNew(NetworkConsumer);
        }

        public Request Add(Request request)
        {
            request.Sequence = Interlocked.Increment(ref sequence);
            Console.WriteLine("Add(): {0}", request);

            if (!request.ShouldCache)
            {
                _network_queue.Add(request);
                return request;
            }

            var key = request.Uri;
            lock (_requests)
            {
                List<Request> list;
                if (_requests.TryGetValue(key, out list))
                {
                    list = list ?? new List<Request>();
                    list.Add(request);

                    _requests[key] = list;
                }
                else
                {
                    _requests[key] = null;
                    _cache_queue.Add(request);
                }                
            }

            return request;
        }

        private void CacheConsumer()
        {
            foreach (var request in _cache_queue.GetConsumingEnumerable())
            {
                if (request.IsCanceled)
                {
                    continue;
                }

                var key = request.Uri;
                CacheEntry entry = _cache[key];
                if (entry == null)
                {
                    _network_queue.Add(request);
                    continue;
                }

                var response = request.Parse(new NetworkResponse
                {
                    Data = entry.Data,
                    Headers = entry.Headers,
                });

                request.SetResponse(response);
                Complete(request);
            }
        }

        private void NetworkConsumer()
        {
            foreach (var request in _network_queue.GetConsumingEnumerable())
            {
                Console.WriteLine("NetworkConsumer: {0}", request);

                if (request.IsCanceled)
                {
                    continue;
                }

                var network_response = _network.Execute(request);
                var response = request.Parse(network_response);

                if (request.ShouldCache && response.CacheEntry != null)
                {
                    _cache[request.Uri] = response.CacheEntry;
                }

                request.SetResponse(response);
                Complete(request);
            }
        }

        private void Complete(Request request)
        {
            if (request.ShouldCache)
            {
                var key = request.Uri;
                List<Request> list;

                lock (_requests)
                {
                    if (_requests.TryGetValue(key, out list) &&
                        _requests.Remove(key) &&
                        list != null)
                    {
                        list.ForEach(_cache_queue.Add);
                    }
                }
            }
        }
    }
}

