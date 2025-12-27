using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace BackendUtils.Notifications.PushNotification
{
    public interface IPushNotificationSubscriptionDataService
    {
        void Add(PushNotificationSubscription sub);
        List<PushNotificationSubscription> All();
        void Delete(PushNotificationSubscription existing);
    }
}
