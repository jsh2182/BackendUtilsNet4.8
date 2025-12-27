namespace BackendUtils.Notifications.PushNotification
{
    public interface IPushNotificationService
    {
        void AddSubscription(PushNotificationSubscription subscription);
        string GetPublicKey();
        void SendToAll(string title, string body, string url = "/");
        void SendToMultipleUsers(int[] userIDs, string title, string body, string url = "/");
        void SendToUser(int userId, string title, string body, string url="/");
    }
}
