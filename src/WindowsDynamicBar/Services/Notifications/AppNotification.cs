namespace WindowsDynamicBar.Services.Notifications;

public sealed record AppNotification(string AppName, string Title, string Body, DateTimeOffset When);
