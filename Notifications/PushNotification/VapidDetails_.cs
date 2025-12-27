namespace BackendUtils.Notifications.PushNotification
{
    public class VapidDetails_
    {
        public string Subject { get; set; }

        public string PublicKey { get; set; }

        public string PrivateKey { get; set; }

        public long Expiration { get; set; } = -1L;


        public VapidDetails_()
        {
        }

        public VapidDetails_(string subject, string publicKey, string privateKey)
        {
            Subject = subject;
            PublicKey = publicKey;
            PrivateKey = privateKey;
        }
    }
}
