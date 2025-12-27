using BackendUtils.EFDataService;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;

namespace BackendUtils.Notifications
{
    public class clsServerEvent
    {
        private static readonly ConcurrentDictionary<string, StreamWriter> _streamMessage = new ConcurrentDictionary<string, StreamWriter>();
        private static readonly ConcurrentDictionary<string, long> _userSystemRelation = new ConcurrentDictionary<string, long>();

        // تولید systemID
        public static string GetSystemID(long userID)
        {
            var id = Guid.NewGuid().ToString();
            _userSystemRelation.TryAdd(id, userID);
            return Cryptography.RC2Encryption(id, Cryptography.cipherKey);
        }

        // متد جدا برای نوشتن stream
        public static void WriteToStream(Stream outputStream, HttpContent content, TransportContext context, string systemId)
        {
            if (!_userSystemRelation.ContainsKey(systemId))
                return;

            if (_streamMessage.ContainsKey(systemId))
                _streamMessage.TryRemove(systemId, out _);

            StreamWriter writer = new StreamWriter(outputStream) { AutoFlush = true };
            var testEvent = new { Type = "Subsc", Text = "Subscription Was Successful" };
            writer.WriteLine($"data: {JsonConvert.SerializeObject(testEvent)}");
            writer.WriteLine();
            _streamMessage.TryAdd(systemId, writer);
        }
        public static HttpResponseMessage ValidateSystemID(HttpRequestMessage request)
        {
            HttpResponseMessage response = request.CreateResponse();
            var query = request.RequestUri.ParseQueryString();

            if (query.Count == 0)
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                response.Content = new StringContent("SystemID missing");
                return response;
            }

            string encryptedSystemId = query["SystemID"];
            if (string.IsNullOrEmpty(encryptedSystemId))
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                response.Content = new StringContent("SystemID invalid");
                return response;
            }
            string systemId;
            try
            {
                systemId = Cryptography.RC2Decryption(encryptedSystemId, Cryptography.cipherKey);
            }
            catch
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                response.Content = new StringContent("SystemID invalid");
                return response;
            }
            if (!_userSystemRelation.ContainsKey(systemId))
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                response.Content = new StringContent("SystemID not registered");
                return response;
            }
            response.StatusCode = HttpStatusCode.OK;
            return response;
        }
        public static HttpResponseMessage SubscribeToListener(HttpRequestMessage request)
        {
            HttpResponseMessage response = request.CreateResponse();
            var query = request.RequestUri.ParseQueryString();

            if (query.Count == 0)
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                response.Content = new StringContent("SystemID missing");
                return response;
            }

            string encryptedSystemId = query["SystemID"];
            if (string.IsNullOrEmpty(encryptedSystemId))
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                response.Content = new StringContent("SystemID invalid");
                return response;
            }

            string systemId;
            try
            {
                systemId = Cryptography.RC2Decryption(encryptedSystemId, Cryptography.cipherKey);
            }
            catch
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                response.Content = new StringContent("SystemID invalid");
                return response;
            }

            if (!_userSystemRelation.ContainsKey(systemId))
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                response.Content = new StringContent("SystemID not registered");
                return response;
            }

            response.Content = new PushStreamContent(
                (stream, content, context) =>
                {
                    content.Headers.Add("x-system-id", systemId);
                    WriteToStream(stream, content, context, systemId);
                },
                new MediaTypeHeaderValue("text/event-stream")
            );

            response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            return response;
        }



        public static void WriteDataToStream(int userID, List<string> dataList)
        {
            var relations = _userSystemRelation.Where(u => u.Value == userID);
            foreach (var relation in relations)
            {
                if (_streamMessage.TryGetValue(relation.Key, out StreamWriter stream))
                {
                    //foreach (string data in dataList)
                    //    stream.WriteLine(data);
                    //stream.Flush();
                    var sb = new StringBuilder();
                    foreach (var data in dataList)
                        sb.AppendLine(data);
                    sb.AppendLine();
                    stream.Write(sb.ToString());
                }
            }
        }

        public static void WriteDataToStream(long userID, int notificationID, string jsonString)
        {
            var relations = _userSystemRelation.Where(u => u.Value == userID);
            foreach (var relation in relations)
            {
                if (_streamMessage.TryGetValue(relation.Key, out StreamWriter stream))
                {
                    stream.WriteLine($"id: {notificationID}");
                    stream.WriteLine("data: " + jsonString + "\n");
                    stream.Flush();
                }
            }
        }


        public static void WriteDataToStream(List<long> userIDs, int notificationID, string jsonString)
        {
            var relations = _userSystemRelation.Where(u => userIDs.Contains(u.Value));
            foreach (var relation in relations)
            {
                //if (_streamMessage.TryGetValue(relation.Key, out StreamWriter stream))
                //{
                //    stream.WriteLine($"id: {notificationID}");
                //    stream.WriteLine("data: " + jsonString + "\n");
                //    stream.Flush();
                //}
                if (_streamMessage.TryGetValue(relation.Key, out StreamWriter stream))
                {
                    var sb = new StringBuilder();

                    // شناسه event
                    sb.AppendLine($"id: {notificationID}");

                    // داده event
                    sb.AppendLine("data: " + jsonString);

                    // پایان event (newline خالی)
                    sb.AppendLine();

                    // ارسال یکجا
                    stream.Write(sb.ToString());
                    // چون هنگام تعریف Streamwriter  AutoFlush = true شده است به خط زیر نیاز نیست
                    //stream.Flush();
                }
            }
        }

        public static void WriteDataToStream(IEnumerable<string> dataList)
        {
            foreach (var subscriber in _streamMessage)
            {
                try
                {
                    var sb = new StringBuilder();
                    foreach (var data in dataList)
                        //subscriber.Value.WriteLine($"data: {data}");
                        sb.AppendLine($"data: {data}");
                    sb.AppendLine();
                    subscriber.Value.Write(sb.ToString());
                    //subscriber.Value.WriteLine();
                    // چون هنگام تعریف Streamwriter  AutoFlush = true شده است به خط زیر نیاز نیست
                    //subscriber.Value.Flush();
                }
                catch
                {
                    _streamMessage.TryRemove(subscriber.Key, out _);
                }
            }
        }

        public static void WriteDataToStream(int notificationID, string jsonString)
        {
            foreach (var subscriber in _streamMessage)
            {
                try
                {
                    subscriber.Value.WriteLine($"id: {notificationID}");
                    subscriber.Value.WriteLine("data: " + jsonString + "\n");
                    subscriber.Value.Flush();
                }
                catch
                {
                    _streamMessage.TryRemove(subscriber.Key, out _);
                }
            }
        }
    }
    public static class GlobalNotificationCounter
    {
        private static int _count = 0;

        public static int Increment()
        {
            return Interlocked.Increment(ref _count);
        }

        public static int GetCount()
        {
            return _count;
        }

        public static void Reset()
        {
            Interlocked.Exchange(ref _count, 0);
        }
    }
}
