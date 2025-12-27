using System;

namespace BackendUtils.Notifications.PushNotification
{
    public class PushNotificationSubscription
    {
        public int? UserID { get; set; }
        public long? CustomerID { get; set; }
        public string Endpoint { get; set; }

        public string P256DH { get; set; }

        public string Auth { get; set; }
        public DateTime CreationDate { get; internal set; }
    }
}
