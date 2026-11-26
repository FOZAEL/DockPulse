using System;
using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace DockPulse.Services;

public static class WindowsNotificationHelper
{
    public static void ShowToast(string title, string body, int expirationMinutes = 30)
    {
        try
        {
            string escapedTitle = SecurityElement.Escape(title) ?? title;
            string escapedBody = SecurityElement.Escape(body) ?? body;

            string xml =
                "<toast>" +
                "<visual><binding template=\"ToastGeneric\">" +
                $"<text>{escapedTitle}</text>" +
                $"<text>{escapedBody}</text>" +
                "</binding></visual>" +
                "<audio src=\"ms-winsoundevent:Notification.Default\"/>" +
                "</toast>";

            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var toast = new ToastNotification(doc)
            {
                ExpirationTime = DateTimeOffset.Now.AddMinutes(Math.Max(1, expirationMinutes)),
            };

            ToastNotificationManager.CreateToastNotifier().Show(toast);
        }
        catch
        {
            // Ignore notification errors if Windows Notifications are disabled at OS level
        }
    }
}
