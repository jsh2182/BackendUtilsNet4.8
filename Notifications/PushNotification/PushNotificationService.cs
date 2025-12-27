using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WebPush;

namespace BackendUtils.Notifications.PushNotification
{

    public class PushNotificationService : IPushNotificationService
    {
        private readonly string _keysFilePath;
        private readonly VapidDetails _vapidKeys;
        private readonly List<PushNotificationSubscription> _subscriptions;
        private readonly IPushNotificationSubscriptionDataService _subscriptionDS;
        public PushNotificationService(string keysFilePath, IPushNotificationSubscriptionDataService subscriptionDS)
        {
            _keysFilePath = keysFilePath;
            _vapidKeys = LoadOrCreateVapidKeys();
            _subscriptionDS = subscriptionDS;
            _subscriptions = subscriptionDS.All();

        }
        public string GetPublicKey() => _vapidKeys.PublicKey;

        public void AddSubscription(PushNotificationSubscription subscription)
        {
            if ((!subscription.UserID.HasValue && !subscription.CustomerID.HasValue) || string.IsNullOrEmpty(subscription.Endpoint))
                return;
            PushSubscription pushSub = new PushSubscription()
            {
                Auth = subscription.Auth,
                Endpoint = subscription.Endpoint,
                P256DH = subscription.P256DH
            };
            lock (_subscriptions)
            {
                var existing = _subscriptions.FirstOrDefault(s => s.UserID == subscription.UserID);
                if (existing != null)
                {
                    _subscriptionDS.Delete(existing);
                    _subscriptions.Remove(existing);
                }
                var sub = new PushNotificationSubscription()
                {
                    Auth = subscription.Auth,
                    Endpoint = subscription.Endpoint,
                    P256DH = subscription.P256DH,
                    CreationDate = DateTime.Now,
                    CustomerID = subscription.CustomerID,
                    UserID = subscription.UserID,
                };
                _subscriptions.Add(sub);
                _subscriptionDS.Add(sub);
            }
        }
        public void SendToAll(string title, string body, string url = "/")
        {
            var client = new WebPushClient();
            var payload = JsonConvert.SerializeObject(new { title, body, url });
            List<PushSubscription> subsToSend;
            lock (_subscriptions)
            {
                subsToSend = _subscriptions
                    .Select(s => new PushSubscription
                    {
                        Endpoint = s.Endpoint,
                        Auth = s.Auth,
                        P256DH = s.P256DH
                    })
                    .ToList();
            }
            var vapid = new VapidDetails(
                _vapidKeys.Subject,
                _vapidKeys.PublicKey,
                _vapidKeys.PrivateKey
            )
            {
                Expiration = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
            };
            foreach (var ps in subsToSend)
            {
                try
                {
                    client.SendNotification(ps, payload, vapid);
                }
                catch (WebPushException ex)
                {

                    if (ex.StatusCode == System.Net.HttpStatusCode.Gone)
                    {
                        var sub = _subscriptions.FirstOrDefault(s => s.Endpoint == ps.Endpoint && s.Auth == ps.Auth && s.P256DH == ps.P256DH);
                        _subscriptionDS.Delete(sub);
                        _subscriptions.Remove(sub);
                    }
                }
            }

        }
        public void SendToMultipleUsers(int[] userIDs, string title, string body, string url = "/")
        {
            if (!userIDs.Any())
                return;

            var client = new WebPushClient();
            var payload = JsonConvert.SerializeObject(new { title, body });

            List<PushSubscription> subsToSend;
            lock (_subscriptions)
            {
                subsToSend = _subscriptions
                    .Where(s => userIDs.Contains(s.UserID ?? 0))
                    .Select(s => new PushSubscription
                    {
                        Endpoint = s.Endpoint,
                        Auth = s.Auth,
                        P256DH = s.P256DH
                    })
                    .ToList();
            }

            var vapid = new VapidDetails(
                _vapidKeys.Subject,
                _vapidKeys.PublicKey,
                _vapidKeys.PrivateKey
            )
            {
                Expiration = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
            };

            var options = new Dictionary<string, object>
            {
                { "vapidDetails", vapid },
                { "TTL", 24*3600 }
            };

            foreach (var ps in subsToSend)
            {
                try
                {

                    client.SendNotification(ps, payload, options);
                }
                catch (WebPushException ex)
                {

                    if (ex.StatusCode == System.Net.HttpStatusCode.Gone)
                    {
                        var sub = _subscriptions.FirstOrDefault(s => s.Endpoint == ps.Endpoint && s.Auth == ps.Auth && s.P256DH == ps.P256DH);
                        _subscriptionDS.Delete(sub);
                        _subscriptions.Remove(sub);
                    }
                }
            }
        }

        public void SendToUser(int userId, string title, string body, string url = "/")
        {
            var client = new WebPushClient();
            var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new { title, body, url });

            lock (_subscriptions)
            {
                var target = _subscriptions.FirstOrDefault(s => s.UserID == userId);
                if (target != null)
                {
                    PushSubscription ps = new PushSubscription()
                    {
                        Auth = target.Auth,
                        Endpoint = target.Endpoint,
                        P256DH = target.P256DH,
                    };
                    var vapid = new VapidDetails(
                        _vapidKeys.Subject,
                        _vapidKeys.PublicKey,
                        _vapidKeys.PrivateKey
                    )
                    {
                        Expiration = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
                    };
                    var options = new Dictionary<string, object>
                    {
                        { "vapidDetails", vapid },
                        { "TTL", 24*3600 }
                    };
                    try
                    {

                        client.SendNotification(ps, payload, options);
                    }
                    catch (WebPushException ex)
                    {

                        if (ex.StatusCode == System.Net.HttpStatusCode.Gone)
                        {
                            _subscriptionDS.Delete(target);
                            _subscriptions.Remove(target);
                        }
                        //throw ex;
                    }
                }
            }
        }
        private VapidDetails LoadOrCreateVapidKeys()
        {
            VapidKey keys;

            if (File.Exists(_keysFilePath))
            {
                keys = JsonConvert.DeserializeObject<VapidKey>(File.ReadAllText(_keysFilePath));
            }
            else
            {
                keys = GenerateVapidKeys();
                File.WriteAllText(_keysFilePath, JsonConvert.SerializeObject(keys, Formatting.Indented));
            }

            return new VapidDetails(
                "mailto:info@hoormah.com",
                keys.PublicKey,
                keys.PrivateKey
            )
            { Expiration = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds() };
        }

        private VapidKey GenerateVapidKeys()
        {
            var keys = WebPush.VapidHelper.GenerateVapidKeys();
            return new VapidKey
            {
                PublicKey = keys.PublicKey,
                PrivateKey = keys.PrivateKey
            };
        }

    }
}

